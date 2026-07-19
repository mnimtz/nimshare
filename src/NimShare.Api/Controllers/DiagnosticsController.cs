using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;

namespace NimShare.Api.Controllers;

public class DiagnosticsController : Controller
{
    private readonly NimShareDbContext _db;

    public DiagnosticsController(NimShareDbContext db) { _db = db; }

    /// <summary>Global error page. Exception details are ONLY shown to signed-in
    /// admins — anonymous visitors and normal users get a generic apology.
    /// (Earlier v1.7.9 leaked the raw Message + Type to any caller — that leak
    /// is closed here.)</summary>
    [Route("/error")]
    [AllowAnonymous]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerFeature>();
        var showDetails = User?.IsInRole("Admin") == true;
        var err = feature?.Error;
        var msg = showDetails ? (err?.Message ?? "Unknown error.") : "";
        var type = showDetails ? (err?.GetType().FullName ?? "") : "";

        // Detect "database file temporarily unreachable" (Azure Files hiccup
        // during app-service restart) and downgrade to a 503 with a friendlier
        // page + auto-refresh. That turns a scary "site is dead" into a "try
        // again in a moment" — which is exactly what's happening.
        var isDbTransient = IsTransientDbError(err);
        if (isDbTransient)
        {
            Response.StatusCode = 503;
            Response.Headers["Retry-After"] = "15";
            return Content(
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>NimShare — Temporarily unavailable</title>" +
                "<meta http-equiv=\"refresh\" content=\"15\"/>" +
                "<style>body{font-family:system-ui,sans-serif;max-width:640px;margin:60px auto;padding:0 20px;color:#0f1f3d;text-align:center}" +
                "h1{color:#e08a00;font-size:1.6rem}p{color:#556}a{color:#0060df}</style></head><body>" +
                "<h1>🌩 Kurz nicht erreichbar</h1>" +
                "<p>Die Datenbank ist gerade nicht ansprechbar. Wir versuchen es automatisch alle 15&nbsp;Sekunden neu.</p>" +
                "<p>Das passiert typischerweise während eines Deploys — meist ist NimShare in einer Minute wieder da.</p>" +
                (showDetails ? $"<pre style=\"background:#f5f5f5;padding:12px;border-radius:6px;text-align:left;font-size:12px\">{System.Net.WebUtility.HtmlEncode(type)}\n\n{System.Net.WebUtility.HtmlEncode(msg)}</pre>" : "") +
                "<p><small>Version: v" + BuildInfo.Version + "</small></p>" +
                "</body></html>",
                "text/html; charset=utf-8");
        }

        Response.StatusCode = 500;
        var detailsBlock = showDetails
            ? $"<h2>Details (admin only)</h2><pre>{System.Net.WebUtility.HtmlEncode(type)}\n\n{System.Net.WebUtility.HtmlEncode(msg)}</pre><p><a href=\"/diagnostics\">→ /diagnostics</a></p>"
            : "";
        return Content(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>NimShare — Error</title>" +
            "<style>body{font-family:system-ui,sans-serif;max-width:720px;margin:40px auto;padding:0 20px;color:#0f1f3d}" +
            "h1{color:#c22}pre{background:#f5f5f5;padding:12px;border-radius:6px;overflow-x:auto;font-size:13px}" +
            "a{color:#0060df}</style></head><body>" +
            "<h1>Server error</h1>" +
            "<p>Something went wrong on the server. Please try again or contact your administrator.</p>" +
            detailsBlock +
            "<p>Version: v" + BuildInfo.Version + "</p>" +
            "<p><a href=\"/\">← Back to home</a></p>" +
            "</body></html>",
            "text/html; charset=utf-8");
    }

    private static bool IsTransientDbError(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is Microsoft.Data.Sqlite.SqliteException sx)
            {
                // 14 = SQLITE_CANTOPEN (Azure Files remounting)
                // 5  = SQLITE_BUSY   (writer holds the lock; will clear)
                // 8  = SQLITE_READONLY (mount temporarily read-only)
                if (sx.SqliteErrorCode is 14 or 5 or 8) return true;
                var lo = (sx.Message ?? "").ToLowerInvariant();
                if (lo.Contains("unable to open") || lo.Contains("locked") || lo.Contains("busy"))
                    return true;
            }
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>Admin diagnostic endpoint. Shows applied vs pending migrations,
    /// last startup errors, and a few basic health facts. Only accessible to
    /// signed-in admins.</summary>
    [Route("/diagnostics")]
    [Authorize(Policy = "WebUser")]
    public async Task<IActionResult> Diagnose(CancellationToken ct)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        var applied = (await _db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = (await _db.Database.GetPendingMigrationsAsync(ct)).ToList();
        var canConnect = await _db.Database.CanConnectAsync(ct);
        return Json(new
        {
            version = BuildInfo.Version,
            dbCanConnect = canConnect,
            appliedMigrations = applied,
            pendingMigrations = pending,
            startupErrors = StartupState.Errors,
            emailDeliveryLog = NimShare.Api.Services.EmailDeliveryLog.Snapshot(),
        });
    }
}

/// <summary>Captured startup issues (e.g. failed migrations) for the
/// /diagnostics view. Written to by Program.cs.</summary>
public static class StartupState
{
    public static List<string> Errors { get; } = new();
}
