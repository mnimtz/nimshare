using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>In-app notification feed. Web page at /notifications + JSON API at /api/v1/notifications.</summary>
public class NotificationsController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public NotificationsController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    [Authorize(Policy = "WebUser")]
    [HttpGet("/notifications")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var items = await _db.UserNotifications
            .Where(n => n.UserId == me.Id)
            .OrderByDescending(n => n.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        // Mark everything read while we're at it.
        var now = DateTimeOffset.UtcNow;
        foreach (var n in items.Where(x => x.ReadAt is null)) n.ReadAt = now;
        await _db.SaveChangesAsync(ct);
        return View(items);
    }

    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/notifications/unread-count")]
    public async Task<IActionResult> UnreadCount([FromServices] IUserNotifier notif, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var n = await notif.UnreadCountAsync(me.Id, ct);
        return Ok(new { unread = n });
    }

    public record NotifyDto(Guid Id, string Kind, string Title, string? Body, string? Href,
        Guid? FileId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/notifications")]
    public async Task<IActionResult> ApiList(bool onlyUnread = false, int limit = 100, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var q = _db.UserNotifications.Where(n => n.UserId == me.Id);
        if (onlyUnread) q = q.Where(n => n.ReadAt == null);
        var items = await q.OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(n => new NotifyDto(n.Id, n.Kind.ToString(), n.Title, n.Body, n.Href, n.FileId, n.CreatedAt, n.ReadAt))
            .ToListAsync(ct);
        return Ok(items);
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/notifications/{id:guid}/read")]
    public async Task<IActionResult> ApiMarkRead(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var n = await _db.UserNotifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id, ct);
        if (n is null) return NotFound();
        if (n.ReadAt is null) { n.ReadAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
        return NoContent();
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/notifications/read-all")]
    public async Task<IActionResult> ApiMarkAllRead(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var now = DateTimeOffset.UtcNow;
        var unread = await _db.UserNotifications.Where(n => n.UserId == me.Id && n.ReadAt == null).ToListAsync(ct);
        foreach (var n in unread) n.ReadAt = now;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
