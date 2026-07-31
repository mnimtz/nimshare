using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.11.63 — JSON twin of UsersController's admin user management, for iOS
/// (1:1-Parität mit /settings/users). Same rules/guards as the MVC actions
/// (last-admin protection, self-disable/self-delete guards, quota range),
/// just JSON in/out instead of form-post + TempData redirects.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = "ApiUser")]
public class UsersApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public UsersApiController(NimShareDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private async Task<User?> RequireAdminAsync(CancellationToken ct)
    {
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        return me.Role == UserRole.Admin ? me : null;
    }

    public record UserListItemDto(Guid Id, string DisplayName, string Email, string Role, bool IsActive,
        long QuotaBytes, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await RequireAdminAsync(ct) is null) return Forbid();
        var users = await _db.Users.OrderBy(u => u.CreatedAt)
            .Select(u => new UserListItemDto(u.Id, u.DisplayName, u.Email, u.Role.ToString(), u.IsActive,
                u.QuotaBytes, u.CreatedAt, u.LastSeenAt))
            .ToListAsync(ct);
        return Ok(users);
    }

    public record CreateUserReq(string Email, string DisplayName, string Password, string Role);

    /// <summary>v1.11.65 — JSON twin of UsersController's "/settings/users/create"
    /// (direct creation with an admin-set password, no invite email round-trip).
    /// That MVC action existed since the web user-management page shipped but
    /// was never exposed to iOS — Marcus noticed the app only offers "Einladen".</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserReq req,
        [FromServices] ILocalAuthService authService, CancellationToken ct)
    {
        if (await RequireAdminAsync(ct) is null) return Forbid();
        var role = string.Equals(req.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User;
        try
        {
            var u = await authService.CreateAsync(req.Email, req.DisplayName, req.Password, role, ct);
            return Ok(new UserListItemDto(u.Id, u.DisplayName, u.Email, u.Role.ToString(), u.IsActive,
                u.QuotaBytes, u.CreatedAt, u.LastSeenAt));
        }
        catch (ArgumentException ex) { return Problem(statusCode: 422, title: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 422, title: ex.Message); }
    }

    public record GroupMembershipDto(Guid GroupId, string GroupName, string Role);
    public record UserDetailDto(Guid Id, string DisplayName, string Email, string Role, bool IsActive,
        long QuotaBytes, bool PublicCanRead, bool PublicCanWrite, bool PublicCanDelete,
        List<GroupMembershipDto> Groups);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        if (await RequireAdminAsync(ct) is null) return Forbid();
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        var groups = await _db.GroupMemberships.Where(m => m.UserId == id)
            .Include(m => m.Group)
            .Select(m => new GroupMembershipDto(m.GroupId, m.Group.Name, m.Role.ToString()))
            .ToListAsync(ct);
        return Ok(new UserDetailDto(u.Id, u.DisplayName, u.Email, u.Role.ToString(), u.IsActive, u.QuotaBytes,
            u.PublicCanRead, u.PublicCanWrite, u.PublicCanDelete, groups));
    }

    public record UpdateUserReq(string DisplayName, string Email, string Role, long QuotaGb, bool IsActive,
        Guid[]? GroupIds, string? NewPassword, bool PublicCanRead, bool PublicCanWrite, bool PublicCanDelete);

    [HttpPost("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserReq req,
        [FromServices] IPasswordHasher hasher, CancellationToken ct)
    {
        var me = await RequireAdminAsync(ct);
        if (me is null) return Forbid();
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();

        if (req.QuotaGb < 1 || req.QuotaGb > 10240)
            return Problem(statusCode: 422, title: "Quota out of range (1-10240 GB).");

        var wantRole = string.Equals(req.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User;
        if (u.Role == UserRole.Admin && (wantRole != UserRole.Admin || !req.IsActive))
        {
            var otherAdmins = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Id != u.Id && x.IsActive, ct);
            if (otherAdmins == 0)
                return Problem(statusCode: 422, title: "Cannot demote or disable the last admin.");
        }
        if (u.Id == me.Id && !req.IsActive)
            return Problem(statusCode: 422, title: "Cannot disable your own account.");

        u.DisplayName = (req.DisplayName ?? "").Trim();
        u.Email = (req.Email ?? u.Email).Trim().ToLowerInvariant();
        u.Role = wantRole;
        u.QuotaBytes = req.QuotaGb * 1024L * 1024L * 1024L;
        u.IsActive = req.IsActive;
        u.PublicCanRead = req.PublicCanRead;
        u.PublicCanWrite = req.PublicCanWrite;
        u.PublicCanDelete = req.PublicCanDelete;

        if (!string.IsNullOrEmpty(req.NewPassword))
        {
            if (req.NewPassword.Length < 8)
                return Problem(statusCode: 422, title: "Password must be at least 8 characters.");
            u.PasswordHash = hasher.Hash(req.NewPassword);
        }

        // Group sync — preserves Manager rows, same as UsersController.Update.
        var wanted = (req.GroupIds ?? Array.Empty<Guid>()).Distinct().ToHashSet();
        var existing = await _db.GroupMemberships.Where(m => m.UserId == id).ToListAsync(ct);
        var haveIds = existing.Select(m => m.GroupId).ToHashSet();
        foreach (var gid in wanted.Except(haveIds))
            _db.GroupMemberships.Add(new GroupMembership { UserId = id, GroupId = gid, Role = GroupRole.Member });
        _db.GroupMemberships.RemoveRange(existing.Where(m => !wanted.Contains(m.GroupId) && m.Role != GroupRole.Manager));

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/toggle-active")]
    public async Task<IActionResult> ToggleActive(Guid id, CancellationToken ct)
    {
        var me = await RequireAdminAsync(ct);
        if (me is null) return Forbid();
        if (me.Id == id) return Problem(statusCode: 422, title: "Cannot disable your own account.");
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        u.IsActive = !u.IsActive;
        await _db.SaveChangesAsync(ct);
        return Ok(new { isActive = u.IsActive });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await RequireAdminAsync(ct);
        if (me is null) return Forbid();
        if (me.Id == id) return Problem(statusCode: 422, title: "Cannot delete your own account.");
        var u = await _db.Users.FindAsync(new object[] { id }, ct);
        if (u is null) return NotFound();
        var otherAdmins = await _db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Id != u.Id && x.IsActive, ct);
        if (u.Role == UserRole.Admin && otherAdmins == 0)
            return Problem(statusCode: 422, title: "Cannot delete the last admin.");
        _db.Users.Remove(u);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <summary>JSON twin of GroupsController's list, used by iOS to populate the
/// group multi-select picker on the user-edit screen. Admin-only, all groups
/// (not just the caller's own — distinct from the existing listShareableGroups
/// used by the direct-share picker).</summary>
[ApiController]
[Route("api/v1/groups")]
[Authorize(Policy = "ApiUser")]
public class GroupsApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public GroupsApiController(NimShareDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public record GroupListItemDto(Guid Id, string Name, int MemberCount);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _currentUser.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var groups = await _db.Groups.OrderBy(g => g.Name)
            .Select(g => new GroupListItemDto(g.Id, g.Name, g.Members.Count))
            .ToListAsync(ct);
        return Ok(groups);
    }
}
