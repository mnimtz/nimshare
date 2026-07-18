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
/// </summary>
public interface IFileAccessService
{
    /// <summary>The set of scope filters the caller may see, expressed as an IQueryable filter.</summary>
    IQueryable<StorageFile> ApplyReadFilter(IQueryable<StorageFile> q, User user);

    Task<bool> CanReadAsync(User user, StorageFile file, CancellationToken ct = default);
    Task<bool> CanUploadIntoAsync(User user, FileScope scope, Guid? groupId, CancellationToken ct = default);
    Task<bool> CanDeleteAsync(User user, StorageFile file, CancellationToken ct = default);
    Task<bool> CanShareAsync(User user, StorageFile file, CancellationToken ct = default);

    /// <summary>The groups the caller is a member of, projected minimally for menus/pickers.</summary>
    Task<List<GroupSummary>> ListMyGroupsAsync(User user, CancellationToken ct = default);

    Task<bool> IsGroupManagerAsync(User user, Guid groupId, CancellationToken ct = default);
    Task<bool> IsGroupMemberAsync(User user, Guid groupId, CancellationToken ct = default);
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
        return q.Where(f =>
            (f.Scope == FileScope.Personal && f.OwnerId == user.Id) ||
            (f.Scope == FileScope.Public) ||
            (f.Scope == FileScope.Group && f.GroupId != null && myGroups.Contains(f.GroupId.Value)));
    }

    public async Task<bool> CanReadAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        return file.Scope switch
        {
            FileScope.Personal => file.OwnerId == user.Id,
            FileScope.Public => true,
            FileScope.Group => file.GroupId is Guid g && await IsGroupMemberAsync(user, g, ct),
            _ => false
        };
    }

    public async Task<bool> CanUploadIntoAsync(User user, FileScope scope, Guid? groupId, CancellationToken ct = default)
    {
        return scope switch
        {
            FileScope.Personal => true,   // upload into your own space
            FileScope.Public => true,     // signed-in users may seed the public bucket
            FileScope.Group => groupId is Guid g && await IsGroupMemberAsync(user, g, ct),
            _ => false
        };
    }

    public async Task<bool> CanDeleteAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Admin) return true;
        if (file.OwnerId == user.Id) return true;
        if (file.Scope == FileScope.Group && file.GroupId is Guid g)
            return await IsGroupManagerAsync(user, g, ct);
        return false;
    }

    public async Task<bool> CanShareAsync(User user, StorageFile file, CancellationToken ct = default)
    {
        // If you can read a file, you can share a public link for it — the link
        // itself carries its own rules (password/expiry/etc.).
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
}
