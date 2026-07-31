using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// JSON auth endpoints for mobile clients (iOS / Android). Same lookup path as
/// the Razor login flow — issues a JWT for /api/v1/* usage instead of dropping
/// a cookie.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public class AuthApiController : ControllerBase
{
    private readonly ILocalAuthService _auth;
    private readonly IJwtTokenService _jwt;
    private readonly ICurrentUserService _current;

    public AuthApiController(ILocalAuthService auth, IJwtTokenService jwt, ICurrentUserService current)
    {
        _auth = auth;
        _jwt = jwt;
        _current = current;
    }

    public record LoginRequest(string Email, string Password);
    public record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);
    public record TotpChallengeResponse(bool RequiresTotp, string ChallengeToken);
    public record TotpSubmitRequest(string ChallengeToken, string Code);
    public record UserDto(Guid Id, string Email, string DisplayName, string Role, string? AvatarUrl, long QuotaBytes, string PreferredCulture);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req,
        [FromServices] ITotpChallengeStore totpStore, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Email) || string.IsNullOrEmpty(req.Password))
            return BadRequest();
        var user = await _auth.AuthenticateAsync(req.Email, req.Password, ct);
        if (user is null) return Unauthorized();
        // 2FA enrolled? Do not issue a token yet — hand back a short-lived
        // challenge token the client redeems together with a TOTP code.
        if (user.TotpEnabled)
        {
            var challenge = totpStore.Create(user.Id, TimeSpan.FromMinutes(5));
            return Ok(new TotpChallengeResponse(true, challenge));
        }
        var token = _jwt.Issue(user, out var exp);
        return Ok(new LoginResponse(token, exp, ToDto(user)));
    }

    [AllowAnonymous]
    [HttpPost("login/totp")]
    public async Task<IActionResult> LoginTotp([FromBody] TotpSubmitRequest req,
        [FromServices] ITotpChallengeStore totpStore, [FromServices] ITotpService totp,
        [FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.ChallengeToken) || string.IsNullOrEmpty(req.Code))
            return BadRequest();
        var userId = totpStore.Consume(req.ChallengeToken);
        if (userId is null) return Problem(statusCode: 401, title: "Challenge abgelaufen oder ungültig.");
        var user = await db.Users.FindAsync(new object[] { userId.Value }, ct);
        if (user is null || !user.TotpEnabled || string.IsNullOrEmpty(user.TotpSecret))
            return Unauthorized();
        if (!totp.Verify(user.TotpSecret, req.Code))
            return Problem(statusCode: 401, title: "Code falsch.");
        var token = _jwt.Issue(user, out var exp);
        return Ok(new LoginResponse(token, exp, ToDto(user)));
    }

    [Authorize(Policy = "ApiUser")]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var me = await _current.GetOrProvisionAsync(User, ct);
        return Ok(ToDto(me));
    }

    private static readonly HashSet<string> SupportedCultures =
        new(new[] { "en", "de", "fr", "it", "es", "nl" }, StringComparer.OrdinalIgnoreCase);

    public record SetCultureRequest(string Code);

    /// <summary>
    /// v1.11.63 — JSON twin of the web /set-culture endpoint, for iOS. There
    /// was no way for the app to ever write User.PreferredCulture (it just
    /// sat at the DB default "en" forever), so the "Sprache" row on the
    /// Profil screen showed a stale value even for German-speaking users.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("me/culture")]
    public async Task<IActionResult> SetCulture([FromBody] SetCultureRequest req, [FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        var code = (req.Code ?? "").Trim().ToLowerInvariant();
        if (!SupportedCultures.Contains(code)) return Problem(statusCode: 422, title: "Unsupported language code.");
        var me = await _current.GetOrProvisionAsync(User, ct);
        me.PreferredCulture = code;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(me));
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.AvatarUrl, u.QuotaBytes, u.PreferredCulture);

    // ── Forgot / reset password (v1.11.64) — JSON twin of the web flow in
    // AccountController, so iOS has the same self-service reset. Always
    // answers 200 for forgot-password regardless of whether the email
    // exists, to avoid user enumeration.

    public record ForgotPasswordRequest(string Email);

    [AllowAnonymous]
    [EnableRateLimiting("public-share")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req,
        [FromServices] NimShareDbContext db, [FromServices] IPasswordHasher hasher,
        [FromServices] IEmailGatewayService gateway, [FromServices] IStringLocalizerFactory localizerFactory,
        CancellationToken ct)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(email))
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is not null)
            {
                var raw = RandomNumberGenerator.GetBytes(32);
                var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
                var reset = new NimShare.Core.Entities.PasswordResetToken
                {
                    UserId = user.Id,
                    Email = user.Email,
                    TokenHash = hasher.Hash(token),
                };
                db.PasswordResetTokens.Add(reset);
                await db.SaveChangesAsync(ct);

                var url = Request.PublicUrl($"/reset-password/{reset.Id}?t={token}");
                var expiry = reset.ExpiresAt.ToString("u");
                var (subject, body, html) = InvitationsController.WithCulture(user.PreferredCulture, () =>
                {
                    var t = localizerFactory.Create(typeof(SharedResources));
                    return (
                        t["reset.email.subject"].Value,
                        t["reset.email.body", url, expiry].Value,
                        InvitationsController.BuildInviteHtml(
                            t["reset.email.intro"].Value,
                            t["reset.email.cta"].Value,
                            url,
                            t["invite.email.expiry_note", expiry].Value)
                    );
                });
                try { await gateway.SendAsync(user.Email, subject, body, html, attachments: null, ct); }
                catch { /* generic response either way */ }
            }
        }
        return Ok();
    }

    public record ResetPasswordRequest(Guid Id, string Token, string NewPassword);

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req,
        [FromServices] NimShareDbContext db, [FromServices] IPasswordHasher hasher, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Token) || string.IsNullOrEmpty(req.NewPassword))
            return BadRequest();
        if (req.NewPassword.Length < 8)
            return Problem(statusCode: 422, title: "Password too short.");

        var reset = await db.PasswordResetTokens.FindAsync(new object[] { req.Id }, ct);
        if (reset is null || reset.UsedAt is not null || reset.ExpiresAt < DateTimeOffset.UtcNow || !hasher.Verify(req.Token, reset.TokenHash))
            return Problem(statusCode: 401, title: "Reset link expired or invalid.");

        var user = await db.Users.FindAsync(new object[] { reset.UserId }, ct);
        if (user is null) return Problem(statusCode: 401, title: "Reset link expired or invalid.");

        user.PasswordHash = hasher.Hash(req.NewPassword);
        reset.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok();
    }
}
