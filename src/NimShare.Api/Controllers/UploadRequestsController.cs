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
        string? RecurringDaysOfWeek = null,
        int? RecurringWindowDays = null,
        // v1.10.146: optionales Absender-Zertifikat (SigningCertificate.Id).
        Guid? SigningCertificateId = null,
        // v1.11.0: optionaler Subdomain-Slug (https://{slug}.{BaseDomain} → /u/…).
        string? SubdomainSlug = null);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        string slug;
        try { slug = await _slugs.ResolveOrGenerateAsync(req.Slug, ct); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: "Slug taken", detail: ex.Message); }
        catch (ArgumentException ex) { return Problem(statusCode: 422, title: "Invalid slug", detail: ex.Message); }

        // v1.11.0: Subdomain-Slug — identische Regeln wie bei ShareLinks
        // (Feature aktiv, User-Recht, DNS-safe, nicht reserviert, frei).
        string? subdomainSlug = null;
        string? subdomainBase = null;
        if (!string.IsNullOrWhiteSpace(req.SubdomainSlug))
        {
            var subSvc = HttpContext.RequestServices.GetRequiredService<ISubdomainShareService>();
            var subSettings = await subSvc.GetSettingsAsync(ct);
            if (subSettings is null || !subSettings.Enabled || string.IsNullOrEmpty(subSettings.BaseDomain))
                return Problem(statusCode: 422, title: "Subdomain sharing is not enabled on this instance.");
            if (user.Role != UserRole.Admin && !user.CanUseSubdomainShares)
                return Problem(statusCode: 403, title: "You are not allowed to create subdomain shares.");
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

        var link = new UploadRequestLink
        {
            OwnerId = user.Id,
            Slug = slug,
            PasswordHash = string.IsNullOrEmpty(req.Password) ? null : _hasher.Hash(req.Password),
            ExpiresAt = req.ExpiresAt,
            MaxUploads = req.MaxUploads,
            Message = req.Message,
            TargetFolder = string.IsNullOrWhiteSpace(req.TargetFolder) ? "Received" : req.TargetFolder!,
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
                l.Id, l.Slug, l.CreatedAt, l.ExpiresAt, l.MaxUploads, l.UploadCount, l.IsRevoked, l.TargetFolder,
            })
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.UploadRequests.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        _db.UploadRequests.Remove(link);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
