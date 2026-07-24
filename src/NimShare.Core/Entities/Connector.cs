namespace NimShare.Core.Entities;

public enum ConnectorType
{
    /// <summary>Microsoft OneDrive for Business (via Graph API).</summary>
    OneDriveBusiness = 1,
    // Zukünftig: OneDrivePersonal, GoogleDrive, Dropbox, SharePoint …
}

/// <summary>
/// Eine autorisierte Verbindung eines Users zu einem externen Cloud-Speicher.
/// Aktuell nur OneDrive for Business. Refresh-Token ist DataProtection-
/// verschlüsselt (analog SigningCertificate.PfxDataEncrypted); der Zugriffs-
/// Token wird zur Laufzeit refresht und nie persistiert. Import-Modus:
/// User klickt sich per Browse-View durch die Cloud, wählt Ordner/Dateien,
/// NimShare streamt sie in den Personal-Ablagebereich (kein automatischer
/// Sync).
///
/// Eingeführt in v1.10.163 mit Migration V187_Connectors.
/// </summary>
public class Connector
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }
    public User? Owner { get; set; }

    public ConnectorType Type { get; set; } = ConnectorType.OneDriveBusiness;

    /// <summary>Menschlich lesbarer Name — z.B. die Anzeige-E-Mail des
    /// verbundenen Microsoft-Kontos. Wird beim OAuth-Callback gesetzt.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>DataProtection-verschlüsselter Refresh-Token (Layout wie
    /// SigningCertificate.PfxDataEncrypted: raw Bytes vom Protector).</summary>
    public byte[] RefreshTokenEncrypted { get; set; } = Array.Empty<byte>();

    /// <summary>Konto-Identifier vom Provider (OneDrive: die Object-ID des
    /// Users im Tenant). Für optionale Duplikat-Erkennung „gleicher Account
    /// zweimal verbunden".</summary>
    public string? ExternalAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Wenn true (Default), wird beim Import die Ordnerhierarchie
    /// des Quell-Systems mit übernommen. Wenn false, landen alle Dateien
    /// flach im Ziel-Ordner.</summary>
    public bool PreserveFolderStructure { get; set; } = true;
}
