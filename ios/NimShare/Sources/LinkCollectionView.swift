import SwiftUI

/// v1.10.111 — Linksammlung (löst die Wiki-Ansicht ab). Eine geteilte,
/// flache Liste nützlicher Links. Alle sehen sie, nur Admins pflegen.
/// Tippen öffnet den Link im Browser. Name bewusst „LinkCollection", weil
/// LinksView bereits die Share-Links („Meine Links") ist.
struct LinkCollectionView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.openURL) private var openURL

    @State private var links: [NimShareAPI.LinkEntryDto] = []
    @State private var loading = true
    @State private var error: String?
    @State private var editing: LinkEditTarget?

    private var isAdmin: Bool { auth.user?.role == "Admin" }

    struct LinkEditTarget: Identifiable {
        let id: UUID          // frische UUID = neu
        let existing: NimShareAPI.LinkEntryDto?
        var isNew: Bool { existing == nil }
    }

    var body: some View {
        Group {
            if loading && links.isEmpty {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if links.isEmpty {
                ContentUnavailableView(
                    "Noch keine Bookmarks",
                    systemImage: "bookmark",
                    description: Text(isAdmin
                        ? "Tippe oben rechts auf +, um das erste Bookmark hinzuzufügen."
                        : "Sobald ein Admin Bookmarks hinzufügt, erscheinen sie hier."))
            } else {
                List {
                    ForEach(links) { l in
                        Button { open(l) } label: { row(l) }
                            .buttonStyle(.plain)
                            .swipeActions(edge: .trailing) {
                                if isAdmin {
                                    Button(role: .destructive) { Task { await delete(l) } } label: {
                                        Label("Löschen", systemImage: "trash")
                                    }
                                    Button { editing = LinkEditTarget(id: l.id, existing: l) } label: {
                                        Label("Bearbeiten", systemImage: "pencil")
                                    }.tint(Theme.tungstenBlue)
                                }
                            }
                    }
                }
            }
        }
        .navigationTitle("Bookmarks")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if isAdmin {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { editing = LinkEditTarget(id: UUID(), existing: nil) } label: {
                        Image(systemName: "plus")
                    }
                }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .sheet(item: $editing) { t in
            LinkCollectionEditSheet(target: t) { Task { await load() } }
        }
        .alert("Fehler", isPresented: .constant(error != nil)) {
            Button("OK", role: .cancel) { error = nil }
        } message: { Text(error ?? "") }
    }

    private func row(_ l: NimShareAPI.LinkEntryDto) -> some View {
        HStack(spacing: 12) {
            Text(l.emoji?.isEmpty == false ? l.emoji! : "🔗").font(.title2)
            VStack(alignment: .leading, spacing: 2) {
                Text(l.title).font(.body.weight(.medium))
                if let d = l.description, !d.isEmpty {
                    Text(d).font(.caption).foregroundStyle(.secondary)
                }
                Text(l.url).font(.caption2).foregroundStyle(Theme.tungstenBlue).lineLimit(1)
            }
            Spacer()
            Image(systemName: "arrow.up.right.square").foregroundStyle(.secondary)
        }
        .contentShape(Rectangle())
    }

    private func open(_ l: NimShareAPI.LinkEntryDto) {
        guard let u = URL(string: l.url) else { return }
        openURL(u)
    }

    private func load() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { links = try await api.linkCollection() }
        catch let e as ApiError { if links.isEmpty { error = e.localizedDescription } }
        catch let ex { if links.isEmpty { error = ex.localizedDescription } }
    }

    private func delete(_ l: NimShareAPI.LinkEntryDto) async {
        guard let api = auth.api else { return }
        do { try await api.deleteLink(id: l.id); await load() }
        catch let ex { error = ex.localizedDescription }
    }
}

/// Editor-Sheet für einen Link (neu oder bearbeiten). Nur Admins erreichen es.
struct LinkCollectionEditSheet: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    let target: LinkCollectionView.LinkEditTarget
    let onSaved: () -> Void

    @State private var emoji = ""
    @State private var title = ""
    @State private var url = ""
    @State private var desc = ""
    @State private var saving = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section("Symbol") {
                    TextField("🔗", text: $emoji).font(.title2)
                }
                Section("Name") {
                    TextField("Tungsten Software Center", text: $title)
                        .textInputAutocapitalization(.words)
                }
                Section("Link (URL)") {
                    TextField("https://delivery.tungstenautomation.com/", text: $url)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                Section("Beschreibung (optional)") {
                    TextField("Kurze Notiz", text: $desc)
                }
            }
            .navigationTitle(target.isNew ? "Bookmark hinzufügen" : "Bookmark bearbeiten")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Speichern") { Task { await save() } }.disabled(saving || !isValid)
                }
            }
            .onAppear {
                if let e = target.existing {
                    emoji = e.emoji ?? ""; title = e.title; url = e.url; desc = e.description ?? ""
                }
            }
            .alert("Fehler", isPresented: .constant(error != nil)) {
                Button("OK", role: .cancel) { error = nil }
            } message: { Text(error ?? "") }
        }
    }

    private var isValid: Bool {
        !title.trimmingCharacters(in: .whitespaces).isEmpty
        && (url.lowercased().hasPrefix("http://") || url.lowercased().hasPrefix("https://"))
    }

    private func save() async {
        guard let api = auth.api else { return }
        saving = true; defer { saving = false }
        let d = desc.trimmingCharacters(in: .whitespaces)
        let em = emoji.trimmingCharacters(in: .whitespaces)
        do {
            if let e = target.existing {
                try await api.updateLink(id: e.id, title: title.trimmingCharacters(in: .whitespaces),
                                         url: url.trimmingCharacters(in: .whitespaces),
                                         description: d.isEmpty ? nil : d, emoji: em.isEmpty ? nil : em)
            } else {
                try await api.createLink(title: title.trimmingCharacters(in: .whitespaces),
                                         url: url.trimmingCharacters(in: .whitespaces),
                                         description: d.isEmpty ? nil : d, emoji: em.isEmpty ? nil : em)
            }
            onSaved()
            dismiss()
        }
        catch let e as ApiError { error = e.localizedDescription }
        catch let ex { error = ex.localizedDescription }
    }
}
