namespace NimShare.Api.Services;

/// <summary>
/// Sehr grobe User-Agent → Device-Type Klassifizierung. Kein Full UA-
/// Parser (das wäre eine Library-Abhängigkeit für einen Datenpunkt),
/// sondern ein 15-Zeilen-Heuristik-Snippet das Desktop / Mobile /
/// Tablet / Bot unterscheidet. Reicht für Marcus's Beweis-Report-
/// Use-Case — wir speichern damit z.B. "der Signer war auf einem
/// Mobiltelefon" und nicht "Chrome 121 on Windows NT 10.0 build 22631".
/// </summary>
public static class DeviceTypeParser
{
    public static string Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown";
        var ua = userAgent.ToLowerInvariant();
        // Bots first — sonst matchen sie oft auch als "Mobile" wenn ihr
        // UA-String das behauptet (Googlebot Mobile etc.).
        if (ua.Contains("bot") || ua.Contains("crawl") || ua.Contains("spider") ||
            ua.Contains("slurp") || ua.Contains("curl/") || ua.Contains("wget/") ||
            ua.Contains("httpclient") || ua.Contains("headlesschrome"))
            return "Bot";
        // Tablets vor Mobile prüfen — "iPad" enthält kein "Mobile"-Token,
        // "Android tablet" wird von Vendors sehr unterschiedlich markiert.
        if (ua.Contains("ipad") || ua.Contains("tablet") ||
            (ua.Contains("android") && !ua.Contains("mobile")))
            return "Tablet";
        if (ua.Contains("iphone") || ua.Contains("ipod") ||
            ua.Contains("android") || ua.Contains("mobile") ||
            ua.Contains("windows phone") || ua.Contains("blackberry"))
            return "Mobile";
        return "Desktop";
    }
}
