namespace NimShare.Core.Entities;

public enum CustomDomainVerificationStatus
{
    Pending = 0,
    Verified = 1,
    Failed = 2,
    Deleted = 3
}

public enum CustomDomainCertificateStatus
{
    None = 0,
    Provisioning = 1,
    Issued = 2,
    Expired = 3,
    Failed = 4
}

public class CustomDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>Lowercase hostname, e.g. "share.example.com".</summary>
    public string Hostname { get; set; } = string.Empty;

    public string VerificationToken { get; set; } = string.Empty;

    public CustomDomainVerificationStatus VerificationStatus { get; set; }
        = CustomDomainVerificationStatus.Pending;

    public CustomDomainCertificateStatus CertificateStatus { get; set; }
        = CustomDomainCertificateStatus.None;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? LastVerificationAttemptAt { get; set; }
}
