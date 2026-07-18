using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Route("api/v1/links")]
[Authorize(Policy = "ApiUser")]
public class LinksController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ISlugService _slugs;
    private readonly IPasswordHasher _hasher;
    private readonly IQrCodeService _qr;
    private readonly ICurrentUserService _users;

    public LinksController(
        NimShareDbContext db, ISlugService slugs, IPasswordHasher hasher,
        IQrCodeService qr, ICurrentUserService users)
    {
        _db = db;
        _slugs = slugs;
        _hasher = hasher;
        _qr = qr;
        _users = users;
    }

    public record CreateLinkRequest(
        Guid? FileId,
        Guid? FolderId,
        string? Slug,
        string? Password,
        DateTimeOffset? ExpiresAt,
        int? MaxDownloads,
        string? Message,
        bool NotifyOnAccess);

    public record LinkDto(
        Guid Id, string Slug, string Url, string QrCodeUrl,
        DateTimeOffset? ExpiresAt, int? MaxDownloads,
        int DownloadCount, int HitCount, bool HasPassword,
        bool IsRevoked, DateTimeOffset CreatedAt);

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

        var link = new ShareLink
        {
            FileId = file?.Id,
            FolderId = folder?.Id,
            OwnerId = user.Id,
            Slug = slug,
            PasswordHash = string.IsNullOrEmpty(req.Password) ? null : _hasher.Hash(req.Password),
            ExpiresAt = req.ExpiresAt,
            MaxDownloads = req.MaxDownloads,
            Message = req.Message,
            NotifyOnAccess = req.NotifyOnAccess,
        };
        _db.ShareLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = link.Id }, ToDto(link));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // Materialize first so we can build absolute URLs from the current
        // HttpContext.Request — same shape as Get/Create.
        var rows = await _db.ShareLinks
            .Where(l => l.OwnerId == user.Id)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LinkDto>> GetById(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        return link is null ? NotFound() : Ok(ToDto(link));
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

    [HttpGet("{id:guid}/qr.svg")]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct)
    {
        // Auth required — otherwise anyone with a link.Id could learn the slug
        // behind it and check whether that id exists.
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks
            .SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        var url = BuildPublicUrl(link.Slug);
        return Content(_qr.RenderSvg(url), "image/svg+xml; charset=utf-8");
    }

    public record UpdateLinkRequest(DateTimeOffset? ExpiresAt, int? MaxDownloads, string? Message, bool? IsRevoked, bool? NotifyOnAccess);

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLinkRequest req, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        if (req.ExpiresAt is not null) link.ExpiresAt = req.ExpiresAt;
        if (req.MaxDownloads is not null) link.MaxDownloads = req.MaxDownloads;
        if (req.Message is not null) link.Message = req.Message;
        if (req.IsRevoked is not null) link.IsRevoked = req.IsRevoked.Value;
        if (req.NotifyOnAccess is not null) link.NotifyOnAccess = req.NotifyOnAccess.Value;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(link));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        _db.ShareLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record SendByEmailRequest(string ToEmail, string? Message);

    [HttpPost("{id:guid}/send-email")]
    public async Task<IActionResult> SendByEmail(Guid id, [FromBody] SendByEmailRequest req, [FromServices] INotificationService notify, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.Include(l => l.File).SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
        if (link is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.ToEmail) || !req.ToEmail.Contains('@'))
            return Problem(statusCode: 422, title: "Invalid recipient email");
        var url = BuildPublicUrl(link.Slug);
        var subject = $"{user.DisplayName} shared a file with you: {link.File.Name}";
        var body = $"""
                    Hello,

                    {user.DisplayName} ({user.Email}) has shared a file with you:

                    {link.File.Name}
                    {url}

                    {(string.IsNullOrWhiteSpace(req.Message) ? "" : "Message from the sender:\n" + req.Message + "\n\n")}
                    — NimShare
                    """;
        await notify.SendShareLinkAsync(req.ToEmail.Trim(), user.DisplayName, subject, body, ct);
        return Ok(new { sent = true });
    }

    private LinkDto ToDto(ShareLink l) => new(
        l.Id, l.Slug, BuildPublicUrl(l.Slug), $"/api/v1/links/{l.Id}/qr.svg",
        l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.HitCount,
        l.PasswordHash != null, l.IsRevoked, l.CreatedAt);

    private string BuildPublicUrl(string slug)
    {
        var req = HttpContext.Request;
        return $"{req.Scheme}://{req.Host}/s/{slug}";
    }
}
