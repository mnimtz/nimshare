namespace NimShare.Core.Entities;

public enum StorageFileStatus
{
    Pending = 0,
    Ready = 1,
    Deleted = 2
}

public class StorageFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Path within the blob container, e.g. users/{ownerId}/{fileId}/{name}.</summary>
    public string BlobPath { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "files";

    /// <summary>Optional — populated at complete time when the client provides it.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Folder path within the user's own namespace, e.g. "Projects/Q3". Empty = root.</summary>
    public string Folder { get; set; } = string.Empty;

    public StorageFileStatus Status { get; set; } = StorageFileStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
}
