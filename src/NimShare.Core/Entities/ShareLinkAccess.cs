namespace NimShare.Core.Entities;

public enum ShareLinkAccessKind
{
    Landing = 0,
    PasswordFail = 1,
    Download = 2,
    // v1.11.18: Besucher hat die Seriennummer angezeigt bzw. sich per Mail
    // zusenden lassen — reiner Audit-Trail (int-Enum, keine Migration nötig).
    SerialRevealed = 3,
    SerialEmailed = 4
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

    // v1.10.156: Optionale Klartext-IP (IPv4 oder IPv6, max 45 Zeichen).
    // Nur befüllt wenn ShareLinks:StoreFullIp=true — analog dem
    // Signatures:StoreFullIp-Toggle für SignatureAudits/SignatureParticipants.
    // Rechtliche Basis: berechtigtes Interesse nach Art. 6(1)(f) DSGVO;
    // Betreiber muss das im eigenen Impressum/Datenschutz anpassen.
    public string? IpAddress { get; set; }

    /// <summary>v1.11.14: ASN/Org-String aus der GeoIP-Auflösung (z.B. "AS8075
    /// Microsoft Corporation") — hilft, automatisierte Link-Vorschau-Abrufe
    /// (Teams, Slack etc.) von echten Besuchen zu unterscheiden, siehe
    /// RefererClassifier. Kein DSGVO-Sonderfall: dieselbe "nur Ergebnis, nie
    /// die IP selbst"-Regel wie CountryCode/City.</summary>
    public string? Isp { get; set; }
}
