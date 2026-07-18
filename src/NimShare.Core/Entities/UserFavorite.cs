namespace NimShare.Core.Entities;

/// <summary>
/// A user's starred file or folder. Exactly one of {FileId, FolderId} is set;
/// enforced by the API layer.
/// </summary>
public class UserFavorite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid? FileId { get; set; }
    public StorageFile? File { get; set; }

    public Guid? FolderId { get; set; }
    public Folder? Folder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
