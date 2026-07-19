using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;
using QRCoder;

namespace NimShare.Api.Controllers;

/// <summary>2FA setup + enrolment. Login-time verification is handled by AccountController.</summary>
[Authorize(Policy = "WebUser")]
public class TwoFactorController : Controller
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly ITotpService _totp;

    public TwoFactorController(NimShareDbContext db, ICurrentUserService users, ITotpService totp)
    {
        _db = db; _users = users; _totp = totp;
    }

    [HttpGet("/settings/2fa")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        ViewData["IsEnrolled"] = me.TotpEnabled;
        if (!me.TotpEnabled)
        {
            // Draft a secret we'll only persist after successful verify.
            var secret = _totp.GenerateSecret();
            var uri = _totp.BuildOtpAuthUri(secret, me.Email, "NimShare");
            HttpContext.Session.SetString("2fa.pending", secret);
            ViewData["Secret"] = secret;
            ViewData["OtpAuthUri"] = uri;
        }
        return View();
    }

    [HttpGet("/settings/2fa/qr")]
    public IActionResult Qr(string data)
    {
        // Serve the QR code as SVG so we don't depend on a raster codec.
        using var gen = new QRCodeGenerator();
        var qrData = gen.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(qrData).GetGraphic(4);
        return Content(svg, "image/svg+xml; charset=utf-8");
    }

    [HttpPost("/settings/2fa/enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(string code, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        var pending = HttpContext.Session.GetString("2fa.pending");
        if (string.IsNullOrEmpty(pending) || !_totp.Verify(pending, code ?? ""))
        {
            TempData["Error"] = "Der Code stimmt nicht. Neuer Versuch — vergiss nicht: die Uhr deines Geräts muss stimmen.";
            return RedirectToAction(nameof(Index));
        }
        me.TotpSecret = pending;
        me.TotpEnabled = true;
        me.TotpEnrolledAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        HttpContext.Session.Remove("2fa.pending");
        TempData["Notice"] = "2FA aktiviert.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/settings/2fa/disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(string code, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (!me.TotpEnabled || string.IsNullOrEmpty(me.TotpSecret)) return RedirectToAction(nameof(Index));
        if (!_totp.Verify(me.TotpSecret, code ?? ""))
        {
            TempData["Error"] = "Falscher Code. 2FA bleibt aktiv.";
            return RedirectToAction(nameof(Index));
        }
        me.TotpSecret = null;
        me.TotpEnabled = false;
        me.TotpEnrolledAt = null;
        await _db.SaveChangesAsync(ct);
        TempData["Notice"] = "2FA deaktiviert.";
        return RedirectToAction(nameof(Index));
    }
}
