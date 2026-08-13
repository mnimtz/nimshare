using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v1.11.0 — Subdomain-Sharing: https://{slug}.{BaseDomain} als zweite
/// Link-Form neben /s/{slug} bzw. /u/{slug}.
///
/// Design-Prinzipien (mit Marcus abgestimmt):
///  * KEINE externen API-Calls im Request-Pfad — die Middleware macht einen
///    reinen Host-Header-Lookup gegen die DB (Settings 60s im MemoryCache).
///  * Basis-Domain konfigurierbar (nicht auf nimshare.com vercoded).
///  * Recht pro User (User.CanUseSubdomainShares), Admin vergibt es.
///  * Cloudflare-Token (verschlüsselt) NUR für den Setup-Assistenten:
///    Wildcard-CNAME (*.domain → OriginHost, proxied) + optional asuid-TXT
///    für das Azure-Custom-Domain-Binding. Proxied + CF-Universal-SSL deckt
///    First-Level-Wildcards ab → kein eigenes Zertifikats-Renewal nötig
///    (SSL-Mode „Full" vorausgesetzt; Doku in docs/SUBDOMAINS.md).
/// </summary>
public interface ISubdomainShareService
{
    Task<SubdomainShareSettings?> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Slug syntaktisch valide + nicht reserviert?</summary>
    bool IsValidSlug(string slug, out string? reason);

    /// <summary>Frei über ShareLinks UND UploadRequests?</summary>
    // v1.12.11: forUserId owner-bewusst — frei für DIESEN User, wenn den Subdomain-
    // Slug nur noch dessen eigene inaktive Links halten. Fremde bleiben belegt.
    Task<bool> IsSlugAvailableAsync(string slug, Guid? forUserId = null, CancellationToken ct = default);
    // v1.12.11: gibt einen Subdomain-Slug frei, den nur eigene inaktive Links halten
    // (SubdomainSlug ist nullable → einfach auf null setzen, Routing greift nur aktiv).
    Task ReclaimOwnedInactiveAsync(string slug, Guid forUserId, CancellationToken ct = default);

    /// <summary>https://{slug}.{BaseDomain} — null wenn Feature aus.</summary>
    Task<string?> BuildUrlAsync(string? slug, CancellationToken ct = default);

    /// <summary>Cache invalidieren (nach Settings-Save).</summary>
    void InvalidateCache();

    /// <summary>Setup-Assistent: legt via Cloudflare-API den Wildcard-CNAME
    /// (+ optional asuid-TXT) an. Liefert menschenlesbares Ergebnis.</summary>
    Task<(bool Ok, string Message)> SetupDnsAsync(CancellationToken ct = default);

    byte[] ProtectToken(string token);
}

public class SubdomainShareService : ISubdomainShareService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SubdomainShareService> _log;
    private const string CacheKey = "subdomain.settings";

    // Reservierte Slugs — Infrastruktur- und Verwechslungs-Namen, die nie
    // als User-Subdomain vergeben werden dürfen.
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "www", "api", "app", "admin", "auth", "login", "logout", "portal",
        "mail", "smtp", "imap", "pop", "webmail", "mx", "ftp", "sftp",
        "cdn", "static", "assets", "img", "media", "files",
        "dev", "test", "staging", "demo", "beta", "preview",
        "status", "health", "monitor", "metrics",
        "ns", "ns1", "ns2", "dns", "vpn", "git", "docs", "help", "support",
        "billing", "pay", "shop", "store", "account", "accounts",
        "autodiscover", "autoconfig", "asuid",
    };

    public SubdomainShareService(IServiceScopeFactory scopes, IMemoryCache cache,
        IDataProtectionProvider dp, IHttpClientFactory http, ILogger<SubdomainShareService> log)
    {
        _scopes = scopes;
        _cache = cache;
        _protector = dp.CreateProtector("NimShare.SubdomainShare.v1");
        _http = http;
        _log = log;
    }

    public async Task<SubdomainShareSettings?> GetSettingsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out SubdomainShareSettings? cached)) return cached;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var row = await db.SubdomainShareSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        _cache.Set(CacheKey, row, TimeSpan.FromSeconds(60));
        return row;
    }

    public void InvalidateCache() => _cache.Remove(CacheKey);

    public bool IsValidSlug(string slug, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(slug)) { reason = "empty"; return false; }
        slug = slug.Trim().ToLowerInvariant();
        if (slug.Length is < 2 or > 63) { reason = "length"; return false; }
        if (slug.StartsWith('-') || slug.EndsWith('-')) { reason = "hyphen"; return false; }
        foreach (var c in slug)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            { reason = "charset"; return false; }
        if (Reserved.Contains(slug)) { reason = "reserved"; return false; }
        return true;
    }

    public async Task<bool> IsSlugAvailableAsync(string slug, Guid? forUserId = null, CancellationToken ct = default)
    {
        slug = slug.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        // "belegt" = ein Link mit diesem Subdomain-Slug, der DIESEN User blockiert:
        // fremd-besessen ODER eigen + noch aktiv. forUserId=null → jeder Link belegt.
        var takenShare = await db.ShareLinks.AnyAsync(l => l.SubdomainSlug == slug
            && (forUserId == null || l.OwnerId != forUserId.Value
                || (!l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now)
                    && (l.MaxDownloads == null || l.DownloadCount < l.MaxDownloads))), ct);
        if (takenShare) return false;
        var takenUpload = await db.UploadRequests.AnyAsync(l => l.SubdomainSlug == slug
            && (forUserId == null || l.OwnerId != forUserId.Value
                || (!l.IsRevoked && (l.ExpiresAt == null || l.ExpiresAt > now)
                    && (l.MaxUploads == null || l.UploadCount < l.MaxUploads))), ct);
        return !takenUpload;
    }

    public async Task ReclaimOwnedInactiveAsync(string slug, Guid forUserId, CancellationToken ct = default)
    {
        slug = slug.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var deadShares = await db.ShareLinks.Where(l => l.SubdomainSlug == slug && l.OwnerId == forUserId
            && (l.IsRevoked || (l.ExpiresAt != null && l.ExpiresAt <= now)
                || (l.MaxDownloads != null && l.DownloadCount >= l.MaxDownloads))).ToListAsync(ct);
        foreach (var l in deadShares) l.SubdomainSlug = null;
        var deadUploads = await db.UploadRequests.Where(l => l.SubdomainSlug == slug && l.OwnerId == forUserId
            && (l.IsRevoked || (l.ExpiresAt != null && l.ExpiresAt <= now)
                || (l.MaxUploads != null && l.UploadCount >= l.MaxUploads))).ToListAsync(ct);
        foreach (var l in deadUploads) l.SubdomainSlug = null;
        if (deadShares.Count + deadUploads.Count > 0) await db.SaveChangesAsync(ct);
    }

    public async Task<string?> BuildUrlAsync(string? slug, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        var s = await GetSettingsAsync(ct);
        if (s is null || !s.Enabled || string.IsNullOrEmpty(s.BaseDomain)) return null;
        return $"https://{slug}.{s.BaseDomain}";
    }

    public byte[] ProtectToken(string token) => _protector.Protect(Encoding.UTF8.GetBytes(token));

    private string? UnprotectToken(byte[]? enc)
    {
        if (enc is null || enc.Length == 0) return null;
        try { return Encoding.UTF8.GetString(_protector.Unprotect(enc)); }
        catch { return null; }
    }

    // ── Cloudflare-Setup-Assistent ───────────────────────────────────────

    public async Task<(bool Ok, string Message)> SetupDnsAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var s = await db.SubdomainShareSettings.FirstOrDefaultAsync(ct);
        if (s is null || string.IsNullOrEmpty(s.BaseDomain) || string.IsNullOrEmpty(s.OriginHost))
            return (false, "BaseDomain/OriginHost fehlen — erst speichern.");
        var token = UnprotectToken(s.CloudflareApiTokenEncrypted);
        if (token is null)
            return (false, "Kein Cloudflare-API-Token hinterlegt.");

        var http = _http.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(20);

        // 1. Zone-Id ermitteln (einmalig, danach gecacht in der Settings-Row).
        var zoneId = s.CloudflareZoneId;
        if (string.IsNullOrEmpty(zoneId))
        {
            var zResp = await http.GetAsync(
                $"https://api.cloudflare.com/client/v4/zones?name={Uri.EscapeDataString(s.BaseDomain)}", ct);
            var zBody = await zResp.Content.ReadAsStringAsync(ct);
            if (!zResp.IsSuccessStatusCode)
                return (false, $"Cloudflare-Zonen-Abfrage fehlgeschlagen ({(int)zResp.StatusCode}): {Truncate(zBody)}");
            using var zDoc = JsonDocument.Parse(zBody);
            var results = zDoc.RootElement.GetProperty("result");
            if (results.GetArrayLength() == 0)
                return (false, $"Keine Cloudflare-Zone für „{s.BaseDomain}“ gefunden — liegt die Domain bei Cloudflare und hat das Token Zugriff auf die Zone?");
            zoneId = results[0].GetProperty("id").GetString();
            s.CloudflareZoneId = zoneId;
            await db.SaveChangesAsync(ct);
        }

        var messages = new List<string>();

        // 2. Wildcard-CNAME *.{BaseDomain} → OriginHost, PROXIED (orange) —
        //    Cloudflare Universal SSL deckt First-Level-Wildcards ab, damit
        //    entfällt jedes eigene Zertifikats-Renewal.
        var cnameOk = await UpsertDnsRecordAsync(http, zoneId!,
            type: "CNAME", name: $"*.{s.BaseDomain}", content: s.OriginHost,
            proxied: true, ct);
        messages.Add(cnameOk.Ok
            ? $"✓ CNAME *.{s.BaseDomain} → {s.OriginHost} (proxied)"
            : $"✗ CNAME: {cnameOk.Message}");

        // 3. Optional: asuid-TXT für das Azure-Wildcard-Custom-Domain-Binding.
        if (!string.IsNullOrWhiteSpace(s.AzureVerificationId))
        {
            var txtOk = await UpsertDnsRecordAsync(http, zoneId!,
                type: "TXT", name: $"asuid.{s.BaseDomain}",
                content: s.AzureVerificationId.Trim(), proxied: false, ct);
            messages.Add(txtOk.Ok
                ? $"✓ TXT asuid.{s.BaseDomain} (Azure-Verifizierung)"
                : $"✗ TXT asuid: {txtOk.Message}");
        }

        var ok = messages.All(m => m.StartsWith('✓'));
        return (ok, string.Join("\n", messages));
    }

    private async Task<(bool Ok, string Message)> UpsertDnsRecordAsync(HttpClient http, string zoneId,
        string type, string name, string content, bool proxied, CancellationToken ct)
    {
        try
        {
            // Existiert der Record schon? Dann PATCHen statt doppelt anlegen.
            var listResp = await http.GetAsync(
                $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type={type}&name={Uri.EscapeDataString(name)}", ct);
            var listBody = await listResp.Content.ReadAsStringAsync(ct);
            string? existingId = null;
            if (listResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(listBody);
                var arr = doc.RootElement.GetProperty("result");
                if (arr.GetArrayLength() > 0)
                    existingId = arr[0].GetProperty("id").GetString();
            }
            var payload = JsonSerializer.Serialize(new { type, name, content, proxied, ttl = 1 });
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage resp = existingId is null
                ? await http.PostAsync($"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records", body, ct)
                : await http.PutAsync($"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{existingId}", body, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(respBody)}");
            return (true, "ok");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloudflare DNS upsert failed for {Name}", name);
            return (false, ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// v1.11.0 — Host-Header-Routing. Läuft VOR dem Routing: kommt ein Request
/// für {slug}.{BaseDomain} auf dem Root-Pfad an, wird der Pfad intern auf
/// /s/{slug} (ShareLink) bzw. /u/{slug} (Upload-Request) umgeschrieben —
/// die komplette bestehende Landing-Logik (Passwort, Ablauf, Gallery, …)
/// läuft dadurch unverändert. Alle anderen Pfade (CSS, /s/x/thumb, POSTs
/// der Landing-Formulare) laufen auf dem Subdomain-Host ganz normal durch.
/// Unbekannter Slug → Rewrite auf einen garantiert unbekannten /s/-Slug,
/// damit die gebrandete NotFound-View rendert (kein nacktes 404).
/// </summary>
public class SubdomainShareMiddleware
{
    private readonly RequestDelegate _next;

    public SubdomainShareMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, ISubdomainShareService svc, IServiceScopeFactory scopes)
    {
        // Nur den Root-Pfad umschreiben — alles andere (Assets, Landing-
        // Unterrouten) funktioniert host-agnostisch.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            || ctx.Request.Path != "/")
        {
            await _next(ctx);
            return;
        }

        var settings = await svc.GetSettingsAsync(ctx.RequestAborted);
        if (settings is null || !settings.Enabled || string.IsNullOrEmpty(settings.BaseDomain))
        {
            await _next(ctx);
            return;
        }

        var host = ctx.Request.Host.Host.ToLowerInvariant();
        var baseDomain = settings.BaseDomain.ToLowerInvariant();
        if (host == baseDomain || host == $"www.{baseDomain}"
            || !host.EndsWith($".{baseDomain}", StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }

        var label = host[..^(baseDomain.Length + 1)];
        // Nur EINE Ebene ({slug}.domain) und keine reservierten Namen.
        if (label.Contains('.') || SubdomainShareService.Reserved.Contains(label))
        {
            await _next(ctx);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
        var shareSlug = await db.ShareLinks.AsNoTracking()
            .Where(l => l.SubdomainSlug == label)
            .Select(l => l.Slug)
            .FirstOrDefaultAsync(ctx.RequestAborted);
        if (shareSlug is not null)
        {
            ctx.Request.Path = $"/s/{shareSlug}";
        }
        else
        {
            var uploadSlug = await db.UploadRequests.AsNoTracking()
                .Where(l => l.SubdomainSlug == label)
                .Select(l => l.Slug)
                .FirstOrDefaultAsync(ctx.RequestAborted);
            // Kein Treffer → /s/__subdomain-not-found__ existiert garantiert
            // nicht → ShareController rendert die saubere NotFound-View.
            ctx.Request.Path = uploadSlug is not null
                ? $"/u/{uploadSlug}"
                : "/s/__subdomain-not-found__";
        }
        await _next(ctx);
    }
}
