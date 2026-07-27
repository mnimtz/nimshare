using System.Globalization;
using Microsoft.Extensions.Localization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

public class InvitationsController : Controller
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "en", "de", "fr", "it", "es", "nl" };

    private readonly NimShareDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _users;
    private readonly ILocalAuthService _auth;
    private readonly IEmailGatewayService _gateway;
    private readonly IStringLocalizer<SharedResources> _l;
    private readonly IStringLocalizerFactory _localizerFactory;

    public InvitationsController(NimShareDbContext db, IPasswordHasher hasher, ICurrentUserService users,
        ILocalAuthService auth, IEmailGatewayService gateway, IStringLocalizer<SharedResources> l,
        IStringLocalizerFactory localizerFactory)
    {
        _db = db;
        _hasher = hasher;
        _users = users;
        _auth = auth;
        _gateway = gateway;
        _l = l;
        _localizerFactory = localizerFactory;
    }

    /// <summary>Runs `body` with CurrentUICulture temporarily switched to `language`
    /// — used so invite/reminder emails go out in the language the admin picked for
    /// the recipient, independent of the admin's own UI language.</summary>
    private static T WithCulture<T>(string language, Func<T> body)
    {
        var prev = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                SupportedLanguages.Contains(language ?? "") ? language! : "en");
            return body();
        }
        catch (CultureNotFoundException)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            return body();
        }
        finally { CultureInfo.CurrentUICulture = prev; }
    }

    /// <summary>Branded HTML shell for the invite/reminder mail — table-based +
    /// inline styles so it renders consistently across email clients (Outlook
    /// desktop included). Dynamic text pieces must already be HTML-encoded by
    /// the caller; `url` is inserted only as an href/plain link, never as markup.</summary>
    private static string BuildInviteHtml(string introHtml, string ctaLabel, string url, string expiryNoteHtml)
    {
        var encodedUrl = System.Net.WebUtility.HtmlEncode(url);
        return $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f2f4f7;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f2f4f7;padding:32px 16px;">
            <tr><td align="center">
              <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;width:100%;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,.08);">
                <tr><td style="background:linear-gradient(135deg,#00A0FB 0%,#00EB86 100%);padding:28px 32px;">
                  <span style="font-size:20px;font-weight:700;color:#ffffff;letter-spacing:.2px;">NimShare</span>
                </td></tr>
                <tr><td style="padding:32px 32px 8px;">
                  <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#231F20;">{{introHtml}}</p>
                  <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                    <tr><td style="border-radius:8px;background:#00A0FB;">
                      <a href="{{encodedUrl}}" style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:8px;">{{ctaLabel}}</a>
                    </td></tr>
                  </table>
                  <p style="margin:0 0 4px;font-size:13px;line-height:1.6;color:#6b7280;">{{expiryNoteHtml}}</p>
                  <p style="margin:0 0 24px;font-size:12px;line-height:1.6;color:#9ca3af;word-break:break-all;">{{encodedUrl}}</p>
                </td></tr>
                <tr><td style="padding:16px 32px 28px;border-top:1px solid #eef0f3;">
                  <p style="margin:0;font-size:12px;color:#9ca3af;">— NimShare</p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body></html>
        """;
    }

    // ── Admin: send invite ─────────────────────────────────────────────────
    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/invite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string email, string displayName, string role, string language, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            TempData["Error"] = _l["err.invalid_email"].Value;
            return RedirectToAction("List", "Users");
        }
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
        {
            TempData["Error"] = _l["err.user_exists"].Value;
            return RedirectToAction("List", "Users");
        }

        // 32-byte random token; store only its bcrypt hash server-side.
        // (Heap allocation on purpose — stackalloc + Span<byte> is not permitted in async methods.)
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var invite = new Invitation
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim(),
            Role = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User,
            TokenHash = _hasher.Hash(token),
            InvitedByUserId = me.Id,
            Language = SupportedLanguages.Contains(language ?? "") ? language!.ToLowerInvariant() : "en",
        };
        _db.Invitations.Add(invite);
        await _db.SaveChangesAsync(ct);

        // Build the acceptance URL from the current request scheme+host.
        var url = Request.PublicUrl($"/accept-invite/{invite.Id}?t={token}");
        var expiry = invite.ExpiresAt.ToString("u");
        var (subject, body, html) = WithCulture(invite.Language, () =>
        {
            var t = _localizerFactory.Create(typeof(SharedResources));
            var encName = System.Net.WebUtility.HtmlEncode(me.DisplayName);
            var encEmail = System.Net.WebUtility.HtmlEncode(me.Email);
            return (
                t["invite.email.subject", me.DisplayName].Value,
                t["invite.email.body", me.DisplayName, me.Email, url, expiry].Value,
                BuildInviteHtml(
                    t["invite.email.intro", encName, encEmail].Value,
                    t["invite.email.cta"].Value,
                    url,
                    t["invite.email.expiry_note", expiry].Value)
            );
        });
        try
        {
            await _gateway.SendAsync(email, subject, body, html, attachments: null, ct);
            TempData["Notice"] = string.Format(_l["notice.invite_sent"].Value, email);
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Invite saved but email failed: {ex.Message}. Copy this URL manually: {url}";
        }
        return RedirectToAction("List", "Users");
    }

    // ── Recipient: accept invite ───────────────────────────────────────────
    [AllowAnonymous]
    [HttpGet("/accept-invite/{id:guid}")]
    public async Task<IActionResult> Accept(Guid id, string t, CancellationToken ct)
    {
        var invite = await ValidateAsync(id, t, ct);
        if (invite is null) return View("Invalid");
        return View(new AcceptInviteViewModel(id, t, invite.Email, invite.DisplayName));
    }

    [AllowAnonymous]
    [HttpPost("/accept-invite/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptPost(Guid id, string t, string displayName, string password, string passwordConfirm, CancellationToken ct)
    {
        var invite = await ValidateAsync(id, t, ct);
        if (invite is null) return View("Invalid");
        if (password != passwordConfirm)
        {
            ModelState.AddModelError("", "Passwords do not match.");
            return View("Accept", new AcceptInviteViewModel(id, t, invite.Email, invite.DisplayName));
        }
        try
        {
            var user = await _auth.CreateAsync(invite.Email, displayName, password, invite.Role, ct);
            invite.UsedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _auth.SignInAsync(HttpContext, user, persistent: false);
            return RedirectToAction("Dashboard", "Home");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View("Accept", new AcceptInviteViewModel(id, t, invite.Email, invite.DisplayName));
        }
    }

    private async Task<Invitation?> ValidateAsync(Guid id, string t, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(t)) return null;
        var invite = await _db.Invitations.FindAsync(new object[] { id }, ct);
        if (invite is null) return null;
        if (invite.UsedAt is not null || invite.RevokedAt is not null) return null;
        if (invite.ExpiresAt < DateTimeOffset.UtcNow) return null;
        if (!_hasher.Verify(t, invite.TokenHash)) return null;
        return invite;
    }

    // v1.10.92: Admin-Actions für die Einladungs-Übersicht auf /settings/users.
    // Der Token existiert nur als bcrypt-Hash — bei „Neu senden" muss ein
    // NEUER Token generiert werden (der alte kann nicht rekonstruiert
    // werden). Deshalb rotieren wir Token + ExpiresAt beim Resend.

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/invite/{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var inv = await _db.Invitations.FindAsync(new object[] { id }, ct);
        if (inv is null) { TempData["Error"] = "Einladung nicht gefunden."; return RedirectToAction("List", "Users"); }
        if (inv.UsedAt is not null) { TempData["Error"] = "Bereits angenommen — kann nicht mehr widerrufen werden."; return RedirectToAction("List", "Users"); }
        inv.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = $"Einladung an {inv.Email} widerrufen.";
        return RedirectToAction("List", "Users");
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/invite/{id:guid}/resend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var inv = await _db.Invitations.FindAsync(new object[] { id }, ct);
        if (inv is null) { TempData["Error"] = "Einladung nicht gefunden."; return RedirectToAction("List", "Users"); }
        if (inv.UsedAt is not null) { TempData["Error"] = "Bereits angenommen."; return RedirectToAction("List", "Users"); }

        // Token rotieren (alter Hash nicht mehr rückrechenbar).
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        inv.TokenHash = _hasher.Hash(token);
        inv.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        inv.RevokedAt = null;
        await _db.SaveChangesAsync(ct);

        var url = Request.PublicUrl($"/accept-invite/{inv.Id}?t={token}");
        var expiry = inv.ExpiresAt.ToString("u");
        var (subject, body, html) = WithCulture(inv.Language, () =>
        {
            var t = _localizerFactory.Create(typeof(SharedResources));
            var encName = System.Net.WebUtility.HtmlEncode(me.DisplayName);
            var encEmail = System.Net.WebUtility.HtmlEncode(me.Email);
            return (
                t["invite.email.reminder.subject", me.DisplayName].Value,
                t["invite.email.reminder.body", me.DisplayName, me.Email, url, expiry].Value,
                BuildInviteHtml(
                    t["invite.email.intro", encName, encEmail].Value,
                    t["invite.email.cta"].Value,
                    url,
                    t["invite.email.expiry_note", expiry].Value)
            );
        });
        try
        {
            await _gateway.SendAsync(inv.Email, subject, body, html, attachments: null, ct);
            TempData["Notice"] = $"Einladung an {inv.Email} erneut gesendet.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Neuer Link generiert aber Email-Versand gescheitert: {ex.Message}. Manueller Link: {url}";
        }
        return RedirectToAction("List", "Users");
    }

    /// <summary>Generiert einen NEUEN Einladungs-Link ohne Email zu senden
    /// (für den „Link kopieren"-Fall wo der Admin ihn manuell weitergibt,
    /// z.B. per Signal/WhatsApp). Rotiert Token wie Resend.</summary>
    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/invite/{id:guid}/get-link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GetLink(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var inv = await _db.Invitations.FindAsync(new object[] { id }, ct);
        if (inv is null) { TempData["Error"] = "Einladung nicht gefunden."; return RedirectToAction("List", "Users"); }
        if (inv.UsedAt is not null) { TempData["Error"] = "Bereits angenommen."; return RedirectToAction("List", "Users"); }

        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        inv.TokenHash = _hasher.Hash(token);
        inv.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        inv.RevokedAt = null;
        await _db.SaveChangesAsync(ct);
        var url = Request.PublicUrl($"/accept-invite/{inv.Id}?t={token}");
        // Als TempData weitergeben damit die View einen „Copy"-Toast rendern kann.
        TempData["InviteLink"] = url;
        TempData["InviteLinkEmail"] = inv.Email;
        return RedirectToAction("List", "Users");
    }
}

public record AcceptInviteViewModel(Guid Id, string Token, string Email, string DisplayName);
