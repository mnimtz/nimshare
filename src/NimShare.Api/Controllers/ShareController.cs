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
            await _access.LogAsync(link, ShareLinkAccessKind.Landing,
                _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
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
                ResolveOwnerAvatar(link.Owner), folderTheme));
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
        await _access.LogAsync(link, ShareLinkAccessKind.Landing,
            _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
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
            ResolveOwnerAvatar(link.Owner)));
    }

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
    [HttpGet("{slug}/preview")]
    public async Task<IActionResult> Preview(string slug, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.File is null || link.File.Status != StorageFileStatus.Ready) return NotFound();
        if (link.PasswordHash is not null) return Forbid();
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return NotFound();
        var ct2 = (link.File.ContentType ?? "").ToLowerInvariant();
        if (!ct2.StartsWith("image/") && ct2 != "application/pdf") return BadRequest();
        // Redirect to a short-lived SAS with inline disposition — Azure serves it
        // directly, no bytes go through the app.
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

        var ipHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        var lfDl = await LandingForensicsAsync(ct);
        if (link.PasswordHash is not null && !_hasher.Verify(password ?? "", link.PasswordHash))
        {
            await _access.LogAsync(link, ShareLinkAccessKind.PasswordFail,
                ipHash, Request.Headers.UserAgent, Request.Headers.Referer,
                lfDl.Country, lfDl.City, lfDl.Device, timezone: null, ct);
            TempData["PasswordError"] = _t["share.password.error"].Value;
            return RedirectToAction(nameof(Landing), new { slug });
        }

        if (!await _access.TryConsumeDownloadAsync(link, ct))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        await _access.LogAsync(link, ShareLinkAccessKind.Download,
            ipHash, Request.Headers.UserAgent, Request.Headers.Referer,
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

        var ipHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        var lfFf = await LandingForensicsAsync(ct);
        if (link.PasswordHash is not null && !_hasher.Verify(password ?? "", link.PasswordHash))
        {
            await _access.LogAsync(link, ShareLinkAccessKind.PasswordFail, ipHash, Request.Headers.UserAgent, Request.Headers.Referer,
                lfFf.Country, lfFf.City, lfFf.Device, timezone: null, ct);
            TempData["PasswordError"] = _t["share.password.error"].Value;
            return RedirectToAction(nameof(Landing), new { slug });
        }
        // Verify the file is actually in that folder.
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId && f.FolderId == link.FolderId && f.Status == StorageFileStatus.Ready, ct);
        if (file is null) return View("NotFound");

        if (!await _access.TryConsumeDownloadAsync(link, ct))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));
        await _access.LogAsync(link, ShareLinkAccessKind.Download, ipHash, Request.Headers.UserAgent, Request.Headers.Referer,
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
}

public record FolderLandingViewModel(
    string Slug, string FolderName, string MessageHtml,
    bool HasPassword, string OwnerName,
    List<FolderLandingFile> Files, string? OwnerAvatarUrl, LandingTheme Theme);
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
    string? OwnerAvatarUrl);

public record GateViewModel(string Slug, bool RequireOtp, bool otpSent, string? error);

public record ExpiredViewModel(string Slug, DateTimeOffset? ExpiresAt);
