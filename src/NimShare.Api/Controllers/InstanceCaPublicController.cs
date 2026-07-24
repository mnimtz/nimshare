using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;

namespace NimShare.Api.Controllers;

/// <summary>
/// Public download for the NimShare-Root-CA certificate (v1.10.153 Weg A).
/// A recipient of any signed link imports this once into their OS/browser
/// trust store, and every future signed link on this instance validates
/// automatically without a "unknown CA" prompt.
/// </summary>
[AllowAnonymous]
public class InstanceCaPublicController : Controller
{
    private readonly IInstanceCaService _ca;
    public InstanceCaPublicController(IInstanceCaService ca) { _ca = ca; }

    /// <summary>Returns the CA cert as DER (.cer) — importable in Windows,
    /// macOS Keychain, iOS profile install, and browser trust stores.</summary>
    [HttpGet("/nimshare-root.cer")]
    public async Task<IActionResult> DownloadDer(CancellationToken ct)
    {
        var der = await _ca.GetPublicCertDerAsync(ct);
        return File(der, "application/x-x509-ca-cert", "nimshare-root.cer");
    }

    /// <summary>PEM variant for tooling that prefers Base64 (openssl, curl).</summary>
    [HttpGet("/nimshare-root.pem")]
    public async Task<IActionResult> DownloadPem(CancellationToken ct)
    {
        var der = await _ca.GetPublicCertDerAsync(ct);
        var pem = "-----BEGIN CERTIFICATE-----\n"
            + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END CERTIFICATE-----\n";
        return File(System.Text.Encoding.UTF8.GetBytes(pem), "application/x-pem-file", "nimshare-root.pem");
    }
}
