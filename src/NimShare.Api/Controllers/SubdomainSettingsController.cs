using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.11.0 — Admin-Seite für Subdomain-Sharing (/settings/subdomains).
/// Web-only (Marcus: „settings kann im web ui bleiben"). Speichert die
/// Instanz-Konfiguration und bietet den Cloudflare-Setup-Assistenten an.
/// </summary>
[Authorize(Policy = "WebUser")]
public class SubdomainSettingsController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly ISubdomainShareService _svc;
    private readonly IStringLocalizer<SharedResources> _l;

    public SubdomainSettingsController(NimShareDbContext db, ICurrentUserService users,
        ISubdomainShareService svc, IStringLocalizer<SharedResources> l)
    {
        _db = db;
        _users = users;
        _svc = svc;
        _l = l;
    }

    public record SubdomainSettingsVm(
        bool Enabled, string BaseDomain, string OriginHost,
        string? AzureVerificationId, bool HasCloudflareToken, string? CloudflareZoneId,
        string SuggestedOriginHost);

    [HttpGet("/settings/subdomains")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var s = await _db.SubdomainShareSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        // Origin-Vorschlag: der App-Service-Default-Host aus der Umgebung.
        var suggested = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") ?? Request.Host.Host;
        return View(new SubdomainSettingsVm(
            s?.Enabled ?? false,
            s?.BaseDomain ?? "",
            s?.OriginHost ?? "",
            s?.AzureVerificationId,
            s?.CloudflareApiTokenEncrypted is { Length: > 0 },
            s?.CloudflareZoneId,
            suggested));
    }

    public record SaveForm(string? BaseDomain, string? OriginHost,
        string? AzureVerificationId, string? CloudflareApiToken, bool Enabled = false);

    [HttpPost("/settings/subdomains")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromForm] SaveForm form, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();

        var s = await _db.SubdomainShareSettings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new SubdomainShareSettings();
            _db.SubdomainShareSettings.Add(s);
        }
        var baseDomain = (form.BaseDomain ?? "").Trim().ToLowerInvariant()
            .Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var originHost = (form.OriginHost ?? "").Trim().ToLowerInvariant()
            .Replace("https://", "").Replace("http://", "").TrimEnd('/');
        if (form.Enabled && (baseDomain.Length == 0 || !baseDomain.Contains('.')))
        {
            TempData["Error"] = _l["subdomains.err.base_domain"].Value;
            return RedirectToAction(nameof(Index));
        }
        s.Enabled = form.Enabled;
        s.BaseDomain = baseDomain;
        s.OriginHost = originHost;
        s.AzureVerificationId = string.IsNullOrWhiteSpace(form.AzureVerificationId)
            ? null : form.AzureVerificationId.Trim();
        // Token nur überschreiben, wenn eines eingegeben wurde (write-only-Feld).
        if (!string.IsNullOrWhiteSpace(form.CloudflareApiToken))
        {
            s.CloudflareApiTokenEncrypted = _svc.ProtectToken(form.CloudflareApiToken.Trim());
            s.CloudflareZoneId = null;   // Zone bei neuem Token neu auflösen
        }
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedByUserId = me.Id;
        await _db.SaveChangesAsync(ct);
        _svc.InvalidateCache();
        TempData["Notice"] = _l["subdomains.saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/subdomains/setup-dns")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetupDns(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var (ok, message) = await _svc.SetupDnsAsync(ct);
        if (ok) TempData["Notice"] = message;
        else TempData["Error"] = message;
        return RedirectToAction(nameof(Index));
    }
}
