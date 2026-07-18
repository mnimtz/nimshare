namespace NimShare.Core.Entities;

/// <summary>
/// Permission granted by a Direct-Share (share to a specific user or group).
/// Read = view + download. Write = Read + upload + rename + delete + reshare.
/// Manage isn't modeled — that's reserved for the file/folder owner and admins.
/// </summary>
public enum DirectSharePermission
{
    Read = 0,
    Write = 1,
}

/// <summary>
/// A grant that gives one signed-in user (or a whole group) access to a
/// specific file or folder that isn't in their scope. Complements ShareLink
/// (which is for anonymous external audiences).
///
/// Exactly one of {FileId, FolderId} and one of {TargetUserId, TargetGroupId}
/// must be non-null. That's a data invariant, enforced by the API layer.
/// </summary>
public class DirectShare
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? FileId { get; set; }
    public StorageFile? File { get; set; }

    public Guid? FolderId { get; set; }
    public Folder? Folder { get; set; }

    public Guid? TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    public Guid? TargetGroupId { get; set; }
    public Group? TargetGroup { get; set; }

    public DirectSharePermission Permission { get; set; } = DirectSharePermission.Read;

    public Guid SharedByUserId { get; set; }
    public User? SharedByUser { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
