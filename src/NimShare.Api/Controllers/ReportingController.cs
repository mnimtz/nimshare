using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v2.0-web: Reporting-Modul, erste Version — Kreuz-Link-Auswertung über
/// Zeitraum + (Admin-only) Owner-/Gruppen-Filter. Ergänzt die bestehenden
/// Einzel-Link-Reports (siehe LinkReportController, /links/{id}) um eine
/// aggregierte Sicht über mehrere Links hinweg. Web-only in dieser Version.
/// </summary>
[Authorize(Policy = "WebUser")]
public class ReportingController : Controller
{
    private readonly IReportingService _reporting;
    private readonly ICurrentUserService _users;

    public ReportingController(IReportingService reporting, ICurrentUserService users)
    {
        _reporting = reporting;
        _users = users;
    }

    public record SummaryQuery(string? From, string? To, Guid? OwnerId, Guid? GroupId);

    // Default-Zeitraum: letzte 30 Tage, jeweils auf Tagesgrenzen normiert
    // (To = Ende des heutigen Tages) — sonst fehlt der laufende Tag im
    // Trend, weil "jetzt" meist vor Mitternacht liegt.
    private static (DateTimeOffset From, DateTimeOffset To) ResolveRange(string? fromRaw, string? toRaw)
    {
        var to = DateTimeOffset.TryParse(toRaw, out var toParsed)
            ? toParsed.Date.AddDays(1).AddTicks(-1)
            : DateTimeOffset.UtcNow.Date.AddDays(1).AddTicks(-1);
        var from = DateTimeOffset.TryParse(fromRaw, out var fromParsed)
            ? fromParsed.Date
            : to.Date.AddDays(-29);
        return (from, to);
    }

    [HttpGet("/reporting")]
    public async Task<IActionResult> Index([FromQuery] SummaryQuery q, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var isAdmin = me.Role == UserRole.Admin;
        ViewData["IsAdmin"] = isAdmin;
        if (isAdmin)
        {
            ViewData["Owners"] = await _reporting.GetOwnerOptionsAsync(ct);
            ViewData["Groups"] = await _reporting.GetGroupOptionsAsync(ct);
        }

        var (from, to) = ResolveRange(q.From, q.To);
        ViewData["From"] = from;
        ViewData["To"] = to;
        ViewData["OwnerId"] = q.OwnerId;
        ViewData["GroupId"] = q.GroupId;

        var summary = await _reporting.GetSummaryAsync(me, from, to, q.OwnerId, q.GroupId, ct);
        return View(summary);
    }

    [HttpGet("/api/v1/reporting/summary")]
    public async Task<IActionResult> Summary([FromQuery] SummaryQuery q, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var (from, to) = ResolveRange(q.From, q.To);
        var summary = await _reporting.GetSummaryAsync(me, from, to, q.OwnerId, q.GroupId, ct);
        return Ok(summary);
    }
}
