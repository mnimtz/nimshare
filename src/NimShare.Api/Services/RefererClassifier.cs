namespace NimShare.Api.Services;

/// <summary>
/// v1.11.14 — Klassifiziert die "Herkunft" eines Link-Zugriffs. Ersetzt die
/// bisherige, doppelt vorhandene "new Uri(referer).Host"-Logik in
/// LinkReportController und LinksController. Erkennt bekannte automatisierte
/// Link-Vorschau-Abrufe (Microsoft Teams, Slack, WhatsApp etc.) — diese
/// tauchen sonst identisch zu einem echten Besuch in der "Herkunft"-Liste
/// auf, obwohl kein Mensch geklickt hat.
/// </summary>
public static class RefererClassifier
{
    // (Teilstring im Referer-Host, Klartext-Label). Contains-Match statt
    // exaktem Host-Vergleich, weil z.B. Teams' CDN-Knoten mit wechselnden
    // Subdomain-Präfixen vor "onecdn.static.microsoft" auftauchen.
    private static readonly (string Pattern, string Label)[] KnownFetcherHosts =
    {
        ("onecdn.static.microsoft", "Microsoft Teams"),
        ("teams.microsoft.com", "Microsoft Teams"),
        ("safelinks.protection.outlook.com", "Outlook (Safe Links)"),
        ("slack.com", "Slack"),
        ("slack-redir.net", "Slack"),
        ("whatsapp.net", "WhatsApp"),
        ("whatsapp.com", "WhatsApp"),
        ("wa.me", "WhatsApp"),
        ("facebook.com", "Facebook"),
        ("fbcdn.net", "Facebook"),
        ("t.co", "X (Twitter)"),
        ("linkedin.com", "LinkedIn"),
        ("googleusercontent.com", "Google"),
        ("discordapp.com", "Discord"),
        ("discord.com", "Discord"),
        ("telegram.org", "Telegram"),
    };

    // Sekundäres Signal: passt der Referer-Host auf keine bekannte Domain,
    // aber der Besucher sitzt nachweislich auf der Infrastruktur eines
    // dieser Anbieter UND der User-Agent sieht nach Bot/Fetcher aus (siehe
    // DeviceTypeParser), ist das ein starkes Indiz für einen automatisierten
    // Abruf ohne aussagekräftigen Referer (viele Preview-Bots senden gar
    // keinen).
    private static readonly (string Pattern, string Label)[] KnownFetcherIsps =
    {
        ("microsoft corporation", "Microsoft"),
        ("slack technologies", "Slack"),
        ("meta platforms", "Meta (Facebook/WhatsApp)"),
        ("google llc", "Google"),
        ("linkedin corporation", "LinkedIn"),
        ("twitter", "X (Twitter)"),
        ("discord inc", "Discord"),
    };

    public static string NormaliseHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try { return new Uri(raw).Host; }
        catch { return ""; }
    }

    public sealed record Classification(string Host, string DisplayLabel, bool IsLikelyAutomatedFetch);

    /// <summary>Null wenn kein (parsbarer) Referer vorliegt — der Aufrufer
    /// zeigt in dem Fall den lokalisierten "(direct)"-Text.</summary>
    public static Classification? Classify(string? rawReferer, string? userAgent, string? isp)
    {
        var host = NormaliseHost(rawReferer);
        if (string.IsNullOrEmpty(host)) return null;

        var hostMatch = KnownFetcherHosts.FirstOrDefault(f => host.Contains(f.Pattern, StringComparison.OrdinalIgnoreCase));
        if (hostMatch.Label is not null)
            return new Classification(host, hostMatch.Label, true);

        var isBotUa = DeviceTypeParser.Classify(userAgent) == "Bot";
        if (isBotUa && !string.IsNullOrEmpty(isp))
        {
            var ispMatch = KnownFetcherIsps.FirstOrDefault(f => isp.Contains(f.Pattern, StringComparison.OrdinalIgnoreCase));
            if (ispMatch.Label is not null)
                return new Classification(host, host, true);
        }

        return new Classification(host, host, isBotUa);
    }
}
