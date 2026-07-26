namespace NimShare.Core.Entities;

/// <summary>
/// v1.11.0 — Instanz-Konfiguration für Subdomain-Sharing
/// (https://wichtig.nimshare.com statt /s/wichtig). Singleton-Row, analog
/// ConnectorProviderSettings. Die Basis-Domain ist bewusst konfigurierbar
/// („nicht fix vercoded auf nimshare.com") — andere Instanzen tragen ihre
/// eigene Domain ein oder lassen das Feature einfach aus.
///
/// Cloudflare-API-Token (Zone.DNS-Edit-Recht reicht) wird DataProtection-
/// verschlüsselt gespeichert und nur für den Setup-Assistenten benutzt
/// (Wildcard-CNAME + asuid-TXT anlegen). Im laufenden Betrieb macht das
/// Feature KEINE externen API-Calls — Routing ist reiner Host-Header-Lookup.
/// </summary>
public class SubdomainShareSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Master-Schalter. Aus = Middleware inaktiv, UI-Option versteckt.</summary>
    public bool Enabled { get; set; }

    /// <summary>Basis-Domain, z.B. "nimshare.com". Subdomain-Links werden als
    /// https://{slug}.{BaseDomain} gebaut. Lowercase, ohne Schema/Slash.</summary>
    public string BaseDomain { get; set; } = "";

    /// <summary>Ziel für den Wildcard-CNAME, z.B. "myapp.azurewebsites.net".
    /// Default wird aus WEBSITE_HOSTNAME vorgeschlagen.</summary>
    public string OriginHost { get; set; } = "";

    /// <summary>Azure "Custom Domain Verification ID" — optional. Wenn gesetzt,
    /// legt der Setup-Assistent auch den asuid.*-TXT-Record an, damit das
    /// Wildcard-Custom-Domain-Binding im Portal validiert werden kann.</summary>
    public string? AzureVerificationId { get; set; }

    /// <summary>DataProtection-verschlüsseltes Cloudflare-API-Token
    /// (Protect(UTF8Bytes(token))). Null = kein Setup-Assistent, nur manuell.</summary>
    public byte[]? CloudflareApiTokenEncrypted { get; set; }

    /// <summary>Cloudflare-Zone-Id — wird beim ersten Setup automatisch über
    /// die Zonen-Suche (name=BaseDomain) ermittelt und hier gecacht.</summary>
    public string? CloudflareZoneId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
