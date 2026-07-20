namespace NimShare.Core.Entities;

/// <summary>
/// Logical "shortcut" from a user's Personal scope to any file they can read
/// (typically a Public one). No new blob, no ref-count games — deleting the
/// pin just removes the shortcut; deleting the underlying file removes every
/// pin cascade-style. The Personal file browser shows pinned files with a
/// small 🔗 badge so it's obvious this is a reference, not a copy.
/// </summary>
public class FilePin
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid FileId { get; set; }
    public StorageFile? File { get; set; }

    /// <summary>Optional short note the user attached at pin time
    /// ("Vertragsvorlage", "wichtig für Q4"). Not shown to anyone else.</summary>
    public string? Note { get; set; }

    public DateTimeOffset PinnedAt { get; set; } = DateTimeOffset.UtcNow;
}
