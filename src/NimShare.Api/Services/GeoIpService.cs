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
///   - <see cref="IpWhoIsGeoIpService"/> → optional via config
///     ("NimShare:GeoIp:Provider" = "IpApiCo"/Standard). HTTPS, kein Key.
///
/// v1.11.41: Provider gewechselt von ipapi.co → ipwho.is. Root-Cause für
/// Marcus's Report "IP wird geloggt, Land/Stadt bleibt leer": ipapi.co's
/// Gratis-Tier ist inzwischen so aggressiv rate-limitiert, dass es fast
/// JEDEN Request mit HTTP 200 + Error-JSON im Body ablehnt
/// ({"error":true,"reason":"RateLimited",...}) — das alte
/// IsSuccessStatusCode-Logging (v1.11.34) griff hier nie, weil der Request
/// laut HTTP-Status ja erfolgreich war. Verifiziert per direktem curl-Test
/// gegen mehrere IPs (immer "RateLimited"), während ipwho.is (und
/// ip-api.com) für dieselben IPs sofort korrekte Daten lieferten.
/// ipwho.is markiert Fehler ebenfalls im Body ("success":false) statt per
/// HTTP-Status — wird jetzt explizit geprüft und geloggt, damit dieselbe
/// Bug-Klasse nicht nochmal unsichtbar wird.
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

    /// <summary>v1.11.17: wie LookupWithIspAsync, aber inklusive Lat/Lon —
    /// Grundlage für IP-basiertes Wetter auf dem Dashboard, damit der
    /// Browser nicht mehr für jeden Besuch die native Standort-Berechtigung
    /// abfragen muss (die IP-Auflösung ist stadtgenau, kein Permission-
    /// Prompt nötig).</summary>
    Task<(string? Country, string? City, string? Isp, double? Latitude, double? Longitude)> LookupFullAsync(string? ip, CancellationToken ct = default);
}

public sealed class NullGeoIpService : IGeoIpService
{
    public Task<(string? Country, string? City)> LookupAsync(string? ip, CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));

    public Task<(string? Country, string? City, string? Isp)> LookupWithIspAsync(string? ip, CancellationToken ct = default)
        => Task.FromResult<(string?, string?, string?)>((null, null, null));

    public Task<(string? Country, string? City, string? Isp, double? Latitude, double? Longitude)> LookupFullAsync(string? ip, CancellationToken ct = default)
        => Task.FromResult<(string?, string?, string?, double?, double?)>((null, null, null, null, null));
}

/// <summary>v1.11.41: ehemals IpApiCoGeoIpService — Provider gewechselt auf
/// ipwho.is, siehe Klassen-Doku bei <see cref="IGeoIpService"/>.</summary>
public sealed class IpWhoIsGeoIpService : IGeoIpService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<IpWhoIsGeoIpService> _log;
    // In-Process-Cache: pro IP nur einmal HTTP-Lookup. Bei einem
    // aktiven Link-Report wären das sonst schnell hundert Requests
    // hintereinander. TTL 24h → nicht zu großzügig (IPs können umziehen),
    // nicht zu knapp (Wiederkehrer treffen den Cache).
    private static readonly Dictionary<string, (DateTimeOffset CachedAt, string? Country, string? City, string? Isp, double? Lat, double? Lon)> Cache = new();
    private static readonly object CacheLock = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public IpWhoIsGeoIpService(IHttpClientFactory http, ILogger<IpWhoIsGeoIpService> log)
    { _http = http; _log = log; }

    public async Task<(string? Country, string? City)> LookupAsync(string? ip, CancellationToken ct = default)
    {
        var (country, city, _, _, _) = await LookupFullAsync(ip, ct);
        return (country, city);
    }

    public async Task<(string? Country, string? City, string? Isp)> LookupWithIspAsync(string? ip, CancellationToken ct = default)
    {
        var (country, city, isp, _, _) = await LookupFullAsync(ip, ct);
        return (country, city, isp);
    }

    public async Task<(string? Country, string? City, string? Isp, double? Latitude, double? Longitude)> LookupFullAsync(string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return (null, null, null, null, null);
        // Reserved-Range-Skip: private IPs (10.*, 192.168.*, ::1, 127.*)
        // liefern immer einen Fehler beim Provider — Request sparen.
        if (ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
            ip.StartsWith("127.") || ip == "::1" || ip.StartsWith("fe80:"))
            return (null, null, null, null, null);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(ip, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < Ttl)
                return (cached.Country, cached.City, cached.Isp, cached.Lat, cached.Lon);
        }
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var resp = await client.GetAsync($"https://ipwho.is/{Uri.EscapeDataString(ip)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("GeoIP lookup for {Ip} returned {Status}: {Body}", ip, (int)resp.StatusCode, body);
                CacheNegative(ip);
                return (null, null, null, null, null);
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // v1.11.41: ipwho.is markiert Fehler (Rate-Limit, ungültige IP,
            // reservierter Bereich, ...) NICHT per HTTP-Status sondern per
            // "success":false im Body (HTTP bleibt 200) — genau die
            // Bug-Klasse, die bei ipapi.co monatelang unsichtbar blieb.
            if (root.TryGetProperty("success", out var successEl)
                && successEl.ValueKind == JsonValueKind.False)
            {
                var msg = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                _log.LogWarning("GeoIP lookup for {Ip} unsuccessful: {Message}", ip, msg);
                CacheNegative(ip);
                return (null, null, null, null, null);
            }
            string? country = null;
            string? city = null;
            string? isp = null;
            double? lat = null;
            double? lon = null;
            if (root.TryGetProperty("country_code", out var cc)) country = cc.GetString();
            if (root.TryGetProperty("city", out var ci)) city = ci.GetString();
            // v1.11.14/v1.11.41: kombiniert ASN + Org zu z.B. "AS3320
            // Deutsche Telekom AG" — gleiches Format wie zuvor bei ipapi.co,
            // Grundlage für RefererClassifier's ISP-Signal.
            if (root.TryGetProperty("connection", out var conn) && conn.ValueKind == JsonValueKind.Object)
            {
                var org = conn.TryGetProperty("org", out var orgEl) ? orgEl.GetString() : null;
                var asn = conn.TryGetProperty("asn", out var asnEl) && asnEl.ValueKind == JsonValueKind.Number ? asnEl.GetInt64().ToString() : null;
                isp = (asn, org) switch
                {
                    (not null, not null) => $"AS{asn} {org}",
                    (null, not null) => org,
                    (not null, null) => $"AS{asn}",
                    _ => null,
                };
            }
            // v1.11.17: Stadt-genaue Koordinaten — Grundlage für IP-basiertes
            // Wetter ohne Browser-Standortabfrage.
            if (root.TryGetProperty("latitude", out var latEl) && latEl.ValueKind == JsonValueKind.Number) lat = latEl.GetDouble();
            if (root.TryGetProperty("longitude", out var lonEl) && lonEl.ValueKind == JsonValueKind.Number) lon = lonEl.GetDouble();
            lock (CacheLock)
            {
                Cache[ip] = (DateTimeOffset.UtcNow, country, city, isp, lat, lon);
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
            return (country, city, isp, lat, lon);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GeoIP lookup failed for {Ip}", ip);
            CacheNegative(ip);
            return (null, null, null, null, null);
        }
    }

    private static void CacheNegative(string ip)
    {
        lock (CacheLock) { Cache[ip] = (DateTimeOffset.UtcNow, null, null, null, null, null); }
    }
}
