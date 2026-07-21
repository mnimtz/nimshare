namespace NimShare.Core.Entities;

/// <summary>
/// v1.10.82: App-Store-Blocker (Apple Guideline 1.2) — User müssen andere
/// User blockieren können. Ein blockierter User kann uns nichts direkt
/// mehr sharen, taucht nicht mehr im Directory/Kontakte auf, und Direct-
/// Shares von ihm werden gefiltert.
///
/// Symmetrie ist NICHT gefordert — wir blockieren ihn nur einseitig
/// (er sieht uns weiter, aber kann uns nichts mehr aufdrängen). Zwei
/// Rows für "beide gegenseitig blockieren".
/// </summary>
public class BlockedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Wer blockiert.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Wen der User blockiert.</summary>
    public Guid BlockedUserId { get; set; }
    public User? BlockedUserRef { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optionaler Freitext (User-Notiz zur Erinnerung, nie geshared).</summary>
    public string? Reason { get; set; }
}
