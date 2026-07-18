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

    public ShareController(
        ILinkAccessService access, IPasswordHasher hasher, IBlobStorageService blobs,
        IIpHashService iphash, INotificationService notify,
        IStringLocalizer<SharedResources> t, IOptions<StorageOptions> storage)
    {
        _access = access;
        _hasher = hasher;
        _blobs = blobs;
        _iphash = iphash;
        _notify = notify;
        _t = t;
        _storage = storage.Value;
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
            await _access.LogAsync(link, ShareLinkAccessKind.Landing,
                _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
                Request.Headers.UserAgent, Request.Headers.Referer, ct);
            return View("FolderLanding", new FolderLandingViewModel(
                link.Slug, folder.Name, RenderMarkdown(link.Message),
                link.PasswordHash is not null, link.Owner.DisplayName,
                files.Select(f => new FolderLandingFile(f.Id, f.Name, f.SizeBytes, f.ContentType)).ToList()));
        }

        if (link.File is null || link.File.Status != StorageFileStatus.Ready)
            return View("NotFound");

        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        // Log the landing hit (fire-and-forget-ish, but awaited so we don't lose it).
        await _access.LogAsync(link, ShareLinkAccessKind.Landing,
            _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
            Request.Headers.UserAgent, Request.Headers.Referer, ct);

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
            link.Owner.DisplayName));
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
        if (link.PasswordHash is not null && !_hasher.Verify(password ?? "", link.PasswordHash))
        {
            await _access.LogAsync(link, ShareLinkAccessKind.PasswordFail,
                ipHash, Request.Headers.UserAgent, Request.Headers.Referer, ct);
            TempData["PasswordError"] = _t["share.password.error"].Value;
            return RedirectToAction(nameof(Landing), new { slug });
        }

        if (!await _access.TryConsumeDownloadAsync(link, ct))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));

        await _access.LogAsync(link, ShareLinkAccessKind.Download,
            ipHash, Request.Headers.UserAgent, Request.Headers.Referer, ct);

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
        if (link.PasswordHash is not null && !_hasher.Verify(password ?? "", link.PasswordHash))
        {
            await _access.LogAsync(link, ShareLinkAccessKind.PasswordFail, ipHash, Request.Headers.UserAgent, Request.Headers.Referer, ct);
            TempData["PasswordError"] = _t["share.password.error"].Value;
            return RedirectToAction(nameof(Landing), new { slug });
        }
        // Verify the file is actually in that folder.
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId && f.FolderId == link.FolderId && f.Status == StorageFileStatus.Ready, ct);
        if (file is null) return View("NotFound");

        if (!await _access.TryConsumeDownloadAsync(link, ct))
            return View("Expired", new ExpiredViewModel(slug, link.ExpiresAt));
        await _access.LogAsync(link, ShareLinkAccessKind.Download, ipHash, Request.Headers.UserAgent, Request.Headers.Referer, ct);
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
    List<FolderLandingFile> Files);
public record FolderLandingFile(Guid Id, string Name, long SizeBytes, string ContentType);

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
    string OwnerName);

public record ExpiredViewModel(string Slug, DateTimeOffset? ExpiresAt);
