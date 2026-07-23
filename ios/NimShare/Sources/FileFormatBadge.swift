import SwiftUI

/// v1.10.143 — Präzises, skalierbares Datei-Format-Icon (Parität zum Web):
/// ein weißes „Dokument" mit farbigem Label, das das echte Format zeigt
/// (PDF, DOCX, PNG …). Keine Marken-Logos (rechtlich sauber), rein vektoriell —
/// skaliert über `size` knackscharf für jede Grösse.
struct FileFormatBadge: View {
    let name: String
    var size: CGFloat = 28

    var body: some View {
        let info = FileFormatInfo.of(name)
        let r = size * 0.16
        ZStack {
            RoundedRectangle(cornerRadius: r)
                .fill(Color.white)
                .overlay(
                    RoundedRectangle(cornerRadius: r)
                        .stroke(info.color, lineWidth: max(1, size * 0.05))
                )
            VStack(spacing: 0) {
                Spacer(minLength: 0)
                Text(info.label)
                    .font(.system(size: size * 0.27, weight: .heavy))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                    .minimumScaleFactor(0.5)
                    .padding(.horizontal, size * 0.08)
                    .frame(maxWidth: .infinity)
                    .frame(height: size * 0.42)
                    .background(info.color)
            }
            .clipShape(RoundedRectangle(cornerRadius: r))
        }
        // Dokument-Seitenverhältnis: etwas schmaler als hoch.
        .frame(width: size * 0.82, height: size)
        .accessibilityLabel(Text(info.label))
    }
}

/// Farbe + Format-Label pro Dateiendung — spiegelt die Web-Logik (FileIconInfo)
/// für konsistentes Aussehen auf beiden Plattformen.
enum FileFormatInfo {
    static func of(_ name: String) -> (color: Color, label: String) {
        let ext = (name as NSString).pathExtension.lowercased()
        func u(_ s: String) -> String { s.uppercased() }
        switch ext {
        case "pdf": return (Color(hex: 0xDC2626), "PDF")
        case "doc", "docx", "odt", "rtf", "pages": return (Color(hex: 0x2563EB), "DOC")
        case "xls", "xlsx", "ods", "numbers": return (Color(hex: 0x059669), "XLS")
        case "csv": return (Color(hex: 0x059669), "CSV")
        case "ppt", "pptx", "odp", "key": return (Color(hex: 0xEA580C), "PPT")
        case "png", "jpg", "jpeg", "gif", "webp", "svg", "heic", "bmp", "tiff", "avif":
            return (Color(hex: 0xDB2777), u(ext == "jpeg" ? "jpg" : ext))
        case "mp4", "mov", "avi", "mkv", "webm", "m4v": return (Color(hex: 0x7C3AED), u(ext))
        case "mp3", "wav", "m4a", "aac", "flac", "ogg", "wma": return (Color(hex: 0x0891B2), u(ext))
        case "zip", "7z", "rar", "tar", "gz", "bz2": return (Color(hex: 0x64748B), u(ext))
        case "txt", "md", "log": return (Color(hex: 0x6B7280), u(ext))
        case "json", "xml", "yml", "yaml", "html", "htm", "css", "js", "ts",
             "cs", "py", "java", "sql", "sh", "swift", "go", "rb", "php":
            return (Color(hex: 0x4F46E5), u(ext))
        case "": return (Color(hex: 0x94A3B8), "FILE")
        default: return (Color(hex: 0x94A3B8), u(ext.count <= 4 ? ext : String(ext.prefix(4))))
        }
    }
}

extension Color {
    /// Init aus 0xRRGGBB.
    init(hex: UInt32) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: 1
        )
    }
}
