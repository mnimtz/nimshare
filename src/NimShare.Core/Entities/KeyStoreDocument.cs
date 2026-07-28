namespace NimShare.Core.Entities;

/// <summary>
/// v1.11.37 — Marcus's Key-Store-Dokumentation: pro-User verwaltete Dokumente
/// (hochgeladenes PDF ODER fester Link, z.B. der Kofax-Cloud-Tenant-Login),
/// die auf der öffentlichen Landing eines KeyStoreMode-Links als zusätzliche
/// Buttons neben dem Lizenzkey erscheinen — aber NUR wenn der Link-Ersteller
/// die "Dokumentation"-Checkbox beim Erstellen des Links aktiviert hat
/// (siehe ShareLink.DocumentationUrl als Gate in LinksController).
///
/// Matching: KeyTypesCsv enthält eine kommaseparierte Liste exakter Key-Typen
/// (dieselben Strings wie KeyStoreEntry.KeyType / die Datalist im Key-Store-
/// Modal). Ein Dokument erscheint nur, wenn der beim Reveal ermittelte
/// KeyStoreEntry.KeyType exakt (case-insensitive) in dieser Liste steht.
/// </summary>
public class KeyStoreDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Gesetzt bei einem hochgeladenen PDF. Exklusiv zu <see cref="Url"/>.</summary>
    public string? BlobPath { get; set; }

    /// <summary>Ursprünglicher Dateiname — für den Download-Dateinamen (CreateDownloadSas).</summary>
    public string? FileName { get; set; }

    /// <summary>Gesetzt bei einem festen Link (z.B. Tenant-Login). Exklusiv zu <see cref="BlobPath"/>.</summary>
    public string? Url { get; set; }

    /// <summary>Kommaseparierte Liste von Key-Typen, für die dieses Dokument gilt.</summary>
    public string KeyTypesCsv { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsFile => !string.IsNullOrEmpty(BlobPath);

    public IReadOnlyList<string> KeyTypes =>
        KeyTypesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool AppliesTo(string keyType) =>
        KeyTypes.Any(t => string.Equals(t, keyType, StringComparison.OrdinalIgnoreCase));
}
