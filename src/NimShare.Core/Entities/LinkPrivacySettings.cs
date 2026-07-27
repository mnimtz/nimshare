namespace NimShare.Core.Entities;

/// <summary>
/// v1.11.14 — Instanzweite Konfiguration für Link-Report-Datenschutz.
/// Single row, angelegt bei der ersten Speicherung über /links/settings/privacy.
/// Ersetzt den bisherigen appsettings-only-Toggle "ShareLinks:StoreFullIp"
/// (der als Fallback erhalten bleibt, solange noch keine DB-Zeile existiert —
/// siehe LinkAccessService.GetStoreFullIpAsync).
/// </summary>
public class LinkPrivacySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool StoreFullIp { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
