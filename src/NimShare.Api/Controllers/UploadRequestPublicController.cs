using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>Public endpoint for a reverse-share (upload) link.</summary>
[AllowAnonymous]
[Route("u")]
public class UploadRequestPublicController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;
    private readonly IPasswordHasher _hasher;
    private readonly INotificationService _notify;

    public UploadRequestPublicController(NimShareDbContext db, IBlobStorageService blobs, IPasswordHasher hasher, INotificationService notify)
    {
        _db = db;
        _blobs = blobs;
        _hasher = hasher;
        _notify = notify;
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Landing(string slug, CancellationToken ct)
    {
        var link = await _db.UploadRequests.Include(l => l.Owner)
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (link is null) return View("NotFound");
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return View("Expired");
        return View("UploadLanding", new UploadLandingViewModel(
            link.Slug, RenderMarkdown(link.Message), link.PasswordHash is not null, link.Owner.DisplayName));
    }

    public record InitUploadRequest(string Filename, long SizeBytes, string ContentType, string? Password);

    /// <summary>Called by the browser via JS after the visitor picks a file; returns a SAS to write into.</summary>
    [HttpPost("{slug}/init")]
    public async Task<IActionResult> Init(string slug, [FromBody] InitUploadRequest req, CancellationToken ct)
    {
        var link = await _db.UploadRequests.Include(l => l.Owner)
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (link is null) return NotFound();
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return StatusCode(410);

        if (link.PasswordHash is not null && !_hasher.Verify(req.Password ?? "", link.PasswordHash))
            return Unauthorized();

        var file = new StorageFile
        {
            OwnerId = link.OwnerId,
            Name = req.Filename,
            SizeBytes = req.SizeBytes,
            ContentType = string.IsNullOrWhiteSpace(req.ContentType) ? "application/octet-stream" : req.ContentType,
            Folder = link.TargetFolder,
            Status = StorageFileStatus.Pending,
        };
        file.BlobPath = $"users/{link.OwnerId:N}/{file.Id:N}/{SanitiseFilename(req.Filename)}";
        _db.Files.Add(file);
        await _db.SaveChangesAsync(ct);

        var ticket = _blobs.CreateUploadTicket(file.BlobPath);
        return Ok(new
        {
            fileId = file.Id,
            uploadUrl = ticket.UploadUrl.ToString(),
            uploadMethod = ticket.Method,
        });
    }

    public record CompleteRequest(Guid FileId);

    [HttpPost("{slug}/complete")]
    public async Task<IActionResult> Complete(string slug, [FromBody] CompleteRequest req, CancellationToken ct)
    {
        var link = await _db.UploadRequests.Include(l => l.Owner)
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (link is null) return NotFound();

        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == req.FileId && f.OwnerId == link.OwnerId, ct);
        if (file is null) return NotFound();

        var probe = await _blobs.ProbeAsync(file.BlobPath, ct);
        if (!probe.Exists) return StatusCode(409);

        file.SizeBytes = probe.SizeBytes;
        file.Status = StorageFileStatus.Ready;
        file.ReadyAt = DateTimeOffset.UtcNow;
        link.UploadCount++;
        link.LastUploadAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _notify.NotifyUploadAsync(link, file.Name, ct);
        return Ok(new { ok = true });
    }

    private static string RenderMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var p = new MarkdownPipelineBuilder().DisableHtml().UseSoftlineBreakAsHardlineBreak().Build();
        return Markdown.ToHtml(md, p);
    }

    private static string SanitiseFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }
}

public record UploadLandingViewModel(string Slug, string MessageHtml, bool HasPassword, string OwnerName);
