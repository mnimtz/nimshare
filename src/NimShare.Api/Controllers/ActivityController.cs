using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class ActivityController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public ActivityController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    [HttpGet("/activity")]
    public async Task<IActionResult> Index(bool all = false, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var showAll = all && me.Role == UserRole.Admin;
        var q = _db.ActivityEvents.Include(e => e.Actor).AsQueryable();
        if (!showAll) q = q.Where(e => e.ActorUserId == me.Id);
        var items = await q.OrderByDescending(e => e.At).Take(200).ToListAsync(ct);
        ViewData["Items"] = items;
        ViewData["ShowAll"] = showAll;
        ViewData["IsAdmin"] = me.Role == UserRole.Admin;
        return View("Index");
    }

    // ── JSON API for iOS ────────────────────────────────────────────────
    public record ActivityDto(string Kind, string ActorName, string Summary,
        Guid? FileId, Guid? FolderId, Guid? GroupId, Guid? TargetUserId, DateTimeOffset At);

    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/activity")]
    public async Task<IActionResult> ApiIndex(bool all = false, int limit = 100, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var showAll = all && me.Role == UserRole.Admin;
        var q = _db.ActivityEvents.Include(e => e.Actor).AsQueryable();
        if (!showAll) q = q.Where(e => e.ActorUserId == me.Id);
        var items = await q.OrderByDescending(e => e.At)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new ActivityDto(e.Kind.ToString(), e.Actor!.DisplayName, e.Summary,
                e.FileId, e.FolderId, e.GroupId, e.TargetUserId, e.At))
            .ToListAsync(ct);
        return Ok(items);
    }
}
