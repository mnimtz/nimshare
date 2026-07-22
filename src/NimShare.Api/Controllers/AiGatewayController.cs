using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        // v1.10.21: Key-Status live prüfen und im UI zeigen — sofort sichtbar
        // ob der gespeicherte Key noch entschlüsselbar ist (DP-Ring stabil)
        // oder ob er verloren gegangen ist. Kein Rätselraten mehr für Marcus.
        ViewData["ApiKeyStatus"] = await ComputeApiKeyStatusAsync(s);
        return View(s);
    }

    // v1.10.68: von sync + .GetAwaiter().GetResult() auf async umgestellt.
    // Marcus's Feedback zu Server-Hängern: sync-over-async blockiert einen
    // ThreadPool-Slot pro Aufruf, ohne SynchronizationContext zwar kein
    // Deadlock — aber unter Last (viele parallele /settings/ai-Aufrufe)
    // trägt es zur Slot-Erschöpfung bei. Async-Version verhält sich sauber.
    private async Task<string> ComputeApiKeyStatusAsync(NimShare.Core.Entities.AiGatewaySettings s)
    {
        if (string.IsNullOrEmpty(s.ApiKeyEncrypted))
            return "kein Key gespeichert. Bitte oben eintragen und speichern.";
        try
        {
            var key = await _ai.GetApiKeyAsync();
            if (string.IsNullOrEmpty(key))
                return "Encrypt-Blob vorhanden aber Unprotect ergab leeren String — vermutlich DataProtection-Keys-Ring wurde regeneriert. Key neu eintragen.";
            // v1.10.28: Zusätzliche Byte-Level-Diagnose. Standard Gemini-Keys
            // sind 39 Zeichen "AIzaSy" + 33. Alles darüber ist verdächtig
            // (Trailing-Whitespace, versehentlich zweiter Key drangehängt,
            // Zero-Width-Chars aus Copy-Paste, etc.). Hex-Dump der ersten
            // und letzten 4 Bytes deckt das schonungslos auf.
            var bytes = System.Text.Encoding.UTF8.GetBytes(key);
            string hexFirst = BitConverter.ToString(bytes, 0, Math.Min(4, bytes.Length)).Replace("-", "");
            string hexLast = bytes.Length > 4
                ? BitConverter.ToString(bytes, bytes.Length - 4, 4).Replace("-", "")
                : "n/a";
            bool suspicious = key.Length > 45; // 39 Std + kleine Toleranz
            bool startsAIza = key.StartsWith("AIza", StringComparison.Ordinal);
            var msg = $"OK — Key entschlüsselbar (Länge {key.Length}, startet '{key[..Math.Min(4, key.Length)]}…', erste 4 Bytes hex={hexFirst}, letzte 4 Bytes hex={hexLast}, UTF8-Bytes={bytes.Length}).";
            if (!startsAIza)
                msg += " ⚠ Kein AIza-Präfix — ist das WIRKLICH ein Gemini-Key? OpenAI-Keys beginnen mit 'sk-'.";
            if (suspicious)
                msg += $" ⚠ Länge {key.Length} ist ungewöhnlich (Standard Gemini-Keys sind 39 Zeichen). Möglicherweise hängt Whitespace / Newline / zweiter Key mit dran. Prüfe die letzten 4 Bytes hex — sind da 20/09/0A/0D drin, ist's Whitespace.";
            return msg;
        }
        catch (Exception ex)
        {
            return $"Encrypt-Blob vorhanden aber Unprotect wirft ({ex.GetType().Name}: {ex.Message}). DataProtection-Keys-Ring wurde regeneriert — Key neu eintragen und speichern, dann wird er mit dem aktuellen Ring re-verschlüsselt.";
        }
    }

    public record SaveForm(AiProvider Provider, string? Model, string? Endpoint, string? ApiKey,
        bool EnableAutoSummary, bool EnableSmartTags, bool EnableSemanticSearch,
        bool EnableGuidedUploadRequests, bool EnableContentRiskDetection,
        bool EnableDraftedShareEmails, bool EnableChatWithFiles, bool EnableOcr,
        string? StatusPageUrl);

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
            StatusPageUrl = string.IsNullOrWhiteSpace(form.StatusPageUrl) ? null : form.StatusPageUrl.Trim(),
        };
        await _ai.SaveAsync(incoming, form.ApiKey, me.Id, ct);
        TempData["Notice"] = _l["notice.ai_saved"].Value;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// v1.10.70: Reindex-Endpoint. Der Button in /settings/ai ruft ihn per
    /// fetch()-POST. Legt alle Ready-Files (alle Scopes) wieder in die
    /// AiPostProcessor-Queue. SemaphoreSlim(2) im PostProcessor bremst so
    /// dass tausende gequeueter Files den Server nicht erschlagen.
    /// Response: { queued: N } — der UI-JS ersetzt {0} damit.
    /// </summary>
    [HttpPost("/api/v1/ai/reindex")]
    public async Task<IActionResult> Reindex([FromServices] IAiPostProcessor postProcessor,
        [FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var s = await _ai.LoadAsync(ct);
        if (s.Provider == AiProvider.Disabled)
            return Problem(statusCode: 422, title: _l["ai.reindex.err_disabled"].Value);
        if (!s.EnableSemanticSearch)
            return Problem(statusCode: 422, title: "Semantische Suche ist deaktiviert.",
                detail: "Ohne Semantische-Suche-Flag baut der PostProcessor keine Embeddings. Aktiviere sie oben.");

        // v1.10.76: SMOKE-TEST — synchron einen einzelnen Embed-Call gegen
        // den Provider machen und Ergebnis in die Response schreiben. Wenn
        // der crasht (falscher API-Key, tote URL, Modell weg), sieht der
        // Admin sofort was los ist statt "hab schon oft reindex gedrückt".
        var provider = await _ai.CreateProviderAsync(ct);
        var testVec = await provider.EmbedAsync("smoke-test");
        if (testVec is null || testVec.Length == 0)
        {
            var err = (provider as NimShare.Api.Services.OpenAiProvider)?.LastError
                ?? (provider as NimShare.Api.Services.GeminiProvider)?.LastError
                ?? (provider as NimShare.Api.Services.AnthropicProvider)?.LastError
                ?? _ai.LastProviderCreationFailure
                ?? $"Provider {provider.GetType().Name} lieferte keinen Vector zurück.";
            return Problem(statusCode: 502,
                title: "AI-Provider liefert keine Embeddings.",
                detail: $"Smoke-Test-Embed schlug fehl: {err}\n\nHäufigste Ursachen: API-Key falsch/abgelaufen, gewähltes Modell unterstützt kein Embedding, oder Rate-Limit. Bitte in Settings › AI-Gateway den Key neu eintragen oder Modell wechseln.");
        }

        var ids = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Files.Where(f => f.Status == StorageFileStatus.Ready).Select(f => f.Id), ct);
        foreach (var id in ids) postProcessor.QueueForFile(id);
        return Ok(new { queued = ids.Count, smokeTest = "ok", vectorDim = testVec.Length });
    }

    /// <summary>
    /// v1.10.98: Diagnose-Endpoint. Zeigt sofort was mit dem AI-Setup los ist:
    /// aktueller Provider, Feature-Flags, Ready-Files vs Embeddings, letzter
    /// Fehler. Löst Marcus's „hab 20 mal Reindex geklickt, geht immer noch nicht".
    /// </summary>
    [HttpGet("/api/v1/ai/diag")]
    public async Task<IActionResult> Diag([FromServices] NimShare.Core.Data.NimShareDbContext db, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var s = await _ai.LoadAsync(ct);
        var apiKey = await _ai.GetApiKeyAsync(ct);

        var readyFiles = await db.Files.CountAsync(f => f.Status == StorageFileStatus.Ready, ct);
        var withText   = await db.Files.CountAsync(f => f.Status == StorageFileStatus.Ready && f.ExtractedText != null && f.ExtractedText != "", ct);
        var embeddings = await db.FileEmbeddings.CountAsync(ct);
        var latestEmbed = await db.FileEmbeddings.OrderByDescending(e => e.CreatedAt).Select(e => new { e.FileId, e.Model, e.CreatedAt }).FirstOrDefaultAsync(ct);
        var missingIds = await db.Files
            .Where(f => f.Status == StorageFileStatus.Ready && !db.FileEmbeddings.Any(e => e.FileId == f.Id))
            .OrderBy(f => f.Name)
            .Select(f => new { f.Id, f.Name, f.ContentType, HasText = f.ExtractedText != null && f.ExtractedText != "" })
            .Take(10)
            .ToListAsync(ct);

        string? providerFailure = _ai.LastProviderCreationFailure;
        object? smokeTestResult = null;
        if (s.Provider != AiProvider.Disabled && !string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var provider = await _ai.CreateProviderAsync(ct);
                var vec = await provider.EmbedAsync("smoke-test");
                var lastErr = (provider as NimShare.Api.Services.OpenAiProvider)?.LastError
                    ?? (provider as NimShare.Api.Services.GeminiProvider)?.LastError
                    ?? (provider as NimShare.Api.Services.AnthropicProvider)?.LastError;
                smokeTestResult = new
                {
                    ok = vec is not null && vec.Length > 0,
                    vectorDim = vec?.Length ?? 0,
                    lastError = lastErr,
                };
            }
            catch (Exception ex) { smokeTestResult = new { ok = false, error = ex.Message }; }
        }

        return Ok(new
        {
            provider = s.Provider.ToString(),
            model = s.Model,
            endpoint = s.Endpoint,
            hasApiKey = !string.IsNullOrEmpty(apiKey),
            apiKeyPrefix = string.IsNullOrEmpty(apiKey) ? null : apiKey.Substring(0, Math.Min(6, apiKey.Length)) + "…",
            flags = new
            {
                enableSemanticSearch = s.EnableSemanticSearch,
                enableSmartTags = s.EnableSmartTags,
                enableContentRiskDetection = s.EnableContentRiskDetection,
                enableAutoSummary = s.EnableAutoSummary,
                enableChatWithFiles = s.EnableChatWithFiles,
            },
            files = new
            {
                ready = readyFiles,
                withExtractedText = withText,
                withoutText = readyFiles - withText,
            },
            embeddings = new
            {
                total = embeddings,
                missing = readyFiles - embeddings,
                latest = latestEmbed,
                sampleMissing = missingIds,
            },
            providerCreationFailure = providerFailure,
            smokeTest = smokeTestResult,
            hint = embeddings == 0 && readyFiles > 0
                ? "Ready-Files existieren, aber NULL Embeddings. Wenn smokeTest.ok=true, läuft der Provider — dann läuft der PostProcessor nicht durch (siehe Server-Log nach 'Embed returned null' oder 'AI post-process failed')."
                : embeddings > 0 && embeddings < readyFiles
                ? $"{embeddings}/{readyFiles} indexiert. Restliche Files landen beim nächsten Reindex oder haben ein Extract-Problem — siehe sampleMissing."
                : embeddings >= readyFiles && readyFiles > 0
                ? "Alles gut. Wenn Chat trotzdem 'keine Embeddings' meldet, bitte iOS-App neu starten (Client-Cache)."
                : null,
        });
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
        // v1.10.27: Weder pageSize noch der x-goog-api-key-Header werden vom
        // v1beta/models-Endpoint zuverlässig unterstützt — beide führen zu
        // 400 (Bad Request) auf manchen Regionen/Projekten. Zurück auf den
        // offiziell dokumentierten Weg: Key als ?key= query-Parameter,
        // keine zusätzlichen Parameter.
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}");
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Google's error body enthält bei 400/401/403 eine sprechende
            // "message" — die statt EnsureSuccessStatusCode's generic Text
            // durchreichen.
            var body = await resp.Content.ReadAsStringAsync(ct);
            string reason = body.Length > 400 ? body[..400] : body;
            try
            {
                using var errDoc = System.Text.Json.JsonDocument.Parse(body);
                if (errDoc.RootElement.TryGetProperty("error", out var e)
                    && e.TryGetProperty("message", out var m))
                    reason = m.GetString() ?? reason;
            }
            catch { }
            throw new HttpRequestException($"Gemini list-models HTTP {(int)resp.StatusCode}: {reason}");
        }
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
