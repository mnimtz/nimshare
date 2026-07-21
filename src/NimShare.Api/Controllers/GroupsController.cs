using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class GroupsController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;
    private readonly IStringLocalizer<SharedResources> _l;

    public GroupsController(NimShareDbContext db, ICurrentUserService users, IFileAccessService access,
        IStringLocalizer<SharedResources> l)
    {
        _db = db;
        _l = l;
        _users = users;
        _access = access;
    }

    // ── List (all groups for admin, only-mine for regular users) ───────────
    [HttpGet("/groups")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        IQueryable<Group> q = _db.Groups.Include(g => g.Members).OrderBy(g => g.Name);
        if (me.Role != UserRole.Admin)
        {
            var myGroupIds = _db.GroupMemberships.Where(m => m.UserId == me.Id).Select(m => m.GroupId);
            q = q.Where(g => myGroupIds.Contains(g.Id));
        }
        var groups = await q.ToListAsync(ct);
        ViewData["IsAdmin"] = me.Role == UserRole.Admin;
        return View(groups);
    }

    [HttpPost("/groups/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string? description, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        if (string.IsNullOrWhiteSpace(name)) { TempData["Error"] = _l["err.group_name_required"].Value; return RedirectToAction(nameof(List)); }
        var g = new Group
        {
            Name = name.Trim(),
            Description = description?.Trim(),
            CreatedByUserId = me.Id,
        };
        _db.Groups.Add(g);
        _db.GroupMemberships.Add(new GroupMembership { GroupId = g.Id, UserId = me.Id, Role = GroupRole.Manager });
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Detail), new { id = g.Id });
    }

    [HttpPost("/groups/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var g = await _db.Groups.FindAsync(new object[] { id }, ct);
        if (g is null) return NotFound();
        if (me.Role != UserRole.Admin && !await _access.IsGroupManagerAsync(me, g.Id, ct)) return Forbid();
        _db.Groups.Remove(g);
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(List));
    }

    // ── Detail: members + files ────────────────────────────────────────────
    [HttpGet("/groups/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var g = await _db.Groups
            .Include(x => x.Members).ThenInclude(m => m.User)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return NotFound();
        if (me.Role != UserRole.Admin && !await _access.IsGroupMemberAsync(me, g.Id, ct)) return Forbid();

        var myRole = g.Members.SingleOrDefault(m => m.UserId == me.Id)?.Role;
        var canManage = me.Role == UserRole.Admin || myRole == GroupRole.Manager;
        var files = await _db.Files
            .Where(f => f.Scope == FileScope.Group && f.GroupId == g.Id && f.Status == StorageFileStatus.Ready)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
        // v1.10.78: System-Rolle raus (versteckter Service-Account soll
        // nicht als Gruppen-Mitglied auswählbar sein).
        var candidates = canManage
            ? await _db.Users.Where(u => u.IsActive && u.Role != UserRole.System
                                       && !g.Members.Select(m => m.UserId).Contains(u.Id))
                .OrderBy(u => u.DisplayName).ToListAsync(ct)
            : new List<User>();
        ViewData["CanManage"] = canManage;
        ViewData["Files"] = files;
        ViewData["Candidates"] = candidates;
        return View(g);
    }

    [HttpPost("/groups/{id:guid}/members/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(Guid id, Guid userId, string role, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin && !await _access.IsGroupManagerAsync(me, id, ct)) return Forbid();
        var exists = await _db.GroupMemberships.AnyAsync(m => m.GroupId == id && m.UserId == userId, ct);
        if (!exists)
        {
            _db.GroupMemberships.Add(new GroupMembership
            {
                GroupId = id,
                UserId = userId,
                Role = string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ? GroupRole.Manager : GroupRole.Member,
            });
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("/groups/{id:guid}/members/{userId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin && !await _access.IsGroupManagerAsync(me, id, ct)) return Forbid();
        var mgr = await _db.GroupMemberships.SingleOrDefaultAsync(m => m.GroupId == id && m.UserId == userId, ct);
        if (mgr is null) return RedirectToAction(nameof(Detail), new { id });

        // Don't leave the group without a manager.
        if (mgr.Role == GroupRole.Manager)
        {
            var otherMgrs = await _db.GroupMemberships.CountAsync(m => m.GroupId == id && m.UserId != userId && m.Role == GroupRole.Manager, ct);
            if (otherMgrs == 0) { TempData["Error"] = _l["err.last_manager"].Value; return RedirectToAction(nameof(Detail), new { id }); }
        }
        _db.GroupMemberships.Remove(mgr);
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("/groups/{id:guid}/members/{userId:guid}/set-role")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMemberRole(Guid id, Guid userId, string role, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin && !await _access.IsGroupManagerAsync(me, id, ct)) return Forbid();
        var m = await _db.GroupMemberships.SingleOrDefaultAsync(x => x.GroupId == id && x.UserId == userId, ct);
        if (m is null) return NotFound();
        var newRole = string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ? GroupRole.Manager : GroupRole.Member;
        if (m.Role == GroupRole.Manager && newRole == GroupRole.Member)
        {
            var otherMgrs = await _db.GroupMemberships.CountAsync(x => x.GroupId == id && x.UserId != userId && x.Role == GroupRole.Manager, ct);
            if (otherMgrs == 0) { TempData["Error"] = _l["err.last_manager"].Value; return RedirectToAction(nameof(Detail), new { id }); }
        }
        m.Role = newRole;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Detail), new { id });
    }
}
