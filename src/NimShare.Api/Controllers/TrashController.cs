using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Trash — soft-deleted files can be restored or purged. Deleted files remain
/// visible only to their owner and admins; blobs are kept until purge.
/// </summary>
[Authorize(Policy = "WebUser")]
public class TrashController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IBlobStorageService _blobs;

    public TrashController(NimShareDbContext db, ICurrentUserService users, IBlobStorageService blobs)
    {
        _db = db; _users = users; _blobs = blobs;
    }

    [HttpGet("/trash")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var query = _db.Files.Where(f => f.Status == StorageFileStatus.Deleted);
        // Non-admins only see their own trashed files.
        if (me.Role != UserRole.Admin)
            query = query.Where(f => f.OwnerId == me.Id);
        var items = await query
            .Include(f => f.Owner)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync(ct);
        ViewData["Files"] = items;
        return View("Trash");
    }

    [HttpPost("/trash/{id:guid}/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { id }, ct);
        if (file is null) return NotFound();
        if (me.Role != UserRole.Admin && file.OwnerId != me.Id) return Forbid();
        if (file.Status != StorageFileStatus.Deleted) return BadRequest();
        file.Status = StorageFileStatus.Ready;
        file.DeletedAt = null;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = "Restored " + file.Name;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/trash/{id:guid}/purge")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Purge(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { id }, ct);
        if (file is null) return NotFound();
        if (me.Role != UserRole.Admin && file.OwnerId != me.Id) return Forbid();
        var path = file.BlobPath;
        _db.Files.Remove(file);
        await _db.SaveChangesAsync(ct);
        try { await _blobs.DeleteAsync(path, ct); } catch { /* orphaned bytes, ignore */ }
        TempData["Notice"] = "Purged permanently";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/trash/empty")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Empty(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var query = _db.Files.Where(f => f.Status == StorageFileStatus.Deleted);
        if (me.Role != UserRole.Admin) query = query.Where(f => f.OwnerId == me.Id);
        var files = await query.ToListAsync(ct);
        var paths = files.Select(f => f.BlobPath).ToList();
        _db.Files.RemoveRange(files);
        await _db.SaveChangesAsync(ct);
        foreach (var p in paths)
        {
            try { await _blobs.DeleteAsync(p, ct); } catch { /* ignore */ }
        }
        TempData["Notice"] = files.Count + " permanently deleted";
        return RedirectToAction(nameof(Index));
    }
}
