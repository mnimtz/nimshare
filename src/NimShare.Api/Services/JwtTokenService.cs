using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public interface IJwtTokenService
{
    string Issue(User user, out DateTimeOffset expiresAt);
    TokenValidationParameters ValidationParameters { get; }
}

public class JwtTokenService : IJwtTokenService
{
    public const string SchemeName = "NimShareLocalJwt";
    public const string Issuer = "nimshare";
    public const string Audience = "nimshare-clients";

    private readonly SymmetricSecurityKey _signingKey;
    private readonly TimeSpan _lifetime = TimeSpan.FromDays(30);

    public JwtTokenService(IConfiguration cfg, IHostEnvironment env, ILogger<JwtTokenService> log)
    {
        // Prefer a DEDICATED signing secret (LocalJwt:Signing). Historically this fell back to
        // IpHash:Salt — reusing one secret for two unrelated purposes (30-day admin-capable token
        // signing + IP pseudonymisation). If the salt ever leaks (it is meant to be rotatable and
        // feeds stored values), that reuse makes admin JWTs forgeable. v1.11.81: keep the fallback
        // for backward compatibility, but refuse the well-known placeholder in production and warn
        // whenever no dedicated key is configured.
        var dedicated = cfg["LocalJwt:Signing"];
        var raw = dedicated ?? cfg["IpHash:Salt"] ?? "override-with-env-var-in-production";
        if (!env.IsDevelopment())
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "override-with-env-var-in-production")
                throw new InvalidOperationException(
                    "LocalJwt:Signing (or at minimum a non-default IpHash:Salt) must be set to a strong secret outside Development.");
            if (string.IsNullOrWhiteSpace(dedicated))
                log.LogWarning("[STARTUP] LocalJwt:Signing is not set — deriving the JWT signing key from IpHash:Salt (secret reuse). Set a dedicated high-entropy LocalJwt:Signing.");
        }
        // Widen to 32 bytes with SHA-256 so HS256 has enough entropy regardless of input length.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("NimShare.Local.JWT:" + raw));
        _signingKey = new SymmetricSecurityKey(bytes);
    }

    public string Issue(User user, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("local", "true"),
        };
        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer,
        ValidateAudience = true, ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _signingKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(5),
    };
}
