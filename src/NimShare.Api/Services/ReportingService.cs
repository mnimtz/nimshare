using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v2.0-web: erste Version des Reporting-Moduls — Kreuz-Link-Auswertung statt
/// der bisherigen Einzel-Link-Reports (LinkReportController, siehe
/// /links/{id}). Gleiche Roh-Datenquelle (ShareLinkAccess), gleiches
/// In-Memory-Aggregations-Muster (siehe Kommentar dort: "für aktuelle
/// Volumen reicht In-Memory-Aggregation, später ggf. SQL-Group-By") — hier
/// bewusst beibehalten statt SQL-seitiger GroupBy, weil komplexe
/// Datums-Bucketing-Ausdrücke über SQLite/EF-Core nicht zuverlässig
/// übersetzt werden. Bei sehr großem Zugriffsvolumen müsste das später
/// durch echte SQL-Aggregation ersetzt werden.
/// </summary>
public class ReportingService : IReportingService
{
    private readonly NimShareDbContext _db;

    public ReportingService(NimShareDbContext db)
    {
        _db = db;
    }

    private IQueryable<ShareLink> ScopedLinks(User currentUser, Guid? ownerId, Guid? groupId)
    {
        var isAdmin = currentUser.Role == UserRole.Admin;
        // Nicht-Admins sehen ausschließlich ihre eigenen Links — Owner-/
        // Gruppen-Filter aus der Query werden für sie ignoriert, analog dem
        // Admin-Personenfilter auf der Links-Seite (v1.11.72).
        var effectiveOwnerId = isAdmin ? ownerId : currentUser.Id;
        var effectiveGroupId = isAdmin ? groupId : null;

        var q = _db.ShareLinks.AsQueryable();
        if (effectiveOwnerId.HasValue) q = q.Where(l => l.OwnerId == effectiveOwnerId.Value);
        if (effectiveGroupId.HasValue)
        {
            q = q.Where(l =>
                (l.FileId != null && l.File!.GroupId == effectiveGroupId)
                || (l.FolderId != null && l.Folder!.OwnerGroupId == effectiveGroupId));
        }
        return q;
    }

    public async Task<ReportingSummaryDto> GetSummaryAsync(
        User currentUser, DateTimeOffset from, DateTimeOffset to,
        Guid? ownerId, Guid? groupId, CancellationToken ct = default)
    {
        var linksQuery = ScopedLinks(currentUser, ownerId, groupId);

        var linkMeta = await linksQuery
            .Select(l => new
            {
                l.Id,
                l.Slug,
                OwnerName = l.Owner.DisplayName,
                Title = l.File != null ? l.File.Name : (l.Folder != null ? l.Folder.Name : l.Slug),
            })
            .ToListAsync(ct);

        var linkIds = linkMeta.Select(l => l.Id).ToList();

        var events = linkIds.Count == 0
            ? new List<ShareLinkAccess>()
            : await _db.ShareLinkAccesses
                .Where(a => linkIds.Contains(a.ShareLinkId) && a.At >= from && a.At <= to)
                .ToListAsync(ct);

        var landings = events.Count(e => e.Kind == ShareLinkAccessKind.Landing);
        var downloads = events.Count(e => e.Kind == ShareLinkAccessKind.Download);
        var uniqueVisitors = events.Where(e => !string.IsNullOrEmpty(e.IpHash))
            .Select(e => e.IpHash).Distinct().Count();

        // Tages-Trend über den gesamten gewählten Zeitraum (nicht auf 30 Tage
        // begrenzt wie der Einzel-Link-Report — hier ist der Zeitraum ja
        // bereits vom Filter vorgegeben).
        var trend = events
            .Where(e => e.Kind == ShareLinkAccessKind.Landing || e.Kind == ShareLinkAccessKind.Download)
            .GroupBy(e => DateOnly.FromDateTime(e.At.UtcDateTime.Date))
            .Select(g => new ReportingDailyPoint(
                g.Key,
                g.Count(x => x.Kind == ShareLinkAccessKind.Landing),
                g.Count(x => x.Kind == ShareLinkAccessKind.Download)))
            .OrderBy(p => p.Day)
            .ToList();

        var metaById = linkMeta.ToDictionary(l => l.Id);
        var topLinks = events
            .Where(e => e.Kind == ShareLinkAccessKind.Download || e.Kind == ShareLinkAccessKind.Landing)
            .GroupBy(e => e.ShareLinkId)
            .Select(g => new
            {
                LinkId = g.Key,
                Downloads = g.Count(x => x.Kind == ShareLinkAccessKind.Download),
                Landings = g.Count(x => x.Kind == ShareLinkAccessKind.Landing),
            })
            .OrderByDescending(x => x.Downloads).ThenByDescending(x => x.Landings)
            .Take(10)
            .Where(x => metaById.ContainsKey(x.LinkId))
            .Select(x => new ReportingTopLink(
                x.LinkId, metaById[x.LinkId].Title, metaById[x.LinkId].Slug,
                metaById[x.LinkId].OwnerName, x.Downloads, x.Landings))
            .ToList();

        var countries = events
            .Where(e => !string.IsNullOrEmpty(e.CountryCode))
            .GroupBy(e => e.CountryCode!)
            .Select(g => new ReportingCountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(10)
            .ToList();

        var cities = events
            .Where(e => !string.IsNullOrEmpty(e.City))
            .GroupBy(e => e.City!)
            .Select(g => new ReportingCountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .Take(10)
            .ToList();

        var kpis = new ReportingKpis(linkMeta.Count, landings, downloads, uniqueVisitors);
        return new ReportingSummaryDto(kpis, trend, topLinks, countries, cities);
    }

    public async Task<List<ReportingOwnerOption>> GetOwnerOptionsAsync(CancellationToken ct = default)
    {
        return await _db.ShareLinks
            .Select(l => new { l.OwnerId, OwnerName = l.Owner.DisplayName })
            .Distinct()
            .OrderBy(o => o.OwnerName)
            .Select(o => new ReportingOwnerOption(o.OwnerId, o.OwnerName))
            .ToListAsync(ct);
    }

    public async Task<List<ReportingGroupOption>> GetGroupOptionsAsync(CancellationToken ct = default)
    {
        var fileGroupIds = _db.ShareLinks.Where(l => l.FileId != null && l.File!.GroupId != null)
            .Select(l => l.File!.GroupId!.Value);
        var folderGroupIds = _db.ShareLinks.Where(l => l.FolderId != null && l.Folder!.OwnerGroupId != null)
            .Select(l => l.Folder!.OwnerGroupId!.Value);
        var groupIds = await fileGroupIds.Union(folderGroupIds).Distinct().ToListAsync(ct);
        if (groupIds.Count == 0) return new List<ReportingGroupOption>();

        return await _db.Groups
            .Where(g => groupIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .Select(g => new ReportingGroupOption(g.Id, g.Name))
            .ToListAsync(ct);
    }
}
