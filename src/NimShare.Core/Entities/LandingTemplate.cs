namespace NimShare.Core.Entities;

public enum LandingTemplateScope
{
    /// <summary>Applied to every download landing for files in the Public scope. Admin-only.</summary>
    Global = 0,

    /// <summary>Applied to download landings for files this user owns (Personal scope).</summary>
    UserPersonal = 1,
}

/// <summary>
/// Customises the public download-landing page for a file or folder share.
/// The Global template covers files in the Public scope; a UserPersonal
/// template covers everything owned by the same user in their Personal scope.
/// Group-scope files fall back to Global.
/// </summary>
public class LandingTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public LandingTemplateScope Scope { get; set; }

    /// <summary>Set for UserPersonal; null for Global.</summary>
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    /// <summary>Main headline, e.g. "Downloads von ACME".</summary>
    public string? Title { get; set; }

    /// <summary>One-line intro under the title.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Longer body — markdown allowed. Rendered above the download button.</summary>
    public string? BodyMarkdown { get; set; }

    /// <summary>Small footer note (privacy line, contact, etc.).</summary>
    public string? FooterText { get; set; }

    /// <summary>Hex color like "#002854" for the accent (buttons, links).</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>Blob storage path of the uploaded logo (top-left). Nullable.</summary>
    public string? LogoBlobPath { get; set; }

    /// <summary>Public URL for the logo — a proxy route that streams from Blob.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Blob storage path of the uploaded hero image (banner). Nullable.</summary>
    public string? HeroBlobPath { get; set; }
    public string? HeroUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
