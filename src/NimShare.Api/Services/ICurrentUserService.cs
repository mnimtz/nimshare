using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public interface ICurrentUserService
{
    /// <summary>Ensures a <see cref="User"/> row exists for the signed-in principal and returns it.</summary>
    Task<User> GetOrProvisionAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}

public class CurrentUserService : ICurrentUserService
{
    private readonly NimShareDbContext _db;

    public CurrentUserService(NimShareDbContext db) => _db = db;

    public async Task<User> GetOrProvisionAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var oid = principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? principal.FindFirst("oid")?.Value
                  ?? throw new UnauthorizedAccessException("No oid claim on principal.");

        var existing = await _db.Users.SingleOrDefaultAsync(x => x.EntraOid == oid, ct);
        if (existing is not null)
        {
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var name = principal.FindFirst("name")?.Value ?? principal.Identity?.Name ?? "New user";
        var email = principal.FindFirst("preferred_username")?.Value
                    ?? principal.FindFirst(ClaimTypes.Email)?.Value
                    ?? "";

        var user = new User
        {
            EntraOid = oid,
            DisplayName = name,
            Email = email,
            Role = UserRole.User,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }
}
