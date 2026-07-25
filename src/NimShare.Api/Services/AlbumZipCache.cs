using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.183 — vorgefertigtes Album-ZIP pro ShareLink. Wird beim
/// Link-Erstellen fire-and-forget aufgebaut und liegt danach im Blob
/// unter `zips/{linkId:N}/album.zip`. Beim Landing-Klick auf
/// „Alle herunterladen" liefert der Endpoint sofort einen SAS-Redirect
/// statt on-the-fly zu packen — 44-Foto-Album wird zum Instant-Download.
///
/// Wird beim Link-Delete via <see cref="DeleteAsync"/> mit weggeräumt.
/// Dedup via <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// verhindert doppelte parallele Builds für denselben Link.
/// </summary>
public interface IAlbumZipCache
{
    /// <summary>Cache-Pfad im Blob-Container.</summary>
    string CachePath(Guid linkId);

    /// <summary>True wenn ein vorgefertigtes ZIP existiert.</summary>
    Task<bool> ExistsAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Direkte SAS-URL zum ZIP (10min gültig). Null wenn nicht da.</summary>
    Task<Uri?> GetSasAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Fire-and-forget-Warmup. Baut das ZIP im Hintergrund,
    /// deduped über den gleichen Link.</summary>
    Task WarmupAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Löscht das ZIP aus dem Cache.</summary>
    Task DeleteAsync(Guid linkId, CancellationToken ct = default);
}

public class AlbumZipCache : IAlbumZipCache
{
    private readonly IBlobStorageService _blobs;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AlbumZipCache> _log;
    private const string CachePrefix = "zips/";
    // Dedup — kein zweiter Warmup für denselben Link, während der erste läuft.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _inflight
        = new();

    public AlbumZipCache(IBlobStorageService blobs, IServiceScopeFactory scopes,
        ILogger<AlbumZipCache> log)
    {
        _blobs = blobs;
        _scopes = scopes;
        _log = log;
    }

    public string CachePath(Guid linkId) => $"{CachePrefix}{linkId:N}/album.zip";

    public Task<bool> ExistsAsync(Guid linkId, CancellationToken ct = default)
        => _blobs.ExistsAsync(CachePath(linkId), ct);

    public async Task<Uri?> GetSasAsync(Guid linkId, CancellationToken ct = default)
    {
        var path = CachePath(linkId);
        if (!await _blobs.ExistsAsync(path, ct)) return null;
        return _blobs.CreateInlineSas(path, "application/zip", TimeSpan.FromMinutes(10));
    }

    public Task WarmupAsync(Guid linkId, CancellationToken ct = default)
    {
        return _inflight.GetOrAdd(linkId, id =>
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    await BuildAsync(id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "AlbumZipCache warmup failed for link {LinkId}", id);
                }
                finally
                {
                    _inflight.TryRemove(id, out Task? _);
                }
            });
            return task;
        });
    }

    public async Task DeleteAsync(Guid linkId, CancellationToken ct = default)
    {
        try { await _blobs.DeleteAsync(CachePath(linkId), ct); }
        catch (Exception ex) { _log.LogWarning(ex, "AlbumZipCache delete failed for link {LinkId}", linkId); }
    }

    private async Task BuildAsync(Guid linkId, CancellationToken ct)
    {
        var cachePath = CachePath(linkId);
        if (await _blobs.ExistsAsync(cachePath, ct)) return;   // Race-Case: schon fertig

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var link = await db.ShareLinks
            .Include(l => l.Folder)
            .SingleOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null || link.FolderId is null) return;

        var files = await db.Files
            .Where(f => f.FolderId == link.FolderId && f.Status == StorageFileStatus.Ready)
            .Select(f => new { f.Id, f.BlobPath, f.Name })
            .ToListAsync(ct);
        if (files.Count == 0) return;
        // v1.10.191: Snapshot der File-Ids — vor dem Upload prüfen wir, ob
        // sich der Ordner-Inhalt während des Builds geändert hat (Gast lädt
        // parallel hoch → Invalidate-Delete könnte VOR unserem Upload laufen
        // und wir würden ein stales ZIP als frisch zementieren).
        var snapshotIds = files.Select(f => f.Id).OrderBy(x => x).ToList();

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nimshare-zipcache-{linkId:N}.zip");
        int written = 0;
        try
        {
            await using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Create,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.None, 1 << 16, System.IO.FileOptions.Asynchronous))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = UniqueEntryName(f.Name, used);
                    // v1.10.191: Medien (JPEG/HEIC/MP4…) sind bereits komprimiert —
                    // Optimal-Deflate darüber verbrennt auf dem 1-vCPU-B1 nur CPU
                    // (~50s+ pro Album!), die währenddessen dem Thumb-Worker und
                    // Kestrel fehlt. NoCompression = reines Store, ~100× schneller,
                    // ZIP wird nur minimal größer.
                    var entry = zip.CreateEntry(name, PickCompression(f.Name));
                    try
                    {
                        await using var es = entry.Open();
                        await _blobs.DownloadToAsync(f.BlobPath, es, ct);
                        written++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "AlbumZipCache: skip blob {Blob} for link {LinkId}", f.BlobPath, linkId);
                    }
                }
            }
            if (written == 0) return;

            // v1.10.191: Staleness-Check vor dem Upload — hat sich der Ordner
            // während des Builds geändert (neuer Gast-Upload, Löschung), das
            // ZIP verwerfen. Der nächste Warmup baut mit frischem Stand.
            var nowIds = await db.Files
                .Where(f => f.FolderId == link.FolderId && f.Status == StorageFileStatus.Ready)
                .Select(f => f.Id).ToListAsync(ct);
            nowIds.Sort();
            if (!nowIds.SequenceEqual(snapshotIds))
            {
                _log.LogInformation("AlbumZipCache: folder changed during build for link {LinkId} — discarding", linkId);
                return;
            }

            await using (var read = new System.IO.FileStream(tmp, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.None, 1 << 16, System.IO.FileOptions.Asynchronous))
            {
                await _blobs.UploadFromStreamAsync(cachePath, read, "application/zip", ct);
            }
            _log.LogInformation("AlbumZipCache built {N} files for link {LinkId}", written, linkId);
        }
        finally
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Schon-komprimierte Formate nur „storen" statt deflaten.</summary>
    internal static CompressionLevel PickCompression(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".heif"
            or ".mp4" or ".mov" or ".m4v" or ".avi" or ".mkv" or ".webm"
            or ".mp3" or ".m4a" or ".aac" or ".zip" or ".7z" or ".gz" or ".rar"
            ? CompressionLevel.NoCompression
            : CompressionLevel.Fastest;
    }

    private static string UniqueEntryName(string desired, HashSet<string> used)
    {
        var name = desired; int n = 1;
        while (!used.Add(name))
        {
            var ext = System.IO.Path.GetExtension(desired);
            var stem = System.IO.Path.GetFileNameWithoutExtension(desired);
            name = $"{stem} ({++n}){ext}";
        }
        return name;
    }
}
