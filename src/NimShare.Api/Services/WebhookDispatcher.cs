using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>Best-effort webhook dispatch. Fire-and-forget from the caller;
/// each subscribing webhook gets a POST with HMAC-SHA256 signature header.</summary>
public interface IWebhookDispatcher
{
    void QueueEvent(Guid ownerUserId, WebhookEvent kind, object payload);
}

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WebhookDispatcher> _log;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpFactory;

    public WebhookDispatcher(IServiceScopeFactory scopes, ILogger<WebhookDispatcher> log,
        IDataProtectionProvider dp, IHttpClientFactory httpFactory)
    {
        _scopes = scopes; _log = log; _httpFactory = httpFactory;
        _protector = dp.CreateProtector("NimShare.Webhook.Secret.v1");
    }

    public void QueueEvent(Guid ownerUserId, WebhookEvent kind, object payload)
    {
        _ = Task.Run(async () =>
        {
            try { await RunAsync(ownerUserId, kind, payload); }
            catch (Exception ex) { _log.LogWarning(ex, "webhook dispatch failed"); }
        });
    }

    private async Task RunAsync(Guid ownerUserId, WebhookEvent kind, object payload)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var name = kind.ToString();
        var subs = await db.Webhooks
            .Where(w => w.OwnerUserId == ownerUserId && w.IsActive)
            .ToListAsync();
        if (subs.Count == 0) return;
        var body = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(),
            @event = name,
            createdAt = DateTimeOffset.UtcNow,
            data = payload,
        });
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var http = _httpFactory.CreateClient("nimshare-webhook");
        http.Timeout = TimeSpan.FromSeconds(10);
        foreach (var w in subs)
        {
            if (!string.IsNullOrEmpty(w.Events) && !w.Events.Split(',').Contains(name)) continue;
            try
            {
                var secret = _protector.Unprotect(w.SecretEncrypted);
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var sig = Convert.ToHexString(hmac.ComputeHash(bodyBytes)).ToLowerInvariant();
                using var req = new HttpRequestMessage(HttpMethod.Post, w.Url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-NimShare-Event", name);
                req.Headers.Add("X-NimShare-Signature", "sha256=" + sig);
                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    w.LastDeliveredAt = DateTimeOffset.UtcNow;
                    w.FailureCount = 0;
                }
                else
                {
                    w.FailureCount++;
                    // Auto-disable after 20 consecutive failures.
                    if (w.FailureCount >= 20) w.IsActive = false;
                }
            }
            catch { w.FailureCount++; if (w.FailureCount >= 20) w.IsActive = false; }
        }
        await db.SaveChangesAsync();
    }
}
