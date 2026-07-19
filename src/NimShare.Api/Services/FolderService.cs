using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public interface IFolderService
{
    /// <summary>Returns (or lazily creates) the root folder for a scope. For groups, groupId is required.</summary>
    Task<Folder> GetOrCreateRootAsync(FileScope scope, Guid? ownerUserId, Guid? ownerGroupId, User createdBy, CancellationToken ct = default);

    /// <summary>Walks a slash-separated path (e.g. "Projects/Q3") from a root folder.</summary>
    Task<Folder?> ResolvePathAsync(Folder root, string[] segments, CancellationToken ct = default);

    Task<Folder> CreateChildAsync(Folder parent, string name, User createdBy, CancellationToken ct = default);
    Task RenameAsync(Folder folder, string newName, CancellationToken ct = default);
    Task DeleteAsync(Folder folder, CancellationToken ct = default);

    Task<List<Folder>> ListSubfoldersAsync(Folder parent, CancellationToken ct = default);
    Task<List<StorageFile>> ListFilesAsync(Folder parent, CancellationToken ct = default);

    Task<bool> CanReadAsync(Folder folder, User user, CancellationToken ct = default);
    Task<bool> CanWriteAsync(Folder folder, User user, CancellationToken ct = default);
    Task<bool> CanManageAsync(Folder folder, User user, CancellationToken ct = default);

    /// <summary>True if this folder subtree has a Read-max override that would
    /// downgrade the caller's otherwise-granted write access. Used by the
    /// FilesController.Delete/Rename/Move path to also honour restrictions.</summary>
    Task<bool> IsFolderReadOnlyForAsync(Guid folderId, User user, CancellationToken ct = default);

    /// <summary>Returns the folder's ancestors from root → folder itself (inclusive), for breadcrumbs.</summary>
    Task<List<Folder>> GetAncestryAsync(Folder folder, CancellationToken ct = default);
}

public class FolderService : IFolderService
{
    private readonly NimShareDbContext _db;
    private readonly IFileAccessService _access;

    public FolderService(NimShareDbContext db, IFileAccessService access)
    {
        _db = db;
        _access = access;
    }

    public NimShareDbContext DbContext => _db;

    public async Task<Folder> GetOrCreateRootAsync(FileScope scope, Guid? ownerUserId, Guid? ownerGroupId, User createdBy, CancellationToken ct = default)
    {
        var q = _db.Folders.Where(f => f.ParentFolderId == null && f.Scope == scope);
        q = scope switch
        {
            FileScope.Personal => q.Where(f => f.OwnerUserId == ownerUserId),
            FileScope.Group => q.Where(f => f.OwnerGroupId == ownerGroupId),
            FileScope.Public => q.Where(f => f.OwnerUserId == null && f.OwnerGroupId == null),
            _ => q,
        };
        var root = await q.SingleOrDefaultAsync(ct);
        if (root is not null) return root;

        root = new Folder
        {
            Name = scope switch
            {
                FileScope.Personal => "My files",
                FileScope.Public => "Public",
                FileScope.Group => "Group",
                _ => "Root",
            },
            Scope = scope,
            OwnerUserId = scope == FileScope.Personal ? ownerUserId : null,
            OwnerGroupId = scope == FileScope.Group ? ownerGroupId : null,
            ParentFolderId = null,
            CreatedByUserId = createdBy.Id,
        };
        _db.Folders.Add(root);
        await _db.SaveChangesAsync(ct);
        return root;
    }

    public async Task<Folder?> ResolvePathAsync(Folder root, string[] segments, CancellationToken ct = default)
    {
        var current = root;
        foreach (var raw in segments)
        {
            var seg = Uri.UnescapeDataString(raw ?? "").Trim();
            if (string.IsNullOrEmpty(seg)) continue;
            var currentId = current.Id;
            var next = await _db.Folders
                .SingleOrDefaultAsync(f => f.ParentFolderId == currentId && f.Name == seg, ct);
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    public async Task<Folder> CreateChildAsync(Folder parent, string name, User createdBy, CancellationToken ct = default)
    {
        name = SanitiseName(name);
        var parentId = parent.Id;
        if (await _db.Folders.AnyAsync(f => f.ParentFolderId == parentId && f.Name == name, ct))
            throw new InvalidOperationException("A folder with that name already exists here.");
        var child = new Folder
        {
            Name = name,
            ParentFolderId = parent.Id,
            Scope = parent.Scope,
            OwnerUserId = parent.OwnerUserId,
            OwnerGroupId = parent.OwnerGroupId,
            CreatedByUserId = createdBy.Id,
        };
        _db.Folders.Add(child);
        await _db.SaveChangesAsync(ct);
        return child;
    }

    public async Task RenameAsync(Folder folder, string newName, CancellationToken ct = default)
    {
        if (folder.ParentFolderId is null) throw new InvalidOperationException("Cannot rename a root folder.");
        newName = SanitiseName(newName);
        var parentId = folder.ParentFolderId!.Value;
        var folderId = folder.Id;
        if (await _db.Folders.AnyAsync(f => f.ParentFolderId == parentId && f.Id != folderId && f.Name == newName, ct))
            throw new InvalidOperationException("A sibling with that name already exists.");
        folder.Name = newName;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Folder folder, CancellationToken ct = default)
    {
        if (folder.ParentFolderId is null) throw new InvalidOperationException("Cannot delete a scope root.");
        var folderId = folder.Id;
        var hasChildren = await _db.Folders.AnyAsync(f => f.ParentFolderId == folderId, ct);
        var hasFiles = await _db.Files.AnyAsync(f => f.FolderId == folderId && f.Status != StorageFileStatus.Deleted, ct);
        if (hasChildren || hasFiles)
            throw new InvalidOperationException("Folder is not empty. Move or delete its contents first.");
        _db.Folders.Remove(folder);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<Folder>> ListSubfoldersAsync(Folder parent, CancellationToken ct = default) =>
        _db.Folders.Where(f => f.ParentFolderId == parent.Id).OrderBy(f => f.Name).ToListAsync(ct);

    public Task<List<StorageFile>> ListFilesAsync(Folder parent, CancellationToken ct = default) =>
        _db.Files
            .Include(f => f.Owner)
            .Where(f => f.FolderId == parent.Id && f.Status == StorageFileStatus.Ready)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);

    public async Task<bool> CanReadAsync(Folder folder, User user, CancellationToken ct = default)
    {
        return folder.Scope switch
        {
            FileScope.Personal => folder.OwnerUserId == user.Id || user.Role == UserRole.Admin,
            FileScope.Public => user.Role == UserRole.Admin || user.PublicCanRead,
            FileScope.Group => folder.OwnerGroupId is Guid g
                && (user.Role == UserRole.Admin || await _access.IsGroupMemberAsync(user, g, ct)),
            _ => false,
        };
    }

    public async Task<bool> CanWriteAsync(Folder folder, User user, CancellationToken ct = default)
    {
        var baseGrant = folder.Scope switch
        {
            FileScope.Personal => folder.OwnerUserId == user.Id || user.Role == UserRole.Admin,
            FileScope.Public => user.Role == UserRole.Admin || user.PublicCanWrite,
            FileScope.Group => folder.OwnerGroupId is Guid g && await _access.IsGroupMemberAsync(user, g, ct),
            _ => false,
        };
        if (!baseGrant) return false;

        // Sub-folder permission override: if the caller (or any of their groups)
        // has a Read-max cap on this folder or any of its ancestors, they lose
        // Write. Admins skip the check by design — they need a full override.
        if (user.Role == UserRole.Admin) return true;
        return !await HasReadOnlyOverrideAsync(folder, user, ct);
    }

    public async Task<bool> CanManageAsync(Folder folder, User user, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        var baseGrant = folder.Scope switch
        {
            FileScope.Personal => folder.OwnerUserId == user.Id,
            FileScope.Public => false, // only admins manage public roots
            FileScope.Group => folder.OwnerGroupId is Guid g && await _access.IsGroupManagerAsync(user, g, ct),
            _ => false,
        };
        if (!baseGrant) return false;
        return !await HasReadOnlyOverrideAsync(folder, user, ct);
    }

    public async Task<bool> IsFolderReadOnlyForAsync(Guid folderId, User user, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return false;
        var folder = await _db.Folders.FindAsync(new object[] { folderId }, ct);
        if (folder is null) return false;
        return await HasReadOnlyOverrideAsync(folder, user, ct);
    }

    /// <summary>
    /// Walk the ancestor chain and return true if any FolderAccessOverride with
    /// MaxPermission=Read matches either the caller directly or one of their
    /// groups. Cycle-guarded via visited-set, capped at 64 levels.
    /// </summary>
    private async Task<bool> HasReadOnlyOverrideAsync(Folder folder, User user, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? cursor = folder.Id;
        while (cursor is Guid fid && visited.Add(fid) && visited.Count <= 64)
        {
            var hasHere = await _db.FolderAccessOverrides.AnyAsync(o =>
                o.FolderId == fid
                && o.MaxPermission == DirectSharePermission.Read
                && (o.TargetUserId == user.Id
                    || (o.TargetGroupId != null && _db.GroupMemberships.Any(m => m.UserId == user.Id && m.GroupId == o.TargetGroupId))),
                ct);
            if (hasHere) return true;
            cursor = await _db.Folders.Where(x => x.Id == fid).Select(x => x.ParentFolderId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    public async Task<List<Folder>> GetAncestryAsync(Folder folder, CancellationToken ct = default)
    {
        var chain = new List<Folder> { folder };
        var current = folder;
        while (current.ParentFolderId is Guid pid)
        {
            var parent = await _db.Folders.FindAsync(new object[] { pid }, ct);
            if (parent is null) break;
            chain.Insert(0, parent);
            current = parent;
        }
        return chain;
    }

    private static string SanitiseName(string s)
    {
        s = (s ?? "").Trim();
        if (string.IsNullOrEmpty(s)) throw new ArgumentException("Folder name is required.");
        foreach (var bad in new[] { '/', '\\', '\0', '\r', '\n', ':', '*', '?', '"', '<', '>', '|' })
            s = s.Replace(bad.ToString(), "");
        if (s.Length > 255) s = s[..255];
        if (string.IsNullOrEmpty(s)) throw new ArgumentException("Folder name is required.");
        return s;
    }
}
