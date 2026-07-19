using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

public class FavoritesPageController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public FavoritesPageController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "WebUser")]
    [HttpGet("/favorites")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var favs = await _db.UserFavorites
            .Where(f => f.UserId == me.Id
                && (f.FileId == null || f.File!.Status != StorageFileStatus.Deleted))
            .Include(f => f.File)
            .Include(f => f.Folder)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
        ViewData["Favorites"] = favs;
        return View("~/Views/Favorites/Index.cshtml");
    }

    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "WebUser")]
    [HttpGet("/shared-with-me")]
    public async Task<IActionResult> SharedWithMe(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var myGroupIds = await _db.GroupMemberships
            .Where(m => m.UserId == me.Id).Select(m => m.GroupId).ToListAsync(ct);
        var shares = await _db.DirectShares
            .Where(s => (s.TargetUserId == me.Id || (s.TargetGroupId != null && myGroupIds.Contains(s.TargetGroupId.Value)))
                && s.SharedByUserId != me.Id
                && (s.File == null || s.File.OwnerId != me.Id)
                && (s.Folder == null || s.Folder.OwnerUserId != me.Id)
                && (s.FileId == null || s.File!.Status != StorageFileStatus.Deleted))
            .Include(s => s.File).Include(s => s.Folder).Include(s => s.SharedByUser).Include(s => s.TargetGroup)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        ViewData["Shares"] = shares;
        return View("~/Views/Favorites/SharedWithMe.cshtml");
    }
}

[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/favorites")]
public class FavoritesController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;
    private readonly IFolderService _folders;

    public FavoritesController(NimShareDbContext db, ICurrentUserService users,
        IFileAccessService access, IFolderService folders)
    {
        _db = db; _users = users; _access = access; _folders = folders;
    }

    public record ToggleReq(Guid? FileId, Guid? FolderId);
    public record FavoriteDto(Guid Id, string Kind, Guid TargetId, string Name, DateTimeOffset CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.UserFavorites
            .Where(f => f.UserId == me.Id)
            .Include(f => f.File)
            .Include(f => f.Folder)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
        var dtos = rows.Select(f => new FavoriteDto(
            f.Id,
            f.FileId is not null ? "file" : "folder",
            f.FileId ?? f.FolderId!.Value,
            f.File?.Name ?? f.Folder?.Name ?? "?",
            f.CreatedAt)).ToList();
        return Ok(dtos);
    }

    /// <summary>Toggles favorite state — returns 200 with { starred: bool }.</summary>
    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle([FromBody] ToggleReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if ((req.FileId is null) == (req.FolderId is null))
            return Problem(statusCode: 422, title: "Provide exactly one of FileId or FolderId.");

        if (req.FileId is Guid fid)
        {
            var file = await _db.Files.FindAsync(new object[] { fid }, ct);
            if (file is null) return NotFound();
            if (!await _access.CanReadAsync(me, file, ct)) return Forbid();
        }
        else
        {
            var folder = await _db.Folders.FindAsync(new object[] { req.FolderId!.Value }, ct);
            if (folder is null) return NotFound();
            if (!await _folders.CanReadAsync(folder, me, ct)) return Forbid();
        }

        var existing = await _db.UserFavorites.FirstOrDefaultAsync(f =>
            f.UserId == me.Id && f.FileId == req.FileId && f.FolderId == req.FolderId, ct);
        if (existing is not null)
        {
            _db.UserFavorites.Remove(existing);
            await _db.SaveChangesAsync(ct);
            return Ok(new { starred = false });
        }
        _db.UserFavorites.Add(new UserFavorite { UserId = me.Id, FileId = req.FileId, FolderId = req.FolderId });
        await _db.SaveChangesAsync(ct);
        return Ok(new { starred = true });
    }
}
