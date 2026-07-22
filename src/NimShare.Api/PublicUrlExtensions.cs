namespace NimShare.Api;

/// <summary>
/// v1.10.135 — kanonische, öffentliche Basis-URL für alle generierten Links
/// (Share /s/, Upload /u/, Signatur /sign/, Einladungen).
///
/// Vorher bauten alle Controller die URL aus <c>Request.Scheme</c> +
/// <c>Request.Host</c> — also aus dem Host, über den die Anfrage gerade kam.
/// Wird die App über mehrere Hosts erreicht (z. B. iOS via
/// nimshare.azurewebsites.net, Web via nimshare.com), landeten uneinheitliche
/// bzw. falsche Hosts in den Links.
///
/// Ist <c>App:PublicBaseUrl</c> gesetzt (appsettings oder App-Setting
/// <c>App__PublicBaseUrl</c> in Azure, z. B. "https://nimshare.com"), gewinnt
/// dieser Wert immer. Ohne Konfiguration bleibt das alte Verhalten
/// (Request-Host) erhalten — wichtig für generische Self-Host-Deployments.
/// </summary>
public static class PublicUrlExtensions
{
    public const string ConfigKey = "App:PublicBaseUrl";

    /// <summary>Basis ohne Trailing-Slash, z. B. "https://nimshare.com".</summary>
    public static string PublicBase(this HttpRequest req)
    {
        var cfg = req.HttpContext.RequestServices.GetService<IConfiguration>();
        var configured = cfg?[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim().TrimEnd('/');
        return $"{req.Scheme}://{req.Host}";
    }

    /// <summary>Baut eine absolute öffentliche URL für einen relativen Pfad.</summary>
    public static string PublicUrl(this HttpRequest req, string relativePath)
    {
        var p = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return req.PublicBase() + p;
    }
}
