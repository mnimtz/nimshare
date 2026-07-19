namespace NimShare.Core.Entities;

/// <summary>
/// Personal address-book entry. Used by the signature wizard to pre-fill
/// participants and by the share-by-email flow to autocomplete recipients.
///
/// Contacts are per-owner; there is no shared team address book (yet).
/// </summary>
public class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string? Notes { get; set; }
    /// <summary>Comma-separated free-form tags (e.g. "kunde,vip,legal"). Used
    /// for the tag filter in the address-book UI.</summary>
    public string? Tags { get; set; }

    /// <summary>Bumped by SignaturesController when a contact's email is used
    /// as a participant, so "Recently used" surfaces the right names.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
    public int UseCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
