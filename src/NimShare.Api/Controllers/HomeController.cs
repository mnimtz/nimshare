using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

public class HomeController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly ILocalAuthService _auth;

    public HomeController(NimShareDbContext db, ICurrentUserService users, ILocalAuthService auth)
    {
        _db = db;
        _users = users;
        _auth = auth;
    }

    [Route("/")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // First-run: force the setup wizard until at least one Admin exists.
        if (await _auth.IsFirstRunAsync(ct))
            return RedirectToAction("Setup", "Account");
        if (User.Identity?.IsAuthenticated ?? false)
            return RedirectToAction(nameof(Dashboard));
        return View("Welcome");
    }

    [Authorize]
    [Route("/dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var files = await _db.Files
            .Where(f => f.OwnerId == user.Id && f.Status == StorageFileStatus.Ready)
            .OrderByDescending(f => f.CreatedAt).Take(10)
            .ToListAsync(ct);
        var linkCount = await _db.ShareLinks.CountAsync(l => l.OwnerId == user.Id, ct);
        // v1.10.24: Quota-Anzeige nur Personal-Scope. Public/Group-Dateien
        // laufen im Shared-Storage, zählen nicht gegen das persönliche Limit.
        var totalPersonalBytes = await _db.Files
            .Where(f => f.OwnerId == user.Id
                && f.Scope == NimShare.Core.Entities.FileScope.Personal
                && f.Status == StorageFileStatus.Ready)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        ViewData["UserName"] = user.DisplayName;
        ViewData["FileCount"] = files.Count;
        ViewData["LinkCount"] = linkCount;
        ViewData["UsedBytes"] = totalPersonalBytes;
        ViewData["QuotaBytes"] = user.QuotaBytes;
        return View(files);
    }

    [Authorize]
    [Route("/upload")]
    public IActionResult Upload() => View();

    [Authorize]
    [Route("/links")]
    public async Task<IActionResult> Links(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        // v1.10.61 — Marcus's Wunsch: Aufteilung in "Privat" vs "Öffentlich"
        // basierend auf dem SCOPE der Zieldatei/-ordner, nicht auf einem
        // separaten IsPublic-Flag. Semantik:
        //   Privat: eigene Links auf Dateien/Ordner im Personal-Scope
        //           (echt privat, nur für mich sinnvoll)
        //   Öffentlich: alle Links (egal wer Owner) wo Ziel im Public-Scope
        //           liegt — automatisch für ALLE User sichtbar, weil die
        //           Zieldatei sowieso alle sehen können. Plus admin-
        //           explizite IsPublic-Links bleiben hier als Backward-
        //           Compat sichtbar.
        //   Gruppen: eigene Links auf Group-Scope-Dateien (Teilbereich
        //           dazwischen — technisch privat weil nur Gruppen-Mitglieder
        //           können, aber nicht dein-persönliches).
        var all = await _db.ShareLinks
            .Include(l => l.File)
            .Include(l => l.Folder)
            .Include(l => l.Owner)
            .Where(l => l.OwnerId == user.Id
                     || (l.File != null && l.File.Scope == FileScope.Public)
                     || (l.Folder != null && l.Folder.Scope == FileScope.Public)
                     || l.IsPublic)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        bool IsPublicScope(ShareLink l)
            => (l.File != null && l.File.Scope == FileScope.Public)
            || (l.Folder != null && l.Folder.Scope == FileScope.Public)
            || l.IsPublic;
        bool IsGroupScope(ShareLink l)
            => (l.File != null && l.File.Scope == FileScope.Group)
            || (l.Folder != null && l.Folder.Scope == FileScope.Group);

        var privateLinks = all
            .Where(l => l.OwnerId == user.Id && !IsPublicScope(l) && !IsGroupScope(l))
            .ToList();
        var groupLinks = all
            .Where(l => l.OwnerId == user.Id && IsGroupScope(l))
            .ToList();
        var publicLinks = all
            .Where(l => IsPublicScope(l))
            .ToList();

        ViewData["PublicLinks"] = publicLinks;
        ViewData["GroupLinks"] = groupLinks;
        ViewData["IsAdmin"] = user.Role == UserRole.Admin;
        return View(privateLinks);
    }

    [Authorize]
    [Route("/settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        ViewData["User"] = user;
        var domains = await _db.CustomDomains
            .Where(x => x.OwnerId == user.Id && x.VerificationStatus != CustomDomainVerificationStatus.Deleted)
            .ToListAsync(ct);
        return View(domains);
    }
}
