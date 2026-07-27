using System.Text.Json;

namespace NimShare.Api.Services;

/// <summary>
/// v1.10.42: grobe Geo-Auflösung einer IP zu (Country, City). Wird in
/// SignatureAudit und ShareLinkAccess persistiert, damit Marcus in
/// Reports Land/Stadt statt "IpHash 4b31..." sieht.
///
/// Zwei Implementierungen:
///   - <see cref="NullGeoIpService"/> → default, macht nichts. Kein
///     externer Call, keine DSGVO-Frage.
///   - <see cref="IpApiCoGeoIpService"/> → optional via config
///     ("NimShare:GeoIp:Provider" = "IpApiCo"). Nutzt ipapi.co ohne
///     Key (kostenlos, 1000 Requests/Tag). HTTPS.
///
/// Persistenz: Die echte IP wird NICHT gespeichert. Nur das Resultat
/// (Country/City) landet in der Audit-Zeile. Damit ist der Lookup
/// DSGVO-neutral: die IP verlässt kurzzeitig den Server per HTTPS,
/// das Resultat trägt keine personenbeziehbaren Merkmale.
/// </summary>
public interface IGeoIpService
{
    Task<(string? Country, string? City)> LookupAsync(string? ip, CancellationToken ct = default);

    /// <summary>v1.11.14: wie LookupAsync, aber inklusive ASN/Org-String
    /// (z.B. "AS8075 Microsoft Corporation") — Grundlage für
    /// RefererClassifier, um automatisierte Link-Vorschau-Abrufe (Teams,
    /// Slack etc.) auch dann zu erkennen, wenn der Referer-Header selbst
    /// nicht auf eine bekannte Domain passt.</summary>
    Task<(string? Country, string? City, string? Isp)> LookupWithIspAsync(string? ip, CancellationToken ct = default);
}

public sealed class NullGeoIpService : IGeoIpService
{
    public Task<(string? Country, string? City)> LookupAsync(string? ip, CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));

    public Task<(string? Country, string? City, string? Isp)> LookupWithIspAsync(string? ip, CancellationToken ct = default)
        => Task.FromResult<(string?, string?, string?)>((null, null, null));
}

public sealed class IpApiCoGeoIpService : IGeoIpService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<IpApiCoGeoIpService> _log;
    // In-Process-Cache: pro IP nur einmal HTTP-Lookup. Bei einem
    // aktiven Link-Report wären das sonst schnell hundert Requests
    // hintereinander, und ipapi.co rate-limitet gratis auf 45/min.
    // TTL 24h → nicht zu großzügig (IPs können umziehen), nicht zu
    // knapp (Wiederkehrer treffen den Cache).
    private static readonly Dictionary<string, (DateTimeOffset CachedAt, string? Country, string? City, string? Isp)> Cache = new();
    private static readonly object CacheLock = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public IpApiCoGeoIpService(IHttpClientFactory http, ILogger<IpApiCoGeoIpService> log)
    { _http = http; _log = log; }

    public async Task<(string? Country, string? City)> LookupAsync(string? ip, CancellationToken ct = default)
    {
        var (country, city, _) = await LookupWithIspAsync(ip, ct);
        return (country, city);
    }

    public async Task<(string? Country, string? City, string? Isp)> LookupWithIspAsync(string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return (null, null, null);
        // Reserved-Range-Skip: private IPs (10.*, 192.168.*, ::1, 127.*)
        // liefern immer "Reserved" bei ipapi.co — Request sparen.
        if (ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
            ip.StartsWith("127.") || ip == "::1" || ip.StartsWith("fe80:"))
            return (null, null, null);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(ip, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < Ttl)
                return (cached.Country, cached.City, cached.Isp);
        }
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            // Endpoint gibt JSON mit country/city/org zurück. Kein Key nötig.
            var resp = await client.GetAsync($"https://ipapi.co/{Uri.EscapeDataString(ip)}/json/", ct);
            if (!resp.IsSuccessStatusCode) { CacheNegative(ip); return (null, null, null); }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? country = null;
            string? city = null;
            string? isp = null;
            if (root.TryGetProperty("country_code", out var cc)) country = cc.GetString();
            if (root.TryGetProperty("city", out var ci)) city = ci.GetString();
            // v1.11.14: "org" trägt üblicherweise "AS8075 Microsoft
            // Corporation" — Grundlage für RefererClassifier's ISP-Signal.
            if (root.TryGetProperty("org", out var orgEl)) isp = orgEl.GetString();
            lock (CacheLock)
            {
                Cache[ip] = (DateTimeOffset.UtcNow, country, city, isp);
                // Cache-Cleanup falls Speicher zu voll wird — >5000 Einträge
                // signalisiert dass wir vermutlich einen Scanner reingekommen
                // sind. Behalten die neuesten 2000.
                if (Cache.Count > 5000)
                {
                    var toRemove = Cache.OrderBy(kv => kv.Value.CachedAt)
                                        .Take(Cache.Count - 2000)
                                        .Select(kv => kv.Key)
                                        .ToList();
                    foreach (var k in toRemove) Cache.Remove(k);
                }
            }
            return (country, city, isp);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GeoIP lookup failed for {Ip}", ip);
            CacheNegative(ip);
            return (null, null, null);
        }
    }

    private static void CacheNegative(string ip)
    {
        lock (CacheLock) { Cache[ip] = (DateTimeOffset.UtcNow, null, null, null); }
    }
}
