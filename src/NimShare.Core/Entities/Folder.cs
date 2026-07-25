namespace NimShare.Core.Entities;

/// <summary>
/// v1.10.166 — optionaler Ordner-Typ, der die Standard-Datei-Browser-Ansicht
/// austauscht. Regular = klassische Liste. Gallery = Photo/Video-Album mit
/// Grid, Lightbox und optionalem „Upload erlauben"-Toggle auf Freigabelinks.
/// </summary>
public enum FolderKind
{
    Regular = 0,
    Gallery = 1,
}

/// <summary>
/// A folder in the hierarchical file browser. Every file scope (Personal /
/// Public / each Group) has exactly one root folder (ParentFolderId == null).
/// Sub-folders live under a parent. Names are unique inside their parent.
/// </summary>
public class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable folder name. "/" at the root, otherwise a plain name.</summary>
    public string Name { get; set; } = "";

    public Guid? ParentFolderId { get; set; }
    public Folder? Parent { get; set; }

    /// <summary>Which top-level scope this folder belongs to.</summary>
    public FileScope Scope { get; set; }

    /// <summary>Set for Personal scope — the owning user.</summary>
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    /// <summary>Set for Group scope — the owning group.</summary>
    public Guid? OwnerGroupId { get; set; }
    public Group? OwnerGroup { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// Optional emoji shown instead of the default folder icon (e.g. "📁",
    /// "📊", "⭐"). Two-char cap covers most single-emoji + variation-selector
    /// combos. Null → renderer uses the default folder icon.
    /// </summary>
    public string? Emoji { get; set; }

    /// <summary>
    /// Optional CSS colour override for the folder icon — 6-char hex without
    /// leading "#" (e.g. "ffc600"). Null → renderer uses the theme default.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// v1.10.104 (Stage 2, „Windows-ACL"): only meaningful on Public-scope
    /// folders. When true, the folder subtree is hidden from ordinary Public
    /// browsers — visible only to the creator, an Admin, or a user with an
    /// explicit DirectShare grant reaching this folder (or one of its
    /// ancestors). Personal + Group scopes ignore this flag.
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// v1.10.166 — Ordner-Typ. Regular = klassischer Datei-Browser. Gallery =
    /// Foto/Video-Album mit Grid-Landing + Lightbox. Freigabelinks können
    /// zusätzlich AllowUploads=true bekommen, sodass Empfänger direkt Bilder
    /// hinzufügen können (Event-Album-Use-Case).
    /// </summary>
    public FolderKind Kind { get; set; } = FolderKind.Regular;

    public ICollection<Folder> Children { get; set; } = new List<Folder>();
    public ICollection<StorageFile> Files { get; set; } = new List<StorageFile>();
}
