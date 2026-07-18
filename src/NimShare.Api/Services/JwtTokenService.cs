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

    public JwtTokenService(IConfiguration cfg)
    {
        // Signing key: from config LocalJwt:Signing, or derived from IpHash:Salt as a fallback
        // so an admin who set that env-var already has a stable key for tokens too.
        var raw = cfg["LocalJwt:Signing"] ?? cfg["IpHash:Salt"] ?? "override-with-env-var-in-production";
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
