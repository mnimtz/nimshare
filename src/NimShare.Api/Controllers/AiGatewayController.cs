using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimShare.Api.Services;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

[Authorize(Policy = "WebUser")]
public class AiGatewayController : Controller
{
    private readonly IAiGatewayService _ai;
    private readonly ICurrentUserService _users;
    private readonly IStringLocalizer<SharedResources> _l;

    public AiGatewayController(IAiGatewayService ai, ICurrentUserService users, IStringLocalizer<SharedResources> l)
    {
        _ai = ai;
        _users = users;
        _l = l;
    }

    private async Task<bool> IsAdmin(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return me.Role == UserRole.Admin;
    }

    [HttpGet("/settings/ai")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var s = await _ai.LoadAsync(ct);
        return View(s);
    }

    public record SaveForm(AiProvider Provider, string? Model, string? Endpoint, string? ApiKey,
        bool EnableAutoSummary, bool EnableSmartTags, bool EnableSemanticSearch,
        bool EnableGuidedUploadRequests, bool EnableContentRiskDetection,
        bool EnableDraftedShareEmails, bool EnableChatWithFiles, bool EnableOcr);

    [HttpPost("/settings/ai")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromForm] SaveForm form, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var me = await _users.GetOrProvisionAsync(User, ct);
        var incoming = new AiGatewaySettings
        {
            Provider = form.Provider,
            Model = form.Model,
            Endpoint = form.Endpoint,
            EnableAutoSummary = form.EnableAutoSummary,
            EnableSmartTags = form.EnableSmartTags,
            EnableSemanticSearch = form.EnableSemanticSearch,
            EnableGuidedUploadRequests = form.EnableGuidedUploadRequests,
            EnableContentRiskDetection = form.EnableContentRiskDetection,
            EnableDraftedShareEmails = form.EnableDraftedShareEmails,
            EnableChatWithFiles = form.EnableChatWithFiles,
            EnableOcr = form.EnableOcr,
        };
        await _ai.SaveAsync(incoming, form.ApiKey, me.Id, ct);
        TempData["Notice"] = _l["notice.ai_saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    public record ListModelsResp(string[] Models, string? Error);

    /// <summary>Live model-listing per provider, using the currently-saved API
    /// key. Lets the operator pick from EVERY model the provider actually has
    /// available (Gemini has ~30, curated dropdown only covers ~6) instead of
    /// typing model IDs blind.</summary>
    [HttpGet("/api/v1/ai/list-models")]
    public async Task<IActionResult> ListModels([FromServices] IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var s = await _ai.LoadAsync(ct);
        var apiKey = await _ai.GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Ok(new ListModelsResp(Array.Empty<string>(), _l["ai.err.no_key"].Value));

        var http = httpFactory.CreateClient("nimshare-ai-listmodels");
        http.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            return s.Provider switch
            {
                AiProvider.Gemini => Ok(new ListModelsResp(await ListGeminiAsync(http, apiKey, ct), null)),
                AiProvider.OpenAi => Ok(new ListModelsResp(await ListOpenAiAsync(http, apiKey, "https://api.openai.com/v1/models", ct), null)),
                AiProvider.AzureOpenAi => Ok(new ListModelsResp(await ListAzureAsync(http, apiKey, s.Endpoint, _l, ct), null)),
                AiProvider.Anthropic => Ok(new ListModelsResp(await ListAnthropicAsync(http, apiKey, ct), null)),
                _ => Ok(new ListModelsResp(Array.Empty<string>(), _l["ai.err.no_listing"].Value)),
            };
        }
        catch (Exception ex)
        {
            return Ok(new ListModelsResp(Array.Empty<string>(), ex.Message));
        }
    }

    private static async Task<string[]> ListGeminiAsync(HttpClient http, string key, CancellationToken ct)
    {
        // Gemini also supports the API key via header, which keeps it out of
        // access logs / request telemetry. Query-string path is only there
        // for platforms without header support.
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");
        req.Headers.Add("x-goog-api-key", key);
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                // Only surface generateContent-capable ones; embedding models
                // would confuse the operator picking a chat model.
                if (m.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.EnumerateArray().Any(x => x.GetString() == "generateContent")
                    && m.TryGetProperty("name", out var name))
                {
                    // "models/gemini-2.5-flash-lite" → "gemini-2.5-flash-lite"
                    var s = name.GetString() ?? "";
                    if (s.StartsWith("models/")) s = s.Substring("models/".Length);
                    names.Add(s);
                }
            }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    private static async Task<string[]> ListOpenAiAsync(HttpClient http, string key, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var arr))
            foreach (var m in arr.EnumerateArray())
                if (m.TryGetProperty("id", out var id)) names.Add(id.GetString() ?? "");
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    /// <summary>SSRF guard shared between ListModels and the runtime provider
    /// factory — admin-supplied endpoint is only allowed to point at real
    /// Azure OpenAI / Cognitive Services hosts, over https.</summary>
    public static bool IsValidAzureOpenAiEndpoint(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var u)
        && u.Scheme == "https"
        && (u.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<string[]> ListAzureAsync(HttpClient http, string key, string? endpoint,
        IStringLocalizer<SharedResources> l, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException(l["ai.err.azure_no_endpoint"].Value);
        if (!IsValidAzureOpenAiEndpoint(endpoint))
            throw new InvalidOperationException(l["ai.err.azure_bad_endpoint"].Value);
        var url = endpoint.TrimEnd('/') + "/openai/deployments?api-version=2023-03-15-preview";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("api-key", key);
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var arr))
            foreach (var m in arr.EnumerateArray())
                if (m.TryGetProperty("id", out var id)) names.Add(id.GetString() ?? "");
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    private static async Task<string[]> ListAnthropicAsync(HttpClient http, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var arr))
            foreach (var m in arr.EnumerateArray())
                if (m.TryGetProperty("id", out var id)) names.Add(id.GetString() ?? "");
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }
}
