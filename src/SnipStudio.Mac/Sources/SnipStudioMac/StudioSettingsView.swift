import SwiftUI

enum StudioSettings {
    static let autoCopy = "autoCopyCapturesAndEdits"
    static let saveOriginals = "autoSaveOriginalCaptures"
    static let fitOnLoad = "fitSnipsOnOpen"
}

struct StudioSettingsView: View {
    @AppStorage(StudioSettings.autoCopy) private var autoCopy = true
    @AppStorage(StudioSettings.saveOriginals) private var saveOriginals = true
    @AppStorage(StudioSettings.fitOnLoad) private var fitOnLoad = true

    var body: some View {
        Form {
            Section("Capture") {
                Toggle("Copy screenshots and edits to the clipboard", isOn: $autoCopy)
                Toggle(isOn: $saveOriginals) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Save original screenshots automatically")
                        Text("Originals are saved in ~/Pictures/Snip Studio.")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }
            }

            Section("Editor") {
                Toggle(isOn: $fitOnLoad) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Fit new snips to the window")
                        Text("Turn this off to start at 100% zoom.")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .toggleStyle(.switch)
        .frame(width: 520, height: 290)
    }
}
