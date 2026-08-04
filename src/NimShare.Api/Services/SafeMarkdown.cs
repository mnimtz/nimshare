using Markdig;

namespace NimShare.Api.Services;

/// <summary>
/// Shared hardened Markdown→HTML renderer for user-authored landing/theme text
/// (share, folder-share, sign and upload-request landing "BodyMarkdown").
///
/// <para>
/// CommonMark passes raw inline HTML — including &lt;script&gt; — straight through by
/// default. These landing pages render attacker-influenceable Markdown to anonymous
/// visitors and the app ships no Content-Security-Policy, so a default render is a
/// stored-XSS sink. <c>DisableHtml()</c> strips raw HTML tags so only safe Markdown
/// constructs survive. This mirrors the pipeline already used for share/upload
/// message text (ShareController / UploadRequestPublicController).
/// </para>
/// </summary>
public static class SafeMarkdown
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().DisableHtml().UseSoftlineBreakAsHardlineBreak().Build();

    /// <summary>Render user Markdown to HTML with raw-HTML passthrough disabled.</summary>
    public static string ToHtml(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);
}
