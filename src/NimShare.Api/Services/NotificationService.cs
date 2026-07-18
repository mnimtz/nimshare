using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;
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
}

public class SmtpNotificationService : INotificationService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpNotificationService> _logger;

    public SmtpNotificationService(IOptions<SmtpOptions> options, ILogger<SmtpNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task NotifyDownloadAsync(ShareLink link, string ipHash, CancellationToken ct = default)
    {
        if (!_options.Enabled || !link.NotifyOnAccess) return Task.CompletedTask;
        var subject = $"[NimShare] Your link '{link.Slug}' was downloaded";
        var body = $"""
                    Hello {link.Owner.DisplayName},

                    Your share link {link.Slug} was just downloaded.

                    - Total downloads: {link.DownloadCount + 1}
                    - Recipient (IP hash, salted): {ipHash[..12]}…
                    - Time (UTC): {DateTimeOffset.UtcNow:u}

                    — NimShare
                    """;
        return SendAsync(link.Owner.Email, subject, body, ct);
    }

    public Task NotifyUploadAsync(UploadRequestLink request, string filename, CancellationToken ct = default)
    {
        if (!_options.Enabled || !request.NotifyOnUpload) return Task.CompletedTask;
        var subject = $"[NimShare] Someone uploaded '{filename}' to your inbox link";
        var body = $"""
                    Hello {request.Owner.DisplayName},

                    A file has just been uploaded via your upload-request link '{request.Slug}':

                    - Filename: {filename}
                    - Target folder: {request.TargetFolder}
                    - Time (UTC): {DateTimeOffset.UtcNow:u}

                    — NimShare
                    """;
        return SendAsync(request.Owner.Email, subject, body, ct);
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
