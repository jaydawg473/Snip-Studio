import AppKit
import SwiftUI

@MainActor
struct SnipCanvas: View {
    @ObservedObject var document: EditorDocument
    let inkColor: Color
    let inkWidth: CGFloat
    let zoom: CGFloat?
    let allowsDrawing: Bool
    let recognizedLines: [RecognizedLine]
    let onZoomChanged: (CGFloat) -> Void

    @Environment(\.displayScale) private var displayScale
    @GestureState private var dragActive = false
    @Namespace private var viewport

    init(document: EditorDocument, inkColor: Color, inkWidth: CGFloat, zoom: CGFloat? = nil,
         allowsDrawing: Bool = true, recognizedLines: [RecognizedLine] = [], onZoomChanged: @escaping (CGFloat) -> Void = { _ in }) {
        _document = ObservedObject(wrappedValue: document)
        self.inkColor = inkColor
        self.inkWidth = inkWidth
        self.zoom = zoom
        self.allowsDrawing = allowsDrawing
        self.recognizedLines = recognizedLines
        self.onZoomChanged = onZoomChanged
    }

    var body: some View {
        GeometryReader { proxy in
            if let image = document.image,
               let layout = SnipViewportLayout(
                   imageSize: CGSize(width: CGFloat(image.width), height: CGFloat(image.height)), viewport: proxy.size,
                   zoom: zoom, displayScale: displayScale
               ) {
                ScrollView([.horizontal, .vertical]) {
                    GeometryReader { surface in
                        let origin = surface.frame(in: .named(viewport)).origin
                        let imageFrame = layout.imageFrame(
                            surfaceOrigin: origin, viewportOrigin: proxy.frame(in: .global).origin
                        )
                        // The drawing surface stays the size of the visible viewport at every zoom.
                        raster(image, frame: imageFrame, layout: layout)
                            .frame(width: proxy.size.width, height: proxy.size.height)
                            .position(x: proxy.size.width / 2 - origin.x, y: proxy.size.height / 2 - origin.y)
                    }
                    .frame(width: layout.surfaceSize.width, height: layout.surfaceSize.height)
                }
                .coordinateSpace(name: viewport)
                .defaultScrollAnchor(.center)
                .scrollBounceBehavior(.basedOnSize)
                .clipped()
                .onChange(of: layout.scale * displayScale, initial: true) { _, selectedScale in
                    onZoomChanged(selectedScale)
                }
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
        .onChange(of: zoom) { _, _ in document.cancelStroke() }
        .onChange(of: allowsDrawing) { _, enabled in
            if !enabled { document.cancelStroke() }
        }
    }

    private func raster(_ image: CGImage, frame: CGRect, layout: SnipViewportLayout) -> some View {
        let strokes = document.strokes
        let draft = document.draftStroke
        let border = Color(nsColor: .separatorColor)
        return ZStack {
            Canvas { context, _ in
                let framePath = Path(frame)
                var shadow = context
                shadow.addFilter(.shadow(color: .black.opacity(0.28), radius: 3, x: 2, y: 3, options: .shadowOnly))
                shadow.fill(framePath, with: .color(.black))
                let preview = Image(decorative: image, scale: 1).interpolation(layout.usesNativePixels ? .none : .high)
                context.draw(preview, in: frame)

                var ink = context
                ink.clip(to: framePath)
                ink.translateBy(x: frame.minX, y: frame.minY)
                ink.scaleBy(x: layout.scale, y: layout.scale)
                for stroke in strokes {
                    ink.fill(Path(stroke.outline), with: .color(Color(cgColor: stroke.color)))
                }
                if let draft {
                    ink.fill(Path(draft.outline), with: .color(Color(cgColor: draft.color)))
                }

                let borderWidth = layout.usesNativePixels ? 1 / displayScale : 1
                let borderRect = frame.insetBy(dx: -borderWidth / 2, dy: -borderWidth / 2)
                context.stroke(Path(borderRect), with: .color(border), lineWidth: borderWidth)
            }
            .contentShape(Rectangle())
            .accessibilityLabel("Snip editor canvas")
            .gesture(inkGesture(in: frame, layout: layout), including: allowsDrawing ? .all : .none)

            if !recognizedLines.isEmpty {
                OCRRegionOverlay(lines: recognizedLines, imageFrame: frame)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
            }
        }
    }

    private func inkGesture(in frame: CGRect, layout: SnipViewportLayout) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .updating($dragActive) { _, active, _ in active = true }
            .onChanged { event in
                addInk(for: event, in: frame, layout: layout)
            }
            .onEnded { event in
                // A click can complete without a distinct drag update.
                addInk(for: event, in: frame, layout: layout)
                document.finishStroke()
            }
    }

    private func addInk(for event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout) {
        if !document.isDrawing {
            guard frame.contains(event.startLocation) else { return }
            let color = NSColor(inkColor).usingColorSpace(.sRGB)?.cgColor ?? NSColor.systemRed.cgColor
            document.beginStroke(at: layout.imagePoint(event.startLocation, in: frame), color: color, width: inkWidth)
        }
        document.extendStroke(to: layout.imagePoint(event.location, in: frame))
    }
}

struct SnipViewportLayout {
    private static let margin: CGFloat = 24
    let surfaceSize: CGSize
    let scale: CGFloat
    let usesNativePixels: Bool
    private let pixelSize: CGSize
    private let rasterSize: CGSize
    private let displayScale: CGFloat

    init?(imageSize: CGSize, viewport: CGSize, zoom: CGFloat?, displayScale: CGFloat) {
        guard imageSize.width > 0, imageSize.height > 0, viewport.width > 0, viewport.height > 0,
              imageSize.width.isFinite, imageSize.height.isFinite, viewport.width.isFinite, viewport.height.isFinite,
              displayScale > 0, displayScale.isFinite else { return nil }
        let padding = Self.margin * 2
        let fitScale = min(1 / displayScale, max(1, viewport.width - padding) / imageSize.width,
                           max(1, viewport.height - padding) / imageSize.height)
        let scale = zoom.map { $0 / displayScale } ?? fitScale
        let rasterSize = CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
        guard scale > 0, rasterSize.width.isFinite, rasterSize.height.isFinite else { return nil }

        pixelSize = imageSize
        self.rasterSize = rasterSize
        self.scale = scale
        self.displayScale = displayScale
        usesNativePixels = abs(scale * displayScale - 1) < 1e-9
        surfaceSize = CGSize(width: max(viewport.width, rasterSize.width + padding),
                             height: max(viewport.height, rasterSize.height + padding))
    }

    func imageFrame(surfaceOrigin: CGPoint, viewportOrigin: CGPoint) -> CGRect {
        let x = surfaceOrigin.x + (surfaceSize.width - rasterSize.width) / 2
        let y = surfaceOrigin.y + (surfaceSize.height - rasterSize.height) / 2
        guard usesNativePixels else { return CGRect(origin: CGPoint(x: x, y: y), size: rasterSize) }
        return CGRect(origin: CGPoint(x: alignToRaster(x, offset: viewportOrigin.x),
                                     y: alignToRaster(y, offset: viewportOrigin.y)), size: rasterSize)
    }

    func imagePoint(_ point: CGPoint, in frame: CGRect) -> CGPoint {
        CGPoint(
            x: min(pixelSize.width, max(0, (point.x - frame.minX) / scale)),
            y: min(pixelSize.height, max(0, (point.y - frame.minY) / scale))
        )
    }

    private func alignToRaster(_ position: CGFloat, offset: CGFloat) -> CGFloat {
        ((position + offset) * displayScale).rounded() / displayScale - offset
    }
}
