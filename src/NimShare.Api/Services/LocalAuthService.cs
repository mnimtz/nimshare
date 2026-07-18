using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public interface ILocalAuthService
{
    /// <summary>True while the users table is empty — the app should force the setup wizard.</summary>
    Task<bool> IsFirstRunAsync(CancellationToken ct = default);

    /// <summary>Creates the first user (Admin) or additional users. Enforces email uniqueness.</summary>
    Task<User> CreateAsync(string email, string displayName, string password, UserRole role, CancellationToken ct = default);

    /// <summary>Verifies email+password. Returns null if credentials are wrong or the account is disabled.</summary>
    Task<User?> AuthenticateAsync(string email, string password, CancellationToken ct = default);

    Task SignInAsync(HttpContext ctx, User user, bool persistent);
    Task SignOutAsync(HttpContext ctx);
}

public class LocalAuthService : ILocalAuthService
{
    private readonly NimShareDbContext _db;
    private readonly IPasswordHasher _hasher;

    public LocalAuthService(NimShareDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public Task<bool> IsFirstRunAsync(CancellationToken ct = default) =>
        _db.Users.AnyAsync(ct).ContinueWith(t => !t.Result, ct);

    public async Task<User> CreateAsync(string email, string displayName, string password, UserRole role, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new ArgumentException("Invalid email.", nameof(email));
        if (password is null || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.", nameof(password));
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            throw new InvalidOperationException("A user with that email already exists.");

        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            PasswordHash = _hasher.Hash(password),
            Role = role,
            IsActive = true,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<User?> AuthenticateAsync(string email, string password, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !user.IsActive || string.IsNullOrEmpty(user.PasswordHash)) return null;
        if (!_hasher.Verify(password ?? "", user.PasswordHash)) return null;
        user.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public Task SignInAsync(HttpContext ctx, User user, bool persistent)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("local", "true"),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = persistent,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(persistent ? 30 : 1),
        });
    }

    public Task SignOutAsync(HttpContext ctx)
        => ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}
