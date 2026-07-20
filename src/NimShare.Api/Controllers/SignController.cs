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
        // Load fields ONLY for this participant — the sign view shows visible
        // "sign here" boxes so the recipient sees where their signature will
        // be stamped.
        var myFields = await _db.SignatureFields
            .Where(f => f.RequestId == req.Id && f.ParticipantId == pid)
            .ToListAsync(ct);
        ViewData["MyFields"] = myFields;

        // Full audit trail for the audit sidebar on the landing page.
        // Anonymized: we show the participant Name/Email that already lives
        // in the request (visible to the participant anyway), plus verb +
        // timestamp. IP hashes stay out of the UI.
        var participants = req.Participants.ToDictionary(x => x.Id);
        var audits = await _db.SignatureAudits
            .Where(a => a.RequestId == req.Id)
            .OrderBy(a => a.At)
            .ToListAsync(ct);
        ViewData["Audits"] = audits;
        ViewData["ParticipantsById"] = participants;
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
        // Reassigned participants are flipped to Declined and the fields move to
        // the delegate — an old bookmark or leaked URL for the original signer
        // must NOT be able to POST /submit and forge the signature onto the
        // (now-empty) participant row. Same for anyone who hit Decline.
        if (p.Status == SignatureParticipantStatus.Declined) return View("Invalid");
        if (req.Status == SignatureRequestStatus.Cancelled
            || req.Status == SignatureRequestStatus.Completed
            || req.Status == SignatureRequestStatus.Declined)
            return View("Invalid");
        if (p.Role != SignatureParticipantRole.Signer)
        {
            // Viewer path: just acknowledge. Save first, then trigger the
            // background finalizer (same rationale as the signer path — the
            // PDF merge shouldn't block the viewer's response).
            p.Status = SignatureParticipantStatus.Viewed;
            await _db.SaveChangesAsync(ct);
            var scopesV = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var reqIdV = req.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopesV.CreateScope();
                    var finalizer = scope.ServiceProvider.GetRequiredService<ISignatureFinalizerService>();
                    await finalizer.TryFinalizeAsync(reqIdV);
                }
                catch { }
            });
            return View("Done", new SignDoneViewModel(req, p, false));
        }

        // Persist signature PNG to blob if provided; else fall back to typed name.
        var fields = await _db.SignatureFields.Where(f => f.RequestId == req.Id && f.ParticipantId == pid).ToListAsync(ct);
        string? sigPath = null;
        // Hard cap on the base64 payload — an anti-forgery-protected POST can
        // still be abused by a legitimate signer to OOM the process with a
        // multi-hundred-MB base64 blob. 2.5 MB base64 → ~1.9 MB PNG, enough
        // for the biggest reasonable signature pad drawing.
        const int MaxSignatureBase64Bytes = 2 * 1024 * 1024 + 512 * 1024;
        if (signatureData is not null && signatureData.Length > MaxSignatureBase64Bytes)
            return View("Invalid");
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

        // Persist the Signed status BEFORE any long-running work.
        await _db.SaveChangesAsync(ct);

        // Sequential chain: trigger the next participant in Order.
        if (req.DeliveryOrder == SignatureDeliveryOrder.Sequential)
        {
            await NotifyNextAsync(req, p, ct);
            await _db.SaveChangesAsync(ct);
        }

        // Finalisation (PDF merge + upload) is expensive on Azure — 10s+ is
        // typical for multi-page contracts. Do it out-of-band so the signer
        // gets the "Done" page immediately instead of watching "Wird
        // gesendet…" for 30 seconds. The BackgroundFinalizerService picks up
        // Sent→Completed transitions the same way MaybeFinalizeAsync did.
        var scopes = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var reqId = req.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var finalizer = scope.ServiceProvider.GetRequiredService<ISignatureFinalizerService>();
                await finalizer.TryFinalizeAsync(reqId);
            }
            catch (Exception ex)
            {
                var logger = HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("SignController");
                logger?.LogWarning(ex, "background finalize failed for {ReqId}", reqId);
            }
        });

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
        // Ping the initiator in their own language.
        var declLocalizer = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<NimShare.Api.SharedResources>>();
        var declPrev = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            var culture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture) ? "en" : req.Initiator!.PreferredCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        }
        catch { System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en"); }
        try
        {
            var declTitle = declLocalizer["sig.declined.notif.title", p.Name, req.Title].Value;
            var declBody = string.IsNullOrWhiteSpace(reason)
                ? declLocalizer["sig.declined.notif.body_noreason"].Value
                : declLocalizer["sig.declined.notif.body", reason].Value;
            await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
                declTitle, body: declBody, href: $"/signatures/{req.Id}", ct: ct);

            // ALSO send an email — the in-app notification alone is easy to
            // miss and the initiator needs to know the workflow is stuck.
            var initiatorEmail = req.Initiator?.Email;
            if (!string.IsNullOrWhiteSpace(initiatorEmail))
            {
                var notif = HttpContext.RequestServices.GetService(typeof(INotificationService)) as INotificationService;
                if (notif is not null)
                {
                    var subject = declTitle;
                    var body = declLocalizer["sig.declined.mail.body",
                        req.Initiator?.DisplayName ?? "",
                        p.Name, p.Email, req.Title,
                        string.IsNullOrWhiteSpace(reason) ? declLocalizer["sig.declined.no_reason_given"].Value : reason].Value;
                    try { await notif.SendShareLinkAsync(initiatorEmail, "NimShare", subject, body, ct); }
                    catch { /* best-effort; in-app notification is the source of truth */ }
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = declPrev; }
        return View("Done", new SignDoneViewModel(req, p, false));
    }

    /// <summary>Reassign / delegate — the recipient says "I'm not the right
    /// person, please forward to X" (DocuSign-style). We mark the current
    /// participant as Declined with a reassigned-to marker, spawn a fresh
    /// participant with the same role + fields + order, and send an invite to
    /// the new address. The initiator gets a notification so they know the
    /// signing chain took a detour.</summary>
    [HttpPost("/sign/{pid:guid}/reassign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reassign(Guid pid, string t, string toEmail, string toName, string? reason,
        [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dpp,
        [FromServices] INotificationService notif,
        CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null) return View("Invalid");
        if (p.Role != SignatureParticipantRole.Signer) return View("Invalid");
        if (p.Status != SignatureParticipantStatus.Pending && p.Status != SignatureParticipantStatus.Viewed)
            return View("Invalid");
        if (req.Status == SignatureRequestStatus.Cancelled || req.Status == SignatureRequestStatus.Completed
            || req.Status == SignatureRequestStatus.Declined)
            return View("Invalid");
        if (string.IsNullOrWhiteSpace(toEmail) || string.IsNullOrWhiteSpace(toName)) return View("Invalid");
        toEmail = toEmail.Trim();
        toName = toName.Trim();
        // Bare-minimum email sanity — a full validator would need to accept
        // RFC-5321 corner cases we don't care about here.
        if (!toEmail.Contains('@') || toEmail.Length > 250) return View("Invalid");

        // Stash a token for the delegate. Same shape as the initial invite —
        // raw token in the URL, hash on the participant row.
        var raw = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hash = _hasher.Hash(raw);

        var delegateP = new SignatureParticipant
        {
            RequestId = req.Id,
            Email = toEmail,
            Name = toName,
            Role = SignatureParticipantRole.Signer,
            Order = p.Order,
            TokenHash = hash,
            Status = SignatureParticipantStatus.Pending,
        };
        _db.SignatureParticipants.Add(delegateP);

        // Move the current participant's fields onto the delegate — this is
        // the whole point of reassignment: they still need to be filled, just
        // by someone else.
        var fields = await _db.SignatureFields
            .Where(f => f.RequestId == req.Id && f.ParticipantId == p.Id).ToListAsync(ct);
        foreach (var f in fields) f.ParticipantId = delegateP.Id;

        p.Status = SignatureParticipantStatus.Declined;
        p.DeclinedReason = $"reassigned to {toEmail}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}");

        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = p.Id, Kind = SignatureAuditKind.Declined,
            Note = $"reassigned:{toEmail}",
            IpHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
            UserAgent = Request.Headers.UserAgent,
        });
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = delegateP.Id,
            Kind = SignatureAuditKind.Invited, Note = $"reassigned-from:{p.Email}",
        });

        // Explicit transaction: the whole reassignment (old participant flipped,
        // new one added, fields re-parented, two audit rows) MUST commit as one
        // atom. Sqlite/SqlServer default is auto-commit per SaveChanges, but a
        // background finalizer for the same request could race and see a
        // half-applied state (delegate exists, fields not yet moved). This
        // guarantees the reader either sees the pre-reassign world or the full
        // post-reassign one.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var url = $"{Request.Scheme}://{Request.Host}/sign/{delegateP.Id}?t={raw}";
        var initiator = req.Initiator?.DisplayName ?? "NimShare";

        // Recipient email must be in THEIR language (best-effort default: the
        // initiator's culture, since we don't know the delegate's yet).
        var localizer = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<NimShare.Api.SharedResources>>();
        var prevCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            var culture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture) ? "en" : req.Initiator!.PreferredCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        }
        catch { System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en"); }
        try
        {
            var subject = localizer["sig.reassign.subject", initiator, req.Title].Value;
            var body = localizer["sig.reassign.body", toName, p.Name, p.Email, req.Title, url].Value;
            try { await notif.SendShareLinkAsync(toEmail, initiator, subject, body, ct); }
            catch { /* delivery failure isn't fatal — audit shows the reassign */ }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = prevCulture; }

        // Ping the initiator so they know the chain took a detour — in their
        // language.
        prevCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            var culture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture) ? "en" : req.Initiator!.PreferredCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        }
        catch { System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en"); }
        try
        {
            var title = localizer["sig.reassign.notif.title", p.Name, toName].Value;
            var body = localizer["sig.reassign.notif.body", req.Title, toEmail].Value;
            try
            {
                await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
                    title, body: body, href: $"/signatures/{req.Id}", ct: ct);
            }
            catch { }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = prevCulture; }

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
        var localizer = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<NimShare.Api.SharedResources>>();
        var prev = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            var culture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture) ? "en" : req.Initiator!.PreferredCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        }
        catch { System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en"); }
        try
        {
            var isSigner = next.Role == SignatureParticipantRole.Signer;
            var action = localizer[isSigner ? "sig.next.action_sign" : "sig.next.action_review"].Value;
            var subject = localizer[isSigner ? "sig.next.subject_signer" : "sig.next.subject_viewer", req.Title].Value;
            var body = localizer["sig.next.body", next.Name, action, req.Title, url].Value;
            try
            {
                var notif = HttpContext.RequestServices.GetService(typeof(INotificationService)) as INotificationService;
                if (notif is not null) await notif.SendShareLinkAsync(next.Email, initiator, subject, body, ct);
            }
            catch { }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = prev; }
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
        // IHttpClientFactory reuses sockets; `new HttpClient()` here leaked a
        // fresh socket per finalisation and eventually starved the ephemeral
        // port range on busy hosts.
        var http = HttpContext.RequestServices
            .GetRequiredService<IHttpClientFactory>().CreateClient("nimshare-signature");
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
        var compLocalizer = HttpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<NimShare.Api.SharedResources>>();
        var compPrev = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            var culture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture) ? "en" : req.Initiator!.PreferredCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        }
        catch { System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en"); }
        try
        {
            await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
                compLocalizer["sig.completed.notif.title", req.Title].Value,
                body: compLocalizer["sig.completed.notif.body"].Value,
                href: "/signatures", fileId: final.Id, ct: ct);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = compPrev; }
        HttpContext.RequestServices.GetService<IWebhookDispatcher>()?
            .QueueEvent(req.InitiatorUserId, WebhookEvent.SignatureRequestCompleted,
                new { requestId = req.Id, title = req.Title, finalFileId = final.Id });
    }
}

public record SignViewModel(SignatureRequest Request, SignatureParticipant Me, string Token);
public record SignDoneViewModel(SignatureRequest Request, SignatureParticipant Me, bool Signed);
