namespace NimShare.Core.Entities;

public enum NotificationKind
{
    DirectShareGranted = 1,
    LinkDownloaded = 2,
    InviteAccepted = 3,
    UserMentioned = 10,
    QuotaWarning = 20,
    SystemAnnouncement = 30,
}

/// <summary>
/// In-app notification shown in the notifications tray and on /notifications.
/// Best-effort: writes should never block the caller's flow.
/// </summary>
public class UserNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public NotificationKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }

    /// <summary>Optional deep-link the user clicks to jump to the relevant page.</summary>
    public string? Href { get; set; }

    /// <summary>Referenced file, if any — for cascade-delete safety when a file is purged.</summary>
    public Guid? FileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}
