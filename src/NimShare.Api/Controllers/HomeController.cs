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
        var totalBytes = files.Sum(f => f.SizeBytes);
        ViewData["UserName"] = user.DisplayName;
        ViewData["FileCount"] = files.Count;
        ViewData["LinkCount"] = linkCount;
        ViewData["UsedBytes"] = totalBytes;
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
        var items = await _db.ShareLinks
            .Include(l => l.File)
            .Where(l => l.OwnerId == user.Id)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);
        return View(items);
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
