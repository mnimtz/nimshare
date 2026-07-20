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
    /// <summary>Embed short text (name + first 2 KB) to a fixed-length float vector for semantic search. Returns null if the provider doesn't support embeddings.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
    /// <summary>Answer a question grounded in the caller's file corpus. Text passages are the retrieved chunks.</summary>
    Task<string?> ChatAnswerAsync(string question, IEnumerable<string> passages, string language, CancellationToken ct = default);
    /// <summary>Personalise an upload-request cover email for a specific recipient.</summary>
    Task<string?> DraftUploadRequestAsync(string senderName, string recipientEmail, string? context, string language, CancellationToken ct = default);
    /// <summary>Describe an image (Vision). Returns null if the provider or the configured model doesn't support vision.</summary>
    Task<string?> DescribeImageAsync(byte[] imageBytes, string mimeType, string language, CancellationToken ct = default);
}

public class NullAiProvider : IAiProvider
{
    public Task<string?> SummarizeAsync(string text, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<AiClassification?> ClassifyAsync(string filename, string text, CancellationToken ct = default) => Task.FromResult<AiClassification?>(null);
    public Task<string?> DraftShareEmailAsync(string senderName, string fileName, string? context, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
    public Task<string?> ChatAnswerAsync(string question, IEnumerable<string> passages, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> DraftUploadRequestAsync(string senderName, string recipientEmail, string? context, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> DescribeImageAsync(byte[] imageBytes, string mimeType, string language, CancellationToken ct = default) => Task.FromResult<string?>(null);
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
    public string? LastError { get; private set; }
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

    public Task<string?> DraftUploadRequestAsync(string senderName, string recipientEmail, string? context, string language, CancellationToken ct = default) =>
        ChatAsync(
            system: $"You are drafting a short cover email asking the recipient to upload a file. Match the tone/register to the recipient's email domain (formal for corporate, warmer for @gmail/@outlook). Write in the language whose ISO code is '{language}'. Under 5 sentences. End with the sender's name. No markdown.",
            user: $"Sender: {senderName}\nRecipient: {recipientEmail}\nContext: {context ?? "—"}",
            temperature: 0.6,
            ct);

    public Task<string?> ChatAnswerAsync(string question, IEnumerable<string> passages, string language, CancellationToken ct = default)
    {
        var joined = string.Join("\n---\n", passages.Take(6));
        return ChatAsync(
            system: $"Answer the user's question using ONLY the provided passages. If the answer isn't in them, say so. Reply in the language whose ISO code is '{language}'. Cite passage numbers like [1] where helpful.",
            user: $"Passages:\n{joined}\n\nQuestion: {question}",
            temperature: 0.2,
            ct);
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = _isAzure
            ? _endpoint.Replace("chat/completions", "embeddings")
            : "https://api.openai.com/v1/embeddings";
        var payload = new { model = _isAzure ? (object?)null : "text-embedding-3-small", input = text.Length > 8000 ? text[..8000] : text };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var s = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(s);
            var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            var vec = new float[arr.GetArrayLength()];
            for (int i = 0; i < vec.Length; i++) vec[i] = arr[i].GetSingle();
            return vec;
        }
        catch { return null; }
    }

    public async Task<string?> DescribeImageAsync(byte[] imageBytes, string mimeType, string language, CancellationToken ct = default)
    {
        // OpenAI Chat Completions supports vision via "image_url" content parts
        // with a data: URL. gpt-4o and gpt-4o-mini handle it; older models don't.
        var dataUrl = "data:" + mimeType + ";base64," + Convert.ToBase64String(imageBytes);
        var payload = new
        {
            model = _isAzure ? (object?)null : _model,
            messages = new object[]
            {
                new { role = "system", content = $"Describe what is visible in the image in 2-4 short sentences. Reply in the language whose ISO code is '{language}'. No preamble." },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        // Prompt text stays English so it doesn't bias the
                        // model's output language — the actual reply language
                        // is dictated by the system message via ISO code.
                        new { type = "text", text = "Please describe what is in this image." },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    }
                }
            },
            temperature = 0.3,
            max_tokens = 400,
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(_endpoint, body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"OpenAI {(int)resp.StatusCode} calling {_model}: {text[..Math.Min(400, text.Length)]}";
                return null;
            }
            var doc = JsonDocument.Parse(text);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            // Guardrails / content-policy refusals surface as "refusal" instead
            // of "content"; expose that so the operator sees WHY the summary
            // came back empty.
            if (msg.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
            {
                LastError = "Model refused: " + refusal.GetString();
                return null;
            }
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
            {
                LastError = "Model returned no content — likely the current model does not support vision. Configured model: " + _model;
                return null;
            }
            var result = content.GetString();
            if (string.IsNullOrWhiteSpace(result))
            {
                LastError = "Model returned empty content. Configured model: " + _model;
                return null;
            }
            return result;
        }
        catch (Exception ex) { LastError = "vision exception (" + _model + "): " + ex.Message; return null; }
    }

    private async Task<string?> ChatAsync(string system, string user, double temperature, CancellationToken ct)
    {
        LastError = null;
        var payload = new
        {
            model = _isAzure ? (object?)null : _model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user },
            },
            temperature,
            max_tokens = 800,
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(_endpoint, body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // v1.10.19: extract the OpenAI-style error.message from the
                // response body so the caller can surface it instead of the
                // generic "check API key, quota, or model availability."
                string reason = text.Length > 500 ? text[..500] : text;
                try
                {
                    var errDoc = JsonDocument.Parse(text);
                    if (errDoc.RootElement.TryGetProperty("error", out var e)
                        && e.TryGetProperty("message", out var m))
                        reason = m.GetString() ?? reason;
                }
                catch { }
                LastError = $"OpenAI HTTP {(int)resp.StatusCode} ({_model}): {reason}";
                return null;
            }
            var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0)
            {
                LastError = $"OpenAI {_model}: keine choices in Response (Content-Filter oder Modell antwortet nicht).";
                return null;
            }
            if (!choices[0].TryGetProperty("message", out var msg)
                || !msg.TryGetProperty("content", out var content))
            {
                LastError = $"OpenAI {_model}: choices[0].message.content fehlt.";
                return null;
            }
            var value = content.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                var finish = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
                LastError = $"OpenAI {_model}: leerer content (finish_reason={finish ?? "unknown"}). Content-Filter oder Modell nicht verfügbar.";
                return null;
            }
            return value;
        }
        catch (Exception ex)
        {
            LastError = $"OpenAI {_model} Exception: {ex.Message}";
            return null;
        }
    }
}

/// <summary>Google Gemini via generateContent API.</summary>
public class GeminiProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiKey;

    /// <summary>Last non-success diagnostic (HTTP status, safety block, empty
    /// output). Callers surface this in 502 Problem-details instead of the
    /// generic "no result" — v1.10.13 makes Vision failures diagnosable for
    /// Gemini the same way OpenAiProvider has done since v1.4.3.</summary>
    public string? LastError { get; private set; }

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

    public Task<string?> DraftUploadRequestAsync(string senderName, string recipientEmail, string? context, string language, CancellationToken ct = default) =>
        GenerateAsync(
            $"Draft a short cover email in the language whose ISO code is '{language}' from {senderName} asking {recipientEmail} to upload a file. Adapt tone to the recipient's email domain.{(string.IsNullOrEmpty(context) ? "" : $" Context: {context}.")} Under 5 sentences, no markdown.",
            0.6, ct);

    public Task<string?> ChatAnswerAsync(string question, IEnumerable<string> passages, string language, CancellationToken ct = default)
    {
        var joined = string.Join("\n---\n", passages.Take(6));
        return GenerateAsync(
            $"Answer using ONLY these passages. Reply in ISO '{language}'. Cite passage numbers like [1] where helpful.\n\nPassages:\n{joined}\n\nQuestion: {question}",
            0.2, ct);
    }

    public async Task<string?> DescribeImageAsync(byte[] imageBytes, string mimeType, string language, CancellationToken ct = default)
    {
        // Gemini generateContent accepts inline_data with base64. gemini-2.0-*
        // and 2.5-* families handle vision natively; 2.5 needs thinkingBudget=0
        // or the token budget is consumed by internal reasoning before any
        // text is emitted (leaves parts[] empty and the caller sees "empty").
        object visionConfig = ModelHasThinkingTokens()
            ? new { temperature = 0.3, maxOutputTokens = 2048, thinkingConfig = new { thinkingBudget = 0 } }
            : new { temperature = 0.3, maxOutputTokens = 2048 };
        // v1.10.18: Safety-Filter auf BLOCK_ONLY_HIGH herunterschrauben. Der
        // Default (BLOCK_MEDIUM_AND_ABOVE) blockt Bilder mit Personen / Gesichtern
        // sehr aggressiv → "keine candidates" ohne verwertbaren Grund. Für ein
        // Auto-Summary-Feature ist "beschreibe was im Bild ist" harmlos genug
        // dass wir den mildesten Schwellwert nutzen. HARM_CATEGORY_CIVIC_INTEGRITY
        // gibt es nicht als Kategorie — nur die vier klassischen.
        var safety = new object[]
        {
            new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_ONLY_HIGH" },
            new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_ONLY_HIGH" },
            new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_ONLY_HIGH" },
            new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_ONLY_HIGH" },
        };
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = $"Describe what is visible in the image in 2-4 sentences. Reply in the language whose ISO code is '{language}'. No preamble." },
                        new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(imageBytes) } },
                    }
                }
            },
            generationConfig = visionConfig,
            safetySettings = safety,
        };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        LastError = null;
        try
        {
            var resp = await _http.PostAsync(url, body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string reason = text.Length > 500 ? text[..500] : text;
                try
                {
                    var errDoc = JsonDocument.Parse(text);
                    if (errDoc.RootElement.TryGetProperty("error", out var e)
                        && e.TryGetProperty("message", out var m))
                        reason = m.GetString() ?? reason;
                }
                catch { }
                LastError = $"Gemini Vision HTTP {(int)resp.StatusCode} ({_model}): {reason}";
                return null;
            }
            var doc = JsonDocument.Parse(text);
            // Check promptFeedback first — safety blocks return here with no
            // candidates and the blockReason is more useful than a generic
            // "no candidates" message.
            string? promptBlockReason = null;
            if (doc.RootElement.TryGetProperty("promptFeedback", out var pf)
                && pf.TryGetProperty("blockReason", out var br))
                promptBlockReason = br.GetString();
            if (!doc.RootElement.TryGetProperty("candidates", out var cands)
                || cands.GetArrayLength() == 0)
            {
                LastError = promptBlockReason is not null
                    ? $"Gemini Vision {_model}: durch Safety-Filter blockiert (Grund: {promptBlockReason}). safetySettings=BLOCK_ONLY_HIGH ist bereits gesetzt — Google's Modell filtert das Bild trotzdem raus. Anderes Modell / anderes Bild versuchen."
                    : $"Gemini Vision {_model}: keine candidates + kein blockReason (Modell ohne Vision-Support, ungültiges Bild, oder Quota erschöpft). Prüfe Response im Server-Log.";
                Console.Error.WriteLine($"[GeminiVision] no-candidates. Model={_model} full-response={(text.Length > 1500 ? text[..1500] : text)}");
                return null;
            }
            var c0 = cands[0];
            string? finishReason = null;
            if (c0.TryGetProperty("finishReason", out var fr)) finishReason = fr.GetString();
            int thinkingTokens = 0;
            if (doc.RootElement.TryGetProperty("usageMetadata", out var um)
                && um.TryGetProperty("thoughtsTokenCount", out var tt))
                thinkingTokens = tt.GetInt32();
            // finishReason=SAFETY means the model refused mid-generation.
            if (string.Equals(finishReason, "SAFETY", StringComparison.OrdinalIgnoreCase))
            {
                LastError = $"Gemini Vision {_model}: durch Safety-Filter mid-generation abgebrochen (finishReason=SAFETY). Bild enthält Content, den das Modell nicht beschreiben will.";
                return null;
            }
            if (!c0.TryGetProperty("content", out var ct2)
                || !ct2.TryGetProperty("parts", out var ps)
                || ps.GetArrayLength() == 0
                || !ps[0].TryGetProperty("text", out var tx))
            {
                LastError = string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
                    ? $"Gemini Vision {_model}: Output-Budget verbraucht ({thinkingTokens} thinking-tokens). thinkingBudget=0 ist gesetzt — falls trotzdem MAX_TOKENS: gemini-2.5-pro versuchen (mehr Kapazität)."
                    : $"Gemini Vision {_model}: Antwort ohne text-Feld (finishReason={finishReason ?? "unknown"}).";
                Console.Error.WriteLine($"[GeminiVision] no-text. Model={_model} finishReason={finishReason} response={(text.Length > 1500 ? text[..1500] : text)}");
                return null;
            }
            var value = tx.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                LastError = $"Gemini Vision {_model}: leerer Text (finishReason={finishReason}) — vermutlich Safety-Soft-Filter.";
                return null;
            }
            return value;
        }
        catch (Exception ex)
        {
            LastError = $"Gemini Vision {_model} Exception: {ex.Message}";
            return null;
        }
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={_apiKey}";
        var payload = new { content = new { parts = new[] { new { text = text.Length > 8000 ? text[..8000] : text } } } };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var s = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(s);
            var arr = doc.RootElement.GetProperty("embedding").GetProperty("values");
            var vec = new float[arr.GetArrayLength()];
            for (int i = 0; i < vec.Length; i++) vec[i] = arr[i].GetSingle();
            return vec;
        }
        catch { return null; }
    }

    private async Task<string?> GenerateAsync(string prompt, double temperature, CancellationToken ct)
        => (await GenerateWithDetailAsync(prompt, temperature, 4096, ct)).Text;

    /// <summary>True when the model belongs to a Gemini generation that emits
    /// internal "thinking" tokens (2.5 family). Those eat the output budget
    /// silently — a 1024-token cap can leave 0 tokens for the actual reply
    /// and returns finishReason=MAX_TOKENS with an empty text field. Fix:
    /// send thinkingConfig.thinkingBudget = 0 for these models.</summary>
    private bool ModelHasThinkingTokens() =>
        _model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase);

    /// <summary>Same as GenerateAsync, but returns both the text and the last
    /// error / raw response so callers can surface a real reason instead of
    /// "Draft empty". The email-template endpoint uses this so a rate limit
    /// or safety block stops looking like a mystery 502.</summary>
    public async Task<(string? Text, string? Error)> GenerateWithDetailAsync(string prompt, double temperature, int maxTokens, CancellationToken ct)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(_apiKey)}";
        // Gemini 2.5 defaults thinkingBudget to "dynamic" — for 2.5-flash that
        // can burn 500-800 output tokens on internal reasoning before writing
        // anything, so a 1024 cap silently produces an empty response. Zero
        // disables thinking entirely (fine for our short generative tasks).
        // Fields ignored by 1.5 models per Gemini API docs, so unconditional.
        object generationConfig = ModelHasThinkingTokens()
            ? new { temperature, maxOutputTokens = maxTokens, thinkingConfig = new { thinkingBudget = 0 } }
            : new { temperature, maxOutputTokens = maxTokens };
        // v1.10.19: Safety-Filter auf BLOCK_ONLY_HIGH — Business-Emails werden
        // sonst gelegentlich als "hate speech" oder "harassment" gefiltert
        // (z.B. Formulierungen wie "urgent action required" triggern's).
        var safety = new object[]
        {
            new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_ONLY_HIGH" },
            new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_ONLY_HIGH" },
            new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_ONLY_HIGH" },
            new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_ONLY_HIGH" },
        };
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig,
            safetySettings = safety,
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync(url, body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Trim + keep the useful part of the API error (Gemini's
                // "error.message" JSON path is the human-readable bit).
                string reason = text.Length > 500 ? text[..500] : text;
                try
                {
                    var errDoc = JsonDocument.Parse(text);
                    if (errDoc.RootElement.TryGetProperty("error", out var e)
                        && e.TryGetProperty("message", out var m))
                        reason = m.GetString() ?? reason;
                }
                catch { }
                return (null, $"Gemini HTTP {(int)resp.StatusCode}: {reason}");
            }
            var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.GetArrayLength() == 0)
                return (null, "Keine candidates in Gemini-Antwort (Safety-Block oder Modell antwortet nicht).");
            var cand = candidates[0];
            string? finishReason = null;
            // MAX_TOKENS finish → the model got cut off before writing anything;
            // surface that as its own error so bumping the limit is obvious.
            if (cand.TryGetProperty("finishReason", out var fr))
            {
                finishReason = fr.GetString();
                if (!string.Equals(finishReason, "STOP", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                    return (null, $"Gemini finishReason={finishReason} (Modell: {_model})");
            }
            // Read thinking-token usage for diagnostic — if the model burned
            // its whole output budget on reasoning we say so plainly.
            int thinkingTokens = 0;
            if (doc.RootElement.TryGetProperty("usageMetadata", out var um)
                && um.TryGetProperty("thoughtsTokenCount", out var tt))
                thinkingTokens = tt.GetInt32();

            if (!cand.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.GetArrayLength() == 0
                || !parts[0].TryGetProperty("text", out var t))
            {
                // Common cause on 2.5-flash: finishReason=MAX_TOKENS + no
                // `parts` at all because ALL output tokens went into thinking.
                if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                    return (null, $"Gemini {_model}: gesamtes Output-Budget von {maxTokens} Tokens in interne Reasoning-Schritte geflossen ({thinkingTokens} thinking-tokens) — kein sichtbarer Text. Provider-Fix: v1.10.4 sendet thinkingConfig=0 für 2.5-Modelle.");
                return (null, $"Gemini-Antwort ohne text-Feld (Modell: {_model}, finishReason={finishReason ?? "unknown"}).");
            }
            var textValue = t.GetString();
            // Gemini can respond with an empty string + finishReason=STOP when
            // the model decided the prompt yielded nothing useful (e.g. a
            // safety soft-block, or the model interpreted the task as "no
            // template is needed"). Treat that as an error so the UI shows
            // something actionable instead of the generic "Draft empty".
            if (string.IsNullOrWhiteSpace(textValue))
            {
                if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                    return (null, $"Gemini {_model}: MAX_TOKENS erreicht ({thinkingTokens} thinking-tokens von {maxTokens}) — Output-Budget im Code erhöhen oder anderes Modell wählen.");
                return (null, $"Gemini {_model} lieferte leeren Text (evtl. Safety-Filter). Modell wechseln oder Prompt weniger allgemein formulieren.");
            }
            return (textValue, null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Anthropic Claude Messages API.</summary>
public class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>Last diagnostic — same pattern as OpenAiProvider.LastError so
    /// AiController can surface real reasons instead of the generic
    /// "Provider returned no text" fallback.</summary>
    public string? LastError { get; private set; }

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

    public Task<string?> DraftUploadRequestAsync(string senderName, string recipientEmail, string? context, string language, CancellationToken ct = default) =>
        MessagesAsync(
            $"Short cover email asking recipient to upload a file. ISO '{language}'. Adapt tone to email domain. Under 5 sentences, no markdown.",
            $"Sender: {senderName}\nRecipient: {recipientEmail}\nContext: {context ?? "—"}", 0.6, ct);

    public Task<string?> ChatAnswerAsync(string question, IEnumerable<string> passages, string language, CancellationToken ct = default)
    {
        var joined = string.Join("\n---\n", passages.Take(6));
        return MessagesAsync(
            $"Answer using ONLY these passages. Reply in ISO '{language}'. Cite [1] where helpful.",
            $"Passages:\n{joined}\n\nQuestion: {question}", 0.2, ct);
    }

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null); // Anthropic doesn't ship embeddings — semantic search falls back to null

    public async Task<string?> DescribeImageAsync(byte[] imageBytes, string mimeType, string language, CancellationToken ct = default)
    {
        // Claude Messages API accepts image blocks with base64.
        var payload = new
        {
            model = _model,
            max_tokens = 400,
            temperature = 0.3,
            system = $"Describe what is visible in the image in 2-4 sentences. Reply in the language whose ISO code is '{language}'. No preamble.",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = mimeType, data = Convert.ToBase64String(imageBytes) } },
                        // Prompt text stays English so it doesn't bias the
                        // model's output language — the actual reply language
                        // is dictated by the system message via ISO code.
                        new { type = "text", text = "Please describe what is in this image." },
                    }
                }
            },
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

    private async Task<string?> MessagesAsync(string system, string user, double temperature, CancellationToken ct)
    {
        LastError = null;
        var payload = new
        {
            model = _model,
            max_tokens = 800,
            temperature,
            system,
            messages = new object[] { new { role = "user", content = user } },
        };
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            var resp = await _http.PostAsync("https://api.anthropic.com/v1/messages", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string reason = text.Length > 500 ? text[..500] : text;
                try
                {
                    var errDoc = JsonDocument.Parse(text);
                    if (errDoc.RootElement.TryGetProperty("error", out var e)
                        && e.TryGetProperty("message", out var m))
                        reason = m.GetString() ?? reason;
                }
                catch { }
                LastError = $"Anthropic HTTP {(int)resp.StatusCode} ({_model}): {reason}";
                return null;
            }
            var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("content", out var contentArr)
                || contentArr.GetArrayLength() == 0
                || !contentArr[0].TryGetProperty("text", out var t))
            {
                LastError = $"Anthropic {_model}: content[0].text fehlt.";
                return null;
            }
            var value = t.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                LastError = $"Anthropic {_model}: leerer content-text.";
                return null;
            }
            return value;
        }
        catch (Exception ex)
        {
            LastError = $"Anthropic {_model} Exception: {ex.Message}";
            return null;
        }
    }
}

/// <summary>Loads the persisted AiGatewaySettings and creates an IAiProvider on demand.</summary>
public interface IAiGatewayService
{
    Task<AiGatewaySettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AiGatewaySettings incoming, string? plainApiKey, Guid updatedBy, CancellationToken ct = default);
    Task<IAiProvider> CreateProviderAsync(CancellationToken ct = default);
    Task<string?> ExtractTextAsync(string blobPath, string contentType, IBlobStorageService blobs, CancellationToken ct = default);
    /// <summary>Decrypt and return the plain-text API key — used by the
    /// list-models endpoint. Returns null if no key is saved.</summary>
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);

    /// <summary>Sticky reason the last CreateProviderAsync fell back to
    /// NullAiProvider — visible to controllers so 502 messages can name the
    /// actual cause (missing key, unwrap-failed, endpoint invalid).</summary>
    string? LastProviderCreationFailure { get; }
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
        // v1.10.29: AGGRESSIVE Sanitize. Trim reicht nicht — Marcus's Key
        // hatte 61 Zeichen statt der 39 die Google standardmäßig ausgibt.
        // Klar: Copy-Paste-Reste, Zero-Width-Chars, NBSPs, wrap-artige Anführungs-
        // zeichen oder ein zweiter Key hingen dran. Alle Provider-Keys folgen
        // dem Muster `[A-Za-z0-9\-_.:/=]`. Wir behalten NUR diese ASCII-Chars
        // (deckt Gemini AIza..., OpenAI sk-..., Azure keys, Anthropic sk-ant-...).
        if (!string.IsNullOrEmpty(plainApiKey))
        {
            var cleaned = SanitizeApiKey(plainApiKey);
            if (!string.IsNullOrEmpty(cleaned))
                s.ApiKeyEncrypted = _protector.Protect(cleaned);
        }
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

    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        if (string.IsNullOrEmpty(s.ApiKeyEncrypted)) return null;
        try
        {
            // v1.10.29: Sanitize auch beim Read — falls die DB noch alte, un-
            // trimmte Keys enthält (z.B. mit Trailing-Whitespace der vor
            // v1.10.29 gespeichert wurde), waschen wir sie im Flug. Das
            // vermeidet dass Marcus zusätzlich zum Deploy den Key nochmal
            // eintragen muss.
            var raw = _protector.Unprotect(s.ApiKeyEncrypted);
            return SanitizeApiKey(raw);
        }
        catch { return null; }
    }

    /// <summary>Provider-API-Keys sind alle strikt ASCII: AIza..., sk-...,
    /// sk-ant-..., Azure-Base64-artige Strings. Alles außer den erlaubten
    /// Chars ist Copy-Paste-Müll (Whitespace, Zero-Width-Space, NBSP,
    /// Smart-Quotes, Newlines). Rausfiltern.</summary>
    internal static string SanitizeApiKey(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input)
        {
            bool allowed = (ch >= 'A' && ch <= 'Z')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9')
                || ch == '-' || ch == '_' || ch == '.' || ch == ':';
            if (allowed) sb.Append(ch);
        }
        return sb.ToString();
    }

    public string? LastProviderCreationFailure { get; private set; }

    public async Task<IAiProvider> CreateProviderAsync(CancellationToken ct = default)
    {
        LastProviderCreationFailure = null;
        var s = await LoadAsync(ct);
        if (s.Provider == AiProvider.Disabled)
        {
            LastProviderCreationFailure = "AI-Gateway ist deaktiviert (settings.Provider=Disabled). Gehe zu /settings/ai und wähle einen Provider.";
            return new NullAiProvider();
        }
        if (string.IsNullOrEmpty(s.ApiKeyEncrypted))
        {
            LastProviderCreationFailure = $"Kein API-Key gespeichert für Provider={s.Provider}. Feld ist im DB-Objekt leer — gib den Key in /settings/ai neu ein und speichere.";
            return new NullAiProvider();
        }
        string apiKey;
        try { apiKey = _protector.Unprotect(s.ApiKeyEncrypted); }
        catch (Exception ex)
        {
            LastProviderCreationFailure = $"API-Key kann nicht entschlüsselt werden ({ex.GetType().Name}: {ex.Message}). Meist Azure App Service DataProtection-Keys weg (Neustart ohne persistenten Key-Ring). Gib den Key in /settings/ai NEU ein und speichere — dann wird er mit dem aktuellen Ring re-verschlüsselt.";
            return new NullAiProvider();
        }
        var http = _httpFactory.CreateClient();
        return s.Provider switch
        {
            AiProvider.OpenAi => new OpenAiProvider(http, apiKey, s.Model ?? "gpt-4o-mini", null),
            // SSRF guard: reuse the same host-allow-list the ListModels endpoint
            // uses. If the saved endpoint doesn't clear it, fall back to
            // NullAiProvider so a bogus / pivot URL can't be used to talk to
            // internal hosts with the API key attached.
            AiProvider.AzureOpenAi => NimShare.Api.Controllers.AiGatewayController
                    .IsValidAzureOpenAiEndpoint(s.Endpoint)
                ? new OpenAiProvider(http, apiKey, s.Model ?? "gpt-4o-mini", s.Endpoint)
                : new NullAiProvider(),
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
                var extracted = sb.ToString();
                // If PDF is scanned (no text layer) AND OCR is enabled, fall through to vision.
                var settings = await LoadAsync(ct);
                if (extracted.Trim().Length < 40 && settings.EnableOcr
                    && settings.Provider != AiProvider.Disabled)
                {
                    return await OcrViaVisionAsync(ms.ToArray(), "application/pdf", ct);
                }
                return extracted;
            }

            if (contentType.StartsWith("image/"))
            {
                var settings = await LoadAsync(ct);
                if (settings.EnableOcr && settings.Provider != AiProvider.Disabled)
                    return await OcrViaVisionAsync(ms.ToArray(), contentType, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Text extraction failed for {Path}", blobPath);
        }
        return null;
    }

    /// <summary>Uses the configured vision provider to extract readable text
    /// from an image or scanned PDF. Prompts the model to transcribe verbatim
    /// so the result flows straight into the fulltext index.</summary>
    private async Task<string?> OcrViaVisionAsync(byte[] bytes, string mimeType, CancellationToken ct)
    {
        var provider = await CreateProviderAsync(ct);
        // Piggy-back the vision API with an OCR prompt encoded into the "language"
        // slot — providers stuff it into the user message so the model treats
        // the response as literal transcription rather than description.
        var text = await provider.DescribeImageAsync(bytes, mimeType,
            "Transcribe every readable word from this image verbatim, one line per line, without adding commentary. If nothing readable, return empty.", ct);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
