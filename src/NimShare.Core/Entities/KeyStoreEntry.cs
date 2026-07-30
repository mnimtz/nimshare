namespace NimShare.Core.Entities;

/// <summary>
/// v1.11.22 — Marcus's Key-Store: pro-User-Verwaltung von Kunden + deren
/// Lizenzschlüsseln (Software-Vertrieb-Anwendungsfall). Getrennt von
/// <see cref="Contact"/> (Adressbuch für Signatur-/Freigabe-Empfänger) —
/// hier geht es um Kunden-Schlüssel-Zuordnung, nicht um Kontaktdaten.
///
/// Owner-Scoping: normale User sehen nur ihre eigenen Einträge
/// (OwnerUserId == me.Id), Admins sehen alle (siehe KeyStoreController).
///
/// Matching für den künftigen Share-Link-Reveal-Flow (v1.11.23+): ein
/// Besucher gibt seine E-Mail ein, der Server sucht zuerst nach exaktem
/// CustomerEmail-Treffer, sonst nach CustomerEmailDomain-Wildcard-Treffer
/// (analog IsEmailAllowed-Pattern in ShareController).
/// </summary>
public enum KeyStoreEntryStatus { Available = 0, Assigned = 1 }

public class KeyStoreEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Exakte E-Mail des Kunden. Optional, wenn stattdessen per
    /// Domain zugeordnet wird (z.B. für einen Firmenkunden mit mehreren
    /// Ansprechpartnern).</summary>
    public string? CustomerEmail { get; set; }

    /// <summary>Domain-Wildcard (z.B. "acme.com") — matched jede E-Mail
    /// @acme.com. Optional, alternativ oder zusätzlich zu CustomerEmail.</summary>
    public string? CustomerEmailDomain { get; set; }

    /// <summary>Freitext-Typ, z.B. "Testversion", "Vollversion", "Abo".</summary>
    public string KeyType { get; set; } = string.Empty;

    /// <summary>Verschlüsselt via IDataProtector (gleiches Pattern wie
    /// ShareLink.SerialNumberEncrypted) — Klartext nie in DB oder DTO.</summary>
    public string KeyValueEncrypted { get; set; } = string.Empty;

    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // v1.11.56 — "Lizenzen": Vorrat an noch nicht zugewiesenen Schlüsseln.
    // Existing rows sind alle implizit Assigned (sie hatten ja immer schon
    // einen Kunden) — die Migration setzt den Default entsprechend.
    /// <summary>Available = Lagerbestand, noch keinem Kunden zugewiesen
    /// (CustomerName/Email/Domain sind dann leer). Assigned = normaler,
    /// wie bisher direkt für einen Kunden angelegter oder aus dem Vorrat
    /// zugewiesener Eintrag.</summary>
    public KeyStoreEntryStatus Status { get; set; } = KeyStoreEntryStatus.Assigned;

    /// <summary>Zeitpunkt der Zuweisung an einen Kunden (nur bei Assigned).
    /// Wird beim Reset wieder auf null gesetzt.</summary>
    public DateTimeOffset? AssignedAt { get; set; }

    /// <summary>Nur von Admins bei Anlage setzbar: macht einen
    /// Available-Eintrag für ALLE User als Vorrat sichtbar/entnehmbar
    /// (nicht nur den Owner) — analog admin-kuratierten öffentlichen
    /// Links. Bleibt nach Zuweisung/Reset unverändert bestehen, damit der
    /// Vorratseintrag bei einem Reset wieder für alle verfügbar wird.</summary>
    public bool IsGlobal { get; set; }
}
