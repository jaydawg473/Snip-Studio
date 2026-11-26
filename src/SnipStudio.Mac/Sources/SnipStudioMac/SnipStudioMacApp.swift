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
    var body: some Scene {
        Window("Snip Studio", id: "studio") {
            StudioView()
        }
        .defaultSize(width: 850, height: 600)
    }
}
