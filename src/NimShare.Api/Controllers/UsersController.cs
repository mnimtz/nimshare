using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public UsersController(NimShareDbContext db, ILocalAuthService auth, ICurrentUserService currentUser)
    {
        _db = db;
        _auth = auth;
        _currentUser = currentUser;
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
            TempData["Notice"] = "User created.";
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
        if (me.Id == id) { TempData["Error"] = "You cannot disable yourself."; return RedirectToAction(nameof(List)); }
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
            if (otherAdmins == 0) { TempData["Error"] = "Cannot demote the last active admin."; return RedirectToAction(nameof(List)); }
        }
        u.Role = newRole;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(List));
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        if (me.Id == id) { TempData["Error"] = "You cannot delete yourself."; return RedirectToAction(nameof(List)); }
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is not null)
        {
            var otherAdmins = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Id != u.Id && x.IsActive, ct);
            if (u.Role == UserRole.Admin && otherAdmins == 0)
            {
                TempData["Error"] = "Cannot delete the last active admin.";
                return RedirectToAction(nameof(List));
            }
            _db.Users.Remove(u);
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToAction(nameof(List));
    }
}
