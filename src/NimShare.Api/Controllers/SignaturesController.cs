using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public SignaturesController(NimShareDbContext db, ICurrentUserService users,
        IPasswordHasher hasher, INotificationService notify, IUserNotifier inApp)
    {
        _db = db; _users = users; _hasher = hasher; _notify = notify; _in = inApp;
    }

    public record RequestDto(Guid Id, Guid SourceFileId, string SourceFileName, string Title,
        string? Message, string Status, string DeliveryOrder, DateTimeOffset CreatedAt,
        DateTimeOffset? SentAt, DateTimeOffset? CompletedAt, Guid? FinalFileId,
        List<ParticipantDto> Participants, List<FieldDto> Fields);
    public record ParticipantDto(Guid Id, string Email, string Name, string Role, int Order, string Status,
        DateTimeOffset? ViewedAt, DateTimeOffset? SignedAt);
    public record FieldDto(Guid Id, Guid ParticipantId, string Type, int Page, string Anchor, string? Label, string? Value);
    public record CreateReq(Guid SourceFileId, string? Title, string? Message, string? DeliveryOrder, DateTimeOffset? Deadline);
    public record AddParticipantReq(string Email, string Name, string Role, int Order);
    public record AddFieldReq(Guid ParticipantId, string Type, int Page, string Anchor, string? Label);

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

        foreach (var p in r.Participants)
        {
            var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            p.TokenHash = _hasher.Hash(raw);
            var url = $"{Request.Scheme}://{Request.Host}/sign/{p.Id}?t={raw}";
            var subject = $"NimShare — {(p.Role == SignatureParticipantRole.Signer ? "Bitte unterschreiben" : "Bitte lesen")}: {r.Title}";
            var body = $"Hallo {p.Name},\n\n{me.DisplayName} bittet dich, {(p.Role == SignatureParticipantRole.Signer ? "das folgende Dokument zu unterschreiben" : "das folgende Dokument zur Kenntnis zu nehmen")}: {r.Title}\n\nÖffne diesen Link:\n{url}\n\n{(string.IsNullOrEmpty(r.Message) ? "" : "Nachricht:\n" + r.Message + "\n\n")}Der Link ist personalisiert und nur für dich bestimmt.\n\n— NimShare";
            try { await _notify.SendShareLinkAsync(p.Email, me.DisplayName, subject, body, ct); }
            catch { /* record only, requester sees state */ }
            _db.SignatureAudits.Add(new SignatureAudit
            {
                RequestId = r.Id, ParticipantId = p.Id, Kind = SignatureAuditKind.Invited,
                Note = "invited",
            });
        }
        r.Status = SignatureRequestStatus.Sent;
        r.SentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(r));
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
            f.Page, f.Anchor.ToString(), f.Label, f.Value)).ToList());
}
