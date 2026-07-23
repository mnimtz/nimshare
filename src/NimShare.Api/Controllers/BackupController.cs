using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.10.116 — Admin-Backup/Restore der kompletten Datenbank (ohne Blob-
/// Inhalte). Nur für Admins. Restore ist destruktiv → doppelte Bestätigung
/// im UI + Server verlangt exaktes „RESTORE".
/// </summary>
[Authorize(Policy = "WebUser")]
public class BackupController : Controller
{
    private readonly IBackupService _backup;
    private readonly ICurrentUserService _users;
    private readonly ILogger<BackupController> _log;

    public BackupController(IBackupService backup, ICurrentUserService users, ILogger<BackupController> log)
    {
        _backup = backup; _users = users; _log = log;
    }

    private async Task<bool> IsAdminAsync(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return me.Role == NimShare.Core.Entities.UserRole.Admin;
    }

    // ── Settings-Seite ───────────────────────────────────────────────────────
    [HttpGet("/settings/backup")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        return View();
    }

    // ── Export (Download) ────────────────────────────────────────────────────
    [HttpGet("/settings/backup/export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        var json = await _backup.ExportAsync(ct);
        var bytes = Encoding.UTF8.GetBytes(json);
        var name = $"nimshare-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
        _log.LogWarning("Admin exported full DB backup ({Bytes} bytes).", bytes.Length);
        return File(bytes, "application/json", name);
    }

    // ── Restore (Upload) ─────────────────────────────────────────────────────
    [HttpPost("/settings/backup/restore")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(200_000_000)] // 200 MB — DB-JSON ohne Blobs bleibt klein
    public async Task<IActionResult> Restore(IFormFile? file, string confirm, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct)) return Forbid();
        if (!string.Equals(confirm?.Trim(), "RESTORE", StringComparison.Ordinal))
        {
            TempData["BackupError"] = "Bitte zur Bestätigung exakt RESTORE eingeben.";
            return RedirectToAction(nameof(Index));
        }
        if (file is null || file.Length == 0)
        {
            TempData["BackupError"] = "Keine Backup-Datei ausgewählt.";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            var json = await reader.ReadToEndAsync(ct);
            // v1.10.145: Admin-Email an Restore reichen → Self-Lockout-Schutz
            // im Service bricht ab, wenn der handelnde Admin im Backup fehlt.
            var me = await _users.GetOrProvisionAsync(User, ct);
            var (tables, rows) = await _backup.ImportAsync(json, me.Email, ct);
            _log.LogWarning("Admin RESTORED DB from backup: {Tables} tables, {Rows} rows.", tables, rows);
            TempData["BackupOk"] = $"Wiederhergestellt: {rows} Zeilen aus {tables} Tabellen. Bitte neu anmelden.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DB restore failed.");
            // v1.10.145: echte Ursache entfalten (DbUpdateException versteckt
            // sonst z. B. „Cannot insert explicit value for identity column").
            TempData["BackupError"] = "Wiederherstellung fehlgeschlagen: " + Flatten(ex);
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>v1.10.145 — Entfaltet die komplette InnerException-Kette.</summary>
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
}
