using System.Net;
using System.Net.Sockets;

namespace NimShare.Api.Services;

/// <summary>
/// Best-effort SSRF guard for user-supplied outbound URLs (currently webhooks). Rejects
/// non-HTTP(S) schemes and any host that resolves to a loopback / private / link-local /
/// unique-local / multicast address, so a user cannot point the server at the cloud metadata
/// endpoint (169.254.169.254) or intranet services.
///
/// <para>
/// Note: resolving DNS here and connecting later leaves a small TOCTOU/rebinding window. This
/// closes the common case; pinning the socket to the validated IP would be the fully robust fix.
/// </para>
/// </summary>
public static class SsrfGuard
{
    public static bool IsPubliclyRoutableHttpUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try { addresses = Dns.GetHostAddresses(uri.Host); }
            catch { return false; }
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // Block 0/8, 10/8, 100.64/10 (CGNAT), 127/8, 169.254/16, 172.16/12, 192.168/16, 224/4+.
            if (b[0] is 0 or 10 or 127) return false;
            if (b[0] == 100 && (b[1] & 0xC0) == 0x40) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && (b[1] & 0xF0) == 0x10) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] >= 224) return false; // multicast + reserved
            return true;
        }

        // IPv6.
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
        if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7 unique-local
        return true;
    }
}
