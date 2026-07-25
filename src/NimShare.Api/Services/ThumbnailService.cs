using ImageMagick;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.174: Server-seitige Thumbnail-Erzeugung für Album-Landings.
/// Zweck: HEIC (Chrome/Firefox können's nicht nativ) und übergroße JPEGs
/// werden zu handlichen JPEG-Kacheln umgerechnet und ins Blob-Storage
/// gecacht. Bei zweitem Request → SAS-Redirect direkt aus dem Cache,
/// die App-Instanz sieht das Byte nicht mehr.
///
/// Design:
///  - Cache-Pfad: `thumbs/{fileId:N}/{size}.jpg`
///  - Cache-Key = fileId + size (kein Content-Hash — Files sind immutable)
///  - Konvertierung nur für image/* (Videos → null, Landing-Fallback greift)
///  - Concurrency-Bremse: 4 parallele Konvertierungen; ein HEIC-Decode
///    kann bei 12-MP-iPhone-Foto 400-800 ms brauchen und ein paar hundert
///    MB RAM belegen — mehr würde einen 1-GB-App-Service ins OOM treiben.
///  - Erlaubte Größen fest verdrahtet (400, 1600) — sonst könnte jemand
///    per `?size=99999` DoS-en.
/// </summary>
public interface IThumbnailService
{
    /// <summary>Ist der Content-Type ein Bild, für das wir Thumbs erzeugen?</summary>
    bool IsImage(string? contentType);

    /// <summary>Gültige Ausgabegrößen (long-edge in Pixel).</summary>
    bool IsAllowedSize(int size);

    /// <summary>
    /// Liefert eine SAS-URL zum gecachten Thumbnail. Existiert der Cache
    /// noch nicht, wird das Original heruntergeladen, konvertiert, upge-
    /// loadet und dann die SAS erzeugt. Null bedeutet: nicht previewbar
    /// (z.B. Video, kaputte Datei).
    /// </summary>
    Task<Uri?> GetOrCreateAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default);

    /// <summary>
    /// Fire-and-forget Warm-up nach dem Upload-Complete. Loggt Fehler,
    /// wirft nichts (der Client wartet nicht drauf).
    /// </summary>
    Task WarmupAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default);
}

public class ThumbnailService : IThumbnailService
{
    private readonly IBlobStorageService _blobs;
    private readonly ILogger<ThumbnailService> _log;
    private const string CachePrefix = "thumbs/";
    // 4 gleichzeitige HEIC-Decodes sind auf einem 2-GB-Container okay —
    // ein 12-MP-Foto braucht ca. 150 MB Peak, in Summe also ~600 MB.
    private static readonly SemaphoreSlim _slot = new(4, 4);

    // Whitelist der Ausgabegrößen. 400 = Grid-Kachel, 1600 = Lightbox-Retina.
    private static readonly int[] AllowedSizes = { 400, 1600 };

    public ThumbnailService(IBlobStorageService blobs, ILogger<ThumbnailService> log)
    {
        _blobs = blobs;
        _log = log;
    }

    public bool IsImage(string? contentType)
    {
        var ct = (contentType ?? "").ToLowerInvariant();
        return ct.StartsWith("image/");
    }

    public bool IsAllowedSize(int size) => AllowedSizes.Contains(size);

    // v1.10.178 Kill-Switch. Magick.NET-Native-Lib hängt on-load im Linux-
    // Container (App Service, publish/AnyCPU + fehlendes Runtime-Pack). Statt
    // wie in v1.10.177 den Thread-Pool zuzumachen, geben wir hier sofort null
    // zurück → Endpoint 404 → Client-Fallback (v1.10.170 Kamera-Placeholder)
    // greift ohne Verzögerung. Feature bleibt bis der Native-Lib-Fix da ist.
    private static readonly bool _disabled = true;

    public async Task<Uri?> GetOrCreateAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default)
    {
        if (_disabled) return null;
        if (!IsImage(sourceContentType)) return null;
        if (!IsAllowedSize(size)) return null;
        if (string.IsNullOrEmpty(sourceBlobPath)) return null;

        var cachePath = $"{CachePrefix}{fileId:N}/{size}.jpg";
        if (await _blobs.ExistsAsync(cachePath, ct))
            return _blobs.CreateInlineSas(cachePath, "image/jpeg", TimeSpan.FromMinutes(10));

        // Slot mit knappem Timeout — kein zweiter Endpunkt darf hängen wenn
        // eine Konvertierung stecken bleibt (Native-Lib-Hang o.ä.). 30s ist
        // grosszügig für iPhone-HEIC-Decodes, kurz genug um Thread-Pool-
        // Suffocation zu vermeiden.
        if (!await _slot.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            _log.LogWarning("Thumb slot timeout for {File}", sourceBlobPath);
            return null;
        }
        try
        {
            // Doppelt geprüft: zwischen erstem Check und Slot-Acquire könnte
            // ein anderer Request schon konvertiert haben.
            if (await _blobs.ExistsAsync(cachePath, ct))
                return _blobs.CreateInlineSas(cachePath, "image/jpeg", TimeSpan.FromMinutes(10));

            using var srcMs = new MemoryStream();
            await _blobs.DownloadToAsync(sourceBlobPath, srcMs, ct);
            srcMs.Position = 0;

            using var outMs = new MemoryStream();
            try
            {
                using var img = new MagickImage(srcMs);
                img.AutoOrient();
                img.Strip();
                img.Resize(new MagickGeometry(size, size)
                {
                    Greater = true,
                });
                img.Quality = 82;
                img.Format = MagickFormat.Jpeg;
                img.Write(outMs, MagickFormat.Jpeg);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Thumbnail conversion failed for {File} (ct={Ct})",
                    sourceBlobPath, sourceContentType);
                return null;
            }

            outMs.Position = 0;
            await _blobs.UploadFromStreamAsync(cachePath, outMs, "image/jpeg", ct);
            return _blobs.CreateInlineSas(cachePath, "image/jpeg", TimeSpan.FromMinutes(10));
        }
        finally
        {
            _slot.Release();
        }
    }

    public async Task WarmupAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default)
    {
        try
        {
            await GetOrCreateAsync(fileId, sourceBlobPath, sourceContentType, size, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Thumbnail warmup failed for {File}", sourceBlobPath);
        }
    }
}
