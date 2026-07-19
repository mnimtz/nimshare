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
        var msg = showDetails ? (feature?.Error?.Message ?? "Unknown error.") : "";
        var type = showDetails ? (feature?.Error?.GetType().FullName ?? "") : "";
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
