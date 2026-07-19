using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// JSON API for the iOS app to enrol / disable TOTP. Web page equivalent
/// lives at /settings/2fa; the two paths do not share Session state — the
/// mobile flow persists the pending secret in the response to the client
/// (it's per-user secret material, no leak).
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/2fa")]
public class TwoFactorApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly ITotpService _totp;

    public TwoFactorApiController(NimShareDbContext db, ICurrentUserService users, ITotpService totp)
    {
        _db = db; _users = users; _totp = totp;
    }

    public record StatusDto(bool Enabled, DateTimeOffset? EnrolledAt);
    public record InitResponse(string Secret, string OtpAuthUri);
    public record VerifyReq(string Secret, string Code);
    public record CodeReq(string Code);

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return Ok(new StatusDto(me.TotpEnabled, me.TotpEnrolledAt));
    }

    /// <summary>Draft a secret. The client shows the QR/URI and demands a code
    /// before persisting via /verify. The secret is only committed on verify.</summary>
    [HttpPost("setup/init")]
    public async Task<IActionResult> Init(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var secret = _totp.GenerateSecret();
        var uri = _totp.BuildOtpAuthUri(secret, me.Email, "NimShare");
        return Ok(new InitResponse(secret, uri));
    }

    /// <summary>Verify the code against the client-provided secret and enrol.</summary>
    [HttpPost("setup/verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Secret) || !_totp.Verify(req.Secret, req.Code))
            return Problem(statusCode: 400, title: "Der Code stimmt nicht.");
        me.TotpSecret = req.Secret;
        me.TotpEnabled = true;
        me.TotpEnrolledAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new StatusDto(true, me.TotpEnrolledAt));
    }

    [HttpPost("disable")]
    public async Task<IActionResult> Disable([FromBody] CodeReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!me.TotpEnabled || string.IsNullOrEmpty(me.TotpSecret)) return NoContent();
        if (!_totp.Verify(me.TotpSecret, req.Code))
            return Problem(statusCode: 400, title: "Falscher Code.");
        me.TotpSecret = null;
        me.TotpEnabled = false;
        me.TotpEnrolledAt = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
