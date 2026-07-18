using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    public async Task<IActionResult> Landing(string slug, CancellationToken ct)
    {
        var link = await _access.FindActiveAsync(slug, ct);
        if (link is null || link.File.Status != StorageFileStatus.Ready)
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
        if (link is null || link.File.Status != StorageFileStatus.Ready) return View("NotFound");
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

    private static string RenderMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().UseSoftlineBreakAsHardlineBreak().Build();
        return Markdown.ToHtml(md, pipeline);
    }
}

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
