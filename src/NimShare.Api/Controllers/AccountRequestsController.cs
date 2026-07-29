using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Public "request an account" flow from the login page, and the admin-side
/// approve/reject actions on /settings/users. Mirrors InvitationsController:
/// approving a request creates an Invitation (recipient sets their own
/// password via emailed link) rather than the admin picking a password.
/// </summary>
public class AccountRequestsController : Controller
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "en", "de", "fr", "it", "es", "nl" };

    private readonly NimShareDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _users;
    private readonly IEmailGatewayService _gateway;
    private readonly IStringLocalizer<SharedResources> _l;
    private readonly IStringLocalizerFactory _localizerFactory;

    public AccountRequestsController(NimShareDbContext db, IPasswordHasher hasher, ICurrentUserService users,
        IEmailGatewayService gateway, IStringLocalizer<SharedResources> l, IStringLocalizerFactory localizerFactory)
    {
        _db = db;
        _hasher = hasher;
        _users = users;
        _gateway = gateway;
        _l = l;
        _localizerFactory = localizerFactory;
    }

    private static T WithCulture<T>(string language, Func<T> body)
    {
        var prev = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(
                SupportedLanguages.Contains(language ?? "") ? language! : "en");
            return body();
        }
        catch (CultureNotFoundException)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            return body();
        }
        finally { CultureInfo.CurrentUICulture = prev; }
    }

    /// <summary>Same branded shell as InvitationsController's mail; `expiryNoteHtml`
    /// is optional (null omits that line) since only the approve-mail needs it.</summary>
    private static string BuildNoticeHtml(string introHtml, string ctaLabel, string url, string? expiryNoteHtml = null)
    {
        var encodedUrl = System.Net.WebUtility.HtmlEncode(url);
        var expiryLine = expiryNoteHtml is null ? "" :
            $"""<p style="margin:0 0 4px;font-size:13px;line-height:1.6;color:#6b7280;">{expiryNoteHtml}</p>""";
        return $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f2f4f7;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f2f4f7;padding:32px 16px;">
            <tr><td align="center">
              <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;width:100%;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,.08);">
                <tr><td style="background:linear-gradient(135deg,#00A0FB 0%,#00EB86 100%);padding:28px 32px;">
                  <span style="font-size:20px;font-weight:700;color:#ffffff;letter-spacing:.2px;">NimShare</span>
                </td></tr>
                <tr><td style="padding:32px 32px 8px;">
                  <p style="margin:0 0 20px;font-size:15px;line-height:1.6;color:#231F20;">{{introHtml}}</p>
                  <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                    <tr><td style="border-radius:8px;background:#00A0FB;">
                      <a href="{{encodedUrl}}" style="display:inline-block;padding:13px 28px;font-size:15px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:8px;">{{ctaLabel}}</a>
                    </td></tr>
                  </table>
                  {{expiryLine}}
                  <p style="margin:0 0 24px;font-size:12px;line-height:1.6;color:#9ca3af;word-break:break-all;">{{encodedUrl}}</p>
                </td></tr>
                <tr><td style="padding:16px 32px 28px;border-top:1px solid #eef0f3;">
                  <p style="margin:0;font-size:12px;color:#9ca3af;">— NimShare</p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body></html>
        """;
    }

    // ── Visitor: submit a request ──────────────────────────────────────────

    [AllowAnonymous]
    [HttpGet("/request-account")]
    public IActionResult RequestAccount() => View(new RequestAccountViewModel());

    [AllowAnonymous]
    [HttpPost("/request-account")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestAccountPost(RequestAccountViewModel vm, CancellationToken ct)
    {
        // Honeypot: real visitors never fill this hidden field. Pretend success
        // so bots don't learn the check exists.
        if (!string.IsNullOrWhiteSpace(vm.Website)) return View("RequestAccountSent");

        var email = (vm.Email ?? "").Trim().ToLowerInvariant();
        var displayName = (vm.DisplayName ?? "").Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@') || string.IsNullOrEmpty(displayName))
        {
            ModelState.AddModelError("", _l["err.invalid_request"].Value);
            return View("RequestAccount", vm);
        }

        // Always show the same success page regardless of outcome below —
        // don't let the response reveal whether an email is already
        // registered or already has a pending request.
        var alreadyUser = await _db.Users.AnyAsync(u => u.Email == email, ct);
        var alreadyPending = await _db.AccountRequests.AnyAsync(
            r => r.Email == email && r.Status == AccountRequestStatus.Pending, ct);

        if (!alreadyUser && !alreadyPending)
        {
            var request = new AccountRequest
            {
                Email = email,
                DisplayName = displayName,
                Message = string.IsNullOrWhiteSpace(vm.Message) ? null : vm.Message.Trim(),
            };
            _db.AccountRequests.Add(request);
            await _db.SaveChangesAsync(ct);
            await NotifyAdminsAsync(request, ct);
        }
        return View("RequestAccountSent");
    }

    private async Task NotifyAdminsAsync(AccountRequest request, CancellationToken ct)
    {
        var admins = await _db.Users
            .Where(u => u.Role == UserRole.Admin && u.IsActive)
            .ToListAsync(ct);
        var url = Request.PublicUrl("/settings/users");
        var encName = System.Net.WebUtility.HtmlEncode(request.DisplayName);
        var encEmail = System.Net.WebUtility.HtmlEncode(request.Email);
        var plainMessage = string.IsNullOrWhiteSpace(request.Message) ? "—" : request.Message;
        var encMessage = string.IsNullOrWhiteSpace(request.Message) ? "—" : System.Net.WebUtility.HtmlEncode(request.Message);
        foreach (var admin in admins)
        {
            var (subject, body, html) = WithCulture(admin.PreferredCulture, () =>
            {
                var t = _localizerFactory.Create(typeof(SharedResources));
                return (
                    t["account_request.email.subject", request.DisplayName].Value,
                    t["account_request.email.body", request.DisplayName, request.Email, plainMessage, url].Value,
                    BuildNoticeHtml(
                        t["account_request.email.intro", encName, encEmail, encMessage].Value,
                        t["account_request.email.cta"].Value,
                        url)
                );
            });
            try { await _gateway.SendAsync(admin.Email, subject, body, html, attachments: null, ct); }
            catch { /* best-effort — the request is still visible under /settings/users */ }
        }
    }

    // ── Admin: approve / reject ────────────────────────────────────────────

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/requests/{id:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, string role, string language, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var request = await _db.AccountRequests.FindAsync(new object[] { id }, ct);
        if (request is null || request.Status != AccountRequestStatus.Pending)
        {
            TempData["Error"] = _l["err.request_not_found"].Value;
            return RedirectToAction("List", "Users");
        }
        if (await _db.Users.AnyAsync(u => u.Email == request.Email, ct))
        {
            TempData["Error"] = _l["err.user_exists"].Value;
            return RedirectToAction("List", "Users");
        }

        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var invite = new Invitation
        {
            Email = request.Email,
            DisplayName = request.DisplayName,
            Role = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User,
            TokenHash = _hasher.Hash(token),
            InvitedByUserId = me.Id,
            Language = SupportedLanguages.Contains(language ?? "") ? language!.ToLowerInvariant() : "en",
        };
        _db.Invitations.Add(invite);
        request.Status = AccountRequestStatus.Approved;
        request.DecidedAt = DateTimeOffset.UtcNow;
        request.DecidedByUserId = me.Id;
        await _db.SaveChangesAsync(ct);

        var url = Request.PublicUrl($"/accept-invite/{invite.Id}?t={token}");
        var expiry = invite.ExpiresAt.ToString("u");
        var (subject, body, html) = WithCulture(invite.Language, () =>
        {
            var t = _localizerFactory.Create(typeof(SharedResources));
            var encName = System.Net.WebUtility.HtmlEncode(me.DisplayName);
            var encEmail = System.Net.WebUtility.HtmlEncode(me.Email);
            return (
                t["invite.email.subject", me.DisplayName].Value,
                t["invite.email.body", me.DisplayName, me.Email, url, expiry].Value,
                BuildNoticeHtml(
                    t["invite.email.intro", encName, encEmail].Value,
                    t["invite.email.cta"].Value,
                    url,
                    t["invite.email.expiry_note", expiry].Value)
            );
        });
        try
        {
            await _gateway.SendAsync(request.Email, subject, body, html, attachments: null, ct);
            TempData["Notice"] = string.Format(_l["notice.request_approved"].Value, request.Email);
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Genehmigt, aber Email-Versand gescheitert: {ex.Message}. Manueller Link: {url}";
        }
        return RedirectToAction("List", "Users");
    }

    [Authorize(Policy = "WebUser")]
    [HttpPost("/settings/users/requests/{id:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (me.Role != UserRole.Admin) return Forbid();
        var request = await _db.AccountRequests.FindAsync(new object[] { id }, ct);
        if (request is null || request.Status != AccountRequestStatus.Pending)
        {
            TempData["Error"] = _l["err.request_not_found"].Value;
            return RedirectToAction("List", "Users");
        }
        request.Status = AccountRequestStatus.Rejected;
        request.DecidedAt = DateTimeOffset.UtcNow;
        request.DecidedByUserId = me.Id;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = string.Format(_l["notice.request_rejected"].Value, request.Email);
        return RedirectToAction("List", "Users");
    }
}

public class RequestAccountViewModel
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Message { get; set; }
    /// <summary>Honeypot — must stay empty. Any value means it's a bot.</summary>
    public string? Website { get; set; }
}
