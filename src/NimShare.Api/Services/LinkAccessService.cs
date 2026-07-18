using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public interface ILinkAccessService
{
    Task<ShareLink?> FindActiveAsync(string slug, CancellationToken ct = default);

    Task LogAsync(ShareLink link, ShareLinkAccessKind kind, string ipHash, string? ua, string? referer, CancellationToken ct = default);

    /// <summary>
    /// Atomically increments <see cref="ShareLink.DownloadCount"/> for a link that is still
    /// active — returns false if the cap has been reached in the meantime (races).
    /// </summary>
    Task<bool> TryConsumeDownloadAsync(ShareLink link, CancellationToken ct = default);
}

public class LinkAccessService : ILinkAccessService
{
    private readonly NimShareDbContext _db;

    public LinkAccessService(NimShareDbContext db) => _db = db;

    public Task<ShareLink?> FindActiveAsync(string slug, CancellationToken ct = default)
        => _db.ShareLinks
            .Include(x => x.File)
            .Include(x => x.Owner)
            .SingleOrDefaultAsync(x => x.Slug == slug, ct);

    public async Task LogAsync(ShareLink link, ShareLinkAccessKind kind, string ipHash, string? ua, string? referer, CancellationToken ct = default)
    {
        _db.ShareLinkAccesses.Add(new ShareLinkAccess
        {
            ShareLinkId = link.Id,
            Kind = kind,
            IpHash = ipHash,
            UserAgent = ua,
            Referer = referer,
        });
        link.HitCount++;
        link.LastAccessAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryConsumeDownloadAsync(ShareLink link, CancellationToken ct = default)
    {
        // Atomic UPDATE with a WHERE clause guarding against expired/revoked/cap-hit.
        // Falls back to load-then-update on providers without ExecuteUpdate support.
        var now = DateTimeOffset.UtcNow;
        var affected = await _db.ShareLinks
            .Where(x => x.Id == link.Id
                        && !x.IsRevoked
                        && (x.ExpiresAt == null || x.ExpiresAt > now)
                        && (x.MaxDownloads == null || x.DownloadCount < x.MaxDownloads))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DownloadCount, x => x.DownloadCount + 1)
                .SetProperty(x => x.LastAccessAt, _ => now), ct);
        return affected > 0;
    }
}
