namespace NimShare.Core.Entities;

/// <summary>
/// Structured verbs logged by controllers when something touches storage,
/// shares, or membership. Keep this a small closed set — one row per user
/// action is fine; per-view-hit is not.
/// </summary>
public enum ActivityKind
{
    FileUploaded = 1,
    FileDeleted = 2,
    FileRenamed = 3,
    FileMoved = 4,
    FolderCreated = 10,
    FolderDeleted = 11,
    FolderRenamed = 12,
    ShareLinkCreated = 20,
    ShareLinkRevoked = 21,
    DirectShareGranted = 30,
    DirectShareRevoked = 31,
    GroupCreated = 40,
    GroupMemberAdded = 41,
    GroupMemberRemoved = 42,
    UserSignedIn = 50,
    UserInvited = 51,
}

/// <summary>
/// Audit-friendly activity log — recent events per user + a global admin view.
/// FileId / FolderId / GroupId / TargetUserId are optional pointers depending
/// on the event kind. Details is a short human-readable line, already localized
/// (the writer picks a resource string).
/// </summary>
public class ActivityEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ActivityKind Kind { get; set; }
    public Guid ActorUserId { get; set; }
    public User? Actor { get; set; }

    public Guid? FileId { get; set; }
    public Guid? FolderId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? TargetUserId { get; set; }

    public string Summary { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
