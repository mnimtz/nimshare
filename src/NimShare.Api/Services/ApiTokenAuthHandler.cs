using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NimShare.Core.Data;

namespace NimShare.Api.Services;

/// <summary>
/// Accepts personal API tokens issued via /api/v1/dev/tokens.
/// Header: Authorization: NimShare-Token &lt;raw-token&gt;
///     or Authorization: Bearer ns_xxx (auto-detected by ns_ prefix)
/// </summary>
public class ApiTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "NimShareApiToken";

    private readonly NimShareDbContext _db;
    private readonly IPasswordHasher _hasher;

    public ApiTokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> opts,
        ILoggerFactory logger, UrlEncoder encoder,
        NimShareDbContext db, IPasswordHasher hasher)
        : base(opts, logger, encoder)
    {
        _db = db; _hasher = hasher;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHdr))
            return AuthenticateResult.NoResult();
        var v = authHdr.ToString();
        string? raw = null;
        if (v.StartsWith("NimShare-Token ", StringComparison.OrdinalIgnoreCase))
            raw = v["NimShare-Token ".Length..].Trim();
        else if (v.StartsWith("Bearer ns_", StringComparison.OrdinalIgnoreCase))
            raw = v["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(raw)) return AuthenticateResult.NoResult();

        var prefix = raw.Length >= 8 ? raw[..8] : raw;
        var candidates = await _db.ApiTokens
            .Where(t => t.TokenPrefix == prefix && t.RevokedAt == null)
            .ToListAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var t in candidates)
        {
            if (t.ExpiresAt.HasValue && t.ExpiresAt.Value < now) continue;
            if (!_hasher.Verify(raw, t.TokenHash)) continue;

            var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == t.OwnerUserId);
            if (user is null || !user.IsActive)
                return AuthenticateResult.Fail("owner disabled");

            t.LastUsedAt = now;
            await _db.SaveChangesAsync();

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.DisplayName ?? user.Email),
                new(ClaimTypes.Email, user.Email),
                new("nimshare.api_token", t.Id.ToString()),
            };
            if (user.Role == NimShare.Core.Entities.UserRole.Admin)
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            if (!string.IsNullOrEmpty(t.Scopes))
                foreach (var s in t.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    claims.Add(new Claim("nimshare.scope", s.Trim()));
            var id = new ClaimsIdentity(claims, SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(id), SchemeName));
        }
        return AuthenticateResult.Fail("invalid token");
    }
}
