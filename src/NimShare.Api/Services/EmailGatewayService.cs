using MailKit.Net.Smtp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Loads the persisted <see cref="EmailGatewaySettings"/> singleton and sends
/// email through the currently-selected provider (SMTP or Resend). Wraps the
/// old <see cref="INotificationService"/> so callers that already accept it
/// keep working. Secrets are unwrapped via ASP.NET Core DataProtection.
/// </summary>
public interface IEmailGatewayService
{
    Task<EmailGatewaySettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(EmailGatewaySettings settings, string? plainSmtpPassword, string? plainResendApiKey, Guid updatedByUserId, CancellationToken ct = default);
    Task<(bool Ok, string Message)> SendTestAsync(string toEmail, CancellationToken ct = default);
    Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default);
    // v1.10.39: Overload mit optionalen Anhängen — der SignatureFinalizer
    // braucht das um dem Initiator das fertig signierte PDF direkt per
    // Mail mitzuschicken. attachments = null hält den bisherigen Pfad
    // unverändert (Text-Only Mail).
    Task SendAsync(string toEmail, string subject, string bodyText, IReadOnlyList<EmailAttachment>? attachments, CancellationToken ct = default);
}

// Small value type — kein Stream, damit der Aufrufer über die Lifetime
// seines byte[] entscheidet und wir nicht ungewollt Dispose'n.
public sealed record EmailAttachment(string Filename, string ContentType, byte[] Content);

public class EmailGatewayService : IEmailGatewayService
{
    private const string ProtectorPurpose = "NimShare.EmailGateway.v1";

    private readonly NimShareDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<EmailGatewayService> _log;

    public EmailGatewayService(NimShareDbContext db, IDataProtectionProvider dpp, IHttpClientFactory http, ILogger<EmailGatewayService> log)
    {
        _db = db;
        _protector = dpp.CreateProtector(ProtectorPurpose);
        _http = http;
        _log = log;
    }

    public async Task<EmailGatewaySettings> LoadAsync(CancellationToken ct = default)
    {
        var s = await _db.EmailGateways.FirstOrDefaultAsync(x => x.Id == EmailGatewaySettings.SingletonId, ct);
        if (s is null)
        {
            s = new EmailGatewaySettings();
            _db.EmailGateways.Add(s);
            await _db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task SaveAsync(EmailGatewaySettings incoming, string? plainSmtpPassword, string? plainResendApiKey, Guid updatedByUserId, CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        s.Provider = incoming.Provider;
        s.FromAddress = incoming.FromAddress;
        s.FromName = incoming.FromName;
        s.SmtpHost = incoming.SmtpHost;
        s.SmtpPort = incoming.SmtpPort;
        s.SmtpUseStartTls = incoming.SmtpUseStartTls;
        s.SmtpUsername = incoming.SmtpUsername;
        if (!string.IsNullOrEmpty(plainSmtpPassword))
            s.SmtpPasswordEncrypted = _protector.Protect(plainSmtpPassword);
        if (!string.IsNullOrEmpty(plainResendApiKey))
            s.ResendApiKeyEncrypted = _protector.Protect(plainResendApiKey);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedByUserId = updatedByUserId;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(bool Ok, string Message)> SendTestAsync(string toEmail, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(toEmail, "NimShare test email", "This is a test email from NimShare — your gateway is configured correctly.", ct);
            return (true, "Test email queued.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Test email failed");
            return (false, ex.Message);
        }
    }

    public Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default)
        => SendAsync(toEmail, subject, bodyText, attachments: null, ct);

    public async Task SendAsync(string toEmail, string subject, string bodyText, IReadOnlyList<EmailAttachment>? attachments, CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        switch (s.Provider)
        {
            case EmailProvider.Disabled:
                _log.LogInformation("Email gateway disabled — would send to {To}: {Subject}", toEmail, subject);
                return;
            case EmailProvider.Smtp:
                await SendSmtpAsync(s, toEmail, subject, bodyText, attachments, ct);
                return;
            case EmailProvider.Resend:
                await SendResendAsync(s, toEmail, subject, bodyText, attachments, ct);
                return;
        }
    }

    private async Task SendSmtpAsync(EmailGatewaySettings s, string to, string subject, string body, IReadOnlyList<EmailAttachment>? attachments, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s.FromName, s.FromAddress));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        if (attachments is null || attachments.Count == 0)
        {
            msg.Body = new TextPart("plain") { Text = body };
        }
        else
        {
            var multipart = new Multipart("mixed");
            multipart.Add(new TextPart("plain") { Text = body });
            foreach (var a in attachments)
            {
                var slash = a.ContentType.IndexOf('/');
                var mediaType = slash > 0 ? a.ContentType[..slash] : "application";
                var mediaSub = slash > 0 ? a.ContentType[(slash + 1)..] : "octet-stream";
                var part = new MimePart(mediaType, mediaSub)
                {
                    Content = new MimeContent(new MemoryStream(a.Content)),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = a.Filename },
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName = a.Filename,
                };
                multipart.Add(part);
            }
            msg.Body = multipart;
        }
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort,
            s.SmtpUseStartTls ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        if (!string.IsNullOrEmpty(s.SmtpUsername) && !string.IsNullOrEmpty(s.SmtpPasswordEncrypted))
            await smtp.AuthenticateAsync(s.SmtpUsername, _protector.Unprotect(s.SmtpPasswordEncrypted), ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    private async Task SendResendAsync(EmailGatewaySettings s, string to, string subject, string body, IReadOnlyList<EmailAttachment>? attachments, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.ResendApiKeyEncrypted))
            throw new InvalidOperationException("Resend API key is not configured.");
        var apiKey = _protector.Unprotect(s.ResendApiKeyEncrypted);
        var http = _http.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        // Resend accepts attachments as an array of { filename, content } with
        // content base64-encoded (docs: https://resend.com/docs/api-reference/emails/send-email).
        object payload;
        if (attachments is null || attachments.Count == 0)
        {
            payload = new
            {
                from = $"{s.FromName} <{s.FromAddress}>",
                to = new[] { to },
                subject,
                text = body,
            };
        }
        else
        {
            payload = new
            {
                from = $"{s.FromName} <{s.FromAddress}>",
                to = new[] { to },
                subject,
                text = body,
                attachments = attachments.Select(a => new
                {
                    filename = a.Filename,
                    content = Convert.ToBase64String(a.Content),
                    content_type = a.ContentType,
                }).ToArray(),
            };
        }
        var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("https://api.resend.com/emails", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Resend returned {(int)resp.StatusCode}: {text}");
        }
    }
}

/// <summary>
/// Thin adapter that plugs the new gateway service into the old INotification
/// interface so existing callers (link download / upload notifications) route
/// through the DB-configured provider automatically.
/// </summary>
public class GatewayBackedNotificationService : INotificationService
{
    private readonly IEmailGatewayService _gateway;
    private readonly ILogger<GatewayBackedNotificationService> _log;

    public GatewayBackedNotificationService(IEmailGatewayService gateway, ILogger<GatewayBackedNotificationService> log)
    {
        _gateway = gateway;
        _log = log;
    }

    public async Task NotifyDownloadAsync(ShareLink link, string ipHash, CancellationToken ct = default)
    {
        if (!link.NotifyOnAccess) return;
        var subject = $"[NimShare] Your link '{link.Slug}' was downloaded";
        var body = $"Hello {link.Owner.DisplayName},\n\nYour share link '{link.Slug}' was just downloaded.\n\n- Total downloads: {link.DownloadCount + 1}\n- Recipient (IP hash, salted): {(ipHash.Length >= 12 ? ipHash[..12] : ipHash)}...\n- Time (UTC): {DateTimeOffset.UtcNow:u}\n\n— NimShare";
        try { await _gateway.SendAsync(link.Owner.Email, subject, body, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "notify download failed"); }
    }

    public async Task NotifyUploadAsync(UploadRequestLink request, string filename, CancellationToken ct = default)
    {
        if (!request.NotifyOnUpload) return;
        var subject = $"[NimShare] Someone uploaded '{filename}' to your inbox link";
        var body = $"Hello {request.Owner.DisplayName},\n\nA file has just been uploaded via your upload-request link '{request.Slug}':\n\n- Filename: {filename}\n- Target folder: {request.TargetFolder}\n- Time (UTC): {DateTimeOffset.UtcNow:u}\n\n— NimShare";
        try { await _gateway.SendAsync(request.Owner.Email, subject, body, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "notify upload failed"); }
    }

    public async Task SendShareLinkAsync(string toEmail, string fromName, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            await _gateway.SendAsync(toEmail, subject, body, ct);
            EmailDeliveryLog.Record(toEmail, subject, ok: true, error: null, kind: "share-link");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "share-link email failed");
            EmailDeliveryLog.Record(toEmail, subject, ok: false, error: ex.Message, kind: "share-link");
            throw; // Let callers see the failure — signatures/uploads decide
                   // whether to swallow or surface it.
        }
    }
}
