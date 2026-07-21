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
    private readonly NimShareDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _users;
    private readonly ILocalAuthService _auth;
    private readonly IEmailGatewayService _gateway;
    private readonly IStringLocalizer<SharedResources> _l;

    public InvitationsController(NimShareDbContext db, IPasswordHasher hasher, ICurrentUserService users,
        ILocalAuthService auth, IEmailGatewayService gateway, IStringLocalizer<SharedResources> l)
    {
        _db = db;
        _hasher = hasher;
        _users = users;
        _auth = auth;
        _gateway = gateway;
        _l = l;
    }

    // ── Admin: send invite ─────────────────────────────────────────────────
    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/invite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string email, string displayName, string role, CancellationToken ct)
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
        };
        _db.Invitations.Add(invite);
        await _db.SaveChangesAsync(ct);

        // Build the acceptance URL from the current request scheme+host.
        var url = $"{Request.Scheme}://{Request.Host}/accept-invite/{invite.Id}?t={token}";
        var subject = $"{me.DisplayName} invited you to NimShare";
        var body = $"Hello,\n\n{me.DisplayName} ({me.Email}) has invited you to NimShare.\n\nOpen this link to set your password and sign in:\n{url}\n\nThe link expires on {invite.ExpiresAt:u}.\n\n— NimShare";
        try
        {
            await _gateway.SendAsync(email, subject, body, ct);
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

        var url = $"{Request.Scheme}://{Request.Host}/accept-invite/{inv.Id}?t={token}";
        var subject = $"{me.DisplayName} invited you to NimShare (Erinnerung)";
        var body = $"Hallo,\n\n{me.DisplayName} ({me.Email}) hat dich zu NimShare eingeladen.\n\nÖffne diesen Link um dein Passwort zu setzen:\n{url}\n\nDer Link läuft am {inv.ExpiresAt:u} ab.\n\n— NimShare";
        try
        {
            await _gateway.SendAsync(inv.Email, subject, body, ct);
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
        var url = $"{Request.Scheme}://{Request.Host}/accept-invite/{inv.Id}?t={token}";
        // Als TempData weitergeben damit die View einen „Copy"-Toast rendern kann.
        TempData["InviteLink"] = url;
        TempData["InviteLinkEmail"] = inv.Email;
        return RedirectToAction("List", "Users");
    }
}

public record AcceptInviteViewModel(Guid Id, string Token, string Email, string DisplayName);
