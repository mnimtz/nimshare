using System.Globalization;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MimeKit;
using NimShare.Api;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

public class SmtpOptions
{
    public const string SectionName = "Smtp";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "no-reply@nimshare.local";
    public string FromName { get; set; } = "NimShare";
}

public interface INotificationService
{
    Task NotifyDownloadAsync(ShareLink link, string ipHash, CancellationToken ct = default);
    Task NotifyUploadAsync(UploadRequestLink request, string filename, CancellationToken ct = default);

    /// <summary>Sends a plain-text share link email from the current user to an arbitrary recipient.</summary>
    Task SendShareLinkAsync(string toEmail, string fromName, string subject, string body, CancellationToken ct = default);
}

public class SmtpNotificationService : INotificationService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpNotificationService> _logger;
    private readonly IStringLocalizerFactory _localizerFactory;

    public SmtpNotificationService(
        IOptions<SmtpOptions> options,
        ILogger<SmtpNotificationService> logger,
        IStringLocalizerFactory localizerFactory)
    {
        _options = options.Value;
        _logger = logger;
        _localizerFactory = localizerFactory;
    }

    public Task NotifyDownloadAsync(ShareLink link, string ipHash, CancellationToken ct = default)
    {
        if (!_options.Enabled || !link.NotifyOnAccess) return Task.CompletedTask;
        using var _ = WithCulture(link.Owner.PreferredCulture);
        var t = _localizerFactory.Create(typeof(SharedResources));
        var subject = t["email.download.subject", link.Slug].Value;
        var body = t["email.download.body",
            link.Owner.DisplayName,
            link.Slug,
            link.DownloadCount + 1,
            ipHash.Length >= 12 ? ipHash[..12] : ipHash,
            DateTimeOffset.UtcNow.ToString("u")].Value;
        return SendAsync(link.Owner.Email, subject, body, ct);
    }

    public Task NotifyUploadAsync(UploadRequestLink request, string filename, CancellationToken ct = default)
    {
        if (!_options.Enabled || !request.NotifyOnUpload) return Task.CompletedTask;
        using var _ = WithCulture(request.Owner.PreferredCulture);
        var t = _localizerFactory.Create(typeof(SharedResources));
        var subject = t["email.upload.subject", filename].Value;
        var body = t["email.upload.body",
            request.Owner.DisplayName,
            request.Slug,
            filename,
            request.TargetFolder,
            DateTimeOffset.UtcNow.ToString("u")].Value;
        return SendAsync(request.Owner.Email, subject, body, ct);
    }

    private static CultureScope WithCulture(string cultureName)
    {
        try
        {
            var c = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(cultureName) ? "en" : cultureName);
            return new CultureScope(c);
        }
        catch { return new CultureScope(CultureInfo.GetCultureInfo("en")); }
    }

    private readonly struct CultureScope : IDisposable
    {
        private readonly CultureInfo _prev;
        public CultureScope(CultureInfo c) { _prev = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = c; }
        public void Dispose() { CultureInfo.CurrentUICulture = _prev; }
    }

    public Task SendShareLinkAsync(string toEmail, string fromName, string subject, string body, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SMTP disabled — would send share link to {To}: {Subject}", toEmail, subject);
            return Task.CompletedTask;
        }
        return SendAsync(toEmail, subject, body, ct);
    }

    private async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
            msg.To.Add(MailboxAddress.Parse(to));
            msg.Subject = subject;
            msg.Body = new TextPart("plain") { Text = body };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_options.Host, _options.Port,
                _options.UseStartTls ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
            if (!string.IsNullOrEmpty(_options.Username))
                await smtp.AuthenticateAsync(_options.Username, _options.Password, ct);
            await smtp.SendAsync(msg, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification email to {To}", to);
        }
    }
}
