namespace NimShare.Core.Entities;

/// <summary>
/// v1.11.52 — Marcus's Wunsch: dezente Emoji-Reaktionsleiste auf der
/// öffentlichen Landing, immer an. Anonym (keine Besucher-Identität
/// gespeichert) — Dedupe pro Besucher läuft rein über die Server-Session
/// (siehe ShareController.React), nicht über diese Tabelle. Ein Klick =
/// eine Zeile; ein Besucher, der seine Reaktion ändert, löscht seine alte
/// Zeile und legt eine neue an (kein Update-in-place nötig).
/// </summary>
public class ShareLinkReaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShareLinkId { get; set; }
    public ShareLink ShareLink { get; set; } = null!;

    /// <summary>Eines von ShareController.AllowedReactionEmojis — serverseitig
    /// gegen die Allow-List geprüft, kein Freitext.</summary>
    public string Emoji { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
