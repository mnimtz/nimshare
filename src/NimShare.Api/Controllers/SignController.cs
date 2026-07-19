using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Public participant-facing endpoints. Every request must carry ?t=&lt;raw
/// token&gt; that hashes to the SignatureParticipant.TokenHash.
/// </summary>
[AllowAnonymous]
public class SignController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IBlobStorageService _blobs;
    private readonly IIpHashService _iphash;
    private readonly ISignaturePdfService _sig;
    private readonly IUserNotifier _in;

    public SignController(NimShareDbContext db, IPasswordHasher hasher,
        IBlobStorageService blobs, IIpHashService iphash,
        ISignaturePdfService sig, IUserNotifier inApp)
    {
        _db = db; _hasher = hasher; _blobs = blobs; _iphash = iphash; _sig = sig; _in = inApp;
    }

    /// <summary>Participant landing — HTML page with the signature UI.</summary>
    [HttpGet("/sign/{pid:guid}")]
    public async Task<IActionResult> Landing(Guid pid, string t, CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null) return View("Invalid");
        if (req.Status == SignatureRequestStatus.Cancelled) return View("Invalid");

        if (p.Status == SignatureParticipantStatus.Pending && p.ViewedAt is null)
        {
            // Record that they opened the URL, but keep Status=Pending until
            // they *explicitly* click Sign/Acknowledge — otherwise Outlook /
            // Slack link previews would silently satisfy a viewer's ack.
            p.ViewedAt = DateTimeOffset.UtcNow;
            p.IpHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
            p.UserAgent = Request.Headers.UserAgent;
            _db.SignatureAudits.Add(new SignatureAudit
            {
                RequestId = req.Id, ParticipantId = pid, Kind = SignatureAuditKind.Viewed,
                IpHash = p.IpHash, UserAgent = p.UserAgent,
            });
            await _db.SaveChangesAsync(ct);
        }
        return View("Sign", new SignViewModel(req, p, t));
    }

    /// <summary>Stream the source PDF inline via a short-lived SAS.</summary>
    [HttpGet("/sign/{pid:guid}/preview")]
    public async Task<IActionResult> Preview(Guid pid, string t, CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null || req.SourceFile is null) return NotFound();
        var sas = _blobs.CreateInlineSas(req.SourceFile.BlobPath, "application/pdf");
        return Redirect(sas.ToString());
    }

    public record SignSubmitReq(string SignatureImagePngBase64, string TypedName);

    /// <summary>Sign submission — persists signature image(s), marks participant Signed,
    /// finalises the request if this was the last one.</summary>
    [HttpPost("/sign/{pid:guid}/submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid pid, string t, string? typedName,
        string? signatureData, CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null) return View("Invalid");
        if (p.Status == SignatureParticipantStatus.Signed) return RedirectToAction(nameof(Landing), new { pid, t });
        if (p.Role != SignatureParticipantRole.Signer)
        {
            // Viewer path: just acknowledge. Save first so MaybeFinalize can
            // see the new Viewed status.
            p.Status = SignatureParticipantStatus.Viewed;
            await _db.SaveChangesAsync(ct);
            await MaybeFinalizeAsync(req, ct);
            await _db.SaveChangesAsync(ct);
            return View("Done", new SignDoneViewModel(req, p, false));
        }

        // Persist signature PNG to blob if provided; else fall back to typed name.
        var fields = await _db.SignatureFields.Where(f => f.RequestId == req.Id && f.ParticipantId == pid).ToListAsync(ct);
        string? sigPath = null;
        if (!string.IsNullOrEmpty(signatureData) && signatureData.StartsWith("data:image/"))
        {
            var comma = signatureData.IndexOf(',');
            var b64 = comma >= 0 ? signatureData[(comma + 1)..] : signatureData;
            try
            {
                var bytes = Convert.FromBase64String(b64);
                var path = $"signatures/{req.Id:N}/{pid:N}.png";
                using var ms = new MemoryStream(bytes);
                var http = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("nimshare-signature");
                var ticket = _blobs.CreateUploadTicket(path);
                using var content = new StreamContent(ms);
                content.Headers.Add("x-ms-blob-type", "BlockBlob");
                content.Headers.Add("x-ms-blob-content-type", "image/png");
                var uploadResp = await http.PutAsync(ticket.UploadUrl, content, ct);
                if (!uploadResp.IsSuccessStatusCode)
                {
                    // Refuse to mark Signed against a missing/failed image — that would
                    // "silently forge" the participant as done with no signature stored.
                    return View("Invalid");
                }
                sigPath = path;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return View("Invalid");
            }
        }
        else if (string.IsNullOrEmpty(typedName))
        {
            // Neither a drawn signature nor a typed name — refuse.
            return View("Invalid");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var f in fields)
        {
            if (f.Type == SignatureFieldType.Signature)
            {
                f.SignatureImagePath = sigPath;
                f.Value = typedName;
            }
            else if (f.Type == SignatureFieldType.Date)
            {
                f.Value = now.ToString("yyyy-MM-dd");
            }
            f.FilledAt = now;
        }
        p.Status = SignatureParticipantStatus.Signed;
        p.SignedAt = now;
        p.IpHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = pid, Kind = SignatureAuditKind.Signed,
            IpHash = p.IpHash, UserAgent = p.UserAgent,
        });

        // Persist the Signed status BEFORE MaybeFinalizeAsync runs — that
        // method re-queries the DB and needs to see p.Status = Signed,
        // otherwise the last signer never triggers finalisation.
        await _db.SaveChangesAsync(ct);

        // Sequential chain: trigger the next participant in Order.
        if (req.DeliveryOrder == SignatureDeliveryOrder.Sequential)
            await NotifyNextAsync(req, p, ct);

        await MaybeFinalizeAsync(req, ct);
        await _db.SaveChangesAsync(ct);
        return View("Done", new SignDoneViewModel(req, p, true));
    }

    [HttpPost("/sign/{pid:guid}/decline")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decline(Guid pid, string t, string? reason, CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null) return View("Invalid");
        // Only signers can decline the workflow. Viewers can just close the tab.
        if (p.Role != SignatureParticipantRole.Signer) return View("Invalid");
        // Terminal states are terminal — a signed participant cannot un-sign
        // by declining, and a completed / cancelled request can't be re-opened.
        if (p.Status == SignatureParticipantStatus.Signed) return View("Invalid");
        if (req.Status == SignatureRequestStatus.Completed
            || req.Status == SignatureRequestStatus.Cancelled
            || req.Status == SignatureRequestStatus.Declined)
            return View("Invalid");
        p.Status = SignatureParticipantStatus.Declined;
        p.DeclinedReason = reason;
        req.Status = SignatureRequestStatus.Declined;
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = pid, Kind = SignatureAuditKind.Declined,
            Note = reason,
        });
        await _db.SaveChangesAsync(ct);
        HttpContext.RequestServices.GetService<IWebhookDispatcher>()?
            .QueueEvent(req.InitiatorUserId, WebhookEvent.SignatureRequestDeclined,
                new { requestId = req.Id, title = req.Title, declinedBy = p.Email, reason });
        // Ping the initiator.
        await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
            $"{p.Name} hat die Signatur abgelehnt: {req.Title}", body: reason,
            href: "/signatures", ct: ct);
        return View("Done", new SignDoneViewModel(req, p, false));
    }

    private async Task NotifyNextAsync(SignatureRequest req, SignatureParticipant justSigned, CancellationToken ct)
    {
        var next = req.Participants.OrderBy(p => p.Order)
            .FirstOrDefault(p => p.Order > justSigned.Order
                && p.Status == SignatureParticipantStatus.Pending
                && !string.IsNullOrEmpty(p.DeclinedReason)
                && p.DeclinedReason.StartsWith("TOKEN:"));
        if (next is null) return;
        var stash = HttpContext.RequestServices
            .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector("NimShare.Signature.Chain.v1");
        string raw;
        try { raw = stash.Unprotect(next.DeclinedReason!["TOKEN:".Length..]); }
        catch { return; }
        var url = $"{Request.Scheme}://{Request.Host}/sign/{next.Id}?t={raw}";
        var initiator = req.Initiator?.DisplayName ?? "NimShare";
        var subject = $"NimShare — {(next.Role == SignatureParticipantRole.Signer ? "Bitte unterschreiben" : "Bitte lesen")}: {req.Title}";
        var body = $"Hallo {next.Name},\n\ndu bist als Nächste:r an der Reihe. Bitte {(next.Role == SignatureParticipantRole.Signer ? "unterschreibe" : "bestätige")} das Dokument '{req.Title}'.\n\n{url}\n\n— NimShare";
        try
        {
            var notif = HttpContext.RequestServices.GetService(typeof(INotificationService)) as INotificationService;
            if (notif is not null) await notif.SendShareLinkAsync(next.Email, initiator, subject, body, ct);
        }
        catch { }
        next.DeclinedReason = null; // clear stashed token now the email is out
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = next.Id,
            Kind = SignatureAuditKind.Invited, Note = "sequential-turn",
        });
    }

    // ── helpers ──
    private async Task<(SignatureRequest?, SignatureParticipant?)> ResolveAsync(Guid pid, string t, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(t)) return (null, null);
        var p = await _db.SignatureParticipants
            .Include(x => x.Request).ThenInclude(r => r!.SourceFile)
            .Include(x => x.Request).ThenInclude(r => r!.Initiator)
            .Include(x => x.Request).ThenInclude(r => r!.Fields)
            .Include(x => x.Request).ThenInclude(r => r!.Participants)
            .SingleOrDefaultAsync(x => x.Id == pid, ct);
        if (p is null || string.IsNullOrEmpty(p.TokenHash)) return (null, null);
        if (!_hasher.Verify(t, p.TokenHash)) return (null, null);
        return (p.Request, p);
    }

    private async Task MaybeFinalizeAsync(SignatureRequest req, CancellationToken ct)
    {
        var all = await _db.SignatureParticipants.Where(p => p.RequestId == req.Id).ToListAsync(ct);
        var allDone = all.All(x =>
            (x.Role == SignatureParticipantRole.Signer && x.Status == SignatureParticipantStatus.Signed)
            || (x.Role == SignatureParticipantRole.Viewer && (x.Status == SignatureParticipantStatus.Viewed || x.Status == SignatureParticipantStatus.Signed)));
        if (!allDone) return;
        // Merge signatures + audit page into a new PDF, store as a StorageFile
        // owned by the initiator, hang it off req.FinalFileId.
        if (req.SourceFile is null) return;
        using var srcMs = new MemoryStream();
        await _blobs.DownloadToAsync(req.SourceFile.BlobPath, srcMs, ct);
        var srcBytes = srcMs.ToArray();

        // Load signature images by participant.
        var sigImages = new Dictionary<Guid, byte[]>();
        var fields = await _db.SignatureFields.Where(f => f.RequestId == req.Id && f.SignatureImagePath != null).ToListAsync(ct);
        foreach (var f in fields.DistinctBy(f => f.ParticipantId))
        {
            try
            {
                using var im = new MemoryStream();
                await _blobs.DownloadToAsync(f.SignatureImagePath!, im, ct);
                sigImages[f.ParticipantId] = im.ToArray();
            }
            catch { /* skip */ }
        }
        // Rehydrate initiator + participants for the audit page.
        req.Participants = all;
        req.Initiator ??= await _db.Users.FindAsync(new object[] { req.InitiatorUserId }, ct);
        var finalBytes = await _sig.RenderFinalAsync(req, srcBytes, sigImages, ct);

        var finalName = System.IO.Path.GetFileNameWithoutExtension(req.SourceFile.Name) + " (signiert).pdf";
        var finalPath = $"users/{req.InitiatorUserId:N}/signatures/{req.Id:N}.pdf";
        using var upMs = new MemoryStream(finalBytes);
        using var http = new HttpClient();
        var ticket = _blobs.CreateUploadTicket(finalPath);
        using var content = new StreamContent(upMs);
        content.Headers.Add("x-ms-blob-type", "BlockBlob");
        content.Headers.Add("x-ms-blob-content-type", "application/pdf");
        await http.PutAsync(ticket.UploadUrl, content, ct);

        var final = new StorageFile
        {
            OwnerId = req.InitiatorUserId,
            Scope = FileScope.Personal,
            FolderId = req.SourceFile.FolderId,
            Name = finalName,
            SizeBytes = finalBytes.LongLength,
            ContentType = "application/pdf",
            BlobPath = finalPath,
            ContainerName = req.SourceFile.ContainerName,
            Status = StorageFileStatus.Ready,
            ReadyAt = DateTimeOffset.UtcNow,
        };
        _db.Files.Add(final);
        req.FinalFileId = final.Id;
        req.Status = SignatureRequestStatus.Completed;
        req.CompletedAt = DateTimeOffset.UtcNow;
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, Kind = SignatureAuditKind.Finalized,
        });
        var quoted = "„" + req.Title + "“";
        await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
            $"Signatur-Anforderung {quoted} abgeschlossen", body: "Das signierte PDF liegt in deiner Ablage.",
            href: "/signatures", fileId: final.Id, ct: ct);
        HttpContext.RequestServices.GetService<IWebhookDispatcher>()?
            .QueueEvent(req.InitiatorUserId, WebhookEvent.SignatureRequestCompleted,
                new { requestId = req.Id, title = req.Title, finalFileId = final.Id });
    }
}

public record SignViewModel(SignatureRequest Request, SignatureParticipant Me, string Token);
public record SignDoneViewModel(SignatureRequest Request, SignatureParticipant Me, bool Signed);
