namespace NimShare.Core.Entities;

/// <summary>
/// A markdown wiki page attached to a scope. Personal-scope wikis are per-user;
/// Group-scope wikis are shared across the group's members; Public wikis are
/// admin-managed and readable by everyone signed in.
/// </summary>
public class WikiPage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public FileScope Scope { get; set; } = FileScope.Personal;

    /// <summary>Personal wiki owner (nullable for group/public).</summary>
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    /// <summary>Group scope owner (nullable for personal/public).</summary>
    public Guid? OwnerGroupId { get; set; }
    public Group? OwnerGroup { get; set; }

    /// <summary>Parent page for the sidebar tree. Null = top-level.</summary>
    public Guid? ParentPageId { get; set; }
    public WikiPage? ParentPage { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ContentMarkdown { get; set; } = string.Empty;

    /// <summary>Display order among siblings.</summary>
    public int SortOrder { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }
    public Guid? LastEditedByUserId { get; set; }
    public User? LastEditedByUser { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
