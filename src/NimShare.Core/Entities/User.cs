namespace NimShare.Core.Entities;

public enum UserRole
{
    User = 0,
    Admin = 1,
    /// <summary>v1.10.78: versteckter Service-Account — nicht sichtbar im
    /// Adressbuch, User-Directory oder als Signatur-Empfänger. Für System-
    /// Webhooks, API-Tokens, automatisierte Prozesse. Admin sieht ihn in
    /// /settings/users und kann ihn verwalten wie jeden anderen User.</summary>
    System = 2
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

    /// <summary>
    /// If true, the user's avatar is rendered top-right on every public share
    /// landing (/s/{slug}) they own. Off by default — most users share
    /// impersonally under the Tungsten mark. Opt-in from the profile page.
    /// </summary>
    public bool ShowAvatarOnLandings { get; set; }

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>Per-user storage quota in bytes.</summary>
    public long QuotaBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Whether this user may see files in the Public library. Default on.</summary>
    public bool PublicCanRead { get; set; } = true;
    /// <summary>Whether this user may upload/modify files in the Public library.</summary>
    public bool PublicCanWrite { get; set; } = true;
    /// <summary>Whether this user may delete files from the Public library. Admins always can.</summary>
    public bool PublicCanDelete { get; set; }

    /// <summary>Preferred UI language — one of en/fr/it/de/es.</summary>
    public string PreferredCulture { get; set; } = "en";

    /// <summary>
    /// v1.10.50: IANA-Zeitzonen-Id für die Display-Formatierung.
    /// Wenn null/leer → falls back auf die Server-Timezone (siehe
    /// TimeService). Sonst zeigt jeder User seine eigene Zeit.
    /// </summary>
    public string? PreferredTimezone { get; set; }

    /// <summary>TOTP secret (base32) — null when 2FA isn't enrolled. When set,
    /// login demands a valid 6-digit code after password verification.</summary>
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    public DateTimeOffset? TotpEnrolledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>v1.10.139 — Zeitpunkt der letzten erfolgreichen Anmeldung
    /// (Web-Cookie ODER iOS-API). Anders als LastSeenAt, das bei jedem Request
    /// aktualisiert wird, wird dies NUR beim tatsächlichen Login gesetzt.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<StorageFile> Files { get; set; } = new List<StorageFile>();
    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
    public ICollection<UploadRequestLink> UploadRequests { get; set; } = new List<UploadRequestLink>();
    public ICollection<CustomDomain> CustomDomains { get; set; } = new List<CustomDomain>();
    public ICollection<GroupMembership> Groups { get; set; } = new List<GroupMembership>();
}
