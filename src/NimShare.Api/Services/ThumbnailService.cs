using System.Threading.Channels;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.191 — Gallery-Perf-Redesign. Die alte Architektur (Task.Run-Flut
/// mit Semaphor + Timeout) hat auf dem 1-vCPU-B1 systematisch Warmups
/// sterben lassen: 65 Fotos × 2 Größen = 130 Tasks pro Landing-Aufruf,
/// 2 Slots, 60s-Timeout → nur die ersten paar überlebten, der Rest loggte
/// und starb. Jeder Reload begann von vorn. Ergebnis: „1 Thumb pro 5 min".
///
/// Neu:
///  - EINE persistente Channel-Queue + BackgroundService-Worker.
///    Jobs warten beliebig lange, nichts stirbt an Timeouts.
///  - Ein Job = ein FILE (nicht eine Größe): Blob 1× laden, 1× decodieren,
///    daraus 1600er UND 400er schreiben. Halbiert Download + Decode.
///  - GPS-EXIF wird im selben Durchgang aus dem bereits geladenen Stream
///    extrahiert (vorher: separater Blob-Download pro Foto).
///  - DB-Flag StorageFile.ThumbsReadyAt nach Erfolg. Die Landing rendert
///    für geflaggte Files direkte SAS-URLs — null MVC-Requests pro Kachel.
///  - Worker-Concurrency = 1: HEIC-Decode ist CPU-bound; auf 1 vCPU bringen
///    2 parallele Decodes keinen Durchsatz, verdoppeln aber RAM-Peak und
///    verhungern Kestrel. Ein Loop lässt die Landing flüssig.
///
/// Cache-Layout unverändert: `thumbs/{fileId:N}/{size}.jpg` — bereits
/// generierte Alt-Thumbs werden beim Backfill nur „gestempelt", nicht
/// neu gebaut.
/// </summary>
public interface IThumbnailService
{
    bool IsImage(string? contentType);
    bool IsAllowedSize(int size);

    /// <summary>SAS-URL auf ein Cache-Thumb OHNE Existenz-Check — nur für
    /// Files verwenden, deren ThumbsReadyAt gesetzt ist (DB ist die Wahrheit).</summary>
    Uri CreateThumbSas(Guid fileId, int size, TimeSpan? ttl = null);

    /// <summary>Cache-Hit → SAS-Redirect-URL. Miss → Job einreihen + null
    /// (Aufrufer gibt 404, Client pollt /thumb-status).</summary>
    Task<Uri?> GetOrCreateAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default);

    /// <summary>File in die Thumb-Queue einreihen (dedup, non-blocking).
    /// No-op für Nicht-Bilder und für als kaputt markierte Files.</summary>
    void Enqueue(Guid fileId, string blobPath, string? contentType);

    /// <summary>True wenn der Decode für dieses File in dieser Prozess-
    /// Lebenszeit endgültig gescheitert ist (kaputte Datei, unbekanntes
    /// Format). Landing rendert dann den Kamera-Fallback statt ewig zu
    /// pollen; nach einem Deploy/Restart gibt's automatisch einen Retry.</summary>
    bool IsFailed(Guid fileId);

    /// <summary>v1.10.196: GPS-Nachzieh für Files, deren Thumbs schon fertig
    /// sind (ThumbsReadyAt gesetzt), die aber keine Koordinaten haben — z.B.
    /// weil sie hochgeladen wurden, als der EXIF-Pfad noch lückenhaft war.
    /// Max. 1 Versuch pro File und Prozess-Lebenszeit (Fotos ohne GPS-EXIF
    /// würden sonst bei jedem Landing-Aufruf neu heruntergeladen).</summary>
    void EnqueueGpsBackfill(Guid fileId, string blobPath, string? contentType);
}

public class ThumbnailService : IThumbnailService
{
    private readonly IBlobStorageService _blobs;
    private readonly ILogger<ThumbnailService> _log;
    internal const string CachePrefix = "thumbs/";
    internal static readonly int[] AllowedSizes = { 400, 1600 };

    // Queue: unbounded — Jobs sind winzig (3 Felder), und die Dedup-Map
    // verhindert, dass dieselbe Datei mehrfach ansteht.
    private readonly Channel<ThumbJob> _queue = Channel.CreateUnbounded<ThumbJob>(
        new UnboundedChannelOptions { SingleReader = true });
    // Dedup: FileId ist drin solange der Job ansteht ODER läuft. Der Worker
    // entfernt nach Abschluss. Erneutes Enqueue derselben Datei ist damit
    // ein No-op — egal wie oft die Landing neu geladen wird.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _pending = new();
    // Decode-Failures (kaputte/unlesbare Dateien). In-memory only — nach
    // einem Container-Restart wird automatisch neu probiert. Verhindert die
    // Endlos-Schleife „Landing enqueued → Worker lädt Blob → Decode failt →
    // nächster Landing-Aufruf enqueued wieder".
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _failed = new();

    public ThumbnailService(IBlobStorageService blobs, ILogger<ThumbnailService> log)
    {
        _blobs = blobs;
        _log = log;
    }

    public bool IsImage(string? contentType)
        => (contentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public bool IsAllowedSize(int size) => AllowedSizes.Contains(size);

    internal static string CachePath(Guid fileId, int size) => $"{CachePrefix}{fileId:N}/{size}.jpg";

    public Uri CreateThumbSas(Guid fileId, int size, TimeSpan? ttl = null)
        => _blobs.CreateInlineSas(CachePath(fileId, size), "image/jpeg", ttl ?? TimeSpan.FromMinutes(60));

    public async Task<Uri?> GetOrCreateAsync(Guid fileId, string sourceBlobPath, string sourceContentType,
        int size, CancellationToken ct = default)
    {
        if (!IsImage(sourceContentType) || !IsAllowedSize(size) || string.IsNullOrEmpty(sourceBlobPath))
            return null;
        if (await _blobs.ExistsAsync(CachePath(fileId, size), ct))
            return _blobs.CreateInlineSas(CachePath(fileId, size), "image/jpeg", TimeSpan.FromMinutes(10));
        Enqueue(fileId, sourceBlobPath, sourceContentType);
        return null;
    }

    public void Enqueue(Guid fileId, string blobPath, string? contentType)
    {
        if (!IsImage(contentType) || string.IsNullOrEmpty(blobPath)) return;
        if (_failed.ContainsKey(fileId)) return;   // endgültig kaputt — kein Retry-Loop
        if (!_pending.TryAdd(fileId, 0)) return;   // schon in Queue/Arbeit
        if (!_queue.Writer.TryWrite(new ThumbJob(fileId, blobPath, contentType!)))
            _pending.TryRemove(fileId, out _);      // unbounded → passiert praktisch nie
    }

    public bool IsFailed(Guid fileId) => _failed.ContainsKey(fileId);

    // v1.10.196: einmal-pro-Prozess-Gate für den GPS-Nachzieh.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _gpsChecked = new();

    public void EnqueueGpsBackfill(Guid fileId, string blobPath, string? contentType)
    {
        if (!IsImage(contentType) || string.IsNullOrEmpty(blobPath)) return;
        if (!_gpsChecked.TryAdd(fileId, 0)) return;   // schon versucht
        // Normale Queue nutzen — der Worker landet im STAMP-ONLY-Pfad
        // (Thumbs existieren) und macht dort nur den GPS-Download.
        Enqueue(fileId, blobPath, contentType);
    }

    // ── Worker-Seite (nur ThumbnailWorker ruft die hier auf) ─────────────
    internal ChannelReader<ThumbJob> Reader => _queue.Reader;
    internal void MarkDone(Guid fileId) => _pending.TryRemove(fileId, out _);
    internal void MarkFailed(Guid fileId) => _failed.TryAdd(fileId, 0);
    internal int PendingCount => _pending.Count;
}

public record ThumbJob(Guid FileId, string BlobPath, string ContentType);

/// <summary>
/// Der eigentliche Thumb-Bauer. Läuft als BackgroundService mit genau
/// einem Consumer-Loop (CPU-bound auf 1 vCPU — siehe ThumbnailService-Doc).
/// Beim Start: Backfill-Scan über Ready-Bilder ohne ThumbsReadyAt, die in
/// einem Gallery-Ordner liegen oder von einem Folder-Link erreichbar sind —
/// damit überleben angefangene Alben auch einen Container-Restart.
/// </summary>
public class ThumbnailWorker : BackgroundService
{
    private readonly ThumbnailService _svc;
    private readonly IBlobStorageService _blobs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ThumbnailWorker> _log;

    public ThumbnailWorker(ThumbnailService svc, IBlobStorageService blobs,
        IServiceScopeFactory scopes, ILogger<ThumbnailWorker> log)
    {
        _svc = svc;
        _blobs = blobs;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Startup kurz abwarten — Migrations laufen vor dem Host-Start,
        // aber wir wollen dem Kestrel-Warmup nicht in die Quere kommen.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
        await BackfillAsync(ct);

        await foreach (var job in _svc.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "ThumbWorker: job for {File} failed", job.BlobPath);
            }
            finally
            {
                _svc.MarkDone(job.FileId);
            }
        }
    }

    /// <summary>Nach Restart: sichtbare Bilder ohne Thumb-Flag wieder einreihen.
    /// „Sichtbar" = im Gallery-Ordner oder von einem Folder-ShareLink erfasst.
    /// Cap 500 — reicht für jedes reale Album, verhindert Massen-Backfill
    /// über die gesamte Alt-Ablage.</summary>
    private async Task BackfillAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
            var candidates = await db.Files
                .Where(f => f.Status == StorageFileStatus.Ready
                    && f.ThumbsReadyAt == null
                    && f.ContentType != null && f.ContentType.StartsWith("image/")
                    && f.FolderId != null
                    && (db.Folders.Any(fo => fo.Id == f.FolderId && fo.Kind == FolderKind.Gallery)
                        || db.ShareLinks.Any(l => l.FolderId == f.FolderId)))
                .OrderByDescending(f => f.ReadyAt)
                .Take(500)
                .Select(f => new { f.Id, f.BlobPath, f.ContentType })
                .ToListAsync(ct);
            foreach (var c in candidates)
                _svc.Enqueue(c.Id, c.BlobPath, c.ContentType);
            if (candidates.Count > 0)
                _log.LogInformation("ThumbWorker: backfill queued {N} files", candidates.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ThumbWorker: backfill scan failed (weiter ohne)");
        }
    }

    private async Task ProcessAsync(ThumbJob job, CancellationToken ct)
    {
        var swAll = System.Diagnostics.Stopwatch.StartNew();
        var path1600 = ThumbnailService.CachePath(job.FileId, 1600);
        var path400 = ThumbnailService.CachePath(job.FileId, 400);

        // Alt-Cache aus der Prä-Redesign-Ära: beide Größen existieren schon →
        // nur DB-Flag stempeln. Fehlt dem File noch GPS, machen wir trotzdem
        // den Download für die EXIF-Extraktion (kein Decode nötig) — sonst
        // blieben Prä-v1.10.178-Fotos für immer ohne Karten-Pin.
        var has1600 = await _blobs.ExistsAsync(path1600, ct);
        var has400 = has1600 && await _blobs.ExistsAsync(path400, ct);
        if (has1600 && has400)
        {
            (double, double)? oldGps = null;
            if (await NeedsGpsAsync(job.FileId, ct))
            {
                using var gpsMs = new MemoryStream();
                try
                {
                    await _blobs.DownloadToAsync(job.BlobPath, gpsMs, ct);
                    oldGps = TryReadGps(gpsMs);
                }
                catch { /* best effort */ }
            }
            await StampAsync(job.FileId, oldGps, ct);
            _log.LogInformation("Thumb STAMP-ONLY {File} (cache existed)", job.BlobPath);
            return;
        }

        using var srcMs = new MemoryStream();
        var swDl = System.Diagnostics.Stopwatch.StartNew();
        await _blobs.DownloadToAsync(job.BlobPath, srcMs, ct);
        swDl.Stop();

        // GPS aus dem bereits geladenen Stream — kein zweiter Download mehr.
        var gps = TryReadGps(srcMs);

        srcMs.Position = 0;
        byte[] out1600, out400;
        var swDec = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var img = new MagickImage(srcMs);
            var fmt = img.Format; var w = img.Width; var h = img.Height;
            img.AutoOrient();
            // Thumbnail() = Resize + Strip in einem, mit schnellerem Sampling-
            // Pfad als der Default-Lanczos von Resize(). Erst 1600 aus dem
            // Full-Decode, dann 400 aus der 1600er — zweite Skalierung ist
            // auf dem kleinen Bild fast gratis.
            img.Thumbnail(new MagickGeometry(1600, 1600) { Greater = true });
            img.Quality = 82;
            img.Format = MagickFormat.Jpeg;
            using (var ms1600 = new MemoryStream())
            {
                img.Write(ms1600, MagickFormat.Jpeg);
                out1600 = ms1600.ToArray();
            }
            img.Thumbnail(new MagickGeometry(400, 400) { Greater = true });
            using (var ms400 = new MemoryStream())
            {
                img.Write(ms400, MagickFormat.Jpeg);
                out400 = ms400.ToArray();
            }
            swDec.Stop();
            _log.LogInformation("Thumb BUILD {File} fmt={Fmt} {W}x{H} src={Src}B dl={DlMs}ms cpu={CpuMs}ms",
                job.BlobPath, fmt, w, h, srcMs.Length, swDl.ElapsedMilliseconds, swDec.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Thumb DECODE FAILED {File} (ct={Ct} {Bytes}B) — Format kaputt oder libheif fehlt",
                job.BlobPath, job.ContentType, srcMs.Length);
            // Als endgültig-kaputt markieren: Landing rendert ab jetzt den
            // Kamera-Fallback statt Pending-Spinner, und Enqueue ist ein
            // No-op — sonst würde jeder Landing-Aufruf den Blob erneut
            // herunterladen und wieder am Decode scheitern. GPS stempeln
            // wir trotzdem, falls die EXIF-Extraktion was gefunden hat.
            _svc.MarkFailed(job.FileId);
            if (gps is not null) await StampAsync(job.FileId, gps, ct, flagThumbs: false);
            return;
        }

        using (var up = new MemoryStream(out1600))
            await _blobs.UploadFromStreamAsync(path1600, up, "image/jpeg", ct);
        using (var up = new MemoryStream(out400))
            await _blobs.UploadFromStreamAsync(path400, up, "image/jpeg", ct);

        await StampAsync(job.FileId, gps, ct);
        swAll.Stop();
        _log.LogInformation("Thumb OK {File} 1600={A}B 400={B}B total={Ms}ms queue={Q}",
            job.BlobPath, out1600.Length, out400.Length, swAll.ElapsedMilliseconds, _svc.PendingCount);
    }

    /// <summary>EXIF-GPS aus einem bereits geladenen Bild-Stream. Wirft nie.</summary>
    private static (double Lat, double Lon)? TryReadGps(MemoryStream ms)
    {
        try
        {
            ms.Position = 0;
            var dirs = MetadataExtractor.ImageMetadataReader.ReadMetadata(ms);
            var g = dirs.OfType<MetadataExtractor.Formats.Exif.GpsDirectory>().FirstOrDefault();
            var loc = g?.GetGeoLocation();
            if (loc is not null && !loc.IsZero
                && !double.IsNaN(loc.Latitude) && !double.IsNaN(loc.Longitude)
                && loc.Latitude is >= -90 and <= 90 && loc.Longitude is >= -180 and <= 180)
                return (loc.Latitude, loc.Longitude);
        }
        catch { /* kein GPS ist kein Fehler */ }
        return null;
    }

    private async Task<bool> NeedsGpsAsync(Guid fileId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        return await db.Files.AnyAsync(f => f.Id == fileId && f.Latitude == null, ct);
    }

    private async Task StampAsync(Guid fileId, (double Lat, double Lon)? gps,
        CancellationToken ct, bool flagThumbs = true)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return;
        if (flagThumbs) file.ThumbsReadyAt = DateTimeOffset.UtcNow;
        if (gps is not null && file.Latitude is null)
        {
            file.Latitude = gps.Value.Lat;
            file.Longitude = gps.Value.Lon;
        }
        await db.SaveChangesAsync(ct);
    }
}
