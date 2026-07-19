using System.Text.RegularExpressions;

namespace NimShare.Api.Services;

/// <summary>
/// Renders NimShare email templates by resolving Handlebars-style
/// <c>{{placeholder}}</c> tokens against a supplied context. Only the tokens
/// listed on <see cref="AvailablePlaceholders"/> are recognised; unknown
/// tokens are left in place so authors can spot typos.
/// </summary>
public static class EmailTemplateRenderer
{
    public static readonly string[] AvailablePlaceholders = new[]
    {
        "recipient.name",
        "recipient.email",
        "sender.name",
        "sender.email",
        "sender.action",   // "sign" / "review" depending on participant role
        "doc.title",
        "doc.name",
        "url",
        "message",
    };

    private static readonly Regex TokenRx = new(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}",
        RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, string?> context)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return TokenRx.Replace(template, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            return context.TryGetValue(key, out var value) ? (value ?? "") : m.Value;
        });
    }
}
