import SwiftUI

struct ChatMessage: Identifiable {
    enum Role { case user, assistant }
    let id = UUID()
    let role: Role
    let text: String
    let citations: [SearchHitDto]
}

struct ChatView: View {
    @EnvironmentObject var auth: AuthStore
    @State private var messages: [ChatMessage] = []
    @State private var input = ""
    @State private var busy = false
    @State private var error: String?
    @State private var previewFileItem: FileItem?
    // v1.10.66: expliziter FocusState damit "Fertig"-Toolbar-Button
    // die Tastatur zuverlässig einklappt. Marcus's Bug: aus dem Chat
    // kam man nur raus indem man die App neu startet — die Tastatur
    // verdeckte die Tab-Bar und es gab keinen Escape.
    @FocusState private var inputFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(spacing: 12) {
                        if messages.isEmpty {
                            emptyState.padding(.top, 60)
                        }
                        ForEach(messages) { m in
                            bubble(m).id(m.id)
                        }
                        if busy {
                            HStack { ProgressView(); Text("Denke…").font(.footnote).foregroundStyle(.secondary) }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.horizontal)
                        }
                    }
                    .padding(.vertical)
                }
                .scrollDismissesKeyboard(.interactively)
                .onChange(of: messages.count) {
                    if let last = messages.last { withAnimation { proxy.scrollTo(last.id, anchor: .bottom) } }
                }
                // Tap anywhere im Scroll-Bereich → Tastatur weg.
                .simultaneousGesture(TapGesture().onEnded { inputFocused = false })
            }

            if let e = error {
                // v1.10.97: Server-Text unverändert durchreichen — der ist seit
                // v1.10.93 rollenabhängig (Admin sieht Reindex-Anleitung, User
                // sieht „Admin muss indexieren"). Vorher hat iOS die Meldung
                // mit eigenem, admin-orientiertem Text überschrieben — dadurch
                // sah jeder User „bitte im Web unter Einstellungen indexieren"
                // auch als Nicht-Admin, obwohl er dort gar keinen Zugriff hat.
                let looksLikeNoIndex = e.contains("indexier") || e.contains("indexed") || e.contains("bereit")
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: looksLikeNoIndex ? "info.circle" : "exclamationmark.triangle")
                        .foregroundStyle(looksLikeNoIndex ? Theme.tungstenBlue : Theme.warnRed)
                    Text(e)
                        .font(.footnote)
                        .foregroundStyle(.primary)
                }
                .padding(10)
                .background((looksLikeNoIndex ? Theme.tungstenBlue : Theme.warnRed).opacity(0.08))
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .padding(.horizontal)
            }

            HStack(spacing: 8) {
                TextField("Frage zu deinen Dateien…", text: $input, axis: .vertical)
                    .textFieldStyle(.plain)
                    .lineLimit(1...4)
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 12).fill(Theme.cardBackground))
                    .focused($inputFocused)
                Button {
                    Task { await send() }
                } label: {
                    Image(systemName: "paperplane.fill")
                        .foregroundStyle(.white)
                        .frame(width: 42, height: 42)
                        .background(Theme.tungstenBlue)
                        .clipShape(Circle())
                }.disabled(input.trimmingCharacters(in: .whitespaces).isEmpty || busy)
            }
            .padding(.horizontal).padding(.bottom, 8)
        }
        .navigationTitle("KI-Chat")
        .toolbar {
            // System-Tastatur-Toolbar: "Fertig"-Button klappt Tastatur ein
            // → Tab-Bar wird wieder sichtbar → User kommt aus dem Chat raus.
            ToolbarItem(placement: .keyboard) {
                HStack {
                    Spacer()
                    Button("Fertig") { inputFocused = false }
                }
            }
        }
        .sheet(item: $previewFileItem) { f in
            NavigationStack { FilePreviewView(file: f) }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: "sparkles").font(.largeTitle).foregroundStyle(Theme.tungstenBlue)
            Text("Chatte mit deinen Dateien").font(.title3.weight(.semibold))
            Text("Stelle eine Frage — der Assistent durchsucht deine Dateien und zeigt die verwendeten Quellen.")
                .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                .padding(.horizontal, 40)
        }
    }

    @Environment(\.horizontalSizeClass) private var hSize

    private func bubble(_ m: ChatMessage) -> some View {
        // v1.10.151: Bubble-Breite reagiert auf horizontalSizeClass — vorher
        // hart 320pt → auf iPad-Landscape (1366pt Fensterbreite) blieb der
        // Chat als schmaler Streifen mit riesigen Rändern. Regular-Klasse
        // (iPad, landscape iPhone Pro Max) → 640pt max.
        let bubbleMax: CGFloat = hSize == .regular ? 640 : 320
        return VStack(alignment: m.role == .user ? .trailing : .leading, spacing: 6) {
            Text(m.text)
                .padding(10)
                .background(m.role == .user ? Theme.tungstenBlue : Theme.cardBackground)
                .foregroundStyle(m.role == .user ? .white : .primary)
                .clipShape(RoundedRectangle(cornerRadius: 14))
                .frame(maxWidth: bubbleMax, alignment: m.role == .user ? .trailing : .leading)
            if !m.citations.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(Array(m.citations.enumerated()), id: \.element.id) { idx, c in
                        Button {
                            previewFileItem = FileItem(id: c.id, name: c.name, sizeBytes: 0,
                                                       contentType: "application/octet-stream",
                                                       createdAt: Date(), ownerName: nil,
                                                       aiTags: nil, aiRiskFlag: nil)
                        } label: {
                            HStack(spacing: 6) {
                                Text("[\(idx + 1)]").font(.caption2.monospaced()).foregroundStyle(.secondary)
                                Text(c.name).font(.caption).foregroundStyle(Theme.tungstenBlue).lineLimit(1)
                            }
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: m.role == .user ? .trailing : .leading)
        .padding(.horizontal)
    }

    private func send() async {
        guard let api = auth.api else { return }
        let q = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if q.isEmpty { return }
        input = ""
        messages.append(.init(role: .user, text: q, citations: []))
        busy = true; error = nil
        defer { busy = false }
        do {
            let resp = try await api.chatAsk(question: q)
            messages.append(.init(role: .assistant, text: resp.answer, citations: resp.citations))
        } catch let e as ApiError {
            error = e.localizedDescription
        } catch let ex { error = ex.localizedDescription }
    }
}
