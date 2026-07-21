import Foundation
import UIKit

/// v1.10.79: Zentraler Helper für Temp-Files + iPad-safe Share-Sheet.
///
/// Zwei Bugs die wir damit gleichzeitig lösen:
/// 1) **Filename-Collision** — vorher wurde direkt in temporaryDirectory/{name}
///    geschrieben. Zwei Files mit gleichem Namen (z.B. „scan.pdf") aus
///    verschiedenen Ordnern haben sich gegenseitig überschrieben. Wenn
///    zwei Previews parallel liefen, gab's Race-Conditions und QuickLook
///    zeigte den falschen Inhalt. Jetzt landet jedes File in einem eigenen
///    UUID-Unterordner, der Original-Filename bleibt erhalten (wichtig für
///    QuickLook-Renderer-Erkennung + „Speichern in Dateien"-Vorschlag).
/// 2) **iPad-Crash** — UIActivityViewController als popover braucht auf
///    iPad zwingend eine sourceView oder sourceItem, sonst crasht der
///    Present-Call mit „Your application has presented a UIAlertController
///    of style UIAlertControllerStyleActionSheet". Der Helper hängt den
///    Popover an das aktive Window an.
enum TmpFile {
    /// Erstellt eine kollisionsfreie Ziel-URL für ein Temp-File mit dem
    /// gewünschten Dateinamen (Original-Extension bleibt erhalten).
    static func destinationURL(for filename: String) -> URL {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("nimshare-tmp/\(UUID().uuidString)", isDirectory: true)
        // Verzeichnis vorbereiten — wir ignorieren „exists"-Fehler; das
        // UUID ist neu, kann eigentlich nicht kollidieren.
        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        return base.appendingPathComponent(filename.isEmpty ? "file" : filename)
    }

    /// iPad-safe Share-Sheet. Auf iPhone verhält es sich wie üblich modal,
    /// auf iPad wird der Popover an die Mitte des KeyWindow gehängt.
    /// MUSS auf dem MainActor aufgerufen werden.
    @MainActor
    static func presentShareSheet(for items: [Any]) {
        let av = UIActivityViewController(activityItems: items, applicationActivities: nil)
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        // Bevorzugt das aktive foreground-Window, fällt zurück auf erstes.
        let window = scenes.first(where: { $0.activationState == .foregroundActive })?.keyWindow
            ?? scenes.first?.keyWindow
            ?? scenes.first?.windows.first
        guard let root = window?.rootViewController else { return }
        // Wenn schon was presentiert wird, hänge das Share-Sheet an das
        // oberste — sonst schluckt SwiftUI es kommentarlos.
        var top = root
        while let presented = top.presentedViewController { top = presented }
        if let pop = av.popoverPresentationController {
            pop.sourceView = top.view
            pop.sourceRect = CGRect(x: top.view.bounds.midX, y: top.view.bounds.midY, width: 0, height: 0)
            pop.permittedArrowDirections = []
        }
        top.present(av, animated: true)
    }
}
