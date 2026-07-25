using NimShare.Core.Data;
using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Public share endpoints. These are the URLs that get emailed / IM'd around,
/// so they must be branded, localised, and rule-checked.
/// </summary>
[AllowAnonymous]
[Route("s")]
[EnableRateLimiting("public-share")]
public class ShareController : Controller
{
    private readonly ILinkAccessService _access;
    private readonly IPasswordHasher _hasher;
    private readonly IBlobStorageService _blobs;
    private readonly IIpHashService _iphash;
    private readonly INotificationService _notify;
    private readonly IStringLocalizer<SharedResources> _t;
    private readonly StorageOptions _storage;
    private readonly NimShareDbContext _db;
    private readonly IGeoIpService _geo;

    public ShareController(
        ILinkAccessService access, IPasswordHasher hasher, IBlobStorageService blobs,
        IIpHashService iphash, INotificationService notify,
        IStringLocalizer<SharedResources> t, IOptions<StorageOptions> storage,
        NimShareDbContext db, IGeoIpService geo)
    {
        _access = access;
        _hasher = hasher;
        _blobs = blobs;
        _iphash = iphash;
        _notify = notify;
        _t = t;
        _storage = storage.Value;
        _db = db;
        _geo = geo;
    }

    // v1.10.42 — kleiner Helper: liefert (country, city, device) für den
    // Link-Report. Timezone kommt hier nicht — Landing ist GET, ohne
    // JS-Beacon können wir sie nicht ermitteln.
    private async Task<(string? Country, string? City, string? Device)> LandingForensicsAsync(CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var device = DeviceTypeParser.Classify(ua);
        var (country, city) = await _geo.LookupAsync(ip, ct);
        return (country, city, device);
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Landing(string slug, [FromServices] NimShare.Core.Data.NimShareDbContext db,
        [FromServices] IFolderService folderSvc, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null) return View("NotFound");

        // Folder share: render the mini file-browser landing instead of the file landing.
        if (link.FolderId is Guid folderId && link.FileId is null)
        {
            var now0 = DateTimeOffset.UtcNow;
            if (!link.IsActive(now0)) return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));
            var folder = await db.Folders.FindAsync(new object[] { folderId }, ct);
            if (folder is null) return View("NotFound");
            var files = await folderSvc.ListFilesAsync(folder, ct);
            var lf0 = await LandingForensicsAsync(ct);
            var ip0 = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            await _access.LogAsync(link, ShareLinkAccessKind.Landing,
                _iphash.Hash(ip0), ip0,
                Request.Headers.UserAgent, Request.Headers.Referer,
                lf0.Country, lf0.City, lf0.Device, timezone: null, ct);
            // Folder shares now honour the same template-resolution as file
            // shares: link creator's personal template ALWAYS wins first, then
            // the folder-owner's (Personal-scope only), else Global. Passing
            // Guid.Empty as fileOwnerId forces the (linkOwner != fileOwner)
            // guard so the link creator's brand is checked even for Public
            // folders where OwnerUserId is null (v1.10.7 — previously
            // Public/Group folder shares fell through to Global-only lookup
            // and looked un-themed if no admin-global template existed).
            var folderTheme = await ResolveThemeAsync(folder.Scope,
                folder.OwnerUserId ?? Guid.Empty, link.OwnerId, ct);
            return View("FolderLanding", new FolderLandingViewModel(
                link.Slug, folder.Name, RenderMarkdown(link.Message),
                link.PasswordHash is not null, link.Owner.DisplayName,
                files.Select(f => new FolderLandingFile(f.Id, f.Name, f.SizeBytes, f.ContentType)).ToList(),
                ResolveOwnerAvatar(link.Owner), folderTheme,
                BuildLandingSigner(link.SigningCertificate),
                // v1.10.167: Landing rendert Gallery, wenn der LINK das explizit
                // setzt (Ersteller-Wahl beim Freigeben) ODER der Ordner Kind=
                // Gallery ist. AllowUploads greift nur im Gallery-Modus.
                IsGallery: link.DisplayAsGallery || folder.Kind == FolderKind.Gallery,
                AllowUploads: link.AllowUploads && (link.DisplayAsGallery || folder.Kind == FolderKind.Gallery),
                FolderId: folder.Id));
        }

        if (link.File is null || link.File.Status != StorageFileStatus.Ready)
            return View("NotFound");

        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        // Recipient allow-list gate: if the link has AllowedEmails set, block
        // access until the visitor's email (and optional OTP) has been
        // verified in this session.
        if (!string.IsNullOrWhiteSpace(link.AllowedEmails))
        {
            var gate = HttpContext.Session.GetString($"gate.{link.Slug}");
            if (gate != "ok")
                return View("Gate", new GateViewModel(slug, link.RequireEmailVerify, otpSent: false, error: null));
        }

        // Log the landing hit (fire-and-forget-ish, but awaited so we don't lose it).
        var lf1 = await LandingForensicsAsync(ct);
        var ip1 = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        await _access.LogAsync(link, ShareLinkAccessKind.Landing,
            _iphash.Hash(ip1), ip1,
            Request.Headers.UserAgent, Request.Headers.Referer,
            lf1.Country, lf1.City, lf1.Device, timezone: null, ct);

        var theme = await ResolveThemeAsync(link.File.Scope, link.File.OwnerId, link.OwnerId, ct);
        return View("Landing", new LandingViewModel(
            link.Slug,
            link.File.Name,
            link.File.SizeBytes,
            link.File.ContentType,
            RenderMarkdown(link.Message),
            link.PasswordHash is not null,
            link.MaxDownloads,
            link.DownloadCount,
            link.ExpiresAt,
            link.Owner.DisplayName,
            theme,
            ResolveOwnerAvatar(link.Owner),
            BuildLandingSigner(link.SigningCertificate)));
    }

    // v1.10.146: Zertifikats-Infos für Landing-Badge extrahieren.
    internal static LandingSignerInfo? BuildLandingSigner(NimShare.Core.Entities.SigningCertificate? c)
        => c is null ? null : new LandingSignerInfo(
            c.SubjectCommonName, c.Issuer, c.Thumbprint,
            c.NotBefore, c.NotAfter, c.IsSelfIssued);

    /// <summary>Returns the owner's avatar URL for public rendering, but only
    /// when they've opted in via profile settings. Prefers the uploaded blob
    /// (served through /avatars/{userId}) over any external AvatarUrl.</summary>
    private static string? ResolveOwnerAvatar(NimShare.Core.Entities.User owner)
    {
        if (owner is null || !owner.ShowAvatarOnLandings) return null;
        if (!string.IsNullOrEmpty(owner.AvatarBlobPath)) return $"/avatars/{owner.Id:N}";
        return string.IsNullOrEmpty(owner.AvatarUrl) ? null : owner.AvatarUrl;
    }

    /// <summary>
    /// Pick the applicable landing-template snapshot. Preference order:
    /// (1) LINK CREATOR's personal template — lets user B publish a Public
    ///     file under their own branding without duplicating the blob (v1.10.2
    ///     "A" fix per user request). This unlocks the reuse-Public-in-Personal
    ///     use case with zero storage cost.
    /// (2) File-scope template — Personal → file-owner's personal template;
    ///     Public/Group → global admin template. Historical fallback that
    ///     still matches direct-owner-shares.
    /// A missing template returns an empty theme so the view falls back to
    /// the built-in NimShare look.
    /// </summary>
    private async Task<LandingTheme> ResolveThemeAsync(
        NimShare.Core.Entities.FileScope scope, Guid fileOwnerId, Guid linkOwnerId, CancellationToken ct)
    {
        NimShare.Core.Entities.LandingTemplate? t = null;
        // Only look for the link-creator's template if they are NOT the file
        // owner (otherwise it's the same lookup as path 2's Personal branch,
        // saved a DB round-trip).
        if (linkOwnerId != fileOwnerId)
        {
            t = await _db.LandingTemplates.FirstOrDefaultAsync(x =>
                x.Scope == NimShare.Core.Entities.LandingTemplateScope.UserPersonal && x.OwnerUserId == linkOwnerId, ct);
        }
        if (t is null)
        {
            t = scope == NimShare.Core.Entities.FileScope.Personal
                ? await _db.LandingTemplates.FirstOrDefaultAsync(x =>
                    x.Scope == NimShare.Core.Entities.LandingTemplateScope.UserPersonal && x.OwnerUserId == fileOwnerId, ct)
                : await _db.LandingTemplates.FirstOrDefaultAsync(x =>
                    x.Scope == NimShare.Core.Entities.LandingTemplateScope.Global, ct);
        }
        return new LandingTheme(
            t?.Title, t?.Subtitle, t?.BodyMarkdown, t?.FooterText,
            t?.PrimaryColor, t?.LogoUrl, t?.HeroUrl);
    }

    /// <summary>Inline preview stream (image or pdf). Only for password-less links —
    /// otherwise the download page still gates the file behind the password prompt.</summary>
    // v1.10.48 — kleiner Beacon-Endpoint. Landing.cshtml postet nach dem
    // Rendern per fetch die Browser-Timezone hierhin; wir schreiben sie
    // auf die letzte Landing-Access-Zeile dieser (slug, ipHash). Keine
    // AntiForgery (wäre für einen Beacon Overkill), nur Timezone-String
    // wird validiert & auf 60 Zeichen begrenzt.
    public record BeaconTz(string Timezone);

    [HttpPost("{slug}/beacon")]
    public async Task<IActionResult> Beacon(string slug, [FromBody] BeaconTz body, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null) return NotFound();
        var ipHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        await _access.StampTimezoneOnLatestLandingAsync(link, ipHash, body?.Timezone ?? "", ct);
        return NoContent();
    }

    // v1.10.153: Public download des Absender-Zertifikats (Stufe 1). Der
    // Empfänger klickt auf der Landing „Zertifikat herunterladen" und
    // vergleicht Fingerprint bzw. importiert es in seinen Trust-Store.
    [HttpGet("{slug}/signer-cert.cer")]
    public async Task<IActionResult> SignerCert(string slug, [FromServices] ISignerCertReader reader, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.SigningCertificate is null) return NotFound();
        var der = reader.GetPublicDer(link.SigningCertificate);
        var fname = SafeFileName(link.SigningCertificate.SubjectCommonName) + ".cer";
        return File(der, "application/x-x509-user-cert", fname);
    }

    private static string SafeFileName(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray();
        var cleaned = new string(chars);
        return string.IsNullOrEmpty(cleaned) ? "signer" : cleaned;
    }

    [HttpGet("{slug}/preview")]
    public async Task<IActionResult> Preview(string slug, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.File is null || link.File.Status != StorageFileStatus.Ready) return NotFound();
        if (link.PasswordHash is not null) return Forbid();
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return NotFound();
        var ct2 = (link.File.ContentType ?? "").ToLowerInvariant();
        // v1.10.83: Video + Audio in Preview freigegeben. Azure Blob Storage
        // beantwortet Range-Requests nativ, das <video>/<audio>-Tag im Browser
        // fetcht die Chunks direkt vom Storage — kein Byte geht durch die App.
        // Damit auch Seek/Fast-Forward funktioniert und der Vorschau-Player
        // sofort losspielen kann, ohne das ganze File vorher zu laden.
        var isPreviewable = ct2.StartsWith("image/")
            || ct2 == "application/pdf"
            || ct2.StartsWith("video/")
            || ct2.StartsWith("audio/");
        if (!isPreviewable) return BadRequest();
        var sas = _blobs.CreateInlineSas(link.File.BlobPath, link.File.ContentType);
        return Redirect(sas.ToString());
    }

    // ── Recipient allow-list gate ─────────────────────────────────────
    [HttpPost("{slug}/gate/email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GateEmail(string slug, string? email,
        [FromServices] INotificationService notify, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null) return View("NotFound");
        if (string.IsNullOrWhiteSpace(link.AllowedEmails)) return RedirectToAction(nameof(Landing), new { slug });
        var e = (email ?? "").Trim().ToLowerInvariant();
        if (!IsEmailAllowed(e, link.AllowedEmails))
            return View("Gate", new GateViewModel(slug, link.RequireEmailVerify, false, "Diese E-Mail ist für den Download nicht zugelassen."));

        if (link.RequireEmailVerify)
        {
            // Draw a 6-digit OTP, stash it in Session + email it.
            var otp = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1_000_000).ToString();
            HttpContext.Session.SetString($"gate.{link.Slug}.otp", otp);
            HttpContext.Session.SetString($"gate.{link.Slug}.email", e);
            try
            {
                await notify.SendShareLinkAsync(e, "NimShare", "Dein Zugangs-Code",
                    $"Dein Zugangs-Code für den Download: {otp}\n\nGültig für 10 Minuten.", ct);
            }
            catch { /* still show the OTP prompt — an admin can look at server logs */ }
            return View("Gate", new GateViewModel(slug, true, otpSent: true, error: null));
        }
        HttpContext.Session.SetString($"gate.{link.Slug}", "ok");
        return RedirectToAction(nameof(Landing), new { slug });
    }

    [HttpPost("{slug}/gate/otp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GateOtp(string slug, string? code, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null) return View("NotFound");
        var expected = HttpContext.Session.GetString($"gate.{link.Slug}.otp");
        var email = HttpContext.Session.GetString($"gate.{link.Slug}.email");
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(email))
            return RedirectToAction(nameof(Landing), new { slug });
        if ((code ?? "").Trim() != expected)
            return View("Gate", new GateViewModel(slug, true, otpSent: true, error: "Falscher Code."));
        HttpContext.Session.Remove($"gate.{link.Slug}.otp");
        HttpContext.Session.SetString($"gate.{link.Slug}", "ok");
        return RedirectToAction(nameof(Landing), new { slug });
    }

    private static bool IsEmailAllowed(string email, string allowed)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return false;
        var domain = email.Split('@')[1];
        foreach (var raw in allowed.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pat = raw.Trim().ToLowerInvariant();
            if (pat.Length == 0) continue;
            if (pat == email) return true;
            // "*.acme.com" or "@acme.com" or "*@acme.com" all mean "any @acme.com".
            if (pat.StartsWith("@") && domain == pat[1..]) return true;
            if (pat.StartsWith("*@") && domain == pat[2..]) return true;
            if (pat.StartsWith("*.") && domain == pat[2..]) return true;
        }
        return false;
    }

    [HttpPost("{slug}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(string slug, string? password, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.File is null || link.File.Status != StorageFileStatus.Ready) return View("NotFound");
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        var ipHash = _iphash.Hash(ip);
        var lfDl = await LandingForensicsAsync(ct);
        if (link.PasswordHash is not null && !_hasher.Verify(password ?? "", link.PasswordHash))
        {
            await _access.LogAsync(link, ShareLinkAccessKind.PasswordFail,
                ipHash, ip, Request.Headers.UserAgent, Request.Headers.Referer,
                lfDl.Country, lfDl.City, lfDl.Device, timezone: null, ct);
            TempData["PasswordError"] = _t["share.password.error"].Value;
            return RedirectToAction(nameof(Landing), new { slug });
        }

        if (!await _access.TryConsumeDownloadAsync(link, ct))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        await _access.LogAsync(link, ShareLinkAccessKind.Download,
            ipHash, ip, Request.Headers.UserAgent, Request.Headers.Referer,
            lfDl.Country, lfDl.City, lfDl.Device, timezone: null, ct);

        await _notify.NotifyDownloadAsync(link, ipHash, ct);

        var sas = _blobs.CreateDownloadSas(link.File.BlobPath, link.File.Name, link.File.ContentType);
        return Redirect(sas.ToString());
    }

    /// <summary>Per-file download from within a folder share.</summary>
    [HttpPost("{slug}/f/{fileId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadFolderFile(string slug, Guid fileId, string? password,
        [FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.FolderId is null) return View("NotFound");
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        var ipFf = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        var ipHash = _iphash.Hash(ipFf);
        var lfFf = await LandingForensicsAsync(ct);
        if (link.PasswordHash is not null && !_hasher.Verify(password ?? "", link.PasswordHash))
        {
            await _access.LogAsync(link, ShareLinkAccessKind.PasswordFail, ipHash, ipFf, Request.Headers.UserAgent, Request.Headers.Referer,
                lfFf.Country, lfFf.City, lfFf.Device, timezone: null, ct);
            TempData["PasswordError"] = _t["share.password.error"].Value;
            return RedirectToAction(nameof(Landing), new { slug });
        }
        // Verify the file is actually in that folder.
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId && f.FolderId == link.FolderId && f.Status == StorageFileStatus.Ready, ct);
        if (file is null) return View("NotFound");

        if (!await _access.TryConsumeDownloadAsync(link, ct))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));
        await _access.LogAsync(link, ShareLinkAccessKind.Download, ipHash, ipFf, Request.Headers.UserAgent, Request.Headers.Referer,
            lfFf.Country, lfFf.City, lfFf.Device, timezone: null, ct);
        await _notify.NotifyDownloadAsync(link, ipHash, ct);
        var sas = _blobs.CreateDownloadSas(file.BlobPath, file.Name, file.ContentType);
        return Redirect(sas.ToString());
    }

    private static string RenderMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().UseSoftlineBreakAsHardlineBreak().Build();
        return Markdown.ToHtml(md, pipeline);
    }

    // ── v1.10.167: Gallery-Landing-Endpoints ────────────────────────────
    // Preview-Redirect für einzelne Fotos/Videos aus dem Album. Analog zum
    // File-Landing-Preview, aber mit expliziter fileId + Folder-Match, damit
    // niemand einen fremden File-Guid über einen Album-Link durchreichen kann.
    [HttpGet("{slug}/media/{fileId:guid}")]
    public async Task<IActionResult> GalleryMedia(string slug, Guid fileId,
        [FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.FolderId is null) return NotFound();
        if (link.PasswordHash is not null) return Forbid();
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return NotFound();
        var file = await db.Files.SingleOrDefaultAsync(
            f => f.Id == fileId && f.FolderId == link.FolderId && f.Status == StorageFileStatus.Ready, ct);
        if (file is null) return NotFound();
        var sas = _blobs.CreateInlineSas(file.BlobPath, file.ContentType ?? "application/octet-stream");
        return Redirect(sas.ToString());
    }

    // Gallery-Upload — nur für Album-Links mit AllowUploads=true. Legt einen
    // StorageFile-Pending an, gibt eine SAS-UploadUrl zurück, und ein Complete-
    // Endpoint stampt danach den Blob als Ready. Kein Auth nötig (öffentlicher
    // Album-Upload), aber hart gegen Missbrauch gehärtet:
    //   * Content-Type-Whitelist (image/*, video/*) — kein PDF, keine .exe
    //   * SizeBytes ≤ 100 MB pro File (Hochzeits-Foto-Ceiling)
    //   * Password-gate wird respektiert (analog zu Download)
    //   * Blob-Pfad enthält guest-Prefix, damit Owner erkennt was Gäste hochgeladen haben
    public record GalleryUploadInitReq(string Name, long SizeBytes, string ContentType);
    public record GalleryUploadInitResp(Guid FileId, string UploadUrl, DateTimeOffset ExpiresAt);

    [HttpPost("{slug}/gallery-upload/init")]
    public async Task<IActionResult> GalleryUploadInit(string slug, [FromBody] GalleryUploadInitReq req,
        [FromServices] NimShare.Core.Data.NimShareDbContext db,
        [FromServices] IFolderService folderSvc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest();
        if (req.SizeBytes <= 0 || req.SizeBytes > 100L * 1024 * 1024)
            return Problem(statusCode: 413, title: _t["gallery.upload.too_large"].Value);
        var ct2 = (req.ContentType ?? "").ToLowerInvariant();
        var isMedia = ct2.StartsWith("image/") || ct2.StartsWith("video/");
        if (!isMedia) return Problem(statusCode: 415, title: _t["gallery.upload.only_media"].Value);
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.FolderId is null || !link.AllowUploads) return NotFound();
        // v1.10.167: AllowUploads gilt nur wenn der Link im Gallery-Modus ist —
        // sonst wäre die Landing eine Datei-Liste ohne Upload-Widget und der
        // Endpoint offen. Erlaube DisplayAsGallery ODER Folder.Kind==Gallery.
        if (link.PasswordHash is not null) return Forbid();
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return NotFound();
        var folder = await db.Folders.FindAsync(new object[] { link.FolderId.Value }, ct);
        if (folder is null) return NotFound();
        // Gallery-Modus muss AKTIV sein — entweder per-Link oder per-Ordner.
        if (!link.DisplayAsGallery && folder.Kind != FolderKind.Gallery) return NotFound();

        var file = new StorageFile
        {
            OwnerId = link.OwnerId,
            Scope = folder.Scope,
            GroupId = folder.OwnerGroupId,
            FolderId = folder.Id,
            Name = SanitiseUploadFilename(req.Name),
            SizeBytes = req.SizeBytes,
            ContentType = req.ContentType ?? "application/octet-stream",
            Folder = "",
            Status = StorageFileStatus.Pending,
        };
        file.BlobPath = $"users/{link.OwnerId:N}/gallery-guest/{file.Id:N}/{SanitiseUploadFilename(req.Name)}";
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);
        var ticket = _blobs.CreateUploadTicket(file.BlobPath);
        return Ok(new GalleryUploadInitResp(file.Id, ticket.UploadUrl.ToString(), ticket.ExpiresAt));
    }

    [HttpPost("{slug}/gallery-upload/{fileId:guid}/complete")]
    public async Task<IActionResult> GalleryUploadComplete(string slug, Guid fileId,
        [FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.FolderId is null || !link.AllowUploads) return NotFound();
        // v1.10.167: AllowUploads gilt nur wenn der Link im Gallery-Modus ist —
        // sonst wäre die Landing eine Datei-Liste ohne Upload-Widget und der
        // Endpoint offen. Erlaube DisplayAsGallery ODER Folder.Kind==Gallery.
        if (link.PasswordHash is not null) return Forbid();
        var file = await db.Files.SingleOrDefaultAsync(
            f => f.Id == fileId && f.FolderId == link.FolderId && f.Status == StorageFileStatus.Pending, ct);
        if (file is null) return NotFound();
        var probe = await _blobs.ProbeAsync(file.BlobPath, ct);
        if (!probe.Exists) return Problem(statusCode: 409, title: "Blob not found");
        file.SizeBytes = probe.SizeBytes;
        if (!string.IsNullOrEmpty(probe.ContentType)) file.ContentType = probe.ContentType!;
        file.Status = StorageFileStatus.Ready;
        file.ReadyAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        // Owner-Notification: „Jemand hat X ins Album Y gelegt". Nutzt die
        // bestehende NotifyOnAccess-Schiene (best-effort, kein Retry).
        await _notify.NotifyGalleryUploadAsync(link, file, ct);
        return Ok(new { id = file.Id, name = file.Name });
    }

    private static string SanitiseUploadFilename(string name)
    {
        var clean = new string(name.Where(c => c > 31 && c != '\\' && c != '/' && c != ':' && c != '*' && c != '?' && c != '"' && c != '<' && c != '>' && c != '|').ToArray());
        clean = clean.Trim();
        if (clean.Length > 200) clean = clean[..200];
        return string.IsNullOrEmpty(clean) ? $"upload-{Guid.NewGuid():N}" : clean;
    }
}

public record FolderLandingViewModel(
    string Slug, string FolderName, string MessageHtml,
    bool HasPassword, string OwnerName,
    List<FolderLandingFile> Files, string? OwnerAvatarUrl, LandingTheme Theme,
    // v1.10.146: optionales Absender-Zertifikat für Landing-Badge.
    LandingSignerInfo? Signer = null,
    // v1.10.167: Gallery-Modus + „Upload erlauben"-Flag steuern das Landing-
    // Rendering. Gallery=true → Grid+Lightbox statt Datei-Liste. AllowUploads
    // (nur wenn Gallery) → Upload-Widget für Besucher.
    bool IsGallery = false,
    bool AllowUploads = false,
    Guid? FolderId = null);
public record FolderLandingFile(Guid Id, string Name, long SizeBytes, string ContentType);

/// <summary>Snapshot of the applicable LandingTemplate (Global for Public files,
/// UserPersonal for Personal files) passed to the download landing view. Nullable
/// pieces let the view fall back to the default look.</summary>
public record LandingTheme(
    string? Title, string? Subtitle, string? BodyMarkdown, string? FooterText,
    string? PrimaryColor, string? LogoUrl, string? HeroUrl);

public record LandingViewModel(
    string Slug,
    string FileName,
    long SizeBytes,
    string ContentType,
    string MessageHtml,
    bool HasPassword,
    int? MaxDownloads,
    int DownloadCount,
    DateTimeOffset? ExpiresAt,
    string OwnerName,
    LandingTheme Theme,
    string? OwnerAvatarUrl,
    // v1.10.146: optionales Absender-Zertifikat für Landing-Badge.
    LandingSignerInfo? Signer = null);

/// <summary>v1.10.146 — Absender-Zertifikats-Info für die Landing.</summary>
public record LandingSignerInfo(
    string Subject, string Issuer, string Thumbprint,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool IsSelfIssued);

public record GateViewModel(string Slug, bool RequireOtp, bool otpSent, string? error);

public record ExpiredViewModel(string Slug, DateTimeOffset? ExpiresAt);
