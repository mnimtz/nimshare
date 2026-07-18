import SwiftUI

enum Theme {
    /// Tungsten Automation primary blue — matches the web app.
    static let tungstenBlue = Color(red: 0.031, green: 0.353, blue: 0.612)
    static let tungstenDark = Color(red: 0.078, green: 0.184, blue: 0.310)
    static let softBackground = Color(.systemGroupedBackground)
    static let cardBackground = Color(.secondarySystemGroupedBackground)
    static let warnRed = Color(red: 0.78, green: 0.20, blue: 0.20)
    static let aiBlueTintBg = Color(red: 0.90, green: 0.94, blue: 0.99)
}

extension Color {
    /// Deterministic tint per string (used for group / avatar badges).
    static func hashTint(_ s: String) -> Color {
        var h: UInt64 = 5381
        for b in s.utf8 { h = ((h << 5) &+ h) &+ UInt64(b) }
        let hue = Double(h % 360) / 360.0
        return Color(hue: hue, saturation: 0.45, brightness: 0.82)
    }
}
