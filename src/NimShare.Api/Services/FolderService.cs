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
    Task DeleteAsync(Folder folder, bool cascade, CancellationToken ct = default);

    Task<List<Folder>> ListSubfoldersAsync(Folder parent, CancellationToken ct = default);
    Task<List<StorageFile>> ListFilesAsync(Folder parent, CancellationToken ct = default);

    /// <summary>
    /// v1.10.104 — filtert eine bereits geladene Unterordner-Liste um
    /// alle Public-Ordner, die für den User via IsPrivate + fehlendem
    /// Grant verborgen sind. Personal + Group werden unverändert
    /// durchgereicht.
    /// </summary>
    Task<List<Folder>> FilterVisibleSubfoldersAsync(List<Folder> subs, User user, CancellationToken ct = default);

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
        // v1.10.37: SingleOrDefaultAsync warf, wenn irgendwann in der DB-Historie
        // ein zweiter Root pro (Scope, Owner) landete (Race-Condition beim
        // ersten Erst-Anlegen, Import, Migration). FirstOrDefault mit stabiler
        // Sortierung nimmt den ältesten — konsistent mit dem, was der Tree-
        // Endpoint als "offiziellen" Root anzeigt.
        var root = await q.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).FirstOrDefaultAsync(ct);
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
        await DeleteAsync(folder, cascade: false, ct);
    }

    /// <summary>
    /// v1.10.96: cascade-Modus für Marcus's Bug „Löschen verweigert bei
    /// Inhalt statt nachzufragen". Ohne cascade: default-Verhalten (throw
    /// wenn nicht leer). Mit cascade: alle Files soft-deleten (→ Trash),
    /// Subfolders rekursiv löschen. Blobs bleiben — Trash-Rescue über
    /// die normale Undelete-Route möglich.
    /// </summary>
    public async Task DeleteAsync(Folder folder, bool cascade, CancellationToken ct = default)
    {
        if (folder.ParentFolderId is null) throw new InvalidOperationException("Cannot delete a scope root.");
        var folderId = folder.Id;
        var hasChildren = await _db.Folders.AnyAsync(f => f.ParentFolderId == folderId, ct);
        var hasFiles = await _db.Files.AnyAsync(f => f.FolderId == folderId && f.Status != StorageFileStatus.Deleted, ct);
        if ((hasChildren || hasFiles) && !cascade)
            throw new InvalidOperationException("Folder is not empty. Move or delete its contents first.");
        if (cascade)
        {
            // Files → Trash (soft-delete)
            var files = await _db.Files
                .Where(f => f.FolderId == folderId && f.Status != StorageFileStatus.Deleted)
                .ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var f in files)
            {
                f.Status = StorageFileStatus.Deleted;
                f.DeletedAt = now;
            }
            // Subfolders rekursiv
            var subs = await _db.Folders.Where(f => f.ParentFolderId == folderId).ToListAsync(ct);
            foreach (var sub in subs)
                await DeleteAsync(sub, cascade: true, ct);
        }
        _db.Folders.Remove(folder);
        await _db.SaveChangesAsync(ct);
    }

    // Bewusst ungefiltert — Privacy-Filterung übernimmt der Caller via
    // FilterVisibleSubfoldersAsync (braucht den User-Kontext).
    public Task<List<Folder>> ListSubfoldersAsync(Folder parent, CancellationToken ct = default) =>
        _db.Folders
            .Where(f => f.ParentFolderId == parent.Id)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);

    public async Task<List<Folder>> FilterVisibleSubfoldersAsync(List<Folder> subs, User user, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin || subs.Count == 0) return subs;
        var visible = new List<Folder>(subs.Count);
        foreach (var f in subs)
        {
            if (f.Scope != FileScope.Public) { visible.Add(f); continue; }
            if (!await _access.IsFolderHiddenByPrivacyAsync(user, f, ct)) visible.Add(f);
        }
        return visible;
    }

    public Task<List<StorageFile>> ListFilesAsync(Folder parent, CancellationToken ct = default) =>
        _db.Files
            .Include(f => f.Owner)
            .Where(f => f.FolderId == parent.Id && f.Status == StorageFileStatus.Ready)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);

    public async Task<bool> CanReadAsync(Folder folder, User user, CancellationToken ct = default)
    {
        var baseGrant = folder.Scope switch
        {
            FileScope.Personal => folder.OwnerUserId == user.Id || user.Role == UserRole.Admin,
            FileScope.Public => user.Role == UserRole.Admin || user.PublicCanRead,
            FileScope.Group => folder.OwnerGroupId is Guid g
                && (user.Role == UserRole.Admin || await _access.IsGroupMemberAsync(user, g, ct)),
            _ => false,
        };
        if (!baseGrant) return false;
        // v1.10.104: Private-Ordner im Public-Scope brauchen zusätzlich
        // einen DirectShare-Grant (oder Ersteller/Admin).
        if (folder.Scope == FileScope.Public
            && await _access.IsFolderHiddenByPrivacyAsync(user, folder, ct)) return false;
        return true;
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

        // v1.10.104: Private-Ordner im Public-Scope: Grant muss existieren
        // (sonst siehst du den Ordner gar nicht → schreiben schon gar nicht).
        if (folder.Scope == FileScope.Public
            && await _access.IsFolderHiddenByPrivacyAsync(user, folder, ct)) return false;

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
            // v1.10.104: Ordner-Ersteller im Public-Scope darf seinen
            // Ordner managen. v1.10.108: zusätzlich jeder mit explizitem
            // Write-DirectShare auf dem Ordner oder einem Ahnen — sonst
            // ist Delegation unmöglich (Admin legt Ordner an, will die
            // Verwaltung einem User übergeben; Legacy-Ordner haben
            // CreatedByUserId == Guid.Empty und wären für immer Admin-only).
            // Bewusst NICHT PublicCanWrite — das würde jedem Schreib-User
            // Rename/Delete/Privacy auf fremden Ordnern geben.
            FileScope.Public => folder.CreatedByUserId == user.Id
                || await HasWriteGrantOnChainAsync(folder, user, ct),
            FileScope.Group => folder.OwnerGroupId is Guid g && await _access.IsGroupManagerAsync(user, g, ct),
            _ => false,
        };
        if (!baseGrant) return false;
        if (folder.Scope == FileScope.Public
            && await _access.IsFolderHiddenByPrivacyAsync(user, folder, ct)) return false;
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
    /// v1.10.108: True wenn der User einen expliziten Write-DirectShare auf
    /// dem Ordner oder einem seiner Ahnen hat (direkt oder via Gruppe).
    /// Grundlage der Manage-Delegation im Public-Scope.
    /// </summary>
    private async Task<bool> HasWriteGrantOnChainAsync(Folder folder, User user, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? cursor = folder.Id;
        while (cursor is Guid fid && visited.Add(fid) && visited.Count <= 64)
        {
            var hasWrite = await _db.DirectShares.AnyAsync(s =>
                s.FolderId == fid
                && s.Permission == DirectSharePermission.Write
                && (s.TargetUserId == user.Id
                    || (s.TargetGroupId != null && _db.GroupMemberships.Any(m => m.UserId == user.Id && m.GroupId == s.TargetGroupId))),
                ct);
            if (hasWrite) return true;
            cursor = await _db.Folders.Where(x => x.Id == fid).Select(x => x.ParentFolderId).FirstOrDefaultAsync(ct);
        }
        return false;
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
