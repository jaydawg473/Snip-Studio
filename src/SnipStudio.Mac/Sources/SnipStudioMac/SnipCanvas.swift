import AppKit
import SwiftUI

@MainActor
struct SnipCanvas: View {
    @ObservedObject var document: EditorDocument
    let inkColor: Color
    let inkWidth: CGFloat

    @GestureState private var dragActive = false

    init(document: EditorDocument, inkColor: Color, inkWidth: CGFloat) {
        _document = ObservedObject(wrappedValue: document)
        self.inkColor = inkColor
        self.inkWidth = inkWidth
    }

    var body: some View {
        GeometryReader { proxy in
            if let image = document.image, let fit = FittedRaster(image: image, in: proxy.size) {
                let strokes = document.strokes
                let draft = document.draftStroke
                Canvas { context, _ in
                    context.clip(to: Path(fit.frame))
                    context.draw(Image(decorative: image, scale: 1), in: fit.frame)
                    context.translateBy(x: fit.frame.minX, y: fit.frame.minY)
                    context.scaleBy(x: fit.scale, y: fit.scale)
                    for stroke in strokes {
                        context.fill(Path(stroke.outline), with: .color(Color(cgColor: stroke.color)))
                    }
                    if let draft {
                        context.fill(Path(draft.outline), with: .color(Color(cgColor: draft.color)))
                    }
                }
                .contentShape(Rectangle())
                .accessibilityLabel("Snip editor canvas")
                .gesture(inkGesture(in: fit))
            }
        }
        .onChange(of: dragActive) { _, active in
            guard !active else { return }
            // A drag cancelled by the system may have no onEnded callback.
            Task { @MainActor in
                if !dragActive { document.cancelStroke() }
            }
        }
        .onDisappear { document.cancelStroke() }
    }

    private func inkGesture(in fit: FittedRaster) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .updating($dragActive) { _, active, _ in active = true }
            .onChanged { event in
                addInk(for: event, in: fit)
            }
            .onEnded { event in
                // A click can complete without a distinct drag update.
                addInk(for: event, in: fit)
                document.finishStroke()
            }
    }

    private func addInk(for event: DragGesture.Value, in fit: FittedRaster) {
        if !document.isDrawing {
            guard fit.frame.contains(event.startLocation) else { return }
            let color = NSColor(inkColor).usingColorSpace(.sRGB)?.cgColor ?? NSColor.systemRed.cgColor
            document.beginStroke(at: fit.imagePoint(event.startLocation), color: color, width: inkWidth)
        }
        document.extendStroke(to: fit.imagePoint(event.location))
    }
}

private struct FittedRaster {
    let frame: CGRect
    let scale: CGFloat
    private let pixelSize: CGSize

    init?(image: CGImage, in viewport: CGSize) {
        let pixelSize = CGSize(width: CGFloat(image.width), height: CGFloat(image.height))
        let scale = min(viewport.width / pixelSize.width, viewport.height / pixelSize.height)
        guard scale > 0, scale.isFinite else { return nil }

        let width = pixelSize.width * scale
        let height = pixelSize.height * scale
        self.pixelSize = pixelSize
        self.scale = scale
        frame = CGRect(x: (viewport.width - width) / 2, y: (viewport.height - height) / 2, width: width, height: height)
    }

    func imagePoint(_ point: CGPoint) -> CGPoint {
        CGPoint(
            x: min(pixelSize.width, max(0, (point.x - frame.minX) / scale)),
            y: min(pixelSize.height, max(0, (point.y - frame.minY) / scale))
        )
    }
}
