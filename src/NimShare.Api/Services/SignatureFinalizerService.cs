using System.Globalization;
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
    Task TryFinalizeAsync(Guid requestId, CancellationToken ct = default);
}

public class SignatureFinalizerService : ISignatureFinalizerService
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;
    private readonly ISignaturePdfService _sig;
    private readonly IUserNotifier _in;
    private readonly IHttpClientFactory _http;
    private readonly IStringLocalizer<SharedResources> _l;
    private readonly ILogger<SignatureFinalizerService> _log;

    public SignatureFinalizerService(NimShareDbContext db, IBlobStorageService blobs,
        ISignaturePdfService sig, IUserNotifier inApp, IHttpClientFactory http,
        IStringLocalizer<SharedResources> localizer, ILogger<SignatureFinalizerService> log)
    {
        _db = db; _blobs = blobs; _sig = sig; _in = inApp; _http = http; _l = localizer; _log = log;
    }

    public async Task TryFinalizeAsync(Guid requestId, CancellationToken ct = default)
    {
        var req = await _db.SignatureRequests
            .Include(r => r.SourceFile)
            .Include(r => r.Participants)
            .Include(r => r.Initiator)
            .SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (req is null) return;
        if (req.Status is SignatureRequestStatus.Completed
            or SignatureRequestStatus.Cancelled
            or SignatureRequestStatus.Declined) return;
        if (req.SourceFile is null) return;

        var allDone = req.Participants.All(x =>
            (x.Role == SignatureParticipantRole.Signer && x.Status == SignatureParticipantStatus.Signed)
            || (x.Role == SignatureParticipantRole.Viewer
                && (x.Status == SignatureParticipantStatus.Viewed
                    || x.Status == SignatureParticipantStatus.Signed)));
        if (!allDone) return;

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
                _log.LogWarning("Signature final blob upload {StatusCode} for {ReqId}", uploadResp.StatusCode, req.Id);
                return;
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
            }
            finally { CultureInfo.CurrentUICulture = prev; }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Signature finalisation failed for {ReqId}", req.Id);
        }
    }
}
