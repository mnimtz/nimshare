using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

namespace NimShare.Api.Controllers;

/// <summary>
/// Endpoints für den Apple-5.1.1(i)-Consent: iOS-App holt sich hier den
/// aktuellen Provider (Name + Endpoint-Region + Data-Handling-Note), damit
/// der First-Use-Consent-Dialog konkret sein kann („dein Prompt geht an
/// OpenAI GPT-4o"), und schreibt/liest den User-Consent-State zurück.
/// </summary>
[ApiController]
[Authorize(Policy = "ApiUser")]
public class AiConsentApiController : ControllerBase
{
    private readonly NimShareDbContext _db;
    private readonly ICurrentUserService _users;
    private readonly IAiGatewayService _ai;

    public AiConsentApiController(NimShareDbContext db, ICurrentUserService users, IAiGatewayService ai)
    {
        _db = db; _users = users; _ai = ai;
    }

    /// <summary>Öffentlich sichtbare Provider-Info — kein Secret, keine Key-
    /// Preview. Damit der Consent-Dialog exakt sagen kann, wer die Daten
    /// bekommt.</summary>
    public record AiProviderInfo(
        string Provider,          // "OpenAi" | "Anthropic" | "AzureOpenAi" | "Gemini" | "Disabled"
        string? Model,            // z.B. "gpt-4o"
        string? EndpointHint,     // z.B. "api.openai.com" (Host only, für Region-Info)
        bool ChatWithFilesEnabled,
        bool AutoSummaryEnabled,
        bool SmartTagsEnabled,
        bool OcrEnabled,
        bool Enabled);            // false = Instanz sendet aktuell nichts an AI

    [HttpGet("/api/v1/ai/provider-info")]
    public async Task<IActionResult> ProviderInfo(CancellationToken ct)
    {
        var s = await _ai.LoadAsync(ct);
        var host = "";
        if (!string.IsNullOrWhiteSpace(s.Endpoint))
        {
            try { host = new Uri(s.Endpoint).Host; } catch { host = s.Endpoint; }
        }
        else
        {
            host = s.Provider switch
            {
                AiProvider.OpenAi => "api.openai.com",
                AiProvider.Anthropic => "api.anthropic.com",
                AiProvider.AzureOpenAi => "*.openai.azure.com",
                AiProvider.Gemini => "generativelanguage.googleapis.com",
                _ => "",
            };
        }
        return Ok(new AiProviderInfo(
            Provider: s.Provider.ToString(),
            Model: s.Model,
            EndpointHint: string.IsNullOrEmpty(host) ? null : host,
            ChatWithFilesEnabled: s.EnableChatWithFiles,
            AutoSummaryEnabled: s.EnableAutoSummary,
            SmartTagsEnabled: s.EnableSmartTags,
            OcrEnabled: s.EnableOcr,
            Enabled: s.Provider != AiProvider.Disabled));
    }

    public record AiConsentDto(bool Consented, DateTimeOffset? ConsentedAt);

    [HttpGet("/api/v1/me/ai-consent")]
    public async Task<IActionResult> GetConsent(CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        return Ok(new AiConsentDto(me.AiConsentedAt is not null, me.AiConsentedAt));
    }

    public record SetConsentReq(bool Consented);

    [HttpPut("/api/v1/me/ai-consent")]
    public async Task<IActionResult> SetConsent([FromBody] SetConsentReq req, CancellationToken ct)
    {
        var me = await _users.GetOrProvisionAsync(User, ct);
        me.AiConsentedAt = req.Consented ? DateTimeOffset.UtcNow : null;
        await _db.SaveChangesAsync(ct);
        return Ok(new AiConsentDto(me.AiConsentedAt is not null, me.AiConsentedAt));
    }
}

/// <summary>Consent-Guard-Helper — von allen AI-Aufrufern nutzbar, um die
/// Verarbeitung mit einem klaren 403 abzulehnen wenn der User noch nicht
/// zugestimmt hat. Server-seitiges Enforcement ist Apple-5.1.1(i)-relevant:
/// ein modifizierter Client oder curl könnte sonst den iOS-Consent-Dialog
/// umgehen und trotzdem Daten an den AI-Provider senden.</summary>
public static class AiConsentGuard
{
    public const string ErrorCode = "ai_consent_required";
    public const string ErrorMessage = "AI-Zustimmung fehlt. In der App unter Profil → KI-Nutzung erteilen.";

    public static bool HasConsent(User u) => u.AiConsentedAt is not null;

    /// <summary>Vor jedem AI-Endpoint aufrufen. Wenn null → weiterprocessing;
    /// wenn IActionResult → sofort return.</summary>
    public static IActionResult? RequireOrReject(ControllerBase c, User u)
        => HasConsent(u) ? null : c.Problem(statusCode: 403,
            title: "AI-Verarbeitung nicht erlaubt",
            detail: ErrorMessage,
            type: ErrorCode);

    /// <summary>Overload für MVC-Controller (nicht ControllerBase). Baut das
    /// gleiche 403-Problem-Response manuell.</summary>
    public static IActionResult? RequireOrRejectMvc(Controller c, User u)
    {
        if (HasConsent(u)) return null;
        c.Response.StatusCode = 403;
        return new ObjectResult(new
        {
            type = ErrorCode,
            title = "AI-Verarbeitung nicht erlaubt",
            status = 403,
            detail = ErrorMessage,
        });
    }
}
