using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NimShare.Core.Data;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.116 — Vollständiges DB-Backup/Restore für Admins. Provider-agnostisch
/// (Sqlite + SqlServer), da über die EF-Metadaten und nicht über DB-Dumps.
/// Serialisiert JEDE Entität mit ihren SKALAR-Feldern (keine Navigationen →
/// keine Zyklen) zu JSON. Der Blob-Inhalt (Datei-Bytes im Storage) ist NICHT
/// Teil des Backups — die StorageFile-Zeilen samt BlobPath schon, ein Restore
/// zeigt also wieder auf dieselben Blobs.
/// </summary>
public interface IBackupService
{
    Task<string> ExportAsync(CancellationToken ct = default);
    Task<(int Tables, int Rows)> ImportAsync(string json, CancellationToken ct = default);
    /// <summary>v1.10.145 — Restore mit Self-Lockout-Schutz: bricht ab, wenn
    /// die E-Mail des aktuell handelnden Admins nicht im Backup enthalten ist.</summary>
    Task<(int Tables, int Rows)> ImportAsync(string json, string? actingAdminEmail, CancellationToken ct = default);
}

public class BackupService : IBackupService
{
    private readonly NimShareDbContext _db;
    public BackupService(NimShareDbContext db) => _db = db;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Entitätstypen in FK-sicherer Reihenfolge (Eltern vor Kindern).</summary>
    private List<IEntityType> OrderedEntityTypes()
    {
        var types = _db.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList();
        // Topologische Sortierung: ein Typ kommt NACH allen Typen, auf die er
        // per FK zeigt. Zyklen (self-ref FKs) werden ignoriert.
        var result = new List<IEntityType>();
        var visiting = new HashSet<IEntityType>();
        var done = new HashSet<IEntityType>();
        void Visit(IEntityType t)
        {
            if (done.Contains(t) || visiting.Contains(t)) return;
            visiting.Add(t);
            foreach (var fk in t.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType;
                if (principal != t && types.Contains(principal)) Visit(principal);
            }
            visiting.Remove(t);
            done.Add(t);
            result.Add(t);
        }
        foreach (var t in types) Visit(t);
        return result;
    }

    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["_meta"] = new { format = "nimshare-backup", version = 1, createdAt = DateTimeOffset.UtcNow },
        };
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var et in OrderedEntityTypes())
        {
            var clr = et.ClrType;
            var props = et.GetProperties().ToList();
            var rows = new List<Dictionary<string, object?>>();
            // Ungetrackt laden — reine Lesekopie.
            var set = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(clr).Invoke(_db, null)!;
            var list = await EntityFrameworkQueryableExtensions.ToListAsync(set.AsNoTracking(), ct);
            foreach (var entity in list)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var p in props)
                {
                    var val = p.PropertyInfo?.GetValue(entity) ?? p.FieldInfo?.GetValue(entity);
                    dict[p.Name] = val;
                }
                rows.Add(dict);
            }
            tables[et.Name] = rows;
        }
        payload["tables"] = tables;
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    public Task<(int Tables, int Rows)> ImportAsync(string json, CancellationToken ct = default)
        => ImportAsync(json, actingAdminEmail: null, ct);

    /// <summary>
    /// v1.10.145 — Restore-Kern mit drei Härtungen aus dem Audit:
    /// (1) SqlServer-Identity-Spalten (ShareLinkAccesses/SignatureAudits) über
    ///     SET IDENTITY_INSERT ON umgehen, sonst „Cannot insert explicit value
    ///     for identity column" → Restore konnte auf Produktion NIE laufen;
    /// (2) Self-Lockout-Schutz: wenn die E-Mail des aktuell handelnden Admins
    ///     im Backup FEHLT, wird der Restore VOR dem Wipe abgebrochen — nach
    ///     dem DB-Umzug-Schreck genau die Absicherung, die wir brauchten;
    /// (3) Aufrufer kann echte InnerException-Kette entfalten (siehe Controller).
    /// </summary>
    public async Task<(int Tables, int Rows)> ImportAsync(string json, string? actingAdminEmail, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tables", out var tablesEl))
            throw new InvalidOperationException("Backup-Datei hat kein 'tables'-Objekt.");

        var ordered = OrderedEntityTypes();

        // (2) Self-Lockout-Schutz: bevor überhaupt etwas gewiped wird, prüfen
        // ob der aktuelle Admin im Backup vorkommt. Sonst wäre er nach dem
        // Restore weg → Willkommens-/Admin-anlegen-Seite → alle ausgesperrt.
        if (!string.IsNullOrWhiteSpace(actingAdminEmail))
        {
            var usersEt = ordered.FirstOrDefault(t => t.ClrType.Name == "User");
            if (usersEt is null || !tablesEl.TryGetProperty(usersEt.Name, out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Backup enthält keine Users-Tabelle — Restore würde dich aussperren.");
            var needle = actingAdminEmail.Trim().ToLowerInvariant();
            bool present = false;
            foreach (var u in usersEl.EnumerateArray())
            {
                if (u.TryGetProperty("Email", out var em) && em.ValueKind == JsonValueKind.String
                    && string.Equals(em.GetString()?.Trim().ToLowerInvariant(), needle, StringComparison.Ordinal))
                { present = true; break; }
            }
            if (!present)
                throw new InvalidOperationException(
                    $"Dein Admin-Konto ({actingAdminEmail}) ist im Backup nicht enthalten. Restore würde dich aussperren — abgebrochen.");
        }

        var isSqlServer = _db.Database.IsSqlServer();
        // Identity-Tabellen ermitteln (Property mit ValueGenerated == OnAdd auf
        // integralem PK) — deckt heute ShareLinkAccesses + SignatureAudits ab,
        // arbeitet aber generisch für alle künftigen.
        var identityTables = ordered
            .Where(t => t.FindPrimaryKey()?.Properties.All(p =>
                p.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd
                && (p.ClrType == typeof(long) || p.ClrType == typeof(int))) == true)
            .Select(t => t.GetTableName())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Provider-übergreifend transaktional. Kinder zuerst löschen (reverse),
        // dann Eltern-zuerst wieder einfügen.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // 1) Alles leeren (umgekehrte FK-Reihenfolge).
        foreach (var et in Enumerable.Reverse(ordered))
        {
            var clr = et.ClrType;
            var set = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(clr).Invoke(_db, null)!;
            var existing = await EntityFrameworkQueryableExtensions.ToListAsync(set, ct);
            if (existing.Count > 0) _db.RemoveRange(existing);
        }
        await _db.SaveChangesAsync(ct);

        // 2) Einfügen (Eltern-zuerst).
        int tableCount = 0, rowCount = 0;
        foreach (var et in ordered)
        {
            if (!tablesEl.TryGetProperty(et.Name, out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                continue;
            var clr = et.ClrType;
            var props = et.GetProperties().ToList();
            tableCount++;

            // (1) IDENTITY_INSERT für SqlServer-Identity-Tabellen einschalten,
            // damit explizite Id-Werte aus dem Backup akzeptiert werden.
            var tableName = et.GetTableName();
            var needsIdentityInsert = isSqlServer && !string.IsNullOrEmpty(tableName)
                                      && identityTables.Contains(tableName!)
                                      && rowsEl.GetArrayLength() > 0;
            if (needsIdentityInsert)
                await _db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] ON", ct);

            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                var entity = Activator.CreateInstance(clr)!;
                foreach (var p in props)
                {
                    if (!rowEl.TryGetProperty(p.Name, out var valEl)) continue;
                    var target = p.ClrType;
                    var value = DeserializeValue(valEl, target);
                    if (p.PropertyInfo is not null && p.PropertyInfo.CanWrite)
                        p.PropertyInfo.SetValue(entity, value);
                    else
                        p.FieldInfo?.SetValue(entity, value);
                }
                _db.Add(entity);
                rowCount++;
            }
            // Batchweise speichern, damit große Backups nicht den Change-Tracker sprengen.
            await _db.SaveChangesAsync(ct);

            if (needsIdentityInsert)
                await _db.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] OFF", ct);
        }

        await tx.CommitAsync(ct);
        return (tableCount, rowCount);
    }

    private static object? DeserializeValue(JsonElement el, Type target)
    {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (el.ValueKind == JsonValueKind.Null) return null;
        try
        {
            if (underlying == typeof(string)) return el.GetString();
            if (underlying == typeof(Guid)) return el.GetGuid();
            if (underlying == typeof(bool)) return el.GetBoolean();
            if (underlying == typeof(int)) return el.GetInt32();
            if (underlying == typeof(long)) return el.GetInt64();
            if (underlying == typeof(double)) return el.GetDouble();
            if (underlying == typeof(float)) return el.GetSingle();
            if (underlying == typeof(decimal)) return el.GetDecimal();
            if (underlying == typeof(DateTimeOffset)) return el.GetDateTimeOffset();
            if (underlying == typeof(DateTime)) return el.GetDateTime();
            if (underlying == typeof(byte[])) return el.ValueKind == JsonValueKind.String ? el.GetBytesFromBase64() : null;
            if (underlying.IsEnum)
                return el.ValueKind == JsonValueKind.Number
                    ? Enum.ToObject(underlying, el.GetInt32())
                    : Enum.Parse(underlying, el.GetString() ?? "0");
            // Fallback: über System.Text.Json deserialisieren.
            return JsonSerializer.Deserialize(el.GetRawText(), target);
        }
        catch { return null; }
    }
}
