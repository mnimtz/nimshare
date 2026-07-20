namespace NimShare.Core.Entities;

public enum ShareLinkAccessKind
{
    Landing = 0,
    PasswordFail = 1,
    Download = 2
}

public class ShareLinkAccess
{
    public long Id { get; set; }

    public Guid ShareLinkId { get; set; }
    public ShareLink ShareLink { get; set; } = null!;

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public ShareLinkAccessKind Kind { get; set; }

    /// <summary>HMAC-SHA256(IP, server salt) — never the raw address.</summary>
    public string IpHash { get; set; } = string.Empty;

    public string? UserAgent { get; set; }
    public string? Referer { get; set; }
    public string? CountryCode { get; set; }
    // v1.10.42: gleiche forensische Felder wie SignatureAudit. City nur
    // wenn ein GeoIP-Provider konfiguriert ist der auf Stadt-Ebene auflöst.
    public string? City { get; set; }
    // "Desktop" | "Mobile" | "Tablet" | "Bot" — aus User-Agent-Heuristik.
    public string? DeviceType { get; set; }
    // IANA-TZ vom Browser via /beacon. Erste Iteration: nur Signaturen
    // schicken die TZ, hier bleibt es meist null.
    public string? Timezone { get; set; }
}
