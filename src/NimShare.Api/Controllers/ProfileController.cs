using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Data;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class ProfileController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IPasswordHasher _hasher;

    public ProfileController(NimShareDbContext db, ICurrentUserService users, IPasswordHasher hasher)
    {
        _db = db;
        _users = users;
        _hasher = hasher;
    }

    public record ProfileFormModel(string DisplayName, string? AvatarUrl,
        string? CurrentPassword, string? NewPassword, string? NewPasswordConfirm);

    [HttpGet("/settings/profile")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return View(me);
    }

    [HttpPost("/settings/profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update([FromForm] ProfileFormModel form, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!string.IsNullOrWhiteSpace(form.DisplayName))
            me.DisplayName = form.DisplayName.Trim();
        me.AvatarUrl = string.IsNullOrWhiteSpace(form.AvatarUrl) ? null : form.AvatarUrl.Trim();

        // Optional password change: only apply if all three password fields are filled.
        if (!string.IsNullOrEmpty(form.NewPassword) || !string.IsNullOrEmpty(form.CurrentPassword))
        {
            if (string.IsNullOrEmpty(me.PasswordHash))
            {
                TempData["Error"] = "This account uses Entra sign-in only; there is no local password to change.";
                return RedirectToAction(nameof(Index));
            }
            if (string.IsNullOrEmpty(form.CurrentPassword) || !_hasher.Verify(form.CurrentPassword, me.PasswordHash))
            {
                TempData["Error"] = "Current password is wrong.";
                return RedirectToAction(nameof(Index));
            }
            if (form.NewPassword != form.NewPasswordConfirm)
            {
                TempData["Error"] = "New passwords do not match.";
                return RedirectToAction(nameof(Index));
            }
            if ((form.NewPassword ?? "").Length < 8)
            {
                TempData["Error"] = "New password must be at least 8 characters.";
                return RedirectToAction(nameof(Index));
            }
            me.PasswordHash = _hasher.Hash(form.NewPassword!);
        }

        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = "Profile saved.";
        return RedirectToAction(nameof(Index));
    }
}
