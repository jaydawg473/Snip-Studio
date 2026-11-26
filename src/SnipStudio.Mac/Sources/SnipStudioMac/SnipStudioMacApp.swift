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
        Window("Snip Studio", id: "studio") {
            StudioView(applicationDelegate: applicationDelegate)
        }
        .defaultSize(width: 540, height: 310)
        .windowResizability(.contentMinSize)

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
    }
}
