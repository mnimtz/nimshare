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

        // No FolderId given? Anchor the file to the scope's root folder so it
        // shows up in the file browser. Legacy uploads used to land with
        // FolderId=null and were invisible to the tree navigation — this is
        // the fix.
        if (folder is null)
        {
            folder = await folders.GetOrCreateRootAsync(
                scope,
                scope == FileScope.Personal ? user.Id : null,
                scope == FileScope.Group ? groupId : null,
                user, ct);
        }

        // v1.10.24: Quota gilt nur für PERSONAL-scope Uploads. Public und
        // Group liegen im gemeinsamen Speicher — dort werden keine Quota-
        // Limits erzwungen, die Verwaltung passiert über Group-Level bzw.
        // Admin-Policy (später). Ein Personal-Upload zählt nur die anderen
        // Personal-Dateien des Users, nicht seine Public/Group-Beiträge.
        if (scope == FileScope.Personal)
        {
            var usedPersonalBytes = await _db.Files
                .Where(f => f.OwnerId == user.Id
                    && f.Scope == FileScope.Personal
                    && f.Status != StorageFileStatus.Deleted)
                .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
            if (usedPersonalBytes + req.SizeBytes > user.QuotaBytes)
                return Problem(statusCode: 413, title: "Quota exceeded",
                    detail: $"Uploading this file would exceed your Personal quota of {user.QuotaBytes / 1024 / 1024} MiB.");
        }

        var file = new StorageFile
        {
            OwnerId = user.Id,
            Scope = scope,
            GroupId = scope == FileScope.Group ? groupId : null,
            FolderId = folder.Id,
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
    public async Task<IActionResult> Complete(Guid id, [FromServices] IAiPostProcessor ai,
        [FromServices] IWebhookDispatcher hooks,
        [FromServices] IActivityLogger activity,
        CancellationToken ct)
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
        hooks.QueueEvent(user.Id, WebhookEvent.FileUploaded,
            new { fileId = file.Id, name = file.Name, sizeBytes = file.SizeBytes, folder = file.Folder });
        // Fills the Aktivität feed. Best-effort; ActivityLogger swallows errors.
        await activity.LogAsync(ActivityKind.FileUploaded, user, $"hochgeladen: {file.Name}",
            fileId: file.Id, folderId: file.FolderId, ct: ct);
        return NoContent();
    }

    public record BulkZipRequest(Guid[] FileIds, string? ArchiveName);

    /// <summary>
    /// Streams a ZIP archive containing the requested files. Response is
    /// chunked (no Content-Length), so the browser shows progress by bytes
    /// received. Only files the caller can read are included; unauthorized
    /// ones are silently skipped.
    /// </summary>
    [HttpPost("bulk-zip")]
    public async Task<IActionResult> BulkZip([FromBody] BulkZipRequest req,
        [FromServices] IFileAccessService access, CancellationToken ct)
    {
        if (req.FileIds is null || req.FileIds.Length == 0) return BadRequest();
        if (req.FileIds.Length > 500) return Problem(statusCode: 413, title: "Zu viele Dateien (max. 500).");
        var user = await _users.GetOrProvisionAsync(User, ct);
        var files = await _db.Files
            .Where(f => req.FileIds.Contains(f.Id) && f.Status == StorageFileStatus.Ready)
            .ToListAsync(ct);
        var allowed = new List<StorageFile>(files.Count);
        foreach (var f in files)
            if (await access.CanReadAsync(user, f, ct)) allowed.Add(f);
        if (allowed.Count == 0) return NotFound();

        var name = string.IsNullOrWhiteSpace(req.ArchiveName)
            ? $"nimshare-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip"
            : req.ArchiveName!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? req.ArchiveName : req.ArchiveName + ".zip";

        Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        Response.ContentType = "application/zip";
        // Chunked because we don't know the total size upfront.
        Response.Headers["X-Accel-Buffering"] = "no";

        using (var zip = new System.IO.Compression.ZipArchive(Response.Body,
            System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in allowed)
            {
                ct.ThrowIfCancellationRequested();
                var entryName = MakeUnique(f.Name, used);
                var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.NoCompression);
                using var es = entry.Open();
                try { await _blobs.DownloadToAsync(f.BlobPath, es, ct); }
                catch (OperationCanceledException) { throw; }
                // Any other error must not leak a half-written entry into the
                // zip. Bubble up so the client sees a broken transfer instead
                // of a silently corrupt archive.
            }
        }
        return new EmptyResult();
    }

    /// <summary>
    /// v1.10.70: Ordner rekursiv als ZIP streamen. Sammelt alle lesbaren
    /// StorageFile-Rows unter folderId (inkl. Sub-Sub-Ordner), baut relative
    /// Pfad-Namen "Unterordner/Datei.pdf" damit die Ordnerstruktur im ZIP
    /// erhalten bleibt, delegiert an dieselbe chunked-ZIP-Pipeline wie
    /// BulkZip. Response-Content-Length ist bewusst nicht gesetzt — Browser
    /// zeigt Progress in Bytes-Received.
    /// </summary>
    [HttpPost("/api/v1/folders/{id:guid}/zip")]
    public async Task<IActionResult> FolderZip(Guid id,
        [FromServices] IFileAccessService access,
        [FromServices] IFolderService folders,
        CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var root = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (root is null) return NotFound();
        if (!await folders.CanReadAsync(root, user, ct)) return Forbid();

        // BFS: alle Sub-Folder-Ids sammeln (ohne rekursive DB-Queries).
        // Same-scope-Filter ist EXAKT — Public zeigt nur Public-Folders,
        // Personal nur eigene, Group nur die dieser Group. Sonst würde
        // ein Public-Root-ZIP versehentlich Personal-Ordner mit reinziehen.
        var rootOwnerUser = root.OwnerUserId;
        var rootOwnerGroup = root.OwnerGroupId;
        var allFolders = await _db.Folders
            .Where(f => f.Scope == root.Scope
                     && f.OwnerUserId == rootOwnerUser
                     && f.OwnerGroupId == rootOwnerGroup)
            .Select(f => new { f.Id, f.Name, f.ParentFolderId })
            .ToListAsync(ct);
        var byParent = allFolders.GroupBy(f => f.ParentFolderId).ToDictionary(g => g.Key, g => g.ToList());
        var pathOf = new Dictionary<Guid, string> { [root.Id] = "" };
        var queue = new Queue<Guid>(); queue.Enqueue(root.Id);
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!byParent.TryGetValue(pid, out var kids)) continue;
            foreach (var k in kids)
            {
                pathOf[k.Id] = string.IsNullOrEmpty(pathOf[pid]) ? SanitizePathSegment(k.Name)
                                                                 : pathOf[pid] + "/" + SanitizePathSegment(k.Name);
                queue.Enqueue(k.Id);
            }
        }
        var folderIds = pathOf.Keys.ToHashSet();

        var files = await _db.Files
            .Where(f => f.FolderId != null && folderIds.Contains(f.FolderId!.Value)
                     && f.Status == StorageFileStatus.Ready)
            .ToListAsync(ct);
        var allowed = new List<(StorageFile file, string prefix)>(files.Count);
        long totalBytes = 0;
        foreach (var f in files)
        {
            if (!await access.CanReadAsync(user, f, ct)) continue;
            allowed.Add((f, pathOf.TryGetValue(f.FolderId!.Value, out var p) ? p : ""));
            totalBytes += f.SizeBytes;
        }
        if (allowed.Count == 0) return NotFound();
        // Hart-Cap: 2 GB ZIP-Content. Alles darüber blockiert einen Container-
        // Slot minutenlang und verlangsamt andere Requests. User bekommt einen
        // klaren Fehler statt eines timeouts.
        if (totalBytes > 2L * 1024 * 1024 * 1024)
            return Problem(statusCode: 413, title: "Ordner zu groß",
                detail: $"Der Ordner enthält {totalBytes / (1024 * 1024)} MB — max. 2048 MB pro ZIP-Download.");

        var name = $"{SanitizePathSegment(root.Name)}-{DateTimeOffset.UtcNow:yyyyMMdd}.zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        Response.ContentType = "application/zip";
        Response.Headers["X-Accel-Buffering"] = "no";

        using (var zip = new System.IO.Compression.ZipArchive(Response.Body,
            System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (f, prefix) in allowed)
            {
                ct.ThrowIfCancellationRequested();
                var entryPath = string.IsNullOrEmpty(prefix) ? f.Name : prefix + "/" + f.Name;
                entryPath = MakeUnique(entryPath, used);
                var entry = zip.CreateEntry(entryPath, System.IO.Compression.CompressionLevel.NoCompression);
                using var es = entry.Open();
                await _blobs.DownloadToAsync(f.BlobPath, es, ct);
            }
        }
        return new EmptyResult();
    }

    private static string SanitizePathSegment(string s)
    {
        // Zip-safe: keine Slashes, keine Steuerzeichen, keine reservierten
        // Windows-Namen. LibreOffice und Windows-Explorer sind da picky.
        var cleaned = new string(s.Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unbenannt" : cleaned.Trim();
    }

    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        for (int i = 2; i < 10000; i++)
        {
            var c = $"{stem} ({i}){ext}";
            if (used.Add(c)) return c;
        }
        return $"{Guid.NewGuid():N}{ext}";
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

    /// <summary>
    /// v1.10.66: preview-url für iOS (JWT/ApiUser-Policy). Der bestehende
    /// gleichnamige Endpoint in BrowseController hängt an der Cookie-only
    /// "WebUser"-Policy — iOS mit Bearer-Token bekommt dort einen HTML-
    /// Login-Redirect und der JSON-Decoder crasht mit "Unexpected character
    /// '<' at column 1". Diese ApiUser-Variante liefert dasselbe SAS-URL-
    /// Objekt, ist aber Token-authentifiziert.
    /// </summary>
    [HttpGet("{id:guid}/preview-url")]
    public async Task<IActionResult> PreviewUrl(Guid id, [FromServices] IFileAccessService access, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();
        if (!await access.CanReadAsync(user, file, ct)) return Forbid();
        var sas = _blobs.CreateDownloadSas(file.BlobPath, file.Name, file.ContentType, TimeSpan.FromMinutes(5));
        return Ok(new { url = sas.ToString(), contentType = file.ContentType, name = file.Name });
    }

    /// <summary>
    /// v1.10.70: Office-Preview-URL. Konvertiert DOCX/XLSX/PPTX/ODT/… on-
    /// demand nach PDF (LibreOffice-headless, gecacht als Blob), gibt eine
    /// SAS-URL zurück. Der bestehende PDF-Viewer im Modal (bzw. QuickLook
    /// auf iOS) rendert das dann direkt.
    /// Response 415 wenn der Content-Type nicht unterstützt wird — Client
    /// weiß dann: "kein Office-Format, fall back auf plain preview".
    /// </summary>
    [HttpGet("{id:guid}/office-preview")]
    public async Task<IActionResult> OfficePreviewUrl(Guid id,
        [FromServices] IFileAccessService access,
        [FromServices] IOfficePreviewService office,
        CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();
        if (!await access.CanReadAsync(user, file, ct)) return Forbid();
        if (!office.IsSupported(file.ContentType))
            return Problem(statusCode: 415, title: "Kein Office-Format",
                detail: $"Content-Type {file.ContentType} wird von der Office-Preview nicht unterstützt.");
        var url = await office.GetPreviewUrlAsync(id, file.BlobPath, file.Name, file.ContentType, ct);
        if (url is null)
            return Problem(statusCode: 500, title: "Konvertierung fehlgeschlagen",
                detail: "LibreOffice konnte das Dokument nicht in PDF konvertieren. Details siehe Server-Log.");
        return Ok(new { url = url.ToString(), contentType = "application/pdf", name = file.Name + ".pdf" });
    }

    /// <summary>v1.10.70: File umbenennen. Auth-Check: nur Owner oder
    /// jemand mit Manage-Rechten auf der Datei darf renamen. Kollisionen
    /// mit Existing-Names im gleichen Folder werden mit 409 gemeldet.</summary>
    public record RenameFileRequest(string Name);

    [HttpPost("{id:guid}/rename")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameFileRequest req,
        [FromServices] IFileAccessService access, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Problem(statusCode: 422, title: "Name ist erforderlich");
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();
        if (!await access.CanDeleteAsync(user, file, ct)) return Forbid();
        var newName = req.Name.Trim();
        // Sanitise: keine Path-Separator, Steuerzeichen etc.
        newName = new string(newName.Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(newName)) return Problem(statusCode: 422, title: "Ungültiger Name");
        // Uniqueness-Check im gleichen Folder (sonst Doppelklick auf Preview verwirrt).
        if (file.FolderId is Guid fid)
        {
            var exists = await _db.Files.AnyAsync(f =>
                f.FolderId == fid && f.Id != file.Id && f.Name == newName
                && f.Status != StorageFileStatus.Deleted, ct);
            if (exists) return Problem(statusCode: 409, title: "Name bereits vergeben in diesem Ordner");
        }
        file.Name = newName;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = file.Id, name = file.Name });
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

    // v1.10.62: echte Kopie einer Datei in einen anderen Ordner. Anders
    // als /move (Original wandert) und /pin (Referenz auf denselben Blob),
    // erzeugt /copy einen NEUEN Blob + neue File-Row. Beide Kopien sind
    // unabhängig — Löschen der einen killt die andere nicht.
    // Erforderlich: Lese-Recht auf Original, Schreib-Recht auf Target-
    // Folder, und bei Personal-Target: Quota-Check des NEUEN Owners.
    public record CopyRequest(Guid FolderId);

    [HttpPost("{id:guid}/copy")]
    public async Task<IActionResult> Copy(Guid id, [FromBody] CopyRequest req,
        [FromServices] IFileAccessService access, [FromServices] IFolderService folders, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var source = await _db.Files.SingleOrDefaultAsync(f => f.Id == id && f.Status == StorageFileStatus.Ready, ct);
        if (source is null) return NotFound();
        if (!await access.CanReadAsync(user, source, ct)) return Forbid();
        var target = await _db.Folders.FindAsync(new object[] { req.FolderId }, ct);
        if (target is null) return NotFound();
        if (!await folders.CanWriteAsync(target, user, ct)) return Forbid();

        // Owner des neuen File-Rows bestimmen sich aus Target-Scope:
        //  Personal → Target-Folder-OwnerUserId (= der Kopierende, wenn er
        //             in seinen eigenen Personal-Bereich kopiert)
        //  Group    → Target-Folder-OwnerGroupId; OwnerId der File-Row
        //             bleibt der User (analog zu Upload in Group)
        //  Public   → OwnerUserId = der Kopierende (er "reicht ein")
        var newOwnerId = target.Scope == FileScope.Personal
            ? (target.OwnerUserId ?? user.Id)
            : user.Id;

        // Quota-Check nur bei Personal-Target — Public/Group zählen nicht
        // gegen User-Quota (siehe v1.10.24 Quota-Split).
        if (target.Scope == FileScope.Personal)
        {
            var used = await _db.Files
                .Where(f => f.OwnerId == newOwnerId
                         && f.Scope == FileScope.Personal
                         && f.Status != StorageFileStatus.Deleted)
                .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
            var owner = await _db.Users.FindAsync(new object[] { newOwnerId }, ct);
            if (owner is not null && owner.QuotaBytes > 0
                && used + source.SizeBytes > owner.QuotaBytes)
            {
                return Problem(statusCode: 413, title: "Quota exceeded",
                    detail: $"Target owner has {(owner.QuotaBytes - used) / (1024 * 1024)} MB free, needs {source.SizeBytes / (1024 * 1024)} MB.");
            }
        }

        // Neuer Blob-Path — analog zum Upload-Naming
        var newFileId = Guid.NewGuid();
        var safeName = source.Name.Replace('/', '_').Replace('\\', '_');
        var newBlobPath = $"users/{newOwnerId:N}/{newFileId:N}/{safeName}";
        try
        {
            await _blobs.CopyAsync(source.BlobPath, newBlobPath, ct);
        }
        catch (Exception ex)
        {
            return Problem(statusCode: 502, title: "Blob copy failed", detail: ex.Message);
        }

        var copy = new StorageFile
        {
            Id = newFileId,
            OwnerId = newOwnerId,
            Scope = target.Scope,
            GroupId = target.OwnerGroupId,
            FolderId = target.Id,
            Name = source.Name,
            SizeBytes = source.SizeBytes,
            ContentType = source.ContentType,
            BlobPath = newBlobPath,
            ContainerName = source.ContainerName,
            Status = StorageFileStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            // AI-Tags werden mit-kopiert damit der neue Owner sofort die
            // gleiche Klassifikation hat — kein AI-Re-Run nötig.
            AiTags = source.AiTags,
            AiRiskFlag = source.AiRiskFlag,
            AiSummary = source.AiSummary,
            AiSummaryLang = source.AiSummaryLang,
        };
        _db.Files.Add(copy);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = copy.Id });
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
    public async Task<IActionResult> Delete(Guid id, [FromServices] IFileAccessService access,
        [FromServices] IActivityLogger activity, CancellationToken ct)
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
        await activity.LogAsync(ActivityKind.FileDeleted, user, $"gelöscht: {file.Name}",
            fileId: file.Id, folderId: file.FolderId, ct: ct);
        return NoContent();
    }

    private static string SanitiseFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }
}
