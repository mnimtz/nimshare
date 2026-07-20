using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Personal-scope shortcuts to any file the caller can read. Doesn't
/// duplicate the blob — a pin is just a small row that surfaces the target
/// file in the caller's personal browser and lets them create their own
/// branded share link on top of it (see ShareController.ResolveThemeAsync).
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/file-pins")]
public class FilePinsController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;
    private readonly IStringLocalizer<SharedResources> _t;

    public FilePinsController(NimShareDbContext db, ICurrentUserService users, IFileAccessService access,
        IStringLocalizer<SharedResources> t)
    {
        _db = db; _users = users; _access = access; _t = t;
    }

    public record PinDto(Guid Id, Guid FileId, string FileName, long SizeBytes,
        string ContentType, string OwnerName, string Scope, string? Note, DateTimeOffset PinnedAt);
    public record CreatePinReq(Guid FileId, string? Note);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var pins = await _db.FilePins
            .Where(p => p.UserId == me.Id)
            .Include(p => p.File).ThenInclude(f => f!.Owner)
            .OrderByDescending(p => p.PinnedAt)
            .ToListAsync(ct);
        return Ok(pins.Where(p => p.File is not null).Select(p => new PinDto(
            p.Id, p.FileId, p.File!.Name, p.File.SizeBytes, p.File.ContentType,
            p.File.Owner?.DisplayName ?? "", p.File.Scope.ToString(), p.Note, p.PinnedAt)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePinReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { req.FileId }, ct);
        if (file is null || file.Status != StorageFileStatus.Ready) return NotFound();
        if (!await _access.CanReadAsync(me, file, ct)) return Forbid();
        // Pinning your OWN Personal file is pointless — refuse cleanly so the
        // UI can render a "already in your Personal folder" hint.
        if (file.OwnerId == me.Id && file.Scope == FileScope.Personal)
            return Conflict(new { message = _t["pin.copy_conflict"].Value });
        var existing = await _db.FilePins
            .SingleOrDefaultAsync(p => p.UserId == me.Id && p.FileId == file.Id, ct);
        if (existing is not null)
            return Conflict(new { message = _t["pin.copy_conflict"].Value, id = existing.Id });
        // Cap note length to match entity's HasMaxLength(500) — otherwise SQL
        // Server truncates with SqlException 8152 and Sqlite silently overruns.
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is not null && note.Length > 500) note = note[..500];
        var pin = new FilePin
        {
            UserId = me.Id,
            FileId = file.Id,
            Note = note,
        };
        _db.FilePins.Add(pin);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent POST (double-click, retry) tripped the unique index.
            // Treat as "already exists" instead of surfacing 500.
            var winner = await _db.FilePins
                .SingleOrDefaultAsync(p => p.UserId == me.Id && p.FileId == file.Id, ct);
            if (winner is not null)
                return Conflict(new { message = _t["pin.copy_conflict"].Value, id = winner.Id });
            throw;
        }
        return CreatedAtAction(nameof(List), new { id = pin.Id }, new { id = pin.Id });
    }

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> Unpin(Guid fileId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var pin = await _db.FilePins.SingleOrDefaultAsync(p => p.UserId == me.Id && p.FileId == fileId, ct);
        if (pin is null) return NotFound();
        _db.FilePins.Remove(pin);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
