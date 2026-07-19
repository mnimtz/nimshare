namespace NimShare.Core.Entities;

/// <summary>
/// Downgrades a user's or group's access on a specific folder. Applies to the
/// folder itself and every descendant, and takes precedence over otherwise-
/// granted scope + direct-share write permission. If a full-write group
/// member has a Read-override on Group/Verträge, they can still read the
/// contents but not upload, rename, delete, or reshare.
///
/// The only meaningful current value is Read (i.e. "no write"). A None value
/// would be an outright deny — not modelled yet, since scope + direct-share
/// permissions cover the "you don't get in" case.
/// </summary>
public class FolderAccessOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FolderId { get; set; }
    public Folder? Folder { get; set; }

    /// <summary>Set for a per-user restriction; null when the whole group is downgraded.</summary>
    public Guid? TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    public Guid? TargetGroupId { get; set; }
    public Group? TargetGroup { get; set; }

    /// <summary>
    /// The maximum permission the target may exercise on this folder subtree.
    /// Read = no write, no delete, no reshare, no rename, no upload.
    /// </summary>
    public DirectSharePermission MaxPermission { get; set; } = DirectSharePermission.Read;

    public Guid CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
