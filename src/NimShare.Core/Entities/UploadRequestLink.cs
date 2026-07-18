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

    /// <summary>Target folder in the owner's namespace for received files.</summary>
    public string TargetFolder { get; set; } = "Received";

    public bool NotifyOnUpload { get; set; } = true;
    public bool IsRevoked { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUploadAt { get; set; }

    public bool IsActive(DateTimeOffset now)
        => !IsRevoked
           && (ExpiresAt is null || ExpiresAt.Value > now)
           && (MaxUploads is null || UploadCount < MaxUploads.Value);
}
