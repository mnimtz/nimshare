using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.82: Zentralisiert die UGC-Moderations-Logik damit sie sowohl
/// aus API- als auch aus MVC-Controllern konsistent aufgerufen werden
/// kann. Der Directory-Filter (blockierte User raus) und der Direct-
/// Share-Filter greifen hier zu.
/// </summary>
public interface IModerationService
{
    /// <summary>Wen der User blockiert hat — nur die BlockedUserIds.</summary>
    Task<HashSet<Guid>> GetBlockedUserIdsAsync(Guid userId, CancellationToken ct = default);
    Task<BlockedUser> BlockAsync(Guid userId, Guid blockedUserId, string? reason, CancellationToken ct = default);
    Task<bool> UnblockAsync(Guid userId, Guid blockedUserId, CancellationToken ct = default);

    Task<ContentReport> ReportAsync(Guid reporterUserId,
        ContentReportSubjectKind kind, Guid subjectId,
        ContentReportReason reason, string? note,
        string? subjectLabel = null, Guid? subjectOwnerUserId = null,
        CancellationToken ct = default);
}

public class ModerationService : IModerationService
{
    private readonly NimShareDbContext _db;
    private readonly ILogger<ModerationService> _log;

    public ModerationService(NimShareDbContext db, ILogger<ModerationService> log)
    {
        _db = db; _log = log;
    }

    public async Task<HashSet<Guid>> GetBlockedUserIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await _db.BlockedUsers
            .Where(b => b.UserId == userId)
            .Select(b => b.BlockedUserId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    public async Task<BlockedUser> BlockAsync(Guid userId, Guid blockedUserId, string? reason, CancellationToken ct = default)
    {
        if (userId == blockedUserId)
            throw new InvalidOperationException("Cannot block yourself.");

        var existing = await _db.BlockedUsers
            .FirstOrDefaultAsync(b => b.UserId == userId && b.BlockedUserId == blockedUserId, ct);
        if (existing is not null)
        {
            // Idempotent — nur den Reason updaten wenn übergeben.
            if (!string.IsNullOrWhiteSpace(reason)) existing.Reason = reason;
            await _db.SaveChangesAsync(ct);
            return existing;
        }
        var entry = new BlockedUser
        {
            UserId = userId,
            BlockedUserId = blockedUserId,
            Reason = reason,
        };
        _db.BlockedUsers.Add(entry);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("User {U} blockiert {B}", userId, blockedUserId);
        return entry;
    }

    public async Task<bool> UnblockAsync(Guid userId, Guid blockedUserId, CancellationToken ct = default)
    {
        var row = await _db.BlockedUsers
            .FirstOrDefaultAsync(b => b.UserId == userId && b.BlockedUserId == blockedUserId, ct);
        if (row is null) return false;
        _db.BlockedUsers.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ContentReport> ReportAsync(Guid reporterUserId,
        ContentReportSubjectKind kind, Guid subjectId,
        ContentReportReason reason, string? note,
        string? subjectLabel = null, Guid? subjectOwnerUserId = null,
        CancellationToken ct = default)
    {
        var rep = new ContentReport
        {
            ReporterUserId = reporterUserId,
            SubjectKind = kind,
            SubjectId = subjectId,
            SubjectLabel = subjectLabel,
            SubjectOwnerUserId = subjectOwnerUserId,
            Reason = reason,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Status = ContentReportStatus.Open,
        };
        _db.ContentReports.Add(rep);
        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Neuer UGC-Report {Id}: {Kind}#{SubjectId} von {Reporter}, Grund: {Reason}",
            rep.Id, kind, subjectId, reporterUserId, reason);
        return rep;
    }
}
