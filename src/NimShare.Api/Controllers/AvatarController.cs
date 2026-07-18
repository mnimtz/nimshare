using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;

namespace NimShare.Api.Controllers;

/// <summary>
/// Streams the user's uploaded avatar image from Blob Storage. Auth-required
/// to prevent unauthenticated user-id enumeration via 200-vs-404 probing.
/// </summary>
[Authorize(Policy = "WebUser")]
[Route("/avatars")]
public class AvatarController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;

    public AvatarController(NimShareDbContext db, IBlobStorageService blobs)
    {
        _db = db;
        _blobs = blobs;
    }

    // v-query is a cache-buster; content cacheable within the browser only (private).
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client)]
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || string.IsNullOrEmpty(user.AvatarBlobPath)) return NotFound();

        var (exists, _, contentType) = await _blobs.ProbeAsync(user.AvatarBlobPath, ct);
        if (!exists) return NotFound();

        var stream = new MemoryStream();
        await _blobs.DownloadToAsync(user.AvatarBlobPath, stream, ct);
        stream.Position = 0;
        return File(stream, contentType ?? "image/png");
    }
}
