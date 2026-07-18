using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Local email + password sign-in / sign-out and the first-run setup wizard
/// (first user created is automatically the Admin).
/// The Entra ID flow uses its own controllers from Microsoft.Identity.Web.UI.
/// </summary>
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly ILocalAuthService _auth;

    public AccountController(ILocalAuthService auth) => _auth = auth;

    // ── First-run setup ────────────────────────────────────────────────────

    [HttpGet("/setup")]
    public async Task<IActionResult> Setup(CancellationToken ct)
    {
        if (!await _auth.IsFirstRunAsync(ct))
            return RedirectToAction("Index", "Home");
        return View(new SetupViewModel());
    }

    [HttpPost("/setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup(SetupViewModel vm, CancellationToken ct)
    {
        if (!await _auth.IsFirstRunAsync(ct))
            return RedirectToAction("Index", "Home");

        if (!ModelState.IsValid) return View(vm);
        if (vm.Password != vm.PasswordConfirm)
        {
            ModelState.AddModelError(nameof(vm.PasswordConfirm), "Passwords do not match.");
            return View(vm);
        }

        try
        {
            var admin = await _auth.CreateAsync(vm.Email, vm.DisplayName, vm.Password, UserRole.Admin, ct);
            await _auth.SignInAsync(HttpContext, admin, persistent: false);
            return RedirectToAction("Dashboard", "Home");
        }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return View(vm); }
        catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); return View(vm); }
    }

    // ── Login ──────────────────────────────────────────────────────────────

    [HttpGet("/login")]
    public async Task<IActionResult> Login(string? returnUrl = null, CancellationToken ct = default)
    {
        if (await _auth.IsFirstRunAsync(ct))
            return RedirectToAction(nameof(Setup));
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    [HttpPost("/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel vm, string? returnUrl = null, CancellationToken ct = default)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(vm);

        var user = await _auth.AuthenticateAsync(vm.Email, vm.Password, ct);
        if (user is null)
        {
            ModelState.AddModelError("", "Incorrect email or password.");
            return View(vm);
        }
        await _auth.SignInAsync(HttpContext, user, vm.RememberMe);
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction("Dashboard", "Home");
    }

    // ── Logout ─────────────────────────────────────────────────────────────

    [HttpPost("/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _auth.SignOutAsync(HttpContext);
        return RedirectToAction("Index", "Home");
    }

    // GET /logout as a convenience — some browsers won't POST from a plain link.
    [HttpGet("/logout")]
    public async Task<IActionResult> LogoutGet()
    {
        await _auth.SignOutAsync(HttpContext);
        return RedirectToAction("Index", "Home");
    }

    // ── Change password (self) ─────────────────────────────────────────────

    [Authorize(Policy = "WebUser")]
    [HttpGet("/account/password")]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [Authorize(Policy = "WebUser")]
    [HttpPost("/account/password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel vm, [FromServices] NimShare.Core.Data.NimShareDbContext db, [FromServices] IPasswordHasher hasher, [FromServices] ICurrentUserService currentUser, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(vm);
        if (vm.NewPassword != vm.NewPasswordConfirm)
        {
            ModelState.AddModelError(nameof(vm.NewPasswordConfirm), "Passwords do not match.");
            return View(vm);
        }
        var me = await currentUser.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrEmpty(me.PasswordHash) || !hasher.Verify(vm.CurrentPassword, me.PasswordHash))
        {
            ModelState.AddModelError("", "Current password is wrong.");
            return View(vm);
        }
        if (vm.NewPassword.Length < 8)
        {
            ModelState.AddModelError(nameof(vm.NewPassword), "Password must be at least 8 characters.");
            return View(vm);
        }
        me.PasswordHash = hasher.Hash(vm.NewPassword);
        await db.SaveChangesAsync(ct);
        TempData["Notice"] = "Password updated.";
        return RedirectToAction("Settings", "Home");
    }
}

public class SetupViewModel
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Password { get; set; } = "";
    public string PasswordConfirm { get; set; } = "";
}

public class LoginViewModel
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
}

public class ChangePasswordViewModel
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public string NewPasswordConfirm { get; set; } = "";
}
