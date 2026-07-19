namespace NimShare.Core.Entities;

public class UploadRequestLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string Slug { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxUploads { get; set; }
    public int UploadCount { get; set; }

    /// <summary>Markdown message shown to the recipient before they drop the file.</summary>
    public string? Message { get; set; }

    /// <summary>Target folder in the owner's namespace for received files (legacy string path).</summary>
    public string TargetFolder { get; set; } = "Received";

    /// <summary>Target folder as a first-class entity; when set, uploads land under this folder.</summary>
    public Guid? TargetFolderId { get; set; }
    public Folder? TargetFolderRef { get; set; }

    public bool NotifyOnUpload { get; set; } = true;
    public bool IsRevoked { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUploadAt { get; set; }

    /// <summary>
    /// Comma-separated list of ISO weekday numbers (1=Mon..7=Sun) on which the
    /// request auto-reopens. Null disables recurrence. The reminder service
    /// resets ExpiresAt+UploadCount at the next matching midnight (owner-local
    /// UTC).
    /// </summary>
    public string? RecurringDaysOfWeek { get; set; }

    /// <summary>How many days after re-opening the reset window stays open.</summary>
    public int RecurringWindowDays { get; set; } = 7;

    public bool IsActive(DateTimeOffset now)
        => !IsRevoked
           && (ExpiresAt is null || ExpiresAt.Value > now)
           && (MaxUploads is null || UploadCount < MaxUploads.Value);
}
