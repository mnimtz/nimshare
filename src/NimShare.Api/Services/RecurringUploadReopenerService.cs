using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;

namespace NimShare.Api.Services;

/// <summary>
/// Reopens recurring upload-request links on their scheduled weekdays.
/// A "reopen" resets ExpiresAt to now+RecurringWindowDays and zeros
/// UploadCount so the recipient can drop the next round of files.
/// </summary>
public class RecurringUploadReopenerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RecurringUploadReopenerService> _log;

    public RecurringUploadReopenerService(IServiceScopeFactory scopes,
        ILogger<RecurringUploadReopenerService> log)
    {
        _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tick once an hour — cheap enough, and a 1-hour drift is acceptable
        // for a recurring drop-box that lives in owner-local calendar days.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "recurring upload reopen tick failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (TaskCanceledException) { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var now = DateTimeOffset.UtcNow;
        var todayIso = ((int)now.DayOfWeek == 0 ? 7 : (int)now.DayOfWeek).ToString();

        var due = await db.UploadRequests
            .Where(l => l.RecurringDaysOfWeek != null && !l.IsRevoked)
            .Where(l => l.ExpiresAt == null || l.ExpiresAt < now)
            .ToListAsync(ct);
        int reopened = 0;
        foreach (var l in due)
        {
            var days = (l.RecurringDaysOfWeek ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (!Array.Exists(days, d => d == todayIso)) continue;
            // Don't reopen twice on the same day.
            if (l.LastUploadAt.HasValue && l.LastUploadAt.Value.Date == now.Date && l.UploadCount > 0) continue;

            var window = Math.Max(1, l.RecurringWindowDays);
            l.ExpiresAt = now.AddDays(window);
            l.UploadCount = 0;
            reopened++;
        }
        if (reopened > 0)
        {
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Reopened {n} recurring upload links", reopened);
        }
    }
}
