using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.10.82: Admin-MVC-Seite für UGC-Moderations-Queue. Auf iOS ist
/// „Melden" der App-Store-Compliance-Pfad; hier arbeitet der Admin die
/// gemeldeten Ressourcen ab (Ansehen, entfernen, Report abweisen).
/// </summary>
[Authorize(Policy = "WebUser")]
public class ModerationController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _current;

    public ModerationController(NimShareDbContext db, ICurrentUserService current)
    {
        _db = db; _current = current;
    }

    public record ReportRow(ContentReport Report, User? Reporter, User? Owner);

    [HttpGet("/settings/moderation")]
    public async Task<IActionResult> List(string? status, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();

        IQueryable<ContentReport> q = _db.ContentReports.AsNoTracking();
        if (Enum.TryParse<ContentReportStatus>(status, ignoreCase: true, out var st))
            q = q.Where(r => r.Status == st);
        else
            q = q.Where(r => r.Status == ContentReportStatus.Open);

        var reports = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var userIds = reports.Select(r => r.ReporterUserId)
            .Concat(reports.Where(r => r.SubjectOwnerUserId.HasValue).Select(r => r.SubjectOwnerUserId!.Value))
            .Distinct().ToList();
        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var rows = reports.Select(r => new ReportRow(r,
            users.GetValueOrDefault(r.ReporterUserId),
            r.SubjectOwnerUserId.HasValue ? users.GetValueOrDefault(r.SubjectOwnerUserId.Value) : null))
            .ToList();

        ViewData["Filter"] = status ?? "open";
        return View(rows);
    }

    [HttpPost("/settings/moderation/{id:guid}/resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid id, ContentReportResolution resolution, string? note, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();

        var r = await _db.ContentReports.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        r.Status = resolution == ContentReportResolution.NoAction || resolution == ContentReportResolution.Duplicate
            ? ContentReportStatus.Dismissed
            : ContentReportStatus.Resolved;
        r.Resolution = resolution;
        r.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        r.ResolvedByUserId = me.Id;
        r.ResolvedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(List));
    }
}
