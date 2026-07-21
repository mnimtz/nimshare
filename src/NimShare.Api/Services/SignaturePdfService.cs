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
    // Ein einfacher „Cursor Y wandert nach unten, bei Bedarf neue Seite"-
    // Layouter. Reicht für den MVP-Audit-Bericht; für richtige Typografie
    // wäre QuestPDF der bessere Weg.
    private static void RenderAuditPages(PdfDocument doc, SignatureRequest req,
        IReadOnlyList<SignatureAudit> audits)
    {
        var titleFont = new XFont("Arial", 18, XFontStyle.Bold);
        var h2Font    = new XFont("Arial", 12, XFontStyle.Bold);
        var bodyFont  = new XFont("Arial", 9.5, XFontStyle.Regular);
        var boldBody  = new XFont("Arial", 9.5, XFontStyle.Bold);
        var monoFont  = new XFont("Courier New", 8.5, XFontStyle.Regular);
        var muted     = new XFont("Arial", 8, XFontStyle.Regular);

        var lightGray = new XSolidBrush(XColor.FromArgb(245, 245, 247));
        var accent    = new XSolidBrush(XColor.FromArgb(0, 29, 61)); // Tungsten navy
        var okGreen   = new XSolidBrush(XColor.FromArgb(42, 127, 42));
        var warnRed   = new XSolidBrush(XColor.FromArgb(200, 40, 40));

        const double marginX = 40;
        const double topY = 50;
        const double bottomY = 800;

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

        // ── Titel-Header ─────────────────────────────────────────────
        g.DrawRectangle(accent, marginX, y - 5, pageWidth - 2 * marginX, 44);
        g.DrawString("SIGNATURE AUDIT REPORT", titleFont, XBrushes.White,
            new XRect(marginX + 12, y + 4, pageWidth - 2 * marginX - 24, 22), XStringFormats.CenterLeft);
        g.DrawString("NimShare · Signature Workflow · Full Forensic Trail",
            muted, XBrushes.White,
            new XRect(marginX + 12, y + 24, pageWidth - 2 * marginX - 24, 14), XStringFormats.CenterLeft);
        y += 56;

        // ── Status-Zeile ─────────────────────────────────────────────
        var statusText = req.Status.ToString().ToUpperInvariant();
        var statusColor = req.Status switch
        {
            SignatureRequestStatus.Completed => okGreen,
            SignatureRequestStatus.Declined or SignatureRequestStatus.Cancelled => warnRed,
            _ => (XSolidBrush)XBrushes.Gray,
        };
        g.DrawString("Status:", boldBody, XBrushes.Black, new XPoint(marginX, y));
        g.DrawString(statusText, boldBody, statusColor, new XPoint(marginX + 60, y));
        var reportGen = $"Report generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}";
        var rgSize = g.MeasureString(reportGen, muted);
        g.DrawString(reportGen, muted, XBrushes.Gray,
            new XPoint(pageWidth - marginX - rgSize.Width, y));
        y += 22;

        // ── Metadaten-Karte ──────────────────────────────────────────
        void KV(string k, string v, bool mono = false)
        {
            CheckPage(14);
            g.DrawString(k, boldBody, XBrushes.Black, new XPoint(marginX + 6, y));
            g.DrawString(v ?? "—", mono ? monoFont : bodyFont, XBrushes.Black,
                new XRect(marginX + 170, y - 2, pageWidth - marginX * 2 - 176, 16), XStringFormats.CenterLeft);
            y += 14;
        }

        g.DrawRectangle(lightGray, marginX, y - 2, pageWidth - 2 * marginX, 172);
        g.DrawString("Request metadata", h2Font, accent, new XPoint(marginX + 6, y + 10));
        y += 20;
        KV("Request ID",       req.Id.ToString(), mono: true);
        KV("Title",            req.Title ?? "");
        KV("Source document",  req.SourceFile?.Name ?? "—");
        KV("Initiator",        $"{req.Initiator?.DisplayName} <{req.Initiator?.Email}>");
        KV("Delivery order",   req.DeliveryOrder.ToString());
        KV("Created (UTC)",    req.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        if (req.SentAt.HasValue)      KV("Sent (UTC)",      req.SentAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        if (req.Deadline.HasValue)    KV("Deadline (UTC)",  req.Deadline.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        if (req.CompletedAt.HasValue) KV("Completed (UTC)", req.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        y += 12;

        // ── Participants ─────────────────────────────────────────────
        CheckPage(30);
        g.DrawString("Participants", h2Font, accent, new XPoint(marginX, y));
        y += 18;

        foreach (var p in req.Participants.OrderBy(x => x.Order))
        {
            CheckPage(72);
            var boxTop = y;
            // Statuspunkt links
            var pStat = p.Status switch
            {
                SignatureParticipantStatus.Signed => okGreen,
                SignatureParticipantStatus.Declined => warnRed,
                SignatureParticipantStatus.Viewed => (XSolidBrush)XBrushes.Orange,
                _ => (XSolidBrush)XBrushes.LightGray,
            };
            g.DrawEllipse(pStat, marginX, y + 3, 8, 8);
            g.DrawString($"#{p.Order + 1}  {p.Name}", boldBody, XBrushes.Black,
                new XPoint(marginX + 14, y + 10));
            var roleLbl = p.Role == SignatureParticipantRole.Signer ? "Signer" : "Viewer";
            var statusLbl = p.Status.ToString();
            var right = $"{roleLbl} · {statusLbl}";
            var rSize = g.MeasureString(right, muted);
            g.DrawString(right, muted, XBrushes.Gray,
                new XPoint(pageWidth - marginX - rSize.Width, y + 10));
            y += 14;
            g.DrawString(p.Email, monoFont, XBrushes.Gray, new XPoint(marginX + 14, y + 10));
            y += 14;
            if (p.ViewedAt.HasValue)
                g.DrawString($"Viewed: {p.ViewedAt.Value:yyyy-MM-dd HH:mm:ss 'UTC'}",
                    muted, XBrushes.Gray, new XPoint(marginX + 14, y + 10));
            if (p.SignedAt.HasValue)
                g.DrawString($"Signed: {p.SignedAt.Value:yyyy-MM-dd HH:mm:ss 'UTC'}",
                    muted, XBrushes.Black, new XPoint(marginX + 200, y + 10));
            y += 12;
            var ipLine = !string.IsNullOrEmpty(p.IpAddress)
                ? $"IP: {p.IpAddress}   Hash: {Truncate(p.IpHash, 24)}…"
                : (!string.IsNullOrEmpty(p.IpHash) ? $"IP hash: {p.IpHash}" : "IP: —");
            g.DrawString(ipLine, monoFont, XBrushes.Black, new XPoint(marginX + 14, y + 10));
            y += 12;
            if (!string.IsNullOrEmpty(p.UserAgent))
            {
                g.DrawString($"UA: {Truncate(p.UserAgent, 110)}", monoFont, XBrushes.Gray,
                    new XPoint(marginX + 14, y + 10));
                y += 12;
            }
            if (!string.IsNullOrEmpty(p.DeclinedReason))
            {
                g.DrawString($"Declined reason: {Truncate(p.DeclinedReason, 100)}",
                    muted, warnRed, new XPoint(marginX + 14, y + 10));
                y += 12;
            }
            // horizontale Trennlinie
            g.DrawLine(new XPen(XColor.FromArgb(220, 220, 225), 0.6),
                marginX, y + 4, pageWidth - marginX, y + 4);
            y += 12;
        }

        // ── Fields-Summary ──────────────────────────────────────────
        if (req.Fields != null && req.Fields.Any())
        {
            CheckPage(30);
            g.DrawString($"Fields ({req.Fields.Count})", h2Font, accent, new XPoint(marginX, y));
            y += 18;
            foreach (var f in req.Fields.OrderBy(f => f.Page).ThenBy(f => f.Y))
            {
                CheckPage(14);
                var pName = req.Participants.FirstOrDefault(p => p.Id == f.ParticipantId)?.Name ?? "?";
                var val = f.Type switch
                {
                    SignatureFieldType.Signature => string.IsNullOrEmpty(f.SignatureImagePath) ? "(unsigned)" : "(handwritten)",
                    SignatureFieldType.Checkbox => f.Value == "true" ? "[x]" : "[ ]",
                    _ => Truncate(f.Value ?? "—", 60),
                };
                var line = $"p.{f.Page}  {f.Type,-10}  {Truncate(pName, 22),-24}  {val}";
                g.DrawString(line, monoFont, XBrushes.Black, new XPoint(marginX + 6, y + 10));
                y += 12;
            }
            y += 6;
        }

        // ── Event-Timeline ──────────────────────────────────────────
        CheckPage(30);
        g.DrawString($"Event timeline ({audits.Count})", h2Font, accent, new XPoint(marginX, y));
        y += 18;

        if (audits.Count == 0)
        {
            g.DrawString("No events recorded.", muted, XBrushes.Gray, new XPoint(marginX + 6, y + 10));
            y += 14;
        }
        else
        {
            foreach (var a in audits)
            {
                CheckPage(60);
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
                // linke Farbleiste
                g.DrawRectangle(evtColor, marginX, y, 3, 44);
                g.DrawString(kindLabel, boldBody, evtColor, new XPoint(marginX + 10, y + 10));
                g.DrawString(pName, bodyFont, XBrushes.Black, new XPoint(marginX + 100, y + 10));
                var when = a.At.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
                var wSize = g.MeasureString(when, monoFont);
                g.DrawString(when, monoFont, XBrushes.Gray,
                    new XPoint(pageWidth - marginX - wSize.Width, y + 10));
                y += 14;

                // Zweite Zeile: IP/Location/Device/TZ
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(a.IpAddress)) meta.Add($"IP {a.IpAddress}");
                else if (!string.IsNullOrEmpty(a.IpHash)) meta.Add($"IP-hash {Truncate(a.IpHash, 16)}…");
                if (!string.IsNullOrEmpty(a.City) || !string.IsNullOrEmpty(a.Country))
                    meta.Add($"📍 {a.City}{(string.IsNullOrEmpty(a.City) || string.IsNullOrEmpty(a.Country) ? "" : ", ")}{a.Country}");
                if (!string.IsNullOrEmpty(a.DeviceType) && a.DeviceType != "Unknown")
                    meta.Add($"Device: {a.DeviceType}");
                if (!string.IsNullOrEmpty(a.Timezone)) meta.Add($"TZ: {a.Timezone}");
                if (meta.Count > 0)
                {
                    g.DrawString(string.Join("   ", meta), monoFont, XBrushes.Black,
                        new XPoint(marginX + 10, y + 10));
                    y += 12;
                }
                // Dritte Zeile: UA
                if (!string.IsNullOrEmpty(a.UserAgent))
                {
                    g.DrawString($"UA: {Truncate(a.UserAgent, 110)}", monoFont, XBrushes.Gray,
                        new XPoint(marginX + 10, y + 10));
                    y += 12;
                }
                if (!string.IsNullOrEmpty(a.Note))
                {
                    g.DrawString($"Note: {Truncate(a.Note, 110)}", muted, XBrushes.Black,
                        new XPoint(marginX + 10, y + 10));
                    y += 12;
                }
                y += 4;
            }
        }

        // ── Footer + Beweiskraft-Hinweis ───────────────────────────
        y += 8;
        CheckPage(40);
        var disclaimer = "This audit trail is an authoritative snapshot generated at PDF finalization. " +
            "It reflects all recorded workflow events for this request including timestamps, IP data " +
            "(where enabled), device fingerprinting hints and geographic origin (where a GeoIP provider " +
            "is configured). The full PDF is also cryptographically signed (PAdES-B, SHA-256) by the " +
            "initiator's certificate when available.";
        g.DrawString(disclaimer, muted, XBrushes.Gray,
            new XRect(marginX, y, pageWidth - 2 * marginX, 60), XStringFormats.TopLeft);
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
