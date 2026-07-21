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

    /// <summary>
    /// v1.10.104 — Variante mit einem vorbereiteten Set „vor dem Nutzer
    /// versteckter Ordner-IDs" (Public-Scope + Ordner ist privat + kein
    /// Grant). Nutz die für Bulk-Reads: Suche, RAG-Chat, Aktivität, damit
    /// Files in privaten Public-Ordnern nicht durchsickern. Für einfache
    /// Aufrufe kann der Overload ohne Set verwendet werden (kein Filter).
    /// </summary>
    IQueryable<StorageFile> ApplyReadFilter(IQueryable<StorageFile> q, User user, HashSet<Guid>? hiddenPublicFolderIds);

    /// <summary>
    /// v1.10.104 — Liefert alle Folder-IDs im Public-Scope, die für den
    /// User verborgen sind, weil sie (oder ein Vorfahre) IsPrivate=true
    /// gesetzt haben und der User weder Ersteller noch per DirectShare
    /// berechtigt ist. Admin bekommt immer ein leeres Set.
    /// </summary>
    Task<HashSet<Guid>> GetHiddenPublicFolderIdsAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// v1.10.104 — True, wenn dieser Ordner selbst oder ein Vorfahre
    /// IsPrivate ist und der User weder Ersteller noch DirectShare-berechtigt
    /// ist. Immer false für Nicht-Public-Scopes und für Admins.
    /// </summary>
    Task<bool> IsFolderHiddenByPrivacyAsync(User user, Folder folder, CancellationToken ct = default);

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
        => ApplyReadFilter(q, user, hiddenPublicFolderIds: null);

    public IQueryable<StorageFile> ApplyReadFilter(IQueryable<StorageFile> q, User user, HashSet<Guid>? hiddenPublicFolderIds)
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

        var publicReadable = user.Role == UserRole.Admin || user.PublicCanRead;
        // v1.10.104: Public + Private-Ordner unsichtbar für Nutzer ohne
        // Grant. Der Guard wirkt zusätzlich zur Direct-Share-Beziehung —
        // ein DirectShare auf die Datei oder den Ordner selbst hebt den
        // Privacy-Filter nicht auf; wer den Private-Root nicht sehen darf,
        // sieht auch Files darin nicht via Bulk-Reads.
        var hidden = hiddenPublicFolderIds;
        return q.Where(f =>
            (f.Scope == FileScope.Personal && f.OwnerId == user.Id) ||
            (f.Scope == FileScope.Public && publicReadable
                && (hidden == null || f.FolderId == null || !hidden.Contains(f.FolderId.Value))) ||
            (f.Scope == FileScope.Group && f.GroupId != null && myGroups.Contains(f.GroupId.Value)) ||
            directFileIds.Contains(f.Id) ||
            (f.FolderId != null && directFolderIds.Contains(f.FolderId.Value)));
    }

    public async Task<HashSet<Guid>> GetHiddenPublicFolderIdsAsync(User user, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return new HashSet<Guid>();

        // 1) Alle Private-Roots im Public-Scope
        var privateRoots = await _db.Folders
            .Where(f => f.Scope == FileScope.Public && f.IsPrivate)
            .Select(f => new { f.Id, f.CreatedByUserId })
            .ToListAsync(ct);
        if (privateRoots.Count == 0) return new HashSet<Guid>();

        // 2) Grants: hat der User (via self oder Group) einen DirectShare?
        var myGroupIds = await _db.GroupMemberships
            .Where(m => m.UserId == user.Id)
            .Select(m => m.GroupId)
            .ToListAsync(ct);
        var rootIds = privateRoots.Select(r => r.Id).ToList();
        var grantedFolderIds = await _db.DirectShares
            .Where(s => s.FolderId != null && rootIds.Contains(s.FolderId!.Value)
                && (s.TargetUserId == user.Id
                    || (s.TargetGroupId != null && myGroupIds.Contains(s.TargetGroupId!.Value))))
            .Select(s => s.FolderId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var grantedSet = new HashSet<Guid>(grantedFolderIds);

        // 3) Für den User inaccessible: Private-Root ohne Grant, dessen
        //    Ersteller er nicht ist. (Ersteller sieht seinen eigenen
        //    Private-Ordner immer — sonst könnte niemand den ACL setzen.)
        var inaccessible = privateRoots
            .Where(r => r.CreatedByUserId != user.Id && !grantedSet.Contains(r.Id))
            .Select(r => r.Id)
            .ToHashSet();
        if (inaccessible.Count == 0) return new HashSet<Guid>();

        // 4) BFS über Descendants — jedes Level in einer Query, damit wir
        //    grosse Public-Bäume nicht komplett laden müssen.
        var hidden = new HashSet<Guid>(inaccessible);
        var frontier = inaccessible.ToList();
        var safety = 64;
        while (frontier.Count > 0 && safety-- > 0)
        {
            var parents = frontier;
            var children = await _db.Folders
                .Where(f => f.ParentFolderId != null && parents.Contains(f.ParentFolderId!.Value))
                .Select(f => f.Id)
                .ToListAsync(ct);
            frontier = children.Where(id => hidden.Add(id)).ToList();
        }
        return hidden;
    }

    public async Task<bool> IsFolderHiddenByPrivacyAsync(User user, Folder folder, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return false;
        if (folder.Scope != FileScope.Public) return false;

        // Walk ancestor chain (incl. this folder) und finde den ersten
        // Private-Root. Wenn keiner → nicht hidden.
        var visited = new HashSet<Guid>();
        Folder? cursor = folder;
        var safety = 64;
        while (cursor != null && visited.Add(cursor.Id) && safety-- > 0)
        {
            if (cursor.IsPrivate)
            {
                if (cursor.CreatedByUserId == user.Id) return false;
                // Grant-Check am Private-Root
                var privateRootId = cursor.Id;
                var myGroupIds = _db.GroupMemberships
                    .Where(m => m.UserId == user.Id)
                    .Select(m => m.GroupId);
                var hasGrant = await _db.DirectShares.AnyAsync(s =>
                    s.FolderId == privateRootId
                    && (s.TargetUserId == user.Id
                        || (s.TargetGroupId != null && myGroupIds.Contains(s.TargetGroupId!.Value))),
                    ct);
                return !hasGrant;
            }
            if (cursor.ParentFolderId is not Guid pid) break;
            cursor = await _db.Folders.FindAsync(new object[] { pid }, ct);
        }
        return false;
    }

    public async Task<bool> CanReadAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        var byScope = file.Scope switch
        {
            FileScope.Personal => file.OwnerId == user.Id,
            FileScope.Public => user.PublicCanRead && !await IsFileInHiddenPrivateFolderAsync(user, file, ct),
            FileScope.Group => file.GroupId is Guid g && await IsGroupMemberAsync(user, g, ct),
            _ => false
        };
        if (byScope) return true;
        return await HasDirectShareAsync(user, file.Id, DirectSharePermission.Read, ct);
    }

    /// <summary>
    /// v1.10.104 — Hilfsmethode: liegt die Datei in einem Public-Ordner,
    /// dessen Private-Root für den User verborgen ist? Wenn ja, blockiert
    /// die Scope-Berechtigung „Public"; DirectShare kann trotzdem greifen.
    /// </summary>
    private async Task<bool> IsFileInHiddenPrivateFolderAsync(User user, StorageFile file, CancellationToken ct)
    {
        if (file.FolderId is not Guid fid) return false;
        var folder = await _db.Folders.FindAsync(new object[] { fid }, ct);
        if (folder is null) return false;
        return await IsFolderHiddenByPrivacyAsync(user, folder, ct);
    }

    public async Task<bool> CanUploadIntoAsync(User user, FileScope scope, Guid? groupId, CancellationToken ct = default)
    {
        return scope switch
        {
            FileScope.Personal => true,
            FileScope.Public => user.PublicCanWrite || user.Role == UserRole.Admin,
            FileScope.Group => groupId is Guid g && await IsGroupMemberAsync(user, g, ct),
            _ => false
        };
    }

    public async Task<bool> CanDeleteAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        // A live lock held by someone else blocks writes for everyone but the
        // owner (they can always break). Admins bypass the whole check anyway.
        if (file.LockedUntil is DateTimeOffset until && until > DateTimeOffset.UtcNow
            && file.LockedByUserId != user.Id
            && user.Role != UserRole.Admin && file.OwnerId != user.Id)
        {
            return false;
        }
        if (user.Role == UserRole.Admin) return true;
        if (file.OwnerId == user.Id) return true;
        if (file.Scope == FileScope.Public && user.PublicCanDelete) return true;
        if (file.Scope == FileScope.Group && file.GroupId is Guid g)
        {
            if (await IsGroupManagerAsync(user, g, ct))
            {
                // Even the group manager loses write when a sub-folder
                // override caps them. Admin explicitly bypasses (handled above).
                if (file.FolderId is Guid fid && await IsSubtreeReadOnlyForAsync(fid, user, ct))
                    return false;
                return true;
            }
        }
        // Write-grant via direct share also permits deletion.
        if (!await HasDirectShareAsync(user, file.Id, DirectSharePermission.Write, ct)) return false;
        if (file.FolderId is Guid fid2 && await IsSubtreeReadOnlyForAsync(fid2, user, ct)) return false;
        return true;
    }

    private async Task<bool> IsSubtreeReadOnlyForAsync(Guid folderId, User user, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? cursor = folderId;
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
        if (folder.Scope == FileScope.Public && (user.Role == UserRole.Admin || user.PublicCanRead))
            return user.PublicCanWrite ? DirectSharePermission.Write : DirectSharePermission.Read;
        if (folder.Scope == FileScope.Group && folder.OwnerGroupId is Guid gm && await IsGroupMemberAsync(user, gm, ct))
            return DirectSharePermission.Read;
        return haveRead ? DirectSharePermission.Read : null;
    }
}
