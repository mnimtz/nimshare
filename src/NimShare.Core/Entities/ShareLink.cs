namespace NimShare.Core.Entities;

public class ShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Set when this link points at a single file. Mutually exclusive with FolderId.</summary>
    public Guid? FileId { get; set; }
    public StorageFile? File { get; set; }

    /// <summary>Set when this link points at a whole folder (recipient gets a mini browser or ZIP). Mutually exclusive with FileId.</summary>
    public Guid? FolderId { get; set; }
    public Folder? Folder { get; set; }

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>Public slug used in the URL, e.g. "project-x". Globally unique.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>v1.11.0 — optionaler Subdomain-Slug: der Link ist zusätzlich
    /// als https://{SubdomainSlug}.{BaseDomain} erreichbar. DNS-safe
    /// ([a-z0-9-], 1-63), unique über ShareLinks UND UploadRequests, gegen
    /// die Reserved-List geprüft. Null = klassischer /s/-Link only.</summary>
    public string? SubdomainSlug { get; set; }

    /// <summary>bcrypt-hashed password, or null if the link is public.</summary>
    public string? PasswordHash { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Max downloads; null = unlimited.</summary>
    public int? MaxDownloads { get; set; }

    public int DownloadCount { get; set; }
    public int HitCount { get; set; }

    /// <summary>Markdown-formatted message shown on the landing page.</summary>
    public string? Message { get; set; }

    /// <summary>Email the owner when the link is downloaded.</summary>
    public bool NotifyOnAccess { get; set; }

    public bool IsRevoked { get; set; }

    /// <summary>
    /// When true, this link surfaces in the "Öffentliche Links" section of the
    /// links page for every signed-in user — read-only for non-owners; only
    /// the owner and admins can revoke, delete, or edit it. Intended for
    /// admin-curated download bundles (installer downloads, form templates,
    /// company brand kit, etc.).
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// Comma-separated allow-list of email addresses or domain wildcards
    /// (e.g. "alice@acme.com,*.tungstenautomation.com,*@partner.io"). When
    /// non-empty, the landing page prompts for an email and only serves the
    /// download after it matches. Combined with RequireEmailVerify, we also
    /// OTP-verify the email before the download unlocks.
    /// </summary>
    public string? AllowedEmails { get; set; }

    /// <summary>
    /// When true, in addition to the allow-list check we email the visitor a
    /// 6-digit code and demand they enter it before the download unlocks.
    /// This proves the recipient controls the mailbox, not just knows the
    /// email address — meaningful when Marcus shares to
    /// finance@customer.com and wants a trail of which human at that shared
    /// mailbox actually opened it.
    /// </summary>
    public bool RequireEmailVerify { get; set; }

    /// <summary>
    /// v1.10.146 — Optionales Absender-Zertifikat, das der Ersteller beim
    /// Erzeugen des Links hinzugefügt hat. Wird auf der Landing als schmale
    /// „✓ Signiert von …"-Zeile mit Details-Popover angezeigt — dezenter
    /// Absender-Ausweis, keine kryptografische Payload-Signatur.
    /// </summary>
    public Guid? SigningCertificateId { get; set; }
    public SigningCertificate? SigningCertificate { get; set; }

    /// <summary>
    /// v1.10.166 — nur relevant für Folder-Links auf Ordnern mit Kind=Gallery.
    /// Wenn true, zeigt die Landing zusätzlich zum Album-Grid einen Upload-
    /// Widget an, mit dem beliebige Besucher Bilder/Videos direkt in den
    /// Ordner hinzufügen können (Event-/Hochzeits-Album-Modus). Auf Nicht-
    /// Gallery-Ordnern und File-Links wird das Flag ignoriert.
    /// </summary>
    public bool AllowUploads { get; set; }

    /// <summary>
    /// v1.10.167 — Anzeige-Modus des Links. Wenn true, rendert die Landing das
    /// Grid+Lightbox-Album statt der klassischen Datei-Liste. Unabhängig vom
    /// Folder.Kind — der Ersteller wählt das per Link, Ordner bleibt neutral.
    /// Für File-Links wird der Wert ignoriert.
    /// </summary>
    public bool DisplayAsGallery { get; set; }

    /// <summary>
    /// v1.10.196 — Aufnahmeort-Karte auf der Album-Landing. Nur relevant im
    /// Gallery-Modus; default true (bisheriges Verhalten). Der Ersteller kann
    /// die Karte pro Link abschalten (Privatsphäre: GPS-Daten der Fotos).
    /// </summary>
    public bool ShowGpsMap { get; set; } = true;

    /// <summary>
    /// v1.11.18 — Optionale Seriennummer/Lizenzcode, der zusätzlich zur Datei
    /// mitgegeben wird (z.B. bei Software-Downloads). Verschlüsselt via
    /// IDataProtector (gleiches Pattern wie EmailGatewaySettings) — steht nie
    /// im rohen Landing-HTML, sondern wird erst nach Klick auf "Anzeigen"
    /// serverseitig entschlüsselt und per Fetch nachgeladen.
    /// </summary>
    public string? SerialNumberEncrypted { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAccessAt { get; set; }

    public ICollection<ShareLinkAccess> Accesses { get; set; } = new List<ShareLinkAccess>();

    /// <summary>True if the link is currently usable — not revoked, not expired, cap not reached.</summary>
    public bool IsActive(DateTimeOffset now)
        => !IsRevoked
           && (ExpiresAt is null || ExpiresAt.Value > now)
           && (MaxDownloads is null || DownloadCount < MaxDownloads.Value);
}
