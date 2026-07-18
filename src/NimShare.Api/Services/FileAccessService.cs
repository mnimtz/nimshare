using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Central place for "who can do what to which file" — used by every
/// controller that touches a StorageFile, ShareLink, or upload endpoint.
///
/// Access rules:
///   Personal → only the OwnerId user (Admins can read/delete too).
///   Public   → everyone signed in can read + upload; only the uploader or an
///              Admin can delete.
///   Group    → all group members can read + upload; the uploader, group
///              Managers, and Admins can delete.
///   DirectShare → grants Read or Write to a specific user or a whole group,
///              on either a single file or a whole folder subtree. Read-write
///              grants make the target a full participant (delete/rename ok);
///              read-only doesn't.
/// </summary>
public interface IFileAccessService
{
    IQueryable<StorageFile> ApplyReadFilter(IQueryable<StorageFile> q, User user);

    Task<bool> CanReadAsync(User user, StorageFile file, CancellationToken ct = default);
    Task<bool> CanUploadIntoAsync(User user, FileScope scope, Guid? groupId, CancellationToken ct = default);
    Task<bool> CanDeleteAsync(User user, StorageFile file, CancellationToken ct = default);
    Task<bool> CanShareAsync(User user, StorageFile file, CancellationToken ct = default);

    Task<List<GroupSummary>> ListMyGroupsAsync(User user, CancellationToken ct = default);
    Task<bool> IsGroupManagerAsync(User user, Guid groupId, CancellationToken ct = default);
    Task<bool> IsGroupMemberAsync(User user, Guid groupId, CancellationToken ct = default);

    /// <summary>True if the file has ANY grant to the given user via direct-share (self or group).</summary>
    Task<bool> HasDirectShareAsync(User user, Guid fileId, DirectSharePermission minLevel, CancellationToken ct = default);

    Task<DirectSharePermission?> EffectivePermissionOnFileAsync(User user, StorageFile file, CancellationToken ct = default);
    Task<DirectSharePermission?> EffectivePermissionOnFolderAsync(User user, Folder folder, CancellationToken ct = default);
}

public record GroupSummary(Guid Id, string Name, GroupRole MyRole);

public class FileAccessService : IFileAccessService
{
    private readonly NimShareDbContext _db;

    public FileAccessService(NimShareDbContext db) => _db = db;

    public IQueryable<StorageFile> ApplyReadFilter(IQueryable<StorageFile> q, User user)
    {
        if (user.Role == UserRole.Admin) return q;
        var myGroups = _db.GroupMemberships.Where(m => m.UserId == user.Id).Select(m => m.GroupId);

        // Files reached by direct share of the file OR any ancestor folder in
        // the file's chain. For the ancestor case we walk parents client-side
        // in the sharing controller; here we just widen the read set with any
        // file-level or folder-level grant hitting the user directly or via a
        // group they belong to.
        var directFileIds = _db.DirectShares
            .Where(s => s.FileId != null && (s.TargetUserId == user.Id || (s.TargetGroupId != null && myGroups.Contains(s.TargetGroupId.Value))))
            .Select(s => s.FileId!.Value);
        var directFolderIds = _db.DirectShares
            .Where(s => s.FolderId != null && (s.TargetUserId == user.Id || (s.TargetGroupId != null && myGroups.Contains(s.TargetGroupId.Value))))
            .Select(s => s.FolderId!.Value);

        return q.Where(f =>
            (f.Scope == FileScope.Personal && f.OwnerId == user.Id) ||
            (f.Scope == FileScope.Public) ||
            (f.Scope == FileScope.Group && f.GroupId != null && myGroups.Contains(f.GroupId.Value)) ||
            directFileIds.Contains(f.Id) ||
            (f.FolderId != null && directFolderIds.Contains(f.FolderId.Value)));
    }

    public async Task<bool> CanReadAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        var byScope = file.Scope switch
        {
            FileScope.Personal => file.OwnerId == user.Id,
            FileScope.Public => true,
            FileScope.Group => file.GroupId is Guid g && await IsGroupMemberAsync(user, g, ct),
            _ => false
        };
        if (byScope) return true;
        return await HasDirectShareAsync(user, file.Id, DirectSharePermission.Read, ct);
    }

    public async Task<bool> CanUploadIntoAsync(User user, FileScope scope, Guid? groupId, CancellationToken ct = default)
    {
        return scope switch
        {
            FileScope.Personal => true,
            FileScope.Public => true,
            FileScope.Group => groupId is Guid g && await IsGroupMemberAsync(user, g, ct),
            _ => false
        };
    }

    public async Task<bool> CanDeleteAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        if (file.OwnerId == user.Id) return true;
        if (file.Scope == FileScope.Group && file.GroupId is Guid g)
        {
            if (await IsGroupManagerAsync(user, g, ct)) return true;
        }
        // Write-grant via direct share also permits deletion.
        return await HasDirectShareAsync(user, file.Id, DirectSharePermission.Write, ct);
    }

    public async Task<bool> CanShareAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        // If you can read a file, you can share a public link for it — the link
        // itself carries its own rules (password/expiry/etc.). Direct-share
        // grants also satisfy this, since read implies "you can hand out a link".
        return await CanReadAsync(user, file, ct);
    }

    public Task<List<GroupSummary>> ListMyGroupsAsync(User user, CancellationToken ct = default) =>
        _db.GroupMemberships
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.Group.Name)
            .Select(m => new GroupSummary(m.GroupId, m.Group.Name, m.Role))
            .ToListAsync(ct);

    public Task<bool> IsGroupMemberAsync(User user, Guid groupId, CancellationToken ct = default) =>
        _db.GroupMemberships.AnyAsync(m => m.GroupId == groupId && m.UserId == user.Id, ct);

    public Task<bool> IsGroupManagerAsync(User user, Guid groupId, CancellationToken ct = default) =>
        _db.GroupMemberships.AnyAsync(m => m.GroupId == groupId && m.UserId == user.Id && m.Role == GroupRole.Manager, ct);

    public async Task<bool> HasDirectShareAsync(User user, Guid fileId, DirectSharePermission minLevel, CancellationToken ct = default)
    {
        // The file's own file-level grants…
        var byFile = await _db.DirectShares
            .Where(s => s.FileId == fileId
                && s.Permission >= minLevel
                && (s.TargetUserId == user.Id
                    || (s.TargetGroupId != null && _db.GroupMemberships.Any(m => m.UserId == user.Id && m.GroupId == s.TargetGroupId))))
            .AnyAsync(ct);
        if (byFile) return true;

        // …or any grant that hits a folder in the file's ancestor chain.
        var f = await _db.Files.FindAsync(new object[] { fileId }, ct);
        if (f?.FolderId is null) return false;
        var folderId = f.FolderId;
        var visited = new HashSet<Guid>();
        var maxDepth = 64;
        while (folderId is Guid fid && visited.Add(fid) && visited.Count <= maxDepth)
        {
            var byFolder = await _db.DirectShares
                .Where(s => s.FolderId == fid
                    && s.Permission >= minLevel
                    && (s.TargetUserId == user.Id
                        || (s.TargetGroupId != null && _db.GroupMemberships.Any(m => m.UserId == user.Id && m.GroupId == s.TargetGroupId))))
                .AnyAsync(ct);
            if (byFolder) return true;
            var parent = await _db.Folders.Where(x => x.Id == fid).Select(x => x.ParentFolderId).FirstOrDefaultAsync(ct);
            folderId = parent;
        }
        return false;
    }

    /// <summary>
    /// The strongest permission the caller has on a file, considering both
    /// scope-based access and direct-share grants. Used to prevent grant
    /// escalation ("you may not grant more than you have").
    /// </summary>
    public async Task<DirectSharePermission?> EffectivePermissionOnFileAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return DirectSharePermission.Write;
        if (file.OwnerId == user.Id) return DirectSharePermission.Write;
        if (file.Scope == FileScope.Group && file.GroupId is Guid g && await IsGroupManagerAsync(user, g, ct))
            return DirectSharePermission.Write;
        if (await HasDirectShareAsync(user, file.Id, DirectSharePermission.Write, ct))
            return DirectSharePermission.Write;
        if (await CanReadAsync(user, file, ct))
            return DirectSharePermission.Read;
        return null;
    }

    /// <summary>Same for a folder — walks the ancestor chain for direct grants.</summary>
    public async Task<DirectSharePermission?> EffectivePermissionOnFolderAsync(User user, Folder folder, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return DirectSharePermission.Write;
        if (folder.OwnerUserId == user.Id) return DirectSharePermission.Write;
        if (folder.Scope == FileScope.Group && folder.OwnerGroupId is Guid g && await IsGroupManagerAsync(user, g, ct))
            return DirectSharePermission.Write;

        // Walk up looking for any grant that gives Write; along the way remember
        // if we saw at least a Read anywhere. Same cycle guard as HasDirectShare.
        var visited = new HashSet<Guid>();
        Guid? cursor = folder.Id;
        var haveRead = false;
        var maxDepth = 64;
        while (cursor is Guid fid && visited.Add(fid) && visited.Count <= maxDepth)
        {
            var grants = await _db.DirectShares
                .Where(s => s.FolderId == fid
                    && (s.TargetUserId == user.Id
                        || (s.TargetGroupId != null && _db.GroupMemberships.Any(m => m.UserId == user.Id && m.GroupId == s.TargetGroupId))))
                .Select(s => s.Permission)
                .ToListAsync(ct);
            if (grants.Contains(DirectSharePermission.Write)) return DirectSharePermission.Write;
            if (grants.Contains(DirectSharePermission.Read)) haveRead = true;
            var parent = await _db.Folders.Where(x => x.Id == fid).Select(x => x.ParentFolderId).FirstOrDefaultAsync(ct);
            cursor = parent;
        }
        // Fall back to scope-based read (group member = read).
        if (folder.Scope == FileScope.Public) return DirectSharePermission.Read;
        if (folder.Scope == FileScope.Group && folder.OwnerGroupId is Guid gm && await IsGroupMemberAsync(user, gm, ct))
            return DirectSharePermission.Read;
        return haveRead ? DirectSharePermission.Read : null;
    }
}
