namespace NimShare.Core.Entities;

public class ShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FileId { get; set; }
    public StorageFile File { get; set; } = null!;

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>Public slug used in the URL, e.g. "project-x". Globally unique.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>bcrypt-hashed password, or null if the link is public.</summary>
    public string? PasswordHash { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Max downloads; null = unlimited.</summary>
    public int? MaxDownloads { get; set; }

    public int DownloadCount { get; set; }
    public int HitCount { get; set; }

    /// <summary>Markdown-formatted message shown on the landing page.</summary>
    public string? Message { get; set; }

    /// <summary>Email the owner when the link is downloaded.</summary>
    public bool NotifyOnAccess { get; set; }

    public bool IsRevoked { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAccessAt { get; set; }

    public ICollection<ShareLinkAccess> Accesses { get; set; } = new List<ShareLinkAccess>();

    /// <summary>True if the link is currently usable — not revoked, not expired, cap not reached.</summary>
    public bool IsActive(DateTimeOffset now)
        => !IsRevoked
           && (ExpiresAt is null || ExpiresAt.Value > now)
           && (MaxDownloads is null || DownloadCount < MaxDownloads.Value);
}
