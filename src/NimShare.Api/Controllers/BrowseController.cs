using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Scoped file browser. /files/personal, /files/public, /files/group/{id}.
/// Each shows the files that belong to that scope, filtered by permissions.
/// </summary>
[Authorize(Policy = "WebUser")]
public class BrowseController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;

    public BrowseController(NimShareDbContext db, ICurrentUserService users, IFileAccessService access)
    {
        _db = db;
        _users = users;
        _access = access;
    }

    [HttpGet("/files")]
    [HttpGet("/files/personal")]
    public async Task<IActionResult> Personal(CancellationToken ct) => await Render(FileScope.Personal, null, ct);

    [HttpGet("/files/public")]
    public async Task<IActionResult> Public(CancellationToken ct) => await Render(FileScope.Public, null, ct);

    [HttpGet("/files/group/{groupId:guid}")]
    public async Task<IActionResult> Group(Guid groupId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin && !await _access.IsGroupMemberAsync(me, groupId, ct))
            return Forbid();
        return await Render(FileScope.Group, groupId, ct);
    }

    private async Task<IActionResult> Render(FileScope scope, Guid? groupId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var q = _db.Files
            .Include(f => f.Owner)
            .Where(f => f.Status == StorageFileStatus.Ready && f.Scope == scope);
        if (scope == FileScope.Personal) q = q.Where(f => f.OwnerId == me.Id);
        if (scope == FileScope.Group) q = q.Where(f => f.GroupId == groupId);

        var files = await q.OrderByDescending(f => f.CreatedAt).ToListAsync(ct);

        ViewData["Scope"] = scope;
        ViewData["GroupId"] = groupId;
        if (groupId is Guid g)
        {
            var group = await _db.Groups.FindAsync(new object[] { g }, ct);
            ViewData["GroupName"] = group?.Name ?? "";
            ViewData["CanManageGroup"] = me.Role == UserRole.Admin || await _access.IsGroupManagerAsync(me, g, ct);
        }
        return View("Browse", files);
    }
}
