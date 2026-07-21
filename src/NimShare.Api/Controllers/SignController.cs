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
    private readonly IGeoIpService _geo;

    public SignController(NimShareDbContext db, IPasswordHasher hasher,
        IBlobStorageService blobs, IIpHashService iphash,
        ISignaturePdfService sig, IUserNotifier inApp, IGeoIpService geo)
    {
        _db = db; _hasher = hasher; _blobs = blobs; _iphash = iphash; _sig = sig; _in = inApp; _geo = geo;
    }

    /// <summary>
    /// v1.10.78: Klartext-IP wird IMMER gespeichert (zusätzlich zum SHA-Hash).
    /// DSGVO-Rechtfertigung: Art. 6(1)(f) berechtigtes Interesse bei
    /// elektronischen Signaturen — Standard bei allen eIDAS-Anbietern
    /// (DocuSign, Adobe Sign etc.). Marcus's Entscheidung nach v1.10.77.
    /// </summary>
    private string? RealIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    // v1.10.42 — extrahiert die vom Client mitgeschickte Timezone (IANA-
    // Id) aus dem Form-Feld "clientTz". Vorsichtige Validation:
    // nur [A-Za-z0-9_/+-] und max 60 Zeichen, damit kein Junk in die
    // DB landet.
    private static string? ReadTimezone(HttpRequest req)
    {
        if (!req.HasFormContentType) return null;
        var tz = req.Form["clientTz"].ToString();
        if (string.IsNullOrWhiteSpace(tz) || tz.Length > 60) return null;
        foreach (var c in tz)
        {
            if (!(char.IsLetterOrDigit(c) || c == '/' || c == '_' || c == '-' || c == '+')) return null;
        }
        return tz;
    }

    // v1.10.42 — sammelt Country/City/Device/Timezone für Persistenz in
    // SignatureAudit. Ein Aufruf pro Event; die Werte gehen in
    // ausgehende Audit-Zeilen (Signed / Declined / Reassigned).
    private async Task<(string? Country, string? City, string? Device, string? Timezone)> ForensicsAsync(CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var device = DeviceTypeParser.Classify(ua);
        var tz = ReadTimezone(HttpContext.Request);
        var (country, city) = await _geo.LookupAsync(ip, ct);
        return (country, city, device, tz);
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
            p.IpAddress = RealIp();
            p.UserAgent = Request.Headers.UserAgent;
            // v1.10.42 — beim Landing haben wir noch keinen Timezone-
            // Header (Landing ist GET, clientTz kommt nur mit Form-POST).
            // Trotzdem Country + City + Device einloggen, das ist der
            // "wann und woher hat er den Link geöffnet"-Datenpunkt.
            var vf = await ForensicsAsync(ct);
            _db.SignatureAudits.Add(new SignatureAudit
            {
                RequestId = req.Id, ParticipantId = pid, Kind = SignatureAuditKind.Viewed,
                IpHash = p.IpHash, IpAddress = p.IpAddress, UserAgent = p.UserAgent,
                Country = vf.Country, City = vf.City,
                DeviceType = vf.Device, Timezone = vf.Timezone,
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
        ViewData["Theme"] = await ResolveSignThemeAsync(req, ct);
        return View("Sign", new SignViewModel(req, p, t));
    }

    /// <summary>Load the same LandingTemplate an initiator uses for download
    /// shares (v1.10.6): personal template first, global as fallback. Applied
    /// to the /sign/{pid} landing so signature invites carry the same brand
    /// as the initiator's download links — logo, avatar, primary colour,
    /// header/footer copy.</summary>
    private async Task<SignLandingTheme> ResolveSignThemeAsync(SignatureRequest req, CancellationToken ct)
    {
        NimShare.Core.Entities.LandingTemplate? tpl = null;
        try
        {
            tpl = await _db.LandingTemplates.FirstOrDefaultAsync(x =>
                x.Scope == NimShare.Core.Entities.LandingTemplateScope.UserPersonal
                && x.OwnerUserId == req.InitiatorUserId, ct);
            tpl ??= await _db.LandingTemplates.FirstOrDefaultAsync(x =>
                x.Scope == NimShare.Core.Entities.LandingTemplateScope.Global, ct);
        }
        catch { /* table might be missing on very old DBs — fall through to defaults */ }
        string? avatarUrl = null;
        if (req.Initiator is { ShowAvatarOnLandings: true } u)
        {
            if (!string.IsNullOrEmpty(u.AvatarBlobPath)) avatarUrl = $"/avatars/{u.Id:N}";
            else if (!string.IsNullOrEmpty(u.AvatarUrl)) avatarUrl = u.AvatarUrl;
        }
        return new SignLandingTheme(
            tpl?.Title, tpl?.Subtitle, tpl?.BodyMarkdown, tpl?.FooterText,
            tpl?.PrimaryColor, tpl?.LogoUrl, tpl?.HeroUrl,
            avatarUrl, req.Initiator?.DisplayName ?? "");
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

    /// <summary>
    /// v1.10.36 — externer Empfänger hat kein NimShare-Account, will aber
    /// mit einem selbst mitgebrachten PFX/P12 signieren. Diese Route nimmt
    /// die Datei + Passwort entgegen, parst sie NUR im Speicher, extrahiert
    /// die Metadaten für den Stempel (CN, Thumbprint, Aussteller) und
    /// verwirft die Bytes + das Passwort SOFORT. Keine Persistenz.
    ///
    /// Marcus-Vorgabe: "wir wollen kein fremdes speichern". Deshalb kein
    /// Cache, keine Session, keine DB — der Client muss bei Submit die
    /// Datei nochmal senden (Doppel-Upload ist die einzige belegbare Form
    /// von Nicht-Speichern).
    /// </summary>
    [HttpPost("/sign/{pid:guid}/validate-cert")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateOwnCert(Guid pid, string t, IFormFile? pfx, string? password, CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null) return NotFound();
        if (p.Role != SignatureParticipantRole.Signer)
            return BadRequest(new { detail = "Only signers can attach a certificate" });
        if (pfx is null || pfx.Length == 0) return BadRequest(new { detail = "No file uploaded" });
        // 2 MB cap — a legitimate PFX is 4–20 KB. Anything above is either
        // wrong content or an OOM attempt.
        if (pfx.Length > 2 * 1024 * 1024) return BadRequest(new { detail = "PFX too large" });

        byte[] bytes;
        using (var ms = new MemoryStream((int)pfx.Length))
        {
            await pfx.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                bytes,
                password ?? "",
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            var subjectCn = ExtractCn(cert.Subject) ?? cert.Subject;
            var issuerCn = ExtractCn(cert.Issuer) ?? cert.Issuer;
            var isSelfIssued = string.Equals(cert.Subject, cert.Issuer, StringComparison.OrdinalIgnoreCase);
            return Ok(new
            {
                subjectCommonName = subjectCn,
                issuerCommonName = issuerCn,
                thumbprint = cert.Thumbprint,
                isSelfIssued,
                notBefore = cert.NotBefore.ToUniversalTime(),
                notAfter = cert.NotAfter.ToUniversalTime(),
            });
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Wrong password or malformed PFX. Do NOT leak which — both cases
            // look the same to a real user ("prüfe die Datei und dein Passwort").
            return BadRequest(new { detail = "cert.invalid_or_wrong_password" });
        }
        finally
        {
            // Wipe the PFX bytes from managed memory. Not a security guarantee
            // (GC may have shuffled copies) but it eliminates the obvious
            // straight-through-heap trace during the response tail.
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    // v1.10.36: extract "CN=..." from an X.500 DN. cert.Subject is a comma-
    // joined DN and the CN comes with commas inside quoted values sometimes;
    // this is deliberately naive because we only need it for a display stamp.
    private static string? ExtractCn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return p.Substring(3).Trim();
        }
        return null;
    }

    /// <summary>Sign submission — persists signature image(s), marks participant Signed,
    /// finalises the request if this was the last one.</summary>
    [HttpPost("/sign/{pid:guid}/submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid pid, string t, string? typedName,
        string? signatureData, string? signMode, CancellationToken ct)
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
            ViewData["Theme"] = await ResolveSignThemeAsync(req, ct);
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
        // v1.10.18: honour user-entered values for Text / Date / Checkbox
        // fields. The form posts them as `fieldValue_{fieldGuid}` — pre-1.10.18
        // these values were ignored and non-signature fields stayed empty in
        // the final PDF. Signer had no way to fill them in the UI either
        // (also fixed in v1.10.18 Sign.cshtml).
        var form = Request.HasFormContentType ? Request.Form : null;
        foreach (var f in fields)
        {
            if (f.Type == SignatureFieldType.Signature)
            {
                f.SignatureImagePath = sigPath;
                f.Value = typedName;
            }
            else if (f.Type == SignatureFieldType.Text)
            {
                var v = form?[$"fieldValue_{f.Id}"].ToString();
                if (!string.IsNullOrWhiteSpace(v)) f.Value = v.Length > 500 ? v[..500] : v;
            }
            else if (f.Type == SignatureFieldType.Date)
            {
                var v = form?[$"fieldValue_{f.Id}"].ToString();
                // If the signer set a date, use it; otherwise auto-stamp today.
                f.Value = !string.IsNullOrWhiteSpace(v) ? v : now.ToString("yyyy-MM-dd");
            }
            else if (f.Type == SignatureFieldType.Checkbox)
            {
                var v = form?[$"fieldValue_{f.Id}"].ToString();
                f.Value = !string.IsNullOrWhiteSpace(v) && v != "false" ? "☑" : "☐";
            }
            f.FilledAt = now;
        }
        p.Status = SignatureParticipantStatus.Signed;
        p.SignedAt = now;
        p.IpHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
        p.IpAddress = RealIp();
        var forensics = await ForensicsAsync(ct);
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = pid, Kind = SignatureAuditKind.Signed,
            IpHash = p.IpHash, IpAddress = p.IpAddress, UserAgent = p.UserAgent,
            // v1.10.15: record which UI-mode produced the signature image so
            // the audit trail can distinguish hand-drawn from cert-stamped.
            Note = string.Equals(signMode, "cert", StringComparison.OrdinalIgnoreCase)
                ? "signed via certificate stamp"
                : (string.Equals(signMode, "byo", StringComparison.OrdinalIgnoreCase)
                    ? "signed via own certificate"
                    : null),
            // v1.10.42 — forensische Zusatzdaten für Beweiskraft.
            Country = forensics.Country, City = forensics.City,
            DeviceType = forensics.Device, Timezone = forensics.Timezone,
        });

        // Persist the Signed status BEFORE any long-running work.
        await _db.SaveChangesAsync(ct);

        // v1.10.76: Initiator-Notification fürs Sign-Event. Marcus's Report
        // "Benachrichtigungen leer außer Ablehnung von gestern".
        // v1.10.78: SignatureParticipant hat kein UserId-Feld (Participants
        // sind Email-basiert). Selbst-Signieren via Email-Vergleich zum
        // Initiator ausblenden.
        try
        {
            var initiatorEmail = req.Initiator?.Email;
            if (req.InitiatorUserId != Guid.Empty &&
                !string.Equals(initiatorEmail, p.Email, StringComparison.OrdinalIgnoreCase))
            {
                var signed = req.Participants.Count(x => x.Role == SignatureParticipantRole.Signer
                                                       && x.Status == SignatureParticipantStatus.Signed);
                var total = req.Participants.Count(x => x.Role == SignatureParticipantRole.Signer);
                var title = $"✍ {p.Name} hat signiert";
                var body = $"\"{req.Title}\" — {signed}/{total} Unterschrift(en) fertig.";
                await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
                    title, body: body, href: $"/signatures/{req.Id}", ct: ct);
            }
        }
        catch { /* Notification-Fehler dürfen den Sign-Flow nicht kippen */ }

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

        ViewData["Theme"] = await ResolveSignThemeAsync(req, ct);
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
        var declF = await ForensicsAsync(ct);
        _db.SignatureAudits.Add(new SignatureAudit
        {
            RequestId = req.Id, ParticipantId = pid, Kind = SignatureAuditKind.Declined,
            Note = reason,
            IpHash = _iphash.Hash(HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
            IpAddress = RealIp(),
            UserAgent = HttpContext.Request.Headers.UserAgent.ToString(),
            Country = declF.Country, City = declF.City,
            DeviceType = declF.Device, Timezone = declF.Timezone,
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
            // Route through IEmailGatewayService directly (same path as
            // signature-invite emails) so the DB-configured provider is used
            // and any failure is LOGGED — not silently caught (a pre-v1.10.5
            // bug that made "no decline email" impossible to debug).
            var initiatorEmail = req.Initiator?.Email;
            if (!string.IsNullOrWhiteSpace(initiatorEmail))
            {
                var gateway = HttpContext.RequestServices.GetService(typeof(IEmailGatewayService)) as IEmailGatewayService;
                var logger = HttpContext.RequestServices.GetService(typeof(ILogger<SignController>)) as ILogger<SignController>;
                if (gateway is not null)
                {
                    var subject = declTitle;
                    var mailBody = declLocalizer["sig.declined.mail.body",
                        req.Initiator?.DisplayName ?? "",
                        p.Name, p.Email, req.Title,
                        string.IsNullOrWhiteSpace(reason) ? declLocalizer["sig.declined.no_reason_given"].Value : reason].Value;
                    try
                    {
                        await gateway.SendAsync(initiatorEmail, subject, mailBody, ct);
                        EmailDeliveryLog.Record(initiatorEmail, subject, ok: true, error: null, kind: "sig-decline");
                        logger?.LogInformation("Decline email sent to initiator {Email} for request {ReqId}",
                            initiatorEmail, req.Id);
                    }
                    catch (Exception ex)
                    {
                        EmailDeliveryLog.Record(initiatorEmail, subject, ok: false, error: ex.Message, kind: "sig-decline");
                        logger?.LogWarning(ex,
                            "Decline email FAILED for request {ReqId} to {Email} — in-app notification stays as source of truth",
                            req.Id, initiatorEmail);
                    }
                }
                else
                {
                    (HttpContext.RequestServices.GetService(typeof(ILogger<SignController>)) as ILogger<SignController>)
                        ?.LogWarning("Decline email skipped: IEmailGatewayService is not registered.");
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = declPrev; }
        ViewData["Theme"] = await ResolveSignThemeAsync(req, ct);
        return View("Done", new SignDoneViewModel(req, p, false));
    }

    /// <summary>AI-drafted summary of the source PDF in the requested language,
    /// so a signer / viewer can quickly grasp what they're being asked to sign
    /// without reading the whole document. Token-gated (same as landing /
    /// preview / submit); refuses if the AI gateway is off or the summary
    /// feature disabled.</summary>
    [HttpGet("/sign/{pid:guid}/summary")]
    public async Task<IActionResult> Summary(Guid pid, string t, string? lang,
        [FromServices] IAiGatewayService ai,
        CancellationToken ct)
    {
        var (req, p) = await ResolveAsync(pid, t, ct);
        if (req is null || p is null) return Problem(statusCode: 404);
        if (req.SourceFile is null) return Problem(statusCode: 404);
        var settings = await ai.LoadAsync(ct);
        if (settings.Provider == NimShare.Core.Entities.AiProvider.Disabled)
            return Problem(statusCode: 503, title: "AI-Zusammenfassungen sind serverseitig deaktiviert.");
        if (!settings.EnableAutoSummary)
            return Problem(statusCode: 503, title: "AI-Zusammenfassung ist im AI-Gateway nicht aktiviert.");

        var text = await ai.ExtractTextAsync(req.SourceFile.BlobPath, req.SourceFile.ContentType, _blobs, ct);
        if (string.IsNullOrWhiteSpace(text))
            return Problem(statusCode: 422, title: "Text-Extraktion ergab nichts.",
                detail: "Das PDF könnte gescannt / bildbasiert sein. OCR ist auf dem Roadmap.");

        // Language: browser hint → participant email domain → German fallback.
        var language = string.IsNullOrWhiteSpace(lang) ? "de" : lang.Trim().Split('-')[0];
        var provider = await ai.CreateProviderAsync(ct);
        var summary = await provider.SummarizeAsync(text, language, ct);
        if (string.IsNullOrWhiteSpace(summary))
        {
            // v1.10.32: Konkrete Ursache aus dem Provider abfragen statt
            // generic "Modell wechseln". Deckt alle Fehlerpfade ab (Safety-
            // Block, MAX_TOKENS, HTTP 4xx, NullAiProvider).
            var openErr = (provider as OpenAiProvider)?.LastError;
            var geminiErr = (provider as GeminiProvider)?.LastError;
            var anthErr = (provider as AnthropicProvider)?.LastError;
            var nullErr = provider is NullAiProvider ? ai.LastProviderCreationFailure : null;
            var detail = openErr ?? geminiErr ?? anthErr ?? nullErr
                ?? "Modell wechseln (im AI-Gateway) oder Prompt/Modell-Verfügbarkeit prüfen.";
            return Problem(statusCode: 502, title: "AI hat keine Zusammenfassung geliefert.",
                detail: detail);
        }
        return Ok(new { text = summary, language, doc = req.SourceFile.Name });
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
            IpAddress = RealIp(),
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

        ViewData["Theme"] = await ResolveSignThemeAsync(req, ct);
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

/// <summary>Landing-Template-Snapshot für die Signatur-Landing (Sign.cshtml
/// und Done.cshtml). Wird über ViewData["Theme"] durchgereicht — dieselbe
/// Struktur wie beim Download-Landing (LandingTheme in ShareController),
/// erweitert um Avatar + Absender-Name für die Sign-spezifische Kopfzeile.
/// Alle Felder nullable — leerer Theme fällt auf den NimShare-Look zurück.</summary>
public record SignLandingTheme(
    string? Title, string? Subtitle, string? BodyMarkdown, string? FooterText,
    string? PrimaryColor, string? LogoUrl, string? HeroUrl,
    string? OwnerAvatarUrl, string OwnerName);
