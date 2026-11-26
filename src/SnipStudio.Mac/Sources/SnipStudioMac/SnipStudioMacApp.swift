import AppKit
import Darwin
import SwiftUI

@main
enum SnipStudioMacEntry {
    @MainActor
    static func main() async {
        if CommandLine.arguments.dropFirst().elementsEqual(["--self-check"]) {
            exit(await TrialSelfCheck.run())
        }
        SnipStudioMacApp.main()
    }
}

struct SnipStudioMacApp: App {
    @NSApplicationDelegateAdaptor private var applicationDelegate: StudioApplicationDelegate
    @StateObject private var shortcut = StudioGlobalShortcut()
    @AppStorage(StudioDesktopIntegration.showInMenuBar) private var showInMenuBar = false

    var body: some Scene {
        WindowGroup("Snip Desk", id: "studio", for: UUID.self) { $sessionID in
            StudioView(applicationDelegate: applicationDelegate, sessionID: sessionID)
                .modifier(StudioGlobalCaptureRegistration(applicationDelegate: applicationDelegate, shortcut: shortcut))
        }
        .defaultSize(width: 540, height: 310)
        .windowResizability(.contentMinSize)
        .commands { StudioCommands(applicationDelegate: applicationDelegate) }

        Settings {
            StudioSettingsView(shortcut: shortcut)
                .modifier(StudioGlobalCaptureRegistration(applicationDelegate: applicationDelegate, shortcut: shortcut))
        }

        WindowGroup("Review video", id: "video-review", for: URL.self) { $videoURL in
            if let videoURL { StudioVideoReview(url: videoURL) }
        }
        .defaultSize(width: 900, height: 640)
        .windowResizability(.contentMinSize)

        Window("Record video", id: "recording") {
            if #available(macOS 15.0, *) {
                StudioRecordingView(applicationDelegate: applicationDelegate)
            } else {
                Text("Video recording requires macOS 15 or later.").padding(24)
            }
        }
        .defaultSize(width: 465, height: 135)
        .windowResizability(.contentSize)

        Window("Scrolling capture", id: "scrolling") {
            StudioScrollingView(applicationDelegate: applicationDelegate)
        }
        .defaultSize(width: 490, height: 205)
        .windowResizability(.contentMinSize)

        Window("Freeform capture", id: "freeform") {
            StudioFreeformCaptureView(applicationDelegate: applicationDelegate)
        }
        .defaultSize(width: 840, height: 620)
        .windowResizability(.contentMinSize)

        MenuBarExtra("Snip Desk", systemImage: "camera.viewfinder", isInserted: $showInMenuBar) {
            StudioMenuBarContent(applicationDelegate: applicationDelegate)
        }
    }
}

private struct StudioCommands: Commands {
    let applicationDelegate: StudioApplicationDelegate

    @Environment(\.openWindow) private var openWindow
    @FocusedValue(\.studioNewSnip) private var newSnip
    @FocusedValue(\.studioHistory) private var history

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Snip or Recording") {
                if let newSnip { newSnip() } else { openWindow(id: "studio") }
            }
            .keyboardShortcut("n", modifiers: .command)
            Button("Close All Snips Without Saving…") {
                applicationDelegate.closeAllSnipsWithoutSaving()
            }
        }
        CommandGroup(replacing: .undoRedo) {
            Button("Undo") { changeHistory(redo: false) }
                .keyboardShortcut("z", modifiers: .command)
                .disabled(history?.editingText != true && history?.canUndo != true)
            Button("Redo") { changeHistory(redo: true) }
                .keyboardShortcut("z", modifiers: [.command, .shift])
                .disabled(history?.editingText != true && history?.canRedo != true)
        }
    }

    private func changeHistory(redo: Bool) {
        guard let history else { return }
        if history.editingText {
            if let undoManager = NSApp.keyWindow?.firstResponder?.undoManager {
                if redo { undoManager.redo() } else { undoManager.undo() }
            }
        } else if redo {
            history.redo()
        } else {
            history.undo()
        }
    }
}

struct StudioHistoryCommands {
    let canUndo: Bool
    let canRedo: Bool
    let editingText: Bool
    let undo: () -> Void
    let redo: () -> Void
}

private struct StudioHistoryKey: FocusedValueKey {
    typealias Value = StudioHistoryCommands
}

private struct StudioNewSnipKey: FocusedValueKey {
    typealias Value = () -> Void
}

extension FocusedValues {
    var studioNewSnip: (() -> Void)? {
        get { self[StudioNewSnipKey.self] }
        set { self[StudioNewSnipKey.self] = newValue }
    }
    var studioHistory: StudioHistoryCommands? {
        get { self[StudioHistoryKey.self] }
        set { self[StudioHistoryKey.self] = newValue }
    }
}
