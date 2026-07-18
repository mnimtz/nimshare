namespace NimShare.Core.Entities;

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

    public ICollection<Folder> Children { get; set; } = new List<Folder>();
    public ICollection<StorageFile> Files { get; set; } = new List<StorageFile>();
}
