using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// Nightly-ish sweep that:
/// - fires reminder emails to still-pending participants 72h before the
///   deadline (once per participant),
/// - marks the request Cancelled when the deadline passes without all
///   signatures.
/// Cheap in-process BackgroundService — fine at the volumes NimShare
/// targets. Ticks every 6h; interval also configurable via
/// Signatures:ReminderIntervalMinutes.
/// </summary>
public class SignatureReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SignatureReminderService> _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _remindWindow;

    public SignatureReminderService(IServiceScopeFactory scopes,
        ILogger<SignatureReminderService> log, IConfiguration cfg)
    {
        _scopes = scopes; _log = log;
        var mins = cfg.GetValue<int?>("Signatures:ReminderIntervalMinutes") ?? 360;
        _interval = TimeSpan.FromMinutes(mins);
        _remindWindow = TimeSpan.FromHours(72);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so the app comes up cleanly before we hammer the DB.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "signature reminder tick failed"); }
            try { await Task.Delay(_interval, stoppingToken); } catch { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var notif = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var inApp = scope.ServiceProvider.GetRequiredService<IUserNotifier>();
        var localizerFactory = scope.ServiceProvider.GetRequiredService<IStringLocalizerFactory>();
        var localizer = localizerFactory.Create(typeof(NimShare.Api.SharedResources));

        var now = DateTimeOffset.UtcNow;
        var soon = now.Add(_remindWindow);

        var pending = await db.SignatureRequests
            .Where(r => r.Status == SignatureRequestStatus.Sent
                && r.Deadline != null && r.Deadline > now && r.Deadline <= soon)
            .Include(r => r.Participants)
            .Include(r => r.Initiator)
            .Include(r => r.SourceFile)
            .ToListAsync(ct);
        foreach (var req in pending)
        {
            // Localise reminder emails against the initiator's preferred
            // culture — Signature participants don't have a User row so we
            // can't look up their own language.
            var initiatorCulture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture)
                ? "en" : req.Initiator!.PreferredCulture;
            CultureInfo prev = CultureInfo.CurrentUICulture;
            try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(initiatorCulture); }
            catch { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
            try
            {
                foreach (var p in req.Participants.Where(x =>
                    x.Status != SignatureParticipantStatus.Signed
                    && x.Status != SignatureParticipantStatus.Declined))
                {
                    var already = await db.SignatureAudits.AnyAsync(a =>
                        a.RequestId == req.Id && a.ParticipantId == p.Id
                        && a.Kind == SignatureAuditKind.Invited
                        && a.Note != null && a.Note.StartsWith("reminder"), ct);
                    if (already) continue;
                    var roleWord = localizer[p.Role == SignatureParticipantRole.Signer
                        ? "sig.reminder.role_signer" : "sig.reminder.role_viewer"].Value;
                    var subject = localizer["sig.reminder.subject", req.Title].Value;
                    var body = localizer["sig.reminder.body",
                        p.Name,
                        req.Initiator?.DisplayName ?? "NimShare",
                        roleWord,
                        req.Title,
                        req.Deadline!.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)].Value;
                    try { await notif.SendShareLinkAsync(p.Email, "NimShare", subject, body, ct); } catch { }
                    db.SignatureAudits.Add(new SignatureAudit
                    {
                        RequestId = req.Id, ParticipantId = p.Id,
                        Kind = SignatureAuditKind.Invited, Note = "reminder",
                    });
                }
            }
            finally { CultureInfo.CurrentUICulture = prev; }
        }
        await db.SaveChangesAsync(ct);

        // Expire requests whose deadline has passed without completion.
        var expired = await db.SignatureRequests
            .Where(r => r.Status == SignatureRequestStatus.Sent
                && r.Deadline != null && r.Deadline <= now)
            .Include(r => r.Initiator)
            .ToListAsync(ct);
        foreach (var req in expired)
        {
            var initiatorCulture = string.IsNullOrWhiteSpace(req.Initiator?.PreferredCulture)
                ? "en" : req.Initiator!.PreferredCulture;
            CultureInfo prev = CultureInfo.CurrentUICulture;
            try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(initiatorCulture); }
            catch { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en"); }
            try
            {
                req.Status = SignatureRequestStatus.Cancelled;
                db.SignatureAudits.Add(new SignatureAudit
                {
                    RequestId = req.Id, Kind = SignatureAuditKind.Cancelled,
                    Note = "deadline expired",
                });
                await inApp.NotifyAsync(req.InitiatorUserId, NotificationKind.SystemAnnouncement,
                    localizer["sig.expired.notif.title", req.Title].Value,
                    body: localizer["sig.expired.notif.body"].Value,
                    href: "/signatures", ct: ct);
            }
            finally { CultureInfo.CurrentUICulture = prev; }
        }
        await db.SaveChangesAsync(ct);
    }
}
