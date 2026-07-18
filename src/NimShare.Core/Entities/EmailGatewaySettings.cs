namespace NimShare.Core.Entities;

public enum EmailProvider
{
    Disabled = 0,
    Smtp = 1,
    Resend = 2
}

/// <summary>
/// Singleton row (Id = fixed Guid) that stores the tenant-wide outbound
/// email configuration. Secrets are encrypted at rest via ASP.NET Core's
/// DataProtection API before being written to this row.
/// </summary>
public class EmailGatewaySettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = SingletonId;

    public EmailProvider Provider { get; set; } = EmailProvider.Disabled;

    public string FromAddress { get; set; } = "no-reply@nimshare.local";
    public string FromName { get; set; } = "NimShare";

    // SMTP fields.
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string? SmtpUsername { get; set; }
    /// <summary>Ciphertext (DataProtection-Encrypted). Never expose in JSON.</summary>
    public string? SmtpPasswordEncrypted { get; set; }

    // Resend fields.
    /// <summary>Ciphertext (DataProtection-Encrypted). Never expose in JSON.</summary>
    public string? ResendApiKeyEncrypted { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
