import AppKit
import ServiceManagement
import SwiftUI

enum StudioDesktopIntegration {
    static let showInMenuBar = "showSnipStudioInMenuBar"

    @MainActor
    static func captureRegion(applicationDelegate: StudioApplicationDelegate, open: @escaping (UUID) -> Void) {
        guard !applicationDelegate.captureInProgress else { return }
        applicationDelegate.captureInProgress = true
        Task { @MainActor in
            defer { applicationDelegate.captureInProgress = false }
            do {
                guard let image = try await CaptureService.capture(.region) else { return }
                let id = applicationDelegate.storePendingSnip(image)
                NSApp.unhide(nil)
                NSApp.activate()
                open(id)
            } catch is CancellationError {
                return
            } catch {
                NSApp.unhide(nil)
                NSApp.activate()
                let alert = NSAlert()
                alert.messageText = "Couldn’t capture the region"
                alert.informativeText = error.localizedDescription
                alert.addButton(withTitle: "OK")
                alert.runModal()
            }
        }
    }
}

@MainActor
struct StudioDesktopSettingsView: View {
    @ObservedObject var shortcut: StudioGlobalShortcut
    @AppStorage(StudioDesktopIntegration.showInMenuBar) private var showInMenuBar = false
    @AppStorage(StudioGlobalShortcut.preferenceKey) private var shortcutEnabled = false
    @AppStorage(StudioGlobalShortcut.chordPreferenceKey) private var shortcutChord = StudioGlobalShortcut.Chord.commandShift6
    @State private var loginStatus = SMAppService.mainApp.status
    @State private var loginError: String?

    var body: some View {
        Section("Desktop") {
            Toggle("Show Snip Studio in the menu bar", isOn: $showInMenuBar)
            Toggle("Enable the global capture shortcut", isOn: $shortcutEnabled)
            if shortcutEnabled {
                Picker("Capture shortcut", selection: $shortcutChord) {
                    ForEach(StudioGlobalShortcut.Chord.allCases) { chord in Text(chord.title).tag(chord) }
                }
            }
            if shortcutEnabled, let error = shortcut.errorMessage {
                Text(error).font(.footnote).foregroundStyle(.secondary)
            }
            Toggle("Open Snip Studio at login", isOn: Binding(
                get: { loginStatus == .enabled || loginStatus == .requiresApproval },
                set: { requested in setLaunchAtLogin(requested) }
            ))
            if loginStatus == .requiresApproval {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Approve Snip Studio in System Settings to finish setting up launch at login.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                    Button("Open Login Items") { SMAppService.openSystemSettingsLoginItems() }
                }
            }
        }
        .onAppear(perform: refreshLoginStatus)
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshLoginStatus()
        }
        .alert("Couldn’t change launch at login", isPresented: Binding(
            get: { loginError != nil },
            set: { if !$0 { loginError = nil } }
        )) {
            Button("OK", role: .cancel) { loginError = nil }
        } message: {
            Text(loginError ?? "")
        }
    }

    private func refreshLoginStatus() {
        loginStatus = SMAppService.mainApp.status
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        let service = SMAppService.mainApp
        let status = service.status
        let registered = status == .enabled || status == .requiresApproval
        defer { refreshLoginStatus() }
        guard enabled != registered else { return }
        do {
            if enabled { try service.register() } else { try service.unregister() }
        } catch {
            loginError = error.localizedDescription
        }
    }
}

@MainActor
struct StudioMenuBarContent: View {
    let applicationDelegate: StudioApplicationDelegate

    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("New Region Snip", action: captureRegion)
            .disabled(applicationDelegate.captureInProgress)
        Button("Open Snip Studio", action: openStudio)
        Divider()
        Button("Quit Snip Studio") { NSApp.terminate(nil) }
    }

    private func openStudio() {
        NSApp.unhide(nil)
        NSApp.activate()
        openWindow(id: "studio")
    }

    private func captureRegion() {
        StudioDesktopIntegration.captureRegion(applicationDelegate: applicationDelegate) { id in
            openWindow(id: "studio", value: id)
        }
    }
}

@MainActor
struct StudioGlobalCaptureRegistration: ViewModifier {
    let applicationDelegate: StudioApplicationDelegate
    let shortcut: StudioGlobalShortcut
    @Environment(\.openWindow) private var openWindow
    @AppStorage(StudioGlobalShortcut.preferenceKey) private var enabled = false
    @AppStorage(StudioGlobalShortcut.chordPreferenceKey) private var chord = StudioGlobalShortcut.Chord.commandShift6

    private struct Preference: Equatable {
        let enabled: Bool
        let chord: StudioGlobalShortcut.Chord
    }

    func body(content: Content) -> some View {
        content.onChange(of: Preference(enabled: enabled, chord: chord), initial: true) { _, value in
            let delegate = applicationDelegate
            let open = openWindow
            shortcut.update(enabled: value.enabled, chord: value.chord) { [weak delegate] in
                guard let delegate else { return }
                StudioDesktopIntegration.captureRegion(applicationDelegate: delegate) { id in
                    open(id: "studio", value: id)
                }
            }
        }
    }
}
