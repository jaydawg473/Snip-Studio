import SwiftUI

enum StudioTimelineInteraction: Equatable {
    case scrub, trimStart, trimEnd
}

struct StudioFilmstrip: View {
    let frames: [CGImage?]
    let duration: Double
    let currentTime: Double
    let trimStart: Double
    let trimEnd: Double
    let onSeek: (StudioTimelineInteraction, Double, Bool) -> Void

    @State private var activeDrag: StudioTimelineInteraction?
    @GestureState private var dragInProgress = false

    private let handleWidth: CGFloat = 10

    var body: some View {
        GeometryReader { proxy in
            let bounds = proxy.size
            let width = max(1, bounds.width)
            let start = position(trimStart, across: width)
            let end = position(trimEnd, across: width)
            let playhead = position(currentTime, across: width)

            ZStack(alignment: .topLeading) {
                thumbnails(width: width, height: bounds.height)
                Color.black.opacity(0.58).frame(width: start)
                Color.black.opacity(0.58).frame(width: max(0, width - end)).offset(x: end)
                Rectangle()
                    .strokeBorder(StudioColors.primary, lineWidth: 2)
                    .frame(width: max(0, end - start))
                    .offset(x: start)

                Rectangle()
                    .fill(.white)
                    .frame(width: 2)
                    .shadow(color: .black.opacity(0.75), radius: 1, x: 0, y: 1)
                    .offset(x: max(0, min(width - 2, playhead)))

                trimHandle(at: start, width: width, label: "Trim start")
                trimHandle(at: end, width: width, label: "Trim end")
            }
            .frame(width: width, height: bounds.height)
            .background(.black.opacity(0.85))
            .clipShape(RoundedRectangle(cornerRadius: 5))
            .contentShape(Rectangle())
            .gesture(dragGesture(width: width, start: start, end: end))
        }
        .frame(height: 70)
        .accessibilityHidden(true)
        .help("Click to seek. Drag a blue handle to trim the video.")
        .onChange(of: dragInProgress) { _, dragging in
            if !dragging { activeDrag = nil }
        }
    }

    private func thumbnails(width: CGFloat, height: CGFloat) -> some View {
        HStack(spacing: 1) {
            ForEach(frames.indices, id: \.self) { index in
                let tileWidth = max(1, (width - CGFloat(frames.count - 1)) / CGFloat(frames.count))
                ZStack {
                    Color.black.opacity(0.7)
                    if let frame = frames[index] {
                        Image(decorative: frame, scale: 1)
                            .resizable()
                            .scaledToFill()
                    }
                }
                .frame(width: tileWidth, height: height)
                .clipped()
            }
        }
    }

    private func trimHandle(at position: CGFloat, width: CGFloat, label: String) -> some View {
        RoundedRectangle(cornerRadius: 3)
            .fill(StudioColors.primary)
            .overlay { Capsule().fill(.white).frame(width: 2, height: 18) }
            .frame(width: handleWidth)
            .offset(x: max(0, min(width - handleWidth, position - handleWidth / 2)))
            .help(label)
    }

    private func position(_ seconds: Double, across width: CGFloat) -> CGFloat {
        guard duration > 0 else { return 0 }
        return width * CGFloat(min(1, max(0, seconds / duration)))
    }

    private func dragGesture(width: CGFloat, start: CGFloat, end: CGFloat) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .updating($dragInProgress) { _, active, _ in active = true }
            .onChanged { value in
                let interaction = activeDrag ?? self.interaction(at: value.startLocation.x, start: start, end: end)
                activeDrag = interaction
                onSeek(interaction, time(at: value.location.x, width: width), false)
            }
            .onEnded { value in
                let interaction = activeDrag ?? self.interaction(at: value.startLocation.x, start: start, end: end)
                activeDrag = nil
                onSeek(interaction, time(at: value.location.x, width: width), true)
            }
    }

    private func interaction(at position: CGFloat, start: CGFloat, end: CGFloat) -> StudioTimelineInteraction {
        let startDistance = abs(position - start)
        let endDistance = abs(position - end)
        if min(startDistance, endDistance) > handleWidth + 3 { return .scrub }
        return startDistance <= endDistance ? .trimStart : .trimEnd
    }

    private func time(at x: CGFloat, width: CGFloat) -> Double {
        duration * Double(min(1, max(0, x / width)))
    }
}
