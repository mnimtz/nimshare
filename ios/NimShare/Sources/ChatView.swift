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
                            HStack { ProgressView(); Text("Thinking…").font(.footnote).foregroundStyle(.secondary) }
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(.horizontal)
                        }
                    }
                    .padding(.vertical)
                }
                .onChange(of: messages.count) {
                    if let last = messages.last { withAnimation { proxy.scrollTo(last.id, anchor: .bottom) } }
                }
            }

            if let e = error {
                Text(e).font(.footnote).foregroundStyle(Theme.warnRed).padding(.horizontal)
            }

            HStack(spacing: 8) {
                TextField("Ask about your files…", text: $input, axis: .vertical)
                    .textFieldStyle(.plain)
                    .lineLimit(1...4)
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 12).fill(Theme.cardBackground))
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
        .navigationTitle("AI Chat")
        .sheet(item: $previewFileItem) { f in
            NavigationStack { FilePreviewView(file: f) }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: "sparkles").font(.largeTitle).foregroundStyle(Theme.tungstenBlue)
            Text("Chat with your files").font(.title3.weight(.semibold))
            Text("Ask a question and the assistant will search your files and cite the sources it used.")
                .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center)
                .padding(.horizontal, 40)
        }
    }

    private func bubble(_ m: ChatMessage) -> some View {
        VStack(alignment: m.role == .user ? .trailing : .leading, spacing: 6) {
            Text(m.text)
                .padding(10)
                .background(m.role == .user ? Theme.tungstenBlue : Theme.cardBackground)
                .foregroundStyle(m.role == .user ? .white : .primary)
                .clipShape(RoundedRectangle(cornerRadius: 14))
                .frame(maxWidth: 320, alignment: m.role == .user ? .trailing : .leading)
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
