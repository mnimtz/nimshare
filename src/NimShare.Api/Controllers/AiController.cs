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
    /// Pick the response language for AI calls. Uses CurrentUICulture, which
    /// the RequestLocalization middleware already populated from (in this
    /// order): explicit ?ui-culture= override, the .AspNetCore.Culture cookie
    /// set by the language picker, Accept-Language header from the browser,
    /// and finally the DefaultRequestCulture ("en"). "iv" (invariant, seen on
    /// some background threads) collapses to "en" so we never send an empty
    /// or malformed ISO code to the provider.
    /// </summary>
    private static string CurrentLanguageIso()
    {
        var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.IsNullOrEmpty(code) || code == "iv") return "en";
        return code;
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
        var lang = CurrentLanguageIso();
        // Cache hit only when the previously generated summary matches the
        // visitor's current language. A German summary served to an English
        // visitor would be jarring — better to spend the second AI call.
        if (!string.IsNullOrEmpty(file.AiSummary)
            && string.Equals(file.AiSummaryLang, lang, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { summary = file.AiSummary, cached = true });
        }

        var provider = await _ai.CreateProviderAsync(ct);

        // Image files go through the vision endpoint of the provider — text
        // extraction returns nothing useful for them.
        string? summary;
        var contentTypeLower = (file.ContentType ?? "").ToLowerInvariant();
        if (contentTypeLower.StartsWith("image/"))
        {
            using var ms = new MemoryStream();
            await _blobs.DownloadToAsync(file.BlobPath, ms, ct);
            var bytes = ms.ToArray();
            summary = await provider.DescribeImageAsync(bytes, contentTypeLower, lang, ct);
            if (string.IsNullOrWhiteSpace(summary))
            {
                var detail = (provider as OpenAiProvider)?.LastError
                    ?? "The configured AI model may not support image input.";
                return Problem(statusCode: 502, title: "Vision returned no result.", detail: detail);
            }
        }
        else
        {
            var text = await _ai.ExtractTextAsync(file.BlobPath, file.ContentType, _blobs, ct);
            if (string.IsNullOrWhiteSpace(text))
                return Problem(statusCode: 415, title: "Cannot summarise this file type",
                    detail: "Text extraction is currently available for text and PDF files.");
            summary = await provider.SummarizeAsync(text, lang, ct);
        }
        if (string.IsNullOrWhiteSpace(summary))
            return Problem(statusCode: 502, title: "Summariser returned no result.");

        file.AiSummary = summary.Length > 1900 ? summary[..1900] : summary;
        file.AiSummaryLang = lang;
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
        var lang = CurrentLanguageIso();
        var draft = await provider.DraftShareEmailAsync(me.DisplayName, link.File.Name, req.Context, lang, ct);
        if (string.IsNullOrWhiteSpace(draft)) return Problem(statusCode: 502, title: "Draft empty.");
        return Ok(new { draft });
    }

    public record DraftReq(Guid LinkId, string? Context);

    public record DraftTemplateReq(string Prompt, string? Locale);

    /// <summary>
    /// AI-drafts an email template (subject + body-markdown) for the
    /// signature workflow. Prompt is user-supplied ("Formal contract for a
    /// legal counterparty"); locale defaults to the user's PreferredCulture.
    /// The reply MUST include placeholder tokens like {{recipient.name}} and
    /// {{url}} — the frontend just shoves the result into the editor.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("draft-email-template")]
    public async Task<IActionResult> DraftEmailTemplate([FromBody] DraftTemplateReq req,
        [FromServices] ICurrentUserService users, CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (!settings.EnableDraftedShareEmails || settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "AI-drafted emails are disabled.");

        var me = await users.GetOrProvisionAsync(User, ct);
        var lang = string.IsNullOrWhiteSpace(req.Locale)
            ? (string.IsNullOrWhiteSpace(me.PreferredCulture) ? "en" : me.PreferredCulture)
            : req.Locale;
        var provider = await _ai.CreateProviderAsync(ct);

        // Reuse the existing chat helper via DraftShareEmailAsync (already
        // wired for all provider back-ends). Feed the template-specific
        // instructions in the "context" field so the provider's system
        // prompt still applies.
        var promptShort = string.IsNullOrWhiteSpace(req.Prompt) ? "signature request" : req.Prompt.Trim();
        var instruction = $"You are drafting an email template used to invite a person to sign a document. " +
            $"Style/tone: {promptShort}. Write in the language whose ISO code is '{lang}'. " +
            $"Return EXACTLY two blocks, plain text, separated by a line 'BODY:':\n" +
            $"SUBJECT: <one-line subject>\nBODY:\n<3-6 short paragraphs of body>\n\n" +
            $"Include these Handlebars placeholders LITERALLY, so downstream code can substitute:\n" +
            $"{{{{recipient.name}}}}, {{{{sender.name}}}}, {{{{doc.title}}}}, {{{{url}}}}. " +
            $"Optionally include {{{{message}}}} in the body if relevant. Do NOT introduce other placeholders. " +
            $"Do not add greetings, disclaimers, HTML, or Markdown headings.";

        // Prefer the detail-carrying path when the provider is Gemini so an
        // empty response can be surfaced with the actual reason (safety
        // block, MAX_TOKENS truncation, 4xx from the API) instead of just
        // "Draft empty."
        string? raw = null;
        string? err = null;
        if (provider is GeminiProvider gp)
        {
            var (draftedText, error) = await gp.GenerateWithDetailAsync(
                $"You are drafting an email template for a signature invite. Style: {promptShort}. Language ISO: {lang}. Return EXACTLY 'SUBJECT: <line>' then a newline 'BODY:' then 3–6 short paragraphs. Use these literal Handlebars placeholders: {{{{recipient.name}}}}, {{{{sender.name}}}}, {{{{doc.title}}}}, {{{{url}}}}. Optional: {{{{message}}}}. No HTML, no markdown headings.",
                0.6, 1024, ct);
            raw = draftedText; err = error;
        }
        else
        {
            raw = await provider.DraftShareEmailAsync(me.DisplayName, "", instruction, lang, ct);
        }
        if (string.IsNullOrWhiteSpace(raw))
            return Problem(statusCode: 502, title: "Draft empty.",
                detail: err ?? "Provider returned no text — check API key, quota, or model availability.");

        // Split on "BODY:" (case-insensitive) — the model usually complies.
        var text = raw.Trim();
        string subject = "", body = text;
        var idx = text.IndexOf("BODY:", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var before = text[..idx].Trim();
            var after = text[(idx + "BODY:".Length)..].Trim();
            if (before.StartsWith("SUBJECT:", StringComparison.OrdinalIgnoreCase))
                subject = before["SUBJECT:".Length..].Trim();
            else
                subject = before;
            body = after;
        }
        return Ok(new { subject, body });
    }

    // ── #3 Semantic search ─────────────────────────────────────────────────

    public record SearchReq(string Query, string Scope, Guid? GroupId, int Limit);
    public record SearchHit(Guid Id, string Name, double Score, string? Snippet, Guid? FolderId);

    /// <summary>
    /// Classic keyword search — runs a case-insensitive LIKE over the
    /// extracted-text column populated by the AI post-processor, plus the
    /// filename. Complements the semantic search — you find "invoice
    /// 47829" reliably even without a semantic model configured.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("keyword-search")]
    public async Task<IActionResult> KeywordSearch([FromBody] SearchReq req,
        [FromServices] ICurrentUserService users, [FromServices] IFileAccessService access, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query) || req.Query.Trim().Length < 2)
            return Ok(Array.Empty<SearchHit>());
        var me = await users.GetOrProvisionAsync(User, ct);
        var q = req.Query.Trim();
        var escaped = q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var like = "%" + escaped + "%";

        var readable = access.ApplyReadFilter(
            _db.Files.Where(f => f.Status == NimShare.Core.Entities.StorageFileStatus.Ready), me);
        if (Enum.TryParse<NimShare.Core.Entities.FileScope>(req.Scope, true, out var sc))
        {
            readable = sc == NimShare.Core.Entities.FileScope.Group && req.GroupId is Guid g
                ? readable.Where(f => f.Scope == sc && f.GroupId == g)
                : readable.Where(f => f.Scope == sc);
        }
        var rows = await readable
            .Where(f => EF.Functions.Like(f.Name, like, "\\")
                || (f.ExtractedText != null && EF.Functions.Like(f.ExtractedText, like, "\\")))
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(req.Limit, 1, 50))
            .Select(f => new { f.Id, f.Name, f.FolderId, f.ExtractedText })
            .ToListAsync(ct);
        var hits = rows.Select(r =>
        {
            var snippet = SnippetAround(r.ExtractedText, q, 160);
            return new SearchHit(r.Id, r.Name, 1.0, snippet, r.FolderId);
        }).ToList();
        return Ok(hits);
    }

    private static string? SnippetAround(string? text, string query, int radius)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text.Length > radius * 2 ? text[..(radius * 2)] : text;
        var start = Math.Max(0, idx - radius);
        var end = Math.Min(text.Length, idx + query.Length + radius);
        var s = text[start..end];
        return (start > 0 ? "…" : "") + s + (end < text.Length ? "…" : "");
    }

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
        var (ok, hits, err) = await RetrieveHitsAsync(req, me, access, ct);
        if (!ok) return err!;
        return Ok(hits);
    }

    /// <summary>Shared retrieval used by both Search and Chat — returns raw hits so
    /// Chat doesn't have to unpack an IActionResult.</summary>
    private async Task<(bool Ok, List<SearchHit> Hits, IActionResult? Error)> RetrieveHitsAsync(
        SearchReq req, User me, IFileAccessService access, CancellationToken ct)
    {
        var provider = await _ai.CreateProviderAsync(ct);
        var qv = await provider.EmbedAsync(req.Query, ct);
        if (qv is null) return (false, new(), Problem(statusCode: 502, title: "Provider does not support embeddings."));

        var readable = access.ApplyReadFilter(_db.Files.Where(f => f.Status == NimShare.Core.Entities.StorageFileStatus.Ready), me);
        if (Enum.TryParse<NimShare.Core.Entities.FileScope>(req.Scope, true, out var sc))
        {
            readable = sc == NimShare.Core.Entities.FileScope.Group && req.GroupId is Guid g
                ? readable.Where(f => f.Scope == sc && f.GroupId == g)
                : readable.Where(f => f.Scope == sc);
        }
        var fileIds = await readable.Select(f => f.Id).ToListAsync(ct);
        if (fileIds.Count == 0) return (true, new(), null);

        var embs = await _db.FileEmbeddings.Where(e => fileIds.Contains(e.FileId)).ToListAsync(ct);
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
        }).ToList();
        return (true, hits, null);
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
            return Problem(statusCode: 503, title: "Chat mit Dateien ist deaktiviert.",
                detail: "Einstellungen → AI-Gateway: 'Chat with your files' aktivieren und einen Provider konfigurieren.");
        if (!settings.EnableSemanticSearch)
            return Problem(statusCode: 503, title: "Chat braucht semantische Suche.",
                detail: "Einstellungen → AI-Gateway: 'Semantische Suche' zusätzlich aktivieren — Chat findet Dokumente über Embeddings.");
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest();

        // Check that the user actually has embeddings — otherwise the retrieval
        // returns empty and the chat says "no context" which reads as broken.
        var me = await users.GetOrProvisionAsync(User, ct);
        var anyEmbedding = await _db.FileEmbeddings
            .Where(e => _db.Files.Any(f => f.Id == e.FileId && f.OwnerId == me.Id))
            .AnyAsync(ct);
        if (!anyEmbedding)
            return Problem(statusCode: 503, title: "Noch keine indexierten Dokumente.",
                detail: "Semantische Suche benötigt Embeddings, die beim Hochladen erzeugt werden. Nach dem Aktivieren neue Dateien hochladen oder ältere neu indexieren (Reindex-Button auf der AI-Gateway-Seite folgt).");

        // Re-use the retrieval path (private helper — bypasses HTTP wrapper).
        var (ok, hits, err) = await RetrieveHitsAsync(new SearchReq(req.Question, req.Scope, req.GroupId, 6), me, access, ct);
        if (!ok) return err!;
        if (hits.Count == 0)
            return Ok(new { answer = string.Empty, citations = Array.Empty<SearchHit>() });

        // Build passages using cached summaries where available; expand with
        // extracted text otherwise. Batch-load the files in ONE query — the
        // hit-loop's FindAsync-per-hit was N+1 (7 db round-trips per chat).
        var hitIds = hits.Select(h => h.Id).ToArray();
        var files = await _db.Files
            .Where(f => hitIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
        var passages = new List<string>();
        for (int i = 0; i < hits.Count; i++)
        {
            if (!files.TryGetValue(hits[i].Id, out var file)) continue;
            var text = !string.IsNullOrEmpty(file.AiSummary)
                ? file.AiSummary
                : await _ai.ExtractTextAsync(file.BlobPath, file.ContentType, _blobs, ct) ?? file.Name;
            passages.Add($"[{i + 1}] {file.Name}: {(text.Length > 800 ? text[..800] : text)}");
        }
        var provider = await _ai.CreateProviderAsync(ct);
        var lang = CurrentLanguageIso();
        var answer = await provider.ChatAnswerAsync(req.Question, passages, lang, ct);
        return Ok(new { answer, citations = hits });
    }

    /// <summary>Backfill embeddings for existing files owned by the caller.
    /// The upload-time post-processor only runs for NEW uploads; without this,
    /// enabling "Chat mit Dateien" on a mature account leaves the semantic
    /// search table empty and the chat looks broken. Fires each file at the
    /// existing IAiPostProcessor pipeline — same tags/embeds/summaries as at
    /// upload time.</summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(
        [FromServices] ICurrentUserService users,
        [FromServices] IAiPostProcessor post,
        CancellationToken ct)
    {
        var settings = await _ai.LoadAsync(ct);
        if (settings.Provider == AiProvider.Disabled)
            return Problem(statusCode: 503, title: "AI Gateway ist deaktiviert.");
        var me = await users.GetOrProvisionAsync(User, ct);
        // Missing embeddings only — no point re-crunching every file if the
        // model didn't change. Admins get the full backfill (everything
        // without an embedding row across scopes they can already see).
        IQueryable<StorageFile> q = _db.Files.Where(f =>
            f.Status == StorageFileStatus.Ready
            && !_db.FileEmbeddings.Any(e => e.FileId == f.Id));
        if (me.Role != UserRole.Admin)
            q = q.Where(f => f.OwnerId == me.Id);
        // Cap the batch — anything larger and the async queue would swamp the
        // provider quota. Callers can re-hit this endpoint later.
        var ids = await q.OrderByDescending(f => f.CreatedAt).Take(500).Select(f => f.Id).ToListAsync(ct);
        foreach (var id in ids) post.QueueForFile(id);
        return Ok(new { queued = ids.Count });
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
        var lang = CurrentLanguageIso();
        var draft = await provider.DraftUploadRequestAsync(me.DisplayName, req.RecipientEmail, req.Context, lang, ct);
        if (string.IsNullOrEmpty(draft)) return Problem(statusCode: 502, title: "Draft empty.");
        return Ok(new { draft });
    }
}
