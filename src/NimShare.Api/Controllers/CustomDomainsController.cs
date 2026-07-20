using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[ApiController]
[Route("api/v1/custom-domains")]
[Authorize(Policy = "ApiUser")]
public class CustomDomainsController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;

    public CustomDomainsController(NimShareDbContext db, ICurrentUserService users)
    {
        _db = db;
        _users = users;
    }

    public record AddDomainRequest(string Hostname);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddDomainRequest req, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var host = req.Hostname.Trim().ToLowerInvariant();
        if (!IsValidHostname(host))
            return Problem(statusCode: 422, title: "Invalid hostname", detail: "Provide a fully-qualified hostname, e.g. share.example.com.");

        if (await _db.CustomDomains.AnyAsync(x => x.Hostname == host, ct))
            return Problem(statusCode: 409, title: "Hostname taken");

        var domain = new CustomDomain
        {
            OwnerId = user.Id,
            Hostname = host,
            VerificationToken = Guid.NewGuid().ToString("N")[..24].ToUpperInvariant(),
            VerificationStatus = CustomDomainVerificationStatus.Pending,
        };
        _db.CustomDomains.Add(domain);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            domain.Id,
            domain.Hostname,
            domain.VerificationToken,
            TxtRecordHost = "_nimshare-verify." + host,
            CnameHost = host,
            CnameTarget = Request.Host.Host, // in Azure: <site>.azurewebsites.net
            domain.VerificationStatus,
        });
    }

    [HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var domain = await _db.CustomDomains.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.Id, ct);
        if (domain is null) return NotFound();

        // Actually verify via Google DoH — the CheckTxtAsync helper below has
        // been ready since v1.3 but the endpoint was still returning 501.
        // "verified" flips the chip green in the UI; "failed" marks it so the
        // user can retry after fixing DNS. Either way we bump the attempt
        // timestamp so the UI can show "last checked X ago".
        domain.LastVerificationAttemptAt = DateTimeOffset.UtcNow;
        var ok = await CheckTxtAsync(domain.Hostname, domain.VerificationToken, ct);
        domain.VerificationStatus = ok
            ? CustomDomainVerificationStatus.Verified
            : CustomDomainVerificationStatus.Failed;
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            verified = ok,
            status = domain.VerificationStatus.ToString(),
            hostname = domain.Hostname,
            expectedTxt = domain.VerificationToken,
            hint = ok
                ? "TXT-Record wurde gefunden. Domain ist verifiziert."
                : $"TXT-Record fehlt oder passt nicht. Trage exakt diesen Wert auf _nimshare-verify.{domain.Hostname} als TXT ein und probiere in 1–5 min erneut.",
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var items = await _db.CustomDomains
            .Where(x => x.OwnerId == user.Id && x.VerificationStatus != CustomDomainVerificationStatus.Deleted)
            .Select(x => new { x.Id, x.Hostname, x.VerificationStatus, x.CertificateStatus, x.AddedAt, x.VerifiedAt })
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _users.GetOrProvisionAsync(User, ct);
        var domain = await _db.CustomDomains.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.Id, ct);
        if (domain is null) return NotFound();
        domain.VerificationStatus = CustomDomainVerificationStatus.Deleted;
        await _db.SaveChangesAsync(ct);
        // TODO: also unbind from App Service.
        return NoContent();
    }

    private static bool IsValidHostname(string s)
    {
        if (s.Length is < 4 or > 253) return false;
        if (!s.Contains('.')) return false;
        return Uri.CheckHostName(s) is UriHostNameType.Dns;
    }

    /// <summary>
    /// Verifies a domain-ownership TXT record by querying Google's public DoH
    /// (DNS-over-HTTPS) API. No extra NuGet package needed; runs anywhere
    /// outbound HTTPS to dns.google resolves.
    /// </summary>
    private static async Task<bool> CheckTxtAsync(string hostname, string expected, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.Add("Accept", "application/dns-json");
            var url = $"https://dns.google/resolve?name={Uri.EscapeDataString(hostname)}&type=TXT";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("Answer", out var answer)) return false;
            foreach (var a in answer.EnumerateArray())
            {
                if (!a.TryGetProperty("data", out var data)) continue;
                var raw = data.GetString() ?? "";
                // DoH returns TXT values wrapped in double quotes.
                var value = raw.Trim().Trim('"');
                if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch { return false; }
    }
}
