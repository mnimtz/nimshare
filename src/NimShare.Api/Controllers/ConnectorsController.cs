using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Verwaltung der User-Konnektoren (aktuell nur OneDrive Business).
/// Import-Modus: der User verbindet einen externen Cloud-Speicher, browst
/// dort und wählt Items zum cloud-to-cloud Streaming in seinen NimShare-
/// Personal-Bereich. Kein Auto-Sync.
/// </summary>
[ApiController]
[Route("api/v1/connectors")]
[Authorize(Policy = "ApiUser")]
public class ConnectorsApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IConnectorService _svc;

    public ConnectorsApiController(NimShareDbContext db, ICurrentUserService users, IConnectorService svc)
    {
        _db = db; _users = users; _svc = svc;
    }

    public record ConnectorDto(Guid Id, ConnectorType Type, string DisplayName,
        DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool PreserveFolderStructure);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var items = await _db.Connectors
            .Where(c => c.OwnerUserId == me.Id)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConnectorDto(c.Id, c.Type, c.DisplayName, c.CreatedAt, c.LastUsedAt, c.PreserveFolderStructure))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var cn = await _db.Connectors.SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == me.Id, ct);
        if (cn is null) return NotFound();
        _db.Connectors.Remove(cn);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/browse")]
    public async Task<IActionResult> Browse(Guid id, [FromQuery] string? remoteFolderId, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var cn = await _db.Connectors.SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == me.Id, ct);
        if (cn is null) return NotFound();
        try
        {
            var items = await _svc.BrowseAsync(id, remoteFolderId, ct);
            return Ok(items);
        }
        catch (Exception ex)
        {
            return Problem(statusCode: 502, title: "Cloud-Provider unerreichbar", detail: ex.Message);
        }
    }

    public record ImportRequest(List<string> RemoteItemIds, Guid TargetFolderId, bool PreserveStructure);

    [HttpPost("{id:guid}/import")]
    public async Task<IActionResult> Import(Guid id, [FromBody] ImportRequest req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var cn = await _db.Connectors.SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == me.Id, ct);
        if (cn is null) return NotFound();
        if (req.RemoteItemIds is null || req.RemoteItemIds.Count == 0)
            return Problem(statusCode: 422, title: "Keine Items ausgewählt.");
        try
        {
            await _svc.ImportAsync(id, req.RemoteItemIds, req.TargetFolderId, req.PreserveStructure, ct);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(statusCode: 403, title: "Zielordner nicht erlaubt", detail: ex.Message);
        }
        catch (Exception ex)
        {
            return Problem(statusCode: 502, title: "Import fehlgeschlagen", detail: ex.Message);
        }
    }
}

/// <summary>OAuth-Flow für neue Konnektoren. Läuft als Cookie-Session
/// (WebUser-Policy) — die MS-Login-Seite redirected in den Browser des
/// Users zurück, mit noch aktivem NimShare-Auth-Cookie.</summary>
[Authorize(Policy = "WebUser")]
public class ConnectorsAuthController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IConnectorService _svc;

    public ConnectorsAuthController(NimShareDbContext db, ICurrentUserService users, IConnectorService svc)
    {
        _db = db; _users = users; _svc = svc;
    }

    private const string SessionKeyState = "connector.onedrive.state";
    private const string SessionKeyVerifier = "connector.onedrive.verifier";

    /// <summary>User klickt „OneDrive verbinden" → wir bauen die MS-Authorize-URL
    /// mit PKCE + state, merken beides in der Session, redirecten nach MS.</summary>
    [HttpGet("/settings/connectors/onedrive/start")]
    public async Task<IActionResult> Start()
    {
        var me = await _users.GetOrProvisionAsync(User);
        var state = RandomToken(24);
        var verifier = RandomToken(64);
        HttpContext.Session.SetString(SessionKeyState, state);
        HttpContext.Session.SetString(SessionKeyVerifier, verifier);
        var redirect = Url.Action(nameof(Callback), "ConnectorsAuth", null, Request.Scheme)!;
        var url = await _svc.BuildAuthorizeUrlAsync(me.Id, redirect, state, verifier);
        return Redirect(url);
    }

    /// <summary>Microsoft redirected nach dem Consent hierher zurück mit ?code &amp; ?state.
    /// Wir prüfen state gegen Session, tauschen code gegen Tokens ein und leiten
    /// zurück zur Connector-Verwaltung.</summary>
    [HttpGet("/settings/connectors/onedrive/callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, [FromQuery] string? error_description, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            TempData["Error"] = $"OneDrive-Verbindung abgelehnt: {error} — {error_description}";
            return Redirect("/settings/connectors");
        }
        var expectedState = HttpContext.Session.GetString(SessionKeyState);
        var verifier = HttpContext.Session.GetString(SessionKeyVerifier);
        HttpContext.Session.Remove(SessionKeyState);
        HttpContext.Session.Remove(SessionKeyVerifier);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(expectedState) || state != expectedState || string.IsNullOrEmpty(verifier))
        {
            TempData["Error"] = "Verbindungsversuch abgelaufen oder manipuliert. Bitte neu starten.";
            return Redirect("/settings/connectors");
        }
        var me = await _users.GetOrProvisionAsync(User, ct);
        var redirect = Url.Action(nameof(Callback), "ConnectorsAuth", null, Request.Scheme)!;
        try
        {
            var cn = await _svc.CompleteAuthorizeAsync(me.Id, code, redirect, verifier, ct);
            TempData["Notice"] = $"OneDrive verbunden: {cn.DisplayName}";
        }
        catch (Exception ex)
        {
            TempData["Error"] = "OneDrive-Verbindung fehlgeschlagen: " + ex.Message;
        }
        return Redirect("/settings/connectors");
    }

    private static string RandomToken(int nBytes)
    {
        var b = RandomNumberGenerator.GetBytes(nBytes);
        return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

[Authorize(Policy = "WebUser")]
public class ConnectorsPageController : Controller
{
    [HttpGet("/settings/connectors")]
    public IActionResult Index() => View("Index");
}
