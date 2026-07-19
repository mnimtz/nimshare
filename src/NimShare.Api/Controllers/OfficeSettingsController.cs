using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Admin-only OnlyOffice / Collabora Document Server config.
/// The doc server URL + shared JWT secret land here; per-file edit sessions
/// use them to open the file in an iframe.
/// </summary>
[Authorize(Policy = "WebUser")]
public class OfficeSettingsController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IDataProtector _protector;
    private readonly IStringLocalizer<SharedResources> _l;

    public OfficeSettingsController(NimShareDbContext db, ICurrentUserService users,
        IDataProtectionProvider dp, IStringLocalizer<SharedResources> localizer)
    {
        _db = db; _users = users; _l = localizer;
        _protector = dp.CreateProtector("NimShare.OfficeSettings.Secret.v1");
    }

    [HttpGet("/settings/office")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var s = await LoadAsync(ct);
        ViewData["HasSecret"] = !string.IsNullOrEmpty(s.JwtSecretEncrypted);
        return View(s);
    }

    [HttpPost("/settings/office")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(bool enabled, string? documentServerUrl,
        string? jwtSecret, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var s = await LoadAsync(ct);
        s.Enabled = enabled;
        s.DocumentServerUrl = string.IsNullOrWhiteSpace(documentServerUrl) ? null : documentServerUrl.Trim();
        if (!string.IsNullOrWhiteSpace(jwtSecret))
            s.JwtSecretEncrypted = _protector.Protect(jwtSecret);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = _l["office.saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task<OfficeSettings> LoadAsync(CancellationToken ct)
    {
        var s = await _db.OfficeSettings.FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new OfficeSettings();
        _db.OfficeSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }
}
