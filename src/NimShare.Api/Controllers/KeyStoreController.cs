using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.11.22 — Key-Store: Kunden + Lizenzschlüssel-Verwaltung, Marcus's
/// Anfrage ("Kunden mit Email, Art des Keys, Key, Test-Zeitraum eintragen").
/// Normale User sehen nur ihre eigenen Einträge, Admins sehen alle (über die
/// gesamte Instanz — analog dem Admin-Moderationsrecht bei Links/Public-
/// Content). Der Klartext-Key wird nie in DB oder Response-DTO abgelegt —
/// gleiches IDataProtector-Pattern wie ShareLink.SerialNumberEncrypted.
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/keystore")]
public class KeyStoreController : ControllerBase
{
    public const string ProtectorPurpose = "NimShare.KeyStore.v1";

    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IDataProtector _protector;
    private readonly IBlobStorageService _blobs;

    public KeyStoreController(NimShareDbContext db, ICurrentUserService users, IDataProtectionProvider dpp, IBlobStorageService blobs)
    {
        _db = db;
        _users = users;
        _protector = dpp.CreateProtector(ProtectorPurpose);
        _blobs = blobs;
    }

    public record KeyStoreEntryDto(
        Guid Id, string CustomerName, string? CustomerEmail, string? CustomerEmailDomain,
        string KeyType, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, string? Notes,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
        // v1.11.22: Owner-Name nur befüllt, wenn ein Admin fremde Einträge sieht
        // (analog LinkDto.OwnerName-Pattern) — für eigene Einträge null.
        string? OwnerName, bool IsOwnedByMe);

    private KeyStoreEntryDto ToDto(KeyStoreEntry e, Guid meId) => new(
        e.Id, e.CustomerName, e.CustomerEmail, e.CustomerEmailDomain, e.KeyType,
        e.ValidFrom, e.ValidUntil, e.Notes, e.CreatedAt, e.UpdatedAt,
        OwnerName: e.OwnerUserId != meId ? e.OwnerUser?.DisplayName : null,
        IsOwnedByMe: e.OwnerUserId == meId);

    /// <summary>Liste + Suche. Normale User: nur eigene Einträge. Admins:
    /// alle Einträge instanzweit (mit Owner-Name, damit klar ist wessen
    /// Kunde das ist).</summary>
    [HttpGet]
    public async Task<IActionResult> List(string? q, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var query = _db.KeyStoreEntries.Include(x => x.OwnerUser).AsQueryable();
        query = me.Role == UserRole.Admin ? query : query.Where(x => x.OwnerUserId == me.Id);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(x =>
                x.CustomerName.ToLower().Contains(needle)
                || (x.CustomerEmail != null && x.CustomerEmail.ToLower().Contains(needle))
                || (x.CustomerEmailDomain != null && x.CustomerEmailDomain.ToLower().Contains(needle))
                || x.KeyType.ToLower().Contains(needle)
                || (x.Notes != null && x.Notes.ToLower().Contains(needle)));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(x => ToDto(x, me.Id)));
    }

    public record CreateReq(string CustomerName, string? CustomerEmail, string? CustomerEmailDomain,
        string KeyType, string KeyValue, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, string? Notes);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.CustomerName))
            return Problem(statusCode: 422, title: "Customer name is required.");
        if (string.IsNullOrWhiteSpace(req.CustomerEmail) && string.IsNullOrWhiteSpace(req.CustomerEmailDomain))
            return Problem(statusCode: 422, title: "Either a customer email or an email domain is required.");
        if (string.IsNullOrWhiteSpace(req.KeyType))
            return Problem(statusCode: 422, title: "Key type is required.");
        if (string.IsNullOrWhiteSpace(req.KeyValue))
            return Problem(statusCode: 422, title: "Key value is required.");
        if (req.KeyValue.Length > 500)
            return Problem(statusCode: 422, title: "Key value too long (max 500 characters).");

        var entry = new KeyStoreEntry
        {
            OwnerUserId = me.Id,
            CustomerName = req.CustomerName.Trim(),
            CustomerEmail = string.IsNullOrWhiteSpace(req.CustomerEmail) ? null : req.CustomerEmail.Trim().ToLowerInvariant(),
            CustomerEmailDomain = string.IsNullOrWhiteSpace(req.CustomerEmailDomain) ? null : req.CustomerEmailDomain.Trim().ToLowerInvariant().TrimStart('@'),
            KeyType = req.KeyType.Trim(),
            KeyValueEncrypted = _protector.Protect(req.KeyValue.Trim()),
            ValidFrom = req.ValidFrom,
            ValidUntil = req.ValidUntil,
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
        };
        _db.KeyStoreEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
        entry.OwnerUser = me;
        return CreatedAtAction(nameof(Get), new { id = entry.Id }, ToDto(entry, me.Id));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var e = await FindAsync(id, me, ct);
        return e is null ? NotFound() : Ok(ToDto(e, me.Id));
    }

    /// <summary>Klartext-Reveal des Keys — eigener Endpoint statt im DTO,
    /// analog dem Serial-Number-Reveal bei Share-Links (nie unaufgefordert
    /// im rohen JSON der Liste).</summary>
    public record RevealResponse(string KeyValue);
    [HttpGet("{id:guid}/reveal")]
    public async Task<IActionResult> Reveal(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var e = await FindAsync(id, me, ct);
        if (e is null) return NotFound();
        try { return Ok(new RevealResponse(_protector.Unprotect(e.KeyValueEncrypted))); }
        catch (System.Security.Cryptography.CryptographicException)
        { return Problem(statusCode: 500, title: "Key could not be decrypted"); }
    }

    public record UpdateReq(string? CustomerName, string? CustomerEmail, string? CustomerEmailDomain,
        string? KeyType, string? KeyValue, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, string? Notes,
        bool ClearValidFrom = false, bool ClearValidUntil = false);

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var e = await FindAsync(id, me, ct);
        if (e is null) return NotFound();
        if (req.KeyValue is { Length: > 500 })
            return Problem(statusCode: 422, title: "Key value too long (max 500 characters).");

        if (!string.IsNullOrWhiteSpace(req.CustomerName)) e.CustomerName = req.CustomerName.Trim();
        if (req.CustomerEmail is not null)
            e.CustomerEmail = string.IsNullOrWhiteSpace(req.CustomerEmail) ? null : req.CustomerEmail.Trim().ToLowerInvariant();
        if (req.CustomerEmailDomain is not null)
            e.CustomerEmailDomain = string.IsNullOrWhiteSpace(req.CustomerEmailDomain) ? null : req.CustomerEmailDomain.Trim().ToLowerInvariant().TrimStart('@');
        if (!string.IsNullOrWhiteSpace(req.KeyType)) e.KeyType = req.KeyType.Trim();
        if (!string.IsNullOrWhiteSpace(req.KeyValue)) e.KeyValueEncrypted = _protector.Protect(req.KeyValue.Trim());
        if (req.ClearValidFrom) e.ValidFrom = null; else if (req.ValidFrom is not null) e.ValidFrom = req.ValidFrom;
        if (req.ClearValidUntil) e.ValidUntil = null; else if (req.ValidUntil is not null) e.ValidUntil = req.ValidUntil;
        if (req.Notes is not null) e.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        // Zwingt weiter unten mind. eine Zuordnung — sonst wäre der Eintrag
        // nie mehr über einen Share-Link-Reveal auffindbar.
        if (string.IsNullOrWhiteSpace(e.CustomerEmail) && string.IsNullOrWhiteSpace(e.CustomerEmailDomain))
            return Problem(statusCode: 422, title: "Either a customer email or an email domain is required.");
        e.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(e, me.Id));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var e = await FindAsync(id, me, ct);
        if (e is null) return NotFound();
        _db.KeyStoreEntries.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record BulkDeleteReq(List<Guid> Ids);
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (req.Ids is null || req.Ids.Count == 0) return Ok(new { deleted = 0 });
        var query = _db.KeyStoreEntries.Where(x => req.Ids.Contains(x.Id));
        query = me.Role == UserRole.Admin ? query : query.Where(x => x.OwnerUserId == me.Id);
        var rows = await query.ToListAsync(ct);
        _db.KeyStoreEntries.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = rows.Count });
    }

    private Task<KeyStoreEntry?> FindAsync(Guid id, User me, CancellationToken ct)
    {
        var query = _db.KeyStoreEntries.Include(x => x.OwnerUser).Where(x => x.Id == id);
        query = me.Role == UserRole.Admin ? query : query.Where(x => x.OwnerUserId == me.Id);
        return query.SingleOrDefaultAsync(ct);
    }

    // ── v1.11.37 — Key-Store-Dokumente ──────────────────────────────────────
    // Pro-User verwaltete Dokumente (PDF-Upload oder fester Link), gebunden an
    // eine Auswahl von Key-Typen. Erscheinen auf der öffentlichen Landing
    // eines KeyStoreMode-Links, sobald der Ersteller die "Dokumentation"-
    // Checkbox aktiviert UND der beim Reveal ermittelte KeyStoreEntry.KeyType
    // zu einem der hier ausgewählten Typen passt (siehe LinksController).

    public record KeyStoreDocumentDto(Guid Id, string Label, bool IsFile, string? FileName, string? Url,
        List<string> KeyTypes, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
        string? OwnerName, bool IsOwnedByMe);

    private KeyStoreDocumentDto ToDocDto(KeyStoreDocument d, Guid meId) => new(
        d.Id, d.Label, d.IsFile, d.FileName, d.Url, d.KeyTypes.ToList(), d.CreatedAt, d.UpdatedAt,
        OwnerName: d.OwnerUserId != meId ? d.OwnerUser?.DisplayName : null,
        IsOwnedByMe: d.OwnerUserId == meId);

    [HttpGet("documents")]
    public async Task<IActionResult> ListDocuments(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var query = _db.KeyStoreDocuments.Include(x => x.OwnerUser).AsQueryable();
        query = me.Role == UserRole.Admin ? query : query.Where(x => x.OwnerUserId == me.Id);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(x => ToDocDto(x, me.Id)));
    }

    public record CreateLinkDocReq(string Label, string Url, List<string> KeyTypes);

    [HttpPost("documents/link")]
    public async Task<IActionResult> CreateLinkDocument([FromBody] CreateLinkDocReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Label))
            return Problem(statusCode: 422, title: "Label is required.");
        if (string.IsNullOrWhiteSpace(req.Url)
            || !(Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            return Problem(statusCode: 422, title: "A valid http(s) URL is required.");
        if (req.KeyTypes is null || req.KeyTypes.Count == 0)
            return Problem(statusCode: 422, title: "At least one key type is required.");

        var doc = new KeyStoreDocument
        {
            OwnerUserId = me.Id,
            Label = req.Label.Trim(),
            Url = req.Url.Trim(),
            KeyTypesCsv = string.Join(',', req.KeyTypes.Select(t => t.Trim()).Where(t => t.Length > 0)),
        };
        _db.KeyStoreDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);
        doc.OwnerUser = me;
        return CreatedAtAction(nameof(ListDocuments), null, ToDocDto(doc, me.Id));
    }

    [HttpPost("documents/upload")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> UploadDocument([FromForm] string label, [FromForm] string keyTypes,
        [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(label))
            return Problem(statusCode: 422, title: "Label is required.");
        var types = (keyTypes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Length == 0)
            return Problem(statusCode: 422, title: "At least one key type is required.");
        if (file is null || file.Length == 0)
            return Problem(statusCode: 422, title: "A file is required.");
        if (file.Length > 20 * 1024 * 1024)
            return Problem(statusCode: 422, title: "File is too large (max 20 MB).");
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

        var doc = new KeyStoreDocument
        {
            OwnerUserId = me.Id,
            Label = label.Trim(),
            FileName = file.FileName,
            KeyTypesCsv = string.Join(',', types),
        };
        var path = $"keystore-docs/{me.Id:N}/{doc.Id:N}/{file.FileName}";
        var ticket = _blobs.CreateUploadTicket(path);
        using (var http = new HttpClient())
        using (var content = new StreamContent(file.OpenReadStream()))
        {
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.Add("x-ms-blob-content-type", contentType);
            var resp = await http.PutAsync(ticket.UploadUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
                return Problem(statusCode: 502, title: "Upload to storage failed.");
        }
        doc.BlobPath = path;
        _db.KeyStoreDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);
        doc.OwnerUser = me;
        return CreatedAtAction(nameof(ListDocuments), null, ToDocDto(doc, me.Id));
    }

    public record UpdateDocReq(string? Label, string? Url, List<string>? KeyTypes);

    [HttpPatch("documents/{id:guid}")]
    public async Task<IActionResult> UpdateDocument(Guid id, [FromBody] UpdateDocReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var d = await FindDocAsync(id, me, ct);
        if (d is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Label)) d.Label = req.Label.Trim();
        if (req.Url is not null && !d.IsFile)
        {
            if (string.IsNullOrWhiteSpace(req.Url)
                || !(Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
                return Problem(statusCode: 422, title: "A valid http(s) URL is required.");
            d.Url = req.Url.Trim();
        }
        if (req.KeyTypes is { Count: > 0 })
            d.KeyTypesCsv = string.Join(',', req.KeyTypes.Select(t => t.Trim()).Where(t => t.Length > 0));
        d.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDocDto(d, me.Id));
    }

    /// <summary>Ersetzt die Datei eines File-Dokuments durch eine neuere
    /// Version — Marcus's Wunsch "leicht erneuern". Label/Key-Typen bleiben,
    /// nur BlobPath/FileName werden getauscht, der alte Blob wird gelöscht.</summary>
    [HttpPost("documents/{id:guid}/replace-file")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> ReplaceDocumentFile(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var d = await FindDocAsync(id, me, ct);
        if (d is null) return NotFound();
        if (!d.IsFile) return Problem(statusCode: 422, title: "This document is a link, not a file.");
        if (file is null || file.Length == 0) return Problem(statusCode: 422, title: "A file is required.");
        if (file.Length > 20 * 1024 * 1024) return Problem(statusCode: 422, title: "File is too large (max 20 MB).");
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

        var oldPath = d.BlobPath;
        var newPath = $"keystore-docs/{d.OwnerUserId:N}/{d.Id:N}/{file.FileName}";
        var ticket = _blobs.CreateUploadTicket(newPath);
        using (var http = new HttpClient())
        using (var content = new StreamContent(file.OpenReadStream()))
        {
            content.Headers.Add("x-ms-blob-type", "BlockBlob");
            content.Headers.Add("x-ms-blob-content-type", contentType);
            var resp = await http.PutAsync(ticket.UploadUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
                return Problem(statusCode: 502, title: "Upload to storage failed.");
        }
        d.BlobPath = newPath;
        d.FileName = file.FileName;
        d.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (!string.IsNullOrEmpty(oldPath) && oldPath != newPath)
        {
            try { await _blobs.DeleteAsync(oldPath, ct); } catch { /* orphaned bytes, not fatal */ }
        }
        return Ok(ToDocDto(d, me.Id));
    }

    [HttpDelete("documents/{id:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var d = await FindDocAsync(id, me, ct);
        if (d is null) return NotFound();
        if (d.IsFile)
        {
            try { await _blobs.DeleteAsync(d.BlobPath!, ct); } catch { /* orphaned bytes, not fatal */ }
        }
        _db.KeyStoreDocuments.Remove(d);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<KeyStoreDocument?> FindDocAsync(Guid id, User me, CancellationToken ct)
    {
        var query = _db.KeyStoreDocuments.Where(x => x.Id == id);
        query = me.Role == UserRole.Admin ? query : query.Where(x => x.OwnerUserId == me.Id);
        return query.SingleOrDefaultAsync(ct);
    }
}

[Authorize(Policy = "WebUser")]
public class KeyStorePageController : Controller
{
    [HttpGet("/keystore")]
    public IActionResult Index() => View();

    // v1.11.43 — eigener Nav-Punkt für Dokumentation (Marcus's Wunsch,
    // analog Signaturen: Übersicht/Email-Vorlagen/Adressbuch als getrennte
    // Unterseiten statt alles auf einer Seite).
    [HttpGet("/keystore/documents")]
    public IActionResult Documents() => View();
}
