import SwiftUI

struct TwoFactorSetupView: View {
    @EnvironmentObject var auth: AuthStore
    @Environment(\.dismiss) private var dismiss

    @State private var status: TotpStatus?
    @State private var setup: TotpInitResponse?
    @State private var code = ""
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                if let s = status {
                    if s.enabled {
                        enrolledSection(s)
                    } else {
                        setupSection
                    }
                } else {
                    ProgressView().frame(maxWidth: .infinity)
                }
                if let e = error {
                    Text(e).foregroundStyle(Theme.warnRed).font(.footnote)
                }
            }
            .navigationTitle("Zwei-Faktor")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Fertig") { dismiss() }
                }
            }
            .task { await loadStatus() }
            .overlay { if loading { ProgressView() } }
        }
    }

    @ViewBuilder
    private func enrolledSection(_ s: TotpStatus) -> some View {
        Section("Status") {
            HStack {
                Image(systemName: "checkmark.shield.fill").foregroundStyle(.green)
                VStack(alignment: .leading) {
                    Text("2FA ist aktiv").font(.body.weight(.semibold))
                    if let at = s.enrolledAt {
                        Text("Eingerichtet: \(at.formatted(date: .abbreviated, time: .shortened))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
        Section("Deaktivieren") {
            TextField("Aktueller 6-stelliger Code", text: $code)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .font(.system(size: 20, design: .monospaced))
                .multilineTextAlignment(.center)
                .onChange(of: code) { _, new in if new.count > 6 { code = String(new.prefix(6)) } }
            Button(role: .destructive) {
                Task { await disable() }
            } label: {
                Label("2FA deaktivieren", systemImage: "shield.slash")
            }
            .disabled(code.count != 6)
        }
    }

    private var setupSection: some View {
        Group {
            Section("1. QR-Code scannen") {
                Text("Öffne deine Authenticator-App und scanne diesen Link:")
                    .font(.caption).foregroundStyle(.secondary)
                if let s = setup {
                    Link(destination: URL(string: s.otpAuthUri)!) {
                        HStack {
                            Image(systemName: "square.and.arrow.up")
                            Text("otpauth-URL öffnen")
                        }
                    }
                    Text("Manuell (Base32):").font(.caption).foregroundStyle(.secondary)
                    Text(s.secret)
                        .font(.system(.footnote, design: .monospaced))
                        .textSelection(.enabled)
                        .padding(6)
                        .background(RoundedRectangle(cornerRadius: 4).fill(Theme.cardBackground))
                } else {
                    Button("Setup starten") { Task { await initSetup() } }
                }
            }
            if setup != nil {
                Section("2. Code bestätigen") {
                    TextField("123456", text: $code)
                        .keyboardType(.numberPad)
                        .textContentType(.oneTimeCode)
                        .font(.system(size: 26, design: .monospaced))
                        .multilineTextAlignment(.center)
                        .onChange(of: code) { _, new in if new.count > 6 { code = String(new.prefix(6)) } }
                    Button("2FA aktivieren") { Task { await verify() } }
                        .disabled(code.count != 6)
                }
            }
        }
    }

    private func loadStatus() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { status = try await api.totpStatus() }
        catch let ex { error = ex.localizedDescription }
    }

    private func initSetup() async {
        guard let api = auth.api else { return }
        loading = true; defer { loading = false }
        do { setup = try await api.totpInit() }
        catch let ex { error = ex.localizedDescription }
    }

    private func verify() async {
        guard let api = auth.api, let s = setup else { return }
        loading = true; error = nil; defer { loading = false }
        do {
            try await api.totpVerify(secret: s.secret, code: code)
            await loadStatus()
            code = ""
        } catch let ex { error = ex.localizedDescription; code = "" }
    }

    private func disable() async {
        guard let api = auth.api else { return }
        loading = true; error = nil; defer { loading = false }
        do {
            try await api.totpDisable(code: code)
            await loadStatus()
            code = ""
        } catch let ex { error = ex.localizedDescription; code = "" }
    }
}
