using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Hierarchical file browser ("Ablage") — a single sidebar entry that shows a
/// classic file/folder tree per scope. URLs:
///   /browse                             list of top-level scope tiles
///   /browse/personal                    personal root
///   /browse/personal/Projects/Q3        deep subfolder
///   /browse/public                      public root
///   /browse/group/{groupId}             group root
///   /browse/group/{groupId}/Docs/...    deeper
/// </summary>
[Authorize(Policy = "WebUser")]
[Route("/browse")]
public class BrowseController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IFolderService _folders;
    private readonly IFileAccessService _access;

    public BrowseController(NimShareDbContext db, ICurrentUserService users, IFolderService folders, IFileAccessService access)
    {
        _db = db;
        _users = users;
        _folders = folders;
        _access = access;
    }

    // ── /browse — jump straight into the personal library ─────────────────
    // (No tile-picker intermediate — Seafile-style flow: sidebar has the
    // library switcher, the main pane is always a browser.)
    [HttpGet("")]
    public IActionResult Root() => RedirectToAction(nameof(Personal), new { path = "" });

    // ── /browse/personal[/**] ──────────────────────────────────────────────
    [HttpGet("personal/{**path}")]
    public Task<IActionResult> Personal(string? path, CancellationToken ct) => RenderScope(FileScope.Personal, null, path, ct);

    // ── /browse/public[/**] ────────────────────────────────────────────────
    [HttpGet("public/{**path}")]
    public Task<IActionResult> Public(string? path, CancellationToken ct) => RenderScope(FileScope.Public, null, path, ct);

    // ── /browse/group/{id}[/**] ────────────────────────────────────────────
    [HttpGet("group/{groupId:guid}/{**path}")]
    public Task<IActionResult> Group(Guid groupId, string? path, CancellationToken ct) => RenderScope(FileScope.Group, groupId, path, ct);

    /// <summary>
    /// Resolve a folder by ID and render its scope-appropriate view directly.
    /// The tree JS uses THIS route (v1.10.8) instead of building name-based
    /// paths because names can contain trailing dots ("Business - Production.")
    /// and non-ASCII chars that IIS/AspNetCoreModule request-filtering blocks
    /// with a 404 before ASP.NET Core routing even sees them. ID-based routing
    /// is deterministic and dot-safe.
    /// </summary>
    [HttpGet("folder/{folderId:guid}")]
    public async Task<IActionResult> ByFolderId(Guid folderId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { folderId }, ct);
        if (folder is null) return NotFound();
        if (folder.Scope == FileScope.Group && folder.OwnerGroupId is Guid gid
            && me.Role != UserRole.Admin && !await _access.IsGroupMemberAsync(me, gid, ct))
            return Forbid();
        if (!await _folders.CanReadAsync(folder, me, ct)) return Forbid();
        return await RenderScopeAtFolder(folder, me, ct);
    }

    private async Task<IActionResult> RenderScope(FileScope scope, Guid? groupId, string? path, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (scope == FileScope.Group && groupId is Guid g
            && me.Role != UserRole.Admin && !await _access.IsGroupMemberAsync(me, g, ct))
            return Forbid();

        var root = await _folders.GetOrCreateRootAsync(
            scope,
            scope == FileScope.Personal ? me.Id : null,
            scope == FileScope.Group ? groupId : null,
            me, ct);

        // One-shot migration: pre-v0.6 files were written with FolderId=null
        // (before folders existed). They belong to the caller's Personal scope
        // and were invisible in the folder browser — attach any of ours to the
        // Personal root on first open.
        if (scope == FileScope.Personal)
        {
            var orphans = await _db.Files
                .Where(f => f.OwnerId == me.Id
                    && f.FolderId == null
                    && f.Status != StorageFileStatus.Deleted
                    && f.Scope == FileScope.Personal)
                .ToListAsync(ct);
            if (orphans.Count > 0)
            {
                foreach (var o in orphans) o.FolderId = root.Id;
                await _db.SaveChangesAsync(ct);
            }
        }

        var segments = string.IsNullOrEmpty(path)
            ? Array.Empty<string>()
            : path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = await _folders.ResolvePathAsync(root, segments, ct);
        if (current is null) return NotFound();
        if (!await _folders.CanReadAsync(current, me, ct)) return Forbid();

        return await RenderCurrentFolder(current, me, scope, groupId, ct);
    }

    /// <summary>Shared rendering step for both path-based and ID-based routes.
    /// Assumes CanRead has already been verified.</summary>
    private async Task<IActionResult> RenderScopeAtFolder(Folder current, User me, CancellationToken ct)
    {
        Guid? groupId = current.Scope == FileScope.Group ? current.OwnerGroupId : null;
        return await RenderCurrentFolder(current, me, current.Scope, groupId, ct);
    }

    private async Task<IActionResult> RenderCurrentFolder(Folder current, User me, FileScope scope, Guid? groupId, CancellationToken ct)
    {
        var subs = await _folders.ListSubfoldersAsync(current, ct);
        // v1.10.104: für den Public-Scope die für den User verborgenen
        // Private-Subordner rausfiltern.
        subs = await _folders.FilterVisibleSubfoldersAsync(subs, me, ct);
        var files = await _folders.ListFilesAsync(current, ct);
        var ancestry = await _folders.GetAncestryAsync(current, ct);
        var canWrite = await _folders.CanWriteAsync(current, me, ct);
        var canManage = await _folders.CanManageAsync(current, me, ct);

        // Pins: we need the ID-set in every listing (so a row already pinned by
        // the caller shows "Unpin" instead of "Pin" — regardless of which
        // scope they're browsing). We only render the pinned-files ROWS on the
        // Personal root, though — a pin is a shortcut, not a real location.
        HashSet<Guid> pinnedIds = new(
            await _db.FilePins.Where(p => p.UserId == me.Id).Select(p => p.FileId).ToListAsync(ct));
        List<StorageFile> pinnedFiles = new();
        if (scope == FileScope.Personal && current.ParentFolderId is null && pinnedIds.Count > 0)
        {
            var pins = await _db.FilePins
                .Where(p => p.UserId == me.Id)
                .Include(p => p.File).ThenInclude(f => f!.Owner)
                .OrderByDescending(p => p.PinnedAt)
                .ToListAsync(ct);
            foreach (var p in pins)
            {
                if (p.File is null || p.File.Status != StorageFileStatus.Ready) continue;
                pinnedFiles.Add(p.File);
            }
        }

        ViewData["Scope"] = scope;
        ViewData["GroupId"] = groupId;
        ViewData["Current"] = current;
        ViewData["Ancestry"] = ancestry;
        ViewData["Subfolders"] = subs;
        ViewData["Files"] = files;
        ViewData["PinnedFiles"] = pinnedFiles;
        ViewData["PinnedIds"] = pinnedIds;
        ViewData["Me"] = me;
        ViewData["CanWrite"] = canWrite;
        ViewData["CanManage"] = canManage;
        ViewData["UrlBase"] = BuildUrlBase(scope, groupId);

        if (scope == FileScope.Group && groupId is Guid gg)
        {
            var g0 = await _db.Groups.FindAsync(new object[] { gg }, ct);
            ViewData["GroupName"] = g0?.Name;
        }
        return View("Browse");
    }

    private static string BuildUrlBase(FileScope scope, Guid? groupId) => scope switch
    {
        FileScope.Personal => "/browse/personal",
        FileScope.Public => "/browse/public",
        FileScope.Group => $"/browse/group/{groupId}",
        _ => "/browse",
    };

    // ── POST: create folder ────────────────────────────────────────────────
    [HttpPost("folders/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFolder(Guid parentId, string name, string returnUrl,
        [FromServices] IActivityLogger activity, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var parent = await _db.Folders.FindAsync(new object[] { parentId }, ct);
        if (parent is null) return NotFound();
        if (!await _folders.CanWriteAsync(parent, me, ct)) return Forbid();
        try
        {
            var child = await _folders.CreateChildAsync(parent, name, me, ct);
            await activity.LogAsync(ActivityKind.FolderCreated, me, $"Ordner erstellt: {name}",
                folderId: child.Id, ct: ct);
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return Redirect(SafeReturn(returnUrl));
    }

    /// <summary>
    /// v1.10.64: JSON-Twin von CreateFolder für die Browse-UI. Vorher ging
    /// das Modal per Form-Submit + Server-Redirect — bei einem beliebigen
    /// Fehler (AntiForgery-Timeout, Name-Kollision, Speichern-Fehler) sah
    /// der User NICHTS. Neue AJAX-Variante gibt strukturiertes JSON zurück,
    /// das Frontend zeigt konkrete Fehlermeldung + Toast bei Erfolg.
    /// </summary>
    public record CreateFolderReq(Guid ParentId, string Name);

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/folders")]
    public async Task<IActionResult> ApiCreateFolder([FromBody] CreateFolderReq req,
        [FromServices] IActivityLogger activity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(statusCode: 422, title: "Name is required");
        var me = await _users.GetOrProvisionAsync(User, ct);
        var parent = await _db.Folders.FindAsync(new object[] { req.ParentId }, ct);
        if (parent is null) return Problem(statusCode: 404, title: "Parent folder not found");
        if (!await _folders.CanWriteAsync(parent, me, ct))
            return Problem(statusCode: 403, title: "You cannot create folders here");
        try
        {
            var child = await _folders.CreateChildAsync(parent, req.Name.Trim(), me, ct);
            await activity.LogAsync(ActivityKind.FolderCreated, me, $"Ordner erstellt: {req.Name}",
                folderId: child.Id, ct: ct);
            return Ok(new { id = child.Id, name = child.Name, parentId = parent.Id });
        }
        catch (Exception ex)
        {
            return Problem(statusCode: 500, title: "Could not create folder", detail: ex.Message);
        }
    }

    // ── POST: rename folder ────────────────────────────────────────────────
    [HttpPost("folders/{id:guid}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameFolder(Guid id, string name, string returnUrl, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        try { await _folders.RenameAsync(folder, name, ct); }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return Redirect(SafeReturn(returnUrl));
    }

    /// <summary>Update the folder's icon (emoji + colour) — used by the right-click
    /// context menu. Both fields are optional; null clears back to the default.</summary>
    public record FolderIconReq(string? Emoji, string? Color);

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/folders/{id:guid}/icon")]
    public async Task<IActionResult> UpdateFolderIcon(Guid id, [FromBody] FolderIconReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        // Sanitise the emoji: cap at 4 chars (single emoji + variation selector
        // + zero-width joiners), strip anything obviously non-emoji.
        var emoji = string.IsNullOrWhiteSpace(req.Emoji) ? null : req.Emoji.Trim();
        if (emoji != null && emoji.Length > 8) emoji = emoji.Substring(0, 8);
        // Sanitise the colour: accept 3- or 6-char hex, no leading "#".
        var color = req.Color?.Trim().TrimStart('#').ToLowerInvariant();
        if (!string.IsNullOrEmpty(color) && !System.Text.RegularExpressions.Regex.IsMatch(color, "^[0-9a-f]{3}([0-9a-f]{3})?$"))
            color = null;
        folder.Emoji = emoji;
        folder.Color = color;
        await _db.SaveChangesAsync(ct);
        return Ok(new { emoji, color });
    }

    /// <summary>v1.10.70: JSON-Rename für Folder (iOS + Web-AJAX). Der
    /// alte Form-Post-Endpoint bleibt für den bestehenden Web-Flow.</summary>
    public record RenameReq(string Name);

    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/folders/{id:guid}/rename")]
    public async Task<IActionResult> ApiRenameFolder(Guid id, [FromBody] RenameReq req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Problem(statusCode: 422, title: "Name ist erforderlich");
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        try { await _folders.RenameAsync(folder, req.Name.Trim(), ct); }
        catch (Exception ex) { return Problem(statusCode: 500, title: "Umbenennen fehlgeschlagen", detail: ex.Message); }
        return Ok(new { id = folder.Id, name = folder.Name });
    }

    // v1.10.113: JSON-Delete für iOS (der MVC-Post braucht AntiForgery +
    // Redirect, was der App-Client nicht liefern kann). force=true löscht
    // rekursiv inkl. Inhalt (→ Papierkorb), sonst nur leere Ordner.
    [Authorize(Policy = "ApiUser")]
    [HttpDelete("/api/v1/folders/{id:guid}")]
    public async Task<IActionResult> ApiDeleteFolder(Guid id, bool force, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (folder.ParentFolderId is null) return Problem(statusCode: 422, title: "Cannot delete a library root.");
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        try { await _folders.DeleteAsync(folder, cascade: force, ct); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: ex.Message); }
        catch (Exception ex) { return Problem(statusCode: 500, title: "Delete failed", detail: ex.Message); }
        return NoContent();
    }

    // ── POST: delete folder ────────────────────────────────────────────────
    [HttpPost("folders/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFolder(Guid id, string returnUrl, bool force, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        try { await _folders.DeleteAsync(folder, cascade: force, ct); }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return Redirect(SafeReturn(returnUrl));
    }

    /// <summary>
    /// v1.10.104 (Stage 2 „Windows-ACL"): Owner + Admin schalten einen
    /// Public-Ordner privat. Ist er privat, sehen ihn nur noch Owner,
    /// Admin und User/Gruppen mit einem DirectShare-Grant. Grants selbst
    /// werden über /api/v1/direct-shares verwaltet (existierende API).
    /// </summary>
    public record PrivacyReq(bool IsPrivate);

    [Authorize(Policy = "ApiUser")]
    [HttpPatch("/api/v1/folders/{id:guid}/privacy")]
    public async Task<IActionResult> UpdateFolderPrivacy(Guid id, [FromBody] PrivacyReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        // Nur Owner (CreatedByUserId) und Admin dürfen den Privacy-Toggle
        // umlegen. CanManageAsync validiert genau das + verhindert dass
        // wir einen Root oder Fremdordner umschalten.
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        if (folder.Scope != FileScope.Public)
            return Problem(statusCode: 422, title: "Privacy is only meaningful on Public-scope folders.");
        if (folder.ParentFolderId is null)
            return Problem(statusCode: 422, title: "Cannot mark the Public root as private.");
        folder.IsPrivate = req.IsPrivate;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = folder.Id, isPrivate = folder.IsPrivate });
    }

    /// <summary>
    /// v1.10.104: Kombinierter Read-Endpoint für die Berechtigungen-UI:
    /// aktueller IsPrivate-Zustand + alle Grants (User + Group) auf DIESEN
    /// Ordner. Wir joinen DirectShare→User/Group für kompakte Anzeige.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/folders/{id:guid}/permissions")]
    public async Task<IActionResult> FolderPermissions(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanReadAsync(folder, me, ct)) return Forbid();

        var userGrants = await _db.DirectShares
            .Where(s => s.FolderId == id && s.TargetUserId != null)
            .Include(s => s.TargetUser)
            .Select(s => new {
                id = s.Id,
                kind = "user",
                userId = s.TargetUserId,
                displayName = s.TargetUser!.DisplayName,
                email = s.TargetUser.Email,
                permission = s.Permission.ToString().ToLowerInvariant(),
                createdAt = s.CreatedAt,
            })
            .ToListAsync(ct);
        var groupGrants = await _db.DirectShares
            .Where(s => s.FolderId == id && s.TargetGroupId != null)
            .Include(s => s.TargetGroup)
            .Select(s => new {
                id = s.Id,
                kind = "group",
                groupId = s.TargetGroupId,
                displayName = s.TargetGroup!.Name,
                permission = s.Permission.ToString().ToLowerInvariant(),
                createdAt = s.CreatedAt,
            })
            .ToListAsync(ct);
        var canManage = await _folders.CanManageAsync(folder, me, ct);
        return Ok(new {
            folderId = folder.Id,
            folderName = folder.Name,
            scope = folder.Scope.ToString(),
            isPrivate = folder.IsPrivate,
            canManage,
            userGrants,
            groupGrants,
        });
    }

    /// <summary>
    /// v1.10.96: Count-Endpoint für die 2-stufige Löschbestätigung. Frontend
    /// ruft diesen vor dem Delete-Post; wenn Inhalt gefunden → Extra-Frage
    /// „X Dateien + Y Unterordner mit in den Papierkorb?"
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/folders/{id:guid}/contents-count")]
    public async Task<IActionResult> FolderContentsCount(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        var files = await _db.Files.CountAsync(f => f.FolderId == id && f.Status != StorageFileStatus.Deleted, ct);
        var subs = await _db.Folders.CountAsync(f => f.ParentFolderId == id, ct);
        return Ok(new { files, subfolders = subs });
    }

    /// <summary>
    /// v1.10.148 — Inhalt eines Ordners direkt per ID lesen (statt über
    /// scope+path). Der Web-„Für mich freigegeben"-Bereich nutzt normale
    /// Ordner-Links; iOS SharedWithMeView tap-te bisher auf Ordner-Rows
    /// ohne Handler (Bug #7). Dieser Endpoint erlaubt der iOS-App, den
    /// geshareten Ordner zu öffnen, ohne dessen Scope/Path zu kennen.
    /// Nur wer CanRead auf dem Ordner hat (Owner, Direct-Share, oder
    /// Public-Sichtbarkeit) bekommt Inhalte zurück.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/folders/{id:guid}/browse")]
    public async Task<IActionResult> BrowseById(Guid id,
        [FromServices] IFileAccessService access, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanReadAsync(folder, me, ct)) return Forbid();
        var subs = await _folders.ListSubfoldersAsync(folder, ct);
        subs = await _folders.FilterVisibleSubfoldersAsync(subs, me, ct);
        var files = await _folders.ListFilesAsync(folder, ct);
        return Ok(new
        {
            id = folder.Id,
            name = folder.Name,
            scope = folder.Scope.ToString(),
            subfolders = subs.Select(f => new { id = f.Id, name = f.Name }).ToList(),
            files = files.Where(f => f.Status == StorageFileStatus.Ready)
                .Select(f => new { id = f.Id, name = f.Name, sizeBytes = f.SizeBytes,
                                   contentType = f.ContentType, createdAt = f.CreatedAt }).ToList(),
        });
    }

    // ── v1.10.110: Ordner verschieben + kopieren (Rechtsklick-Parität mit Dateien) ──
    public record FolderMoveReq(Guid FolderId);

    /// <summary>
    /// Verschiebt einen Ordner (reparent) in einen anderen Ziel-Ordner
    /// DERSELBEN Bibliothek. Cross-Scope-Move ist bewusst nicht erlaubt —
    /// das müsste Scope+Owner auf dem ganzen Teilbaum + allen Dateien
    /// umschreiben. Validiert: kein Root, kein Zyklus, keine Namenskollision.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/folders/{id:guid}/move")]
    public async Task<IActionResult> MoveFolder(Guid id, [FromBody] FolderMoveReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (folder.ParentFolderId is null)
            return Problem(statusCode: 422, title: "Cannot move a library root.");
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();

        var target = await _db.Folders.FindAsync(new object[] { req.FolderId }, ct);
        if (target is null) return NotFound();
        if (!await _folders.CanWriteAsync(target, me, ct)) return Forbid();

        if (target.Scope != folder.Scope)
            return Problem(statusCode: 422, title: "Move within the same library only.");
        if (folder.Scope == FileScope.Group && target.OwnerGroupId != folder.OwnerGroupId)
            return Problem(statusCode: 422, title: "Move within the same group only.");
        if (folder.Scope == FileScope.Personal && target.OwnerUserId != folder.OwnerUserId)
            return Problem(statusCode: 422, title: "Move within your own library only.");
        if (target.Id == folder.Id || await IsDescendantOfAsync(target.Id, folder.Id, ct))
            return Problem(statusCode: 409, title: "Cannot move a folder into itself or one of its subfolders.");
        if (await _db.Folders.AnyAsync(f => f.ParentFolderId == target.Id && f.Id != folder.Id && f.Name == folder.Name, ct))
            return Problem(statusCode: 409, title: "A folder with that name already exists in the target.");

        folder.ParentFolderId = target.Id;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = folder.Id });
    }

    public record FolderCopyReq(Guid FolderId);

    /// <summary>
    /// Kopiert einen Ordner rekursiv (Unterordner + Dateien als NEUE Blobs)
    /// in einen Ziel-Ordner. Scope/Owner der Kopien richten sich nach dem
    /// Ziel (wie bei Datei-Copy). Quota-Vorabprüfung bei Personal-Ziel.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpPost("/api/v1/folders/{id:guid}/copy")]
    public async Task<IActionResult> CopyFolder(Guid id, [FromBody] FolderCopyReq req,
        [FromServices] IBlobStorageService blobs, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var source = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (source is null) return NotFound();
        if (!await _folders.CanReadAsync(source, me, ct)) return Forbid();

        var target = await _db.Folders.FindAsync(new object[] { req.FolderId }, ct);
        if (target is null) return NotFound();
        if (!await _folders.CanWriteAsync(target, me, ct)) return Forbid();
        if (target.Id == source.Id || await IsDescendantOfAsync(target.Id, source.Id, ct))
            return Problem(statusCode: 409, title: "Cannot copy a folder into itself or one of its subfolders.");

        // Quota-Vorabprüfung nur bei Personal-Ziel (Public/Group zählen nicht
        // gegen User-Quota). Wir summieren den gesamten Quell-Teilbaum.
        if (target.Scope == FileScope.Personal)
        {
            var subtree = await CollectSubtreeFolderIdsAsync(source.Id, ct);
            var totalBytes = await _db.Files
                .Where(f => subtree.Contains(f.FolderId!.Value)
                    && f.Status == StorageFileStatus.Ready)
                .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
            var newOwnerId = target.OwnerUserId ?? me.Id;
            var used = await _db.Files
                .Where(f => f.OwnerId == newOwnerId && f.Scope == FileScope.Personal && f.Status != StorageFileStatus.Deleted)
                .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
            var owner = await _db.Users.FindAsync(new object[] { newOwnerId }, ct);
            if (owner is not null && owner.QuotaBytes > 0 && used + totalBytes > owner.QuotaBytes)
                return Problem(statusCode: 413, title: "Quota exceeded",
                    detail: $"Copy needs {totalBytes / (1024 * 1024)} MB, only {(owner.QuotaBytes - used) / (1024 * 1024)} MB free.");
        }

        try
        {
            var newRootId = await CopyFolderRecursiveAsync(source, target, me, blobs, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(new { id = newRootId });
        }
        catch (Exception ex)
        {
            return Problem(statusCode: 500, title: "Folder copy failed", detail: ex.Message);
        }
    }

    /// <summary>True, wenn candidateId ein Nachfahre von ancestorId ist (Ahnenkette hoch).</summary>
    private async Task<bool> IsDescendantOfAsync(Guid candidateId, Guid ancestorId, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        Guid? cur = candidateId;
        while (cur is Guid c && visited.Add(c) && visited.Count <= 64)
        {
            if (c == ancestorId) return true;
            cur = await _db.Folders.Where(x => x.Id == c).Select(x => x.ParentFolderId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    /// <summary>BFS über alle Folder-IDs des Teilbaums inkl. Wurzel (Quota-Summierung).</summary>
    private async Task<HashSet<Guid>> CollectSubtreeFolderIdsAsync(Guid rootId, CancellationToken ct)
    {
        var all = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        var safety = 64;
        while (frontier.Count > 0 && safety-- > 0)
        {
            var parents = frontier;
            var kids = await _db.Folders
                .Where(f => f.ParentFolderId != null && parents.Contains(f.ParentFolderId!.Value))
                .Select(f => f.Id).ToListAsync(ct);
            frontier = kids.Where(k => all.Add(k)).ToList();
        }
        return all;
    }

    private async Task<Guid> CopyFolderRecursiveAsync(Folder source, Folder targetParent, User me,
        IBlobStorageService blobs, CancellationToken ct)
    {
        // Kollisionssicherer Name auf dieser Ebene.
        var siblings = await _db.Folders.Where(f => f.ParentFolderId == targetParent.Id).Select(f => f.Name).ToListAsync(ct);
        var name = source.Name;
        if (siblings.Contains(name))
        {
            var i = 2;
            while (siblings.Contains($"{source.Name} ({i})")) i++;
            name = $"{source.Name} ({i})";
        }

        var newFolder = new Folder
        {
            Name = name,
            ParentFolderId = targetParent.Id,
            Scope = targetParent.Scope,
            OwnerUserId = targetParent.Scope == FileScope.Personal ? targetParent.OwnerUserId : null,
            OwnerGroupId = targetParent.Scope == FileScope.Group ? targetParent.OwnerGroupId : null,
            CreatedByUserId = me.Id,
            Emoji = source.Emoji,
            Color = source.Color,
            // IsPrivate bewusst NICHT übernommen — eine Kopie an neuer Stelle
            // soll nicht still eine Privacy-Sperre erben.
        };
        _db.Folders.Add(newFolder);

        var newOwnerId = targetParent.Scope == FileScope.Personal ? (targetParent.OwnerUserId ?? me.Id) : me.Id;
        var files = await _db.Files
            .Where(f => f.FolderId == source.Id && f.Status == StorageFileStatus.Ready).ToListAsync(ct);
        foreach (var f in files)
        {
            var newFileId = Guid.NewGuid();
            var safeName = f.Name.Replace('/', '_').Replace('\\', '_');
            var newBlobPath = $"users/{newOwnerId:N}/{newFileId:N}/{safeName}";
            await blobs.CopyAsync(f.BlobPath, newBlobPath, ct);
            _db.Files.Add(new StorageFile
            {
                Id = newFileId,
                OwnerId = newOwnerId,
                Scope = targetParent.Scope,
                GroupId = targetParent.OwnerGroupId,
                FolderId = newFolder.Id,
                Name = f.Name,
                SizeBytes = f.SizeBytes,
                ContentType = f.ContentType,
                BlobPath = newBlobPath,
                ContainerName = f.ContainerName,
                Status = StorageFileStatus.Ready,
                CreatedAt = DateTimeOffset.UtcNow,
                ReadyAt = DateTimeOffset.UtcNow,
                AiTags = f.AiTags,
                AiRiskFlag = f.AiRiskFlag,
                AiSummary = f.AiSummary,
                AiSummaryLang = f.AiSummaryLang,
            });
        }

        var subs = await _db.Folders.Where(f => f.ParentFolderId == source.Id).ToListAsync(ct);
        foreach (var sub in subs)
            await CopyFolderRecursiveAsync(sub, newFolder, me, blobs, ct);

        return newFolder.Id;
    }

    private string SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/browse";

    /// <summary>Flat list of the current user's writable folders — used by the Move modal.</summary>
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/folders/writable")]
    public async Task<IActionResult> WritableFolders(string scope, Guid? exclude, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!Enum.TryParse<FileScope>(scope, true, out var s)) return BadRequest();

        IQueryable<Folder> q = _db.Folders.Where(f => f.Scope == s);
        // Restrict Personal to the caller's OWN folders — even Admin. Cross-owner
        // moves are refused by FilesController.Move, and listing every user's
        // private tree in a dropdown would be both huge and confusing.
        if (s == FileScope.Personal)
            q = q.Where(f => f.OwnerUserId == me.Id);
        else if (s == FileScope.Group)
        {
            var myGroupIds = _db.GroupMemberships.Where(m => m.UserId == me.Id).Select(m => m.GroupId);
            q = q.Where(f => myGroupIds.Contains(f.OwnerGroupId!.Value) || me.Role == UserRole.Admin);
        }
        var all = await q.OrderBy(f => f.Name).ToListAsync(ct);
        // v1.10.104: Copy/Move-Dropdown darf verborgene Private-Ordner
        // nicht anbieten.
        if (s == FileScope.Public && me.Role != UserRole.Admin)
        {
            var hidden = await _access.GetHiddenPublicFolderIdsAsync(me, ct);
            if (hidden.Count > 0)
                all = all.Where(f => !hidden.Contains(f.Id)).ToList();
        }
        // Build path label per folder by walking up.
        var byId = all.ToDictionary(f => f.Id);
        string PathOf(Folder f)
        {
            var parts = new List<string> { f.Name };
            var cur = f;
            while (cur.ParentFolderId is Guid pid && byId.TryGetValue(pid, out var p)) { parts.Insert(0, p.Name); cur = p; }
            return string.Join(" / ", parts);
        }
        var items = all.Where(f => f.Id != exclude).Select(f => new { id = f.Id, path = PathOf(f) }).ToList();
        return Ok(items);
    }

    /// <summary>
    /// v1.10.62 — writable-all: alle beschreibbaren Ordner ÜBER alle Scopes
    /// hinweg. Fürs Copy-Modal, das Cross-Scope-Kopien erlaubt (z.B. Datei
    /// aus Group → Public). Rückgabe enthält Scope für Client-side Grouping.
    /// </summary>
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/folders/writable-all")]
    public async Task<IActionResult> WritableFoldersAll(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var myGroupIds = await _db.GroupMemberships
            .Where(m => m.UserId == me.Id).Select(m => m.GroupId).ToListAsync(ct);
        // Personal (nur eigene) + Public (alle) + Group (nur wo Mitglied
        // oder Admin — Admin sieht alle Group-Ordner).
        var q = _db.Folders.Where(f =>
            (f.Scope == FileScope.Personal && f.OwnerUserId == me.Id)
         || (f.Scope == FileScope.Public)
         || (f.Scope == FileScope.Group && (myGroupIds.Contains(f.OwnerGroupId!.Value) || me.Role == UserRole.Admin)));
        var all = await q.OrderBy(f => f.Scope).ThenBy(f => f.Name).ToListAsync(ct);
        // v1.10.104: Copy/Move-Modal darf keine Ziele in verborgenen
        // Private-Public-Subtrees anbieten (der User kann sie nicht sehen).
        if (me.Role != UserRole.Admin)
        {
            var hidden = await _access.GetHiddenPublicFolderIdsAsync(me, ct);
            if (hidden.Count > 0)
                all = all.Where(f => !hidden.Contains(f.Id)).ToList();
        }
        var byId = all.ToDictionary(f => f.Id);
        string PathOf(Folder f)
        {
            var parts = new List<string> { f.Name };
            var cur = f;
            while (cur.ParentFolderId is Guid pid && byId.TryGetValue(pid, out var p)) { parts.Insert(0, p.Name); cur = p; }
            return string.Join(" / ", parts);
        }
        // v1.10.67: parentId + isRoot fürs Baum-Browser-Modal (Copy/Move
        // im Web und iOS bauen daraus einen aufklappbaren Ordnerbaum
        // statt einer flachen Dropdown-Liste).
        var items = all.Select(f => new
        {
            id = f.Id,
            path = PathOf(f),
            name = f.Name,
            scope = f.Scope.ToString(),
            parentId = f.ParentFolderId,
            groupId = f.OwnerGroupId,
            isRoot = f.ParentFolderId == null,
        }).ToList();
        return Ok(items);
    }

    /// <summary>Full folder tree for the current scope — used by the left tree panel.</summary>
    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/folders/tree")]
    public async Task<IActionResult> Tree(string scope, Guid? groupId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!Enum.TryParse<FileScope>(scope, true, out var s)) return BadRequest();

        IQueryable<Folder> q = _db.Folders.Where(f => f.Scope == s);
        q = s switch
        {
            FileScope.Personal => q.Where(f => f.OwnerUserId == me.Id),
            FileScope.Group => groupId is Guid g ? q.Where(f => f.OwnerGroupId == g) : q.Where(f => false),
            FileScope.Public => q.Where(f => f.OwnerUserId == null && f.OwnerGroupId == null),
            _ => q.Where(f => false),
        };
        // v1.10.37: Wir haben genau EINEN offiziellen Scope-Root (den, den
        // FolderService.GetOrCreateRootAsync einmalig anlegt). In gealterten
        // Datenbanken existieren aber gelegentlich mehrere Ordner mit
        // ParentFolderId == null pro Scope — Race-Conditions bei parallelem
        // Erstverwendung, Migrations-Artefakte, oder Legacy-Code. Vorher
        // erschienen die alle als getrennte Wurzeln UND alle bekamen den
        // Scope-Namen ("Public"), weshalb Marcus zwei Zeilen "Public"
        // untereinander sah und der Klick zwischen ihnen loopte (beide
        // Links zeigten auf dieselbe pretty-URL /browse/public).
        //
        // Fix: den ältesten Root als offizielle Wurzel wählen (deterministisch
        // per CreatedAt), alle weiteren Root-Kandidaten als seine Kinder
        // rendern. Nur der offizielle Root wird mit dem Scope-Namen benannt;
        // andere behalten ihren tatsächlichen Ordner-Namen — sonst sieht man
        // "Public / Public" statt "Public / <realer-Name>".
        var all = await q.OrderBy(f => f.Name).ToListAsync(ct);
        // v1.10.104: Private-Ordner im Public-Scope aus dem Tree entfernen,
        // wenn der User keinen Grant hat. Personal + Group werden vom
        // Endpoint sowieso vorgefiltert (own / member-of).
        if (s == FileScope.Public && me.Role != UserRole.Admin)
        {
            var hidden = await _access.GetHiddenPublicFolderIdsAsync(me, ct);
            if (hidden.Count > 0)
                all = all.Where(f => !hidden.Contains(f.Id)).ToList();
        }
        var rootCandidates = all.Where(f => f.ParentFolderId is null)
                                .OrderBy(f => f.CreatedAt)
                                .ThenBy(f => f.Id)
                                .ToList();
        if (rootCandidates.Count == 0) return Ok(new List<object>());
        var officialRoot = rootCandidates[0];
        var byParent = all.GroupBy(f => f.ParentFolderId ?? Guid.Empty)
                          .ToDictionary(g => g.Key, g => g.ToList());
        if (rootCandidates.Count > 1)
        {
            if (!byParent.TryGetValue(officialRoot.Id, out var kids))
            {
                kids = new List<Folder>();
                byParent[officialRoot.Id] = kids;
            }
            kids.AddRange(rootCandidates.Skip(1));
        }

        object Build(Folder f, bool isOfficialRoot) => new
        {
            id = f.Id,
            name = isOfficialRoot ? s.ToString() : f.Name,
            emoji = f.Emoji,
            color = f.Color,
            children = byParent.TryGetValue(f.Id, out var kids)
                ? kids.Select(k => Build(k, false)).ToArray()
                : Array.Empty<object>(),
        };
        return Ok(new[] { Build(officialRoot, true) });
    }

    // ── JSON browse for mobile clients ────────────────────────────────────
    public record MobileScopeTile(string Scope, Guid? GroupId, string Name);
    public record MobileFolderItem(Guid Id, string Name);
    public record MobileFileItem(Guid Id, string Name, long SizeBytes, string ContentType,
        DateTimeOffset CreatedAt, string? OwnerName, string? AiTags, string? AiRiskFlag);
    public record MobileBreadcrumb(string Name, string Path);
    public record MobileBrowseResponse(
        List<MobileFolderItem> Subfolders, List<MobileFileItem> Files,
        List<MobileBreadcrumb> Breadcrumbs, Guid CurrentFolderId, bool CanWrite, bool CanManage);

    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/browse/scopes")]
    public async Task<IActionResult> MobileScopes(CancellationToken ct)
    {
        // v1.10.102: Marcus's Präzisierung — Gruppen sind reine Verteiler für
        // „Teilen mit → Gruppe" via DirectShare, keine eigene Team-Bibliothek.
        // Tiles reduziert auf Personal + Public. Bestehende Group-Scope-Files
        // bleiben in der DB und via /api/v1/browse/list?scope=Group&groupId=…
        // weiter erreichbar (BC), aber tauchen nicht mehr als Tile auf. Für
        // den Signatur-Wizard (SignaturesView.loadPersonalFiles) sammeln wir
        // die Files trotzdem quer über alle Scopes — dort ist der Groups-Filter
        // erwünscht damit auch PDFs aus alten Gruppen-Files signierbar bleiben.
        await Task.CompletedTask;
        var tiles = new List<MobileScopeTile>
        {
            new("Personal", null, "Personal"),
            new("Public", null, "Public"),
        };
        return Ok(tiles);
    }

    [Authorize(Policy = "ApiUser")]
    [HttpGet("/api/v1/browse/list")]
    public async Task<IActionResult> MobileList(string scope, Guid? groupId, string? path, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!Enum.TryParse<FileScope>(scope, true, out var s)) return BadRequest();
        if (s == FileScope.Group && groupId is Guid g0
            && me.Role != UserRole.Admin && !await _access.IsGroupMemberAsync(me, g0, ct))
            return Forbid();

        var root = await _folders.GetOrCreateRootAsync(s,
            s == FileScope.Personal ? me.Id : null,
            s == FileScope.Group ? groupId : null,
            me, ct);
        var segs = string.IsNullOrEmpty(path)
            ? Array.Empty<string>()
            : path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = await _folders.ResolvePathAsync(root, segs, ct);
        if (current is null) return NotFound();
        if (!await _folders.CanReadAsync(current, me, ct)) return Forbid();
        var subs = await _folders.ListSubfoldersAsync(current, ct);
        // v1.10.104: gleicher Privacy-Filter wie in der Web-Route.
        subs = await _folders.FilterVisibleSubfoldersAsync(subs, me, ct);
        var files = await _folders.ListFilesAsync(current, ct);
        var ancestry = await _folders.GetAncestryAsync(current, ct);
        var canWrite = await _folders.CanWriteAsync(current, me, ct);
        var canManage = await _folders.CanManageAsync(current, me, ct);
        var crumbs = new List<MobileBreadcrumb>();
        for (int i = 0; i < ancestry.Count; i++)
        {
            var name = i == 0 ? (s == FileScope.Group ? "Group" : s.ToString()) : ancestry[i].Name;
            var p = string.Join('/', ancestry.Skip(1).Take(i).Select(a => Uri.EscapeDataString(a.Name)));
            crumbs.Add(new MobileBreadcrumb(name, p));
        }
        var resp = new MobileBrowseResponse(
            subs.Select(f => new MobileFolderItem(f.Id, f.Name)).ToList(),
            files.Select(f => new MobileFileItem(f.Id, f.Name, f.SizeBytes, f.ContentType,
                f.CreatedAt, f.Owner?.DisplayName, f.AiTags, f.AiRiskFlag)).ToList(),
            crumbs, current.Id, canWrite, canManage);
        return Ok(resp);
    }

    // v1.10.66: preview-url wurde von hier nach FilesController umgezogen
    // (ApiUser-Policy statt WebUser-only) damit iOS mit Bearer-Token nicht
    // mehr HTML statt JSON kriegt. Web-Client funktioniert weiter, weil
    // ApiUser sowohl Cookie- als auch JWT-Schemes akzeptiert.
}
