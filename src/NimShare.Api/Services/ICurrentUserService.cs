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
        // Local cookie sign-in path: NameIdentifier holds the user's Guid directly.
        if (principal.HasClaim(c => c.Type == "local" && c.Value == "true"))
        {
            var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? throw new UnauthorizedAccessException("No sub claim on local principal.");
            if (!Guid.TryParse(sub, out var userId))
                throw new UnauthorizedAccessException("Bad sub claim.");
            var existing = await _db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct)
                           ?? throw new UnauthorizedAccessException("User no longer exists.");
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        // Entra ID path: look up (or auto-provision) by object id.
        var oid = principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? principal.FindFirst("oid")?.Value
                  ?? throw new UnauthorizedAccessException("No oid claim on principal.");

        var existingByOid = await _db.Users.SingleOrDefaultAsync(x => x.EntraOid == oid, ct);
        if (existingByOid is not null)
        {
            existingByOid.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existingByOid;
        }

        var name = principal.FindFirst("name")?.Value ?? principal.Identity?.Name ?? "New user";
        var email = (principal.FindFirst("preferred_username")?.Value
                     ?? principal.FindFirst(ClaimTypes.Email)?.Value
                     ?? "").Trim().ToLowerInvariant();

        // If a local user with the same email exists, link the Entra oid to it.
        if (!string.IsNullOrEmpty(email))
        {
            var linkable = await _db.Users.SingleOrDefaultAsync(x => x.Email == email, ct);
            if (linkable is not null)
            {
                linkable.EntraOid = oid;
                linkable.LastSeenAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return linkable;
            }
        }

        var user = new User
        {
            EntraOid = oid,
            DisplayName = name,
            Email = email,
            Role = UserRole.User,
            IsActive = true,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }
}
