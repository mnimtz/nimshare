using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageMagick;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NimShare.Core.Data;
using NimShare.Core.Entities;
using NimShare.Api.Services;

namespace NimShare.Api.Controllers;

/// <summary>
/// v1.12 — KI-Auto-Branding pro Freigabelink. Absender gibt die Kunden-Domain
/// ein; wir holen die Website, ziehen Logo/Firmenname/Akzentfarbe, lassen die
/// KI Namen + persönliche Zeile aufhübschen und legen eine link-eigene
/// LandingTemplate-Zeile (Scope=Link) an. Der zurückgegebene templateId wird
/// beim Link-Anlegen als CreateLinkRequest.LandingTemplateId mitgegeben.
///
/// Bewusst ein EIGENER, isolierter Controller: fasst keinen bestehenden
/// Code-Pfad an. SSRF-abgesichert (SsrfGuard) + Consent-Gate wie alle KI-
/// Endpoints. Alles graceful — Logo/Farbe/KI sind optional; schlägt ein Teil
/// fehl, kommt trotzdem eine brauchbare Vorlage zurück (Name + Default).
/// </summary>
[Route("api/v1/ai")]
[Authorize(Policy = "ApiUser")]
[EnableRateLimiting("ai-branding")] // v1.12 (Review F2): teurer Endpoint → 10/min/User
public class AiBrandingController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly IAiGatewayService _ai;
    private readonly IBlobStorageService _blobs;
    private readonly ICurrentUserService _users;
    private readonly ILogger<AiBrandingController> _log;

    public AiBrandingController(NimShareDbContext db, IAiGatewayService ai,
        IBlobStorageService blobs, ICurrentUserService users, ILogger<AiBrandingController> log)
    {
        _db = db; _ai = ai; _blobs = blobs; _users = users; _log = log;
    }

    public record BrandFromDomainReq(string? Domain);

    [HttpPost("brand-from-domain")]
    public async Task<IActionResult> BrandFromDomain([FromBody] BrandFromDomainReq req,
        [FromServices] IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        if (AiConsentGuard.RequireOrReject(this, me) is IActionResult noConsent) return noConsent;

        // ── Domain → https-URL normalisieren + SSRF-Gate ──────────────────────
        if (req is null || string.IsNullOrWhiteSpace(req.Domain))
            return BadRequest(new { error = "Domain fehlt." });
        var raw = req.Domain.Trim();
        if (!raw.Contains("://")) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
            return BadRequest(new { error = "Ungültige Domain." });
        var siteUri = new Uri($"{parsed.Scheme}://{parsed.Host}/");
        if (!SsrfGuard.IsPubliclyRoutableHttpUrl(siteUri.ToString()))
            return BadRequest(new { error = "Diese Domain ist nicht erlaubt/erreichbar." });

        // ── Homepage-HTML holen (Timeout + Größen-Cap) ────────────────────────
        string html;
        var finalUri = siteUri; // nach Redirects — Basis für relative Logo-URLs
        try
        {
            var http = httpFactory.CreateClient("brandfetch");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NimShareBrandBot/1.0");
            using var resp = await FetchGuardedAsync(http, siteUri, ct);
            if (resp is null || !resp.IsSuccessStatusCode)
                return BadRequest(new { error = "Website nicht erreichbar (blockiert, Redirect auf interne Adresse oder Fehlerstatus)." });
            // v1.12.2: finale URL NACH Redirects — relative Logo-Pfade müssen
            // dagegen aufgelöst werden (basf.de → www.basf.com), sonst falscher Host.
            finalUri = resp.RequestMessage?.RequestUri ?? siteUri;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 3_000_000) bytes = bytes[..3_000_000];
            html = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Brand fetch failed for {Host}", siteUri.Host);
            return BadRequest(new { error = "Abruf der Website fehlgeschlagen." });
        }

        var host = siteUri.Host.StartsWith("www.") ? siteUri.Host[4..] : siteUri.Host;
        var companyName = MetaContent(html, "og:site_name")
                          ?? MetaContent(html, "application-name")
                          ?? PageTitle(html)
                          ?? host;
        string? primaryColor = NormalizeHex(MetaName(html, "theme-color"));

        // ── Logo: Kandidat finden → laden → Farbe + als PNG in Blob ───────────
        var templateId = Guid.NewGuid();
        string? logoBlobPath = null, logoUrl = null;
        var logoCandidate = ResolveLogoUri(html, finalUri);
        if (logoCandidate is not null && SsrfGuard.IsPubliclyRoutableHttpUrl(logoCandidate.ToString()))
        {
            try
            {
                var http = httpFactory.CreateClient("brandfetch");
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NimShareBrandBot/1.0");
                using var lresp = await FetchGuardedAsync(http, logoCandidate, ct);
                var imgBytes = lresp is { IsSuccessStatusCode: true }
                    ? await lresp.Content.ReadAsByteArrayAsync(ct) : Array.Empty<byte>();
                if (imgBytes.Length > 0 && imgBytes.Length < 8_000_000)
                {
                    using var img = new MagickImage(imgBytes);
                    img.AutoOrient();
                    if (string.IsNullOrEmpty(primaryColor)) primaryColor = DominantHex(img);
                    img.Format = MagickFormat.Png;
                    using var png = new MemoryStream();
                    img.Write(png, MagickFormat.Png);
                    png.Position = 0;
                    logoBlobPath = $"landing/{templateId:N}/logo.png";
                    await _blobs.UploadFromStreamAsync(logoBlobPath, png, "image/png", ct);
                    logoUrl = $"/landing/img/{templateId}/logo?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                }
            }
            catch (Exception ex) { _log.LogInformation(ex, "Brand logo fetch failed for {Host}", host); }
        }

        // ── KI: Namen säubern + persönliche Überschrift/Zeile (optional) ──────
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string title = companyName;
        string? subtitle = null;
        try
        {
            var provider = await _ai.CreateProviderAsync(ct);
            var prompt =
                $"Ein Unternehmen teilt Dateien mit dem Kunden \"{companyName}\" (Domain {host}). " +
                "Antworte AUSSCHLIESSLICH mit kompaktem JSON in genau dieser Form: " +
                "{\"company\":\"\",\"headline\":\"\",\"subtitle\":\"\"}. " +
                "company = sauberer, korrekt geschriebener Firmenname. " +
                $"headline = kurze persönliche Überschrift (max. 5 Wörter) in der Sprache mit ISO-Code '{lang}'. " +
                "subtitle = EIN freundlicher Willkommenssatz an den Empfänger (max. 18 Wörter), gleiche Sprache. " +
                "Keine Emojis, kein Markdown, keine Erklärungen ausser dem JSON.";
            var aiText = await provider.FreeformAsync(prompt, lang, ct);
            var json = ExtractJsonObject(aiText);
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("company", out var c) && !string.IsNullOrWhiteSpace(c.GetString())) companyName = c.GetString()!.Trim();
                if (r.TryGetProperty("headline", out var h) && !string.IsNullOrWhiteSpace(h.GetString())) title = h.GetString()!.Trim();
                if (r.TryGetProperty("subtitle", out var s) && !string.IsNullOrWhiteSpace(s.GetString())) subtitle = s.GetString()!.Trim();
            }
        }
        catch (Exception ex) { _log.LogInformation(ex, "Brand AI step failed for {Host}", host); }

        // ── Link-Scope-Template anlegen ───────────────────────────────────────
        var tpl = new LandingTemplate
        {
            Id = templateId,
            Scope = LandingTemplateScope.Link,
            OwnerUserId = null, // bewusst kein OwnerUserId (UserPersonal-Unique-Index-Kollision vermeiden)
            CreatedByUserId = me.Id, // v1.12 (Review F5): Ersteller für IDOR-Prüfung + Cleanup
            Title = Trunc(title, 200),
            Subtitle = Trunc(subtitle, 400),
            PrimaryColor = ReadableAccentOrNull(NormalizeHex(primaryColor)),
            LogoBlobPath = logoBlobPath,
            LogoUrl = logoUrl,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.LandingTemplates.Add(tpl);
        await _db.SaveChangesAsync(ct);

        // v1.12.1: Slug-Vorschlag aus dem ersten Domain-Label (= i.d.R. der
        // Markenname). Die Web-UI füllt ihn NUR vor, wenn kein Slug/keine
        // Subdomain gesetzt ist (Subdomain hat Vorrang).
        var suggestedSlug = Slugify(host.Split('.')[0]);

        return Ok(new
        {
            templateId = tpl.Id,
            companyName,
            title = tpl.Title,
            subtitle = tpl.Subtitle,
            primaryColor = tpl.PrimaryColor,
            logoUrl = tpl.LogoUrl,
            suggestedSlug,
        });
    }

    /// <summary>GET mit manuellem Redirect-Following, das JEDEN Hop erneut mit
    /// SsrfGuard prüft — schützt gegen SSRF-über-Redirect (öffentliche Domain →
    /// 3xx → interne IP/Metadaten-Endpoint). Nutzt den "brandfetch"-Client
    /// (AllowAutoRedirect=false). null bei blockiertem Hop oder zu vielen Redirects.</summary>
    private static async Task<HttpResponseMessage?> FetchGuardedAsync(HttpClient http, Uri start, CancellationToken ct)
    {
        var uri = start;
        for (int hop = 0; hop < 4; hop++)
        {
            if (!SsrfGuard.IsPubliclyRoutableHttpUrl(uri.ToString())) return null;
            var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            if (code >= 300 && code < 400 && resp.Headers.Location is Uri loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                resp.Dispose();
                uri = next;
                continue;
            }
            return resp;
        }
        return null; // zu viele Redirects
    }

    // ── Helpers (rein statisch, kein Zustand) ─────────────────────────────────

    private static string? MetaContent(string html, string property)
    {
        // <meta property="og:site_name" content="...">
        var m = Regex.Match(html,
            $"<meta[^>]+(?:property|name)\\s*=\\s*[\"']{Regex.Escape(property)}[\"'][^>]*content\\s*=\\s*[\"']([^\"']+)[\"']",
            RegexOptions.IgnoreCase);
        if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        // content-vor-property-Reihenfolge
        var m2 = Regex.Match(html,
            $"<meta[^>]+content\\s*=\\s*[\"']([^\"']+)[\"'][^>]*(?:property|name)\\s*=\\s*[\"']{Regex.Escape(property)}[\"']",
            RegexOptions.IgnoreCase);
        return m2.Success ? WebUtility.HtmlDecode(m2.Groups[1].Value).Trim() : null;
    }

    private static string? MetaName(string html, string name) => MetaContent(html, name);

    private static string? PageTitle(string html)
    {
        var m = Regex.Match(html, "<title[^>]*>([^<]{1,200})</title>", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var t = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        // "ACME – Startseite" → "ACME"
        var cut = t.Split(new[] { " – ", " - ", " | ", " · ", ": " }, StringSplitOptions.None)[0].Trim();
        return cut.Length > 0 ? cut : t;
    }

    private static Uri? ResolveLogoUri(string html, Uri baseUri)
    {
        // Priorität: apple-touch-icon → og:image → link rel=icon → /favicon.ico
        string? href =
            LinkHref(html, "apple-touch-icon")
            ?? MetaContent(html, "og:image")
            ?? LinkHref(html, "icon")
            ?? LinkHref(html, "shortcut icon");
        if (string.IsNullOrWhiteSpace(href)) href = "/favicon.ico";
        return Uri.TryCreate(baseUri, href, out var abs) ? abs : null;
    }

    private static string? LinkHref(string html, string rel)
    {
        var m = Regex.Match(html,
            $"<link[^>]+rel\\s*=\\s*[\"'][^\"']*{Regex.Escape(rel)}[^\"']*[\"'][^>]*href\\s*=\\s*[\"']([^\"']+)[\"']",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();
        var m2 = Regex.Match(html,
            $"<link[^>]+href\\s*=\\s*[\"']([^\"']+)[\"'][^>]*rel\\s*=\\s*[\"'][^\"']*{Regex.Escape(rel)}[^\"']*[\"']",
            RegexOptions.IgnoreCase);
        return m2.Success ? m2.Groups[1].Value.Trim() : null;
    }

    private static string? DominantHex(MagickImage img)
    {
        try
        {
            using var clone = (MagickImage)img.Clone();
            clone.Resize(new MagickGeometry("1x1!")); // erzwungen 1x1 = Durchschnittsfarbe
            using var px = clone.GetPixels();
            var color = px.GetPixel(0, 0).ToColor();
            var hex = color?.ToHexString();
            return NormalizeHex(hex);
        }
        catch { return null; }
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : null;
    }

    private static string? NormalizeHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        // auf #RRGGBB kürzen (Feld erlaubt max 9 Zeichen, CSS-Akzent will 7)
        if (Regex.IsMatch(s, "^#[0-9a-fA-F]{6,8}$")) return s[..7];
        if (Regex.IsMatch(s, "^#[0-9a-fA-F]{3}$")) return s; // Kurzform ok
        return null;
    }

    private static string? Trunc(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? null : (s.Length <= max ? s : s[..max]);

    /// <summary>v1.12.4: Der Akzent wird als Button-Hintergrund MIT weißer Schrift
    /// und für Überschriften genutzt. Eine zu helle Kundenfarbe (hohe Luminanz)
    /// ergibt blasse, unlesbare Buttons/Titel → in dem Fall verwerfen (null), damit
    /// das Standard-Navy greift. Logo/Text bleiben unberührt.</summary>
    private static string? ReadableAccentOrNull(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7) return hex;
        try
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16);
            var g = Convert.ToInt32(hex.Substring(3, 2), 16);
            var b = Convert.ToInt32(hex.Substring(5, 2), 16);
            var lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
            return lum > 0.62 ? null : hex; // zu hell → Default-Navy statt blass
        }
        catch { return hex; }
    }

    /// <summary>URL-tauglicher Slug aus Name/Domain-Label: lowercase, nur
    /// [a-z0-9-], zusammengefasste Trenner, auf 40 Zeichen begrenzt.</summary>
    private static string Slugify(string s)
    {
        s = (s ?? "").ToLowerInvariant().Trim();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
