using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.10.111 — Geteilte Linksammlung (löst das Wiki ab). EINE firmenweite,
/// flache Liste: alle eingeloggten Nutzer sehen sie, nur Admins pflegen sie.
/// API-first — die MVC-Seite unter /links und iOS nutzen dieselben Endpoints.
///
/// Route-Präfix bewusst „link-collection", weil /api/v1/links bereits von den
/// Share-Links (LinksController) belegt ist.
/// </summary>
public class LinkCollectionController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public LinkCollectionController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    public record LinkDto(Guid Id, string Title, string Url, string? Description, string? Emoji, int SortOrder);
    public record CreateReq(string Title, string Url, string? Description, string? Emoji);
    public record UpdateReq(string? Title, string? Url, string? Description, string? Emoji);
    public record ReorderReq(Guid[] OrderedIds);

    private static LinkDto ToDto(LinkEntry l) => new(l.Id, l.Title, l.Url, l.Description, l.Emoji, l.SortOrder);

    // Nur http/https zulassen — verhindert javascript:/data:-URLs in einer
    // Liste, die alle Nutzer anklicken.
    private static bool IsSafeUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // ── API ────────────────────────────────────────────────────────────────
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/link-collection")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await _users.GetOrProvisionAsync(User, ct); // Auth erzwingen
        var rows = await _db.LinkEntries
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Title)
            .ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/link-collection")]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Title)) return Problem(statusCode: 422, title: "Title is required.");
        if (!IsSafeUrl(req.Url)) return Problem(statusCode: 422, title: "A valid http(s) URL is required.");

        var maxOrder = await _db.LinkEntries.AnyAsync(ct)
            ? await _db.LinkEntries.MaxAsync(l => l.SortOrder, ct)
            : -1;
        var entry = new LinkEntry
        {
            Title = req.Title.Trim(),
            Url = req.Url.Trim(),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            Emoji = string.IsNullOrWhiteSpace(req.Emoji) ? null : req.Emoji.Trim(),
            SortOrder = maxOrder + 1,
            CreatedByUserId = me.Id,
        };
        _db.LinkEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entry));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPut("/api/v1/link-collection/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var entry = await _db.LinkEntries.FindAsync(new object[] { id }, ct);
        if (entry is null) return NotFound();

        if (req.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return Problem(statusCode: 422, title: "Title cannot be empty.");
            entry.Title = req.Title.Trim();
        }
        if (req.Url is not null)
        {
            if (!IsSafeUrl(req.Url)) return Problem(statusCode: 422, title: "A valid http(s) URL is required.");
            entry.Url = req.Url.Trim();
        }
        if (req.Description is not null)
            entry.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        if (req.Emoji is not null)
            entry.Emoji = string.IsNullOrWhiteSpace(req.Emoji) ? null : req.Emoji.Trim();
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entry));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpDelete("/api/v1/link-collection/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var entry = await _db.LinkEntries.FindAsync(new object[] { id }, ct);
        if (entry is null) return NotFound();
        _db.LinkEntries.Remove(entry);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/link-collection/reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        if (req.OrderedIds is null || req.OrderedIds.Length == 0) return BadRequest();
        var all = await _db.LinkEntries.ToListAsync(ct);
        var byId = all.ToDictionary(l => l.Id);
        for (int i = 0; i < req.OrderedIds.Length; i++)
            if (byId.TryGetValue(req.OrderedIds[i], out var e)) e.SortOrder = i;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── MVC-Seite ────────────────────────────────────────────────────────────
    // Route bewusst /link-collection — /links gehört den Share-Links
    // (HomeController.Links „Meine Links").
    [Authorize(Policy = "WebUser")]
    [HttpGet("/link-collection")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.LinkEntries
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Title)
            .ToListAsync(ct);
        ViewData["Links"] = rows;
        ViewData["IsAdmin"] = me.Role == UserRole.Admin;
        return View();
    }
}
