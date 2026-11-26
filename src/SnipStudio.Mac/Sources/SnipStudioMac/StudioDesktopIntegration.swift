import AppKit
import ServiceManagement
import SwiftUI

enum StudioDesktopIntegration {
    static let showInMenuBar = "showSnipStudioInMenuBar"
}

@MainActor
struct StudioDesktopSettingsView: View {
    @AppStorage(StudioDesktopIntegration.showInMenuBar) private var showInMenuBar = false
    @State private var loginStatus = SMAppService.mainApp.status
    @State private var loginError: String?

    var body: some View {
        Section("Desktop") {
            Toggle("Show Snip Studio in the menu bar", isOn: $showInMenuBar)
            Toggle("Open Snip Studio at login", isOn: Binding(
                get: { loginStatus == .enabled || loginStatus == .requiresApproval },
                set: setLaunchAtLogin
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
        guard !applicationDelegate.captureInProgress else { return }
        applicationDelegate.captureInProgress = true
        Task { @MainActor in
            defer { applicationDelegate.captureInProgress = false }
            do {
                guard let image = try await CaptureService.capture(.region) else { return }
                let id = applicationDelegate.storePendingSnip(image)
                NSApp.unhide(nil)
                NSApp.activate()
                openWindow(id: "studio", value: id)
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
