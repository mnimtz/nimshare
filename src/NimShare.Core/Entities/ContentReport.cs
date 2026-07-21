namespace NimShare.Core.Entities;

/// <summary>
/// v1.10.82: App-Store-Blocker (Apple Guideline 1.2) — User müssen Inhalte
/// anderer User melden können (Missbrauch/illegal/spam). Admin sieht die
/// gesammelten Reports in der Moderations-Queue und kann handeln
/// (Ressource entfernen, User sperren, Report abweisen).
/// </summary>
public class ContentReport
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Wer gemeldet hat.</summary>
    public Guid ReporterUserId { get; set; }
    public User? Reporter { get; set; }

    /// <summary>Was gemeldet wurde — File / Folder / ShareLink / User / Contact / SignatureRequest / WikiPage</summary>
    public ContentReportSubjectKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    /// <summary>Menschlich lesbarer Anker (Dateiname, Slug etc.) für die Moderations-UI.</summary>
    public string? SubjectLabel { get; set; }
    /// <summary>Optional: der User dem die Ressource gehört — für Admin-Kontext.</summary>
    public Guid? SubjectOwnerUserId { get; set; }

    public ContentReportReason Reason { get; set; }
    /// <summary>Freitext-Details des Meldenden (bis ~2000 Zeichen).</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Moderations-Zustand ──
    public ContentReportStatus Status { get; set; } = ContentReportStatus.Open;
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public User? ResolvedBy { get; set; }
    public ContentReportResolution? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
}

public enum ContentReportSubjectKind
{
    File = 0,
    Folder = 1,
    ShareLink = 2,
    User = 3,
    Contact = 4,
    SignatureRequest = 5,
    WikiPage = 6,
    ChatMessage = 7,
}

public enum ContentReportReason
{
    Spam = 0,
    Harassment = 1,
    IllegalContent = 2,
    IntellectualProperty = 3,
    CsamOrChildSafety = 4,
    Impersonation = 5,
    Malware = 6,
    Other = 99,
}

public enum ContentReportStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2,
}

public enum ContentReportResolution
{
    ContentRemoved = 0,
    UserSuspended = 1,
    Warning = 2,
    NoAction = 3,
    Duplicate = 4,
}
