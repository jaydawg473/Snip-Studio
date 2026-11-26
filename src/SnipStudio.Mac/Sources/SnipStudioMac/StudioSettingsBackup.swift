import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct StudioSettingsBackup {
    private static let valuesByKey = [StudioSettings.autoCopy: true, StudioSettings.saveOriginals: false,
                                      StudioSettings.fitOnLoad: true, StudioSettings.resizeToSnip: true]

    private struct Archive: Codable {
        let version: Int
        let preferences: [String: Bool]
    }

    @MainActor
    static func save(_ defaults: UserDefaults = .standard, to url: URL) throws {
        let values = Dictionary(uniqueKeysWithValues: valuesByKey.map { key, fallback in
            (key, defaults.object(forKey: key) as? Bool ?? fallback)
        })
        let data = try JSONEncoder().encode(Archive(version: 1, preferences: values))
        try data.write(to: url, options: .atomic)
    }

    @MainActor
    static func restore(from url: URL, into defaults: UserDefaults = .standard) throws {
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
        guard values.isRegularFile == true, let size = values.fileSize, (1...65_536).contains(size) else {
            throw Failure.invalidArchive
        }
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        let data = try input.read(upToCount: 65_537) ?? Data()
        guard data.count <= 65_536,
              let archive = try? JSONDecoder().decode(Archive.self, from: data), archive.version == 1,
              Set(archive.preferences.keys) == Set(valuesByKey.keys) else { throw Failure.invalidArchive }
        for (key, value) in archive.preferences { defaults.set(value, forKey: key) }
    }

    private enum Failure: LocalizedError {
        case invalidArchive

        var errorDescription: String? { "Choose a complete Mac settings backup exported from the app." }
    }
}

@MainActor
struct StudioSettingsBackupSection: View {
    @State private var message: String?

    var body: some View {
        Section("Preferences backup") {
            HStack {
                Button("Back up…") { save() }
                Button("Restore…") { restore() }
            }
            Text("Includes capture and editor preferences. macOS permissions and login items stay under System Settings.")
                .font(.footnote).foregroundStyle(.secondary)
        }
        .alert("Snip Desk Settings", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
            Button("OK", role: .cancel) { message = nil }
        } message: {
            Text(message ?? "")
        }
    }

    private func save() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "Snip Desk Settings.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try StudioSettingsBackup.save(to: url)
            message = "Settings backup saved."
        } catch { message = error.localizedDescription }
    }

    private func restore() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try StudioSettingsBackup.restore(from: url)
            message = "Capture and editor preferences restored."
        } catch { message = error.localizedDescription }
    }
}
