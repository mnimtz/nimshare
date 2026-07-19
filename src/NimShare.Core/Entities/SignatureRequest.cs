namespace NimShare.Core.Entities;

public enum SignatureRequestStatus
{
    Draft = 0,      // requester still building it
    Sent = 1,       // emails out, waiting on participants
    Completed = 2,  // all signers signed + all viewers acknowledged
    Cancelled = 3,  // requester aborted
    Declined = 4,   // at least one signer declined
}

public enum SignatureDeliveryOrder
{
    Parallel = 0,   // everyone can act right now
    Sequential = 1, // wait for previous participant to finish before notifying the next
}

/// <summary>
/// A signature-workflow request built by a signed-in user (initiator) around
/// one PDF document (SourceFileId). Once "sent", each participant receives an
/// email with a personalised link. When every signer has signed and every
/// viewer has confirmed, the request completes, we render a final PDF (with
/// signature overlays + an audit page) and store it as FinalFileId.
/// </summary>
public class SignatureRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceFileId { get; set; }
    public StorageFile? SourceFile { get; set; }

    public Guid InitiatorUserId { get; set; }
    public User? Initiator { get; set; }

    public SignatureRequestStatus Status { get; set; } = SignatureRequestStatus.Draft;
    public SignatureDeliveryOrder DeliveryOrder { get; set; } = SignatureDeliveryOrder.Parallel;

    /// <summary>Title shown to signers. Defaults to the source filename.</summary>
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTimeOffset? Deadline { get; set; }

    /// <summary>Populated on completion — a StorageFile pointer to the merged/signed PDF.</summary>
    public Guid? FinalFileId { get; set; }
    public StorageFile? FinalFile { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public ICollection<SignatureParticipant> Participants { get; set; } = new List<SignatureParticipant>();
    public ICollection<SignatureField> Fields { get; set; } = new List<SignatureField>();
    public ICollection<SignatureAudit> Audits { get; set; } = new List<SignatureAudit>();
}

public enum SignatureParticipantRole { Signer = 0, Viewer = 1 }
public enum SignatureParticipantStatus
{
    Pending = 0, Viewed = 1, Signed = 2, Declined = 3,
}

public class SignatureParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }
    public SignatureRequest? Request { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SignatureParticipantRole Role { get; set; } = SignatureParticipantRole.Signer;
    public int Order { get; set; }

    /// <summary>bcrypt-hashed opaque token; the raw token lives only in the emailed link.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public SignatureParticipantStatus Status { get; set; } = SignatureParticipantStatus.Pending;
    public DateTimeOffset? ViewedAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? DeclinedReason { get; set; }
    public string? IpHash { get; set; }
    public string? UserAgent { get; set; }
}

public enum SignatureFieldType { Signature = 0, Text = 1, Date = 2, Checkbox = 3 }
public enum SignatureFieldAnchor
{
    TopLeft = 0, TopCenter = 1, TopRight = 2,
    Center = 3,
    BottomLeft = 4, BottomCenter = 5, BottomRight = 6,
}

/// <summary>
/// A field the assigned participant must fill/sign. For v1.6.0 MVP the
/// positioning is coarse (page + anchor preset) — v1.6.1 will layer a
/// visual PDF-drag-to-place editor on top.
/// </summary>
public class SignatureField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public SignatureRequest? Request { get; set; }

    public Guid ParticipantId { get; set; }
    public SignatureParticipant? Participant { get; set; }

    public SignatureFieldType Type { get; set; } = SignatureFieldType.Signature;
    public int Page { get; set; } = 1;
    public SignatureFieldAnchor Anchor { get; set; } = SignatureFieldAnchor.BottomCenter;

    public string? Label { get; set; }

    /// <summary>Text/date value entered by the participant.</summary>
    public string? Value { get; set; }
    /// <summary>Blob path of the drawn signature image (PNG) when Type=Signature.</summary>
    public string? SignatureImagePath { get; set; }

    public DateTimeOffset? FilledAt { get; set; }
}

public enum SignatureAuditKind
{
    Invited = 0, Viewed = 1, Signed = 2, Declined = 3, Finalized = 4, Cancelled = 5,
}

public class SignatureAudit
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? ParticipantId { get; set; }
    public SignatureAuditKind Kind { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? IpHash { get; set; }
    public string? UserAgent { get; set; }
    public string? Note { get; set; }
}
