using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.11.63 — JSON twin of InvitationsController's admin invite flow, for iOS
/// user management. Reuses InvitationsController.WithCulture/BuildInviteHtml
/// (made `internal`) so the branded HTML email template stays in one place.
/// </summary>
[ApiController]
[Route("api/v1/invitations")]
[Authorize(Policy = "ApiUser")]
public class InvitationsApiController : ControllerBase
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "en", "de", "fr", "it", "es", "nl" };

    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IPasswordHasher _hasher;
    private readonly IEmailGatewayService _gateway;
    private readonly IStringLocalizerFactory _localizerFactory;

    public InvitationsApiController(NimShareDbContext db, ICurrentUserService users, IPasswordHasher hasher,
        IEmailGatewayService gateway, IStringLocalizerFactory localizerFactory)
    {
        _db = db;
        _users = users;
        _hasher = hasher;
        _gateway = gateway;
        _localizerFactory = localizerFactory;
    }

    public record InvitationDto(Guid Id, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt, string? InvitedByName);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var since = DateTimeOffset.UtcNow.AddDays(-60);
        var invites = await _db.Invitations
            .Include(i => i.InvitedBy)
            .Where(i => i.CreatedAt >= since)
            .OrderByDescending(i => i.CreatedAt)
            .Take(200)
            .Select(i => new InvitationDto(i.Id, i.Email, i.DisplayName, i.Role.ToString(), i.CreatedAt,
                i.ExpiresAt, i.UsedAt, i.RevokedAt, i.InvitedBy != null ? i.InvitedBy.DisplayName : null))
            .ToListAsync(ct);
        return Ok(invites);
    }

    public record InviteReq(string Email, string DisplayName, string Role, string Language);

    [HttpPost]
    public async Task<IActionResult> Send([FromBody] InviteReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            return Problem(statusCode: 422, title: "Invalid email address.");
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Problem(statusCode: 409, title: "A user with that email already exists.");

        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var invite = new Invitation
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? email.Split('@')[0] : req.DisplayName.Trim(),
            Role = string.Equals(req.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User,
            TokenHash = _hasher.Hash(token),
            InvitedByUserId = me.Id,
            Language = SupportedLanguages.Contains(req.Language ?? "") ? req.Language!.ToLowerInvariant() : "en",
        };
        _db.Invitations.Add(invite);
        await _db.SaveChangesAsync(ct);

        var url = Request.PublicUrl($"/accept-invite/{invite.Id}?t={token}");
        var expiry = invite.ExpiresAt.ToString("u");
        var (subject, body, html) = InvitationsController.WithCulture(invite.Language, () =>
        {
            var t = _localizerFactory.Create(typeof(SharedResources));
            var encName = System.Net.WebUtility.HtmlEncode(me.DisplayName);
            var encEmail = System.Net.WebUtility.HtmlEncode(me.Email);
            return (
                t["invite.email.subject", me.DisplayName].Value,
                t["invite.email.body", me.DisplayName, me.Email, url, expiry].Value,
                InvitationsController.BuildInviteHtml(
                    t["invite.email.intro", encName, encEmail].Value,
                    t["invite.email.cta"].Value,
                    url,
                    t["invite.email.expiry_note", expiry].Value)
            );
        });
        try
        {
            await _gateway.SendAsync(email, subject, body, html, attachments: null, ct);
        }
        catch (Exception ex)
        {
            return Ok(new { id = invite.Id, emailSent = false, manualUrl = url, error = ex.Message });
        }
        return Ok(new { id = invite.Id, emailSent = true });
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var inv = await _db.Invitations.FindAsync(new object[] { id }, ct);
        if (inv is null) return NotFound();
        if (inv.UsedAt is not null) return Problem(statusCode: 422, title: "Already accepted — cannot revoke.");
        inv.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/resend")]
    public async Task<IActionResult> Resend(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var inv = await _db.Invitations.FindAsync(new object[] { id }, ct);
        if (inv is null) return NotFound();
        if (inv.UsedAt is not null) return Problem(statusCode: 422, title: "Already accepted.");

        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        inv.TokenHash = _hasher.Hash(token);
        inv.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        inv.RevokedAt = null;
        await _db.SaveChangesAsync(ct);

        var url = Request.PublicUrl($"/accept-invite/{inv.Id}?t={token}");
        var expiry = inv.ExpiresAt.ToString("u");
        var (subject, body, html) = InvitationsController.WithCulture(inv.Language, () =>
        {
            var t = _localizerFactory.Create(typeof(SharedResources));
            var encName = System.Net.WebUtility.HtmlEncode(me.DisplayName);
            var encEmail = System.Net.WebUtility.HtmlEncode(me.Email);
            return (
                t["invite.email.reminder.subject", me.DisplayName].Value,
                t["invite.email.reminder.body", me.DisplayName, me.Email, url, expiry].Value,
                InvitationsController.BuildInviteHtml(
                    t["invite.email.intro", encName, encEmail].Value,
                    t["invite.email.cta"].Value,
                    url,
                    t["invite.email.expiry_note", expiry].Value)
            );
        });
        try
        {
            await _gateway.SendAsync(inv.Email, subject, body, html, attachments: null, ct);
        }
        catch (Exception ex)
        {
            return Ok(new { emailSent = false, manualUrl = url, error = ex.Message });
        }
        return Ok(new { emailSent = true });
    }
}
