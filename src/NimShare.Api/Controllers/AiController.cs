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

    // ── #3 Semantic search ─────────────────────────────────────────────────

    public record SearchReq(string Query, string Scope, Guid? GroupId, int Limit);
    public record SearchHit(Guid Id, string Name, double Score, string? Snippet, Guid? FolderId);

    [Authorize(Policy = "ApiUser")]
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchReq req,
        [FromServices] ICurrentUserService users, [FromServices] IFileAccessService access, CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (!settings.EnableSemanticSearch || settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "Semantic search is disabled.");
        if (string.IsNullOrWhiteSpace(req.Query)) return BadRequest();

        var me = await users.GetOrProvisionAsync(User, ct);
        var provider = await _ai.CreateProviderAsync(ct);
        var qv = await provider.EmbedAsync(req.Query, ct);
        if (qv is null) return Problem(statusCode: 502, title: "Provider does not support embeddings.");

        // Restrict to files the caller can read.
        var readable = access.ApplyReadFilter(_db.Files.Where(f => f.Status == NimShare.Core.Entities.StorageFileStatus.Ready), me);
        if (Enum.TryParse<NimShare.Core.Entities.FileScope>(req.Scope, true, out var sc))
        {
            readable = sc == NimShare.Core.Entities.FileScope.Group && req.GroupId is Guid g
                ? readable.Where(f => f.Scope == sc && f.GroupId == g)
                : readable.Where(f => f.Scope == sc);
        }
        var fileIds = await readable.Select(f => f.Id).ToListAsync(ct);
        if (fileIds.Count == 0) return Ok(Array.Empty<SearchHit>());

        var embs = await _db.FileEmbeddings.Where(e => fileIds.Contains(e.FileId)).ToListAsync(ct);
        // Score all candidate embeddings client-side; small datasets are fine.
        var scored = new List<(Guid Id, double Score)>();
        foreach (var e in embs)
        {
            var vec = new float[e.Vector.Length / 4];
            Buffer.BlockCopy(e.Vector, 0, vec, 0, e.Vector.Length);
            if (vec.Length != qv.Length) continue;
            scored.Add((e.FileId, Cosine(qv, vec)));
        }
        var top = scored.OrderByDescending(x => x.Score).Take(Math.Clamp(req.Limit, 1, 50)).ToList();
        var topIds = top.Select(x => x.Id).ToList();
        var byId = await _db.Files.Where(f => topIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Name, f.FolderId, f.AiSummary }).ToDictionaryAsync(x => x.Id, ct);
        var hits = top.Where(t => byId.ContainsKey(t.Id)).Select(t =>
        {
            var f = byId[t.Id];
            return new SearchHit(t.Id, f.Name, Math.Round(t.Score, 4), f.AiSummary?[..Math.Min(160, f.AiSummary.Length)], f.FolderId);
        });
        return Ok(hits);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        var d = Math.Sqrt(na) * Math.Sqrt(nb);
        return d == 0 ? 0 : dot / d;
    }

    // ── #7 Chat with files (RAG over embeddings) ───────────────────────────

    public record ChatReq(string Question, string Scope, Guid? GroupId);

    [Authorize(Policy = "ApiUser")]
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatReq req,
        [FromServices] ICurrentUserService users, [FromServices] IFileAccessService access, CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (!settings.EnableChatWithFiles || settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "Chat is disabled.");
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest();

        // Re-use Search to retrieve the top passages.
        var searchResult = await Search(new SearchReq(req.Question, req.Scope, req.GroupId, 6), users, access, ct);
        if (searchResult is not OkObjectResult ok) return searchResult;
        var hits = ((IEnumerable<SearchHit>)ok.Value!).ToList();
        if (hits.Count == 0)
            return Ok(new { answer = string.Empty, citations = Array.Empty<SearchHit>() });

        // Build passages using cached summaries where available; expand with extracted text otherwise.
        var passages = new List<string>();
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var file = await _db.Files.FindAsync(new object[] { hit.Id }, ct);
            if (file is null) continue;
            var text = !string.IsNullOrEmpty(file.AiSummary)
                ? file.AiSummary
                : await _ai.ExtractTextAsync(file.BlobPath, file.ContentType, _blobs, ct) ?? file.Name;
            passages.Add($"[{i + 1}] {file.Name}: {(text.Length > 800 ? text[..800] : text)}");
        }
        var provider = await _ai.CreateProviderAsync(ct);
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName ?? "en";
        var answer = await provider.ChatAnswerAsync(req.Question, passages, lang, ct);
        return Ok(new { answer, citations = hits });
    }

    // ── #4 Guided upload-request cover email ───────────────────────────────

    public record GuidedUrReq(Guid LinkId, string RecipientEmail, string? Context);

    [Authorize(Policy = "ApiUser")]
    [HttpPost("draft-upload-request")]
    public async Task<IActionResult> DraftUploadRequest([FromBody] GuidedUrReq req,
        [FromServices] ICurrentUserService users, CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (!settings.EnableGuidedUploadRequests || settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "Guided upload requests are disabled.");
        var me = await users.GetOrProvisionAsync(User, ct);
        var link = await _db.UploadRequests.SingleOrDefaultAsync(l => l.Id == req.LinkId && l.OwnerId == me.Id, ct);
        if (link is null) return NotFound();
        var provider = await _ai.CreateProviderAsync(ct);
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName ?? "en";
        var draft = await provider.DraftUploadRequestAsync(me.DisplayName, req.RecipientEmail, req.Context, lang, ct);
        if (string.IsNullOrEmpty(draft)) return Problem(statusCode: 502, title: "Draft empty.");
        return Ok(new { draft });
    }
}
