using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;

namespace NimShare.Api.Services;

/// <summary>
/// v1.11.50 — Marcus's Wunsch: Links/Upload-Requests sollen nicht endlos
/// liegen bleiben, wenn niemand sie von Hand löscht. Löscht stündlich alle
/// ShareLinks/UploadRequests, deren ExpiresAt in der Vergangenheit liegt und
/// die nicht als IsPermanent markiert sind. Wiederkehrende Upload-Requests
/// (RecurringDaysOfWeek gesetzt) werden verschont — die managt
/// <see cref="RecurringUploadReopenerService"/> selbst, ein hartes Löschen
/// hier würde mit deren Reopen-Zyklus kollidieren.
/// </summary>
public class ExpiredLinkCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ExpiredLinkCleanupService> _log;

    public ExpiredLinkCleanupService(IServiceScopeFactory scopes,
        ILogger<ExpiredLinkCleanupService> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "expired link cleanup tick failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (TaskCanceledException) { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var now = DateTimeOffset.UtcNow;

        var expiredShareLinks = await db.ShareLinks
            .Where(l => !l.IsPermanent && l.ExpiresAt != null && l.ExpiresAt <= now)
            .ToListAsync(ct);
        if (expiredShareLinks.Count > 0)
        {
            var zipCache = scope.ServiceProvider.GetService<IAlbumZipCache>();
            db.ShareLinks.RemoveRange(expiredShareLinks);
            await db.SaveChangesAsync(ct);
            if (zipCache is not null)
            {
                foreach (var l in expiredShareLinks)
                    _ = zipCache.DeleteAsync(l.Id, CancellationToken.None);
            }
            _log.LogInformation("Deleted {n} expired share links", expiredShareLinks.Count);
        }

        var expiredUploadRequests = await db.UploadRequests
            .Where(l => !l.IsPermanent && l.ExpiresAt != null && l.ExpiresAt <= now
                     && l.RecurringDaysOfWeek == null)
            .ToListAsync(ct);
        if (expiredUploadRequests.Count > 0)
        {
            db.UploadRequests.RemoveRange(expiredUploadRequests);
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Deleted {n} expired upload requests", expiredUploadRequests.Count);
        }

        // v1.12 (Review F3): verwaiste KI-Branding-Vorlagen (Scope=Link) aufräumen.
        // Jede "KI-Vorschau" legt eine LandingTemplate + Logo-Blob an — auch wenn
        // der Nutzer danach keinen Link erstellt. Ohne Cleanup wüchse das
        // unbegrenzt. Löschen: Scope=Link, älter als 24 h, von KEINEM ShareLink
        // referenziert (linked-and-in-use bleiben unangetastet).
        var brandCutoff = now.AddHours(-24);
        var orphanBrandTemplates = await db.LandingTemplates
            .Where(t => t.Scope == NimShare.Core.Entities.LandingTemplateScope.Link
                        && t.UpdatedAt < brandCutoff
                        && !db.ShareLinks.Any(s => s.LandingTemplateId == t.Id))
            .ToListAsync(ct);
        if (orphanBrandTemplates.Count > 0)
        {
            var blobs = scope.ServiceProvider.GetService<IBlobStorageService>();
            foreach (var t in orphanBrandTemplates)
            {
                if (blobs is not null && !string.IsNullOrEmpty(t.LogoBlobPath))
                    try { await blobs.DeleteAsync(t.LogoBlobPath, CancellationToken.None); } catch { /* orphan bytes, egal */ }
            }
            db.LandingTemplates.RemoveRange(orphanBrandTemplates);
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Deleted {n} orphaned link-branding templates", orphanBrandTemplates.Count);
        }
    }
}
