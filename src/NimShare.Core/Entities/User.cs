namespace NimShare.Core.Entities;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Object id from the Entra ID token; the join key for federated sign-ins. Empty for local-only accounts.</summary>
    public string EntraOid { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>bcrypt hash for local password auth. Null when the account is Entra-only.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Disabled users cannot sign in and their links are hidden from public resolution.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional public URL for a profile picture (avatar). Falls back to initials on the sidebar.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Blob path of the user's uploaded avatar; served via /avatars/{userId}.</summary>
    public string? AvatarBlobPath { get; set; }

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>Per-user storage quota in bytes.</summary>
    public long QuotaBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Preferred UI language — one of en/fr/it/de/es.</summary>
    public string PreferredCulture { get; set; } = "en";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public ICollection<StorageFile> Files { get; set; } = new List<StorageFile>();
    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
    public ICollection<UploadRequestLink> UploadRequests { get; set; } = new List<UploadRequestLink>();
    public ICollection<CustomDomain> CustomDomains { get; set; } = new List<CustomDomain>();
    public ICollection<GroupMembership> Groups { get; set; } = new List<GroupMembership>();
}
