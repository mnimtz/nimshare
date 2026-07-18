using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NimShare.Core.Data;
using NimShare.Core.Entities;
using UglyToad.PdfPig;

namespace NimShare.Api.Services;

public record AiClassification(string[] Tags, string? RiskFlag);

public interface IAiProvider
{
    Task<string?> SummarizeAsync(string text, string language, CancellationToken ct = default);
    Task<AiClassification?> ClassifyAsync(string filename, string text, CancellationToken ct = default);
    Task<string?> DraftShareEmailAsync(string senderName, string fileName, string? context, string language, CancellationToken ct = default);
}

public class NullAiProvider : IAiProvider
{
    public Task<string?> SummarizeAsync(string text, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<AiClassification?> ClassifyAsync(string filename, string text, CancellationToken ct = default) => Task.FromResult<AiClassification?>(null);
    public Task<string?> DraftShareEmailAsync(string senderName, string fileName, string? context, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>
/// OpenAI Chat Completions client (also Azure OpenAI, with a different endpoint).
/// Small, single-endpoint wrapper — good enough for summary/classify/draft.
/// </summary>
public class OpenAiProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly bool _isAzure;

    public OpenAiProvider(HttpClient http, string apiKey, string model, string? azureEndpoint)
    {
        _http = http;
        _model = string.IsNullOrEmpty(model) ? "gpt-4o-mini" : model;
        _isAzure = !string.IsNullOrEmpty(azureEndpoint);
        _endpoint = _isAzure
            ? $"{azureEndpoint!.TrimEnd('/')}/openai/deployments/{_model}/chat/completions?api-version=2024-06-01"
            : "https://api.openai.com/v1/chat/completions";
        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.Remove("api-key");
        if (_isAzure)
            _http.DefaultRequestHeaders.Add("api-key", apiKey);
        else
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public Task<string?> SummarizeAsync(string text, string language, CancellationToken ct = default) =>
        ChatAsync(
            system: $"You are a concise assistant. Write a 2-3 sentence summary in the language whose ISO code is '{language}'. No markdown, no preamble.",
            user: text.Length > 8000 ? text[..8000] : text,
            temperature: 0.2,
            ct);

    public async Task<AiClassification?> ClassifyAsync(string filename, string text, CancellationToken ct = default)
    {
        var json = await ChatAsync(
            system: "Return a compact JSON object with two keys: \"tags\" (array of 2-5 short lower-case keywords) and \"risk\" (one of: clean, pii, credit-card, secret, unknown). No markdown, only JSON.",
            user: $"Filename: {filename}\n\n{(text.Length > 2000 ? text[..2000] : text)}",
            temperature: 0.1,
            ct);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            var tags = doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Take(5).ToArray()
                : Array.Empty<string>();
            var risk = doc.RootElement.TryGetProperty("risk", out var r) ? r.GetString() : null;
            return new AiClassification(tags, risk);
        }
        catch { return null; }
    }

    public Task<string?> DraftShareEmailAsync(string senderName, string fileName, string? context, string language, CancellationToken ct = default) =>
        ChatAsync(
            system: $"You are drafting a short, warm cover email to accompany a file share. Write in the language whose ISO code is '{language}'. Keep it under 6 sentences. End with a signature that uses the sender's name. No markdown.",
            user: $"Sender: {senderName}\nFile: {fileName}\nContext: {context ?? "—"}",
            temperature: 0.6,
            ct);

    private async Task<string?> ChatAsync(string system, string user, double temperature, CancellationToken ct)
    {
        var payload = new
        {
            model = _isAzure ? (object?)null : _model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user },
            },
            temperature,
            max_tokens = 400,
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(_endpoint, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch { return null; }
    }
}

/// <summary>Google Gemini via generateContent API.</summary>
public class GeminiProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;

    public GeminiProvider(HttpClient http, string apiKey, string model)
    {
        _http = http;
        _model = string.IsNullOrEmpty(model) ? "gemini-2.0-flash" : model;
        _apiKey = apiKey;
    }

    public Task<string?> SummarizeAsync(string text, string language, CancellationToken ct = default) =>
        GenerateAsync(
            $"Summarize the following in 2-3 sentences in the language whose ISO code is '{language}'. No preamble.\n\n{(text.Length > 8000 ? text[..8000] : text)}",
            0.2, ct);

    public async Task<AiClassification?> ClassifyAsync(string filename, string text, CancellationToken ct = default)
    {
        var json = await GenerateAsync(
            $"Return only JSON: {{\"tags\":[…2-5 short keywords…],\"risk\":\"clean|pii|credit-card|secret|unknown\"}}\n\nFilename: {filename}\n\n{(text.Length > 2000 ? text[..2000] : text)}",
            0.1, ct);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var doc = JsonDocument.Parse(json[start..(end + 1)]);
            var tags = doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Take(5).ToArray()
                : Array.Empty<string>();
            var risk = doc.RootElement.TryGetProperty("risk", out var r) ? r.GetString() : null;
            return new AiClassification(tags, risk);
        }
        catch { return null; }
    }

    public Task<string?> DraftShareEmailAsync(string senderName, string fileName, string? context, string language, CancellationToken ct = default) =>
        GenerateAsync(
            $"Draft a short, warm cover email (max 6 sentences, no markdown) in the language whose ISO code is '{language}' from {senderName} accompanying the file '{fileName}'.{(string.IsNullOrEmpty(context) ? "" : $" Context: {context}.")} End with a signature that uses the sender's name.",
            0.6, ct);

    private async Task<string?> GenerateAsync(string prompt, double temperature, CancellationToken ct)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature, maxOutputTokens = 400 },
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
        }
        catch { return null; }
    }
}

/// <summary>Anthropic Claude Messages API.</summary>
public class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;

    public AnthropicProvider(HttpClient http, string apiKey, string model)
    {
        _http = http;
        _model = string.IsNullOrEmpty(model) ? "claude-3-5-haiku-latest" : model;
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public Task<string?> SummarizeAsync(string text, string language, CancellationToken ct = default) =>
        MessagesAsync(
            $"You are a concise assistant. Write a 2-3 sentence summary in the language whose ISO code is '{language}'. No markdown, no preamble.",
            text.Length > 8000 ? text[..8000] : text, 0.2, ct);

    public async Task<AiClassification?> ClassifyAsync(string filename, string text, CancellationToken ct = default)
    {
        var json = await MessagesAsync(
            "Return a compact JSON object with two keys: \"tags\" (array of 2-5 short lower-case keywords) and \"risk\" (one of: clean, pii, credit-card, secret, unknown). No markdown, only JSON.",
            $"Filename: {filename}\n\n{(text.Length > 2000 ? text[..2000] : text)}", 0.1, ct);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var doc = JsonDocument.Parse(json[start..(end + 1)]);
            var tags = doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).Take(5).ToArray()
                : Array.Empty<string>();
            var risk = doc.RootElement.TryGetProperty("risk", out var r) ? r.GetString() : null;
            return new AiClassification(tags, risk);
        }
        catch { return null; }
    }

    public Task<string?> DraftShareEmailAsync(string senderName, string fileName, string? context, string language, CancellationToken ct = default) =>
        MessagesAsync(
            $"You are drafting a short, warm cover email in the language whose ISO code is '{language}'. Max 6 sentences, no markdown, end with a signature.",
            $"Sender: {senderName}\nFile: {fileName}\nContext: {context ?? "—"}", 0.6, ct);

    private async Task<string?> MessagesAsync(string system, string user, double temperature, CancellationToken ct)
    {
        var payload = new
        {
            model = _model,
            max_tokens = 400,
            temperature,
            system,
            messages = new object[] { new { role = "user", content = user } },
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync("https://api.anthropic.com/v1/messages", body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        }
        catch { return null; }
    }
}

/// <summary>Loads the persisted AiGatewaySettings and creates an IAiProvider on demand.</summary>
public interface IAiGatewayService
{
    Task<AiGatewaySettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AiGatewaySettings incoming, string? plainApiKey, Guid updatedBy, CancellationToken ct = default);
    Task<IAiProvider> CreateProviderAsync(CancellationToken ct = default);
    Task<string?> ExtractTextAsync(string blobPath, string contentType, IBlobStorageService blobs, CancellationToken ct = default);
}

public class AiGatewayService : IAiGatewayService
{
    private const string ProtectorPurpose = "NimShare.AiGateway.v1";

    private readonly NimShareDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AiGatewayService> _log;

    public AiGatewayService(NimShareDbContext db, IDataProtectionProvider dpp, IHttpClientFactory http, ILogger<AiGatewayService> log)
    {
        _db = db;
        _protector = dpp.CreateProtector(ProtectorPurpose);
        _httpFactory = http;
        _log = log;
    }

    public async Task<AiGatewaySettings> LoadAsync(CancellationToken ct = default)
    {
        var s = await _db.AiGateways.FirstOrDefaultAsync(x => x.Id == AiGatewaySettings.SingletonId, ct);
        if (s is null)
        {
            s = new AiGatewaySettings();
            _db.AiGateways.Add(s);
            await _db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task SaveAsync(AiGatewaySettings incoming, string? plainApiKey, Guid updatedBy, CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        s.Provider = incoming.Provider;
        s.Model = incoming.Model;
        s.Endpoint = incoming.Endpoint;
        if (!string.IsNullOrEmpty(plainApiKey)) s.ApiKeyEncrypted = _protector.Protect(plainApiKey);
        s.EnableAutoSummary = incoming.EnableAutoSummary;
        s.EnableSmartTags = incoming.EnableSmartTags;
        s.EnableSemanticSearch = incoming.EnableSemanticSearch;
        s.EnableGuidedUploadRequests = incoming.EnableGuidedUploadRequests;
        s.EnableContentRiskDetection = incoming.EnableContentRiskDetection;
        s.EnableDraftedShareEmails = incoming.EnableDraftedShareEmails;
        s.EnableChatWithFiles = incoming.EnableChatWithFiles;
        s.EnableOcr = incoming.EnableOcr;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedByUserId = updatedBy;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IAiProvider> CreateProviderAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        if (s.Provider == AiProvider.Disabled || string.IsNullOrEmpty(s.ApiKeyEncrypted))
            return new NullAiProvider();
        string apiKey;
        try { apiKey = _protector.Unprotect(s.ApiKeyEncrypted); }
        catch { return new NullAiProvider(); }
        var http = _httpFactory.CreateClient();
        return s.Provider switch
        {
            AiProvider.OpenAi => new OpenAiProvider(http, apiKey, s.Model ?? "gpt-4o-mini", null),
            AiProvider.AzureOpenAi => new OpenAiProvider(http, apiKey, s.Model ?? "gpt-4o-mini", s.Endpoint),
            AiProvider.Gemini => new GeminiProvider(http, apiKey, s.Model ?? "gemini-2.0-flash"),
            AiProvider.Anthropic => new AnthropicProvider(http, apiKey, s.Model ?? "claude-3-5-haiku-latest"),
            _ => new NullAiProvider(),
        };
    }

    /// <summary>Extracts plain text from a blob. Supports text/*, application/pdf, application/json.</summary>
    public async Task<string?> ExtractTextAsync(string blobPath, string contentType, IBlobStorageService blobs, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            await blobs.DownloadToAsync(blobPath, ms, ct);
            ms.Position = 0;

            if (contentType.StartsWith("text/") || contentType == "application/json")
                return Encoding.UTF8.GetString(ms.ToArray());

            if (contentType == "application/pdf")
            {
                using var pdf = PdfDocument.Open(ms);
                var sb = new StringBuilder();
                var pages = 0;
                foreach (var page in pdf.GetPages())
                {
                    if (pages++ > 30) break; // cap the effort — first 30 pages
                    sb.AppendLine(page.Text);
                    if (sb.Length > 20_000) break;
                }
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Text extraction failed for {Path}", blobPath);
        }
        return null;
    }
}
