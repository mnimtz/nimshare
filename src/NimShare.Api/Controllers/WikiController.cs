using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Wiki pages per scope. API-first — the MVC pages under /wiki reuse the
/// same endpoints so the iOS app can drive a wiki view without HTML scraping.
/// </summary>
public class WikiController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;

    public WikiController(NimShareDbContext db, ICurrentUserService users, IFileAccessService access)
    {
        _db = db; _users = users; _access = access;
    }

    public record PageDto(Guid Id, string Scope, Guid? OwnerUserId, Guid? OwnerGroupId,
        Guid? ParentPageId, string Title, string Slug, string? ContentMarkdown,
        int SortOrder, string CreatedByName, string? LastEditedByName,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public record CreateReq(string Scope, Guid? OwnerGroupId, Guid? ParentPageId,
        string Title, string? ContentMarkdown);
    public record UpdateReq(string? Title, string? ContentMarkdown, Guid? ParentPageId, int? SortOrder);

    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/wiki/pages")]
    public async Task<IActionResult> List(string scope, Guid? groupId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!Enum.TryParse<FileScope>(scope, true, out var s)) return BadRequest();
        var q = _db.WikiPages.Include(p => p.CreatedByUser).Include(p => p.LastEditedByUser).AsQueryable();
        switch (s)
        {
            case FileScope.Personal:
                q = q.Where(p => p.Scope == FileScope.Personal && p.OwnerUserId == me.Id);
                break;
            case FileScope.Group:
                if (groupId is null) return BadRequest();
                if (me.Role != UserRole.Admin && !await _access.IsGroupMemberAsync(me, groupId.Value, ct)) return Forbid();
                q = q.Where(p => p.Scope == FileScope.Group && p.OwnerGroupId == groupId);
                break;
            case FileScope.Public:
                if (me.Role != UserRole.Admin && !me.PublicCanRead) return Forbid();
                q = q.Where(p => p.Scope == FileScope.Public);
                break;
        }
        var rows = await q.OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/wiki/pages")]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!Enum.TryParse<FileScope>(req.Scope, true, out var s)) return BadRequest();
        // Only admins can write public wiki pages.
        if (s == FileScope.Public && me.Role != UserRole.Admin) return Forbid();
        if (s == FileScope.Group && (req.OwnerGroupId is null || !await _access.IsGroupMemberAsync(me, req.OwnerGroupId.Value, ct)))
            return Forbid();
        var p = new WikiPage
        {
            Scope = s,
            OwnerUserId = s == FileScope.Personal ? me.Id : null,
            OwnerGroupId = s == FileScope.Group ? req.OwnerGroupId : null,
            ParentPageId = req.ParentPageId,
            Title = (req.Title ?? "").Trim(),
            Slug = SlugFrom(req.Title ?? ""),
            ContentMarkdown = req.ContentMarkdown ?? "",
            CreatedByUserId = me.Id,
            LastEditedByUserId = me.Id,
        };
        _db.WikiPages.Add(p);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetOne), new { id = p.Id }, ToDto(p));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/wiki/pages/{id:guid}")]
    public async Task<IActionResult> GetOne(Guid id, CancellationToken ct)
    {
        var p = await _db.WikiPages.Include(x => x.CreatedByUser).Include(x => x.LastEditedByUser)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        return await CanReadAsync(p, ct) ? Ok(ToDto(p)) : Forbid();
    }

    [Authorize(Policy = "ApiUser")]
    [HttpPatch("/api/v1/wiki/pages/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var p = await _db.WikiPages.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        if (!await CanWriteAsync(p, ct)) return Forbid();
        if (req.Title is not null) { p.Title = req.Title.Trim(); p.Slug = SlugFrom(req.Title); }
        if (req.ContentMarkdown is not null) p.ContentMarkdown = req.ContentMarkdown;
        if (req.ParentPageId is not null) p.ParentPageId = req.ParentPageId;
        if (req.SortOrder is not null) p.SortOrder = req.SortOrder.Value;
        p.LastEditedByUserId = me.Id;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(p));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpDelete("/api/v1/wiki/pages/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await _db.WikiPages.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        if (!await CanWriteAsync(p, ct)) return Forbid();
        _db.WikiPages.Remove(p);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── HTML pages ──
    [Authorize(Policy = "WebUser")]
    [HttpGet("/wiki")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var mine = await _db.WikiPages.Where(p => p.Scope == FileScope.Personal && p.OwnerUserId == me.Id)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToListAsync(ct);
        var groups = await _access.ListMyGroupsAsync(me, ct);
        ViewData["Mine"] = mine;
        ViewData["Groups"] = groups;
        return View();
    }

    [Authorize(Policy = "WebUser")]
    [HttpGet("/wiki/{id:guid}")]
    public async Task<IActionResult> Page(Guid id, CancellationToken ct)
    {
        var p = await _db.WikiPages.Include(x => x.LastEditedByUser).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound();
        if (!await CanReadAsync(p, ct)) return Forbid();
        var siblings = await _db.WikiPages.Where(x => x.Scope == p.Scope
            && x.OwnerUserId == p.OwnerUserId && x.OwnerGroupId == p.OwnerGroupId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync(ct);
        var pipeline = new Markdig.MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
        ViewData["HTML"] = Markdown.ToHtml(p.ContentMarkdown ?? "", pipeline);
        ViewData["Siblings"] = siblings;
        ViewData["CanWrite"] = await CanWriteAsync(p, ct);
        return View(p);
    }

    // ── helpers ──
    private async Task<bool> CanReadAsync(WikiPage p, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role == UserRole.Admin) return true;
        return p.Scope switch
        {
            FileScope.Personal => p.OwnerUserId == me.Id,
            FileScope.Public => true,
            FileScope.Group => p.OwnerGroupId is Guid g && await _access.IsGroupMemberAsync(me, g, ct),
            _ => false,
        };
    }
    private async Task<bool> CanWriteAsync(WikiPage p, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role == UserRole.Admin) return true;
        return p.Scope switch
        {
            FileScope.Personal => p.OwnerUserId == me.Id,
            FileScope.Public => false,
            FileScope.Group => p.OwnerGroupId is Guid g && await _access.IsGroupMemberAsync(me, g, ct),
            _ => false,
        };
    }

    private static PageDto ToDto(WikiPage p) => new(
        p.Id, p.Scope.ToString(), p.OwnerUserId, p.OwnerGroupId, p.ParentPageId,
        p.Title, p.Slug, p.ContentMarkdown, p.SortOrder,
        p.CreatedByUser?.DisplayName ?? "?", p.LastEditedByUser?.DisplayName,
        p.CreatedAt, p.UpdatedAt);

    private static string SlugFrom(string title)
    {
        var s = new string(title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        if (string.IsNullOrEmpty(s)) s = "page-" + Guid.NewGuid().ToString("N")[..8];
        return s.Length > 120 ? s[..120] : s;
    }
}
