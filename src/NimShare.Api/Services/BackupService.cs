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

    public async Task<(int Tables, int Rows)> ImportAsync(string json, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tables", out var tablesEl))
            throw new InvalidOperationException("Backup-Datei hat kein 'tables'-Objekt.");

        var ordered = OrderedEntityTypes();
        var byName = ordered.ToDictionary(t => t.Name);

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
