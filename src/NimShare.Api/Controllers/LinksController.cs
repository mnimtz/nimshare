using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Route("api/v1/links")]
[Authorize(Policy = "ApiUser")]
public class LinksController : ControllerBase
{
    // v1.11.18: gleiches DataProtection-Purpose-Pattern wie EmailGatewayService
    // — muss dem Purpose-String in ShareController (Reveal/Email) exakt
    // entsprechen, sonst schlägt Unprotect() fehl.
    public const string SerialNumberProtectorPurpose = "NimShare.ShareLink.SerialNumber.v1";

    private readonly NimShareDbContext _db;
    private readonly ISlugService _slugs;
    private readonly IPasswordHasher _hasher;
    private readonly IQrCodeService _qr;
    private readonly ICurrentUserService _users;
    private readonly bool _configStoreFullIp;
    private readonly IDataProtector _serialProtector;
    // v1.11.22: separater Protector für Key-Store-Einträge — muss dem
    // Purpose-String in KeyStoreController exakt entsprechen.
    private readonly IDataProtector _keyStoreProtector;
    // v1.11.37: Doku-PDFs/Links (Key-Store-Dokumente) + Attachment-fähiger
    // Mail-Versand + lokalisierte Fallback-Texte für die Key-Store-Mail.
    private readonly IBlobStorageService _blobs;
    private readonly IEmailGatewayService _emailGateway;
    private readonly IStringLocalizer<SharedResources> _l;

    public LinksController(
        NimShareDbContext db, ISlugService slugs, IPasswordHasher hasher,
        IQrCodeService qr, ICurrentUserService users, IConfiguration cfg,
        IDataProtectionProvider dpp, IBlobStorageService blobs,
        IEmailGatewayService emailGateway, IStringLocalizer<SharedResources> l)
    {
        _db = db;
        _slugs = slugs;
        _hasher = hasher;
        _qr = qr;
        _users = users;
        _configStoreFullIp = cfg.GetValue<bool>("ShareLinks:StoreFullIp");
        _serialProtector = dpp.CreateProtector(SerialNumberProtectorPurpose);
        _keyStoreProtector = dpp.CreateProtector(KeyStoreController.ProtectorPurpose);
        _blobs = blobs;
        _emailGateway = emailGateway;
        _l = l;
    }

    public record CreateLinkRequest(
        Guid? FileId,
        Guid? FolderId,
        string? Slug,
        string? Password,
        DateTimeOffset? ExpiresAt,
        int? MaxDownloads,
        string? Message,
        bool NotifyOnAccess,
        // v1.10.146: optionales Absender-Zertifikat (SigningCertificate.Id).
        Guid? SigningCertificateId = null,
        // v1.10.167: Landing als Foto/Video-Album rendern (Grid + Lightbox)
        // statt klassischer Datei-Liste. Nur für Folder-Links; auf File-Links
        // wird der Wert serverseitig auf false erzwungen.
        bool DisplayAsGallery = false,
        // v1.10.167: Wenn true UND (DisplayAsGallery ODER Folder.Kind==Gallery),
        // dürfen Empfänger direkt ins Album hochladen. Sonst wird das Flag
        // serverseitig ignoriert (Landing zeigt kein Upload-Widget).
        bool AllowUploads = false,
        // v1.10.196: Aufnahmeort-Karte auf der Album-Landing (nur Gallery-Modus).
        bool ShowGpsMap = true,
        // v1.11.0: optionaler Subdomain-Slug (https://{slug}.{BaseDomain}).
        // Nur wirksam wenn das Feature aktiv ist und der User das Recht hat.
        string? SubdomainSlug = null,
        // v1.11.18: optionale Seriennummer/Lizenzcode, wird verschlüsselt
        // gespeichert und erst nach Klick auf der Landing entschlüsselt.
        string? SerialNumber = null,
        // v1.11.22: Lizenzschlüssel-Modus (Key-Store-Lookup per Besucher-
        // Email) — schließt sich mit SerialNumber gegenseitig aus, siehe
        // ShareLink.KeyStoreMode-Doku.
        // v1.11.44: DocumentationUrl → DocumentationEnabled (reines Ein/Aus,
        // kein fester URL-Wert mehr — siehe ShareLink-Doku).
        bool KeyStoreMode = false,
        bool DocumentationEnabled = false,
        // v1.11.50: explizites "läuft nie ab" — Default false, damit ein
        // fehlendes ExpiresAt serverseitig auf +8 Wochen defaultet statt
        // stillschweigend permanent zu werden (siehe Create()).
        bool IsPermanent = false,
        // v1.12: optionale link-eigene Landing-Vorlage (Custom Branding pro Link,
        // u.a. KI-Auto-Fill aus der Empfänger-Domain). Verweist auf eine
        // LandingTemplate-Zeile mit Scope=Link (angelegt vom Branding-Endpoint).
        // Null = kein Custom-Branding → unveränderter Global/UserPersonal-Fallback.
        Guid? LandingTemplateId = null,
        // v1.12.7: finaler Firmenname neben dem Logo — vom Teilenden editiert.
        // null/leer ⇒ Toggle aus bzw. kein Name → nichts neben dem Logo.
        // Wird nur auf die EIGENE Link-Vorlage angewandt (nach Ownership-Check).
        string? BrandName = null);

    public record LinkDto(
        Guid Id, string Slug, string Url, string QrCodeUrl,
        DateTimeOffset? ExpiresAt, int? MaxDownloads,
        int DownloadCount, int HitCount, bool HasPassword,
        bool IsRevoked, DateTimeOffset CreatedAt,
        bool IsPublic,
        // v1.10.71: Wofür ist der Link? iOS/Web zeigt jetzt "Datei: X"
        // oder "Ordner: Y" statt bloß Slug. TargetKind = "file"|"folder"|null.
        string? TargetKind, string? TargetName,
        // v1.10.146: optionales Absender-Zertifikat für Landing-Badge.
        SignerInfo? Signer = null,
        // v1.10.167: Anzeige-Modus + Upload-Option des Links (nicht Ordner).
        // FolderIsGallery = Convenience-Info: Ordner ist Kind=Gallery (Default).
        bool FolderIsGallery = false,
        bool DisplayAsGallery = false,
        bool AllowUploads = false,
        // v1.10.196: GPS-Karten-Toggle des Links.
        bool ShowGpsMap = true,
        // v1.11.0: fertige Subdomain-URL (https://{slug}.{BaseDomain}), null
        // wenn der Link keinen Subdomain-Slug hat oder das Feature aus ist.
        string? SubdomainUrl = null,
        // v1.11.18: iOS zeigte bislang nur eigene Links (WHERE OwnerId==me),
        // Web dagegen zusätzlich alle Public-Scope-Links (auch von anderen
        // Ownern) + eigene Group-Scope-Links separat. Damit iOS dieselbe
        // Liste sehen kann, liefert das DTO jetzt Scope-Klassifikation +
        // Owner-Name (nur relevant, wenn IsOwnedByMe=false).
        string Scope = "private",
        bool IsOwnedByMe = true,
        string? OwnerName = null,
        // v1.11.18: Seriennummer optional pro Link — nie im Klartext im DTO,
        // nur ob eine hinterlegt ist (Landing entschlüsselt on-demand).
        bool HasSerialNumber = false,
        // v1.11.22: Lizenzschlüssel-Modus. v1.11.44: DocumentationEnabled
        // statt DocumentationUrl — reines Ein/Aus-Flag.
        bool KeyStoreMode = false,
        bool DocumentationEnabled = false,
        // v1.11.50: Ablauf-Opt-out, siehe CreateLinkRequest.IsPermanent.
        bool IsPermanent = false);

    public record SignerInfo(
        Guid CertificateId,
        string Subject,
        string Issuer,
        string Thumbprint,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter,
        bool IsSelfIssued);

    // v1.10.41: Live-Check für den Share-Dialog. Während der User tippt
    // fragt das Frontend hier an (debounce 400ms), zeigt sofort ob der
    // Wunsch-Slug frei ist. Bei belegtem Slug liefern wir bis zu 3
    // klickfertige Alternativen — keine 409 mehr beim "Speichern".
    // Auth: der Route liegt bereits hinter ApiUser-Policy; ein Login-
    // Nutzer darf naturgemäss wissen ob ein Slug frei ist (das ist
    // auch beim Aufruf des Public-Landings sowieso sichtbar).
    public record SlugCheckResponse(bool Available, string? Reason, string Normalised, List<string> Suggestions);

    [HttpGet("slug-check")]
    public async Task<ActionResult<SlugCheckResponse>> SlugCheck(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return Ok(new SlugCheckResponse(false, "empty", "", new List<string>()));
        // Empty → passt: der Server generiert dann Random. Aber die UI
        // zeigt bei leerem Feld sowieso nichts an, also 200/false ist OK.
        var normalised = _slugs.IsValid(slug) ? slug : SlugService.Normalise(slug);
        if (!_slugs.IsValid(normalised))
            return Ok(new SlugCheckResponse(false, "invalid", normalised, new List<string>()));
        var free = await _slugs.IsAvailableAsync(normalised, ct);
        if (free)
            return Ok(new SlugCheckResponse(true, null, normalised, new List<string>()));
        var suggestions = await _slugs.SuggestAlternativesAsync(normalised, 3, ct);
        return Ok(new SlugCheckResponse(false, "taken", normalised, suggestions));
    }

    // ── v1.11.0: Subdomain-Sharing ──────────────────────────────────────
    // Info fürs Frontend (Web-Modal + iOS-Sheet): Feature an? Basis-Domain?
    // Darf der aktuelle User? Ein Aufruf beim Öffnen des Dialogs genügt.
    public record SubdomainInfoResponse(bool Enabled, string? BaseDomain, bool CanUse);

    [HttpGet("subdomain-info")]
    public async Task<ActionResult<SubdomainInfoResponse>> SubdomainInfo(
        [FromServices] ISubdomainShareService subSvc, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var s = await subSvc.GetSettingsAsync(ct);
        var enabled = s is { Enabled: true } && !string.IsNullOrEmpty(s.BaseDomain);
        // v1.11.27: Marcus's Wunsch — Subdomain-Sharing steht jetzt jedem User
        // offen (nicht mehr nur Admins/Admin-freigeschalteten Usern).
        return Ok(new SubdomainInfoResponse(enabled, enabled ? s!.BaseDomain : null, enabled));
    }

    // Live-Check für das Subdomain-Feld (analog slug-check).
    public record SubdomainCheckResponse(bool Available, string? Reason, string Normalised);

    [HttpGet("subdomain-check")]
    public async Task<ActionResult<SubdomainCheckResponse>> SubdomainCheck(string slug,
        [FromServices] ISubdomainShareService subSvc, CancellationToken ct)
    {
        var normalised = (slug ?? "").Trim().ToLowerInvariant();
        if (!subSvc.IsValidSlug(normalised, out var reason))
            return Ok(new SubdomainCheckResponse(false, reason, normalised));
        var free = await subSvc.IsSlugAvailableAsync(normalised, ct);
        return Ok(new SubdomainCheckResponse(free, free ? null : "taken", normalised));
    }

    [HttpPost]
    public async Task<ActionResult<LinkDto>> Create([FromBody] CreateLinkRequest req,
        [FromServices] IFileAccessService access,
        [FromServices] IFolderService folderSvc,
        CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        if (req.FileId is null && req.FolderId is null)
            return Problem(statusCode: 422, title: "Either FileId or FolderId is required.");
        if (req.FileId is not null && req.FolderId is not null)
            return Problem(statusCode: 422, title: "Provide either FileId or FolderId, not both.");
        // v1.11.19: Review-Fund — ohne Längenprüfung lief ein langer
        // eingefügter Lizenzblock roh in _serialProtector.Protect(), dessen
        // Output über die HasMaxLength(4000)-Spalte hinauswachsen und
        // SaveChangesAsync() mit einer rohen 500 statt einer sauberen 422
        // crashen lassen konnte.
        if (req.SerialNumber is { Length: > 1000 })
            return Problem(statusCode: 422, title: "Serial number too long (max 1000 characters).");

        StorageFile? file = null;
        NimShare.Core.Entities.Folder? folder = null;
        if (req.FileId is Guid fid)
        {
            file = await _db.Files.Include(f => f.Owner).SingleOrDefaultAsync(f => f.Id == fid && f.Status == StorageFileStatus.Ready, ct);
            if (file is null || !await access.CanShareAsync(user, file, ct)) return Forbid();
        }
        else if (req.FolderId is Guid folid)
        {
            folder = await _db.Folders.FindAsync(new object[] { folid }, ct);
            if (folder is null || !await folderSvc.CanReadAsync(folder, user, ct)) return Forbid();
        }

        string slug;
        try { slug = await _slugs.ResolveOrGenerateAsync(req.Slug, ct); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: "Slug taken", detail: ex.Message); }
        catch (ArgumentException ex) { return Problem(statusCode: 422, title: "Invalid slug", detail: ex.Message); }

        // v1.11.0: optionaler Subdomain-Slug. Feature muss instanzweit aktiv
        // sein, der Slug muss DNS-safe + nicht reserviert + über beide Link-
        // Typen frei sein.
        // v1.11.27: Marcus's Wunsch — jeder User darf Subdomain-Links anlegen
        // (das Admin-vergebene Per-User-Recht CanUseSubdomainShares entfällt).
        string? subdomainSlug = null;
        if (!string.IsNullOrWhiteSpace(req.SubdomainSlug))
        {
            var subSvc = HttpContext.RequestServices.GetRequiredService<ISubdomainShareService>();
            var subSettings = await subSvc.GetSettingsAsync(ct);
            if (subSettings is null || !subSettings.Enabled || string.IsNullOrEmpty(subSettings.BaseDomain))
                return Problem(statusCode: 422, title: "Subdomain sharing is not enabled on this instance.");
            var candidate = req.SubdomainSlug.Trim().ToLowerInvariant();
            if (!subSvc.IsValidSlug(candidate, out var reason))
                return Problem(statusCode: 422, title: "Invalid subdomain slug", detail: reason);
            if (!await subSvc.IsSlugAvailableAsync(candidate, ct))
                return Problem(statusCode: 409, title: "Subdomain slug taken");
            subdomainSlug = candidate;
        }

        // v1.10.146: Absender-Zertifikat — nur eigene akzeptieren, sonst leise
        // ignorieren (kein Fehler, damit der Link trotzdem erstellt wird).
        Guid? certId = null;
        if (req.SigningCertificateId is Guid cid)
        {
            var owned = await _db.SigningCertificates
                .AnyAsync(c => c.Id == cid && c.OwnerUserId == user.Id, ct);
            if (owned) certId = cid;
        }

        // v1.10.167: DisplayAsGallery ist ein Per-Link-Anzeige-Modus (nicht Ordner-
        // attribut). Nur für Folder-Links erlaubt. Auf File-Links wird das Flag
        // ignoriert, weil ein einzelnes File kein Album ist.
        var displayAsGallery = req.DisplayAsGallery && folder is not null;
        // AllowUploads gilt wenn der Link im Gallery-Modus rendert (per Link-
        // Setting ODER Folder.Kind==Gallery als Default). Auf File-Links und
        // reinen Nicht-Gallery-Ordner-Links serverseitig hart auf false.
        var isGalleryLink = displayAsGallery || (folder is not null && folder.Kind == FolderKind.Gallery);
        var allowUploads = req.AllowUploads && folder is not null && isGalleryLink;

        // v1.11.50: Marcus's Wunsch — Links sollen nicht endlos liegen bleiben,
        // wenn niemand sie später von Hand löscht. Default: 8 Wochen ab
        // Erstellung, außer der Ersteller wählt explizit "Dauerhaft" oder gibt
        // ein eigenes Datum vor.
        var expiresAt = req.IsPermanent ? (DateTimeOffset?)null : (req.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(56));

        // v1.12 — Custom-Branding-Vorlage nur akzeptieren, wenn sie existiert UND
        // Scope=Link ist. Verhindert, dass ein Link auf ein Global/UserPersonal-
        // Template (oder eine geratene GUID) gezeigt wird.
        Guid? landingTemplateId = null;
        if (req.LandingTemplateId is Guid ltId)
        {
            // v1.12 (Review F5): nur eine Link-Vorlage akzeptieren, die DIESER User
            // per KI-Auto-Branding erzeugt hat (CreatedByUserId) — verhindert, dass
            // jemand die (per öffentlicher Logo-URL erratbare) Vorlage eines anderen
            // an seinen Link hängt.
            var tpl = await _db.LandingTemplates.FirstOrDefaultAsync(
                t => t.Id == ltId
                     && t.Scope == NimShare.Core.Entities.LandingTemplateScope.Link
                     && t.CreatedByUserId == user.Id, ct);
            if (tpl is null)
            {
                // v1.12.8 (Audit): NICHT mehr still ohne Branding erstellen — z. B.
                // Vorlage vom 24-h-Orphan-Sweep abgeräumt (Modal lag lange offen).
                // Der User hat Branding explizit gewollt → klarer Fehler statt
                // kommentarlosem Downgrade; UI fordert zum erneuten Vorschau-Lauf auf.
                return UnprocessableEntity(new { error = "branding_template_gone" });
            }
            landingTemplateId = ltId;
            // v1.12.7/v1.12.8: finaler Firmenname neben dem Logo.
            // Semantik: null = Feld nicht mitgesendet (alte Clients/iOS) → Vorlage
            // UNVERÄNDERT lassen (kein Wipe des KI-Namens); "" = bewusst aus;
            // sonst Wert (max 120 Zeichen, kein halbes Surrogate-Paar).
            // Schutz geteilter Vorlagen: hängt die Vorlage schon an einem anderen
            // Link, wird sie NICHT mehr mutiert — sonst änderte ein zweiter
            // "Erstellen"-Klick rückwirkend die Landing von Link 1.
            if (req.BrandName is not null)
            {
                var inUse = await _db.ShareLinks.AnyAsync(l => l.LandingTemplateId == ltId, ct);
                if (!inUse)
                {
                    var bn = req.BrandName.Trim();
                    if (bn.Length > 120)
                    {
                        var cut = 120;
                        if (char.IsHighSurrogate(bn[cut - 1])) cut--;
                        bn = bn[..cut];
                    }
                    tpl.BrandName = bn.Length == 0 ? null : bn;
                }
            }
            tpl.UpdatedAt = DateTimeOffset.UtcNow; // Sweep-Schutz immer refreshen
        }

        var link = new ShareLink
        {
            FileId = file?.Id,
            FolderId = folder?.Id,
            OwnerId = user.Id,
            Slug = slug,
            PasswordHash = string.IsNullOrEmpty(req.Password) ? null : _hasher.Hash(req.Password),
            ExpiresAt = expiresAt,
            IsPermanent = req.IsPermanent,
            MaxDownloads = req.MaxDownloads,
            Message = req.Message,
            NotifyOnAccess = req.NotifyOnAccess,
            SigningCertificateId = certId,
            AllowUploads = allowUploads,
            DisplayAsGallery = displayAsGallery,
            // v1.10.196: GPS-Karte pro Link abschaltbar (nur Gallery relevant).
            ShowGpsMap = req.ShowGpsMap,
            // v1.11.0: Subdomain-Slug (oben validiert, null wenn nicht gewünscht).
            SubdomainSlug = subdomainSlug,
            // v1.11.18: Seriennummer verschlüsselt ablegen — nie im Klartext
            // in der DB oder im Response-DTO. v1.11.20: Marcus's Bug-Report —
            // v1.11.19 hatte das fälschlich auf File-Links beschränkt, obwohl
            // das Share-Modal das Feld für BEIDE Link-Typen anbietet (z.B.
            // ein "Downloads"-Ordner mit Installer + Lizenzcode ist ein
            // legitimer Anwendungsfall). Jetzt für File- UND Folder-Links.
            // v1.11.22: KeyStoreMode und die statische Seriennummer schließen
            // sich aus — bei aktiviertem Key-Store-Modus wird eine evtl.
            // trotzdem mitgeschickte statische Nummer stillschweigend
            // ignoriert (analog anderer sich-ausschließender Flag-Paare in
            // diesem Endpoint, z.B. DisplayAsGallery/File-Link).
            SerialNumberEncrypted = (!req.KeyStoreMode && !string.IsNullOrWhiteSpace(req.SerialNumber))
                ? _serialProtector.Protect(req.SerialNumber.Trim()) : null,
            KeyStoreMode = req.KeyStoreMode,
            DocumentationEnabled = req.DocumentationEnabled,
            // v1.12 — link-eigene Landing-Vorlage (oben validiert, null wenn keine/ungültig).
            LandingTemplateId = landingTemplateId,
        };
        _db.ShareLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        // v1.10.146: Signer für Response-DTO nachladen (Include beim frischen
        // Entity greift noch nicht).
        if (certId is Guid cid2)
            link.SigningCertificate = await _db.SigningCertificates.FindAsync(new object[] { cid2 }, ct);

        HttpContext.RequestServices.GetService<IWebhookDispatcher>()?
            .QueueEvent(user.Id, WebhookEvent.LinkCreated,
                new { linkId = link.Id, slug = link.Slug, fileId = link.FileId, folderId = link.FolderId });

        // v1.10.181: Thumbnail-Pre-Warm bei Album-Links. Der Empfänger sieht
        // sofort echte Vorschauen statt Kamera-Fallbacks + Wartezeit — wir
        // wissen JETZT welche Files geshared werden, also fangen wir jetzt
        // an. Enqueue ist dedup-safe (v1.10.191 Worker-Queue), harmlos wenn
        // dieselbe Datei mehrfach getriggert wird.
        if (folder is not null && isGalleryLink)
        {
            var thumbs = HttpContext.RequestServices.GetService<IThumbnailService>();
            if (thumbs is not null)
            {
                var mediaFiles = await _db.Files
                    .Where(f => f.FolderId == folder.Id
                        && f.Status == StorageFileStatus.Ready
                        && f.ContentType != null
                        && f.ContentType.StartsWith("image/"))
                    .Select(f => new { f.Id, f.BlobPath, f.ContentType })
                    .ToListAsync(ct);
                foreach (var mf in mediaFiles)
                {
                    // v1.10.191: ein Enqueue pro FILE — der Worker baut beide
                    // Größen (400 + 1600) aus einem einzigen Decode-Durchgang.
                    thumbs.Enqueue(mf.Id, mf.BlobPath, mf.ContentType);
                }
            }
        }
        // v1.10.183: Album-ZIP im Hintergrund vorbauen — auch für Folder-Links
        // ohne Gallery-Modus, weil der „Alle herunterladen"-Button unabhängig
        // vom Anzeige-Modus da ist. Bei Link-Delete wird das ZIP mitgeräumt.
        if (folder is not null)
        {
            var zipCache = HttpContext.RequestServices.GetService<IAlbumZipCache>();
            if (zipCache is not null)
                _ = zipCache.WarmupAsync(link.Id, CancellationToken.None);
        }

        var activity = HttpContext.RequestServices.GetService<IActivityLogger>();
        if (activity is not null)
        {
            var subject = file?.Name ?? folder?.Name ?? "Element";
            await activity.LogAsync(ActivityKind.ShareLinkCreated, user,
                $"Share-Link erstellt: /s/{link.Slug} ({subject})",
                fileId: link.FileId, folderId: link.FolderId, ct: ct);
        }
        return CreatedAtAction(nameof(GetById), new { id = link.Id }, ToDto(link, user.Id, await SubdomainBaseAsync(ct)));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.10.66: Include File+Folder damit IsPublic korrekt berechnet
        // werden kann (Split "Öffentliche Links" vs "Meine Links" im iOS-
        // und Web-Client).
        // v1.11.18: iOS rief bislang dieselbe Query wie hier — nur eigene
        // Links (OwnerId==me). Web (/links, HomeController.Links) zeigt
        // zusätzlich alle Public-Scope-Links (auch von anderen Ownern) sowie
        // die eigenen Group-Scope-Links separat. Damit iOS dieselbe
        // Gesamtmenge sieht, matcht die Query jetzt HomeController.Links 1:1.
        // v1.11.22: Admin-Bypass ergänzt (siehe HomeController.Links) — Admins
        // sehen jeden Link, unabhängig von Owner/Scope.
        var isAdmin = user.Role == UserRole.Admin;
        var rows = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .Include(l => l.SigningCertificate)
            .Include(l => l.Owner)
            .Where(l => isAdmin
                     || l.OwnerId == user.Id
                     || (l.File != null && l.File.Scope == FileScope.Public)
                     || (l.Folder != null && l.Folder.Scope == FileScope.Public)
                     || l.IsPublic)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);
        // v1.11.27: Marcus's Wunsch — Subdomain-Links sind jetzt für ALLE User
        // sichtbar (nicht nur eigene/Public-Scope), unabhängig vom Owner.
        // Lösch-/Bearbeitungsrechte ändern sich NICHT (weiterhin nur Owner
        // oder Admin, siehe Update()/Delete()) — es geht nur um Sichtbarkeit.
        var subdomainExtra = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .Include(l => l.SigningCertificate)
            .Include(l => l.Owner)
            .Where(l => l.SubdomainSlug != null && l.SubdomainSlug != "")
            .ToListAsync(ct);
        var merged = rows.Concat(subdomainExtra)
            .GroupBy(l => l.Id).Select(g => g.First())
            .OrderByDescending(l => l.CreatedAt)
            .ToList();
        var subBase = await SubdomainBaseAsync(ct);
        return Ok(merged.Select(l => ToDto(l, user.Id, subBase)));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LinkDto>> GetById(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.11.18: analog List() — auch Detail-Abruf für fremde Public-
        // Scope-Links erlauben (read-only; Update/Delete bleiben Owner/Admin
        // over die eigenen Guards weiter unten in Update()/Delete() geschützt).
        // v1.11.22: Admin-Bypass ergänzt (siehe List()).
        var link = await _db.ShareLinks
            .Include(l => l.File).Include(l => l.Folder).Include(l => l.SigningCertificate).Include(l => l.Owner)
            .SingleOrDefaultAsync(l => l.Id == id && (user.Role == UserRole.Admin
                     || l.OwnerId == user.Id
                     || (l.File != null && l.File.Scope == FileScope.Public)
                     || (l.Folder != null && l.Folder.Scope == FileScope.Public)
                     || l.IsPublic), ct);
        return link is null ? NotFound() : Ok(ToDto(link, user.Id, await SubdomainBaseAsync(ct)));
    }

    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        var events = await _db.ShareLinkAccesses
            .Where(a => a.ShareLinkId == id)
            .OrderByDescending(a => a.At)
            .Take(200)
            .Select(a => new { a.At, a.Kind, a.IpHash, a.UserAgent, a.Referer, a.CountryCode })
            .ToListAsync(ct);
        return Ok(new { link.HitCount, link.DownloadCount, link.LastAccessAt, events });
    }

    // v1.10.158: reichere Report-Aggregate für Web + iOS. Ergänzt den alten
    // /stats-Endpoint um Country/City/Device/Timezone-Splits, Peak-Hour-
    // Heatmap und Time-to-Download-Median. StoreFullIp-Flag zeigt der App,
    // ob sie die IP-Spalte einblenden darf.
    public record ReportCountRow(string Key, int Count);
    // v1.11.14: Label (Klartext, z.B. "Microsoft Teams") + IsBot ergänzt,
    // aus RefererClassifier — "Key" bleibt der rohe Host (Feldname
    // unverändert für Abwärtskompatibilität mit älteren iOS-Builds, die
    // dieses Feld einfach ignorieren).
    public record ReportReferrerRow(string Key, string Label, bool IsBot, int Count);
    public record ReportDailyRow(DateOnly Day, int Landings, int Downloads, int PasswordFails);
    public record ReportHeatCell(int DayOfWeek, int Hour, int Count);
    public record ReportEvent(DateTimeOffset At, string Kind, string? CountryCode,
        string? City, string? DeviceType, string? Timezone, string? Referer, string? IpAddress);
    public record ReportResponse(
        Guid LinkId, string Slug, int HitCount, int DownloadCount, int UniqueVisitors,
        double? MedianTimeToDownloadSeconds, DateTimeOffset? LastAccessAt,
        List<ReportDailyRow> ByDay,
        List<ReportCountRow> Countries,
        List<ReportCountRow> Cities,
        List<ReportCountRow> Devices,
        List<ReportCountRow> Timezones,
        List<ReportReferrerRow> Referrers,
        List<ReportHeatCell> HourHeatmap,
        List<ReportEvent> RecentEvents,
        int TotalEventCount,
        bool StoreFullIp);

    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.11.69: fehlte hier — List()/GetById() erlauben Admins + Public-
        // Scope-Ziel + IsPublic + Subdomain-Links schon lange (siehe dort),
        // dieser Endpoint prüfte aber stur nur OwnerId==me. iOS zeigte den
        // Link in "Meine Links" (via List()) korrekt an, der Tap auf den
        // Bericht eines fremden Links warf dann aber 404 ("Nicht gefunden").
        // Web hat dieselbe Regel schon in LinkReportController.Detail().
        var link = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .SingleOrDefaultAsync(l => l.Id == id && (
                user.Role == UserRole.Admin
                || l.OwnerId == user.Id
                || (l.File != null && l.File.Scope == FileScope.Public)
                || (l.Folder != null && l.Folder.Scope == FileScope.Public)
                || l.IsPublic
                || (l.SubdomainSlug != null && l.SubdomainSlug != "")
            ), ct);
        if (link is null) return NotFound();

        var all = await _db.ShareLinkAccesses
            .Where(a => a.ShareLinkId == id)
            .OrderByDescending(a => a.At)
            .ToListAsync(ct);

        var since = DateTimeOffset.UtcNow.Date.AddDays(-29);
        var byDay = new List<ReportDailyRow>();
        for (int d = 0; d < 30; d++)
        {
            var day = DateOnly.FromDateTime(since.AddDays(d));
            byDay.Add(new ReportDailyRow(day, 0, 0, 0));
        }
        foreach (var e in all.Where(e => e.At >= since))
        {
            var day = DateOnly.FromDateTime(e.At.UtcDateTime.Date);
            var idx = byDay.FindIndex(x => x.Day == day);
            if (idx < 0) continue;
            var b = byDay[idx];
            byDay[idx] = e.Kind switch
            {
                ShareLinkAccessKind.Landing => b with { Landings = b.Landings + 1 },
                ShareLinkAccessKind.Download => b with { Downloads = b.Downloads + 1 },
                ShareLinkAccessKind.PasswordFail => b with { PasswordFails = b.PasswordFails + 1 },
                _ => b,
            };
        }

        var unique = all.Select(e => e.IpHash).Where(h => !string.IsNullOrEmpty(h)).Distinct().Count();

        var countries = all.Where(e => !string.IsNullOrEmpty(e.CountryCode))
            .GroupBy(e => e.CountryCode!.ToUpperInvariant())
            .Select(g => new ReportCountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count).Take(10).ToList();
        var cities = all.Where(e => !string.IsNullOrEmpty(e.City))
            .GroupBy(e => e.City!)
            .Select(g => new ReportCountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count).Take(10).ToList();
        var devices = all
            .Select(e => string.IsNullOrEmpty(e.DeviceType) || e.DeviceType == "Unknown" ? "Unknown" : e.DeviceType!)
            .GroupBy(d => d)
            .Select(g => new ReportCountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count).ToList();
        var timezones = all.Where(e => !string.IsNullOrEmpty(e.Timezone))
            .GroupBy(e => e.Timezone!)
            .Select(g => new ReportCountRow(g.Key, g.Count()))
            .OrderByDescending(r => r.Count).Take(10).ToList();
        var referrers = all
            .Select(e => RefererClassifier.Classify(e.Referer, e.UserAgent, e.Isp))
            .Where(c => c is not null)
            .GroupBy(c => c!.Host)
            .Select(g => new ReportReferrerRow(
                g.Key,
                g.OrderByDescending(c => c!.IsLikelyAutomatedFetch).First()!.DisplayLabel,
                g.Any(c => c!.IsLikelyAutomatedFetch),
                g.Count()))
            .OrderByDescending(r => r.Count).Take(8).ToList();

        var heat = new int[7, 24];
        foreach (var e in all.Where(e => e.At >= since && e.Kind != ShareLinkAccessKind.PasswordFail))
            heat[(int)e.At.UtcDateTime.DayOfWeek, e.At.UtcDateTime.Hour]++;
        var heatCells = new List<ReportHeatCell>(7 * 24);
        for (int dow = 0; dow < 7; dow++)
            for (int h = 0; h < 24; h++)
                heatCells.Add(new ReportHeatCell(dow, h, heat[dow, h]));

        double? medianTtdSec = null;
        var deltas = new List<double>();
        foreach (var g in all.GroupBy(e => e.IpHash).Where(g => !string.IsNullOrEmpty(g.Key)))
        {
            var fl = g.Where(e => e.Kind == ShareLinkAccessKind.Landing).OrderBy(e => e.At).FirstOrDefault();
            var fd = g.Where(e => e.Kind == ShareLinkAccessKind.Download).OrderBy(e => e.At).FirstOrDefault();
            if (fl is null || fd is null || fd.At < fl.At) continue;
            deltas.Add((fd.At - fl.At).TotalSeconds);
        }
        if (deltas.Count > 0)
        {
            deltas.Sort();
            medianTtdSec = deltas[deltas.Count / 2];
        }

        var privacySettings = await _db.LinkPrivacySettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var storeFullIp = privacySettings?.StoreFullIp ?? _configStoreFullIp;
        var events = all.Take(200).Select(a => new ReportEvent(
            a.At, a.Kind.ToString(), a.CountryCode, a.City, a.DeviceType, a.Timezone, a.Referer,
            storeFullIp ? a.IpAddress : null)).ToList();

        return Ok(new ReportResponse(
            link.Id, link.Slug, link.HitCount, link.DownloadCount, unique,
            medianTtdSec, link.LastAccessAt, byDay,
            countries, cities, devices, timezones, referrers,
            heatCells, events, all.Count, storeFullIp));
    }

    [HttpGet("{id:guid}/qr.svg")]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct)
    {
        // Auth required — otherwise anyone with a link.Id could learn the slug
        // behind it and check whether that id exists.
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.11.19: List()/GetById() wurden in v1.11.18 gelockert, damit iOS
        // dieselbe Menge wie Web sieht (eigene + fremde Public-Scope-Links +
        // eigene Group-Links) — LinkDto.QrCodeUrl zeigt seither für JEDE
        // dieser Zeilen auf diesen Endpoint. Ohne diese Lockerung wäre der
        // Link für fremde Public-Links ein toter 404 (die QR kodiert ohnehin
        // nur die public /s/{slug}-URL, kein Owner-Geheimnis).
        var link = await _db.ShareLinks
            .SingleOrDefaultAsync(l => l.Id == id && (l.OwnerId == user.Id
                     || (l.File != null && l.File.Scope == FileScope.Public)
                     || (l.Folder != null && l.Folder.Scope == FileScope.Public)
                     || l.IsPublic), ct);
        if (link is null) return NotFound();
        var url = BuildPublicUrl(link.Slug);
        return Content(_qr.RenderSvg(url), "image/svg+xml; charset=utf-8");
    }

    // v1.11.44 — Marcus's Notfall-Idee: manche Kunden können nimshare.com
    // gar nicht erreichen (Firmen-Proxy blockt die Domain, z.B. Zscaler
    // "Miscellaneous or Unknown"-Kategorie). Statt der Landing-Page liefert
    // dieser Endpoint zeitlich befristete Azure-Blob-SAS-Direktlinks — die
    // laufen über *.blob.core.windows.net, eine bei Enterprise-Filtern
    // praktisch immer schon vertraute Microsoft-Domain. Reine Notfall-
    // Umgehung: gesperrt für passwortgeschützte Links (SAS umgeht den
    // Passwortschutz komplett), kein Tracking/Reporting auf diesem Pfad.
    // Bei Ordner-Links nur die oberste Ebene (Marcus's Entscheidung — 90%
    // der Fälle sind flache 2-3-Datei-Ordner, Unterordner sind selten genug
    // dass der Sender die Datei sonst einzeln nachreichen kann).
    public record EmergencyFileDto(Guid Id, string Name, long SizeBytes, string Url);
    public record EmergencyDownloadResponse(IReadOnlyList<EmergencyFileDto> Files);

    [HttpPost("{id:guid}/emergency-download")]
    public async Task<IActionResult> EmergencyDownload(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = user.Role == UserRole.Admin
            ? await _db.ShareLinks.Include(l => l.File).Include(l => l.Folder)
                .SingleOrDefaultAsync(l => l.Id == id, ct)
            : await _db.ShareLinks.Include(l => l.File).Include(l => l.Folder)
                .SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        if (link.PasswordHash is not null)
            return Problem(statusCode: 422, title: "Notfall-Download nicht möglich: Link ist passwortgeschützt.");

        var ttl = TimeSpan.FromHours(48);
        var files = new List<EmergencyFileDto>();
        if (link.File is { DeletedAt: null } file)
        {
            var sas = _blobs.CreateDownloadSas(file.BlobPath, file.Name, file.ContentType, ttl);
            files.Add(new EmergencyFileDto(file.Id, file.Name, file.SizeBytes, sas.ToString()));
        }
        else if (link.FolderId is not null)
        {
            var children = await _db.Files
                .Where(f => f.FolderId == link.FolderId && f.DeletedAt == null)
                .OrderBy(f => f.Name)
                .ToListAsync(ct);
            foreach (var f in children)
            {
                var sas = _blobs.CreateDownloadSas(f.BlobPath, f.Name, f.ContentType, ttl);
                files.Add(new EmergencyFileDto(f.Id, f.Name, f.SizeBytes, sas.ToString()));
            }
        }
        return Ok(new EmergencyDownloadResponse(files));
    }

    public record UpdateLinkRequest(DateTimeOffset? ExpiresAt, int? MaxDownloads, string? Message, bool? IsRevoked, bool? NotifyOnAccess, bool? IsPublic, string? AllowedEmails, bool? RequireEmailVerify,
        // v1.11.18: analog AllowedEmails — leerer String löscht die Seriennummer,
        // null = unverändert lassen.
        string? SerialNumber = null,
        // v1.11.22: analog. null = unverändert lassen.
        bool? KeyStoreMode = null,
        // v1.11.44: DocumentationEnabled statt DocumentationUrl.
        bool? DocumentationEnabled = null,
        // v1.11.50: null = unverändert. true → ExpiresAt wird gelöscht
        // (nie ablaufen); false → falls ExpiresAt dabei auch null ist, wird
        // wieder auf +8 Wochen ab jetzt gesetzt statt versehentlich permanent
        // zu bleiben.
        bool? IsPermanent = null);

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLinkRequest req, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.10.97: Admin darf auch fremde Links moderieren (revoke/delete/…).
        // Marcus's Report: „als Admin auch öffentlich Links löschen dürfen".
        var link = user.Role == UserRole.Admin
            ? await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id, ct)
            : await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        // v1.11.19: siehe Create() — gleiche Längenprüfung vor Protect().
        if (req.SerialNumber is { Length: > 1000 })
            return Problem(statusCode: 422, title: "Serial number too long (max 1000 characters).");
        if (req.ExpiresAt is not null) link.ExpiresAt = req.ExpiresAt;
        // v1.11.50: Permanent-Umschalter. true → ExpiresAt raus. false →
        // wenn dabei kein eigenes ExpiresAt mitkam und der Link bisher
        // permanent war, auf +8 Wochen ab jetzt zurückfallen (sonst bliebe
        // ExpiresAt weiter null und der Link liefe trotz "nicht permanent"
        // nie ab).
        if (req.IsPermanent is not null)
        {
            link.IsPermanent = req.IsPermanent.Value;
            if (req.IsPermanent.Value) link.ExpiresAt = null;
            else if (req.ExpiresAt is null && link.ExpiresAt is null) link.ExpiresAt = DateTimeOffset.UtcNow.AddDays(56);
        }
        if (req.MaxDownloads is not null) link.MaxDownloads = req.MaxDownloads;
        if (req.Message is not null) link.Message = req.Message;
        if (req.IsRevoked is not null) link.IsRevoked = req.IsRevoked.Value;
        if (req.NotifyOnAccess is not null) link.NotifyOnAccess = req.NotifyOnAccess.Value;
        // "Public for everyone" is admin-only. Any other user attempting to
        // set it just gets silently ignored — no 403 to avoid a leaky UX.
        if (req.IsPublic is not null && user.Role == UserRole.Admin)
            link.IsPublic = req.IsPublic.Value;
        if (req.AllowedEmails is not null)
            link.AllowedEmails = string.IsNullOrWhiteSpace(req.AllowedEmails) ? null : req.AllowedEmails.Trim();
        if (req.RequireEmailVerify is not null) link.RequireEmailVerify = req.RequireEmailVerify.Value;
        // v1.11.20: für File- UND Folder-Links (siehe Create()-Korrektur).
        if (req.SerialNumber is not null)
            link.SerialNumberEncrypted = string.IsNullOrWhiteSpace(req.SerialNumber)
                ? null : _serialProtector.Protect(req.SerialNumber.Trim());
        if (req.KeyStoreMode is not null) link.KeyStoreMode = req.KeyStoreMode.Value;
        if (req.DocumentationEnabled is not null) link.DocumentationEnabled = req.DocumentationEnabled.Value;
        // v1.11.22: gegenseitiger Ausschluss auch beim Update erzwingen.
        if (link.KeyStoreMode) link.SerialNumberEncrypted = null;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(link, user.Id, await SubdomainBaseAsync(ct)));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.10.97: Admin darf auch fremde Links löschen (Moderation).
        var link = user.Role == UserRole.Admin
            ? await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id, ct)
            : await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        var linkId = link.Id;   // vor Remove kopieren, EF nullt evtl. den PK
        _db.ShareLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
        // v1.10.183: Album-ZIP-Cache mit weggeräumen — sonst würden verwaiste
        // ZIPs im Blob-Container schwellen.
        var zipCache = HttpContext.RequestServices.GetService<IAlbumZipCache>();
        if (zipCache is not null)
            _ = zipCache.DeleteAsync(linkId, CancellationToken.None);
        return NoContent();
    }

    // ── v1.11.18: Öffentliche Seriennummer-Endpoints ────────────────────
    // Anonym erreichbar (Landing-Seite ruft sie per fetch auf, kein Login).
    // Zugriff wird trotzdem geprüft: Passwort (falls gesetzt) + AllowedEmails-
    // Session-Gate (falls gesetzt) — exakt dieselben Regeln wie der Download
    // selbst, damit die Seriennummer nicht mehr preisgibt als die Datei.
    private bool SerialAccessOk(ShareLink link, string? password)
    {
        if (!string.IsNullOrWhiteSpace(link.AllowedEmails)
            && HttpContext.Session.GetString($"gate.{link.Slug}") != "ok")
            return false;
        if (link.PasswordHash is null) return true;
        if (HttpContext.Session.GetString($"gate.{link.Slug}") == "ok") return true;
        return !string.IsNullOrEmpty(password) && _hasher.Verify(password, link.PasswordHash);
    }

    public record SerialRevealRequest(string? Password);
    public record SerialRevealResponse(string SerialNumber);

    // v1.11.19: Review-Fund — dieser Endpoint erlaubte einen Passwort-
    // Brute-Force ohne jedes Limit, während der eigentliche Download-Pfad
    // (ShareController) klassenweit mit [EnableRateLimiting("public-share")]
    // gedeckelt ist. Gleiche Policy hier ergänzt, sonst wäre die Serial-
    // Reveal-Route ein ungedrosseltes Orakel für `link.PasswordHash`.
    [AllowAnonymous]
    [EnableRateLimiting("public-share")]
    [HttpPost("public/{slug}/serial/reveal")]
    public async Task<IActionResult> RevealSerial(string slug, [FromBody] SerialRevealRequest req,
        [FromServices] ILinkAccessService access, [FromServices] IIpHashService iphash, CancellationToken ct)
    {
        var link = await access.FindActiveAsync(slug, ct);
        // v1.11.20: File- UND Folder-Links (siehe LinksController.Create()-
        // Korrektur — die File-only-Beschränkung von v1.11.19 war ein Bug).
        if (link is null || !link.IsActive(DateTimeOffset.UtcNow) || link.SerialNumberEncrypted is null)
            return NotFound();
        if (!SerialAccessOk(link, req.Password))
            return Problem(statusCode: 403, title: "Access denied");

        string plain;
        try { plain = _serialProtector.Unprotect(link.SerialNumberEncrypted); }
        catch (System.Security.Cryptography.CryptographicException)
        { return Problem(statusCode: 500, title: "Serial number could not be decrypted"); }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        await access.LogAsync(link, ShareLinkAccessKind.SerialRevealed, iphash.Hash(ip),
            Request.Headers.UserAgent, Request.Headers.Referer, ct);
        return Ok(new SerialRevealResponse(plain));
    }

    public record SerialEmailRequest(string ToEmail, string? Password);

    // v1.11.19: Review-Fund — ohne Rate-Limit war dieser Endpoint zusätzlich
    // als offenes Mail-Relay missbrauchbar (beliebige ToEmail-Werte in
    // Schleife, kein Passwort nötig bei passwortlosen Links). Gleiche Policy
    // wie RevealSerial deckelt beide Angriffsflächen.
    [AllowAnonymous]
    [EnableRateLimiting("public-share")]
    [HttpPost("public/{slug}/serial/email")]
    public async Task<IActionResult> EmailSerial(string slug, [FromBody] SerialEmailRequest req,
        [FromServices] ILinkAccessService access, [FromServices] IIpHashService iphash,
        [FromServices] INotificationService notify, CancellationToken ct)
    {
        var link = await access.FindActiveAsync(slug, ct);
        // v1.11.20: File- UND Folder-Links (siehe RevealSerial-Korrektur).
        if (link is null || !link.IsActive(DateTimeOffset.UtcNow) || link.SerialNumberEncrypted is null)
            return NotFound();
        if (!SerialAccessOk(link, req.Password))
            return Problem(statusCode: 403, title: "Access denied");
        if (string.IsNullOrWhiteSpace(req.ToEmail) || !req.ToEmail.Contains('@'))
            return Problem(statusCode: 422, title: "Invalid recipient email");

        string plain;
        try { plain = _serialProtector.Unprotect(link.SerialNumberEncrypted); }
        catch (System.Security.Cryptography.CryptographicException)
        { return Problem(statusCode: 500, title: "Serial number could not be decrypted"); }

        var itemName = link.File?.Name ?? link.Folder?.Name ?? "Download";
        var subject = $"Deine Seriennummer für {itemName}";
        var body = $"""
                    Hallo,

                    hier ist die angeforderte Seriennummer für "{itemName}":

                    {plain}

                    — NimShare
                    """;
        await notify.SendShareLinkAsync(req.ToEmail.Trim(), "NimShare", subject, body, ct);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        await access.LogAsync(link, ShareLinkAccessKind.SerialEmailed, iphash.Hash(ip),
            Request.Headers.UserAgent, Request.Headers.Referer, ct);
        return Ok(new { sent = true });
    }

    // ── v1.11.22: Öffentliche Key-Store-Lookup-Endpoints ────────────────
    // Für Links im Lizenzschlüssel-Modus (KeyStoreMode=true). Der Besucher
    // gibt seine Email ein — sie dient GLEICHZEITIG als Identifikation (wer
    // bekommt welchen Key) UND als einziges mögliche Ziel für den Email-
    // Versand (bewusst kein separates "an"-Feld: sonst könnte man die Email
    // eines fremden Kunden eintippen und sich dessen Key an die EIGENE
    // Adresse schicken lassen).
    public record KeyStoreLookupRequest(string Email, string? Password);
    public record KeyStoreDocLinkDto(string Label, string Url, bool IsFile);
    public record KeyStoreLookupResponse(string KeyValue, string KeyType,
        DateTimeOffset? ValidUntil, IReadOnlyList<KeyStoreDocLinkDto> Documents);

    private async Task<KeyStoreEntry?> FindKeyStoreMatchAsync(Guid ownerId, string email, CancellationToken ct)
    {
        var needle = email.Trim().ToLowerInvariant();
        if (!needle.Contains('@')) return null;
        var domain = needle.Split('@')[1];
        // Exakter Email-Treffer geht vor Domain-Wildcard (spezifischer Kunde
        // vor "irgendwer aus dieser Firma").
        var exact = await _db.KeyStoreEntries
            .SingleOrDefaultAsync(k => k.OwnerUserId == ownerId && k.CustomerEmail == needle, ct);
        if (exact is not null) return exact;
        return await _db.KeyStoreEntries
            .Where(k => k.OwnerUserId == ownerId && k.CustomerEmailDomain == domain)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>v1.11.37 — Marcus: Doku-Dokumente (PDFs/feste Links wie der
    /// "Tenant"-Login) erscheinen NUR, wenn die "Dokumentation"-Checkbox beim
    /// Link aktiviert wurde (link.DocumentationEnabled) UND ihre Key-Typ-
    /// Auswahl exakt zum beim Reveal ermittelten KeyStoreEntry.KeyType passt.
    /// File-Dokumente bekommen eine kurzlebige Download-SAS statt eines
    /// dauerhaften öffentlichen Links.</summary>
    private async Task<List<KeyStoreDocLinkDto>> FindMatchingDocumentsAsync(ShareLink link, string keyType, CancellationToken ct)
    {
        if (!link.DocumentationEnabled) return new();
        var docs = await _db.KeyStoreDocuments.Where(d => d.OwnerUserId == link.OwnerId).ToListAsync(ct);
        return docs.Where(d => d.AppliesTo(keyType)).Select(d => new KeyStoreDocLinkDto(
            d.Label,
            d.IsFile
                ? _blobs.CreateDownloadSas(d.BlobPath!, d.FileName ?? d.Label, GuessContentType(d.FileName), TimeSpan.FromMinutes(15)).ToString()
                : d.Url!,
            d.IsFile)).ToList();
    }

    private static string GuessContentType(string? fileName) =>
        System.IO.Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream",
        };

    [AllowAnonymous]
    [EnableRateLimiting("public-share")]
    [HttpPost("public/{slug}/keystore/reveal")]
    public async Task<IActionResult> RevealKeyStoreKey(string slug, [FromBody] KeyStoreLookupRequest req,
        [FromServices] ILinkAccessService access, [FromServices] IIpHashService iphash, CancellationToken ct)
    {
        var link = await access.FindActiveAsync(slug, ct);
        if (link is null || !link.IsActive(DateTimeOffset.UtcNow) || !link.KeyStoreMode) return NotFound();
        if (!SerialAccessOk(link, req.Password))
            return Problem(statusCode: 403, title: "Access denied");
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return Problem(statusCode: 422, title: "Invalid email");

        var match = await FindKeyStoreMatchAsync(link.OwnerId, req.Email, ct);
        if (match is null) return NotFound();

        string plain;
        try { plain = _keyStoreProtector.Unprotect(match.KeyValueEncrypted); }
        catch (System.Security.Cryptography.CryptographicException)
        { return Problem(statusCode: 500, title: "Key could not be decrypted"); }

        var documents = await FindMatchingDocumentsAsync(link, match.KeyType, ct);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        await access.LogAsync(link, ShareLinkAccessKind.KeyStoreRevealed, iphash.Hash(ip),
            Request.Headers.UserAgent, Request.Headers.Referer, ct);
        return Ok(new KeyStoreLookupResponse(plain, match.KeyType, match.ValidUntil, documents));
    }

    [AllowAnonymous]
    [EnableRateLimiting("public-share")]
    [HttpPost("public/{slug}/keystore/email")]
    public async Task<IActionResult> EmailKeyStoreKey(string slug, [FromBody] KeyStoreLookupRequest req,
        [FromServices] ILinkAccessService access, [FromServices] IIpHashService iphash, CancellationToken ct)
    {
        var link = await access.FindActiveAsync(slug, ct);
        if (link is null || !link.IsActive(DateTimeOffset.UtcNow) || !link.KeyStoreMode) return NotFound();
        if (!SerialAccessOk(link, req.Password))
            return Problem(statusCode: 403, title: "Access denied");
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return Problem(statusCode: 422, title: "Invalid email");

        var match = await FindKeyStoreMatchAsync(link.OwnerId, req.Email, ct);
        if (match is null) return NotFound();

        string plain;
        try { plain = _keyStoreProtector.Unprotect(match.KeyValueEncrypted); }
        catch (System.Security.Cryptography.CryptographicException)
        { return Problem(statusCode: 500, title: "Key could not be decrypted"); }

        var itemName = link.File?.Name ?? link.Folder?.Name ?? "Download";
        var documents = await FindMatchingDocumentsAsync(link, match.KeyType, ct);

        // v1.11.37 — Marcus: die Mail soll in der Sprache raus, in der der
        // Besucher die Landing gerade sieht (CultureInfo.CurrentUICulture ist
        // für diesen Request bereits durch die RequestLocalization-Middleware
        // auf die vom Besucher gewählte Sprache gesetzt — dasselbe Cookie wie
        // beim Landing-Seitenaufruf, siehe AiController.CurrentLanguageIso).
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.IsNullOrEmpty(lang) || lang == "iv") lang = "en";

        // v1.11.37 — falls der Besitzer eine eigene Vorlage für diese Sprache
        // als Default markiert hat (Kind=KeyStoreDelivery), wird DIE gerendert
        // statt des hartkodierten Fallback-Texts.
        var template = await _db.EmailTemplates.SingleOrDefaultAsync(t =>
            t.OwnerUserId == link.OwnerId && t.Kind == EmailTemplateKind.KeyStoreDelivery
            && t.Locale == lang && t.IsDefault, ct);

        string subject, body;
        if (template is not null)
        {
            var ctx = new Dictionary<string, string?>
            {
                ["customer.name"] = match.CustomerName,
                ["key.type"] = match.KeyType,
                ["key.value"] = plain,
                ["item.name"] = itemName,
                ["sender.name"] = link.Owner?.DisplayName ?? "",
                ["recipient.email"] = req.Email.Trim(),
            };
            subject = EmailTemplateRenderer.Render(template.Subject, ctx);
            body = EmailTemplateRenderer.Render(template.BodyMarkdown, ctx);
        }
        else
        {
            subject = _l["email.keystore.subject", itemName].Value;
            body = string.Join("\n", new[]
            {
                _l["email.keystore.greeting"].Value,
                "",
                _l["email.keystore.intro", match.KeyType, itemName].Value,
                "",
                plain,
            });
        }
        if (documents.Count > 0)
        {
            body += $"\n\n{_l["email.keystore.documents_heading"].Value}";
            foreach (var d in documents)
                body += d.IsFile ? $"\n📎 {d.Label}" : $"\n🔗 {d.Label}: {d.Url}";
        }
        body += "\n\n— NimShare";

        // v1.11.37 — PDF-Dokumente werden zusätzlich zum Text-Link direkt
        // angehängt (Marcus's Wunsch), damit der Empfänger nicht extra
        // klicken muss. Feste Links (z.B. Tenant-Login) bleiben Text-Links.
        List<EmailAttachment>? attachments = null;
        var fileDocs = await _db.KeyStoreDocuments
            .Where(d => d.OwnerUserId == link.OwnerId && d.BlobPath != null)
            .ToListAsync(ct);
        foreach (var d in fileDocs.Where(d => d.AppliesTo(match.KeyType)))
        {
            try
            {
                using var ms = new MemoryStream();
                await _blobs.DownloadToAsync(d.BlobPath!, ms, ct);
                attachments ??= new List<EmailAttachment>();
                attachments.Add(new EmailAttachment(d.FileName ?? $"{d.Label}.pdf", GuessContentType(d.FileName), ms.ToArray()));
            }
            catch { /* Anhang fehlgeschlagen darf den Mail-Versand nicht blockieren */ }
        }

        // v1.11.22: bewusst an DIESELBE Email, mit der der Key gefunden wurde
        // — kein separates "an"-Feld (siehe Klassen-Kommentar oben).
        await _emailGateway.SendAsync(req.Email.Trim(), subject, body, attachments, ct);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        await access.LogAsync(link, ShareLinkAccessKind.KeyStoreEmailed, iphash.Hash(ip),
            Request.Headers.UserAgent, Request.Headers.Referer, ct);
        return Ok(new { sent = true });
    }

    public record SendByEmailRequest(string ToEmail, string? Message);

    [HttpPost("{id:guid}/send-email")]
    public async Task<IActionResult> SendByEmail(Guid id, [FromBody] SendByEmailRequest req, [FromServices] INotificationService notify, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.Include(l => l.File).Include(l => l.Folder)
            .SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.ToEmail) || !req.ToEmail.Contains('@'))
            return Problem(statusCode: 422, title: "Invalid recipient email");
        var url = BuildPublicUrl(link.Slug);
        var itemName = link.File?.Name ?? link.Folder?.Name ?? "Freigabe";
        var itemKind = link.File is not null ? "a file" : "a folder";
        var subject = $"{user.DisplayName} shared {itemKind} with you: {itemName}";
        var body = $"""
                    Hello,

                    {user.DisplayName} ({user.Email}) has shared {itemKind} with you:

                    {itemName}
                    {url}

                    {(string.IsNullOrWhiteSpace(req.Message) ? "" : "Message from the sender:\n" + req.Message + "\n\n")}
                    — NimShare
                    """;
        await notify.SendShareLinkAsync(req.ToEmail.Trim(), user.DisplayName, subject, body, ct);
        return Ok(new { sent = true });
    }

    private LinkDto ToDto(ShareLink l, Guid currentUserId, string? subdomainBase = null)
    {
        // v1.11.18: gleiche Scope-Klassifikation wie HomeController.Links —
        // "public" wenn Ziel Public-Scope ODER admin-explizit IsPublic,
        // sonst "group" wenn Ziel Group-Scope, sonst "private".
        var isPublicScope = (l.File != null && l.File.Scope == FileScope.Public)
              || (l.Folder != null && l.Folder.Scope == FileScope.Public)
              || l.IsPublic;
        var isGroupScope = (l.File != null && l.File.Scope == FileScope.Group)
              || (l.Folder != null && l.Folder.Scope == FileScope.Group);
        var scope = isPublicScope ? "public" : (isGroupScope ? "group" : "private");
        return new(
            l.Id, l.Slug, BuildPublicUrl(l.Slug), $"/api/v1/links/{l.Id}/qr.svg",
            l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.HitCount,
            l.PasswordHash != null, l.IsRevoked, l.CreatedAt,
            IsPublic: isPublicScope,
            TargetKind: l.File != null ? "file" : (l.Folder != null ? "folder" : null),
            TargetName: l.File?.Name ?? l.Folder?.Name,
            Signer: BuildSignerInfo(l.SigningCertificate),
            FolderIsGallery: l.Folder != null && l.Folder.Kind == FolderKind.Gallery,
            DisplayAsGallery: l.DisplayAsGallery,
            AllowUploads: l.AllowUploads,
            ShowGpsMap: l.ShowGpsMap,
            // v1.11.0: fertige Subdomain-URL, wenn Feature aktiv + Slug gesetzt.
            SubdomainUrl: l.SubdomainSlug != null && subdomainBase != null
                ? $"https://{l.SubdomainSlug}.{subdomainBase}" : null,
            Scope: scope,
            IsOwnedByMe: l.OwnerId == currentUserId,
            OwnerName: l.OwnerId != currentUserId ? l.Owner?.DisplayName : null,
            HasSerialNumber: l.SerialNumberEncrypted != null,
            KeyStoreMode: l.KeyStoreMode,
            DocumentationEnabled: l.DocumentationEnabled,
            IsPermanent: l.IsPermanent);
    }

    /// <summary>v1.11.0 — BaseDomain für DTOs (null wenn Feature aus).</summary>
    private async Task<string?> SubdomainBaseAsync(CancellationToken ct)
    {
        var svc = HttpContext.RequestServices.GetRequiredService<ISubdomainShareService>();
        var s = await svc.GetSettingsAsync(ct);
        return s is { Enabled: true } && !string.IsNullOrEmpty(s.BaseDomain) ? s.BaseDomain : null;
    }

    // v1.10.146: Signer-Info fürs Landing-Badge; nur bei vorhandenem Zertifikat.
    internal static SignerInfo? BuildSignerInfo(SigningCertificate? c)
        => c is null ? null : new SignerInfo(
            c.Id, c.SubjectCommonName, c.Issuer, c.Thumbprint,
            c.NotBefore, c.NotAfter, c.IsSelfIssued);

    private string BuildPublicUrl(string slug)
        => HttpContext.Request.PublicUrl($"/s/{slug}");
}
