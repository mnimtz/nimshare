namespace NimShare.Core.Entities;

/// <summary>
/// A team / department / project — a bucket where multiple users can share a
/// pool of files. Every group has at least one Manager (who can add/remove
/// members and manage files); regular Members can read all files in the group
/// but only manage what they uploaded themselves.
/// </summary>
public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user who created the group. Auto-promoted to Manager on creation.</summary>
    public Guid CreatedByUserId { get; set; }

    public ICollection<GroupMembership> Members { get; set; } = new List<GroupMembership>();
    public ICollection<StorageFile> Files { get; set; } = new List<StorageFile>();
}
