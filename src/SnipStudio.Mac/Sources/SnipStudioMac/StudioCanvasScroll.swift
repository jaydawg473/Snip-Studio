import SwiftUI

enum StudioPanEvent {
    case began, changed(CGSize), ended
}

typealias StudioPanAction = (StudioPanEvent) -> Void

struct StudioCanvasScroll<Content: View>: View {
    let makeContent: (StudioPanAction?) -> Content

    var body: some View {
        if #available(macOS 15.0, *) {
            StudioInteractiveCanvasScroll(makeContent: makeContent)
        } else {
            ScrollView([.horizontal, .vertical]) { makeContent(nil) }
                .defaultScrollAnchor(.center)
                .scrollBounceBehavior(.basedOnSize)
        }
    }
}

@available(macOS 15.0, *)
private struct StudioInteractiveCanvasScroll<Content: View>: View {
    let makeContent: (StudioPanAction?) -> Content
    @State private var position = ScrollPosition()
    @State private var offset = CGPoint.zero
    @State private var dragStart: CGPoint?

    var body: some View {
        ScrollView([.horizontal, .vertical]) { makeContent(handlePan) }
            .defaultScrollAnchor(.center)
            .scrollBounceBehavior(.basedOnSize)
            .scrollPosition($position)
            .onScrollGeometryChange(for: CGPoint.self) { geometry in
                geometry.contentOffset
            } action: { _, point in
                offset = point
            }
    }

    private func handlePan(_ event: StudioPanEvent) {
        switch event {
        case .began: dragStart = offset
        case let .changed(translation):
            guard let dragStart else { return }
            position.scrollTo(point: CGPoint(x: max(0, dragStart.x - translation.width),
                                             y: max(0, dragStart.y - translation.height)))
        case .ended: dragStart = nil
        }
    }
}
