namespace NimShare.Core.Entities;

/// <summary>
/// v1.10.111 — Ein Eintrag der geteilten Linksammlung (löst das Wiki ab).
/// Eine einzige firmenweite Liste: Admins pflegen, alle eingeloggten Nutzer
/// sehen sie. Kein Scope, keine Hierarchie — flache, sortierbare Liste aus
/// {Titel, URL, optionale Beschreibung}. Beispiel: „Tungsten Software
/// Center" → https://delivery.tungstenautomation.com/.
/// </summary>
public class LinkEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Anzeigename, z. B. „Tungsten Software Center".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Ziel-URL (http/https). Wird serverseitig auf Schema geprüft.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Optionale kurze Beschreibung/Notiz.</summary>
    public string? Description { get; set; }

    /// <summary>Optionales Emoji-Icon (z. B. „🏢", „📦"). Null → Standard-Icon.</summary>
    public string? Emoji { get; set; }

    /// <summary>Sortierreihenfolge in der Liste (aufsteigend).</summary>
    public int SortOrder { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
