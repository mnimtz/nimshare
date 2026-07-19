namespace NimShare.Core.Entities;

/// <summary>
/// An immutable snapshot of a StorageFile's byte content at a point in time.
/// The current version is whichever row shares the same BlobPath as
/// StorageFile.BlobPath — older versions carry a versioned blob path so their
/// bytes remain retrievable until pruned.
/// </summary>
public class StorageFileVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FileId { get; set; }
    public StorageFile? File { get; set; }

    /// <summary>1-based, increments per file. Read-only reference for restore actions.</summary>
    public int VersionNumber { get; set; }

    /// <summary>Blob storage path — versioned (users/{uid}/{fid}/versions/{n}/{name}) for old versions,
    /// same as StorageFile.BlobPath for the current one.</summary>
    public string BlobPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public string? Sha256 { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
