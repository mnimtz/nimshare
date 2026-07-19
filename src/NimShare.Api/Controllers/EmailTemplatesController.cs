using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Authorize(Policy = "ApiUser")]
[Route("api/v1/email-templates")]
public class EmailTemplatesApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IStringLocalizer<SharedResources> _l;

    public EmailTemplatesApiController(NimShareDbContext db, ICurrentUserService users,
        IStringLocalizer<SharedResources> l)
    {
        _db = db; _users = users; _l = l;
    }

    public record TemplateDto(Guid Id, string Name, string Kind, string Subject,
        string BodyMarkdown, string Locale, bool IsDefault,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public record CreateReq(string Name, string Kind, string Subject, string BodyMarkdown,
        string Locale, bool IsDefault);
    public record UpdateReq(string? Name, string? Subject, string? BodyMarkdown,
        string? Locale, bool? IsDefault);

    private static TemplateDto ToDto(EmailTemplate t) => new(
        t.Id, t.Name, t.Kind.ToString(), t.Subject, t.BodyMarkdown, t.Locale, t.IsDefault,
        t.CreatedAt, t.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> List(string? kind, string? locale, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var q = _db.EmailTemplates.Where(t => t.OwnerUserId == me.Id);
        if (!string.IsNullOrWhiteSpace(kind)
            && Enum.TryParse<EmailTemplateKind>(kind, true, out var k))
            q = q.Where(t => t.Kind == k);
        if (!string.IsNullOrWhiteSpace(locale))
            q = q.Where(t => t.Locale == locale);
        var rows = await q.OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var t = await _db.EmailTemplates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        return t is null ? NotFound() : Ok(ToDto(t));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Subject))
            return BadRequest();
        if (!Enum.TryParse<EmailTemplateKind>(req.Kind, true, out var k))
            return BadRequest();
        var locale = string.IsNullOrWhiteSpace(req.Locale) ? "de" : req.Locale.Trim().ToLowerInvariant();

        var t = new EmailTemplate
        {
            OwnerUserId = me.Id,
            Name = req.Name.Trim(),
            Kind = k,
            Subject = req.Subject.Trim(),
            BodyMarkdown = req.BodyMarkdown ?? "",
            Locale = locale,
            IsDefault = req.IsDefault,
        };
        if (t.IsDefault) await UnsetOtherDefaultsAsync(me.Id, t.Kind, t.Locale, ct);
        _db.EmailTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, ToDto(t));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var t = await _db.EmailTemplates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (t is null) return NotFound();
        if (req.Name is not null) t.Name = req.Name.Trim();
        if (req.Subject is not null) t.Subject = req.Subject.Trim();
        if (req.BodyMarkdown is not null) t.BodyMarkdown = req.BodyMarkdown;
        if (!string.IsNullOrWhiteSpace(req.Locale)) t.Locale = req.Locale.Trim().ToLowerInvariant();
        if (req.IsDefault is bool wantDefault)
        {
            if (wantDefault) await UnsetOtherDefaultsAsync(me.Id, t.Kind, t.Locale, ct, exceptId: t.Id);
            t.IsDefault = wantDefault;
        }
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(t));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var t = await _db.EmailTemplates.SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me.Id, ct);
        if (t is null) return NotFound();
        _db.EmailTemplates.Remove(t);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record PreviewReq(string Subject, string BodyMarkdown);

    /// <summary>Renders a template with dummy placeholder values so the author
    /// can see what a real email would look like. Never sends anything.</summary>
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] PreviewReq req)
    {
        // Dummy values shown in the preview follow the author's locale so the
        // rendered result reads naturally instead of switching languages
        // half-way through the body.
        var ctx = new Dictionary<string, string?>
        {
            ["recipient.name"] = _l["email_tpl.preview.recipient_name"].Value,
            ["recipient.email"] = "alice@example.com",
            ["sender.name"] = _l["email_tpl.preview.sender_name"].Value,
            ["sender.email"] = "bob@example.com",
            ["sender.action"] = _l["email_tpl.preview.sender_action"].Value,
            ["doc.title"] = _l["email_tpl.preview.doc_title"].Value,
            ["doc.name"] = "contract.pdf",
            ["url"] = "https://nimshare.example/sign/xyz",
            ["message"] = _l["email_tpl.preview.message"].Value,
        };
        return Ok(new
        {
            subject = EmailTemplateRenderer.Render(req.Subject ?? "", ctx),
            body = EmailTemplateRenderer.Render(req.BodyMarkdown ?? "", ctx),
            placeholders = EmailTemplateRenderer.AvailablePlaceholders,
        });
    }

    private async Task UnsetOtherDefaultsAsync(Guid ownerId, EmailTemplateKind kind, string locale,
        CancellationToken ct, Guid? exceptId = null)
    {
        var others = await _db.EmailTemplates
            .Where(x => x.OwnerUserId == ownerId && x.Kind == kind && x.Locale == locale && x.IsDefault
                && (exceptId == null || x.Id != exceptId))
            .ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;
    }
}

/// <summary>Razor views: /signatures/templates + /signatures/templates/{id}</summary>
[Authorize(Policy = "WebUser")]
public class EmailTemplatesPageController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public EmailTemplatesPageController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db; _users = users;
    }

    [HttpGet("/signatures/templates")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var rows = await _db.EmailTemplates
            .Where(t => t.OwnerUserId == me.Id)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return View(rows);
    }
}
