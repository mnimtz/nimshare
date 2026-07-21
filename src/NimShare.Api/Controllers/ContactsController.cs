using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/contacts")]
public class ContactsApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public ContactsApiController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    public record ContactDto(Guid Id, string Email, string Name, string? Company,
        string? Tags, DateTimeOffset? LastUsedAt, int UseCount);
    public record CreateReq(string Email, string Name, string? Company, string? Notes, string? Tags);

    private static ContactDto ToDto(Contact c) => new(
        c.Id, c.Email, c.Name, c.Company, c.Tags, c.LastUsedAt, c.UseCount);

    /// <summary>List + filter + autocomplete. When <c>q</c> is set, does a
    /// case-insensitive OR-match across Name/Email/Company/Tags; used by the
    /// signature wizard's participant autocomplete.</summary>
    [HttpGet]
    public async Task<IActionResult> List(string? q, int limit = 100, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var query = _db.Contacts.Where(c => c.OwnerUserId == me.Id);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(c =>
                c.Email.ToLower().Contains(needle)
                || c.Name.ToLower().Contains(needle)
                || (c.Company != null && c.Company.ToLower().Contains(needle))
                || (c.Tags != null && c.Tags.ToLower().Contains(needle)));
        }
        // Recently used surfaces first when no query; alphabetical when filtered.
        query = string.IsNullOrWhiteSpace(q)
            ? query.OrderByDescending(c => c.LastUsedAt).ThenBy(c => c.Name)
            : query.OrderBy(c => c.Name);
        var rows = await query.Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest();

        var email = req.Email.Trim().ToLowerInvariant();
        var existing = await _db.Contacts.SingleOrDefaultAsync(
            c => c.OwnerUserId == me.Id && c.Email == email, ct);
        if (existing is not null)
        {
            // Idempotent — update the name/company/tags on repeat POST so the
            // wizard's "save on send" flow doesn't spam duplicates.
            existing.Name = req.Name.Trim();
            existing.Company = req.Company?.Trim();
            existing.Notes = req.Notes?.Trim();
            existing.Tags = req.Tags?.Trim();
            await _db.SaveChangesAsync(ct);
            return Ok(ToDto(existing));
        }
        var c = new Contact
        {
            OwnerUserId = me.Id,
            Email = email,
            Name = req.Name.Trim(),
            Company = req.Company?.Trim(),
            Notes = req.Notes?.Trim(),
            Tags = req.Tags?.Trim(),
        };
        _db.Contacts.Add(c);
        await _db.SaveChangesAsync(ct);
        // Location must point at the created resource — was pointing at the
        // list route with a null id, which produced a meaningless header.
        return CreatedAtAction(nameof(Get), new { id = c.Id }, ToDto(c));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.Contacts.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        return c is null ? NotFound() : Ok(ToDto(c));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.Contacts.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (c is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Email))
        {
            var newEmail = req.Email.Trim().ToLowerInvariant();
            // Guard the (OwnerUserId, Email) unique index up front — a raw
            // SaveChanges here would 500 on DbUpdateException and the UI
            // shows a success toast that turns into an error on next click.
            if (!string.Equals(c.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            {
                var taken = await _db.Contacts.AnyAsync(x =>
                    x.OwnerUserId == me.Id && x.Id != c.Id && x.Email == newEmail, ct);
                if (taken) return Conflict(new { error = "email_taken", email = newEmail });
                c.Email = newEmail;
            }
        }
        if (!string.IsNullOrWhiteSpace(req.Name)) c.Name = req.Name.Trim();
        c.Company = req.Company?.Trim();
        c.Notes = req.Notes?.Trim();
        c.Tags = req.Tags?.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(c));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var c = await _db.Contacts.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (c is null) return NotFound();
        _db.Contacts.Remove(c);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// v1.10.74: Öffentliches User-Directory. Marcus's Wunsch — im Adressbuch
    /// sowohl private Kontakte (Contacts) als auch alle NimShare-User als
    /// Read-Only-Einträge sichtbar. iOS zeigt die zwei Listen als getrennte
    /// Segmente ("Meine" / "NimShare-User"). Auth: ApiUser reicht — jeder
    /// eingeloggte User darf die Kollegen-Liste sehen (klassisches Directory-
    /// Verhalten). Sensitive Felder (Role, Quota, LastLoginAt) sind nicht im
    /// DTO — nur was für Signieren/Adressieren nötig ist: Name + E-Mail.
    /// Gelöschte oder deaktivierte User werden ausgeblendet, der User selbst
    /// auch (kein Sinn sich selbst als Kontakt zu haben).
    /// </summary>
    public record DirectoryUserDto(Guid Id, string Name, string Email, bool IsSelf);

    [HttpGet("directory")]
    public async Task<IActionResult> Directory(string? q, int limit = 500, CancellationToken ct = default)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var query = _db.Users.Where(u => u.IsActive && u.Id != me.Id);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(u =>
                u.Email.ToLower().Contains(needle) || u.DisplayName.ToLower().Contains(needle));
        }
        query = query.OrderBy(u => u.DisplayName).ThenBy(u => u.Email);
        var rows = await query.Take(Math.Clamp(limit, 1, 1000))
            .Select(u => new DirectoryUserDto(u.Id, u.DisplayName, u.Email, false))
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>Called by the signature wizard on send to bump LastUsedAt +
    /// UseCount for participants that already exist in the address book, and
    /// silently create ones that don't. Safe to no-op if turned off later.</summary>
    public record BumpReq(string Email, string Name);
    [HttpPost("bump")]
    public async Task<IActionResult> Bump([FromBody] BumpReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@')) return BadRequest();
        var email = req.Email.Trim().ToLowerInvariant();
        var c = await _db.Contacts.SingleOrDefaultAsync(
            x => x.OwnerUserId == me.Id && x.Email == email, ct);
        if (c is null)
        {
            c = new Contact
            {
                OwnerUserId = me.Id,
                Email = email,
                Name = string.IsNullOrWhiteSpace(req.Name) ? email : req.Name.Trim(),
                LastUsedAt = DateTimeOffset.UtcNow,
                UseCount = 1,
            };
            _db.Contacts.Add(c);
        }
        else
        {
            c.LastUsedAt = DateTimeOffset.UtcNow;
            c.UseCount++;
            if (!string.IsNullOrWhiteSpace(req.Name) && string.IsNullOrEmpty(c.Name))
                c.Name = req.Name.Trim();
        }
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(c));
    }
}

[Authorize(Policy = "WebUser")]
public class ContactsPageController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public ContactsPageController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    [HttpGet("/signatures/contacts")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.Contacts
            .Where(c => c.OwnerUserId == me.Id)
            .OrderByDescending(c => c.LastUsedAt).ThenBy(c => c.Name)
            .ToListAsync(ct);
        return View(rows);
    }
}
