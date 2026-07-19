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
        // ApiKeyDecrypted is what the provider services use internally, so
        // this stays in sync with what's actually configured.
        var apiKey = await _ai.GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Ok(new ListModelsResp(Array.Empty<string>(), "Kein API-Key gespeichert."));

        var http = httpFactory.CreateClient("nimshare-ai-listmodels");
        http.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            return s.Provider switch
            {
                AiProvider.Gemini => Ok(new ListModelsResp(await ListGeminiAsync(http, apiKey, ct), null)),
                AiProvider.OpenAi => Ok(new ListModelsResp(await ListOpenAiAsync(http, apiKey, "https://api.openai.com/v1/models", ct), null)),
                AiProvider.AzureOpenAi => Ok(new ListModelsResp(await ListAzureAsync(http, apiKey, s.Endpoint, ct), null)),
                AiProvider.Anthropic => Ok(new ListModelsResp(await ListAnthropicAsync(http, apiKey, ct), null)),
                _ => Ok(new ListModelsResp(Array.Empty<string>(), "Provider unterstützt kein Model-Listing.")),
            };
        }
        catch (Exception ex)
        {
            return Ok(new ListModelsResp(Array.Empty<string>(), ex.Message));
        }
    }

    private static async Task<string[]> ListGeminiAsync(HttpClient http, string key, CancellationToken ct)
    {
        var resp = await http.GetAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={key}&pageSize=200", ct);
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

    private static async Task<string[]> ListAzureAsync(HttpClient http, string key, string? endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Azure OpenAI Endpoint fehlt.");
        // SSRF guard: the endpoint field is admin-configurable, so a rogue
        // admin (or a compromised session) could pivot the app-service into
        // hitting IMDS (169.254.169.254) or any internal host with the saved
        // API key attached. Restrict to real Azure OpenAI hosts.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var eUri)
            || eUri.Scheme != "https"
            || !(eUri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                 || eUri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Endpoint must be an https://*.openai.azure.com or *.cognitiveservices.azure.com URL.");
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
