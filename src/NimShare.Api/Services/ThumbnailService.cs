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
    // v1.10.188: Semaphor auf 2 reduziert. Marcus's App Service ist B1
    // (1 vCPU, 1.75 GB RAM) — bei 4 parallelen HEIC-Decodes × ~150 MB Peak
    // + .NET Runtime + SQLite + Kestrel schneidet der Container zu knapp
    // an OOM. 2 Slots decken auch bei 100-Foto-Alben durch: bei ~3s pro
    // Konversion × 100/2 = 150 s = 2.5 Min bis alles fertig.
    private static readonly SemaphoreSlim _slot = new(2, 2);
    // v1.10.180: Warmup-Deduplication. Client feuert bei 44-Foto-Landing
    // 44 parallele /thumb-Requests → 44 identische Warmups würden entstehen.
    // Der ConcurrentDictionary hält den aktuell laufenden Task pro (fileId,
    // size)-Key und gibt ihn allen Aufrufern zurück. Fertige Warmups fliegen
    // per ContinueWith(_ => _inflight.TryRemove...) sofort raus.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _inflight
        = new();

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

    // v1.10.179: Kill-Switch wieder aus. Die tatsächliche Ursache für v1.10.177
    // war NICHT Magick.NET Native-Lib, sondern SNAT-Port-Exhaustion durch das
    // als Scoped registrierte BlobStorageService — das ist jetzt Singleton
    // (Program.cs). Zusätzlich neue Semantik: Cache-Miss → sofort null, kein
    // inline-Convert mehr. GalleryThumb liefert dann 404 → Client-Fallback
    // (v1.10.170 Kamera-Placeholder). Warmup läuft im Hintergrund; beim
    // nächsten Reload greift der Cache-Hit → 302-Redirect ohne CPU.

    public async Task<Uri?> GetOrCreateAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default)
    {
        if (!IsImage(sourceContentType)) return null;
        if (!IsAllowedSize(size)) return null;
        if (string.IsNullOrEmpty(sourceBlobPath)) return null;

        var cachePath = $"{CachePrefix}{fileId:N}/{size}.jpg";
        if (await _blobs.ExistsAsync(cachePath, ct))
            return _blobs.CreateInlineSas(cachePath, "image/jpeg", TimeSpan.FromMinutes(10));

        // v1.10.179: KEIN inline-Convert mehr im GET-Pfad. Der HTTP-Handler
        // hat den falschen Ort dafür — 44 Cache-Misses × ~500 ms HEIC-Decode
        // durch einen 4er-Slot serialisieren die Landing minutenlang. Statt-
        // dessen: 404 zurückgeben → Client zeigt sofort den v1.10.170-
        // Fallback → parallel Warmup im Hintergrund → nächster Reload ist
        // Cache-Hit + instant. Aufrufer (GalleryThumb) darf hier direkt
        // WarmupAsync fire-and-forgetten.
        return null;
    }

    /// <summary>
    /// v1.10.179: Erzeugt den Thumb blockierend (nur im Warmup-Pfad genutzt).
    /// Nicht direkt vom Request-Handler aufrufen — den Client interessiert
    /// nach 404 nicht mehr, was hier passiert.
    /// </summary>
    private async Task GenerateAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct)
    {
        if (!IsImage(sourceContentType)) return;
        if (!IsAllowedSize(size)) return;
        if (string.IsNullOrEmpty(sourceBlobPath)) return;
        var cachePath = $"{CachePrefix}{fileId:N}/{size}.jpg";
        if (await _blobs.ExistsAsync(cachePath, ct)) return;

        // v1.10.179: Semaphore-Leak-Fix — nur releasen wenn wir den Slot
        // tatsächlich bekommen haben. Bei Cancellation wirft WaitAsync ohne
        // Acquire, der frühere finally-Block hat dann über-relaesed.
        bool acquired = false;
        try
        {
            // v1.10.189: Slot-Timeout drastisch auf 60s runter. Wenn das
            // System gesund ist reicht das dicke (Download+Decode+Upload
            // ~2-5s pro Foto × 2 Slots). Wenn nicht, sehen wir im Log
            // sofort welcher Task hängt, statt 10min Diagnose-Blackbox.
            if (!await _slot.WaitAsync(TimeSpan.FromSeconds(60), ct))
            {
                _log.LogError("Thumb slot timeout (60s) for {File} size={Size} — vorheriger Slot hängt oder decode ist ekelhaft langsam",
                    sourceBlobPath, size);
                return;
            }
            acquired = true;
            if (await _blobs.ExistsAsync(cachePath, ct)) return;

            var swAll = System.Diagnostics.Stopwatch.StartNew();

            using var srcMs = new MemoryStream();
            var swDl = System.Diagnostics.Stopwatch.StartNew();
            await _blobs.DownloadToAsync(sourceBlobPath, srcMs, ct);
            swDl.Stop();
            srcMs.Position = 0;
            _log.LogInformation("Thumb DL {File} ct={Ct} size={Bytes}B in {Ms}ms",
                sourceBlobPath, sourceContentType, srcMs.Length, swDl.ElapsedMilliseconds);

            using var outMs = new MemoryStream();
            try
            {
                var swDec = System.Diagnostics.Stopwatch.StartNew();
                using var img = new MagickImage(srcMs);
                swDec.Stop();
                _log.LogInformation("Thumb DECODE {File} fmt={Fmt} src={W}x{H} in {Ms}ms",
                    sourceBlobPath, img.Format, img.Width, img.Height, swDec.ElapsedMilliseconds);

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
                // v1.10.189: LogError statt Warning. Warmup ist fire-and-forget
                // → Warnings landen nur in Debug-Levels und Marcus sieht sie
                // im Azure Log Stream nicht. HEIC-Decode-Failures sind KEIN
                // ok-passiert-Zustand, sondern die Ursache für "1 Thumb / 5 min".
                _log.LogError(ex, "Thumbnail conversion FAILED for {File} (ct={Ct} size={Bytes}B) — Magick.NET libheif fehlt?",
                    sourceBlobPath, sourceContentType, srcMs.Length);
                return;
            }

            outMs.Position = 0;
            var swUp = System.Diagnostics.Stopwatch.StartNew();
            await _blobs.UploadFromStreamAsync(cachePath, outMs, "image/jpeg", ct);
            swUp.Stop();
            swAll.Stop();
            _log.LogInformation("Thumb OK {File} out={Bytes}B UL={UpMs}ms total={AllMs}ms",
                sourceBlobPath, outMs.Length, swUp.ElapsedMilliseconds, swAll.ElapsedMilliseconds);
        }
        finally
        {
            if (acquired) _slot.Release();
        }
    }

    public Task WarmupAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default)
    {
        // v1.10.180: Deduplizieren. Bei 44 parallelen /thumb-Requests würden
        // sonst 44 identische Generate-Läufe starten und alle über den 4-Slot-
        // Semaphor stauen. Der Client könnte durch schnelles Reloaden das
        // Vielfache produzieren. GetOrAdd hält den bereits-laufenden Task und
        // gibt ihn wiederholten Aufrufern zurück.
        var key = $"{fileId:N}:{size}";
        return _inflight.GetOrAdd(key, _ =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    await GenerateAsync(fileId, sourceBlobPath, sourceContentType, size, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Thumbnail warmup failed for {File}", sourceBlobPath);
                }
                finally
                {
                    _inflight.TryRemove(key, out Task? _);
                }
            });
            return task;
        });
    }
}
