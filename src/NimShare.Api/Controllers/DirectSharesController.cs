using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Direct-share management API. A direct share grants Read or Write on a file
/// or a folder to either a specific user or a group, without going through a
/// public link. Used by the "Share to user/group" modal.
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/direct-shares")]
public class DirectSharesController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;
    private readonly IFolderService _folders;
    private readonly IActivityLogger _log;

    public DirectSharesController(NimShareDbContext db, ICurrentUserService users,
        IFileAccessService access, IFolderService folders, IActivityLogger log)
    {
        _db = db;
        _users = users;
        _access = access;
        _folders = folders;
        _log = log;
    }

    public record CreateReq(Guid? FileId, Guid? FolderId, Guid? UserId, Guid? GroupId, string Permission);
    public record DirectShareDto(Guid Id, Guid? FileId, Guid? FolderId, Guid? UserId, string? UserDisplayName,
        Guid? GroupId, string? GroupName, string Permission, DateTimeOffset CreatedAt);
    public record UserOption(Guid Id, string DisplayName, string Email);
    public record GroupOption(Guid Id, string Name);

    // ── Look-up helpers used by the pickers ─────────────────────────────
    [HttpGet("users")]
    public async Task<IActionResult> SearchUsers(string q, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        q = (q ?? "").Trim();
        // Require at least 2 characters so an empty q doesn't enumerate the
        // whole directory. Escape LIKE meta-chars so users can't wildcard.
        if (q.Length < 2) return Ok(Array.Empty<UserOption>());
        var escaped = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var like = "%" + escaped + "%";
        var rows = await _db.Users
            .Where(u => u.IsActive && u.Id != me.Id)
            .Where(u => EF.Functions.Like(u.DisplayName, like, "\\") || EF.Functions.Like(u.Email, like, "\\"))
            .OrderBy(u => u.DisplayName)
            .Take(20)
            .Select(u => new UserOption(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("groups")]
    public async Task<IActionResult> ListGroups(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        // Admins see all groups (can share into any). Regular users only their
        // own — you can't grant to a group you're not part of.
        IQueryable<Group> q = _db.Groups.OrderBy(g => g.Name);
        if (me.Role != UserRole.Admin)
        {
            var mine = _db.GroupMemberships.Where(m => m.UserId == me.Id).Select(m => m.GroupId);
            q = q.Where(g => mine.Contains(g.Id));
        }
        var rows = await q.Select(g => new GroupOption(g.Id, g.Name)).ToListAsync(ct);
        return Ok(rows);
    }

    // ── List grants for a single file or folder ─────────────────────────
    [HttpGet("for-file/{fileId:guid}")]
    public async Task<IActionResult> ForFile(Guid fileId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { fileId }, ct);
        if (file is null) return NotFound();
        if (!await _access.CanShareAsync(me, file, ct)) return Forbid();
        var rows = await _db.DirectShares
            .Where(s => s.FileId == fileId)
            .Include(s => s.TargetUser)
            .Include(s => s.TargetGroup)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new DirectShareDto(s.Id, s.FileId, s.FolderId,
                s.TargetUserId, s.TargetUser!.DisplayName,
                s.TargetGroupId, s.TargetGroup!.Name,
                s.Permission.ToString(), s.CreatedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("for-folder/{folderId:guid}")]
    public async Task<IActionResult> ForFolder(Guid folderId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { folderId }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanReadAsync(folder, me, ct)) return Forbid();
        var rows = await _db.DirectShares
            .Where(s => s.FolderId == folderId)
            .Include(s => s.TargetUser)
            .Include(s => s.TargetGroup)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new DirectShareDto(s.Id, s.FileId, s.FolderId,
                s.TargetUserId, s.TargetUser!.DisplayName,
                s.TargetGroupId, s.TargetGroup!.Name,
                s.Permission.ToString(), s.CreatedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }

    // ── Grant / revoke ──────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if ((req.FileId is null) == (req.FolderId is null))
            return Problem(statusCode: 422, title: "Either FileId or FolderId is required (exactly one).");
        if ((req.UserId is null) == (req.GroupId is null))
            return Problem(statusCode: 422, title: "Either UserId or GroupId is required (exactly one).");
        if (!Enum.TryParse<DirectSharePermission>(req.Permission, true, out var perm))
            return Problem(statusCode: 422, title: "Permission must be Read or Write.");

        // Authorization: not just "can share", but "may not grant a higher
        // permission than you have yourself" — otherwise a Read-recipient
        // could re-share as Write and escalate. Compute the caller's own
        // effective permission on the item, then require perm ≤ that.
        DirectSharePermission? mine;
        if (req.FileId is Guid fid)
        {
            var file = await _db.Files.FindAsync(new object[] { fid }, ct);
            if (file is null) return NotFound();
            mine = await _access.EffectivePermissionOnFileAsync(me, file, ct);
        }
        else
        {
            var folder = await _db.Folders.FindAsync(new object[] { req.FolderId!.Value }, ct);
            if (folder is null) return NotFound();
            mine = await _access.EffectivePermissionOnFolderAsync(me, folder, ct);
        }
        if (mine is null) return Forbid();
        if (perm > mine)
            return Problem(statusCode: 403, title: "Cannot grant a permission higher than your own on this item.");

        // Idempotency: same (item, target) upserts the permission.
        var existing = await _db.DirectShares.FirstOrDefaultAsync(s =>
            s.FileId == req.FileId && s.FolderId == req.FolderId
            && s.TargetUserId == req.UserId && s.TargetGroupId == req.GroupId, ct);
        DirectShare share;
        if (existing is not null)
        {
            existing.Permission = perm;
            share = existing;
        }
        else
        {
            share = new DirectShare
            {
                FileId = req.FileId,
                FolderId = req.FolderId,
                TargetUserId = req.UserId,
                TargetGroupId = req.GroupId,
                Permission = perm,
                SharedByUserId = me.Id,
            };
            _db.DirectShares.Add(share);
        }
        await _db.SaveChangesAsync(ct);
        await _log.LogAsync(ActivityKind.DirectShareGranted, me,
            $"granted {perm} on {(req.FileId is null ? "folder" : "file")} to {(req.UserId is null ? "group" : "user")}",
            fileId: req.FileId, folderId: req.FolderId, groupId: req.GroupId, targetUserId: req.UserId, ct: ct);
        return Ok(new { id = share.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var share = await _db.DirectShares.FindAsync(new object[] { id }, ct);
        if (share is null) return NotFound();
        // Revoke rights: the granter, an admin, or the original file/folder
        // owner may all take a grant back.
        var ok = me.Role == UserRole.Admin || share.SharedByUserId == me.Id;
        if (!ok && share.FileId is Guid fid)
        {
            var file = await _db.Files.FindAsync(new object[] { fid }, ct);
            ok = file is not null && file.OwnerId == me.Id;
        }
        if (!ok && share.FolderId is Guid folid)
        {
            var folder = await _db.Folders.FindAsync(new object[] { folid }, ct);
            ok = folder is not null && folder.OwnerUserId == me.Id;
        }
        if (!ok) return Forbid();
        _db.DirectShares.Remove(share);
        await _db.SaveChangesAsync(ct);
        await _log.LogAsync(ActivityKind.DirectShareRevoked, me, "revoked direct share",
            fileId: share.FileId, folderId: share.FolderId, groupId: share.TargetGroupId, targetUserId: share.TargetUserId, ct: ct);
        return NoContent();
    }

    // ── "Shared with me" — files/folders others have granted to me ──────
    public record SharedWithMeItem(string Kind, Guid Id, string Name, string Permission, string SharedByName, DateTimeOffset SharedAt);

    [HttpGet("shared-with-me")]
    public async Task<IActionResult> SharedWithMe(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var myGroupIds = await _db.GroupMemberships
            .Where(m => m.UserId == me.Id).Select(m => m.GroupId).ToListAsync(ct);
        var shares = await _db.DirectShares
            .Where(s => s.TargetUserId == me.Id || (s.TargetGroupId != null && myGroupIds.Contains(s.TargetGroupId.Value)))
            .Include(s => s.File).Include(s => s.Folder).Include(s => s.SharedByUser)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        var items = shares.Select(s => new SharedWithMeItem(
            Kind: s.FileId is not null ? "file" : "folder",
            Id: s.FileId ?? s.FolderId!.Value,
            Name: s.File?.Name ?? s.Folder?.Name ?? "?",
            Permission: s.Permission.ToString(),
            SharedByName: s.SharedByUser?.DisplayName ?? "?",
            SharedAt: s.CreatedAt)).ToList();
        return Ok(items);
    }
}
