namespace NimShare.Core.Entities;

/// <summary>
/// A personal API token that grants a subset of the owning user's rights.
/// The raw token is only shown once at creation; TokenHash stores a bcrypt
/// hash for verification. Expiring/revocable/scoped.
/// </summary>
public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;  // first 6 chars, for display

    /// <summary>Comma-separated scope list. "files:read", "files:write",
    /// "links:manage", "signatures:read" etc. Empty = full user rights.</summary>
    public string? Scopes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum WebhookEvent
{
    FileUploaded = 1,
    FileDeleted = 2,
    LinkCreated = 10,
    LinkDownloaded = 11,
    SignatureRequestSent = 20,
    SignatureRequestCompleted = 21,
    SignatureRequestDeclined = 22,
    DirectShareGranted = 30,
    GroupMemberAdded = 40,
}

/// <summary>
/// Webhook subscription per user. When one of Events fires, a POST is
/// delivered to Url with a JSON payload signed by HMAC-SHA256(secret, body).
/// Failed deliveries are logged; retries are best-effort.
/// </summary>
public class Webhook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public string Url { get; set; } = string.Empty;
    /// <summary>HMAC signing secret shared with the receiver, data-protected before persist.</summary>
    public string SecretEncrypted { get; set; } = string.Empty;
    /// <summary>Comma-separated event names to subscribe to. Empty = all.</summary>
    public string? Events { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastDeliveredAt { get; set; }
    public int FailureCount { get; set; }
}
