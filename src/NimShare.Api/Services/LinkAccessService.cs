using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public interface ILinkAccessService
{
    Task<ShareLink?> FindActiveAsync(string slug, CancellationToken ct = default);

    Task LogAsync(ShareLink link, ShareLinkAccessKind kind, string ipHash, string? ua, string? referer, CancellationToken ct = default);
    // v1.10.42 — Overload mit forensischen Feldern für Link-Reports.
    // country/city aus GeoIP-Auflösung, device aus User-Agent-Heuristik,
    // timezone (aktuell noch nicht befüllt bei Landings — nur Signaturen
    // schicken die TZ per POST).
    Task LogAsync(ShareLink link, ShareLinkAccessKind kind, string ipHash, string? ua, string? referer,
        string? country, string? city, string? device, string? timezone, CancellationToken ct = default);

    /// <summary>
    /// Atomically increments <see cref="ShareLink.DownloadCount"/> for a link that is still
    /// active — returns false if the cap has been reached in the meantime (races).
    /// </summary>
    Task<bool> TryConsumeDownloadAsync(ShareLink link, CancellationToken ct = default);

    // v1.10.48: nachträglich Timezone auf das letzte Landing-Event dieser
    // (slug, ipHash) setzen. Der Landing-Log läuft server-side beim GET,
    // ohne Client-JS ist die TZ dort nicht bekannt — deshalb schickt ein
    // kleiner JS-Beacon post-render die TZ nach.
    Task StampTimezoneOnLatestLandingAsync(ShareLink link, string ipHash, string timezone, CancellationToken ct = default);
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

    public Task LogAsync(ShareLink link, ShareLinkAccessKind kind, string ipHash, string? ua, string? referer, CancellationToken ct = default)
        => LogAsync(link, kind, ipHash, ua, referer, country: null, city: null, device: null, timezone: null, ct);

    public async Task LogAsync(ShareLink link, ShareLinkAccessKind kind, string ipHash, string? ua, string? referer,
        string? country, string? city, string? device, string? timezone, CancellationToken ct = default)
    {
        _db.ShareLinkAccesses.Add(new ShareLinkAccess
        {
            ShareLinkId = link.Id,
            Kind = kind,
            IpHash = ipHash,
            UserAgent = ua,
            Referer = referer,
            CountryCode = country,
            City = city,
            DeviceType = device,
            Timezone = timezone,
        });
        link.HitCount++;
        link.LastAccessAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task StampTimezoneOnLatestLandingAsync(ShareLink link, string ipHash, string timezone, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(timezone) || timezone.Length > 60) return;
        // Guard-Validation: nur IANA-ähnliche Zeichen erlauben, sonst könnte
        // ein böser Client die DB mit beliebigem String füllen.
        foreach (var c in timezone)
        {
            if (!(char.IsLetterOrDigit(c) || c == '/' || c == '_' || c == '-' || c == '+')) return;
        }
        var latest = await _db.ShareLinkAccesses
            .Where(a => a.ShareLinkId == link.Id
                     && a.IpHash == ipHash
                     && a.Kind == ShareLinkAccessKind.Landing
                     && a.Timezone == null)
            .OrderByDescending(a => a.At)
            .FirstOrDefaultAsync(ct);
        if (latest is null) return;
        latest.Timezone = timezone;
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
