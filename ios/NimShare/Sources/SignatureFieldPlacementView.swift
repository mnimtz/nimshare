import SwiftUI
import PDFKit

/// v1.11.18: Visuelles Feld-Placement für Signaturen — iOS-Parität zum
/// Web-Wizard (pdf.js-Drag-Overlay in Views/SignaturesPage/NewRequest.cshtml,
/// siehe `setupDrag()`). Vorher konnte iOS nur einen festen Anchor-Preset
/// ("Seite 1, unten Mitte") senden — der Ersteller hatte keine Möglichkeit
/// zu sehen oder zu bestimmen, wo die Unterschrift landet. Marcus's Report:
/// "kann dort keine felder plazieren, frag mich wo die unterschrift dann
/// erscheinen soll..auch nicht 1:1 nahezu umgesetzt worden".
///
/// Rendert jede PDF-Seite als Bild bei bekannter Punkt-Skalierung (PDF
/// Media-Box in 72dpi-Punkten) und rechnet Drag-Gesten direkt in dieses
/// System um — TOP-LEFT-Y-runter, exakt wie der Server (SignaturePdfService/
/// XGraphics) es erwartet (siehe Kommentar in NewRequest.cshtml `end()`:
/// "der Server nutzt durchgängig Top-Left-Y-runter-Koordinaten — NICHT die
/// native PDF-Konvention"), also keine zusätzliche Flip-Logik nötig.
struct SignatureFieldPlacementView: View {
    @EnvironmentObject var auth: AuthStore
    let requestId: UUID
    let sourceFileId: UUID
    let participants: [NewSignatureRequestSheet.Participant]

    @State private var pdf: PDFDocument?
    @State private var loading = true
    @State private var error: String?
    @State private var currentPage = 0

    @State private var selectedParticipantId: UUID?
    @State private var selectedType = "Signature"
    // v1.11.42 — Marcus: "kann ich keine Felder ziehen, die Ansicht der PDF
    // ist so klein, das man garnichts sehen kann. auch kein zoom möglich."
    // Root-Cause: PageCanvas hat vorher IMMER "fit beide Dimensionen"
    // (min(widthRatio,heightRatio)) gerechnet — bei einer sehr hohen Seite
    // (wie AIDA-Reisemagazin-Export) ergab das eine winzige Breite, in der
    // ein Rechteck ziehen praktisch unmöglich war. Jetzt: Fit-Breite (wie
    // jeder normale PDF-Viewer), vertikal scrollbar, plus Pinch-Zoom. Ein
    // Moden-Umschalter trennt "Platzieren" (1-Finger zieht ein Feld) von
    // "Verschieben" (1-Finger scrollt) — beide Modi erlauben Pinch-Zoom.
    @State private var isPlaceMode = true

    struct PlacedField: Identifiable {
        let id: UUID
        let participantId: UUID
        let type: String
        let page: Int          // 1-indexed, wie Server
        // PDF-Punkte, Top-Left-Origin, Y runter (siehe Type-Doku oben).
        let x: Double, y: Double, width: Double, height: Double
    }
    @State private var fields: [PlacedField] = []

    private var signers: [NewSignatureRequestSheet.Participant] {
        participants.filter { $0.role == "Signer" }
    }

    var body: some View {
        VStack(spacing: 0) {
            if !signers.isEmpty {
                HStack {
                    Menu {
                        ForEach(signers) { p in
                            Button(p.name) { selectedParticipantId = p.serverId }
                        }
                    } label: {
                        Label(signers.first(where: { $0.serverId == selectedParticipantId })?.name ?? "Empfänger wählen",
                              systemImage: "person.crop.circle")
                    }
                    .tint(Theme.navy)
                    Spacer()
                    Menu {
                        Button("✍ Unterschrift") { selectedType = "Signature" }
                        Button("🔤 Text") { selectedType = "Text" }
                        Button("📅 Datum") { selectedType = "Date" }
                        Button("☑ Checkbox") { selectedType = "Checkbox" }
                    } label: {
                        Label(typeLabel(selectedType), systemImage: "square.dashed")
                    }
                    .tint(Theme.cyan)
                }
                .font(TFont.bodyM)
                .padding(.horizontal)
                .padding(.top, 8)
                .padding(.bottom, 4)
            }

            if pdf != nil {
                Picker("Modus", selection: $isPlaceMode) {
                    Label("Platzieren", systemImage: "plus.square.dashed").tag(true)
                    Label("Verschieben/Zoom", systemImage: "arrow.up.left.and.arrow.down.right").tag(false)
                }
                .pickerStyle(.segmented)
                .padding(.horizontal)
                .padding(.bottom, 6)
            }

            if loading {
                ProgressView("PDF wird geladen…").frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let e = error {
                VStack(spacing: 12) {
                    Image(systemName: "exclamationmark.triangle").font(.largeTitle).foregroundStyle(Theme.warnRed)
                    Text(e).multilineTextAlignment(.center).padding(.horizontal)
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let pdf, pdf.pageCount > 0 {
                TabView(selection: $currentPage) {
                    ForEach(0..<pdf.pageCount, id: \.self) { i in
                        if let page = pdf.page(at: i) {
                            PageCanvas(
                                page: page,
                                fields: fields.filter { $0.page == i + 1 },
                                isPlaceMode: isPlaceMode,
                                onDraw: { rect in await addField(pageIndex: i, rect: rect) },
                                onDelete: { field in await deleteField(field) }
                            )
                            .tag(i)
                        }
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: pdf.pageCount > 1 ? .always : .never))
                Text(pdf.pageCount > 1
                     ? "Seite \(currentPage + 1) von \(pdf.pageCount) · Ziehe ein Rechteck, um ein Feld zu platzieren"
                     : "Ziehe ein Rechteck auf das Dokument, um ein Feld zu platzieren")
                    .font(TFont.caption).foregroundStyle(Theme.textSecondary)
                    .padding(.vertical, 6)
            } else {
                RSEmptyState(systemImage: "doc.questionmark", title: "PDF leer", desc: "")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .task { await load() }
        .onAppear {
            if selectedParticipantId == nil { selectedParticipantId = signers.first?.serverId }
        }
    }

    private func typeLabel(_ t: String) -> String {
        switch t {
        case "Text": return "🔤 Text"
        case "Date": return "📅 Datum"
        case "Checkbox": return "☑ Checkbox"
        default: return "✍ Unterschrift"
        }
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; error = nil
        defer { loading = false }
        do {
            let resp = try await api.previewUrl(fileId: sourceFileId)
            guard let url = URL(string: resp.url) else { throw ApiError.network("Bad SAS URL") }
            let (tmp, _) = try await URLSession.shared.download(from: url)
            let dest = TmpFile.destinationURL(for: "signature-doc.pdf")
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: tmp, to: dest)
            guard let doc = PDFDocument(url: dest) else {
                error = "PDF konnte nicht geladen werden."
                return
            }
            pdf = doc
        } catch is CancellationError { /* Pull-Refresh/Task-Cancel — kein Fehler */ }
        catch let ex { error = ex.localizedDescription }
    }

    private func addField(pageIndex: Int, rect: CGRect) async {
        guard let api = auth.api, let pid = selectedParticipantId else { return }
        do {
            let fieldId = try await api.addSignatureField(
                requestId, participantId: pid, type: selectedType,
                page: pageIndex + 1, anchor: "BottomCenter",
                x: rect.minX, y: rect.minY, width: rect.width, height: rect.height)
            fields.append(PlacedField(id: fieldId, participantId: pid, type: selectedType,
                                       page: pageIndex + 1, x: rect.minX, y: rect.minY,
                                       width: rect.width, height: rect.height))
        } catch { /* Feld bleibt einfach ungesetzt, User kann erneut ziehen */ }
    }

    private func deleteField(_ field: PlacedField) async {
        guard let api = auth.api else { return }
        do {
            try await api.removeSignatureField(requestId, fieldId: field.id)
            fields.removeAll { $0.id == field.id }
        } catch { /* bleibt sichtbar, User kann erneut versuchen */ }
    }
}

/// Rendert eine PDF-Seite + transparentes Drag-Overlay. Rechnet Display-
/// Pixel zurück in PDF-Punkte (Media-Box-Breite/Höhe geteilt durch die
/// tatsächlich gerenderte Größe).
///
/// v1.11.42 — vorher IMMER "fit beide Dimensionen" (min(widthRatio,
/// heightRatio)): bei einer sehr hohen Seite (z.B. ein als-eine-Seite
/// exportiertes Reisemagazin) ergab das eine winzige Breite, in der ein
/// Rechteck ziehen praktisch unmöglich war ("kann ich keine Felder ziehen").
/// Jetzt: Fit-Breite wie jeder normale PDF-Viewer, vertikal/horizontal
/// scrollbar + Pinch-Zoom (bisher gar nicht möglich). isPlaceMode trennt
/// "Platzieren" (1-Finger zieht ein Feld, Scroll aus) von "Verschieben"
/// (1-Finger scrollt) — Pinch-Zoom ist in beiden Modi aktiv (2 Finger,
/// kollidiert nie mit der 1-Finger-Zieh-Geste).
private struct PageCanvas: View {
    let page: PDFPage
    let fields: [SignatureFieldPlacementView.PlacedField]
    let isPlaceMode: Bool
    let onDraw: (CGRect) async -> Void
    let onDelete: (SignatureFieldPlacementView.PlacedField) async -> Void

    @State private var dragStart: CGPoint?
    @State private var dragCurrent: CGPoint?
    @State private var committedZoom: CGFloat = 1
    @GestureState private var pinchDelta: CGFloat = 1
    private let minZoom: CGFloat = 1
    private let maxZoom: CGFloat = 6

    var body: some View {
        GeometryReader { geo in
            let pdfSize = page.bounds(for: .mediaBox).size
            // Fit-Breite statt Fit-beide-Dimensionen — siehe Klassen-Doku.
            let fitScale = pdfSize.width > 0 ? geo.size.width / pdfSize.width : 1
            let zoom = min(max(committedZoom * pinchDelta, minZoom), maxZoom)
            let displayWidth = pdfSize.width * fitScale * zoom
            let displayHeight = pdfSize.height * fitScale * zoom
            let pdfPerDisplayPixel = (fitScale * zoom) > 0 ? 1 / (fitScale * zoom) : 1

            let canvas = ZStack(alignment: .topLeading) {
                if let img = renderedImage(width: displayWidth, height: displayHeight) {
                    Image(uiImage: img).resizable().frame(width: displayWidth, height: displayHeight)
                } else {
                    Color(.secondarySystemBackground).frame(width: displayWidth, height: displayHeight)
                }

                ForEach(fields) { f in
                    let r = CGRect(x: f.x / pdfPerDisplayPixel, y: f.y / pdfPerDisplayPixel,
                                   width: f.width / pdfPerDisplayPixel, height: f.height / pdfPerDisplayPixel)
                    fieldBox(r, kind: f.type)
                        .overlay(alignment: .topTrailing) {
                            Button { Task { await onDelete(f) } } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .symbolRenderingMode(.palette)
                                    .foregroundStyle(.white, Theme.warnRed)
                            }
                            .offset(x: 10, y: -10)
                        }
                        .position(x: r.midX, y: r.midY)
                }

                if let s = dragStart, let c = dragCurrent {
                    let r = CGRect(x: min(s.x, c.x), y: min(s.y, c.y), width: abs(c.x - s.x), height: abs(c.y - s.y))
                    fieldBox(r, kind: "Signature").position(x: r.midX, y: r.midY)
                }
            }
            .frame(width: displayWidth, height: displayHeight)

            let magnify = MagnificationGesture()
                .updating($pinchDelta) { value, state, _ in state = value }
                .onEnded { value in
                    committedZoom = min(max(committedZoom * value, minZoom), maxZoom)
                }

            ScrollView([.horizontal, .vertical], showsIndicators: !isPlaceMode) {
                Group {
                    if isPlaceMode {
                        canvas
                            .contentShape(Rectangle())
                            .gesture(
                                DragGesture(minimumDistance: 8, coordinateSpace: .local)
                                    .onChanged { v in
                                        if dragStart == nil { dragStart = v.startLocation }
                                        dragCurrent = v.location
                                    }
                                    .onEnded { v in
                                        defer { dragStart = nil; dragCurrent = nil }
                                        guard let s = dragStart else { return }
                                        let c = v.location
                                        let dispRect = CGRect(x: min(s.x, c.x), y: min(s.y, c.y),
                                                               width: abs(c.x - s.x), height: abs(c.y - s.y))
                                        guard dispRect.width > 20, dispRect.height > 15 else { return }
                                        let pdfRect = CGRect(
                                            x: dispRect.minX * pdfPerDisplayPixel, y: dispRect.minY * pdfPerDisplayPixel,
                                            width: dispRect.width * pdfPerDisplayPixel, height: dispRect.height * pdfPerDisplayPixel)
                                        Task { await onDraw(pdfRect) }
                                    }
                            )
                    } else {
                        canvas
                    }
                }
                .frame(minWidth: geo.size.width, minHeight: geo.size.height, alignment: .top)
            }
            .scrollDisabled(isPlaceMode)
            .simultaneousGesture(magnify)
        }
    }

    private func fieldBox(_ r: CGRect, kind: String) -> some View {
        RoundedRectangle(cornerRadius: 4)
            .strokeBorder(Theme.cyan, style: StrokeStyle(lineWidth: 2, dash: [5, 3]))
            .background(RoundedRectangle(cornerRadius: 4).fill(Theme.cyan.opacity(0.12)))
            .frame(width: max(r.width, 1), height: max(r.height, 1))
            .overlay(Text(icon(for: kind)).font(.caption))
    }
    private func icon(for kind: String) -> String {
        switch kind {
        case "Text": return "🔤"
        case "Date": return "📅"
        case "Checkbox": return "☑"
        default: return "✍"
        }
    }

    private func renderedImage(width: CGFloat, height: CGFloat) -> UIImage? {
        guard width > 0, height > 0 else { return nil }
        let scale = UIScreen.main.scale
        return page.thumbnail(of: CGSize(width: width * scale, height: height * scale), for: .mediaBox)
    }
}
