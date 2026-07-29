namespace NimShare.Core.Entities;

public enum AccountRequestStatus { Pending = 0, Approved = 1, Rejected = 2 }

/// <summary>
/// A visitor-submitted request for an account, made from the public login
/// page. An Admin reviews it under /settings/users and either approves it
/// (which creates an Invitation so the visitor sets their own password —
/// see AccountRequestsController.Approve) or rejects it.
/// </summary>
public class AccountRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Message { get; set; }

    public AccountRequestStatus Status { get; set; } = AccountRequestStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public User? DecidedBy { get; set; }
}
