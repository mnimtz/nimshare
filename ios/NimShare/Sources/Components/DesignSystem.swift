import SwiftUI

// MARK: - Redesign Design System (v2, pilot: Home only)
//
// Additive to the existing `Theme` enum (Theme.swift) — nothing already in
// use elsewhere (Theme.tungstenBlue, .tungstenDark, .cardBackground,
// .warnRed, .aiBlueTintBg, Color.hashTint) is touched, so every other
// screen keeps its exact current look. These new tokens are what the
// redesigned screens (starting with Home) build on. Reuses the existing
// `Color(hex:)` initializer from FileFormatBadge.swift instead of
// redeclaring it.
//
// Source: nimshare-handoff design brief, "playful/soft" direction (Turn 1 ·
// Option 1a). Fonts: Red Hat Display / Red Hat Mono, registered as variable
// fonts under Resources/Fonts + Info.plist UIAppFonts. If the fonts fail to
// load for any reason, `Font.custom` transparently falls back to the system
// font — nothing crashes, it just looks like SF Pro until fixed.

extension Theme {
    // MARK: Colors — semantic (adapt to color scheme automatically)

    static let bgTop = Color.dynamic(light: Color(hex: 0xF5F1EA), dark: Color(hex: 0x0B1220))
    static let bgBottom = Color.dynamic(light: Color(hex: 0xEEE7DB), dark: Color(hex: 0x050912))
    static let surface2 = Color.dynamic(light: Color(hex: 0xFFFFFF), dark: Color(hex: 0x0F1B2D))
    static let surfaceMuted = Color.dynamic(light: Color(hex: 0xF5F6F8), dark: Color.white.opacity(0.06))
    static let border2 = Color.dynamic(light: Color(hex: 0x002854).opacity(0.08), dark: Color.white.opacity(0.08))

    static let textPrimary = Color.dynamic(light: Color(hex: 0x0F1B2D), dark: Color(hex: 0xF5F1EA))
    static let textSecondary = Color.dynamic(light: Color(hex: 0x0F1B2D).opacity(0.55), dark: Color(hex: 0xF5F1EA).opacity(0.55))
    static let textTertiary = Color.dynamic(light: Color(hex: 0x0F1B2D).opacity(0.4), dark: Color(hex: 0xF5F1EA).opacity(0.4))

    // MARK: Colors — brand (redesign palette)

    static let navy = Color(hex: 0x002854)
    static let navyDark2 = Color(hex: 0x001938)
    /// v2.0.1: `navy` itself stays fixed (used for solid fills — buttons,
    /// gradients, chat bubbles — where contrast comes from the fill, not
    /// from being read as icon/text foreground). Everywhere `navy` was used
    /// as `.foregroundStyle`/icon tint on top of the app's own adaptive
    /// background, it went near-invisible in dark mode (dark navy on
    /// near-black). `navyFg` is the adaptive variant for that case.
    static let navyFg = Color.dynamic(light: Color(hex: 0x002854), dark: Color(hex: 0x7DD3FC))
    static let cyan = Color(hex: 0x00A0FB)
    static let cyanBright = Color(hex: 0x7DD3FC)
    static let yellow = Color(hex: 0xFFC600)
    static let yellowDeep = Color(hex: 0xF59E0B)

    // MARK: Colors — status

    static let success2 = Color(hex: 0x22C55E)
    static let warn2 = Color(hex: 0xEA580C)
    static let danger2 = Color(hex: 0xDC2626)

    // MARK: Gradients

    static let bgGradient = LinearGradient(colors: [bgTop, bgBottom], startPoint: .top, endPoint: .bottom)
    static let heroGradient = LinearGradient(colors: [navy, Color(hex: 0x003E7E), Color(hex: 0x0055AB)], startPoint: .topLeading, endPoint: .bottomTrailing)

    // MARK: Spacing scale (pt)
    enum Space {
        static let xxs: CGFloat = 2
        static let xs: CGFloat = 4
        static let s: CGFloat = 8
        static let sm: CGFloat = 10
        static let md: CGFloat = 12
        static let lg: CGFloat = 16
        static let xl: CGFloat = 20
        static let xxl: CGFloat = 24
        static let xxxl: CGFloat = 32
    }

    // MARK: Corner radii
    enum Radius2 {
        static let chip: CGFloat = 20
        static let card: CGFloat = 14
        static let cardLarge: CGFloat = 22
        static let hero: CGFloat = 26
    }
}

// MARK: - Color helpers

extension Color {
    /// Dynamic color — picks between light + dark variant based on the
    /// current trait environment (works outside SwiftUI's `.foregroundStyle`
    /// call site too, unlike `@Environment(\.colorScheme)`).
    static func dynamic(light: Color, dark: Color) -> Color {
        Color(UIColor { trait in
            trait.userInterfaceStyle == .dark ? UIColor(dark) : UIColor(light)
        })
    }
}

// MARK: - Typography

enum TFont {
    /// Registered family names — see Resources/Fonts + Info.plist UIAppFonts.
    private static let display = "Red Hat Display"
    private static let mono = "Red Hat Mono"

    static let titleXL = Font.custom(display, size: 28).weight(.heavy)
    static let titleL = Font.custom(display, size: 22).weight(.heavy)
    static let titleM = Font.custom(display, size: 18).weight(.heavy)
    static let titleS = Font.custom(display, size: 15).weight(.bold)
    static let bodyL = Font.custom(display, size: 14).weight(.medium)
    static let bodyM = Font.custom(display, size: 13).weight(.medium)
    static let bodyS = Font.custom(display, size: 12).weight(.medium)
    static let caption = Font.custom(display, size: 11).weight(.medium)
    static let micro = Font.custom(display, size: 10).weight(.bold)
    static let mono12 = Font.custom(mono, size: 12).weight(.semibold)
}

// MARK: - Card modifier

extension View {
    /// Standard redesign card treatment: surface bg + hairline border +
    /// rounded corners + soft shadow.
    func rsCard(radius: CGFloat = Theme.Radius2.card, padding: CGFloat = 14) -> some View {
        self
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: radius, style: .continuous).fill(Theme.surface2))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).stroke(Theme.border2, lineWidth: 1))
            .shadow(color: .black.opacity(0.05), radius: 8, x: 0, y: 4)
    }
}

// MARK: - Section header

struct RSSectionHeader: View {
    let title: String
    var trailing: String? = nil
    var trailingAction: (() -> Void)? = nil
    var body: some View {
        HStack {
            Text(title.uppercased())
                .font(TFont.micro)
                .kerning(0.8)
                .foregroundStyle(Theme.textSecondary)
            Spacer()
            if let t = trailing {
                Button(action: { trailingAction?() }) {
                    Text(t).font(TFont.bodyS.weight(.semibold)).foregroundStyle(Theme.cyan)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, Theme.Space.xl)
        .padding(.top, Theme.Space.lg)
        .padding(.bottom, Theme.Space.s)
    }
}

// MARK: - Weather chip (real data — NimShareAPI.WeatherInfo)

struct RSWeatherChip: View {
    let weather: NimShareAPI.WeatherInfo
    var body: some View {
        HStack(spacing: 8) {
            // v1.11.71: Marcus's Report "Wetter-Logo nicht erkennbar" — die
            // Multicolor-Wettersymbole (z.B. cloud.fill) sind bei bewölktem
            // Wetter fast weiß/hellgrau und verschwanden auf dem hellen
            // Chip-Hintergrund. Getönter Kreis dahinter sorgt für Kontrast
            // unabhängig vom Wettercode.
            ZStack {
                Circle().fill(Theme.cyan.opacity(0.14)).frame(width: 32, height: 32)
                Image(systemName: weather.sfSymbol)
                    .symbolRenderingMode(.multicolor)
                    .font(.system(size: 17))
            }
            VStack(alignment: .leading, spacing: 1) {
                Text("\(weather.tempC)°").font(TFont.titleS).foregroundStyle(Theme.textPrimary)
                Text("↑\(weather.highC) ↓\(weather.lowC)").font(TFont.caption).foregroundStyle(Theme.textSecondary)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 18).fill(Theme.surface2))
        .overlay(RoundedRectangle(cornerRadius: 18).stroke(Theme.border2, lineWidth: 1))
        .shadow(color: .black.opacity(0.06), radius: 8, x: 0, y: 4)
    }
}

// MARK: - Empty state (SF-Symbol illustration + title + desc + optional CTA)

struct RSEmptyState: View {
    let systemImage: String
    let title: String
    let desc: String
    var ctaLabel: String? = nil
    var onCTA: (() -> Void)? = nil

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: systemImage)
                .font(.system(size: 68))
                .foregroundStyle(Theme.yellow.gradient)
                .padding(.bottom, 2)
            Text(title).font(TFont.titleM).foregroundStyle(Theme.textPrimary)
            Text(desc).font(TFont.bodyS).foregroundStyle(Theme.textSecondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 260)
            if let label = ctaLabel {
                Button(label) { onCTA?() }
                    .font(TFont.bodyS.weight(.heavy))
                    .padding(.horizontal, 18).padding(.vertical, 10)
                    .foregroundStyle(.white)
                    .background(RoundedRectangle(cornerRadius: 12).fill(Theme.navy))
                    .shadow(color: Theme.navy.opacity(0.35), radius: 14, y: 8)
            }
        }
        .padding(.horizontal, Theme.Space.xxl).padding(.top, 40).padding(.bottom, 60)
        .frame(maxWidth: .infinity)
    }
}
