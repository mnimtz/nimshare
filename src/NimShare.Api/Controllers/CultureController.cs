using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Language switcher endpoint. Writes the .AspNetCore.Culture cookie so the
/// next request keeps the picked language, and persists it on the User row
/// when the caller is signed in — so a fresh sign-in on another device also
/// starts in the picked language.
/// </summary>
public class CultureController : Controller
{
    private static readonly HashSet<string> Supported =
        new(new[] { "en", "de", "fr", "it", "es", "nl" }, StringComparer.OrdinalIgnoreCase);

    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public CultureController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db;
        _users = users;
    }

    /// <summary>Set the preferred language and redirect back to where the user was.</summary>
    [HttpGet("/set-culture")]
    public async Task<IActionResult> Set(string code, string? returnUrl, CancellationToken ct)
    {
        code = (code ?? "").Trim().ToLowerInvariant();
        if (!Supported.Contains(code)) code = "en";

        // Persistent cookie (1 year). CookieRequestCultureProvider's default
        // cookie name (".AspNetCore.Culture") is what our RequestLocalization
        // middleware reads on every subsequent request.
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(code, code)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = true,
            });

        // Also persist to the signed-in user's profile so a fresh sign-in on
        // another device starts in the picked language (matches the schema
        // field User.PreferredCulture).
        if (User.Identity?.IsAuthenticated == true)
        {
            try
            {
                var me = await _users.GetOrProvisionAsync(User, ct);
                if (!string.Equals(me.PreferredCulture, code, StringComparison.OrdinalIgnoreCase))
                {
                    me.PreferredCulture = code;
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch { /* language change should never fail because of DB */ }
        }

        // Same-origin only — never bounce off-site.
        return Redirect(SafeReturn(returnUrl));
    }

    private string SafeReturn(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/dashboard";
        if (Url.IsLocalUrl(url)) return url!;
        return "/dashboard";
    }
}
