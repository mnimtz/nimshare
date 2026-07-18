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

        domain.LastVerificationAttemptAt = DateTimeOffset.UtcNow;
        var ok = await CheckTxtAsync(domain.Hostname, domain.VerificationToken, ct);
        if (ok)
        {
            domain.VerificationStatus = CustomDomainVerificationStatus.Verified;
            domain.VerifiedAt = DateTimeOffset.UtcNow;
            domain.CertificateStatus = CustomDomainCertificateStatus.Provisioning;
            // TODO: call App Service management API to bind hostname + request managed cert.
            //       Requires either a Managed Identity + role assignment, or a service principal.
        }
        else
        {
            domain.VerificationStatus = CustomDomainVerificationStatus.Failed;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { domain.VerificationStatus, domain.CertificateStatus });
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

    private static async Task<bool> CheckTxtAsync(string hostname, string expected, CancellationToken ct)
    {
        // NOTE: .NET doesn't ship a native TXT-record resolver. In production, plug in DnsClient.NET
        //       or MX Toolbox API. This stub keeps the pipeline shape without pulling that in yet.
        //       Returning false forces the user to hit "Verify" again after we wire the resolver.
        await Task.Yield();
        _ = hostname; _ = expected;
        return false;
    }
}
