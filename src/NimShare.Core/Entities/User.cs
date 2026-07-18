namespace NimShare.Core.Entities;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Object id from the Entra ID token; the join key across sign-ins.</summary>
    public string EntraOid { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

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
}
