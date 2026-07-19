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
        CancellationToken ct = default);
}

public class SignaturePdfService : ISignaturePdfService
{
    public Task<byte[]> RenderFinalAsync(SignatureRequest req, byte[] sourcePdf,
        Dictionary<Guid, byte[]> sigImages, CancellationToken ct = default)
    {
        using var srcMs = new MemoryStream(sourcePdf);
        using var doc = PdfReader.Open(srcMs, PdfDocumentOpenMode.Modify);

        // Overlay signature fields onto their pages.
        foreach (var field in req.Fields.OrderBy(f => f.Page).ThenBy(f => f.Anchor))
        {
            if (field.Page < 1 || field.Page > doc.PageCount) continue;
            var page = doc.Pages[field.Page - 1];
            using var gfx = XGraphics.FromPdfPage(page);
            var (x, y, w, h) = AnchorRect(page, field.Anchor);
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

        // Append audit page.
        var audit = doc.AddPage();
        audit.Size = PdfSharpCore.PageSize.A4;
        using (var g = XGraphics.FromPdfPage(audit))
        {
            var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
            var bodyFont = new XFont("Arial", 10, XFontStyle.Regular);
            var muted = new XFont("Arial", 8, XFontStyle.Regular);
            var y = 60.0;
            g.DrawString("Signatur-Bericht — NimShare", titleFont, XBrushes.Black, new XPoint(50, y));
            y += 30;
            g.DrawString($"Dokument: {req.Title}", bodyFont, XBrushes.Black, new XPoint(50, y)); y += 16;
            g.DrawString($"Anforderung-ID: {req.Id}", muted, XBrushes.Gray, new XPoint(50, y)); y += 14;
            g.DrawString($"Angefordert von: {req.Initiator?.DisplayName} ({req.Initiator?.Email})", bodyFont, XBrushes.Black, new XPoint(50, y)); y += 14;
            g.DrawString($"Erstellt: {req.CreatedAt:yyyy-MM-dd HH:mm 'UTC'}", muted, XBrushes.Gray, new XPoint(50, y)); y += 14;
            g.DrawString($"Abgeschlossen: {(req.CompletedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "—")}", muted, XBrushes.Gray, new XPoint(50, y)); y += 24;

            g.DrawString("Teilnehmer:innen", titleFont, XBrushes.Black, new XPoint(50, y)); y += 24;
            foreach (var p in req.Participants.OrderBy(x => x.Order))
            {
                var statusStr = p.Status switch
                {
                    SignatureParticipantStatus.Signed => "unterschrieben",
                    SignatureParticipantStatus.Viewed => "gelesen",
                    SignatureParticipantStatus.Declined => "abgelehnt",
                    _ => "ausstehend",
                };
                var roleStr = p.Role == SignatureParticipantRole.Signer ? "Unterzeichner" : "Leser";
                g.DrawString($"• {p.Name} <{p.Email}> — {roleStr}, {statusStr}",
                    bodyFont, XBrushes.Black, new XPoint(50, y));
                y += 14;
                if (p.SignedAt is DateTimeOffset s)
                {
                    g.DrawString($"    Am {s:yyyy-MM-dd HH:mm 'UTC'} • IP-Hash {p.IpHash ?? "—"}",
                        muted, XBrushes.Gray, new XPoint(50, y));
                    y += 12;
                }
            }
            y += 16;
            g.DrawString("Dieser Bericht wurde von NimShare erzeugt. Die Prüfsumme des Ausgangs-PDF ist Teil des Blob-Storage-Audit-Log.",
                muted, XBrushes.Gray, new XRect(50, y, audit.Width - 100, 40),
                XStringFormats.TopLeft);
        }

        using var outMs = new MemoryStream();
        doc.Save(outMs, false);
        return Task.FromResult(outMs.ToArray());
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
