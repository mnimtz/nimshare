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
}
