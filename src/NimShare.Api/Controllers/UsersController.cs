using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>Admin-only user management. Reachable both as MVC pages (/settings/users)
/// and as JSON (/api/v1/users). The two share the same controller for simplicity.</summary>
public class UsersController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ILocalAuthService _auth;
    private readonly ICurrentUserService _currentUser;
    private readonly IStringLocalizer<SharedResources> _l;

    public UsersController(NimShareDbContext db, ILocalAuthService auth, ICurrentUserService currentUser,
        IStringLocalizer<SharedResources> localizer)
    {
        _db = db;
        _auth = auth;
        _currentUser = currentUser;
        _l = localizer;
    }

    private async Task<bool> RequireAdmin(CancellationToken ct)
    {
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        return me.Role == UserRole.Admin;
    }

    // ── MVC list page ─────────────────────────────────────────────────────

    [Authorize(Policy = "WebUser")]
    [HttpGet("/settings/users")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var users = await _db.Users.OrderBy(u => u.CreatedAt).ToListAsync(ct);
        // Groups list for the Groups section on the same page.
        var groups = await _db.Groups
            .Include(g => g.Members).ThenInclude(m => m.User)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
        ViewData["Groups"] = groups;
        return View(users);
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string email, string displayName, string password, string role, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        try
        {
            var r = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User;
            await _auth.CreateAsync(email, displayName, password, r, ct);
            TempData["Notice"] = _l["notice.user_created"].Value;
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(List));
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/toggle-active")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(Guid id, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        if (me.Id == id) { TempData["Error"] = _l["err.cannot_disable_self"].Value; return RedirectToAction(nameof(List)); }
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is not null) { u.IsActive = !u.IsActive; await _db.SaveChangesAsync(ct); }
        return RedirectToAction(nameof(List));
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/set-role")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(Guid id, string role, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        var newRole = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User;
        // Don't allow demoting the last admin (including yourself).
        if (newRole == UserRole.User && u.Role == UserRole.Admin)
        {
            var otherAdmins = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Id != u.Id && x.IsActive, ct);
            if (otherAdmins == 0) { TempData["Error"] = _l["err.last_admin"].Value; return RedirectToAction(nameof(List)); }
        }
        u.Role = newRole;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(List));
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/set-quota")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetQuota(Guid id, long quotaGb, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        if (quotaGb < 1 || quotaGb > 10240) { TempData["Error"] = _l["err.quota_range"].Value; return RedirectToAction(nameof(List)); }
        u.QuotaBytes = quotaGb * 1024L * 1024L * 1024L;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["notice.quota_updated"].Value;
        return RedirectToAction(nameof(List));
    }

    // ── Modern Edit page (all fields on one screen) ────────────────────────
    [Authorize(Policy = "WebUser")]
    [HttpGet("/settings/users/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync(ct);
        var mine = await _db.GroupMemberships.Where(m => m.UserId == id)
            .Select(m => new { m.GroupId, m.Role }).ToListAsync(ct);
        ViewData["Groups"] = groups;
        ViewData["MemberIds"] = mine.Select(x => x.GroupId).ToHashSet();
        ViewData["ManagerIds"] = mine.Where(x => x.Role == GroupRole.Manager).Select(x => x.GroupId).ToHashSet();
        return View("Edit", u);
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid id, string displayName, string email, string role,
        long quotaGb, bool isActive, Guid[]? groupIds, string? newPassword,
        [FromServices] IPasswordHasher hasher, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();

        if (quotaGb < 1 || quotaGb > 10240)
        {
            TempData["Error"] = _l["err.quota_range"].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Self-guard: can't demote / disable the last active admin (including self).
        var wantRole = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User;
        if (u.Role == UserRole.Admin && (wantRole != UserRole.Admin || !isActive))
        {
            var otherAdmins = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Id != u.Id && x.IsActive, ct);
            if (otherAdmins == 0)
            {
                TempData["Error"] = _l["err.last_admin"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }
        }
        if (u.Id == me.Id && !isActive)
        {
            TempData["Error"] = _l["err.cannot_disable_self"].Value;
            return RedirectToAction(nameof(Edit), new { id });
        }

        u.DisplayName = (displayName ?? "").Trim();
        u.Email = (email ?? u.Email).Trim().ToLowerInvariant();
        u.Role = wantRole;
        u.QuotaBytes = quotaGb * 1024L * 1024L * 1024L;
        u.IsActive = isActive;

        // Optional password reset by admin (only for local accounts).
        if (!string.IsNullOrEmpty(newPassword))
        {
            if (newPassword.Length < 8)
            {
                TempData["Error"] = _l["err.password_too_short"].Value;
                return RedirectToAction(nameof(Edit), new { id });
            }
            u.PasswordHash = hasher.Hash(newPassword);
        }

        // Group sync (preserves Manager rows).
        var wanted = (groupIds ?? Array.Empty<Guid>()).Distinct().ToHashSet();
        var existing = await _db.GroupMemberships.Where(m => m.UserId == id).ToListAsync(ct);
        var haveIds = existing.Select(m => m.GroupId).ToHashSet();
        foreach (var gid in wanted.Except(haveIds))
            _db.GroupMemberships.Add(new GroupMembership { UserId = id, GroupId = gid, Role = GroupRole.Member });
        _db.GroupMemberships.RemoveRange(existing.Where(m => !wanted.Contains(m.GroupId) && m.Role != GroupRole.Manager));

        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["notice.user_updated"].Value;
        return RedirectToAction(nameof(Edit), new { id });
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/set-groups")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGroups(Guid id, Guid[] groupIds, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        var wanted = (groupIds ?? Array.Empty<Guid>()).Distinct().ToHashSet();
        var existing = await _db.GroupMemberships.Where(m => m.UserId == id).ToListAsync(ct);
        var haveIds = existing.Select(m => m.GroupId).ToHashSet();
        // Add missing (as Member — Managers are promoted separately elsewhere)
        foreach (var gid in wanted.Except(haveIds))
        {
            _db.GroupMemberships.Add(new GroupMembership { UserId = id, GroupId = gid, Role = GroupRole.Member });
        }
        // Remove ones no longer wanted — but leave Manager rows alone so a
        // simple save-of-the-form can't silently demote them.
        _db.GroupMemberships.RemoveRange(
            existing.Where(m => !wanted.Contains(m.GroupId) && m.Role != GroupRole.Manager));
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["notice.groups_updated"].Value;
        return RedirectToAction(nameof(List));
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        if (me.Id == id) { TempData["Error"] = _l["err.cannot_delete_self"].Value; return RedirectToAction(nameof(List)); }
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is not null)
        {
            var otherAdmins = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Id != u.Id && x.IsActive, ct);
            if (u.Role == UserRole.Admin && otherAdmins == 0)
            {
                TempData["Error"] = _l["err.last_admin"].Value;
                return RedirectToAction(nameof(List));
            }
            _db.Users.Remove(u);
            await _db.SaveChangesAsync(ct);
            TempData["Notice"] = _l["notice.user_deleted"].Value;
        }
        return RedirectToAction(nameof(List));
    }
}
