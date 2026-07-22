using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
                .UseSqlServer(targetConnectionString, o =>
                {
                    o.CommandTimeout(120);
                    // Same MigrationsAssembly as the runtime app so this
                    // context can Migrate() with the SqlServer-specific set.
                    o.MigrationsAssembly("NimShare.Migrations.SqlServer");
                })
                .Options;
            await using var target = new NimShareDbContext(opts);
            // Real migrations — evolves schema across app upgrades, unlike the
            // one-shot EnsureCreated we used in v1.8.0.
            await target.Database.MigrateAsync(ct);
            // v1.10.127: modellgetriebener Schema-Abgleich. Die handgeschriebenen
            // SqlServer-Migrationen sind über die Zeit vom EF-Modell weggedriftet
            // (Spalten wie Users.PreferredTimezone, SignatureAudits.City, …, die
            // im Live-Betrieb per EnsureForensicColumnsAsync nachgezogen werden,
            // aber NIE ins Migrations-Ziel gelangten). Das führte beim Kopieren
            // zu „Invalid column name …". Statt diese Liste doppelt zu pflegen,
            // gehen wir hier das komplette Modell durch und ergänzen JEDE fehlende
            // Spalte mit dem korrekten SqlServer-Store-Typ — einmal, generisch,
            // für alle künftigen Drifts.
            var added = await ReconcileColumnsAsync(target, targetConnectionString, ct);
            sw.Stop();
            return new CopyResult(true, added, 0, sw.Elapsed, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CopyResult(false, 0, 0, sw.Elapsed, Flatten(ex));
        }
    }

    /// <summary>
    /// v1.10.127 — gleicht das Ziel-Schema (Azure SQL) spaltengenau an das
    /// EF-Modell an. Für jede Entity und jede skalare Property wird geprüft, ob
    /// die Spalte existiert; fehlt sie, wird sie per ALTER TABLE ADD als NULL
    /// mit dem vom SqlServer-Provider gemappten Store-Typ ergänzt (die Zieltabelle
    /// ist beim anschliessenden Copy leer, Werte kommen aus dem Copy — NOT NULL
    /// würde am ADD scheitern). Fehlende Tabellen kann diese Routine nicht
    /// anlegen; sie loggt sie laut (dürfen aber laut Migrations-Set nicht
    /// vorkommen). Gibt die Zahl der ergänzten Spalten zurück.
    /// </summary>
    private async Task<int> ReconcileColumnsAsync(NimShareDbContext target, string conn, CancellationToken ct)
    {
        int added = 0;
        await using var cn = new SqlConnection(conn);
        await cn.OpenAsync(ct);
        foreach (var et in target.Model.GetEntityTypes())
        {
            var tableName = et.GetTableName();
            if (string.IsNullOrEmpty(tableName)) continue;   // keyless / view / owned-splitting
            var storeObj = StoreObjectIdentifier.Table(tableName, et.GetSchema());

            // Tabelle überhaupt vorhanden? (Migrations sollten sie angelegt haben.)
            using (var tcheck = cn.CreateCommand())
            {
                tcheck.CommandText = "SELECT OBJECT_ID(@t, 'U')";
                tcheck.Parameters.AddWithValue("@t", tableName);
                var tid = await tcheck.ExecuteScalarAsync(ct);
                if (tid is null || tid == DBNull.Value)
                {
                    _log.LogWarning("Schema-Reconcile: Zieltabelle {Table} fehlt komplett — Migrations-Set unvollständig.", tableName);
                    continue;
                }
            }

            foreach (var prop in et.GetProperties())
            {
                var col = prop.GetColumnName(storeObj);
                if (string.IsNullOrEmpty(col)) continue;   // Property nicht auf diese Tabelle gemappt

                bool exists;
                using (var ccheck = cn.CreateCommand())
                {
                    ccheck.CommandText = "SELECT COL_LENGTH(@t, @c)";
                    ccheck.Parameters.AddWithValue("@t", tableName);
                    ccheck.Parameters.AddWithValue("@c", col);
                    var r = await ccheck.ExecuteScalarAsync(ct);
                    exists = r is not null && r != DBNull.Value;
                }
                if (exists) continue;

                // Store-Typ vom SqlServer-Provider-Modell (nicht vom Live-SQLite-
                // Modell — sonst kämen "TEXT"/"INTEGER" statt nvarchar/bigint).
                var storeType = prop.GetColumnType();
                if (string.IsNullOrEmpty(storeType))
                    storeType = prop.GetRelationalTypeMapping().StoreType;
                if (string.IsNullOrEmpty(storeType)) continue;

                using var alter = cn.CreateCommand();
                alter.CommandText = $"ALTER TABLE [{tableName}] ADD [{col}] {storeType} NULL";
                await alter.ExecuteNonQueryAsync(ct);
                added++;
                _log.LogWarning("Schema-Reconcile: Spalte {Table}.{Col} ({Type}) ergänzt (Migrations-Drift).", tableName, col, storeType);
            }
        }
        if (added > 0)
            _log.LogInformation("Schema-Reconcile: {Count} fehlende Spalte(n) im Ziel-Schema ergänzt.", added);
        return added;
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
            ("LinkEntries",          (s,t,c) => CopySet<NimShare.Core.Entities.LinkEntry>(s, t, c)),
            ("BlockedUsers",         (s,t,c) => CopySet<NimShare.Core.Entities.BlockedUser>(s, t, c)),
            ("ContentReports",       (s,t,c) => CopySet<NimShare.Core.Entities.ContentReport>(s, t, c)),
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
            ("FilePins",             (s,t,c) => CopySet<NimShare.Core.Entities.FilePin>(s, t, c)),
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
                .UseSqlServer(targetConnectionString, o =>
                {
                    o.CommandTimeout(180);
                    o.MigrationsAssembly("NimShare.Migrations.SqlServer");
                })
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
                long n;
                try
                {
                    n = await copy(source, target, ct);
                }
                catch (Exception ex)
                {
                    // v1.10.121: Die eigentliche Ursache eines Kopier-Fehlers
                    // (FK-Verletzung, String-Truncation, NOT-NULL) steckt in
                    // der InnerException — EFs Aussenmeldung ist nur „See the
                    // inner exception". Wir entfalten die ganze Kette UND
                    // nennen die Tabelle, an der es scheiterte, damit der
                    // Admin überhaupt etwas zum Anfassen hat.
                    sw.Stop();
                    var detail = Flatten(ex);
                    _log.LogError(ex, "Data copy failed on table {Table}", name);
                    return new CopyResult(false, i, totalRows, sw.Elapsed,
                        $"Tabelle „{name}\": {detail}");
                }
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
            return new CopyResult(false, 0, totalRows, sw.Elapsed, Flatten(ex));
        }
    }

    /// <summary>
    /// v1.10.121: Entfaltet die komplette InnerException-Kette zu einer Zeile.
    /// EF Core wirft <c>DbUpdateException</c> mit der nichtssagenden Meldung
    /// „An error occurred while saving the entity changes. See the inner
    /// exception for details." — die echte Ursache (z. B. eine SqlException
    /// „The INSERT statement conflicted with the FOREIGN KEY constraint …" oder
    /// „String or binary data would be truncated in column …") liegt eine oder
    /// mehrere Ebenen tiefer. Ohne dieses Entfalten sah der Admin nie die
    /// eigentliche Fehlermeldung.
    /// </summary>
    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            var msg = cur.Message?.Trim();
            if (!string.IsNullOrEmpty(msg) && (parts.Count == 0 || parts[^1] != msg))
                parts.Add(msg);
        }
        return string.Join(" → ", parts);
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

        // v1.10.132: Hat der PK im Ziel eine IDENTITY-Spalte (z. B.
        // ShareLinkAccesses.Id = bigint IDENTITY, SignatureAudits, ActivityEvents …),
        // muss SET IDENTITY_INSERT ON aktiv sein, während wir die aus der Quelle
        // übernommenen Id-Werte 1:1 einfügen (für FK-Treue). Sonst lehnt
        // SqlServer ab: „Cannot insert explicit value for identity column …
        // IDENTITY_INSERT is set to OFF." Generisch über sys.identity_columns
        // erkannt — deckt jede Identity-Tabelle automatisch ab.
        var et = target.Model.FindEntityType(typeof(T));
        var tableName = et?.GetTableName() ?? typeof(T).Name;
        var connStr = target.Database.GetConnectionString()!;
        bool useIdentityInsert = await TableHasIdentityAsync(connStr, tableName, ct);

        // Verbindung offen halten: SET IDENTITY_INSERT ist session-scoped und
        // muss für alle Batches derselben Tabelle aktiv bleiben; EF würde die
        // Verbindung sonst nach jedem SaveChanges schliessen und die Einstellung
        // verlieren.
        await target.Database.OpenConnectionAsync(ct);
        try
        {
            if (useIdentityInsert)
                await target.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] ON", ct);

            var batch = new List<T>(chunk);
            async Task FlushAsync()
            {
                if (batch.Count == 0) return;
                await target.Set<T>().AddRangeAsync(batch, ct);
                await target.SaveChangesAsync(ct);
                target.ChangeTracker.Clear();
                total += batch.Count;
                batch.Clear();
            }

            await foreach (var row in source.Set<T>().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
            {
                batch.Add(row);
                if (batch.Count >= chunk) await FlushAsync();
            }
            await FlushAsync();

            if (useIdentityInsert)
                await target.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] OFF", ct);
        }
        finally
        {
            await target.Database.CloseConnectionAsync();
        }
        return total;
    }

    /// <summary>
    /// v1.10.132: Prüft, ob eine Zieltabelle eine IDENTITY-Spalte hat. Über
    /// eine eigene, kurze Metadaten-Verbindung — unabhängig vom EF-Kontext.
    /// </summary>
    private static async Task<bool> TableHasIdentityAsync(string connStr, string table, CancellationToken ct)
    {
        try
        {
            await using var cn = new SqlConnection(connStr);
            await cn.OpenAsync(ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(@t)";
            cmd.Parameters.AddWithValue("@t", table);
            var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
            return n > 0;
        }
        catch { return false; }
    }
}
