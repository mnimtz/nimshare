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

        var subs = await _folders.ListSubfoldersAsync(current, ct);
        var files = await _folders.ListFilesAsync(current, ct);
        var ancestry = await _folders.GetAncestryAsync(current, ct);
        var canWrite = await _folders.CanWriteAsync(current, me, ct);
        var canManage = await _folders.CanManageAsync(current, me, ct);

        ViewData["Scope"] = scope;
        ViewData["GroupId"] = groupId;
        ViewData["Current"] = current;
        ViewData["Ancestry"] = ancestry;
        ViewData["Subfolders"] = subs;
        ViewData["Files"] = files;
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
    public async Task<IActionResult> CreateFolder(Guid parentId, string name, string returnUrl, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var parent = await _db.Folders.FindAsync(new object[] { parentId }, ct);
        if (parent is null) return NotFound();
        if (!await _folders.CanWriteAsync(parent, me, ct)) return Forbid();
        try { await _folders.CreateChildAsync(parent, name, me, ct); }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return Redirect(SafeReturn(returnUrl));
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

    // ── POST: delete folder ────────────────────────────────────────────────
    [HttpPost("folders/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFolder(Guid id, string returnUrl, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var folder = await _db.Folders.FindAsync(new object[] { id }, ct);
        if (folder is null) return NotFound();
        if (!await _folders.CanManageAsync(folder, me, ct)) return Forbid();
        try { await _folders.DeleteAsync(folder, ct); }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return Redirect(SafeReturn(returnUrl));
    }

    private string SafeReturn(string? url) =>
        !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/browse";

    /// <summary>Flat list of the current user's writable folders — used by the Move modal.</summary>
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

    /// <summary>Full folder tree for the current scope — used by the left tree panel.</summary>
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
        var all = await q.OrderBy(f => f.Name).ToListAsync(ct);
        var byParent = all.GroupBy(f => f.ParentFolderId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.ToList());

        object Build(Folder f) => new
        {
            id = f.Id,
            name = f.ParentFolderId is null ? (s.ToString()) : f.Name,
            emoji = f.Emoji,
            color = f.Color,
            children = byParent.TryGetValue(f.Id, out var kids)
                ? kids.Select(Build).ToArray()
                : Array.Empty<object>(),
        };
        var roots = all.Where(f => f.ParentFolderId is null).Select(Build).ToList();
        return Ok(roots);
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
        var me = await _users.GetOrProvisionAsync(User, ct);
        var groups = await _access.ListMyGroupsAsync(me, ct);
        var tiles = new List<MobileScopeTile>
        {
            new("Personal", null, "Personal"),
            new("Public", null, "Public"),
        };
        tiles.AddRange(groups.Select(g => new MobileScopeTile("Group", g.Id, g.Name)));
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

    /// <summary>Time-limited download URL for inline preview (image / PDF iframe).</summary>
    [HttpGet("/api/v1/files/{id:guid}/preview-url")]
    public async Task<IActionResult> PreviewUrl(Guid id, [FromServices] IFileAccessService access, [FromServices] IBlobStorageService blobs, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound();
        if (!await access.CanReadAsync(user, file, ct)) return Forbid();
        var sas = blobs.CreateDownloadSas(file.BlobPath, file.Name, file.ContentType, TimeSpan.FromMinutes(5));
        return Ok(new { url = sas.ToString(), contentType = file.ContentType, name = file.Name });
    }
}
