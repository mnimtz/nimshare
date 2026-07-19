using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Editors for the public download-landing page.
/// - /settings/landing/global  — Admin only, covers Public-scope shares.
/// - /settings/landing/mine    — Any signed-in user, covers their own
///                                Personal-scope shares.
/// Public read routes stream the logo/hero blobs via /landing/logo/{id}
/// and /landing/hero/{id} so the anonymous download page can display them.
/// </summary>
public class LandingTemplateController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IBlobStorageService _blobs;

    public LandingTemplateController(NimShareDbContext db, ICurrentUserService users, IBlobStorageService blobs)
    {
        _db = db; _users = users; _blobs = blobs;
    }

    // ── Global (Admin) ─────────────────────────────────────────────────────
    [Authorize(Policy = "WebUser")]
    [HttpGet("/settings/landing/global")]
    public async Task<IActionResult> EditGlobal(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var t = await GetOrCreateAsync(LandingTemplateScope.Global, null, ct);
        ViewData["Scope"] = LandingTemplateScope.Global;
        ViewData["EditRoute"] = "/settings/landing/global";
        return View("Edit", t);
    }

    // ── Personal (any user) ────────────────────────────────────────────────
    [Authorize(Policy = "WebUser")]
    [HttpGet("/settings/landing/mine")]
    public async Task<IActionResult> EditMine(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var t = await GetOrCreateAsync(LandingTemplateScope.UserPersonal, me.Id, ct);
        ViewData["Scope"] = LandingTemplateScope.UserPersonal;
        ViewData["EditRoute"] = "/settings/landing/mine";
        return View("Edit", t);
    }

    public record SaveModel(string? Title, string? Subtitle, string? BodyMarkdown,
        string? FooterText, string? PrimaryColor);

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/landing/{scopeName}/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string scopeName, [FromForm] SaveModel form, CancellationToken ct)
    {
        var (t, backUrl) = await ResolveTargetAsync(scopeName, ct);
        if (t is null) return Forbid();
        t.Title = form.Title?.Trim();
        t.Subtitle = form.Subtitle?.Trim();
        t.BodyMarkdown = form.BodyMarkdown?.Trim();
        t.FooterText = form.FooterText?.Trim();
        t.PrimaryColor = NormaliseColor(form.PrimaryColor);
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = "OK";
        return Redirect(backUrl);
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/landing/{scopeName}/upload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Upload(string scopeName, string kind, [FromForm] IFormFile file, CancellationToken ct)
    {
        var (t, backUrl) = await ResolveTargetAsync(scopeName, ct);
        if (t is null) return Forbid();
        if (file is null || file.Length == 0) return BadRequest();
        if (file.Length > 5 * 1024 * 1024) { TempData["Error"] = "Bild ist zu groß (max 5 MB)."; return Redirect(backUrl); }
        var ct2 = (file.ContentType ?? "image/png").ToLowerInvariant();
        if (!ct2.StartsWith("image/")) { TempData["Error"] = "Nur Bilder erlaubt."; return Redirect(backUrl); }

        // Blob path scoped per template so re-uploads overwrite cleanly.
        var ext = ct2 switch { "image/png" => "png", "image/jpeg" => "jpg", "image/webp" => "webp", "image/svg+xml" => "svg", _ => "img" };
        var path = $"landing/{t.Id:N}/{kind}.{ext}";
        var ticket = _blobs.CreateUploadTicket(path);
        using var http = new HttpClient();
        using (var content = new StreamContent(file.OpenReadStream()))
        {
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.Add("x-ms-blob-content-type", ct2);
            var resp = await http.PutAsync(ticket.UploadUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                TempData["Error"] = "Upload fehlgeschlagen.";
                return Redirect(backUrl);
            }
        }
        // Bust caches by appending an unix-second version query.
        var v = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (kind == "logo") { t.LogoBlobPath = path; t.LogoUrl = $"/landing/img/{t.Id}/logo?v={v}"; }
        else { t.HeroBlobPath = path; t.HeroUrl = $"/landing/img/{t.Id}/hero?v={v}"; }
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Redirect(backUrl);
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/landing/{scopeName}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveImage(string scopeName, string kind, CancellationToken ct)
    {
        var (t, backUrl) = await ResolveTargetAsync(scopeName, ct);
        if (t is null) return Forbid();
        var path = kind == "logo" ? t.LogoBlobPath : t.HeroBlobPath;
        if (!string.IsNullOrEmpty(path))
        {
            try { await _blobs.DeleteAsync(path, ct); } catch { /* orphaned bytes */ }
        }
        if (kind == "logo") { t.LogoBlobPath = null; t.LogoUrl = null; }
        else { t.HeroBlobPath = null; t.HeroUrl = null; }
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Redirect(backUrl);
    }

    // ── Public image proxy (no auth) ──────────────────────────────────────
    [AllowAnonymous]
    [HttpGet("/landing/img/{id:guid}/{kind}")]
    public async Task<IActionResult> ServeImage(Guid id, string kind, CancellationToken ct)
    {
        var t = await _db.LandingTemplates.FindAsync(new object[] { id }, ct);
        if (t is null) return NotFound();
        var path = kind == "logo" ? t.LogoBlobPath : t.HeroBlobPath;
        if (string.IsNullOrEmpty(path)) return NotFound();
        try
        {
            var ms = new MemoryStream();
            await _blobs.DownloadToAsync(path, ms, ct);
            ms.Position = 0;
            var ct2 = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream",
            };
            Response.Headers["Cache-Control"] = "public, max-age=300";
            return File(ms, ct2);
        }
        catch { return NotFound(); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────
    private async Task<(LandingTemplate?, string)> ResolveTargetAsync(string scopeName, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.Equals(scopeName, "global", StringComparison.OrdinalIgnoreCase))
        {
            if (me.Role != UserRole.Admin) return (null, "/dashboard");
            return (await GetOrCreateAsync(LandingTemplateScope.Global, null, ct), "/settings/landing/global");
        }
        return (await GetOrCreateAsync(LandingTemplateScope.UserPersonal, me.Id, ct), "/settings/landing/mine");
    }

    private async Task<LandingTemplate> GetOrCreateAsync(LandingTemplateScope scope, Guid? ownerId, CancellationToken ct)
    {
        var t = scope == LandingTemplateScope.Global
            ? await _db.LandingTemplates.FirstOrDefaultAsync(x => x.Scope == LandingTemplateScope.Global, ct)
            : await _db.LandingTemplates.FirstOrDefaultAsync(x => x.Scope == LandingTemplateScope.UserPersonal && x.OwnerUserId == ownerId, ct);
        if (t is not null) return t;
        t = new LandingTemplate { Scope = scope, OwnerUserId = ownerId };
        _db.LandingTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return t;
    }

    private static string? NormaliseColor(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        c = c.Trim();
        if (!c.StartsWith("#")) c = "#" + c;
        return System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{3,8}$") ? c : null;
    }
}
