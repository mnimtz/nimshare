namespace NimShare.Core.Entities;

/// <summary>
/// Admin-Config für einen Konnektor-Provider (z.B. OneDrive Business).
/// Analog AiGatewaySettings: Singleton pro Provider-Type, ClientSecret
/// DataProtection-verschlüsselt. Ersetzt die alte appsettings.json-Variante
/// (die weiterhin als Fallback funktioniert, wenn hier nichts gepflegt ist)
/// — Admin kann alles im NimShare-UI unter /settings/connectors einrichten,
/// ohne App-Setting-Editor.
///
/// Eingeführt in v1.10.164 (Migration V188_ConnectorProviderSettings).
/// </summary>
public class ConnectorProviderSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Welcher Provider — passt zu <see cref="ConnectorType"/>.</summary>
    public ConnectorType Provider { get; set; } = ConnectorType.OneDriveBusiness;

    public string ClientId { get; set; } = "";

    /// <summary>DataProtection-verschlüsselter Client-Secret-Wert (nicht der Key).
    /// Layout: Protect(UTF8Bytes(secret)).</summary>
    public byte[]? ClientSecretEncrypted { get; set; }

    /// <summary>„common" (default = jede Microsoft-Identity) oder Tenant-GUID.</summary>
    public string Tenant { get; set; } = "common";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
