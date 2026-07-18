using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Public/anonymous AI endpoints used by the share landing and by the
/// authenticated share modal to draft cover emails.
/// </summary>
[Route("api/v1/ai")]
public class AiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly IAiGatewayService _ai;
    private readonly IBlobStorageService _blobs;

    public AiController(NimShareDbContext db, IAiGatewayService ai, IBlobStorageService blobs)
    {
        _db = db;
        _ai = ai;
        _blobs = blobs;
    }

    /// <summary>
    /// Auto-summary for a file. Callable ANONYMOUSLY only if the visitor
    /// has landed via a valid share link (slug provided).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("summary")]
    public async Task<IActionResult> Summary([FromBody] SummaryReq req, CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (!settings.EnableAutoSummary || settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "AI summary is disabled by the administrator.");
        if (string.IsNullOrWhiteSpace(req.Slug)) return BadRequest();

        // Only serve summaries for files behind a currently-valid share link.
        var link = await _db.ShareLinks
            .Include(l => l.File)
            .SingleOrDefaultAsync(l => l.Slug == req.Slug && l.FileId != null, ct);
        if (link is null || link.File is null) return NotFound();
        if (link.IsRevoked || (link.ExpiresAt is not null && link.ExpiresAt <= DateTimeOffset.UtcNow))
            return Problem(statusCode: 410, title: "Link expired");

        var file = link.File;
        if (!string.IsNullOrEmpty(file.AiSummary))
            return Ok(new { summary = file.AiSummary, cached = true });

        var text = await _ai.ExtractTextAsync(file.BlobPath, file.ContentType, _blobs, ct);
        if (string.IsNullOrWhiteSpace(text))
            return Problem(statusCode: 415, title: "Cannot summarise this file type",
                detail: "Text extraction is currently available for text and PDF files.");

        var provider = await _ai.CreateProviderAsync(ct);
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName ?? "en";
        var summary = await provider.SummarizeAsync(text, lang, ct);
        if (string.IsNullOrWhiteSpace(summary))
            return Problem(statusCode: 502, title: "Summariser returned no result.");

        file.AiSummary = summary.Length > 1900 ? summary[..1900] : summary;
        await _db.SaveChangesAsync(ct);
        return Ok(new { summary = file.AiSummary, cached = false });
    }

    public record SummaryReq(string Slug);

    /// <summary>Draft a share-by-email cover text. Auth required.</summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("draft-email")]
    public async Task<IActionResult> DraftEmail([FromBody] DraftReq req, [FromServices] ICurrentUserService users, CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (!settings.EnableDraftedShareEmails || settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "AI-drafted emails are disabled.");

        var me = await users.GetOrProvisionAsync(User, ct);
        var link = await _db.ShareLinks.Include(l => l.File)
            .SingleOrDefaultAsync(l => l.Id == req.LinkId && l.OwnerId == me.Id, ct);
        if (link is null || link.File is null) return NotFound();

        var provider = await _ai.CreateProviderAsync(ct);
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName ?? "en";
        var draft = await provider.DraftShareEmailAsync(me.DisplayName, link.File.Name, req.Context, lang, ct);
        if (string.IsNullOrWhiteSpace(draft)) return Problem(statusCode: 502, title: "Draft empty.");
        return Ok(new { draft });
    }

    public record DraftReq(Guid LinkId, string? Context);
}
