using Microsoft.AspNetCore.Localization;

namespace NimShare.Api.Middleware;

// Public share / sign / upload-request landings are always visited by
// someone who is NOT the sender. Their browser advertises the languages
// they actually read. But the standard middleware chain evaluates the
// `.AspNetCore.Culture` cookie BEFORE Accept-Language — so a visitor
// who once logged in to the app (or clicked a "Sprache: DE" widget on
// another link) drags that cookie into every subsequent share visit
// forever, even when the browser is set to French.
//
// This provider fires ONLY on the anonymous public paths (`/s/`, `/u/`,
// `/sign/`) and returns the best Accept-Language match early, short-
// circuiting the cookie provider that would otherwise win. The list of
// prefixes is deliberately narrow — logged-in app pages keep the cookie
// behaviour they always had, so a user who explicitly picked their UI
// language keeps it.
public sealed class PublicPathBrowserLanguageProvider : RequestCultureProvider
{
    private static readonly string[] PublicPrefixes = { "/s/", "/u/", "/sign/" };
    // .NET 8 dropped the base-class NullResult static, so we cache our own.
    // Returning Task.FromResult((ProviderCultureResult?)null) at every fall-
    // through would allocate a Task per request; one shared instance is fine.
    private static readonly Task<ProviderCultureResult?> NullResultTask = Task.FromResult<ProviderCultureResult?>(null);

    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return NullResultTask;
        var isPublic = false;
        foreach (var p in PublicPrefixes)
        {
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { isPublic = true; break; }
        }
        if (!isPublic) return NullResultTask;

        // Respect an explicit ?ui-culture=xx / ?culture=xx override on the URL
        // itself — that's how the "Sprache" pill on the landings works and it
        // must beat the browser default when the visitor clicked it.
        var q = httpContext.Request.Query;
        var qCulture = q["ui-culture"].ToString();
        if (string.IsNullOrWhiteSpace(qCulture)) qCulture = q["culture"].ToString();
        if (!string.IsNullOrWhiteSpace(qCulture))
        {
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(qCulture));
        }

        var accept = httpContext.Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(accept)) return NullResultTask;

        // Parse "de-DE,de;q=0.9,en;q=0.8" — take the first token that we ship
        // as a SupportedUICulture. Options.SupportedUICultures is filled by
        // AddRequestLocalization; without it we can't do a safe match, so we
        // just hand back the raw first token and let downstream fall through
        // to the DefaultRequestCulture if it isn't shipped.
        var supported = Options?.SupportedUICultures?.Select(c => c.Name).ToList();
        foreach (var raw in accept.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = raw.Split(';', 2)[0].Trim();
            if (tag.Length == 0) continue;
            var shortTag = tag.Split('-', 2)[0];
            if (supported == null || supported.Count == 0)
            {
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(tag));
            }
            var exact = supported.FirstOrDefault(s => string.Equals(s, tag, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(exact));
            var prefix = supported.FirstOrDefault(s => string.Equals(s, shortTag, StringComparison.OrdinalIgnoreCase));
            if (prefix != null) return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(prefix));
            var loose = supported.FirstOrDefault(s => s.StartsWith(shortTag + "-", StringComparison.OrdinalIgnoreCase));
            if (loose != null) return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(loose));
        }
        return NullResultTask;
    }
}
