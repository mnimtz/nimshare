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

    public record CreateFileRequest(string Name, long SizeBytes, string ContentType, string? Folder);
    public record CreateFileResponse(Guid FileId, string UploadUrl, string UploadMethod, DateTimeOffset ExpiresAt);

    [HttpPost]
    public async Task<ActionResult<CreateFileResponse>> Create([FromBody] CreateFileRequest req, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);

        var usedBytes = await _db.Files
            .Where(f => f.OwnerId == user.Id && f.Status != StorageFileStatus.Deleted)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        if (usedBytes + req.SizeBytes > user.QuotaBytes)
            return Problem(statusCode: 413, title: "Quota exceeded",
                detail: $"Uploading this file would exceed your quota of {user.QuotaBytes / 1024 / 1024} MiB.");

        var file = new StorageFile
        {
            OwnerId = user.Id,
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
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.Include(f => f.ShareLinks)
            .SingleOrDefaultAsync(f => f.Id == id && f.OwnerId == user.Id, ct);
        if (file is null) return NotFound();

        // Soft-delete first, then best-effort remove the blob.
        file.Status = StorageFileStatus.Deleted;
        file.DeletedAt = DateTimeOffset.UtcNow;
        foreach (var link in file.ShareLinks) link.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
        await _blobs.DeleteAsync(file.BlobPath, ct);
        return NoContent();
    }

    private static string SanitiseFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }
}
