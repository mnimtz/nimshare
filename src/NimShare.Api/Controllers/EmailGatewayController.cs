using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class EmailGatewayController : Controller
{
    private readonly IEmailGatewayService _gateway;
    private readonly ICurrentUserService _users;
    private readonly IStringLocalizer<SharedResources> _l;

    public EmailGatewayController(IEmailGatewayService gateway, ICurrentUserService users, IStringLocalizer<SharedResources> l)
    {
        _gateway = gateway;
        _users = users;
        _l = l;
    }

    private async Task<bool> IsAdmin(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return me.Role == UserRole.Admin;
    }

    [HttpGet("/settings/email")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var s = await _gateway.LoadAsync(ct);
        return View(s);
    }

    public record SaveForm(EmailProvider Provider, string FromAddress, string FromName,
        string? SmtpHost, int SmtpPort, bool SmtpUseStartTls, string? SmtpUsername, string? SmtpPassword,
        string? ResendApiKey);

    [HttpPost("/settings/email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromForm] SaveForm form, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var me = await _users.GetOrProvisionAsync(User, ct);
        var incoming = new EmailGatewaySettings
        {
            Provider = form.Provider,
            FromAddress = form.FromAddress,
            FromName = form.FromName,
            SmtpHost = form.SmtpHost,
            SmtpPort = form.SmtpPort == 0 ? 587 : form.SmtpPort,
            SmtpUseStartTls = form.SmtpUseStartTls,
            SmtpUsername = form.SmtpUsername,
        };
        await _gateway.SaveAsync(incoming, form.SmtpPassword, form.ResendApiKey, me.Id, ct);
        TempData["Notice"] = _l["notice.email_saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/email/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(string toEmail, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var (ok, msg) = await _gateway.SendTestAsync(toEmail, ct);
        // If the gateway returned a stock "queued/sent" success, localize it;
        // real error messages from the SMTP/Resend layer stay as-is for
        // diagnostic value.
        TempData[ok ? "Notice" : "Error"] = ok ? _l["notice.email_test_queued"].Value : msg;
        return RedirectToAction(nameof(Index));
    }
}
