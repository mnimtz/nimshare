using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class DatabaseSettingsPageController : Controller
{
    private readonly ICurrentUserService _users;

    public DatabaseSettingsPageController(ICurrentUserService users) { _users = users; }

    [HttpGet("/settings/database")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Use the DB role (single source of truth) so a role change takes
        // effect immediately — cookie-claim IsInRole("Admin") would go stale
        // until sign-out and match the API's DB-based check.
        var me = await _users.GetOrProvisionAsync(User, ct);
        return me.Role == NimShare.Core.Entities.UserRole.Admin ? View("Index") : Forbid();
    }
}

[ApiController]
[Authorize(Policy = "WebUser")]
[Route("api/v1/settings/database")]
public class DatabaseSettingsApiController : ControllerBase
{
    private readonly IDbMigrationService _mig;
    private readonly DbConfigStore _store;
    private readonly NimShareDbContext _db;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ICurrentUserService _users;
    private readonly IConfiguration _config;

    public DatabaseSettingsApiController(IDbMigrationService mig, DbConfigStore store,
        NimShareDbContext db, IHostApplicationLifetime lifetime, ICurrentUserService users,
        IConfiguration config)
    {
        _mig = mig; _store = store; _db = db; _lifetime = lifetime; _users = users; _config = config;
    }

    private async Task<bool> RequireAdmin(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return me.Role == NimShare.Core.Entities.UserRole.Admin;
    }

    public record StatusDto(string ActiveProvider, string ConfigPath, bool ConfigOverride,
        string? RedactedConnection, long? SqliteFileBytes, int? UserCount, string BuildVersion);

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var cfg = _store.Load();
        var provider = cfg?.Provider ?? _config["Database:Provider"] ?? "Sqlite";
        var conn = cfg?.ConnectionString ?? _config.GetConnectionString("Default") ?? "";
        long? size = null;
        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var idx = conn.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var raw = conn[(idx + "Data Source=".Length)..];
                var end = raw.IndexOf(';');
                var path = end >= 0 ? raw[..end] : raw;
                if (System.IO.File.Exists(path)) size = new FileInfo(path).Length;
            }
        }
        int? userCount = null;
        try { userCount = await _db.Users.CountAsync(ct); } catch { }
        return Ok(new StatusDto(provider, _store.ConfigPath, cfg is not null,
            Redact(conn), size, userCount, BuildInfo.Version));
    }

    public record TestReq(string? Server, string? Username, string? Password, string? Database, string? RawConnectionString);

    /// <summary>Assemble a connection string from either the individual fields
    /// or a raw one, then verify it reaches the server. <paramref name="Database"/>
    /// is optional — a blank value hits the master DB, which is what we need to
    /// create a new one.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestReq req, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var conn = BuildConnectionString(req, "master");
        if (conn is null) return Problem(statusCode: 422, title: "Server + user + password required.");
        var result = await _mig.TestAsync(conn, ct);
        return Ok(new { ok = result.Ok, serverVersion = result.ServerVersion, error = result.Error });
    }

    public record CreateReq(string? Server, string? Username, string? Password, string? RawConnectionString, string DatabaseName);

    /// <summary>Uses the same credentials to hit master and issue
    /// <c>CREATE DATABASE …</c> for the target DB name. No-op if it exists.</summary>
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var conn = BuildConnectionString(new TestReq(req.Server, req.Username, req.Password, null, req.RawConnectionString), "master");
        if (conn is null) return Problem(statusCode: 422, title: "Server + user + password required.");
        var result = await _mig.CreateDatabaseIfMissingAsync(conn, req.DatabaseName, ct);
        return Ok(new { ok = result.Ok, createdNew = result.CreatedNew, error = result.Error });
    }

    public record SwitchReq(string? Server, string? Username, string? Password, string? RawConnectionString,
        string DatabaseName, bool CopyExistingData);

    /// <summary>Full "switch" — ensure schema on target, optionally copy every
    /// row over, persist the new connection string in the config file, then
    /// tell the host to stop. Azure App Service restarts the container, and
    /// the next boot picks up the new provider from the config file.</summary>
    [HttpPost("switch")]
    public async Task<IActionResult> Switch([FromBody] SwitchReq req, CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        var conn = BuildConnectionString(new TestReq(req.Server, req.Username, req.Password, req.DatabaseName, req.RawConnectionString), req.DatabaseName);
        if (conn is null) return Problem(statusCode: 422, title: "Server + user + password required.");
        // 1. Schema
        var schema = await _mig.EnsureSchemaAsync(conn, ct);
        if (!schema.Ok) return Problem(statusCode: 500, title: "Schema creation failed", detail: schema.Error);
        // 2. Optional data copy
        CopyResult? copy = null;
        if (req.CopyExistingData)
        {
            copy = await _mig.CopyDataAsync(conn, null, ct);
            if (!copy.Ok) return Problem(statusCode: 500, title: "Data copy failed", detail: copy.Error);
        }
        // 3. Persist config
        var me = await _users.GetOrProvisionAsync(User, ct);
        _store.Save(new DbConfigStore.DbConfig("SqlServer", conn, DateTimeOffset.UtcNow, me.Email));
        // 4. Restart — fire-and-forget so the response reaches the browser
        // first. 5 s gives the slowest mobile connection a comfortable window
        // to finish flushing the JSON response before Kestrel goes down.
        // Earlier 1 s cut off users on slow uplinks with ERR_CONNECTION_RESET
        // instead of the success banner.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            _lifetime.StopApplication();
        });
        return Ok(new { ok = true, schemaMs = schema.Duration.TotalMilliseconds,
            copiedTables = copy?.TablesProcessed ?? 0, copiedRows = copy?.RowsCopied ?? 0 });
    }

    [HttpPost("revert-to-sqlite")]
    public async Task<IActionResult> RevertToSqlite(CancellationToken ct)
    {
        if (!await RequireAdmin(ct)) return Forbid();
        // Clearing the config file makes the next start fall back to
        // appsettings.json / env vars, which point at the local Sqlite DB.
        // Existing data on the Azure SQL DB stays there; the admin can flip
        // back at any time.
        _store.Clear();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            _lifetime.StopApplication();
        });
        return Ok(new { ok = true });
    }

    // ── helpers ──────────────────────────────────────────────────────
    private static string? BuildConnectionString(TestReq req, string? database)
    {
        if (!string.IsNullOrWhiteSpace(req.RawConnectionString)) return req.RawConnectionString;
        if (string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return null;
        var b = new SqlConnectionStringBuilder
        {
            DataSource = req.Server!.Trim(),
            UserID = req.Username!.Trim(),
            Password = req.Password!,
            InitialCatalog = string.IsNullOrWhiteSpace(database) ? "master" : database!.Trim(),
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = 15,
        };
        return b.ConnectionString;
    }

    private static string Redact(string conn)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(conn);
            if (!string.IsNullOrEmpty(b.Password)) b.Password = "***";
            return b.ConnectionString;
        }
        catch { return "(non-SQL connection string)"; }
    }
}
