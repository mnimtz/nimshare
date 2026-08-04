using Microsoft.Extensions.Localization;
using NimShare.Core.Entities;

namespace NimShare.Api.Services;

/// <summary>
/// v1.11.80: SignatureAudit.Note ist bewusst unlokalisiert gespeichert (siehe
/// Kommentar in SignController.RecordViewed — die Notiz wird 1:1 in den
/// forensischen PDF/JSON-Audit-Bericht gedruckt, der als stabiler,
/// sprachunabhängiger Nachweis archiviert wird). Für die WEB-ANZEIGE
/// (Detail.cshtml, Audit.cshtml) übersetzt dieser Helper die bekannten
/// internen Schlüsselwörter ("created draft", "invited", "reminder", …) in
/// EFIGS+NL-Klartext, ohne die gespeicherte Note oder den PDF-Export
/// anzufassen. Echte Freitext-Inhalte (Ablehnungsgrund, Krypto-Zertifikat-
/// Details) bleiben unverändert, weil dafür keine Übersetzung existiert.
/// </summary>
public static class SignatureAuditNoteFormatter
{
    public static string? Display(SignatureAudit a, IStringLocalizer<SharedResources> T)
    {
        var note = a.Note;
        if (string.IsNullOrEmpty(note)) return null;

        switch (note)
        {
            case "created draft": return T["sig.audit.note.created_draft"].Value;
            case "invited": return T["sig.audit.note.invited"].Value;
            case "reminder": return T["sig.audit.note.reminder"].Value;
            case "manual-reminder": return T["sig.audit.note.manual_reminder"].Value;
            case "sequential-turn": return T["sig.audit.note.sequential_turn"].Value;
            case "deadline expired": return T["sig.audit.note.deadline_expired"].Value;
            case "signed via certificate stamp": return T["sig.audit.note.signed_cert_stamp"].Value;
            case "signed via own certificate": return T["sig.audit.note.signed_own_cert"].Value;
            case "acknowledged (no page-scroll data)": return T["sig.audit.note.ack_no_scroll"].Value;
        }

        const string emailFailedPrefix = "email-failed:";
        if (note.StartsWith(emailFailedPrefix, StringComparison.Ordinal))
            return T["sig.audit.note.email_failed", note[emailFailedPrefix.Length..].Trim()].Value;

        const string reassignedPrefix = "reassigned:";
        if (note.StartsWith(reassignedPrefix, StringComparison.Ordinal))
            return T["sig.audit.note.reassigned_to", note[reassignedPrefix.Length..]].Value;

        const string reassignedFromPrefix = "reassigned-from:";
        if (note.StartsWith(reassignedFromPrefix, StringComparison.Ordinal))
            return T["sig.audit.note.reassigned_from", note[reassignedFromPrefix.Length..]].Value;

        const string pagesPrefix = "Pages viewed: ";
        if (note.StartsWith(pagesPrefix, StringComparison.Ordinal))
        {
            var rest = note[pagesPrefix.Length..];
            var ofIdx = rest.IndexOf(" of ", StringComparison.Ordinal);
            return ofIdx < 0
                ? T["sig.audit.note.pages_viewed", rest].Value
                : T["sig.audit.note.pages_viewed_of", rest[..ofIdx], rest[(ofIdx + 4)..]].Value;
        }

        // Unbekannt/dynamisch (Ablehnungsgrund, Krypto-Stempel-Info, …) —
        // unverändert anzeigen, dafür gibt es keine sinnvolle Übersetzung.
        return note;
    }
}
