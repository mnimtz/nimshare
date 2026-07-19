using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>Best-effort in-app notification writer — never breaks the caller.</summary>
public interface IUserNotifier
{
    Task NotifyAsync(Guid userId, NotificationKind kind, string title, string? body = null,
        string? href = null, Guid? fileId = null, CancellationToken ct = default);
    Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default);
}

public class UserNotifier : IUserNotifier
{
    private readonly NimShareDbContext _db;
    private readonly ILogger<UserNotifier> _log;

    public UserNotifier(NimShareDbContext db, ILogger<UserNotifier> log)
    {
        _db = db; _log = log;
    }

    public async Task NotifyAsync(Guid userId, NotificationKind kind, string title, string? body = null,
        string? href = null, Guid? fileId = null, CancellationToken ct = default)
    {
        try
        {
            _db.UserNotifications.Add(new UserNotification
            {
                UserId = userId, Kind = kind,
                Title = title.Length > 240 ? title[..240] : title,
                Body = body,
                Href = href,
                FileId = fileId,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "notification write failed for user {UserId}", userId);
        }
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default) =>
        _db.UserNotifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
}
