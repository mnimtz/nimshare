import SwiftUI

struct SearchView: View {
    @EnvironmentObject var auth: AuthStore
    // v2.0.2: von KIView durchgereicht — Bereichs-Picker (Persönlich/
    // Öffentlich/Gruppe), siehe KIView.swift.
    @Binding var scope: KiScope
    @State private var query = ""
    @State private var results: [SearchHitDto] = []
    @State private var busy = false
    @State private var error: String?
    @State private var hasSearched = false
    @State private var previewFileItem: FileItem?
    // v1.10.165: AI-Consent-Gate (Apple 5.1.1(i))
    @State private var showAiConsent = false
    @State private var pendingQuery: String?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "sparkle.magnifyingglass").foregroundStyle(Theme.cyan)
                TextField("Dateien nach Bedeutung suchen…", text: $query)
                    .font(TFont.bodyM)
                    .textFieldStyle(.plain)
                    .submitLabel(.search)
                    .onSubmit { Task { await run() } }
                if busy { ProgressView() }
                else if !query.isEmpty {
                    Button { query = ""; results = []; hasSearched = false } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(Theme.textTertiary)
                    }
                }
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 10).fill(Theme.surface2))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Theme.border2, lineWidth: 1))
            .padding()

            if let e = error {
                Text(e).font(TFont.bodyS).foregroundStyle(Theme.danger2).padding(.horizontal)
            }

            if results.isEmpty {
                RSEmptyState(
                    systemImage: hasSearched ? "magnifyingglass" : "sparkle.magnifyingglass",
                    title: hasSearched ? "Keine Treffer" : "Semantische Suche",
                    desc: hasSearched
                        ? "Versuch andere Stichworte oder eine längere Formulierung."
                        : #"Frag wie bei einer Suchmaschine — „Budget-Folien Q4" oder „Vertrag Lizenz". Benötigt einen konfigurierten AI-Provider in den Server-Einstellungen."#)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(results) { hit in
                    Button { open(hit) } label: {
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(hit.name).font(TFont.titleS).foregroundStyle(Theme.textPrimary).lineLimit(2)
                                Spacer()
                                Text(Int(hit.score * 100).description + "%")
                                    .font(TFont.mono12)
                                    .foregroundStyle(Theme.textSecondary)
                            }
                            if let s = hit.snippet, !s.isEmpty {
                                Text(s).font(TFont.caption).foregroundStyle(Theme.textSecondary).lineLimit(3)
                            }
                        }.padding(.vertical, 4)
                    }.buttonStyle(.plain)
                    .listRowBackground(Theme.surface2)
                    .listRowSeparator(.hidden)
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
            }
        }
        .background(Theme.bgGradient.ignoresSafeArea())
        .sheet(item: $previewFileItem) { f in
            FileDetailView(file: f)
        }
        // v1.10.165: AI-Consent-Gate (Apple 5.1.1(i))
        .sheet(isPresented: $showAiConsent) {
            AiConsentSheet(onDecided: { granted in
                if granted, let q = pendingQuery {
                    pendingQuery = nil
                    query = q
                    Task { await run() }
                } else {
                    pendingQuery = nil
                }
            })
        }
    }

    private func run() async {
        guard let api = auth.api, !query.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        // v1.10.165: vor semantischer Suche Consent prüfen (Embedding-Provider).
        if !auth.aiReady {
            pendingQuery = query
            showAiConsent = true
            return
        }
        busy = true; error = nil; hasSearched = true
        defer { busy = false }
        do { results = try await api.semanticSearch(query: query, scope: scope.apiValue) }
        catch let e as ApiError {
            // v1.10.171: 403 „ai_consent_required" (Consent auf anderem Gerät
            // widerrufen) → lokal spiegeln + Consent-Sheet öffnen.
            if auth.handleServerErrorForAiConsent(e) { pendingQuery = query; showAiConsent = true }
            else { error = e.localizedDescription }
        }
        catch let ex {
            if auth.handleServerErrorForAiConsent(ex) { pendingQuery = query; showAiConsent = true }
            else { error = ex.localizedDescription }
        }
    }

    private func open(_ hit: SearchHitDto) {
        previewFileItem = FileItem(
            id: hit.id, name: hit.name, sizeBytes: 0,
            contentType: "application/octet-stream",
            createdAt: Date(), ownerName: nil,
            aiTags: nil, aiRiskFlag: nil)
    }
}
