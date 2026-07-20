using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;

namespace NimShare.Api.Controllers;

/// <summary>
/// Streams the user's uploaded avatar image from Blob Storage.
/// Access model (v1.10.12):
///   - Anonymous WHEN the target user has opted in via
///     <c>ShowAvatarOnLandings</c>. This is the same consent that already
///     exposes the avatar on public download / signature landings; requiring
///     login here made the &lt;img&gt; in those landings load as 401 for
///     signers who aren't NimShare users, so the alt text ("Marcus") showed
///     through as a broken image.
///   - Auth-required otherwise (in-app profile lookups, admin views etc.).
///     Blocks user-id enumeration for accounts that DIDN'T opt in.
/// </summary>
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

    // v-query is a cache-buster; opted-in avatars are cacheable public
    // (they're already reachable without auth), the others stay private.
    [AllowAnonymous]
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || string.IsNullOrEmpty(user.AvatarBlobPath)) return NotFound();

        // If the user hasn't opted into public landing display, require login
        // — the previous default. Prevents unauthenticated enumeration.
        if (!user.ShowAvatarOnLandings && !(User?.Identity?.IsAuthenticated ?? false))
            return NotFound();

        var (exists, _, contentType) = await _blobs.ProbeAsync(user.AvatarBlobPath, ct);
        if (!exists) return NotFound();

        var stream = new MemoryStream();
        await _blobs.DownloadToAsync(user.AvatarBlobPath, stream, ct);
        stream.Position = 0;
        Response.Headers.CacheControl = user.ShowAvatarOnLandings
            ? "public, max-age=604800"
            : "private, max-age=604800";
        return File(stream, contentType ?? "image/png");
    }
}
