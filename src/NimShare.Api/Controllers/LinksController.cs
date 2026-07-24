using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private readonly bool _storeFullIp;

    public LinksController(
        NimShareDbContext db, ISlugService slugs, IPasswordHasher hasher,
        IQrCodeService qr, ICurrentUserService users, IConfiguration cfg)
    {
        _db = db;
        _slugs = slugs;
        _hasher = hasher;
        _qr = qr;
        _users = users;
        _storeFullIp = cfg.GetValue<bool>("ShareLinks:StoreFullIp");
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
        Guid? SigningCertificateId = null);

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
        SignerInfo? Signer = null);

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

        // v1.10.146: Absender-Zertifikat — nur eigene akzeptieren, sonst leise
        // ignorieren (kein Fehler, damit der Link trotzdem erstellt wird).
        Guid? certId = null;
        if (req.SigningCertificateId is Guid cid)
        {
            var owned = await _db.SigningCertificates
                .AnyAsync(c => c.Id == cid && c.OwnerUserId == user.Id, ct);
            if (owned) certId = cid;
        }

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
            SigningCertificateId = certId,
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
        var activity = HttpContext.RequestServices.GetService<IActivityLogger>();
        if (activity is not null)
        {
            var subject = file?.Name ?? folder?.Name ?? "Element";
            await activity.LogAsync(ActivityKind.ShareLinkCreated, user,
                $"Share-Link erstellt: /s/{link.Slug} ({subject})",
                fileId: link.FileId, folderId: link.FolderId, ct: ct);
        }
        return CreatedAtAction(nameof(GetById), new { id = link.Id }, ToDto(link));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.10.66: Include File+Folder damit IsPublic korrekt berechnet
        // werden kann (Split "Öffentliche Links" vs "Meine Links" im iOS-
        // und Web-Client).
        var rows = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .Include(l => l.SigningCertificate)
            .Where(l => l.OwnerId == user.Id)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LinkDto>> GetById(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks
            .Include(l => l.File).Include(l => l.Folder).Include(l => l.SigningCertificate)
            .SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
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

    // v1.10.158: reichere Report-Aggregate für Web + iOS. Ergänzt den alten
    // /stats-Endpoint um Country/City/Device/Timezone-Splits, Peak-Hour-
    // Heatmap und Time-to-Download-Median. StoreFullIp-Flag zeigt der App,
    // ob sie die IP-Spalte einblenden darf.
    public record ReportCountRow(string Key, int Count);
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
        List<ReportCountRow> Referrers,
        List<ReportHeatCell> HourHeatmap,
        List<ReportEvent> RecentEvents,
        int TotalEventCount,
        bool StoreFullIp);

    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.OwnerId == user.Id, ct);
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
        var referrers = all.Select(e => e.Referer ?? "")
            .Select(r =>
            {
                if (string.IsNullOrWhiteSpace(r)) return "";
                try { return new Uri(r).Host; } catch { return "(direct)"; }
            })
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!)
            .Select(g => new ReportCountRow(g.Key, g.Count()))
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

        var events = all.Take(200).Select(a => new ReportEvent(
            a.At, a.Kind.ToString(), a.CountryCode, a.City, a.DeviceType, a.Timezone, a.Referer,
            _storeFullIp ? a.IpAddress : null)).ToList();

        return Ok(new ReportResponse(
            link.Id, link.Slug, link.HitCount, link.DownloadCount, unique,
            medianTtdSec, link.LastAccessAt, byDay,
            countries, cities, devices, timezones, referrers,
            heatCells, events, all.Count, _storeFullIp));
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

    public record UpdateLinkRequest(DateTimeOffset? ExpiresAt, int? MaxDownloads, string? Message, bool? IsRevoked, bool? NotifyOnAccess, bool? IsPublic, string? AllowedEmails, bool? RequireEmailVerify);

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
        if (req.ExpiresAt is not null) link.ExpiresAt = req.ExpiresAt;
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
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(link));
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
        _db.ShareLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
        return NoContent();
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

    private LinkDto ToDto(ShareLink l) => new(
        l.Id, l.Slug, BuildPublicUrl(l.Slug), $"/api/v1/links/{l.Id}/qr.svg",
        l.ExpiresAt, l.MaxDownloads, l.DownloadCount, l.HitCount,
        l.PasswordHash != null, l.IsRevoked, l.CreatedAt,
        // v1.10.66: Public wenn File/Folder Scope=Public, oder explizit
        // als isPublic markierter Admin-Link.
        IsPublic: (l.File != null && l.File.Scope == FileScope.Public)
              || (l.Folder != null && l.Folder.Scope == FileScope.Public)
              || l.IsPublic,
        TargetKind: l.File != null ? "file" : (l.Folder != null ? "folder" : null),
        TargetName: l.File?.Name ?? l.Folder?.Name,
        Signer: BuildSignerInfo(l.SigningCertificate));

    // v1.10.146: Signer-Info fürs Landing-Badge; nur bei vorhandenem Zertifikat.
    internal static SignerInfo? BuildSignerInfo(SigningCertificate? c)
        => c is null ? null : new SignerInfo(
            c.Id, c.SubjectCommonName, c.Issuer, c.Thumbprint,
            c.NotBefore, c.NotAfter, c.IsSelfIssued);

    private string BuildPublicUrl(string slug)
        => HttpContext.Request.PublicUrl($"/s/{slug}");
}
