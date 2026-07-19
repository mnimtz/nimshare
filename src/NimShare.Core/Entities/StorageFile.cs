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

    /// <summary>Cached AI-generated 2-3 sentence summary (v0.7.0+). Populated by first landing click.</summary>
    public string? AiSummary { get; set; }

    /// <summary>
    /// ISO-2 language code the cached AiSummary is in. Lets the summary
    /// endpoint know it must re-generate when a visitor with a different
    /// Accept-Language than the previous one asks for a summary — otherwise
    /// an English visitor would get the German summary a German visitor
    /// generated moments earlier.
    /// </summary>
    public string? AiSummaryLang { get; set; }

    /// <summary>Comma-separated AI-generated tags. Set on upload complete when the feature is on.</summary>
    public string? AiTags { get; set; }

    /// <summary>Content risk classification result (e.g. "clean", "pii", "credit-card"). Set on public uploads.</summary>
    public string? AiRiskFlag { get; set; }

    /// <summary>
    /// Extracted plain text of the file (PDF, docx, txt…) for classic keyword
    /// search. Written by the same AI post-processor that populates the
    /// semantic embedding, so the two search flavours share a single
    /// extraction pass. Truncated to 200 KB — bigger than most invoices,
    /// smaller than a full book.
    /// </summary>
    public string? ExtractedText { get; set; }

    /// <summary>Current version pointer — starts at 1 with the first upload, bumped on every re-upload.</summary>
    public int VersionNumber { get; set; } = 1;

    /// <summary>How many past versions to retain. 0 = infinite (retention job may still archive).</summary>
    public int KeepVersions { get; set; } = 10;

    /// <summary>Who currently holds the write lock, if any. Null = unlocked.
    /// The owner + admins can always break a lock.</summary>
    public Guid? LockedByUserId { get; set; }
    public User? LockedByUser { get; set; }

    /// <summary>Auto-unlock timestamp. Set to now + 30 min when acquired; the
    /// endpoints touching the lock also renew it. Prevents dead-locks when a
    /// browser tab is closed without releasing.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>"manual" (user hit the lock icon) vs "office" (OnlyOffice edit
    /// session took the lock automatically). Only informational — the enforcer
    /// treats both the same.</summary>
    public string? LockKind { get; set; }

    public StorageFileStatus Status { get; set; } = StorageFileStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
}
