using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;

namespace NimShare.Api.Services;

/// <summary>
/// Ferries the metadata database from the current provider (Sqlite / whatever
/// is live) to a target Azure SQL database. Two-phase:
/// (1) verify + prepare the target (connect to master, create DB if absent,
///     create schema via EnsureCreated),
/// (2) copy every row of every entity across in FK-safe order, then hand back
///     to the caller who saves the new config and restarts the app.
///
/// Idempotent enough to re-run: the copy phase first empties the target of any
/// prior rows so partial migrations don't leave duplicates.
/// </summary>
public interface IDbMigrationService
{
    Task<TestResult> TestAsync(string connectionString, CancellationToken ct);
    Task<CreateResult> CreateDatabaseIfMissingAsync(string masterConnectionString, string dbName, CancellationToken ct);
    Task<CopyResult> CopyDataAsync(string targetConnectionString, IProgress<CopyProgress>? progress, CancellationToken ct);
    Task<CopyResult> EnsureSchemaAsync(string targetConnectionString, CancellationToken ct);
}

public record TestResult(bool Ok, string? ServerVersion, string? Error);
public record CreateResult(bool Ok, bool CreatedNew, string? Error);
public record CopyResult(bool Ok, int TablesProcessed, long RowsCopied, TimeSpan Duration, string? Error);
public record CopyProgress(string Table, int TableIndex, int TableTotal, long RowsSoFar);

public class DbMigrationService : IDbMigrationService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DbMigrationService> _log;

    public DbMigrationService(IServiceProvider services, ILogger<DbMigrationService> log)
    {
        _services = services; _log = log;
    }

    public async Task<TestResult> TestAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION";
            var v = (await cmd.ExecuteScalarAsync(ct))?.ToString();
            return new TestResult(true, v?.Split('\n').FirstOrDefault()?.Trim(), null);
        }
        catch (Exception ex) { return new TestResult(false, null, ex.Message); }
    }

    public async Task<CreateResult> CreateDatabaseIfMissingAsync(string masterConnectionString, string dbName, CancellationToken ct)
    {
        try
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(dbName, "^[A-Za-z0-9_\\-]{1,64}$"))
                return new CreateResult(false, false, "Database name must be alphanumeric, dash or underscore (max 64 chars).");
            await using var conn = new SqlConnection(masterConnectionString);
            await conn.OpenAsync(ct);
            // Check existence
            bool exists;
            await using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(1) FROM sys.databases WHERE name = @n";
                probe.Parameters.AddWithValue("@n", dbName);
                exists = (int)(await probe.ExecuteScalarAsync(ct) ?? 0) > 0;
            }
            if (exists) return new CreateResult(true, false, null);
            // Azure SQL Basic tier — cheapest edition, fine for NimShare workload
            await using var create = conn.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{dbName}] (EDITION = 'Basic', SERVICE_OBJECTIVE = 'Basic')";
            create.CommandTimeout = 120;
            await create.ExecuteNonQueryAsync(ct);
            return new CreateResult(true, true, null);
        }
        catch (Exception ex) { return new CreateResult(false, false, ex.Message); }
    }

    public async Task<CopyResult> EnsureSchemaAsync(string targetConnectionString, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var opts = new DbContextOptionsBuilder<NimShareDbContext>()
                .UseSqlServer(targetConnectionString, o => o.CommandTimeout(120))
                .Options;
            await using var target = new NimShareDbContext(opts);
            await target.Database.EnsureCreatedAsync(ct);
            sw.Stop();
            return new CopyResult(true, 0, 0, sw.Elapsed, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CopyResult(false, 0, 0, sw.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// Copies every row of every entity from the currently-live NimShareDbContext
    /// to the target SqlServer connection. Runs in FK-safe insert order and
    /// keeps AutoDetectChanges off so bulk inserts don't grind on the change
    /// tracker.
    /// </summary>
    public async Task<CopyResult> CopyDataAsync(string targetConnectionString, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long totalRows = 0;
        var tables = new List<(string Name, Func<NimShareDbContext, NimShareDbContext, CancellationToken, Task<long>> Copy)>
        {
            // Parent tables first — every entity below either has no FKs or
            // only references entities earlier in this list.
            ("Users",                (s,t,c) => CopySet<NimShare.Core.Entities.User>(s, t, c)),
            ("Groups",               (s,t,c) => CopySet<NimShare.Core.Entities.Group>(s, t, c)),
            ("GroupMemberships",     (s,t,c) => CopySet<NimShare.Core.Entities.GroupMembership>(s, t, c)),
            ("Invitations",          (s,t,c) => CopySet<NimShare.Core.Entities.Invitation>(s, t, c)),
            ("CustomDomains",        (s,t,c) => CopySet<NimShare.Core.Entities.CustomDomain>(s, t, c)),
            ("EmailGateways",        (s,t,c) => CopySet<NimShare.Core.Entities.EmailGatewaySettings>(s, t, c)),
            ("AiGateways",           (s,t,c) => CopySet<NimShare.Core.Entities.AiGatewaySettings>(s, t, c)),
            ("OfficeSettings",       (s,t,c) => CopySet<NimShare.Core.Entities.OfficeSettings>(s, t, c)),
            ("LandingTemplates",     (s,t,c) => CopySet<NimShare.Core.Entities.LandingTemplate>(s, t, c)),
            ("Folders",              (s,t,c) => CopySet<NimShare.Core.Entities.Folder>(s, t, c)),
            ("Files",                (s,t,c) => CopySet<NimShare.Core.Entities.StorageFile>(s, t, c)),
            ("StorageFileVersions",  (s,t,c) => CopySet<NimShare.Core.Entities.StorageFileVersion>(s, t, c)),
            ("FileEmbeddings",       (s,t,c) => CopySet<NimShare.Core.Entities.FileEmbedding>(s, t, c)),
            ("ShareLinks",           (s,t,c) => CopySet<NimShare.Core.Entities.ShareLink>(s, t, c)),
            ("ShareLinkAccesses",    (s,t,c) => CopySet<NimShare.Core.Entities.ShareLinkAccess>(s, t, c)),
            ("UploadRequests",       (s,t,c) => CopySet<NimShare.Core.Entities.UploadRequestLink>(s, t, c)),
            ("DirectShares",         (s,t,c) => CopySet<NimShare.Core.Entities.DirectShare>(s, t, c)),
            ("FolderAccessOverrides",(s,t,c) => CopySet<NimShare.Core.Entities.FolderAccessOverride>(s, t, c)),
            ("UserFavorites",        (s,t,c) => CopySet<NimShare.Core.Entities.UserFavorite>(s, t, c)),
            ("ActivityEvents",       (s,t,c) => CopySet<NimShare.Core.Entities.ActivityEvent>(s, t, c)),
            ("UserNotifications",    (s,t,c) => CopySet<NimShare.Core.Entities.UserNotification>(s, t, c)),
            ("SignatureRequests",    (s,t,c) => CopySet<NimShare.Core.Entities.SignatureRequest>(s, t, c)),
            ("SignatureParticipants",(s,t,c) => CopySet<NimShare.Core.Entities.SignatureParticipant>(s, t, c)),
            ("SignatureFields",      (s,t,c) => CopySet<NimShare.Core.Entities.SignatureField>(s, t, c)),
            ("SignatureAudits",      (s,t,c) => CopySet<NimShare.Core.Entities.SignatureAudit>(s, t, c)),
            ("WikiPages",            (s,t,c) => CopySet<NimShare.Core.Entities.WikiPage>(s, t, c)),
            ("ApiTokens",            (s,t,c) => CopySet<NimShare.Core.Entities.ApiToken>(s, t, c)),
            ("Webhooks",             (s,t,c) => CopySet<NimShare.Core.Entities.Webhook>(s, t, c)),
            ("EmailTemplates",       (s,t,c) => CopySet<NimShare.Core.Entities.EmailTemplate>(s, t, c)),
            ("Contacts",             (s,t,c) => CopySet<NimShare.Core.Entities.Contact>(s, t, c)),
            ("SigningCertificates",  (s,t,c) => CopySet<NimShare.Core.Entities.SigningCertificate>(s, t, c)),
        };
        try
        {
            using var scope = _services.CreateScope();
            var source = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
            var opts = new DbContextOptionsBuilder<NimShareDbContext>()
                .UseSqlServer(targetConnectionString, o => o.CommandTimeout(180))
                .Options;
            await using var target = new NimShareDbContext(opts);
            // Bulk inserts don't need change tracking.
            target.ChangeTracker.AutoDetectChangesEnabled = false;

            // WIPE FIRST, in reverse-list order so children go before parents.
            // If we wiped-and-inserted table-by-table in the parent-first
            // iteration order below, the first SaveChanges on Users would trip
            // an FK from any pre-existing Files/Groups still in the target DB
            // (re-runs after a partial migration would guarantee that).
            for (int i = tables.Count - 1; i >= 0; i--)
            {
                var wipeName = tables[i].Name;
                var entityType = target.Model.FindEntityType(wipeName)?.ClrType ?? typeof(object);
                // Skip the wipe if the table doesn't exist yet — first-time
                // switch to a fresh DB has nothing to remove.
                try
                {
                    await target.Database.ExecuteSqlRawAsync($"DELETE FROM [{wipeName}]", ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Wipe of {Table} skipped ({Message})", wipeName, ex.Message);
                }
            }

            for (int i = 0; i < tables.Count; i++)
            {
                var (name, copy) = tables[i];
                var n = await copy(source, target, ct);
                totalRows += n;
                progress?.Report(new CopyProgress(name, i + 1, tables.Count, totalRows));
                _log.LogInformation("Copied {Count} rows into {Table}", n, name);
            }
            sw.Stop();
            return new CopyResult(true, tables.Count, totalRows, sw.Elapsed, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "Data copy failed");
            return new CopyResult(false, 0, totalRows, sw.Elapsed, ex.Message);
        }
    }

    private static async Task<long> CopySet<T>(NimShareDbContext source, NimShareDbContext target, CancellationToken ct)
        where T : class
    {
        // Streamed read + chunked write — avoids loading a full Files or
        // FileEmbeddings table (potentially hundreds of MB) into memory on a
        // B1 App Service. Target wipe already happened up front in
        // CopyDataAsync (reverse FK order).
        const int chunk = 500;
        long total = 0;
        var batch = new List<T>(chunk);
        await foreach (var row in source.Set<T>().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            batch.Add(row);
            if (batch.Count >= chunk)
            {
                await target.Set<T>().AddRangeAsync(batch, ct);
                await target.SaveChangesAsync(ct);
                target.ChangeTracker.Clear();
                total += batch.Count;
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await target.Set<T>().AddRangeAsync(batch, ct);
            await target.SaveChangesAsync(ct);
            target.ChangeTracker.Clear();
            total += batch.Count;
        }
        return total;
    }
}
