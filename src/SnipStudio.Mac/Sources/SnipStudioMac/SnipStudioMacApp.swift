import SwiftUI

@main
struct SnipStudioMacApp: App {
    var body: some Scene {
        Window("Snip Studio", id: "studio") {
            StudioView()
        }
        .defaultSize(width: 850, height: 600)
    }
}
