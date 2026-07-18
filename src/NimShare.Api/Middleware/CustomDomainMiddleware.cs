using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Middleware;

/// <summary>
/// Looks up the Host header in the <see cref="CustomDomain"/> table and, if verified,
/// stashes the owner id on the request for downstream code (branded landing pages).
/// Slug lookup remains global — this middleware only affects branding.
/// </summary>
public class CustomDomainMiddleware
{
    public const string OwnerIdItemKey = "CustomDomainOwnerId";
    public const string HostnameItemKey = "CustomDomainHostname";

    private readonly RequestDelegate _next;

    public CustomDomainMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx, NimShareDbContext db)
    {
        var host = ctx.Request.Host.Host.ToLowerInvariant();
        if (!host.EndsWith(".azurewebsites.net") && host != "localhost" && host != "127.0.0.1")
        {
            var domain = await db.CustomDomains
                .Where(x => x.Hostname == host
                            && x.VerificationStatus == CustomDomainVerificationStatus.Verified)
                .Select(x => new { x.OwnerId, x.Hostname })
                .SingleOrDefaultAsync(ctx.RequestAborted);

            if (domain is not null)
            {
                ctx.Items[OwnerIdItemKey] = domain.OwnerId;
                ctx.Items[HostnameItemKey] = domain.Hostname;
            }
        }

        await _next(ctx);
    }
}
