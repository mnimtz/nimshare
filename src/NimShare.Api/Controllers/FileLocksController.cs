using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/files/{id:guid}/lock")]
public class FileLocksController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;

    public FileLocksController(NimShareDbContext db, ICurrentUserService users, IFileAccessService access)
    {
        _db = db; _users = users; _access = access;
    }

    public record LockStatusDto(bool Locked, Guid? ByUserId, string? ByUserName, DateTimeOffset? Until, string? Kind);

    [HttpGet]
    public async Task<IActionResult> Status(Guid id, CancellationToken ct)
    {
        var f = await _db.Files.Include(x => x.LockedByUser)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return NotFound();
        var live = f.LockedUntil is DateTimeOffset lu && lu > DateTimeOffset.UtcNow;
        return Ok(new LockStatusDto(live, live ? f.LockedByUserId : null,
            live ? f.LockedByUser?.DisplayName : null, live ? f.LockedUntil : null, live ? f.LockKind : null));
    }

    /// <summary>Acquire or renew the lock. 30-min sliding TTL.</summary>
    [HttpPost]
    public async Task<IActionResult> Acquire(Guid id, [FromQuery] string? kind, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var f = await _db.Files.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return NotFound();
        // Only writers can lock — otherwise a public-read-only user could freeze the file.
        if (!await _access.CanDeleteAsync(me, f, ct)) return Forbid();
        var now = DateTimeOffset.UtcNow;
        var alive = f.LockedUntil is DateTimeOffset until && until > now;
        if (alive && f.LockedByUserId != me.Id)
            return Problem(statusCode: 423, title: "Datei ist bereits gesperrt.");
        f.LockedByUserId = me.Id;
        f.LockedUntil = now.AddMinutes(30);
        f.LockKind = kind ?? "manual";
        await _db.SaveChangesAsync(ct);
        return Ok(new { until = f.LockedUntil });
    }

    /// <summary>Release the lock. Owner or admin can break someone else's lock.</summary>
    [HttpDelete]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var f = await _db.Files.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return NotFound();
        var isBreaker = me.Role == UserRole.Admin || f.OwnerId == me.Id;
        if (f.LockedByUserId != me.Id && !isBreaker) return Forbid();
        f.LockedByUserId = null;
        f.LockedUntil = null;
        f.LockKind = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
