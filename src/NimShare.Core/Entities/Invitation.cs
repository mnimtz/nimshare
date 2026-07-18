namespace NimShare.Core.Entities;

/// <summary>
/// An open invitation an Admin sent to prospective user. On accept, the
/// recipient sets their own password and a normal User row is created.
/// </summary>
public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>bcrypt hash of the one-time token; the plain token only lives in the invite URL.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid InvitedByUserId { get; set; }
    public User? InvitedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
