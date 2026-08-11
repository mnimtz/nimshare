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
        // v1.11.82 (Security-Review): Datenschutzklasse für (potenziell sensible) Downloads
        // — lesbar solange offen (QuickLook/AVPlayer), sonst bei gesperrtem Gerät geschützt.
        // Best-effort; das tmp-Verzeichnis wird von iOS ohnehin nicht ins Backup übernommen.
        try? FileManager.default.setAttributes(
            [.protectionKey: FileProtectionType.completeUnlessOpen], ofItemAtPath: base.path)
        return base.appendingPathComponent(Self.sanitizedFilename(filename))
    }

    /// v2.0.7 (Audit): `FileManager.moveItem` übernimmt die Schutzklasse der
    /// QUELLE (URLSession-Download-tmp = completeUntilFirstUserAuthentication),
    /// NICHT die des Zielverzeichnisses — die Klasse aus destinationURL(for:)
    /// griff für gemovte Downloads also nie. Nach jedem Move explizit setzen.
    static func applyProtection(at url: URL) {
        try? FileManager.default.setAttributes(
            [.protectionKey: FileProtectionType.completeUnlessOpen], ofItemAtPath: url.path)
    }

    private static var tmpRoot: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("nimshare-tmp", isDirectory: true)
    }

    /// v2.0.7 (Audit): Downloads räumen sich nie selbst auf — bis iOS tmp purged
    /// (Tage!) liegen Previews/ZIPs entschlüsselbar herum. Beim App-Start alles
    /// löschen, was älter als einen Tag ist (Datums-basiert, deckt sich mit der
    /// in PrivacyInfo.xcprivacy deklarierten Begründung "Cleanup nach Datum").
    static func cleanupSweep(olderThan age: TimeInterval = 86_400) {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(
            at: tmpRoot, includingPropertiesForKeys: [.creationDateKey]) else { return }
        let cutoff = Date().addingTimeInterval(-age)
        for entry in entries {
            let created = (try? entry.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? .distantPast
            if created < cutoff { try? fm.removeItem(at: entry) }
        }
    }

    /// v2.0.7 (Audit): beim Abmelden ALLE Temp-Downloads entfernen — vertrauliche
    /// Dateien dürfen einen Logout nicht auf der Platte überleben.
    static func cleanupAll() {
        try? FileManager.default.removeItem(at: tmpRoot)
    }

    /// v1.11.82 (Security-Review): server-gelieferter Dateiname darf keine Pfad-Separatoren
    /// oder „..“ einschleusen. Nur der letzte Pfad-Bestandteil, Separatoren entschärft.
    /// (Der Schreibpfad bleibt so oder so im App-Sandbox-tmp, das ist Defense-in-Depth.)
    private static func sanitizedFilename(_ name: String) -> String {
        var n = (name as NSString).lastPathComponent
        n = n.replacingOccurrences(of: "/", with: "_")
             .replacingOccurrences(of: "\\", with: "_")
        n = n.trimmingCharacters(in: CharacterSet(charactersIn: " ."))
        return n.isEmpty ? "file" : n
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
