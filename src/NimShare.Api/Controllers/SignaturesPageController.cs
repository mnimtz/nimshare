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
        // Load the newest Invited/reminder audit per participant so the view
        // can render "✉️ invited" vs "⚠️ email-failed: <error>" inline. This is
        // how Marcus (and any requester) finds out WHY nothing arrived without
        // needing /diagnostics or the log stream.
        var reqIds = rows.Select(r => r.Id).ToList();
        var audits = await _db.SignatureAudits
            .Where(a => reqIds.Contains(a.RequestId)
                && a.Kind == NimShare.Core.Entities.SignatureAuditKind.Invited
                && a.ParticipantId != null)
            .OrderByDescending(a => a.At)
            .ToListAsync(ct);
        // Newest audit per (request, participant) wins.
        var lastNote = new Dictionary<(Guid RequestId, Guid ParticipantId), string?>();
        foreach (var a in audits)
        {
            var key = (a.RequestId, a.ParticipantId!.Value);
            if (!lastNote.ContainsKey(key)) lastNote[key] = a.Note;
        }
        ViewData["LatestInviteNote"] = lastNote;
        return View(rows);
    }

    [HttpGet("/signatures/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests
            .Include(x => x.SourceFile)
            .Include(x => x.Participants)
            .Include(x => x.Initiator)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id && me.Role != NimShare.Core.Entities.UserRole.Admin)
            return Forbid();

        var fields = await _db.SignatureFields
            .Where(f => f.RequestId == id)
            .ToListAsync(ct);
        var audits = await _db.SignatureAudits
            .Where(a => a.RequestId == id)
            .OrderBy(a => a.At)
            .ToListAsync(ct);
        // Latest invite-audit per participant (for "invited" vs "email-failed:").
        var latestInvite = audits
            .Where(a => a.Kind == NimShare.Core.Entities.SignatureAuditKind.Invited && a.ParticipantId != null)
            .GroupBy(a => a.ParticipantId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.At).First());

        ViewData["Fields"] = fields;
        ViewData["Audits"] = audits;
        ViewData["LatestInvite"] = latestInvite;
        return View("Detail", r);
    }

    // v1.10.85: Dedizierte Audit-Ansicht — vollständiges Forensik-Log
    // (jedes Event mit Timestamp, Wer, IP-Klartext falls vorhanden, IP-Hash,
    // UserAgent, Country, City, DeviceType, Timezone, Notiz). Auf der
    // Detail-Seite ist es kompakt; hier ist alles auf einer druckbaren
    // Seite versammelt. Access-Regel identisch zu Detail (nur Initiator
    // oder Admin).
    [HttpGet("/signatures/{id:guid}/audit")]
    public async Task<IActionResult> Audit(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests
            .Include(x => x.SourceFile)
            .Include(x => x.Participants)
            .Include(x => x.Initiator)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id && me.Role != NimShare.Core.Entities.UserRole.Admin)
            return Forbid();

        var fields = await _db.SignatureFields
            .Where(f => f.RequestId == id)
            .OrderBy(f => f.Page).ThenBy(f => f.Y).ToListAsync(ct);
        var audits = await _db.SignatureAudits
            .Where(a => a.RequestId == id)
            .OrderBy(a => a.At).ToListAsync(ct);

        ViewData["Fields"] = fields;
        ViewData["Audits"] = audits;
        return View("Audit", r);
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
