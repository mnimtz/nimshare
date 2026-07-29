using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Route("api/v1/upload-requests")]
[Authorize(Policy = "ApiUser")]
public class UploadRequestsController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ISlugService _slugs;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _users;

    public UploadRequestsController(NimShareDbContext db, ISlugService slugs, IPasswordHasher hasher, ICurrentUserService users)
    {
        _db = db;
        _slugs = slugs;
        _hasher = hasher;
        _users = users;
    }

    public record CreateRequest(
        string? Slug,
        string? Password,
        DateTimeOffset? ExpiresAt,
        int? MaxUploads,
        string? Message,
        string? TargetFolder,
        bool NotifyOnUpload,
        // v1.11.32: der Ordner, aus dem die Anfrage erstellt wurde (Browse-
        // Kontextmenü "📥 Anfrage" liefert den immer mit). Ohne dieses Feld
        // landeten Uploads bislang IMMER unter Personal-Root statt im
        // tatsächlich gewählten Ordner — egal ob Public/Group/Personal.
        Guid? TargetFolderId = null,
        // v1.11.28: Uploads landen zusätzlich in einem yyyy-MM-dd-Unterordner
        // unter TargetFolder — Default an, damit man später leichter findet
        // was wann reinkam.
        bool UseDateSubfolders = true,
        string? RecurringDaysOfWeek = null,
        int? RecurringWindowDays = null,
        // v1.10.146: optionales Absender-Zertifikat (SigningCertificate.Id).
        Guid? SigningCertificateId = null,
        // v1.11.0: optionaler Subdomain-Slug (https://{slug}.{BaseDomain} → /u/…).
        string? SubdomainSlug = null,
        // v1.11.50: explizites "läuft nie ab" — analog LinksController.
        // Default false → fehlendes ExpiresAt defaultet auf +8 Wochen.
        bool IsPermanent = false);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        string slug;
        try { slug = await _slugs.ResolveOrGenerateAsync(req.Slug, ct); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: "Slug taken", detail: ex.Message); }
        catch (ArgumentException ex) { return Problem(statusCode: 422, title: "Invalid slug", detail: ex.Message); }

        // v1.11.0: Subdomain-Slug — identische Regeln wie bei ShareLinks
        // (Feature instanzweit aktiv, DNS-safe, nicht reserviert, frei).
        // v1.11.27: Marcus's Wunsch — jeder User darf Subdomain-Links anlegen
        // (das Admin-vergebene Per-User-Recht CanUseSubdomainShares entfällt).
        string? subdomainSlug = null;
        string? subdomainBase = null;
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
            subdomainBase = subSettings.BaseDomain;
        }

        // v1.10.146: Absender-Zertifikat, nur eigene akzeptieren.
        Guid? certId = null;
        if (req.SigningCertificateId is Guid cid)
        {
            var owned = await _db.SigningCertificates
                .AnyAsync(c => c.Id == cid && c.OwnerUserId == user.Id, ct);
            if (owned) certId = cid;
        }

        // v1.11.32: BUGFIX — der Ordner, aus dem "📥 Anfrage" im Browse
        // aufgerufen wurde, wurde bisher komplett ignoriert; jede Anfrage
        // bekam serverseitig das feste Label "Received" und Uploads landeten
        // (nach v1.11.28) immer unter dem Personal-Root des Owners, egal ob
        // der Link eigentlich auf einen Public- oder Group-Ordner zielte.
        Guid? targetFolderId = null;
        var targetFolderLabel = string.IsNullOrWhiteSpace(req.TargetFolder) ? "Received" : req.TargetFolder!;
        if (req.TargetFolderId is Guid tfid)
        {
            var folderSvc = HttpContext.RequestServices.GetRequiredService<IFolderService>();
            var targetFolder = await _db.Folders.FindAsync(new object[] { tfid }, ct);
            if (targetFolder is null || !await folderSvc.CanWriteAsync(targetFolder, user, ct))
                return Problem(statusCode: 403, title: "You don't have write access to that folder.");
            targetFolderId = targetFolder.Id;
            targetFolderLabel = targetFolder.Name;
        }

        // v1.11.50: siehe LinksController.Create — gleicher 8-Wochen-Default.
        var expiresAt = req.IsPermanent ? (DateTimeOffset?)null : (req.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(56));

        var link = new UploadRequestLink
        {
            OwnerId = user.Id,
            Slug = slug,
            PasswordHash = string.IsNullOrEmpty(req.Password) ? null : _hasher.Hash(req.Password),
            ExpiresAt = expiresAt,
            IsPermanent = req.IsPermanent,
            MaxUploads = req.MaxUploads,
            Message = req.Message,
            TargetFolder = targetFolderLabel,
            TargetFolderId = targetFolderId,
            UseDateSubfolders = req.UseDateSubfolders,
            NotifyOnUpload = req.NotifyOnUpload,
            RecurringDaysOfWeek = string.IsNullOrWhiteSpace(req.RecurringDaysOfWeek) ? null : req.RecurringDaysOfWeek!.Trim(),
            RecurringWindowDays = req.RecurringWindowDays is > 0 ? req.RecurringWindowDays.Value : 7,
            SigningCertificateId = certId,
            SubdomainSlug = subdomainSlug,
        };
        _db.UploadRequests.Add(link);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            link.Id,
            link.Slug,
            Url = Request.PublicUrl($"/u/{link.Slug}"),
            link.ExpiresAt,
            link.IsPermanent,
            link.MaxUploads,
            link.TargetFolder,
            HasPassword = link.PasswordHash is not null,
            // v1.11.0: fertige Subdomain-URL für die Erfolgs-Anzeige.
            SubdomainUrl = subdomainSlug is not null ? $"https://{subdomainSlug}.{subdomainBase}" : null,
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var items = await _db.UploadRequests
            .Where(l => l.OwnerId == user.Id)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id, l.Slug, l.CreatedAt, l.ExpiresAt, l.IsPermanent, l.MaxUploads, l.UploadCount, l.IsRevoked, l.TargetFolder,
            })
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.11.29: Admin-Bypass ergänzt, analog LinksController.Delete().
        var link = user.Role == UserRole.Admin
            ? await _db.UploadRequests.SingleOrDefaultAsync(l => l.Id == id, ct)
            : await _db.UploadRequests.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        _db.UploadRequests.Remove(link);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
