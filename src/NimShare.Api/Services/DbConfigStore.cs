using System.Text.Json;

namespace NimShare.Api.Services;

/// <summary>
/// Persistent DB-provider choice — sits on top of appsettings.json /
/// environment variables so an admin can flip the app from Sqlite to Azure SQL
/// via the UI (Settings → Datenbank) without redeploying.
///
/// The file lives on the mounted data volume (same place as the Sqlite DB by
/// default, so it survives App Service restarts / new container images) and
/// contains the connection string in *plain text* — anyone with file access
/// to `/data` already has read access to the Sqlite DB itself, so wrapping
/// this behind DataProtection would just add ceremony without raising the
/// bar. If deployed against SqlServer, the DP keys live in the same folder
/// too, so encrypting with them would introduce a chicken-and-egg on
/// restore.
/// </summary>
public class DbConfigStore
{
    public record DbConfig(string Provider, string ConnectionString, DateTimeOffset UpdatedAt, string? UpdatedBy);

    private readonly string _path;

    public DbConfigStore(IConfiguration config)
    {
        // Prefer an explicit override; otherwise co-locate with the Sqlite DB
        // path so operators don't need a separate volume mount.
        var explicitPath = config["Database:ConfigFilePath"];
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            _path = explicitPath;
            return;
        }
        var sqliteConn = config.GetConnectionString("Default") ?? "";
        var idx = sqliteConn.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var raw = sqliteConn[(idx + "Data Source=".Length)..];
            var end = raw.IndexOf(';');
            var dbPath = end >= 0 ? raw[..end] : raw;
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                _path = Path.Combine(dir, "nimshare-dbconfig.json");
                return;
            }
        }
        _path = Path.Combine(AppContext.BaseDirectory, "nimshare-dbconfig.json");
    }

    /// <summary>Absolute path where the config lives — surfaced on the admin
    /// page so support can find it if they need to hand-edit.</summary>
    public string ConfigPath => _path;

    public DbConfig? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<DbConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            // Corrupt file — better to fall back to env-var config than to
            // crash the whole startup.
            return null;
        }
    }

    public void Save(DbConfig cfg)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        // Atomic write: temp file + rename, so a crash mid-write can never
        // leave a half-written config that would brick the next start.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
