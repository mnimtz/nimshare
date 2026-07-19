using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>Personal API tokens and webhook subscriptions.</summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/dev")]
public class DevApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IPasswordHasher _hasher;
    private readonly IDataProtector _protector;

    public DevApiController(NimShareDbContext db, ICurrentUserService users,
        IPasswordHasher hasher, IDataProtectionProvider dp)
    {
        _db = db; _users = users; _hasher = hasher;
        _protector = dp.CreateProtector("NimShare.Webhook.Secret.v1");
    }

    // ── API Tokens ──
    public record TokenDto(Guid Id, string Name, string Prefix, string? Scopes,
        DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);
    public record CreateTokenReq(string Name, string? Scopes, DateTimeOffset? ExpiresAt);
    public record CreatedTokenDto(TokenDto Token, string RawToken);

    [HttpGet("tokens")]
    public async Task<IActionResult> ListTokens(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.ApiTokens.Where(t => t.OwnerUserId == me.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TokenDto(t.Id, t.Name, t.TokenPrefix, t.Scopes,
                t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("tokens")]
    public async Task<IActionResult> CreateToken([FromBody] CreateTokenReq req, CancellationToken ct)
    {
        // Refuse token creation from other API-token holders: otherwise a
        // "files:read" scoped token could mint itself a "*" token for the
        // same user, defeating scoping entirely.
        if (User.HasClaim(c => c.Type == "nimshare.api_token"))
            return Problem(statusCode: 403, title: "Not allowed from an API token",
                detail: "Tokens can only be created from a cookie session in /settings/dev.");
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest();
        // 32-byte random, base64-url-encoded — matches invite tokens.
        var raw = "ns_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var prefix = raw[..8];
        var t = new ApiToken
        {
            OwnerUserId = me.Id,
            Name = req.Name.Trim(),
            TokenHash = _hasher.Hash(raw),
            TokenPrefix = prefix,
            Scopes = string.IsNullOrWhiteSpace(req.Scopes) ? null : req.Scopes.Trim(),
            ExpiresAt = req.ExpiresAt,
        };
        _db.ApiTokens.Add(t);
        await _db.SaveChangesAsync(ct);
        return Ok(new CreatedTokenDto(new TokenDto(t.Id, t.Name, t.TokenPrefix, t.Scopes,
            t.CreatedAt, t.ExpiresAt, null, null), raw));
    }

    [HttpDelete("tokens/{id:guid}")]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var t = await _db.ApiTokens.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (t is null) return NotFound();
        t.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Webhooks ──
    public record WebhookDto(Guid Id, string Url, string? Events, bool IsActive,
        DateTimeOffset CreatedAt, DateTimeOffset? LastDeliveredAt, int FailureCount);
    public record CreateWebhookReq(string Url, string Secret, string? Events);

    [HttpGet("webhooks")]
    public async Task<IActionResult> ListWebhooks(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.Webhooks.Where(w => w.OwnerUserId == me.Id)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new WebhookDto(w.Id, w.Url, w.Events, w.IsActive,
                w.CreatedAt, w.LastDeliveredAt, w.FailureCount))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("webhooks")]
    public async Task<IActionResult> CreateWebhook([FromBody] CreateWebhookReq req, CancellationToken ct)
    {
        if (User.HasClaim(c => c.Type == "nimshare.api_token"))
            return Problem(statusCode: 403, title: "Not allowed from an API token",
                detail: "Webhooks can only be created from a cookie session in /settings/dev.");
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.Secret)) return BadRequest();
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var u) || !(u.Scheme == "http" || u.Scheme == "https"))
            return Problem(statusCode: 422, title: "URL muss http(s) sein.");
        var w = new Webhook
        {
            OwnerUserId = me.Id,
            Url = req.Url.Trim(),
            SecretEncrypted = _protector.Protect(req.Secret),
            Events = string.IsNullOrWhiteSpace(req.Events) ? null : req.Events.Trim(),
        };
        _db.Webhooks.Add(w);
        await _db.SaveChangesAsync(ct);
        return Ok(new WebhookDto(w.Id, w.Url, w.Events, w.IsActive, w.CreatedAt, null, 0));
    }

    [HttpDelete("webhooks/{id:guid}")]
    public async Task<IActionResult> DeleteWebhook(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var w = await _db.Webhooks.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (w is null) return NotFound();
        _db.Webhooks.Remove(w);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
