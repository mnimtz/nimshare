using PdfSharpCore.Fonts;

namespace NimShare.Api.Services;

/// <summary>
/// v1.11.47 — PdfSharpCore has no usable built-in font on Linux (no GDI to
/// fall back on): without a registered IFontResolver, requests for "Arial"/
/// "Courier New" silently resolve to whatever internal placeholder
/// PdfSharpCore substitutes — a serif/italic-looking stand-in that made the
/// Signature-Audit-PDF look broken/unfinished (Marcus's report). Fix:
/// resolve to Liberation Sans/Mono, which the Dockerfile already installs
/// system-wide (fonts-liberation package, originally added for the
/// LibreOffice office-preview feature) and which are metric-compatible,
/// openly-licensed replacements for Arial/Courier New. Falls back to a
/// couple of macOS system paths so local dev outside the container doesn't
/// throw either.
/// </summary>
public sealed class LiberationPdfFontResolver : IFontResolver
{
    private static readonly string[] SearchDirs =
    {
        "/usr/share/fonts/truetype/liberation",   // Debian/Ubuntu — see Dockerfile
        "/usr/share/fonts/liberation",             // some other distros
        "/System/Library/Fonts/Supplemental",      // macOS — ships real Arial/Courier New
        "/Library/Fonts",                          // macOS — user/site-installed fonts
    };

    private static readonly Dictionary<string, string[]> CandidateFiles = new()
    {
        ["Sans#Regular"]    = new[] { "LiberationSans-Regular.ttf", "Arial.ttf" },
        ["Sans#Bold"]       = new[] { "LiberationSans-Bold.ttf", "Arial Bold.ttf" },
        ["Sans#Italic"]     = new[] { "LiberationSans-Italic.ttf", "Arial Italic.ttf" },
        ["Sans#BoldItalic"] = new[] { "LiberationSans-BoldItalic.ttf", "Arial Bold Italic.ttf" },
        ["Mono#Regular"]    = new[] { "LiberationMono-Regular.ttf", "Courier New.ttf" },
        ["Mono#Bold"]       = new[] { "LiberationMono-Bold.ttf", "Courier New Bold.ttf" },
    };

    private readonly Dictionary<string, byte[]> _cache = new();

    public string DefaultFontName => "Sans#Regular";

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var family = familyName.Equals("Courier New", StringComparison.OrdinalIgnoreCase) ? "Mono" : "Sans";
        var style = (isBold, isItalic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        };
        var key = $"{family}#{style}";
        // Mono has no Italic/BoldItalic in our candidate list — fall back to Regular.
        if (!CandidateFiles.ContainsKey(key)) key = $"{family}#Regular";
        return new FontResolverInfo(key);
    }

    public byte[] GetFont(string faceName)
    {
        if (_cache.TryGetValue(faceName, out var cached)) return cached;
        if (!CandidateFiles.TryGetValue(faceName, out var fileNames))
            fileNames = CandidateFiles["Sans#Regular"];
        foreach (var dir in SearchDirs)
        {
            foreach (var fn in fileNames)
            {
                var path = Path.Combine(dir, fn);
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    _cache[faceName] = bytes;
                    return bytes;
                }
            }
        }
        throw new FileNotFoundException(
            $"No font file found for '{faceName}'. Expected Liberation fonts under " +
            "/usr/share/fonts/truetype/liberation (installed via the Dockerfile's " +
            "fonts-liberation package) or a macOS system font as local-dev fallback.");
    }
}
