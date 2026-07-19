using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Read-only overrides that cap a user's or group's permission on a folder
/// subtree. Only the folder owner (or a group manager, or an admin) can add
/// or remove them.
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/folder-permissions")]
public class FolderPermissionsController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;
    private readonly IFolderService _folders;

    public FolderPermissionsController(NimShareDbContext db, ICurrentUserService users,
        IFileAccessService access, IFolderService folders)
    {
        _db = db; _users = users; _access = access; _folders = folders;
    }

    public record OverrideDto(Guid Id, Guid FolderId, Guid? UserId, string? UserName,
        Guid? GroupId, string? GroupName, string MaxPermission, DateTimeOffset CreatedAt);
    public record CreateReq(Guid FolderId, Guid? UserId, Guid? GroupId);

    [HttpGet("for-folder/{folderId:guid}")]
    public async Task<IActionResult> ForFolder(Guid folderId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { folderId }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct) && me.Role != UserRole.Admin) return Forbid();

        var rows = await _db.FolderAccessOverrides
            .Where(o => o.FolderId == folderId)
            .Include(o => o.TargetUser)
            .Include(o => o.TargetGroup)
            .OrderBy(o => o.CreatedAt)
            .Select(o => new OverrideDto(o.Id, o.FolderId, o.TargetUserId, o.TargetUser!.DisplayName,
                o.TargetGroupId, o.TargetGroup!.Name, o.MaxPermission.ToString(), o.CreatedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if ((req.UserId is null) == (req.GroupId is null))
            return Problem(statusCode: 422, title: "Exactly one of UserId or GroupId is required.");
        var folder = await _db.Folders.FindAsync(new object[] { req.FolderId }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct) && me.Role != UserRole.Admin) return Forbid();

        // Idempotent — if the same (folder, target) already has an override,
        // just return it.
        var existing = await _db.FolderAccessOverrides.FirstOrDefaultAsync(o =>
            o.FolderId == req.FolderId && o.TargetUserId == req.UserId && o.TargetGroupId == req.GroupId, ct);
        if (existing is not null) return Ok(new { id = existing.Id });

        var o = new FolderAccessOverride
        {
            FolderId = req.FolderId,
            TargetUserId = req.UserId,
            TargetGroupId = req.GroupId,
            MaxPermission = DirectSharePermission.Read,
            CreatedByUserId = me.Id,
        };
        _db.FolderAccessOverrides.Add(o);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = o.Id });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var o = await _db.FolderAccessOverrides.FindAsync(new object[] { id }, ct);
        if (o is null) return NotFound();
        var folder = await _db.Folders.FindAsync(new object[] { o.FolderId }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct) && me.Role != UserRole.Admin) return Forbid();
        _db.FolderAccessOverrides.Remove(o);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
