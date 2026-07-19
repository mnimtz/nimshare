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

        // DNS TXT verification is not implemented yet — we deliberately do NOT
        // mark the domain as Failed on every call (that led to a permanent
        // "Failed" chip in the UI). Return 501 so the UI can render an
        // "install a DNS resolver first" hint instead.
        domain.LastVerificationAttemptAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Problem(
            statusCode: 501,
            title: "Domain verification not implemented",
            detail: "TXT-record verification is a scaffolded stub. Wire DnsClient.NET into CheckTxtAsync to enable it. See docs/CUSTOM_DOMAINS.md.");
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
