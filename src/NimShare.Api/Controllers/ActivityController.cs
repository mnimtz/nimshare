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
}
