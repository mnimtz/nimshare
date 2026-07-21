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

    /// <summary>Delete an entire signature request — cascades participants,
    /// fields, and audits. Refuses on Completed requests unless caller is the
    /// initiator (a signed workflow is legally interesting; keep it around by
    /// default). The source file and any FinalFile are left in place.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var reqRow = await _db.SignatureRequests.FindAsync(new object[] { id }, ct);
        if (reqRow is null) return NotFound();
        if (reqRow.InitiatorUserId != me.Id) return Forbid();

        var pids = await _db.SignatureParticipants.Where(p => p.RequestId == id).Select(p => p.Id).ToListAsync(ct);
        _db.SignatureAudits.RemoveRange(_db.SignatureAudits.Where(a => a.RequestId == id));
        _db.SignatureFields.RemoveRange(_db.SignatureFields.Where(f => f.RequestId == id));
        _db.SignatureParticipants.RemoveRange(_db.SignatureParticipants.Where(p => p.RequestId == id));
        _db.SignatureRequests.Remove(reqRow);
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

    /// <summary>Remove a single field from a Draft request — used by the
    /// wizard's × button so the user can undo a mis-placed box without
    /// re-doing the whole placement.</summary>
    [HttpDelete("{id:guid}/fields/{fieldId:guid}")]
    public async Task<IActionResult> RemoveField(Guid id, Guid fieldId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var reqRow = await _db.SignatureRequests.FindAsync(new object[] { id }, ct);
        if (reqRow is null) return NotFound();
        if (reqRow.InitiatorUserId != me.Id) return Forbid();
        if (reqRow.Status != SignatureRequestStatus.Draft) return Problem(statusCode: 409);
        var f = await _db.SignatureFields.SingleOrDefaultAsync(x => x.Id == fieldId && x.RequestId == id, ct);
        if (f is null) return NotFound();
        _db.SignatureFields.Remove(f);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, [FromQuery] Guid? templateId, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await LoadFullAsync(id, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id) return Forbid();
        if (r.Status != SignatureRequestStatus.Draft) return Problem(statusCode: 409, title: "Bereits versendet.");
        if (!r.Participants.Any(p => p.Role == SignatureParticipantRole.Signer))
            return Problem(statusCode: 422, title: "Mindestens ein Unterzeichner ist erforderlich.");

        // Look up the requested / default email template so EmailInviteAsync
        // can render it with placeholders.
        EmailTemplate? template = null;
        if (templateId is Guid tid)
        {
            template = await _db.EmailTemplates.SingleOrDefaultAsync(
                t => t.Id == tid && t.OwnerUserId == me.Id, ct);
        }
        template ??= await _db.EmailTemplates.FirstOrDefaultAsync(
            t => t.OwnerUserId == me.Id
                && t.Kind == EmailTemplateKind.SignatureInvite
                && t.IsDefault, ct);

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
            await EmailInviteAsync(r, me, p, template, ct);
        }
        // Bump address-book usage for every participant we just emailed —
        // "recently used" then sorts them to the top on next request.
        foreach (var p in r.Participants)
        {
            var email = p.Email.Trim().ToLowerInvariant();
            var c = await _db.Contacts.SingleOrDefaultAsync(
                x => x.OwnerUserId == me.Id && x.Email == email, ct);
            if (c is null)
            {
                _db.Contacts.Add(new Contact
                {
                    OwnerUserId = me.Id, Email = email, Name = p.Name,
                    LastUsedAt = DateTimeOffset.UtcNow, UseCount = 1,
                });
            }
            else
            {
                c.LastUsedAt = DateTimeOffset.UtcNow;
                c.UseCount++;
                if (string.IsNullOrEmpty(c.Name)) c.Name = p.Name;
            }
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
        SignatureParticipant p, EmailTemplate? template, CancellationToken ct)
    {
        var raw = ExtractStashedToken(p);
        var url = $"{Request.Scheme}://{Request.Host}/sign/{p.Id}?t={raw}";
        var isSigner = p.Role == SignatureParticipantRole.Signer;

        string subject, body;
        if (template is not null)
        {
            // User-authored template: render placeholders. Localisation lives
            // inside the template itself (author picked the language).
            var ctx = new Dictionary<string, string?>
            {
                ["recipient.name"] = p.Name,
                ["recipient.email"] = p.Email,
                ["sender.name"] = initiator.DisplayName,
                ["sender.email"] = initiator.Email,
                ["sender.action"] = isSigner
                    ? _l["sig.manual_remind.action_sign"].Value
                    : _l["sig.manual_remind.action_review"].Value,
                ["doc.title"] = r.Title,
                ["doc.name"] = r.SourceFile?.Name ?? r.Title,
                ["url"] = url,
                ["message"] = r.Message ?? "",
            };
            subject = EmailTemplateRenderer.Render(template.Subject, ctx);
            body = EmailTemplateRenderer.Render(template.BodyMarkdown, ctx);
            // v1.10.75: Selbstheilung für User-Templates die durch den
            // AI-Draft-Bug in v1.10.74 kaputt gespeichert wurden (Subject
            // leer, "SUBJECT: xxx" als erste Zeile im BodyMarkdown).
            // Ohne diesen Fix landet "SUBJECT: xxx" im Empfänger-Inbox als
            // Body und der Betreff ist leer. Marcus's Bug-Report.
            if (string.IsNullOrWhiteSpace(subject) || body.StartsWith("SUBJECT:", StringComparison.OrdinalIgnoreCase))
            {
                var combined = string.IsNullOrWhiteSpace(subject) ? body : (subject + "\n" + body);
                var (subj, bod) = NimShare.Api.Controllers.AiController.SplitSubjectBody(combined);
                if (!string.IsNullOrWhiteSpace(subj)) subject = subj;
                body = bod;
            }
        }
        else
        {
            // No template: fall back to the built-in localised copy.
            var prev = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                    string.IsNullOrWhiteSpace(initiator.PreferredCulture) ? "en" : initiator.PreferredCulture);
            }
            catch { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
            try
            {
                subject = _l[isSigner ? "sig.invite.subject_signer" : "sig.invite.subject_viewer", r.Title].Value;
                var msgPrefix = string.IsNullOrEmpty(r.Message) ? "" : _l["sig.invite.msg_prefix", r.Message].Value;
                body = _l[isSigner ? "sig.invite.body_signer" : "sig.invite.body_viewer",
                    p.Name, initiator.DisplayName, r.Title, url, msgPrefix].Value;
            }
            finally { CultureInfo.CurrentUICulture = prev; }
        }

        string? emailErr = null;
        try { await _notify.SendShareLinkAsync(p.Email, initiator.DisplayName, subject, body, ct); }
        catch (Exception ex) { emailErr = ex.Message; }
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = r.Id, ParticipantId = p.Id, Kind = SignatureAuditKind.Invited,
            Note = emailErr is null ? "invited" : $"email-failed: {emailErr}",
        });
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

    /// <summary>Verify the embedded PAdES signature of a finalized request's
    /// signed PDF. Downloads the final blob, re-hashes the byte range covered
    /// by /ByteRange, and reports whether the crypto matches. Auth-guarded to
    /// initiator + admin (v1.10.16).</summary>
    [HttpGet("{id:guid}/verify")]
    public async Task<IActionResult> VerifySignature(Guid id,
        [FromServices] IBlobStorageService blobs,
        [FromServices] IPdfSignatureService pdfSign,
        CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests.Include(x => x.FinalFile)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id && me.Role != UserRole.Admin) return Forbid();
        if (r.FinalFile is null) return Problem(statusCode: 409, title: "Request not finalized yet.");
        var ms = new MemoryStream();
        await blobs.DownloadToAsync(r.FinalFile.BlobPath, ms, ct);
        var verdict = pdfSign.Verify(ms.ToArray());
        if (verdict is null)
            return Ok(new { signed = false, message = "No embedded PAdES signature found. This PDF was finalized without an initiator certificate." });
        return Ok(new
        {
            signed = true,
            cryptoValid = verdict.CryptoValid,
            coverageComplete = verdict.CoverageComplete,
            signer = verdict.SignerCommonName,
            thumbprint = verdict.Thumbprint,
            validFrom = verdict.NotBefore,
            validTo = verdict.NotAfter,
            diagnostic = verdict.Diagnostic,
        });
    }

    /// <summary>
    /// v1.10.40 — download / inline-view the final signed PDF. Vorher ging
    /// der "📥 Signiertes PDF" Button auf /browse/personal — man musste den
    /// Dateinamen kennen. Jetzt direkter Zugriff über die Request-Id.
    /// </summary>
    [HttpGet("{id:guid}/signed-pdf")]
    public async Task<IActionResult> SignedPdf(Guid id,
        [FromServices] IBlobStorageService blobs, bool download = false, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests.Include(x => x.FinalFile)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        // Alle Beteiligten (Initiator + Participants) dürfen das signierte
        // Endprodukt sehen — bei einem regulären Signaturvorgang ist das ja
        // genau das Ergebnis das alle geteilt bekommen sollten.
        var isParticipant = await _db.SignatureParticipants
            .AnyAsync(p => p.RequestId == id && p.Email == me.Email, ct);
        if (r.InitiatorUserId != me.Id && me.Role != UserRole.Admin && !isParticipant)
            return Forbid();
        if (r.FinalFile is null)
            return Problem(statusCode: 409, title: "Request not finalized yet.",
                detail: $"Status is {r.Status}. Try /finalize first.");
        var ms = new MemoryStream();
        await blobs.DownloadToAsync(r.FinalFile.BlobPath, ms, ct);
        ms.Position = 0;
        var fn = r.FinalFile.Name;
        // Inline by default → Browser zeigt PDF direkt an. ?download=true
        // erzwingt Content-Disposition: attachment.
        if (download)
            return File(ms, "application/pdf", fn);
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{fn}\"";
        return File(ms, "application/pdf");
    }

    /// <summary>
    /// v1.10.40 — Diagnose + Trigger. Marcus's Fall "Status bleibt auf läuft
    /// nach signieren": entweder fehlt ein Beteiligter (allDone=false), oder
    /// der Background-Finalizer ist an einer Exception gestorben ohne dass
    /// jemand die Logs liest. Der Endpoint sagt Marcus was los ist UND
    /// versucht bei Bedarf synchron zu finalisieren.
    /// </summary>
    [HttpPost("{id:guid}/finalize")]
    public async Task<IActionResult> ForceFinalize(Guid id,
        [FromServices] ISignatureFinalizerService finalizer, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var r = await _db.SignatureRequests.Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        if (r.InitiatorUserId != me.Id && me.Role != UserRole.Admin) return Forbid();
        if (r.Status == SignatureRequestStatus.Completed)
            return Ok(new { status = "Completed", finalFileId = r.FinalFileId, note = "Already completed." });

        // Zunächst prüfen wer noch aussteht — das ist die häufigste Ursache
        // für "steckt auf läuft".
        var pending = r.Participants
            .Where(p => !(
                (p.Role == SignatureParticipantRole.Signer && p.Status == SignatureParticipantStatus.Signed)
                || (p.Role == SignatureParticipantRole.Viewer
                    && (p.Status == SignatureParticipantStatus.Viewed || p.Status == SignatureParticipantStatus.Signed))
            ))
            .Select(p => new { p.Id, p.Name, p.Email, p.Role, p.Status })
            .ToList();
        if (pending.Count > 0)
        {
            return Ok(new
            {
                status = r.Status.ToString(),
                message = "Waiting on participants — finalize deferred.",
                pending,
            });
        }

        // Alle sind fertig, aber Status hängt → Finalizer synchron nochmal
        // laufen lassen und Fehler an den Aufrufer geben.
        try
        {
            await finalizer.TryFinalizeAsync(r.Id, ct);
        }
        catch (Exception ex)
        {
            return Problem(statusCode: 500, title: "Finalizer threw.", detail: ex.ToString());
        }
        var after = await _db.SignatureRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return Ok(new
        {
            status = after?.Status.ToString() ?? "?",
            finalFileId = after?.FinalFileId,
            note = after?.Status == SignatureRequestStatus.Completed
                ? "Finalized successfully."
                : "Finalizer ran but state did not change to Completed — check server logs.",
        });
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
