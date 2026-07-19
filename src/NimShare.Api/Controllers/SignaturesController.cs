using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Signature-workflow API for the requester side. Public participant sign
/// endpoints live on SignController (/sign/{token}/*).
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/signatures")]
public class SignaturesController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IPasswordHasher _hasher;
    private readonly INotificationService _notify;
    private readonly IUserNotifier _in;
    private readonly IDataProtector _stash;
    private readonly IStringLocalizer<SharedResources> _l;

    public SignaturesController(NimShareDbContext db, ICurrentUserService users,
        IPasswordHasher hasher, INotificationService notify, IUserNotifier inApp,
        IDataProtectionProvider dp, IStringLocalizer<SharedResources> localizer)
    {
        _db = db; _users = users; _hasher = hasher; _notify = notify; _in = inApp;
        _stash = dp.CreateProtector("NimShare.Signature.Chain.v1");
        _l = localizer;
    }

    public record RequestDto(Guid Id, Guid SourceFileId, string SourceFileName, string Title,
        string? Message, string Status, string DeliveryOrder, DateTimeOffset CreatedAt,
        DateTimeOffset? SentAt, DateTimeOffset? CompletedAt, Guid? FinalFileId,
        List<ParticipantDto> Participants, List<FieldDto> Fields);
    public record ParticipantDto(Guid Id, string Email, string Name, string Role, int Order, string Status,
        DateTimeOffset? ViewedAt, DateTimeOffset? SignedAt);
    public record FieldDto(Guid Id, Guid ParticipantId, string Type, int Page, string Anchor,
        double X, double Y, double Width, double Height, string? Label, string? Value);
    public record CreateReq(Guid SourceFileId, string? Title, string? Message, string? DeliveryOrder, DateTimeOffset? Deadline);
    public record AddParticipantReq(string Email, string Name, string Role, int Order);
    public record AddFieldReq(Guid ParticipantId, string Type, int Page, string Anchor, string? Label,
        double? X, double? Y, double? Width, double? Height);

    [HttpGet]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.SignatureRequests
            .Where(r => r.InitiatorUserId == me.Id)
            .Include(r => r.SourceFile)
            .Include(r => r.Participants)
            .Include(r => r.Fields)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var req = await LoadFullAsync(id, ct);
        if (req is null) return NotFound();
        if (req.InitiatorUserId != me.Id && me.Role != UserRole.Admin) return Forbid();
        return Ok(ToDto(req));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var file = await _db.Files.Include(f => f.Owner)
            .SingleOrDefaultAsync(f => f.Id == req.SourceFileId && f.Status == StorageFileStatus.Ready, ct);
        if (file is null) return NotFound();
        // Only PDFs for the MVP — a text/docx would need a converter.
        if (!(file.ContentType ?? "").Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: 422, title: "Signaturen werden derzeit nur für PDF unterstützt.");
        // Requester must at least be able to read the file.
        if (file.OwnerId != me.Id && me.Role != UserRole.Admin) return Forbid();

        var order = string.Equals(req.DeliveryOrder, "Sequential", StringComparison.OrdinalIgnoreCase)
            ? SignatureDeliveryOrder.Sequential : SignatureDeliveryOrder.Parallel;
        var r = new SignatureRequest
        {
            SourceFileId = file.Id,
            InitiatorUserId = me.Id,
            Title = string.IsNullOrWhiteSpace(req.Title) ? file.Name : req.Title.Trim(),
            Message = req.Message?.Trim(),
            DeliveryOrder = order,
            Deadline = req.Deadline,
        };
        _db.SignatureRequests.Add(r);
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = r.Id,
            Kind = SignatureAuditKind.Invited,
            At = DateTimeOffset.UtcNow,
            Note = "created draft",
        });
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Detail), new { id = r.Id }, ToDto(await LoadFullAsync(r.Id, ct) ?? r));
    }

    [HttpPost("{id:guid}/participants")]
    public async Task<IActionResult> AddParticipant(Guid id, [FromBody] AddParticipantReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var reqRow = await _db.SignatureRequests.FindAsync(new object[] { id }, ct);
        if (reqRow is null) return NotFound();
        if (reqRow.InitiatorUserId != me.Id) return Forbid();
        if (reqRow.Status != SignatureRequestStatus.Draft)
            return Problem(statusCode: 409, title: "Anforderung ist bereits versendet.");
        var role = string.Equals(req.Role, "Viewer", StringComparison.OrdinalIgnoreCase)
            ? SignatureParticipantRole.Viewer : SignatureParticipantRole.Signer;
        var p = new SignatureParticipant
        {
            RequestId = id,
            Email = (req.Email ?? "").Trim().ToLowerInvariant(),
            Name = (req.Name ?? "").Trim(),
            Role = role,
            Order = req.Order,
        };
        _db.SignatureParticipants.Add(p);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = p.Id });
    }

    [HttpDelete("{id:guid}/participants/{pid:guid}")]
    public async Task<IActionResult> RemoveParticipant(Guid id, Guid pid, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var reqRow = await _db.SignatureRequests.FindAsync(new object[] { id }, ct);
        if (reqRow is null) return NotFound();
        if (reqRow.InitiatorUserId != me.Id) return Forbid();
        if (reqRow.Status != SignatureRequestStatus.Draft)
            return Problem(statusCode: 409, title: "Anforderung ist bereits versendet.");
        var p = await _db.SignatureParticipants.SingleOrDefaultAsync(x => x.Id == pid && x.RequestId == id, ct);
        if (p is null) return NotFound();
        // Cascade fields too.
        _db.SignatureFields.RemoveRange(_db.SignatureFields.Where(f => f.ParticipantId == pid));
        _db.SignatureParticipants.Remove(p);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/fields")]
    public async Task<IActionResult> AddField(Guid id, [FromBody] AddFieldReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var reqRow = await _db.SignatureRequests.FindAsync(new object[] { id }, ct);
        if (reqRow is null) return NotFound();
        if (reqRow.InitiatorUserId != me.Id) return Forbid();
        if (reqRow.Status != SignatureRequestStatus.Draft) return Problem(statusCode: 409);

        var type = Enum.TryParse<SignatureFieldType>(req.Type, true, out var t) ? t : SignatureFieldType.Signature;
        var anchor = Enum.TryParse<SignatureFieldAnchor>(req.Anchor, true, out var a) ? a : SignatureFieldAnchor.BottomCenter;
        var f = new SignatureField
        {
            RequestId = id,
            ParticipantId = req.ParticipantId,
            Type = type,
            Page = Math.Max(1, req.Page),
            Anchor = anchor,
            X = req.X ?? 0, Y = req.Y ?? 0,
            Width = req.Width ?? 0, Height = req.Height ?? 0,
            Label = req.Label,
        };
        _db.SignatureFields.Add(f);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = f.Id });
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await LoadFullAsync(id, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id) return Forbid();
        if (r.Status != SignatureRequestStatus.Draft) return Problem(statusCode: 409, title: "Bereits versendet.");
        if (!r.Participants.Any(p => p.Role == SignatureParticipantRole.Signer))
            return Problem(statusCode: 422, title: "Mindestens ein Unterzeichner ist erforderlich.");

        // Mint a token for every participant. Emails go out immediately for
        // parallel delivery; for sequential we only email the lowest-order
        // participant now, and the sign flow triggers the next one.
        foreach (var p in r.Participants)
        {
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            p.TokenHash = _hasher.Hash(raw);
            // Stash the raw token temporarily on the DB row so the sign-flow
            // can rebuild the URL for the *next* participant when acting
            // sequentially. Since the token is opaque (not sensitive by
            // itself once the flow is in-flight), this trade is fine for the
            // MVP; a follow-up can move to a signed session cookie.
            p.DeclinedReason = "TOKEN:" + _stash.Protect(raw);
        }
        r.Status = SignatureRequestStatus.Sent;
        r.SentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Decide who gets an email right now.
        var toEmail = r.DeliveryOrder == SignatureDeliveryOrder.Sequential
            ? new[] { r.Participants.OrderBy(p => p.Order).First() }
            : r.Participants.ToArray();
        foreach (var p in toEmail)
        {
            await EmailInviteAsync(r, me, p, ct);
        }
        // Clear the temp tokens from Reason once we've sent them out; keep
        // just the ones for still-pending participants that need chaining.
        foreach (var p in r.Participants.Where(x => toEmail.Contains(x)))
        {
            p.DeclinedReason = null;
        }
        await _db.SaveChangesAsync(ct);
        HttpContext.RequestServices.GetService<IWebhookDispatcher>()?
            .QueueEvent(me.Id, WebhookEvent.SignatureRequestSent,
                new { requestId = r.Id, title = r.Title, participantCount = r.Participants.Count });

        // Read back the audit rows we just wrote to know which participants
        // actually got their invite email — surface that to the wizard so the
        // requester sees "sent to 2, failed for 1" instead of the misleading
        // "all sent" success screen.
        var invitedAudits = await _db.SignatureAudits
            .Where(a => a.RequestId == r.Id && a.Kind == SignatureAuditKind.Invited)
            .OrderByDescending(a => a.At)
            .ToListAsync(ct);
        var delivery = toEmail.Select(p =>
        {
            var audit = invitedAudits.FirstOrDefault(a => a.ParticipantId == p.Id);
            var ok = audit?.Note == "invited";
            return new
            {
                email = p.Email,
                name = p.Name,
                ok,
                error = ok ? null : (audit?.Note ?? "no-audit"),
            };
        }).ToArray();

        // Flatten RequestDto + delivery so old clients that read RequestDto
        // fields (like iOS) still work; new fields are additive.
        var dto = ToDto(r);
        return Ok(new
        {
            dto.Id,
            dto.SourceFileId,
            dto.SourceFileName,
            dto.Title,
            dto.Message,
            dto.Status,
            dto.DeliveryOrder,
            dto.CreatedAt,
            dto.SentAt,
            dto.CompletedAt,
            dto.FinalFileId,
            dto.Participants,
            dto.Fields,
            delivery,
        });
    }

    private async Task EmailInviteAsync(SignatureRequest r, User initiator,
        SignatureParticipant p, CancellationToken ct)
    {
        var raw = ExtractStashedToken(p);
        var url = $"{Request.Scheme}://{Request.Host}/sign/{p.Id}?t={raw}";
        // Localise against the initiator's preferred culture — participants
        // don't have a user row so we can't look up their own language.
        var prev = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                string.IsNullOrWhiteSpace(initiator.PreferredCulture) ? "en" : initiator.PreferredCulture);
        }
        catch { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
        try
        {
            var isSigner = p.Role == SignatureParticipantRole.Signer;
            var subject = _l[isSigner ? "sig.invite.subject_signer" : "sig.invite.subject_viewer", r.Title].Value;
            var msgPrefix = string.IsNullOrEmpty(r.Message) ? "" : _l["sig.invite.msg_prefix", r.Message].Value;
            var body = _l[isSigner ? "sig.invite.body_signer" : "sig.invite.body_viewer",
                p.Name, initiator.DisplayName, r.Title, url, msgPrefix].Value;
            string? emailErr = null;
            try { await _notify.SendShareLinkAsync(p.Email, initiator.DisplayName, subject, body, ct); }
            catch (Exception ex) { emailErr = ex.Message; }
            _db.SignatureAudits.Add(new SignatureAudit
            {
                RequestId = r.Id, ParticipantId = p.Id, Kind = SignatureAuditKind.Invited,
                Note = emailErr is null ? "invited" : $"email-failed: {emailErr}",
            });
        }
        finally { CultureInfo.CurrentUICulture = prev; }
    }

    private string ExtractStashedToken(SignatureParticipant p)
    {
        if (!(p.DeclinedReason ?? "").StartsWith("TOKEN:")) return "";
        try { return _stash.Unprotect(p.DeclinedReason!["TOKEN:".Length..]); }
        catch { return ""; }
    }

    [HttpPost("{id:guid}/remind")]
    public async Task<IActionResult> ManualRemind(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await LoadFullAsync(id, ct);
        if (r is null || r.InitiatorUserId != me.Id) return Forbid();
        if (r.Status != SignatureRequestStatus.Sent) return Problem(statusCode: 409);
        // For SEQUENTIAL delivery only the person currently in turn gets a
        // reminder — otherwise we'd leak the workflow to downstream signers
        // who aren't supposed to know they're in the queue yet.
        var stillPending = r.Participants.Where(p =>
            p.Status != SignatureParticipantStatus.Signed
            && p.Status != SignatureParticipantStatus.Declined).ToList();
        var toRemind = r.DeliveryOrder == SignatureDeliveryOrder.Sequential
            ? stillPending.OrderBy(p => p.Order).Take(1).ToList()
            : stillPending;
        var prev = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                string.IsNullOrWhiteSpace(me.PreferredCulture) ? "en" : me.PreferredCulture);
        }
        catch { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
        try
        {
            foreach (var p in toRemind)
            {
                var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .Replace("+", "-").Replace("/", "_").TrimEnd('=');
                p.TokenHash = _hasher.Hash(raw);
                var url = $"{Request.Scheme}://{Request.Host}/sign/{p.Id}?t={raw}";
                var action = _l[p.Role == SignatureParticipantRole.Signer
                    ? "sig.manual_remind.action_sign" : "sig.manual_remind.action_review"].Value;
                var subject = _l["sig.manual_remind.subject", r.Title].Value;
                var body = _l["sig.manual_remind.body",
                    p.Name, me.DisplayName, action, r.Title, url].Value;
                try { await _notify.SendShareLinkAsync(p.Email, me.DisplayName, subject, body, ct); } catch { }
                _db.SignatureAudits.Add(new SignatureAudit
                {
                    RequestId = r.Id, ParticipantId = p.Id,
                    Kind = SignatureAuditKind.Invited, Note = "manual-reminder",
                });
            }
        }
        finally { CultureInfo.CurrentUICulture = prev; }
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record UploadSourceReq(string Name, long SizeBytes, string? ContentType);
    public record UploadSourceResp(Guid FileId, string UploadUrl, string UploadMethod, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Fast-path upload for the signature wizard: creates the file directly
    /// inside a "Signatures" subfolder of the caller's Personal library, so
    /// the requester doesn't have to leave the wizard to upload beforehand.
    /// Client then PUTs bytes to UploadUrl and calls /api/v1/files/{id}/complete
    /// exactly like the normal upload flow.
    /// </summary>
    [HttpPost("upload-source")]
    public async Task<IActionResult> UploadSource([FromBody] UploadSourceReq req,
        [FromServices] IFolderService folders, [FromServices] IBlobStorageService blobs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.SizeBytes <= 0) return BadRequest();
        var me = await _users.GetOrProvisionAsync(User, ct);
        var used = await _db.Files.Where(f => f.OwnerId == me.Id && f.Status != StorageFileStatus.Deleted)
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        if (used + req.SizeBytes > me.QuotaBytes)
            return Problem(statusCode: 413, title: "Quota überschritten");

        var root = await folders.GetOrCreateRootAsync(FileScope.Personal, me.Id, null, me, ct);
        var target = await _db.Folders.SingleOrDefaultAsync(
            f => f.ParentFolderId == root.Id && f.Name == "Signatures", ct);
        if (target is null)
        {
            try { target = await folders.CreateChildAsync(root, "Signatures", me, ct); }
            catch (InvalidOperationException)
            {
                target = await _db.Folders.SingleAsync(
                    f => f.ParentFolderId == root.Id && f.Name == "Signatures", ct);
            }
        }

        var ct2 = string.IsNullOrWhiteSpace(req.ContentType) ? "application/pdf" : req.ContentType!;
        var file = new StorageFile
        {
            OwnerId = me.Id,
            Scope = FileScope.Personal,
            FolderId = target.Id,
            Name = req.Name,
            SizeBytes = req.SizeBytes,
            ContentType = ct2,
            Folder = "Signatures",
            Status = StorageFileStatus.Pending,
        };
        file.BlobPath = $"users/{me.Id:N}/{file.Id:N}/{req.Name.Replace('/', '_').Replace('\\', '_')}";
        _db.Files.Add(file);
        await _db.SaveChangesAsync(ct);

        var ticket = blobs.CreateUploadTicket(file.BlobPath);
        return Ok(new UploadSourceResp(file.Id, ticket.UploadUrl.ToString(), ticket.Method, ticket.ExpiresAt));
    }

    /// <summary>Server-side proxy that streams the source PDF from Blob to the
    /// requester's browser — same-origin, so pdf.js can render it without
    /// cross-origin restrictions.</summary>
    [HttpGet("{id:guid}/source-pdf")]
    public async Task<IActionResult> SourcePdf(Guid id, [FromServices] IBlobStorageService blobs, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests.Include(x => x.SourceFile)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r?.SourceFile is null) return NotFound();
        if (r.InitiatorUserId != me.Id && me.Role != UserRole.Admin) return Forbid();
        var ms = new MemoryStream();
        await blobs.DownloadToAsync(r.SourceFile.BlobPath, ms, ct);
        ms.Position = 0;
        return File(ms, "application/pdf");
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests.FindAsync(new object[] { id }, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id) return Forbid();
        if (r.Status == SignatureRequestStatus.Completed) return Problem(statusCode: 409);
        r.Status = SignatureRequestStatus.Cancelled;
        _db.SignatureAudits.Add(new SignatureAudit { RequestId = id, Kind = SignatureAuditKind.Cancelled });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── mapping helpers ──
    private async Task<SignatureRequest?> LoadFullAsync(Guid id, CancellationToken ct) =>
        await _db.SignatureRequests
            .Include(r => r.SourceFile)
            .Include(r => r.Initiator)
            .Include(r => r.Participants)
            .Include(r => r.Fields)
            .SingleOrDefaultAsync(r => r.Id == id, ct);

    private static RequestDto ToDto(SignatureRequest r) => new(
        r.Id, r.SourceFileId, r.SourceFile?.Name ?? "?", r.Title, r.Message,
        r.Status.ToString(), r.DeliveryOrder.ToString(),
        r.CreatedAt, r.SentAt, r.CompletedAt, r.FinalFileId,
        r.Participants.OrderBy(p => p.Order).Select(p => new ParticipantDto(
            p.Id, p.Email, p.Name, p.Role.ToString(), p.Order, p.Status.ToString(),
            p.ViewedAt, p.SignedAt)).ToList(),
        r.Fields.Select(f => new FieldDto(f.Id, f.ParticipantId, f.Type.ToString(),
            f.Page, f.Anchor.ToString(), f.X, f.Y, f.Width, f.Height, f.Label, f.Value)).ToList());
}
