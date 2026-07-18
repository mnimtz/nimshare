namespace NimShare.Core.Entities;

public enum StorageFileStatus
{
    Pending = 0,
    Ready = 1,
    Deleted = 2
}

/// <summary>Where a file lives — controls who can see/modify it.</summary>
public enum FileScope
{
    /// <summary>Only the OwnerId user can see or modify.</summary>
    Personal = 0,

    /// <summary>Every signed-in user can see and add files. Only the uploader (or an Admin) can delete.</summary>
    Public = 1,

    /// <summary>All members of GroupId can see. Only the uploader, group Managers, or Admins can delete.</summary>
    Group = 2
}

public class StorageFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The user who uploaded the file. Owns quota accounting even for public/group files.</summary>
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>Where the file lives — Personal, Public, or in a Group.</summary>
    public FileScope Scope { get; set; } = FileScope.Personal;

    /// <summary>The group this file belongs to, when Scope=Group. Null otherwise.</summary>
    public Guid? GroupId { get; set; }
    public Group? Group { get; set; }

    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Path within the blob container, e.g. users/{ownerId}/{fileId}/{name}.</summary>
    public string BlobPath { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "files";

    /// <summary>Optional — populated at complete time when the client provides it.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Folder path within the user's own namespace, e.g. "Projects/Q3". Empty = root. Legacy — new files use FolderId.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>Owning folder — nullable for legacy files, always set for files uploaded in v0.6.0+.</summary>
    public Guid? FolderId { get; set; }
    public Folder? FolderRef { get; set; }

    public StorageFileStatus Status { get; set; } = StorageFileStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
}
