using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.10.82: UGC-Moderations-Endpoints — App-Store-Blocker Apple 1.2.
/// User-Block + Content-Report für iOS-Parity. Admin-Queue an
/// /api/v1/admin/moderation.
/// </summary>
[ApiController]
[Route("api/v1/moderation")]
[Authorize(Policy = "ApiUser")]
public class ModerationApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly IModerationService _mod;
    private readonly ICurrentUserService _current;

    public ModerationApiController(NimShareDbContext db, IModerationService mod, ICurrentUserService current)
    {
        _db = db; _mod = mod; _current = current;
    }

    public record BlockDto(Guid Id, Guid BlockedUserId, string? BlockedName, string? BlockedEmail,
        string? Reason, DateTimeOffset CreatedAt);

    [HttpGet("blocks")]
    public async Task<IActionResult> ListBlocks(CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        var q = from b in _db.BlockedUsers.AsNoTracking()
                where b.UserId == me.Id
                join u in _db.Users.AsNoTracking() on b.BlockedUserId equals u.Id into gj
                from u in gj.DefaultIfEmpty()
                orderby b.CreatedAt descending
                select new BlockDto(b.Id, b.BlockedUserId, u == null ? null : u.DisplayName,
                    u == null ? null : u.Email, b.Reason, b.CreatedAt);
        return Ok(await q.ToListAsync(ct));
    }

    public record BlockRequest(Guid BlockedUserId, string? Reason);

    [HttpPost("blocks")]
    public async Task<IActionResult> Block([FromBody] BlockRequest req, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        try
        {
            var row = await _mod.BlockAsync(me.Id, req.BlockedUserId, req.Reason, ct);
            return Ok(new { row.Id, row.BlockedUserId, row.CreatedAt });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: 400, title: ex.Message);
        }
    }

    [HttpDelete("blocks/{blockedUserId:guid}")]
    public async Task<IActionResult> Unblock(Guid blockedUserId, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        var ok = await _mod.UnblockAsync(me.Id, blockedUserId, ct);
        return ok ? NoContent() : NotFound();
    }

    public record ReportRequest(
        ContentReportSubjectKind SubjectKind,
        Guid SubjectId,
        ContentReportReason Reason,
        string? Note,
        string? SubjectLabel,
        Guid? SubjectOwnerUserId);

    [HttpPost("reports")]
    public async Task<IActionResult> Report([FromBody] ReportRequest req, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        var r = await _mod.ReportAsync(me.Id, req.SubjectKind, req.SubjectId,
            req.Reason, req.Note, req.SubjectLabel, req.SubjectOwnerUserId, ct);
        return Ok(new { r.Id, r.Status, r.CreatedAt });
    }

    // ── Admin ──

    public record AdminReportDto(Guid Id, ContentReportSubjectKind Kind, Guid SubjectId,
        string? SubjectLabel, ContentReportReason Reason, string? Note,
        Guid ReporterUserId, string? ReporterName, string? ReporterEmail,
        Guid? SubjectOwnerUserId, string? SubjectOwnerName,
        ContentReportStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt,
        ContentReportResolution? Resolution, string? ResolutionNote);

    [HttpGet("admin/reports")]
    public async Task<IActionResult> AdminList([FromQuery] string? status, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();

        IQueryable<ContentReport> q = _db.ContentReports.AsNoTracking();
        if (Enum.TryParse<ContentReportStatus>(status, ignoreCase: true, out var st))
            q = q.Where(r => r.Status == st);
        else
            q = q.Where(r => r.Status == ContentReportStatus.Open); // Default: nur offene

        var rows = await (from r in q.OrderByDescending(r => r.CreatedAt).Take(500)
                          join rp in _db.Users.AsNoTracking() on r.ReporterUserId equals rp.Id into rpj
                          from rp in rpj.DefaultIfEmpty()
                          join own in _db.Users.AsNoTracking() on r.SubjectOwnerUserId equals own.Id into ownj
                          from own in ownj.DefaultIfEmpty()
                          select new AdminReportDto(r.Id, r.SubjectKind, r.SubjectId,
                              r.SubjectLabel, r.Reason, r.Note,
                              r.ReporterUserId, rp == null ? null : rp.DisplayName, rp == null ? null : rp.Email,
                              r.SubjectOwnerUserId, own == null ? null : own.DisplayName,
                              r.Status, r.CreatedAt, r.ResolvedAt, r.Resolution, r.ResolutionNote))
                         .ToListAsync(ct);
        return Ok(rows);
    }

    public record ResolveRequest(ContentReportResolution Resolution, string? Note);

    [HttpPost("admin/reports/{id:guid}/resolve")]
    public async Task<IActionResult> AdminResolve(Guid id, [FromBody] ResolveRequest req, CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();

        var r = await _db.ContentReports.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        r.Status = req.Resolution == ContentReportResolution.NoAction || req.Resolution == ContentReportResolution.Duplicate
            ? ContentReportStatus.Dismissed
            : ContentReportStatus.Resolved;
        r.Resolution = req.Resolution;
        r.ResolutionNote = req.Note;
        r.ResolvedByUserId = me.Id;
        r.ResolvedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { r.Id, r.Status, r.Resolution, r.ResolvedAt });
    }
}
