using NimShare.Core.Entities;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace NimShare.Api.Services;

/// <summary>
/// Merges signature overlays and an audit page into the source PDF. Uses
/// PdfSharpCore (MIT) — good enough for the MVP; a later release can look at
/// QuestPDF for richer typesetting.
/// </summary>
public interface ISignaturePdfService
{
    Task<byte[]> RenderFinalAsync(SignatureRequest req, byte[] sourcePdf,
        Dictionary<Guid, byte[]> participantSignatureImages,
        // v1.10.86: Audit-Events für die eingebettete Audit-Seite. Optional
        // damit alte Aufrufer (Tests) nicht brechen — ist null → keine
        // Timeline, sonst volle Forensik pro Event mit IP/UA/Device etc.
        IReadOnlyList<SignatureAudit>? audits = null,
        CancellationToken ct = default);
}

public class SignaturePdfService : ISignaturePdfService
{
    // v1.11.47: siehe LiberationPdfFontResolver.cs — ohne diese Registrierung
    // rendert PdfSharpCore "Arial"/"Courier New" auf Linux als falschen
    // Ersatz-Font. Static Constructor läuft genau einmal, unabhängig von DI.
    static SignaturePdfService()
    {
        PdfSharpCore.Fonts.GlobalFontSettings.FontResolver ??= new LiberationPdfFontResolver();
    }

    public Task<byte[]> RenderFinalAsync(SignatureRequest req, byte[] sourcePdf,
        Dictionary<Guid, byte[]> sigImages,
        IReadOnlyList<SignatureAudit>? audits = null,
        CancellationToken ct = default)
    {
        using var srcMs = new MemoryStream(sourcePdf);
        using var doc = PdfReader.Open(srcMs, PdfDocumentOpenMode.Modify);

        // Overlay signature fields onto their pages.
        foreach (var field in req.Fields.OrderBy(f => f.Page).ThenBy(f => f.Anchor))
        {
            if (field.Page < 1 || field.Page > doc.PageCount) continue;
            var page = doc.Pages[field.Page - 1];
            using var gfx = XGraphics.FromPdfPage(page);
            // Prefer the exact coordinates from the visual editor when any
            // dimension is > 0; otherwise fall back to the anchor preset.
            double x, y, w, h;
            if (field.Width > 0 && field.Height > 0)
            {
                x = field.X; y = field.Y; w = field.Width; h = field.Height;
            }
            else
            {
                (x, y, w, h) = AnchorRect(page, field.Anchor);
            }
            switch (field.Type)
            {
                case SignatureFieldType.Signature:
                    if (sigImages.TryGetValue(field.ParticipantId, out var png) && png.Length > 0)
                    {
                        using var imgMs = new MemoryStream(png);
                        var img = XImage.FromStream(() => new MemoryStream(png));
                        gfx.DrawImage(img, x, y, w, h);
                    }
                    else if (!string.IsNullOrEmpty(field.Value))
                    {
                        DrawTypedName(gfx, field.Value!, x, y, w, h);
                    }
                    if (field.FilledAt is DateTimeOffset ts)
                    {
                        var font = new XFont("Arial", 7, XFontStyle.Regular);
                        gfx.DrawString(ts.ToString("dd.MM.yyyy HH:mm 'UTC'"),
                            font, XBrushes.Gray, new XPoint(x, y + h + 10));
                    }
                    break;
                case SignatureFieldType.Text:
                case SignatureFieldType.Date:
                    if (!string.IsNullOrEmpty(field.Value))
                    {
                        var font = new XFont("Arial", 10, XFontStyle.Regular);
                        gfx.DrawString(field.Value, font, XBrushes.Black,
                            new XRect(x, y, w, h), XStringFormats.CenterLeft);
                    }
                    break;
                case SignatureFieldType.Checkbox:
                    var checkPen = new XPen(XColors.Black, 1);
                    gfx.DrawRectangle(checkPen, x, y, 12, 12);
                    if (!string.IsNullOrEmpty(field.Value) && field.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        gfx.DrawLine(checkPen, x, y, x + 12, y + 12);
                        gfx.DrawLine(checkPen, x + 12, y, x, y + 12);
                    }
                    break;
            }
        }

        // v1.10.86: Ausführlicher Audit-Bericht als Anhang — Marcus's
        // Report: „Audit Seite ist nicht wirklich schön, hat kaum
        // Informationen zum Workflow, Audit, IP…". Der bisherige Bericht
        // hatte nur Name+Email+IP-Hash. Jetzt: Header-Box mit Vorgangs-
        // Metadaten, Participants-Tabelle mit Full-IP/UserAgent/Timezone,
        // Ereignis-Timeline mit jedem Event, Auto-Page-Break, Footer.
        RenderAuditPages(doc, req, audits ?? Array.Empty<SignatureAudit>());

        using var outMs = new MemoryStream();
        doc.Save(outMs, false);
        return Task.FromResult(outMs.ToArray());
    }

    // ── Audit-Seiten-Renderer ────────────────────────────────────────────
    // v1.11.47 — Marcus's Report: der Bericht sah "unfertig" aus, mit
    // überlappenden Zeilen und schlechten Zeilenumbrüchen. Root Cause #1
    // war der fehlende Font-Resolver (siehe static ctor oben); Root Cause
    // #2 war, dass DrawString in PdfSharpCore NICHT automatisch umbricht —
    // jeder lange Wert (E-Mail, User-Agent, der Disclaimer-Absatz) wurde
    // als EINE Zeile gezeichnet und lief über den Rand bzw. unter die
    // nächste Zeile. Fix: WrapText/DrawWrapped unten + durchgängig
    // content-abhängige Zeilenhöhen statt fixer Konstanten.
    private static void RenderAuditPages(PdfDocument doc, SignatureRequest req,
        IReadOnlyList<SignatureAudit> audits)
    {
        var titleFont = new XFont("Arial", 18, XFontStyle.Bold);
        var h2Font    = new XFont("Arial", 12, XFontStyle.Bold);
        var bodyFont  = new XFont("Arial", 9.5, XFontStyle.Regular);
        var boldBody  = new XFont("Arial", 9.5, XFontStyle.Bold);
        var monoFont  = new XFont("Courier New", 8.5, XFontStyle.Regular);
        var muted     = new XFont("Arial", 8, XFontStyle.Regular);

        var lightGray  = new XSolidBrush(XColor.FromArgb(246, 247, 249));
        var cardBorder = new XPen(XColor.FromArgb(224, 227, 232), 0.75);
        var accent     = new XSolidBrush(XColor.FromArgb(0, 29, 61)); // Tungsten navy
        var accentLine = new XPen(XColor.FromArgb(0, 29, 61), 1.2);
        var okGreen    = new XSolidBrush(XColor.FromArgb(42, 127, 42));
        var warnRed    = new XSolidBrush(XColor.FromArgb(200, 40, 40));

        const double marginX = 40;
        const double topY = 50;
        const double bottomY = 790;
        const double lineH = 13;

        var (page, g) = NewPage(doc);
        double y = topY;
        int pageNo = 1;
        double pageWidth = page.Width.Point;

        void CheckPage(double needed)
        {
            if (y + needed > bottomY)
            {
                DrawFooter(g, page, pageNo);
                var (np, ng) = NewPage(doc);
                page = np; g = ng; pageNo++;
                pageWidth = page.Width.Point;
                y = topY;
            }
        }

        // Bug-Fix v1.11.7: erzwungener Seitenumbruch (nicht nur "falls kein
        // Platz mehr") — Marcus wollte, dass "Event timeline" immer sauber
        // auf einer neuen Seite beginnt statt direkt unter Fields angehängt
        // zu werden. No-op wenn wir gerade erst eine frische Seite begonnen
        // haben (sonst gäbe es unnötige Leerseiten).
        void ForcePage()
        {
            if (y > topY + 5)
            {
                DrawFooter(g, page, pageNo);
                var (np, ng) = NewPage(doc);
                page = np; g = ng; pageNo++;
                pageWidth = page.Width.Point;
                y = topY;
            }
        }

        // Bricht Text manuell um — PdfSharpCore's DrawString tut das NICHT
        // von selbst, egal ob man ein XRect übergibt (das clippt nur).
        List<string> WrapText(string text, XFont font, double maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return new List<string> { "" };
            var words = text.Split(' ');
            var lines = new List<string>();
            var cur = "";
            foreach (var w in words)
            {
                var trial = cur.Length == 0 ? w : cur + " " + w;
                if (g.MeasureString(trial, font).Width > maxWidth && cur.Length > 0)
                {
                    lines.Add(cur);
                    cur = w;
                }
                else
                {
                    cur = trial;
                }
                // Ein einzelnes Wort (z.B. eine lange URL/Hash ohne
                // Leerzeichen) breiter als maxWidth: hart am Zeichen umbrechen
                // statt endlos über den Rand zu laufen.
                while (g.MeasureString(cur, font).Width > maxWidth && cur.Contains(' ') == false && cur.Length > 1)
                {
                    var cut = cur.Length - 1;
                    while (cut > 1 && g.MeasureString(cur[..cut], font).Width > maxWidth) cut--;
                    lines.Add(cur[..cut]);
                    cur = cur[cut..];
                }
            }
            if (cur.Length > 0) lines.Add(cur);
            return lines.Count == 0 ? new List<string> { "" } : lines;
        }

        // Zeichnet umgebrochenen Text ab (x, topOfBlock) und gibt die
        // tatsächlich verbrauchte Höhe zurück, damit der Aufrufer y korrekt
        // weiterschieben kann (statt einer geschätzten Konstante).
        double DrawWrapped(string text, XFont font, XBrush brush, double x, double topOfBlock,
            double maxWidth, double rowLineH, int maxLines = int.MaxValue)
        {
            var lines = WrapText(text, font, maxWidth);
            if (lines.Count > maxLines)
            {
                lines = lines.Take(maxLines).ToList();
                lines[^1] = lines[^1].TrimEnd() + " …";
            }
            var yy = topOfBlock;
            foreach (var line in lines)
            {
                g.DrawString(line, font, brush, new XPoint(x, yy));
                yy += rowLineH;
            }
            return yy - topOfBlock;
        }

        // ── Titel-Header ─────────────────────────────────────────────
        g.DrawRectangle(accent, marginX, y - 5, pageWidth - 2 * marginX, 46);
        g.DrawString("SIGNATURE AUDIT REPORT", titleFont, XBrushes.White,
            new XRect(marginX + 14, y + 3, pageWidth - 2 * marginX - 28, 24), XStringFormats.CenterLeft);
        g.DrawString("NimShare  ·  Signature Workflow  ·  Full Forensic Trail",
            muted, XBrushes.White,
            new XRect(marginX + 14, y + 26, pageWidth - 2 * marginX - 28, 14), XStringFormats.CenterLeft);
        y += 60;

        // ── Status-Zeile ─────────────────────────────────────────────
        var statusText = req.Status.ToString().ToUpperInvariant();
        var statusColor = req.Status switch
        {
            SignatureRequestStatus.Completed => okGreen,
            SignatureRequestStatus.Declined or SignatureRequestStatus.Cancelled => warnRed,
            _ => (XSolidBrush)XBrushes.Gray,
        };
        g.DrawString("Status:", boldBody, XBrushes.Black, new XPoint(marginX, y));
        g.DrawString(statusText, boldBody, statusColor, new XPoint(marginX + 46, y));
        var reportGen = $"Report generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}";
        var rgSize = g.MeasureString(reportGen, muted);
        g.DrawString(reportGen, muted, XBrushes.Gray,
            new XPoint(pageWidth - marginX - rgSize.Width, y));
        y += 12;
        g.DrawLine(new XPen(XColor.FromArgb(224, 227, 232), 0.75), marginX, y, pageWidth - marginX, y);
        y += 16;

        // ── Metadaten-Karte ──────────────────────────────────────────
        // v1.11.47: statt einer fest verdrahteten Box-Höhe wird die Karte
        // in zwei Durchgängen gebaut — erst die Zeilenhöhen messen (inkl.
        // Umbruch bei langen Werten wie dem Initiator), dann Box + Inhalt
        // mit der tatsächlich benötigten Höhe zeichnen. Löst sowohl den
        // alten "Participants beginnt im Kasten"-Bug als auch die neue
        // Ursache dafür (variable Zeilenzahl durch Umbruch).
        const double labelColW = 132;
        double valueColX = marginX + 20 + labelColW;
        double valueColW = pageWidth - 2 * marginX - 32 - labelColW;

        var initiatorName = req.Initiator?.DisplayName;
        var initiatorEmail = req.Initiator?.Email;
        var initiatorLine = string.IsNullOrWhiteSpace(initiatorName)
            ? (initiatorEmail ?? "—")
            : $"{initiatorName} <{initiatorEmail}>";

        var metaRows = new List<(string Label, string Value, bool Mono)>
        {
            ("Request ID",      req.Id.ToString(), true),
            ("Title",           string.IsNullOrWhiteSpace(req.Title) ? "—" : req.Title, false),
            ("Source document", req.SourceFile?.Name ?? "—", false),
            ("Initiator",       initiatorLine, false),
            ("Delivery order",  req.DeliveryOrder.ToString(), false),
            ("Created (UTC)",   req.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), false),
        };
        if (req.SentAt.HasValue)      metaRows.Add(("Sent (UTC)",      req.SentAt.Value.ToString("yyyy-MM-dd HH:mm:ss"), false));
        if (req.Deadline.HasValue)    metaRows.Add(("Deadline (UTC)",  req.Deadline.Value.ToString("yyyy-MM-dd HH:mm:ss"), false));
        if (req.CompletedAt.HasValue) metaRows.Add(("Completed (UTC)", req.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"), false));

        double RowHeight(string v, bool mono) =>
            Math.Max(lineH, WrapText(v, mono ? monoFont : bodyFont, valueColW).Count * lineH) + 3;

        var metaContentH = metaRows.Sum(r => RowHeight(r.Value, r.Mono));
        var metaBoxHeight = 26 + metaContentH + 10;
        CheckPage(metaBoxHeight + 8);

        var metaBoxTop = y;
        g.DrawRectangle(lightGray, marginX, metaBoxTop, pageWidth - 2 * marginX, metaBoxHeight);
        g.DrawRectangle(cardBorder, marginX, metaBoxTop, pageWidth - 2 * marginX, metaBoxHeight);
        g.DrawString("Request metadata", h2Font, accent, new XPoint(marginX + 12, metaBoxTop + 18));
        y = metaBoxTop + 32;
        foreach (var row in metaRows)
        {
            var rh = RowHeight(row.Value, row.Mono);
            g.DrawString(row.Label, boldBody, XBrushes.Black, new XPoint(marginX + 20, y));
            DrawWrapped(row.Value, row.Mono ? monoFont : bodyFont, XBrushes.Black,
                valueColX, y, valueColW, lineH);
            y += rh;
        }
        y = metaBoxTop + metaBoxHeight + 22;

        // ── Participants ─────────────────────────────────────────────
        CheckPage(26);
        g.DrawString("Participants", h2Font, accent, new XPoint(marginX, y));
        y += 16;
        g.DrawLine(accentLine, marginX, y, marginX + 22, y);
        y += 14;

        foreach (var p in req.Participants.OrderBy(x => x.Order))
        {
            // Vorab abschätzen wie viele Zusatzzeilen dieser Teilnehmer
            // braucht, damit CheckPage die Karte nicht mittendrin abschneidet.
            var uaLineCount = !string.IsNullOrEmpty(p.UserAgent)
                ? WrapText($"UA: {p.UserAgent}", monoFont, pageWidth - 2 * marginX - 14).Count
                : 0;
            CheckPage(60 + uaLineCount * 11);

            var pStat = p.Status switch
            {
                SignatureParticipantStatus.Signed => okGreen,
                SignatureParticipantStatus.Declined => warnRed,
                SignatureParticipantStatus.Viewed => (XSolidBrush)XBrushes.Orange,
                _ => (XSolidBrush)XBrushes.LightGray,
            };
            // v1.11.47 HOTFIX: alle Zeilen dieser Karte zeichnen jetzt exakt
            // AN y (kein "+8"/"+9"-Baseline-Offset mehr), und y wird nach
            // jeder Zeile um genau die Zeilenhöhe weitergeschoben — inkl.
            // der über DrawWrapped gezeichneten Zeilen. Vorher mischten sich
            // zwei Konventionen (manuelle Offsets vs. DrawWrapped ohne
            // Offset), wodurch IP/Hash- und UA-Zeile übereinander landeten.
            g.DrawEllipse(pStat, marginX, y + 3, 8, 8);
            g.DrawString($"#{p.Order + 1}  {p.Name}", boldBody, XBrushes.Black,
                new XPoint(marginX + 16, y));
            var roleLbl = p.Role == SignatureParticipantRole.Signer ? "Signer" : "Viewer";
            var right = $"{roleLbl}  ·  {p.Status}";
            var rSize = g.MeasureString(right, muted);
            g.DrawString(right, muted, XBrushes.Gray,
                new XPoint(pageWidth - marginX - rSize.Width, y));
            y += 16;
            g.DrawString(p.Email, monoFont, XBrushes.Gray, new XPoint(marginX + 16, y));
            y += 14;

            if (p.ViewedAt.HasValue || p.SignedAt.HasValue)
            {
                if (p.ViewedAt.HasValue)
                    g.DrawString($"Viewed  {p.ViewedAt.Value:yyyy-MM-dd HH:mm:ss 'UTC'}",
                        muted, XBrushes.Gray, new XPoint(marginX + 16, y));
                if (p.SignedAt.HasValue)
                    g.DrawString($"Signed  {p.SignedAt.Value:yyyy-MM-dd HH:mm:ss 'UTC'}",
                        muted, okGreen, new XPoint(marginX + 220, y));
                y += 13;
            }
            var ipLine = !string.IsNullOrEmpty(p.IpAddress)
                ? $"IP {p.IpAddress}    Hash {Truncate(p.IpHash, 24)}"
                : (!string.IsNullOrEmpty(p.IpHash) ? $"IP-hash {p.IpHash}" : "IP —");
            g.DrawString(ipLine, monoFont, XBrushes.Black, new XPoint(marginX + 16, y));
            y += 13;
            if (!string.IsNullOrEmpty(p.UserAgent))
            {
                y += DrawWrapped($"UA  {p.UserAgent}", monoFont, XBrushes.Gray,
                    marginX + 16, y, pageWidth - 2 * marginX - 22, 11, maxLines: 2);
            }
            if (!string.IsNullOrEmpty(p.DeclinedReason))
            {
                y += DrawWrapped($"Declined reason: {p.DeclinedReason}", muted, warnRed,
                    marginX + 16, y + 2, pageWidth - 2 * marginX - 22, 11);
            }
            y += 6;
            g.DrawLine(new XPen(XColor.FromArgb(224, 227, 232), 0.6),
                marginX, y, pageWidth - marginX, y);
            y += 14;
        }

        // ── Fields-Summary ──────────────────────────────────────────
        if (req.Fields != null && req.Fields.Any())
        {
            CheckPage(30);
            g.DrawString($"Fields ({req.Fields.Count})", h2Font, accent, new XPoint(marginX, y));
            y += 16;
            g.DrawLine(accentLine, marginX, y, marginX + 22, y);
            y += 14;

            // Feste Spalten-X-Positionen statt String-Padding — Padding
            // richtet sich nur in echten Monospace-Fonts sauber aus, und
            // war hier ohnehin bislang von der falschen Font-Substitution
            // betroffen. Kopfzeile + Spalten machen daraus eine echte
            // Tabelle statt einer einzigen kryptischen Zeile pro Feld.
            double colPage = marginX + 6, colType = marginX + 46, colWho = marginX + 130, colVal = marginX + 300;
            g.DrawString("Page", muted, XBrushes.Gray, new XPoint(colPage, y));
            g.DrawString("Type", muted, XBrushes.Gray, new XPoint(colType, y));
            g.DrawString("Participant", muted, XBrushes.Gray, new XPoint(colWho, y));
            g.DrawString("Value", muted, XBrushes.Gray, new XPoint(colVal, y));
            y += 10;
            g.DrawLine(new XPen(XColor.FromArgb(224, 227, 232), 0.6), marginX, y, pageWidth - marginX, y);
            y += 12;

            foreach (var f in req.Fields.OrderBy(f => f.Page).ThenBy(f => f.Y))
            {
                CheckPage(15);
                var pName = req.Participants.FirstOrDefault(p => p.Id == f.ParticipantId)?.Name ?? "?";
                var val = f.Type switch
                {
                    SignatureFieldType.Signature => string.IsNullOrEmpty(f.SignatureImagePath) ? "(unsigned)" : "(handwritten)",
                    SignatureFieldType.Checkbox => f.Value == "true" ? "[x] checked" : "[ ] unchecked",
                    _ => Truncate(f.Value ?? "—", 46),
                };
                g.DrawString($"p.{f.Page}", bodyFont, XBrushes.Black, new XPoint(colPage, y + 9));
                g.DrawString(f.Type.ToString(), bodyFont, XBrushes.Black, new XPoint(colType, y + 9));
                g.DrawString(Truncate(pName, 26), bodyFont, XBrushes.Black, new XPoint(colWho, y + 9));
                g.DrawString(val, bodyFont, XBrushes.Black, new XPoint(colVal, y + 9));
                y += 15;
            }
            y += 8;
        }

        // ── Event-Timeline ──────────────────────────────────────────
        // Bug-Fix v1.11.7: Marcus wollte einen sauberen Seitenumbruch hier
        // statt "Event timeline" direkt unter Fields anzuhängen.
        ForcePage();
        g.DrawString($"Event timeline ({audits.Count})", h2Font, accent, new XPoint(marginX, y));
        y += 16;
        g.DrawLine(accentLine, marginX, y, marginX + 22, y);
        y += 14;

        if (audits.Count == 0)
        {
            g.DrawString("No events recorded.", muted, XBrushes.Gray, new XPoint(marginX + 6, y + 9));
            y += 14;
        }
        else
        {
            foreach (var a in audits)
            {
                var uaLineCount = !string.IsNullOrEmpty(a.UserAgent)
                    ? WrapText($"UA  {a.UserAgent}", monoFont, pageWidth - 2 * marginX - 16).Count
                    : 0;
                var noteLineCount = !string.IsNullOrEmpty(a.Note)
                    ? WrapText($"Note: {a.Note}", muted, pageWidth - 2 * marginX - 16).Count
                    : 0;
                CheckPage(40 + uaLineCount * 11 + noteLineCount * 11);

                var rowTop = y;
                var pName = a.ParticipantId is Guid pid
                    ? req.Participants.FirstOrDefault(p => p.Id == pid)?.Name ?? "?"
                    : "system";
                var kindLabel = a.Kind.ToString().ToUpperInvariant();
                var evtColor = a.Kind switch
                {
                    SignatureAuditKind.Signed or SignatureAuditKind.Finalized => okGreen,
                    SignatureAuditKind.Declined or SignatureAuditKind.Cancelled => warnRed,
                    _ => (XSolidBrush)XBrushes.Gray,
                };
                // v1.11.47 HOTFIX: siehe Participants-Block oben — auch hier
                // zeichnet jede Zeile exakt AN y, kein Mix aus "+9"-Offset
                // und offsetlosem DrawWrapped mehr (verursachte die
                // "INVITED"/"Note: ..."-Überlappung).
                g.DrawString(kindLabel, boldBody, evtColor, new XPoint(marginX + 10, y));
                g.DrawString(pName, bodyFont, XBrushes.Black, new XPoint(marginX + 108, y));
                var when = a.At.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
                var wSize = g.MeasureString(when, monoFont);
                g.DrawString(when, monoFont, XBrushes.Gray,
                    new XPoint(pageWidth - marginX - wSize.Width, y));
                y += 15;

                // Zweite Zeile: IP/Location/Device/TZ. Kein Emoji-Pin mehr —
                // Liberation-Fonts haben keine (Farb-)Emoji-Glyphen, das
                // Symbol wäre als leere/kaputte Box gerendert worden.
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(a.IpAddress)) meta.Add($"IP {a.IpAddress}");
                else if (!string.IsNullOrEmpty(a.IpHash)) meta.Add($"IP-hash {Truncate(a.IpHash, 16)}");
                if (!string.IsNullOrEmpty(a.City) || !string.IsNullOrEmpty(a.Country))
                    meta.Add($"Location: {a.City}{(string.IsNullOrEmpty(a.City) || string.IsNullOrEmpty(a.Country) ? "" : ", ")}{a.Country}");
                if (!string.IsNullOrEmpty(a.DeviceType) && a.DeviceType != "Unknown")
                    meta.Add($"Device: {a.DeviceType}");
                if (!string.IsNullOrEmpty(a.Timezone)) meta.Add($"TZ: {a.Timezone}");
                if (meta.Count > 0)
                {
                    y += DrawWrapped(string.Join("    ", meta), monoFont, XBrushes.Black,
                        marginX + 10, y, pageWidth - 2 * marginX - 16, 12);
                }
                if (!string.IsNullOrEmpty(a.UserAgent))
                {
                    y += DrawWrapped($"UA  {a.UserAgent}", monoFont, XBrushes.Gray,
                        marginX + 10, y, pageWidth - 2 * marginX - 16, 11, maxLines: 2);
                }
                if (!string.IsNullOrEmpty(a.Note))
                {
                    y += DrawWrapped($"Note: {a.Note}", muted, XBrushes.Black,
                        marginX + 10, y, pageWidth - 2 * marginX - 16, 11);
                }
                // Linke Farbleiste über die komplette, tatsächlich
                // verbrauchte Höhe dieses Events (statt einer festen
                // 44pt-Konstante, die bei mehrzeiligem UA/Note zu kurz war).
                g.DrawRectangle(evtColor, marginX, rowTop, 3, y - rowTop - 3);
                y += 7;
            }
        }

        // ── Footer + Beweiskraft-Hinweis ───────────────────────────
        y += 10;
        var disclaimer = "This audit trail is an authoritative snapshot generated at PDF finalization. " +
            "It reflects all recorded workflow events for this request including timestamps, IP data " +
            "(where enabled), device fingerprinting hints and geographic origin (where a GeoIP provider " +
            "is configured). The full PDF is also cryptographically signed (PAdES-B, SHA-256) by the " +
            "initiator's certificate when available.";
        var disclaimerH = WrapText(disclaimer, muted, pageWidth - 2 * marginX).Count * 11 + 6;
        CheckPage(disclaimerH + 10);
        g.DrawLine(new XPen(XColor.FromArgb(224, 227, 232), 0.6), marginX, y, pageWidth - marginX, y);
        y += 10;
        DrawWrapped(disclaimer, muted, XBrushes.Gray, marginX, y, pageWidth - 2 * marginX, 11);
        DrawFooter(g, page, pageNo);
    }

    private static (PdfPage page, XGraphics gfx) NewPage(PdfDocument doc)
    {
        var p = doc.AddPage();
        p.Size = PdfSharpCore.PageSize.A4;
        return (p, XGraphics.FromPdfPage(p));
    }

    private static void DrawFooter(XGraphics g, PdfPage page, int pageNo)
    {
        var f = new XFont("Arial", 7, XFontStyle.Regular);
        var footer = $"Generated by NimShare · {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm 'UTC'} · Audit page {pageNo}";
        g.DrawString(footer, f, XBrushes.Gray,
            new XRect(0, page.Height.Point - 24, page.Width.Point, 14),
            XStringFormats.Center);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static (double x, double y, double w, double h) AnchorRect(PdfPage page, SignatureFieldAnchor a)
    {
        var pw = page.Width.Point;
        var ph = page.Height.Point;
        const double sigW = 180, sigH = 60;
        double x, y;
        switch (a)
        {
            case SignatureFieldAnchor.TopLeft:      x = 50;         y = 50; break;
            case SignatureFieldAnchor.TopCenter:    x = (pw-sigW)/2;y = 50; break;
            case SignatureFieldAnchor.TopRight:     x = pw-sigW-50; y = 50; break;
            case SignatureFieldAnchor.Center:       x = (pw-sigW)/2;y = (ph-sigH)/2; break;
            case SignatureFieldAnchor.BottomLeft:   x = 50;         y = ph-sigH-70; break;
            case SignatureFieldAnchor.BottomRight:  x = pw-sigW-50; y = ph-sigH-70; break;
            case SignatureFieldAnchor.BottomCenter:
            default:                                x = (pw-sigW)/2;y = ph-sigH-70; break;
        }
        return (x, y, sigW, sigH);
    }

    private static void DrawTypedName(XGraphics gfx, string name, double x, double y, double w, double h)
    {
        var font = new XFont("Arial", 22, XFontStyle.Italic);
        gfx.DrawString(name, font, XBrushes.Black, new XRect(x, y, w, h), XStringFormats.Center);
        // A subtle underline below the name so it looks like a signature line.
        var pen = new XPen(XColors.Black, 0.8);
        gfx.DrawLine(pen, x, y + h - 4, x + w, y + h - 4);
    }
}
