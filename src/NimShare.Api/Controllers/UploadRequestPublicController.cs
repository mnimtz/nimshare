using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>Public endpoint for a reverse-share (upload) link.</summary>
[AllowAnonymous]
[Route("u")]
[EnableRateLimiting("public-share")]
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

    // v1.10.153: Public download des Absender-Zertifikats (Stufe 1). Analog
    // ShareController.SignerCert — für Upload-Anforderungs-Landings.
    [HttpGet("{slug}/signer-cert.cer")]
    public async Task<IActionResult> SignerCert(string slug, [FromServices] ISignerCertReader reader, CancellationToken ct)
    {
        var link = await _db.UploadRequests
            .Include(l => l.SigningCertificate)
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (link is null || link.SigningCertificate is null) return NotFound();
        var der = reader.GetPublicDer(link.SigningCertificate);
        var chars = link.SigningCertificate.SubjectCommonName
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray();
        var fname = (chars.Length == 0 ? "signer" : new string(chars)) + ".cer";
        return File(der, "application/x-x509-user-cert", fname);
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Landing(string slug, CancellationToken ct)
    {
        var link = await _db.UploadRequests.Include(l => l.Owner)
            .Include(l => l.SigningCertificate)   // v1.10.146
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (link is null) return View("NotFound");
        var now = DateTimeOffset.UtcNow;
        if (!link.IsActive(now)) return View("Expired");

        // v1.11.34: Marcus's Wunsch — gleiche Scope-basierte Branding-Logik
        // (Logo/Farbe/Avatar) wie bei Share-Links, statt immer nur dem
        // festen NimShare-Icon. Scope kommt vom echten Zielordner (seit
        // v1.11.32 gesetzt); ohne Zielordner (Alt-Links/API ohne Folder)
        // Fallback auf Personal — deckt sich mit dem Fallback in
        // ResolveTargetFolderAsync() unten.
        var targetFolder = link.TargetFolderId is Guid tfid
            ? await _db.Folders.FindAsync(new object[] { tfid }, ct)
            : null;
        var scope = targetFolder?.Scope ?? FileScope.Personal;
        var scopeOwnerId = targetFolder?.OwnerUserId ?? link.OwnerId;
        var theme = await ShareController.ResolveThemeAsync(_db, scope, scopeOwnerId, ct);
        var avatar = ShareController.ResolveOwnerAvatar(link.Owner, isPublicShare: scope == FileScope.Public);

        return View("UploadLanding", new UploadLandingViewModel(
            link.Slug, RenderMarkdown(link.Message), link.PasswordHash is not null, link.Owner.DisplayName,
            ShareController.BuildLandingSigner(link.SigningCertificate), theme, avatar));
    }

    public record InitUploadRequest(string Filename, long SizeBytes, string ContentType, string? Password);

    private const long MaxUploadRequestBytes = 5L * 1024 * 1024 * 1024; // 5 GB per upload from a public request

    /// <summary>Called by the browser via JS after the visitor picks a file; returns a SAS to write into.</summary>
    [HttpPost("{slug}/init")]
    public async Task<IActionResult> Init(string slug, [FromBody] InitUploadRequest req, CancellationToken ct)
    {
        var link = await _db.UploadRequests.Include(l => l.Owner)
            .SingleOrDefaultAsync(l => l.Slug == slug, ct);
        if (link is null) return NotFound();

        if (link.PasswordHash is not null && !_hasher.Verify(req.Password ?? "", link.PasswordHash))
            return Unauthorized();

        // Basic size sanity check (recipient can lie, but this at least blocks obvious abuse).
        if (req.SizeBytes <= 0 || req.SizeBytes > MaxUploadRequestBytes)
            return Problem(statusCode: 413, title: "File too large", detail: $"Max {MaxUploadRequestBytes / 1024 / 1024} MiB per upload-request link.");

        // v1.10.24: Quota gilt nur für Personal-Scope. Upload-Request-Links,
        // die in einen Public/Group-Ordner zielen, laufen ohne Quota-Prüfung
        // (dort ist der Speicher gemeinsam, nicht dem User zugerechnet).
        // Ohne TargetFolder = Personal-Fallback → wir prüfen.
        var targetScope = link.TargetFolderRef?.Scope ?? FileScope.Personal;
        if (targetScope == FileScope.Personal)
        {
            var usedPersonalBytes = await _db.Files
                .Where(f => f.OwnerId == link.OwnerId
                    && f.Scope == FileScope.Personal
                    && f.Status != StorageFileStatus.Deleted)
                .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
            if (usedPersonalBytes + req.SizeBytes > link.Owner.QuotaBytes)
                return Problem(statusCode: 413, title: "Recipient is out of storage");
        }

        // Atomically reserve one upload slot on the link. Prevents concurrent visitors
        // racing past MaxUploads and stops one visitor from creating hundreds of
        // orphaned Pending StorageFile rows in a tight loop.
        var now = DateTimeOffset.UtcNow;
        var reserved = await _db.UploadRequests
            .Where(l => l.Id == link.Id
                        && !l.IsRevoked
                        && (l.ExpiresAt == null || l.ExpiresAt > now)
                        && (l.MaxUploads == null || l.UploadCount < l.MaxUploads))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UploadCount, l => l.UploadCount + 1), ct);
        if (reserved == 0) return StatusCode(410); // gone/expired/full

        // v1.11.28: BUGFIX — bislang wurde nur das Freitext-Label
        // (StorageFile.Folder) gesetzt, nie die echte FolderId. Damit landete
        // JEDE per Upload-Anfrage empfangene Datei mit FolderId=null in der
        // DB — für die Ordneransicht unsichtbar (Marcus's Report: Mail kam,
        // "Done" wurde angezeigt, aber die Datei tauchte nirgends auf).
        var folderSvc = HttpContext.RequestServices.GetRequiredService<IFolderService>();
        var targetFolder = await ResolveTargetFolderAsync(link, folderSvc, ct);

        var file = new StorageFile
        {
            OwnerId = link.OwnerId,
            Name = req.Filename,
            SizeBytes = req.SizeBytes,
            ContentType = string.IsNullOrWhiteSpace(req.ContentType) ? "application/octet-stream" : req.ContentType,
            Folder = link.TargetFolder,
            FolderId = targetFolder.Id,
            Scope = targetFolder.Scope,
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

        // UploadCount was already incremented atomically in /init. Just mark the
        // file Ready and update the link's last-touched timestamp.
        file.SizeBytes = probe.SizeBytes;
        file.Status = StorageFileStatus.Ready;
        file.ReadyAt = DateTimeOffset.UtcNow;
        link.LastUploadAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _notify.NotifyUploadAsync(link, file.Name, ct);
        // v1.10.192: Upload-Request-Eingänge durch die AI-Pipeline schicken
        // (Tags/Risk/Embedding) — analog FilesController.Complete. Thumbs für
        // Bilder gleich mit (Browser-Preview + evtl. spätere Album-Nutzung).
        HttpContext.RequestServices.GetRequiredService<IAiPostProcessor>().QueueForFile(file.Id);
        if ((file.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            HttpContext.RequestServices.GetRequiredService<IThumbnailService>()
                .Enqueue(file.Id, file.BlobPath, file.ContentType);
        return Ok(new { ok = true });
    }

    /// <summary>v1.11.28: Löst TargetFolder in eine echte Folder-Entity auf
    /// (findet oder legt sie unter dem Personal-Root des Owners an) und
    /// persistiert TargetFolderId einmalig am Link, damit nachfolgende
    /// Uploads sie direkt wiederverwenden statt jedes Mal neu aufzulösen.
    /// Bei UseDateSubfolders=true kommt zusätzlich ein yyyy-MM-dd-Unterordner
    /// dazu (wird NICHT am Link persistiert, ändert sich ja täglich).</summary>
    private async Task<Folder> ResolveTargetFolderAsync(UploadRequestLink link, IFolderService folderSvc, CancellationToken ct)
    {
        Folder? target = null;
        if (link.TargetFolderId is Guid existingId)
            target = await _db.Folders.FindAsync(new object[] { existingId }, ct);

        if (target is null)
        {
            var root = await folderSvc.GetOrCreateRootAsync(FileScope.Personal, link.OwnerId, null, link.Owner, ct);
            var name = string.IsNullOrWhiteSpace(link.TargetFolder) ? "Received" : link.TargetFolder;
            target = await folderSvc.ResolvePathAsync(root, new[] { name }, ct);
            if (target is null)
            {
                try { target = await folderSvc.CreateChildAsync(root, name, link.Owner, ct); }
                catch (InvalidOperationException)
                {
                    // Race: ein zweiter gleichzeitiger Upload hat ihn zuerst angelegt.
                    target = await folderSvc.ResolvePathAsync(root, new[] { name }, ct);
                }
            }
            link.TargetFolderId = target!.Id;
            await _db.SaveChangesAsync(ct);
        }

        if (!link.UseDateSubfolders) return target!;

        var dateName = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var dateFolder = await folderSvc.ResolvePathAsync(target!, new[] { dateName }, ct);
        if (dateFolder is null)
        {
            try { dateFolder = await folderSvc.CreateChildAsync(target!, dateName, link.Owner, ct); }
            catch (InvalidOperationException)
            {
                dateFolder = await folderSvc.ResolvePathAsync(target!, new[] { dateName }, ct);
            }
        }
        return dateFolder!;
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

public record UploadLandingViewModel(string Slug, string MessageHtml, bool HasPassword, string OwnerName,
    LandingSignerInfo? Signer = null,   // v1.10.146
    // v1.11.34: gleiches Branding wie Share-Landings — Logo/Farbe vom
    // scope-passenden LandingTemplate, Avatar nur wenn der Owner es für
    // diesen Scope freigegeben hat.
    LandingTheme? Theme = null,
    string? OwnerAvatarUrl = null);
