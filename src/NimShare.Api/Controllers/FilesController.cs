using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Route("api/v1/files")]
[Authorize(Policy = "ApiUser")]
public class FilesController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;
    private readonly ICurrentUserService _users;

    public FilesController(NimShareDbContext db, IBlobStorageService blobs, ICurrentUserService users)
    {
        _db = db;
        _blobs = blobs;
        _users = users;
    }

    public record CreateFileRequest(string Name, long SizeBytes, string ContentType,
        string? Folder, string? Scope, Guid? GroupId, Guid? FolderId);
    public record CreateFileResponse(Guid FileId, string UploadUrl, string UploadMethod, DateTimeOffset ExpiresAt);

    [HttpPost]
    public async Task<ActionResult<CreateFileResponse>> Create([FromBody] CreateFileRequest req,
        [FromServices] IFileAccessService access,
        [FromServices] IFolderService folders,
        CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);

        // Resolve folder — if the client gave a FolderId, use it (new file-browser flow).
        Folder? folder = null;
        if (req.FolderId is Guid folderId)
        {
            folder = await _db.Folders.FindAsync(new object[] { folderId }, ct);
            if (folder is null) return NotFound();
            if (!await folders.CanWriteAsync(folder, user, ct))
                return Problem(statusCode: 403, title: "Cannot upload into this folder");
        }

        // Parse scope; default is Personal (or derive from folder if present).
        var scope = folder?.Scope ?? FileScope.Personal;
        if (folder is null && !string.IsNullOrWhiteSpace(req.Scope) && Enum.TryParse<FileScope>(req.Scope, true, out var parsed))
            scope = parsed;
        var groupId = folder?.OwnerGroupId ?? req.GroupId;
        if (folder is null && !await access.CanUploadIntoAsync(user, scope, groupId, ct))
            return Problem(statusCode: 403, title: "Cannot upload into this scope");

        var usedBytes = await _db.Files
            .Where(f => f.OwnerId == user.Id && f.Status != StorageFileStatus.Deleted)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        if (usedBytes + req.SizeBytes > user.QuotaBytes)
            return Problem(statusCode: 413, title: "Quota exceeded",
                detail: $"Uploading this file would exceed your quota of {user.QuotaBytes / 1024 / 1024} MiB.");

        var file = new StorageFile
        {
            OwnerId = user.Id,
            Scope = scope,
            GroupId = scope == FileScope.Group ? groupId : null,
            FolderId = folder?.Id,
            Name = req.Name,
            SizeBytes = req.SizeBytes,
            ContentType = string.IsNullOrWhiteSpace(req.ContentType) ? "application/octet-stream" : req.ContentType,
            Folder = req.Folder?.Trim('/') ?? "",
            Status = StorageFileStatus.Pending,
        };
        file.BlobPath = $"users/{user.Id:N}/{file.Id:N}/{SanitiseFilename(req.Name)}";
        _db.Files.Add(file);
        await _db.SaveChangesAsync(ct);

        var ticket = _blobs.CreateUploadTicket(file.BlobPath);
        return CreatedAtAction(nameof(GetById), new { id = file.Id },
            new CreateFileResponse(file.Id, ticket.UploadUrl.ToString(), ticket.Method, ticket.ExpiresAt));
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromServices] IAiPostProcessor ai, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id && f.OwnerId == user.Id, ct);
        if (file is null) return NotFound();

        var probe = await _blobs.ProbeAsync(file.BlobPath, ct);
        if (!probe.Exists) return Problem(statusCode: 409, title: "Blob not found", detail: "Upload the file bytes before calling complete.");

        file.SizeBytes = probe.SizeBytes;
        if (!string.IsNullOrEmpty(probe.ContentType)) file.ContentType = probe.ContentType!;
        file.Status = StorageFileStatus.Ready;
        file.ReadyAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Fire-and-forget AI post-processing (tags, risk flag, embedding) when
        // enabled in the gateway. Never blocks the uploader's response.
        ai.QueueForFile(file.Id);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 50, string? folder = null, string? search = null, CancellationToken ct = default)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var q = _db.Files.Where(f => f.OwnerId == user.Id && f.Status != StorageFileStatus.Deleted);
        if (folder is not null) q = q.Where(f => f.Folder == folder);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(f => f.Name.Contains(search));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(f => f.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new { f.Id, f.Name, f.SizeBytes, f.ContentType, f.Folder, f.CreatedAt, f.Status })
            .ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id && f.OwnerId == user.Id, ct);
        return file is null ? NotFound() : Ok(file);
    }

    public record MoveRequest(Guid FolderId);

    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveRequest req,
        [FromServices] IFileAccessService access, [FromServices] IFolderService folders, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();
        if (!await access.CanDeleteAsync(user, file, ct)) return Forbid();
        var target = await _db.Folders.FindAsync(new object[] { req.FolderId }, ct);
        if (target is null) return NotFound();
        if (!await folders.CanWriteAsync(target, user, ct)) return Forbid();
        // Refuse moving into another user's Personal folder — would leave the file
        // orphaned (readable by neither party) unless we also reassign ownership,
        // which changes quota accounting silently. Not supported yet.
        if (target.Scope == FileScope.Personal
            && target.OwnerUserId is Guid targetOwner
            && targetOwner != file.OwnerId)
        {
            return Problem(statusCode: 409, title: "Cross-owner Personal move not allowed",
                detail: "Move to Public or a Group instead.");
        }
        file.FolderId = target.Id;
        file.Scope = target.Scope;
        file.GroupId = target.OwnerGroupId;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record BulkDeleteRequest(Guid[] Ids);

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteRequest req, [FromServices] IFileAccessService access, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var files = await _db.Files.Include(f => f.ShareLinks)
            .Where(f => req.Ids.Contains(f.Id)).ToListAsync(ct);
        var deleted = 0;
        // Soft-delete: blob stays until purged from Trash; user can restore.
        foreach (var f in files)
        {
            if (!await access.CanDeleteAsync(user, f, ct)) continue;
            f.Status = StorageFileStatus.Deleted;
            f.DeletedAt = DateTimeOffset.UtcNow;
            foreach (var link in f.ShareLinks) link.IsRevoked = true;
            deleted++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromServices] IFileAccessService access, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.Include(f => f.ShareLinks)
            .SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();
        if (!await access.CanDeleteAsync(user, file, ct)) return Forbid();

        // Soft-delete only — blob is kept until purged from Trash.
        file.Status = StorageFileStatus.Deleted;
        file.DeletedAt = DateTimeOffset.UtcNow;
        foreach (var link in file.ShareLinks) link.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string SanitiseFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }
}
