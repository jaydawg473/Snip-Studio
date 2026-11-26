import SwiftUI

enum StudioSettings {
    static let autoCopy = "autoCopyCapturesAndEdits"
    static let saveOriginals = "autoSaveOriginalCaptures"
    static let fitOnLoad = "fitSnipsOnOpen"
    static let resizeToSnip = "resizeNewWindowsToSnip"
}

struct StudioSettingsView: View {
    let shortcut: StudioGlobalShortcut
    @AppStorage(StudioSettings.autoCopy) private var autoCopy = true
    @AppStorage(StudioSettings.saveOriginals) private var saveOriginals = false
    @AppStorage(StudioSettings.fitOnLoad) private var fitOnLoad = true
    @AppStorage(StudioSettings.resizeToSnip) private var resizeToSnip = true
    @AppStorage(StudioTranslationServer.preferenceKey) private var translationServer = ""

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
                Toggle("Resize new windows to the snip", isOn: $resizeToSnip)
            }
            StudioDesktopSettingsView(shortcut: shortcut)
            StudioSettingsBackupSection()
            Section("Translation") {
                TextField("Optional LibreTranslate server", text: $translationServer)
                    .textFieldStyle(.roundedBorder)
                Text("Enter an HTTPS server address. Snip Desk asks each time before sending recognized text.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .toggleStyle(.switch)
        .frame(width: 540, height: 510)
    }
}
