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

    var body: some Scene {
        WindowGroup("Snip Studio", id: "studio", for: UUID.self) { $sessionID in
            StudioView(applicationDelegate: applicationDelegate, sessionID: sessionID)
        }
        .defaultSize(width: 540, height: 310)
        .windowResizability(.contentMinSize)
        .commands { StudioCommands(applicationDelegate: applicationDelegate) }

        Settings { StudioSettingsView() }

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
    }
}

private struct StudioCommands: Commands {
    let applicationDelegate: StudioApplicationDelegate

    @Environment(\.openWindow) private var openWindow
    @FocusedValue(\.studioNewSnip) private var newSnip

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
    }
}

private struct StudioNewSnipKey: FocusedValueKey {
    typealias Value = () -> Void
}

extension FocusedValues {
    var studioNewSnip: (() -> Void)? {
        get { self[StudioNewSnipKey.self] }
        set { self[StudioNewSnipKey.self] = newValue }
    }
}
