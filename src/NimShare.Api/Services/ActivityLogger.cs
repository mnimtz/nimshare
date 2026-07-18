using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// One-line writer for activity events. Best-effort — never let a log write
/// break the caller's actual work; swallow exceptions.
/// </summary>
public interface IActivityLogger
{
    Task LogAsync(ActivityKind kind, User actor, string summary,
        Guid? fileId = null, Guid? folderId = null, Guid? groupId = null, Guid? targetUserId = null,
        CancellationToken ct = default);
}

public class ActivityLogger : IActivityLogger
{
    private readonly NimShareDbContext _db;
    private readonly ILogger<ActivityLogger> _log;

    public ActivityLogger(NimShareDbContext db, ILogger<ActivityLogger> log)
    {
        _db = db;
        _log = log;
    }

    public async Task LogAsync(ActivityKind kind, User actor, string summary,
        Guid? fileId = null, Guid? folderId = null, Guid? groupId = null, Guid? targetUserId = null,
        CancellationToken ct = default)
    {
        try
        {
            _db.ActivityEvents.Add(new ActivityEvent
            {
                Kind = kind,
                ActorUserId = actor.Id,
                Summary = summary.Length > 400 ? summary[..400] : summary,
                FileId = fileId, FolderId = folderId, GroupId = groupId, TargetUserId = targetUserId,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "activity log write failed for {Kind}", kind);
        }
    }
}
