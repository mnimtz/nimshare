using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Turns a SignatureRequest into its final signed PDF once every participant
/// is done. Called out-of-band from the sign flow so the signer doesn't wait
/// on PDF-merge + blob-upload (which is 10s+ on Azure for multi-page docs).
/// </summary>
public interface ISignatureFinalizerService
{
    Task<FinalizeOutcome> TryFinalizeAsync(Guid requestId, CancellationToken ct = default);
}

/// <summary>
/// v1.10.80: Ergebnis eines Finalize-Versuchs. Der bisherige Return
/// void hat alle Fehler im ILogger versenkt — Marcus's ForceFinalize-
/// Trace konnte nur „keine Exception" sagen, ohne den echten Grund zu
/// nennen. Jetzt kann der Aufrufer die Ursache direkt anzeigen.
/// </summary>
public enum FinalizeOutcomeKind
{
    Completed,
    Skipped,             // Bereits Completed/Cancelled/Declined
    WaitingParticipants, // Noch Signer/Viewer offen
    RequestNotFound,
    SourceMissing,
    BlobUploadFailed,
    RenderFailed,        // PDF-Merge/Sig-Rendering hat geworfen
    UnexpectedError,     // catch-all für alles andere
}

public sealed record FinalizeOutcome(
    FinalizeOutcomeKind Kind,
    string? Detail = null,
    string? Exception = null);

public class SignatureFinalizerService : ISignatureFinalizerService
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;
    private readonly ISignaturePdfService _sig;
    private readonly IUserNotifier _in;
    private readonly IHttpClientFactory _http;
    private readonly IStringLocalizer<SharedResources> _l;
    private readonly ILogger<SignatureFinalizerService> _log;
    private readonly IPdfSignatureService _pdfSign;
    private readonly IDataProtectionProvider _dp;
    private readonly IEmailGatewayService _email;

    public SignatureFinalizerService(NimShareDbContext db, IBlobStorageService blobs,
        ISignaturePdfService sig, IUserNotifier inApp, IHttpClientFactory http,
        IStringLocalizer<SharedResources> localizer, ILogger<SignatureFinalizerService> log,
        IPdfSignatureService pdfSign, IDataProtectionProvider dp, IEmailGatewayService email)
    {
        _db = db; _blobs = blobs; _sig = sig; _in = inApp; _http = http; _l = localizer; _log = log;
        _pdfSign = pdfSign; _dp = dp; _email = email;
    }

    public async Task<FinalizeOutcome> TryFinalizeAsync(Guid requestId, CancellationToken ct = default)
    {
        // v1.10.75: EXTENSIVES Logging jedes early-return-Pfads. Marcus's
        // Report v1.10.74: "1/1 signiert, bleibt trotzdem auf Läuft" —
        // Finalizer wurde entweder gar nicht getriggert ODER er stieg
        // stumm aus. Ohne konkrete Log-Zeilen war die Root-Cause nicht
        // identifizierbar. v1.10.80: die Info geht zusätzlich über
        // FinalizeOutcome an den Aufrufer zurück, damit ForceFinalize
        // sie im Response-Trace zeigen kann.
        _log.LogInformation("Finalizer-Start für Request {Id}", requestId);
        var req = await _db.SignatureRequests
            .Include(r => r.SourceFile)
            .Include(r => r.Participants)
            .Include(r => r.Initiator)
            .SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (req is null)
        {
            _log.LogWarning("Finalizer-Abort {Id}: Request not found", requestId);
            return new FinalizeOutcome(FinalizeOutcomeKind.RequestNotFound);
        }
        if (req.Status is SignatureRequestStatus.Completed
            or SignatureRequestStatus.Cancelled
            or SignatureRequestStatus.Declined)
        {
            _log.LogInformation("Finalizer-Skip {Id}: bereits {Status}", requestId, req.Status);
            return new FinalizeOutcome(FinalizeOutcomeKind.Skipped, $"already {req.Status}");
        }
        if (req.SourceFile is null)
        {
            _log.LogWarning("Finalizer-Abort {Id}: SourceFile ist null", requestId);
            return new FinalizeOutcome(FinalizeOutcomeKind.SourceMissing);
        }

        var allDone = req.Participants.All(x =>
            (x.Role == SignatureParticipantRole.Signer && x.Status == SignatureParticipantStatus.Signed)
            || (x.Role == SignatureParticipantRole.Viewer
                && (x.Status == SignatureParticipantStatus.Viewed
                    || x.Status == SignatureParticipantStatus.Signed)));
        if (!allDone)
        {
            var openList = string.Join(", ", req.Participants
                .Where(x => !(
                    (x.Role == SignatureParticipantRole.Signer && x.Status == SignatureParticipantStatus.Signed)
                    || (x.Role == SignatureParticipantRole.Viewer
                        && (x.Status == SignatureParticipantStatus.Viewed
                            || x.Status == SignatureParticipantStatus.Signed))))
                .Select(x => $"{x.Name}({x.Role}={x.Status})"));
            _log.LogInformation("Finalizer-Skip {Id}: offene Beteiligte: {Open}", requestId, openList);
            return new FinalizeOutcome(FinalizeOutcomeKind.WaitingParticipants, openList);
        }

        try
        {
            using var srcMs = new MemoryStream();
            await _blobs.DownloadToAsync(req.SourceFile.BlobPath, srcMs, ct);
            var srcBytes = srcMs.ToArray();

            var sigImages = new Dictionary<Guid, byte[]>();
            var fields = await _db.SignatureFields
                .Where(f => f.RequestId == req.Id && f.SignatureImagePath != null)
                .ToListAsync(ct);
            foreach (var f in fields.DistinctBy(f => f.ParticipantId))
            {
                try
                {
                    using var im = new MemoryStream();
                    await _blobs.DownloadToAsync(f.SignatureImagePath!, im, ct);
                    sigImages[f.ParticipantId] = im.ToArray();
                }
                catch { /* skip missing sig */ }
            }

            var finalBytes = await _sig.RenderFinalAsync(req, srcBytes, sigImages, ct);

            // v1.10.16 — embed a real PAdES-B cryptographic signature using
            // the initiator's default certificate (if any). Every byte of the
            // visually-flattened PDF is covered by SHA-256 hash inside a
            // PKCS#7 SignedData that Adobe Reader validates natively (green
            // padlock). Silent fallback if no cert: ship the unsigned PDF as
            // before.
            // v1.10.81: Root-Cause aus Marcus's v1.10.80-Diagnose — SqlServer-
            // Provider kann DateTimeOffset.UtcNow nicht in einem Where in SQL
            // übersetzen, wirft InvalidOperationException. SQLite hat's still
            // per Client-Evaluation gemacht, deshalb lokal ok. Fix: Wert vorher
            // in eine lokale Variable, dann geht der Provider ihn als Parameter.
            // Zusätzlich: das ganze Cert-Lookup gehört in ein defensives try —
            // ein failer Cert-Query darf NIE den Finalize blockieren, weil das
            // PAdES-Siegel per Design optional ist (fällt auf unsigned zurück).
            SigningCertificate? initiatorCert = null;
            try
            {
                var now = DateTimeOffset.UtcNow;
                initiatorCert = await _db.SigningCertificates
                    .Where(c => c.OwnerUserId == req.InitiatorUserId && c.NotAfter > now)
                    .OrderByDescending(c => c.IsDefault)
                    .ThenByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cert-Lookup failed for {ReqId} — proceeding without PAdES signature", req.Id);
            }
            string? cryptoSigInfo = null;
            if (initiatorCert is not null)
            {
                try
                {
                    var protector = _dp.CreateProtector("NimShare.SigningCertificate.v1");
                    var signed = _pdfSign.SignPdf(finalBytes, initiatorCert, protector,
                        signerName: req.Initiator?.DisplayName ?? "NimShare user",
                        reasonText: "Signature workflow — all participants signed",
                        locationText: "NimShare",
                        out var failure);
                    if (failure is null && signed.Length > finalBytes.Length)
                    {
                        finalBytes = signed;
                        initiatorCert.LastUsedAt = DateTimeOffset.UtcNow;
                        initiatorCert.UseCount++;
                        cryptoSigInfo = $"PKCS#7 attached (SHA-256) — cert {initiatorCert.SubjectCommonName} / {initiatorCert.Thumbprint[..16]}…";
                        _log.LogInformation("PAdES signature embedded for {ReqId} using cert {CN}",
                            req.Id, initiatorCert.SubjectCommonName);
                    }
                    else if (failure is not null)
                    {
                        _log.LogWarning("PAdES signing skipped for {ReqId}: {Failure}", req.Id, failure);
                        cryptoSigInfo = "unsigned — " + failure;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "PAdES signing threw for {ReqId}", req.Id);
                }
            }
            var finalName = System.IO.Path.GetFileNameWithoutExtension(req.SourceFile.Name) + " (signed).pdf";
            var finalPath = $"users/{req.InitiatorUserId:N}/signatures/{req.Id:N}.pdf";
            using var upMs = new MemoryStream(finalBytes);
            var http = _http.CreateClient("nimshare-signature");
            http.Timeout = TimeSpan.FromSeconds(30);
            var ticket = _blobs.CreateUploadTicket(finalPath);
            using var content = new StreamContent(upMs);
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.Add("x-ms-blob-content-type", "application/pdf");
            var uploadResp = await http.PutAsync(ticket.UploadUrl, content, ct);
            if (!uploadResp.IsSuccessStatusCode)
            {
                var bodySnippet = string.Empty;
                try { bodySnippet = (await uploadResp.Content.ReadAsStringAsync(ct)).Trim(); } catch { }
                if (bodySnippet.Length > 400) bodySnippet = bodySnippet[..400] + "…";
                _log.LogWarning("Signature final blob upload {StatusCode} for {ReqId} — body: {Body}",
                    uploadResp.StatusCode, req.Id, bodySnippet);
                return new FinalizeOutcome(
                    FinalizeOutcomeKind.BlobUploadFailed,
                    $"{(int)uploadResp.StatusCode} {uploadResp.ReasonPhrase}: {bodySnippet}");
            }

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
                Note = cryptoSigInfo,
            });

            var prev = CultureInfo.CurrentUICulture;
            try
            {
                var culture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture)
                    ? "en" : req.Initiator!.PreferredCulture;
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            }
            catch { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
            try
            {
                await _in.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
                    _l["sig.completed.notif.title", req.Title].Value,
                    body: _l["sig.completed.notif.body"].Value,
                    href: "/signatures", fileId: final.Id, ct: ct);

                // v1.10.39: Initiator bekommt das fertige PDF direkt per
                // Mail. Locale ist bereits auf req.Initiator.PreferredCulture
                // umgestellt (siehe oben), also greifen die _l[]-Zugriffe
                // in der Sprache des Empfängers.
                if (req.Initiator is { Email: var toEmail } && !string.IsNullOrWhiteSpace(toEmail))
                {
                    var subject = _l["sig.completed.email.subject", req.Title].Value;
                    var body = _l["sig.completed.email.body",
                        req.Initiator.DisplayName ?? "",
                        req.Title,
                        req.Participants.Count].Value;
                    var attachment = new EmailAttachment(finalName, "application/pdf", finalBytes);
                    try
                    {
                        await _email.SendAsync(toEmail, subject, body, new[] { attachment }, ct);
                    }
                    catch (Exception mailEx)
                    {
                        // Mail-Fehler dürfen den Finalize nicht zurückrollen —
                        // die In-App-Notification ist schon raus, das Dokument
                        // liegt im Personal-Bereich. Nur loggen.
                        _log.LogWarning(mailEx, "Sending completion email to {Email} for {ReqId} failed", toEmail, req.Id);
                    }
                }
            }
            finally { CultureInfo.CurrentUICulture = prev; }
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Finalizer-OK {Id}: FinalFileId={FinalId}, cryptoSig={Crypto}",
                req.Id, req.FinalFileId, cryptoSigInfo ?? "(none)");
            return new FinalizeOutcome(FinalizeOutcomeKind.Completed, cryptoSigInfo);
        }
        catch (Exception ex)
        {
            // v1.10.75: LogError statt LogWarning — Finalize-Fehler bedeuten
            // dass ein Signatur-Vorgang auf "läuft" hängen bleibt obwohl er
            // fertig ist. Marcus's Bug in v1.10.74. Muss im Log-Stream
            // sofort auffallen, nicht in der Rauschzone. v1.10.80: das
            // Outcome-Objekt liefert die Details zusätzlich an die Route
            // zurück, damit die ForceFinalize-Response die echte Ursache
            // zeigt statt "keine Exception".
            _log.LogError(ex, "Finalisierung fehlgeschlagen für {ReqId} — Vorgang bleibt auf {Status}. Der Startup-Rescue oder /remind + \"Abschluss erzwingen\" retriggern den Finalizer.", req.Id, req.Status);
            var kind = ex is HttpRequestException or IOException or TaskCanceledException
                ? FinalizeOutcomeKind.BlobUploadFailed
                : FinalizeOutcomeKind.RenderFailed;
            return new FinalizeOutcome(kind,
                $"{ex.GetType().Name}: {ex.Message}",
                ex.ToString());
        }
    }
}
