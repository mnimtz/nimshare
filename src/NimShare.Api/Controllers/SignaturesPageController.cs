using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>Requester-facing HTML pages for the signature workflow.</summary>
[Authorize(Policy = "WebUser")]
public class SignaturesPageController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public SignaturesPageController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    [HttpGet("/signatures")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.SignatureRequests
            .Where(r => r.InitiatorUserId == me.Id)
            .Include(r => r.SourceFile)
            .Include(r => r.Participants)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        return View(rows);
    }

    [HttpGet("/signatures/new")]
    public async Task<IActionResult> NewRequest(Guid? fileId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        // Only the user's own PDFs are pickable in the MVP.
        var pdfs = await _db.Files
            .Where(f => f.OwnerId == me.Id && f.Status == StorageFileStatus.Ready
                && f.ContentType.Contains("pdf"))
            .OrderByDescending(f => f.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        ViewData["Pdfs"] = pdfs;
        // Preselect via ?fileId=… (used by the Browse right-click "Signatur"
        // action). Only pre-select if the file actually is a PDF the user owns.
        ViewData["PreselectFileId"] = fileId is Guid pf && pdfs.Any(p => p.Id == pf) ? (Guid?)pf : null;
        return View();
    }
}
