namespace NimShare.Core.Entities;

/// <summary>
/// A one-time link a user requested via "Forgot password?". Same token-hash
/// pattern as <see cref="Invitation"/>: the plain token only ever lives in
/// the emailed URL, the DB stores just its bcrypt hash. Short-lived (1h) —
/// unlike invitations, which need days for the recipient to act.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Snapshot of the email the reset was requested for, for admin/audit visibility.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>bcrypt hash of the one-time token; the plain token only lives in the reset URL.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
    public DateTimeOffset? UsedAt { get; set; }
}
