namespace NimShare.Core.Entities;

public enum AiProvider
{
    Disabled = 0,
    OpenAi = 1,
    Gemini = 2,
    Anthropic = 3,
    AzureOpenAi = 4,
}

/// <summary>
/// Singleton row holding the tenant-wide AI configuration. Secrets are
/// encrypted at rest via ASP.NET Core DataProtection.
/// </summary>
public class AiGatewaySettings
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000002");

    public Guid Id { get; set; } = SingletonId;

    public AiProvider Provider { get; set; } = AiProvider.Disabled;

    /// <summary>Ciphertext (DataProtection). Never expose in JSON.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>Model id — provider-specific (e.g. "gpt-4o-mini" or "gemini-2.0-flash").</summary>
    public string? Model { get; set; }

    /// <summary>For Azure OpenAI: full endpoint URL of the resource.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// v1.10.117 — optionale URL einer öffentlichen Status-Seite (z. B.
    /// https://status.tungstencloud.com/europe). Ist sie gesetzt, holt die
    /// KI-Startseiten-Begrüssung den aktuellen Cloud-Status und baut ihn
    /// locker mit ein („übrigens, alle Dienste laufen rund"). Nur http/https.
    /// </summary>
    public string? StatusPageUrl { get; set; }

    /// <summary>
    /// v1.10.118 — kommagetrennte Liste der Produkte/Dienste, die aus der
    /// Status-Seite in die Begrüssung einfliessen sollen (z. B. „Printix,
    /// PowerPDF Cloud Editor"). Leer = alle. Die KI matcht die Namen locker
    /// gegen den Seitentext, exakte Schreibweise ist nicht nötig.
    /// </summary>
    public string? StatusPageProducts { get; set; }

    // ── Per-feature toggles ───────────────────────────────────────────────
    public bool EnableAutoSummary { get; set; }
    public bool EnableSmartTags { get; set; }
    public bool EnableSemanticSearch { get; set; }
    public bool EnableGuidedUploadRequests { get; set; }
    public bool EnableContentRiskDetection { get; set; }
    public bool EnableDraftedShareEmails { get; set; }
    public bool EnableChatWithFiles { get; set; }
    public bool EnableOcr { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
