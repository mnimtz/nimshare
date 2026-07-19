using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Per-file version history: list, upload-new-version, restore. The current
/// version lives at StorageFile.BlobPath; older versions live at
/// users/{ownerId}/{fileId}/versions/{n}/{name}. Retention is per-file
/// (StorageFile.KeepVersions, default 10).
/// </summary>
[ApiController]
[Route("api/v1/files/{fileId:guid}/versions")]
[Authorize(Policy = "ApiUser")]
public class FileVersionsController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;
    private readonly ICurrentUserService _users;
    private readonly IFileAccessService _access;

    public FileVersionsController(NimShareDbContext db, IBlobStorageService blobs,
        ICurrentUserService users, IFileAccessService access)
    {
        _db = db; _blobs = blobs; _users = users; _access = access;
    }

    public record VersionDto(Guid Id, int VersionNumber, long SizeBytes, string ContentType,
        string CreatedByName, DateTimeOffset CreatedAt, bool IsCurrent);
    public record NewVersionResponse(Guid VersionId, int VersionNumber, string UploadUrl, DateTimeOffset ExpiresAt);

    [HttpGet]
    public async Task<IActionResult> List(Guid fileId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { fileId }, ct);
        if (file is null) return NotFound();
        if (!await _access.CanReadAsync(me, file, ct)) return Forbid();

        var versions = await _db.StorageFileVersions
            .Where(v => v.FileId == fileId)
            .Include(v => v.CreatedByUser)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new VersionDto(v.Id, v.VersionNumber, v.SizeBytes, v.ContentType,
                v.CreatedByUser!.DisplayName, v.CreatedAt, v.VersionNumber == file.VersionNumber))
            .ToListAsync(ct);
        return Ok(versions);
    }

    public record NewVersionRequest(string ContentType, long SizeBytes);

    [HttpPost]
    public async Task<IActionResult> InitNewVersion(Guid fileId, [FromBody] NewVersionRequest req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { fileId }, ct);
        if (file is null) return NotFound();
        // Whoever can delete may also replace bytes (owners, admins, group
        // managers, write-grant recipients).
        if (!await _access.CanDeleteAsync(me, file, ct)) return Forbid();

        // Quota still counts against the owner, delta only.
        var used = await _db.Files.Where(f => f.OwnerId == file.OwnerId && f.Status != StorageFileStatus.Deleted)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        var extra = req.SizeBytes - file.SizeBytes;
        if (extra > 0)
        {
            var owner = await _db.Users.FindAsync(new object[] { file.OwnerId }, ct);
            if (owner is not null && used + extra > owner.QuotaBytes)
                return Problem(statusCode: 413, title: "Quota exceeded.");
        }

        // Freeze the current bytes into a versioned path — the caller uploads
        // the NEW bytes over the current blob path so it stays the "current".
        var nextNo = file.VersionNumber + 1;
        var currentPath = $"users/{file.OwnerId:N}/{file.Id:N}/versions/{file.VersionNumber}/{Sanitise(file.Name)}";
        await _blobs.CopyAsync(file.BlobPath, currentPath, ct);

        var snapshot = new StorageFileVersion
        {
            FileId = file.Id, VersionNumber = file.VersionNumber,
            BlobPath = currentPath, SizeBytes = file.SizeBytes,
            ContentType = file.ContentType, Sha256 = file.Sha256,
            CreatedByUserId = file.OwnerId, CreatedAt = file.CreatedAt,
        };
        _db.StorageFileVersions.Add(snapshot);

        file.VersionNumber = nextNo;
        file.SizeBytes = req.SizeBytes;
        file.ContentType = req.ContentType;
        await _db.SaveChangesAsync(ct);

        var ticket = _blobs.CreateUploadTicket(file.BlobPath);
        return Ok(new NewVersionResponse(snapshot.Id, snapshot.VersionNumber,
            ticket.UploadUrl.ToString(), ticket.ExpiresAt));
    }

    [HttpPost("{versionId:guid}/restore")]
    public async Task<IActionResult> Restore(Guid fileId, Guid versionId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.FindAsync(new object[] { fileId }, ct);
        if (file is null) return NotFound();
        if (!await _access.CanDeleteAsync(me, file, ct)) return Forbid();

        var v = await _db.StorageFileVersions.SingleOrDefaultAsync(x => x.Id == versionId && x.FileId == fileId, ct);
        if (v is null) return NotFound();

        // Snapshot the current bytes then copy the target version onto the current blob path.
        var nextNo = file.VersionNumber + 1;
        var frozenPath = $"users/{file.OwnerId:N}/{file.Id:N}/versions/{file.VersionNumber}/{Sanitise(file.Name)}";
        await _blobs.CopyAsync(file.BlobPath, frozenPath, ct);
        _db.StorageFileVersions.Add(new StorageFileVersion
        {
            FileId = file.Id, VersionNumber = file.VersionNumber,
            BlobPath = frozenPath, SizeBytes = file.SizeBytes,
            ContentType = file.ContentType, Sha256 = file.Sha256,
            CreatedByUserId = file.OwnerId, CreatedAt = file.CreatedAt,
        });
        await _blobs.CopyAsync(v.BlobPath, file.BlobPath, ct);
        file.SizeBytes = v.SizeBytes;
        file.ContentType = v.ContentType;
        file.VersionNumber = nextNo;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string Sanitise(string name) =>
        string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
}
