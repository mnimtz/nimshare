using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public record ReportingKpis(int LinksCount, int Landings, int Downloads, int UniqueVisitors);
public record ReportingDailyPoint(DateOnly Day, int Landings, int Downloads);
public record ReportingTopLink(Guid Id, string Title, string Slug, string OwnerName, int Downloads, int Landings);
public record ReportingCountRow(string Key, int Count);
public record ReportingOwnerOption(Guid Id, string DisplayName);
public record ReportingGroupOption(Guid Id, string Name);

public record ReportingSummaryDto(
    ReportingKpis Kpis,
    List<ReportingDailyPoint> Trend,
    List<ReportingTopLink> TopLinks,
    List<ReportingCountRow> Countries,
    List<ReportingCountRow> Cities);

public interface IReportingService
{
    /// <summary>
    /// Cross-link aggregation over ShareLinkAccess, filtered by date range and
    /// (admin-only) owner/group. Non-admins are always scoped to their own
    /// links, regardless of what's passed in ownerId/groupId.
    /// </summary>
    Task<ReportingSummaryDto> GetSummaryAsync(
        User currentUser, DateTimeOffset from, DateTimeOffset to,
        Guid? ownerId, Guid? groupId, CancellationToken ct = default);

    /// <summary>Owners with at least one link — for the admin-only owner filter dropdown.</summary>
    Task<List<ReportingOwnerOption>> GetOwnerOptionsAsync(CancellationToken ct = default);

    /// <summary>Groups with at least one link targeting them — for the admin-only group filter dropdown.</summary>
    Task<List<ReportingGroupOption>> GetGroupOptionsAsync(CancellationToken ct = default);
}
