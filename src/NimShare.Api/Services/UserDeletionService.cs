using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.82: App-Store-Blocker — Apple Guideline 5.1.1(v) verlangt seit
/// Juni 2022 dass jede App mit Account-Erstellung auch Account-Löschung
/// IN-APP anbietet. "Nur im Web" reicht NICHT für Review-Approval.
///
/// Diese Klasse räumt einen User + alle seine Daten sauber ab:
///   • StorageFiles (Blob + DB row + Version-Blobs)
///   • Persönliche Assets: Contacts, ApiTokens, Webhooks, EmailTemplates,
///     LandingTemplates (nur Personal — Global-Templates bleiben), FilePins,
///     UserFavorites, UserNotifications, SigningCertificates
///   • Ownership: Folders, ShareLinks, UploadRequests, CustomDomains,
///     DirectShares (SharedBy + Target), SignatureRequests (Initiator)
///   • Membership: GroupMemberships, Invitations (InvitedBy)
///   • Avatar-Blob
///   • Wiki-Pages (Owner)
///
/// SignatureParticipants sind email-linked (nicht user-linked) und bleiben
/// als Audit-Trail stehen — DSGVO-konform, weil sie den forensischen Nachweis
/// der Signatur belegen (Art. 6(1)(f) berechtigtes Interesse).
///
/// ActivityEvents wo der User Actor war werden anonymisiert (ActorUserId
/// auf Guid.Empty), nicht gelöscht — damit die Chronologie in Gruppen für
/// andere Nutzer nicht rückwirkend kaputt geht.
/// </summary>
public interface IUserDeletionService
{
    Task<UserDeletionResult> DeleteAsync(Guid userId, CancellationToken ct = default);
}

public sealed record UserDeletionResult(
    int FilesDeleted,
    long BytesFreed,
    int BlobDeleteFailures);

public class UserDeletionService : IUserDeletionService
{
    private readonly NimShareDbContext _db;
    private readonly IBlobStorageService _blobs;
    private readonly ILogger<UserDeletionService> _log;

    public UserDeletionService(NimShareDbContext db, IBlobStorageService blobs,
        ILogger<UserDeletionService> log)
    {
        _db = db; _blobs = blobs; _log = log;
    }

    public async Task<UserDeletionResult> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new UserDeletionResult(0, 0, 0);

        _log.LogWarning("Account-Löschung startet für User {Id} ({Email})", user.Id, user.Email);

        // 1) Files: erst Blobs im Storage abräumen, dann DB rows
        var files = await _db.Files.Where(f => f.OwnerId == userId).ToListAsync(ct);
        var versions = await _db.StorageFileVersions
            .Where(v => files.Select(f => f.Id).Contains(v.FileId)).ToListAsync(ct);
        long freed = 0;
        int blobFailures = 0;
        foreach (var v in versions)
        {
            try { await _blobs.DeleteAsync(v.BlobPath, ct); }
            catch (Exception ex) { blobFailures++; _log.LogWarning(ex, "Version-Blob {P} konnte nicht gelöscht werden", v.BlobPath); }
        }
        _db.StorageFileVersions.RemoveRange(versions);
        foreach (var f in files)
        {
            try { await _blobs.DeleteAsync(f.BlobPath, ct); freed += f.SizeBytes; }
            catch (Exception ex) { blobFailures++; _log.LogWarning(ex, "File-Blob {P} konnte nicht gelöscht werden", f.BlobPath); }
        }
        _db.Files.RemoveRange(files);

        // 2) Signatur-Requests des Initiators (Final-PDFs sind Files → schon oben gelöscht)
        var sigReqs = await _db.SignatureRequests.Where(r => r.InitiatorUserId == userId).ToListAsync(ct);
        _db.SignatureRequests.RemoveRange(sigReqs);

        // 3) Signing-Certs (Blobs im DP-Store, bleiben verschlüsselt liegen wenn Blob-Delete failed)
        _db.SigningCertificates.RemoveRange(
            await _db.SigningCertificates.Where(c => c.OwnerUserId == userId).ToListAsync(ct));

        // 4) Persönliche Templates (globale Templates ohne OwnerUserId bleiben)
        _db.LandingTemplates.RemoveRange(
            await _db.LandingTemplates.Where(t => t.OwnerUserId == userId).ToListAsync(ct));
        _db.EmailTemplates.RemoveRange(
            await _db.EmailTemplates.Where(t => t.OwnerUserId == userId).ToListAsync(ct));

        // 5) Persönliche Kontakte, Pins, Favorites, Notifications, API-Tokens, Webhooks
        _db.Contacts.RemoveRange(await _db.Contacts.Where(c => c.OwnerUserId == userId).ToListAsync(ct));
        _db.FilePins.RemoveRange(await _db.FilePins.Where(p => p.UserId == userId).ToListAsync(ct));
        _db.UserFavorites.RemoveRange(await _db.UserFavorites.Where(f => f.UserId == userId).ToListAsync(ct));
        _db.UserNotifications.RemoveRange(await _db.UserNotifications.Where(n => n.UserId == userId).ToListAsync(ct));
        _db.ApiTokens.RemoveRange(await _db.ApiTokens.Where(t => t.OwnerUserId == userId).ToListAsync(ct));
        _db.Webhooks.RemoveRange(await _db.Webhooks.Where(w => w.OwnerUserId == userId).ToListAsync(ct));

        // 6) DirectShares in beiden Richtungen (der User teilt / der User bekommt geteilt)
        _db.DirectShares.RemoveRange(
            await _db.DirectShares.Where(d => d.SharedByUserId == userId || d.TargetUserId == userId).ToListAsync(ct));

        // 7) ShareLinks / UploadRequests / CustomDomains (Owner)
        _db.ShareLinks.RemoveRange(await _db.ShareLinks.Where(s => s.OwnerId == userId).ToListAsync(ct));
        _db.UploadRequests.RemoveRange(await _db.UploadRequests.Where(u => u.OwnerId == userId).ToListAsync(ct));
        _db.CustomDomains.RemoveRange(await _db.CustomDomains.Where(d => d.OwnerId == userId).ToListAsync(ct));

        // 8) Wiki-Owner-Pages
        _db.WikiPages.RemoveRange(await _db.WikiPages.Where(w => w.OwnerUserId == userId).ToListAsync(ct));

        // 9) Ordner (persönliche + vom User erstellte in Groups) — Public-Ordner nicht,
        //    weil andere Nutzer sie brauchen. Cascade auf StorageFile ist oben schon
        //    manuell gelaufen.
        _db.Folders.RemoveRange(await _db.Folders
            .Where(f => f.OwnerUserId == userId).ToListAsync(ct));

        // 10) Group-Memberships (User verlässt alle Gruppen; Gruppen bleiben)
        _db.GroupMemberships.RemoveRange(
            await _db.GroupMemberships.Where(m => m.UserId == userId).ToListAsync(ct));

        // 11) Invitations vom User verschickt
        _db.Invitations.RemoveRange(
            await _db.Invitations.Where(i => i.InvitedByUserId == userId).ToListAsync(ct));

        // 12) Avatar-Blob (falls URL relative → own Container)
        if (!string.IsNullOrWhiteSpace(user.AvatarUrl) && user.AvatarUrl.StartsWith("/avatars/", StringComparison.OrdinalIgnoreCase))
        {
            try { await _blobs.DeleteAsync(user.AvatarUrl.TrimStart('/'), ct); }
            catch (Exception ex) { blobFailures++; _log.LogWarning(ex, "Avatar-Blob konnte nicht gelöscht werden"); }
        }

        // 13) Activity-Events: nicht löschen, aber Actor anonymisieren — sonst
        //     verschwinden Gruppen-Ereignisse aus der Chronologie anderer Nutzer.
        var acts = await _db.ActivityEvents.Where(a => a.ActorUserId == userId).ToListAsync(ct);
        foreach (var a in acts) a.ActorUserId = Guid.Empty;

        // 14) User selbst
        _db.Users.Remove(user);

        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Account-Löschung fertig für {Id}: {Files} Dateien, {Bytes} Bytes, {Fail} Blob-Fehler",
            userId, files.Count, freed, blobFailures);

        return new UserDeletionResult(files.Count, freed, blobFailures);
    }
}
