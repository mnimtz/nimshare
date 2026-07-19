namespace NimShare.Core.Entities;

/// <summary>
/// User-authored email template used when NimShare sends emails on behalf of
/// the user (currently only signature invites; extensible to share-link
/// notifications and upload-request cover emails later).
///
/// Templates use Handlebars-style placeholders — resolved at send time:
///   {{recipient.name}}, {{recipient.email}},
///   {{sender.name}},    {{sender.email}},
///   {{doc.title}},      {{doc.name}},
///   {{url}}, {{message}}, {{sender.action}} (sign/review)
/// </summary>
public class EmailTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    /// <summary>Human-visible label. "Formal contract", "Casual reminder", …</summary>
    public string Name { get; set; } = string.Empty;
    public EmailTemplateKind Kind { get; set; } = EmailTemplateKind.SignatureInvite;
    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
    /// <summary>ISO-639 language code the copy is written in. Templates are
    /// filtered by locale in the wizard picker.</summary>
    public string Locale { get; set; } = "de";
    /// <summary>Auto-picked in the wizard if no other template is chosen.
    /// One default per (owner, kind, locale) triple.</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum EmailTemplateKind
{
    SignatureInvite = 0,
    SignatureReminder = 1,
    // Reserved for future scope expansion:
    ShareLink = 10,
    UploadRequest = 20,
}
