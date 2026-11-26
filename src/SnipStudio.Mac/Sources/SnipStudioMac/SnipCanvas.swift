import AppKit
import SwiftUI

@MainActor
struct SnipCanvas: View {
    @ObservedObject var document: EditorDocument
    let inkColor: Color
    let inkWidth: CGFloat
    let zoom: CGFloat?
    let allowsDrawing: Bool
    let activeShape: SnipShape?
    let lineStyle: SnipLineStyle
    let erasesAnnotations: Bool
    let cropSelection: Binding<CGRect>?
    let annotationSelection: Binding<UUID?>?
    let textDraft: Binding<StudioTextDraft?>?
    let onTextTap: ((CGPoint) -> Void)?
    let recognizedLines: [RecognizedLine]
    let ocrSelection: Binding<Set<Int>>?
    let onZoomChanged: (CGFloat) -> Void

    @Environment(\.displayScale) private var displayScale
    @GestureState private var dragActive = false
    @State private var lastEraseLocation: CGPoint?
    @State private var annotationDrag: AnnotationDrag?
    @State private var textSelectionFrame: CGRect?
    @Namespace private var viewport

    init(document: EditorDocument, inkColor: Color, inkWidth: CGFloat, zoom: CGFloat? = nil,
         allowsDrawing: Bool = true, activeShape: SnipShape? = nil, lineStyle: SnipLineStyle = .solid,
         erasesAnnotations: Bool = false, cropSelection: Binding<CGRect>? = nil,
         annotationSelection: Binding<UUID?>? = nil, textDraft: Binding<StudioTextDraft?>? = nil,
         onTextTap: ((CGPoint) -> Void)? = nil, recognizedLines: [RecognizedLine] = [], ocrSelection: Binding<Set<Int>>? = nil,
         onZoomChanged: @escaping (CGFloat) -> Void = { _ in }) {
        _document = ObservedObject(wrappedValue: document)
        self.inkColor = inkColor
        self.inkWidth = inkWidth
        self.zoom = zoom
        self.allowsDrawing = allowsDrawing
        self.activeShape = activeShape
        self.lineStyle = lineStyle
        self.erasesAnnotations = erasesAnnotations
        self.cropSelection = cropSelection
        self.annotationSelection = annotationSelection
        self.textDraft = textDraft
        self.onTextTap = onTextTap
        self.recognizedLines = recognizedLines
        self.ocrSelection = ocrSelection
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
                if !dragActive {
                    document.cancelStroke()
                    lastEraseLocation = nil
                    annotationDrag = nil
                }
            }
        }
        .onDisappear {
            document.cancelStroke()
            if annotationSelection != nil { NSCursor.arrow.set() }
        }
        .onChange(of: zoom) { _, _ in
            document.cancelStroke()
            annotationDrag = nil
            textSelectionFrame = nil
        }
        .onChange(of: annotationSelection != nil) { _, selecting in
            if !selecting {
                annotationDrag = nil
                NSCursor.arrow.set()
            }
        }
        .onChange(of: allowsDrawing) { _, enabled in
            if !enabled { document.cancelStroke() }
        }
    }

    private func raster(_ image: CGImage, frame: CGRect, layout: SnipViewportLayout) -> some View {
        let editedID = textDraft?.wrappedValue?.annotationID
        let strokes = document.strokes
        let draft = document.draftStroke
        let selected = annotationSelection?.wrappedValue
        let drag = annotationDrag
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
                for stroke in strokes where stroke.id != editedID {
                    let offset = drag?.id == stroke.id ? drag?.translation ?? .zero : .zero
                    var layer = ink
                    layer.translateBy(x: offset.width, y: offset.height)
                    layer.fill(Path(stroke.outline), with: .color(Color(cgColor: stroke.color)))
                    if selected == stroke.id {
                        let padding = 4 / layout.scale
                        let bounds = Path(stroke.bounds.insetBy(dx: -padding, dy: -padding))
                        layer.stroke(bounds, with: .color(.white), lineWidth: 2 / layout.scale)
                        layer.stroke(bounds, with: .color(StudioColors.primary), style: StrokeStyle(
                            lineWidth: 1 / layout.scale, dash: [4 / layout.scale, 3 / layout.scale]
                        ))
                    }
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
            .gesture(inkGesture(in: frame, layout: layout), including: allowsDrawing || annotationSelection != nil ? .all : .none)
            .gesture(SpatialTapGesture().onEnded { event in
                guard frame.contains(event.location), textDraft?.wrappedValue == nil else { return }
                onTextTap?(layout.imagePoint(event.location, in: frame))
            }, including: onTextTap == nil ? .none : .all)
            .gesture(textSelectionGesture(in: frame), including: ocrSelection == nil || recognizedLines.isEmpty ? .none : .all)
            .onContinuousHover { phase in
                guard annotationSelection != nil else { return }
                switch phase {
                case let .active(location):
                    let hit = frame.contains(location) && document.annotation(
                        at: layout.imagePoint(location, in: frame), tolerance: 5 / layout.scale
                    ) != nil
                    if hit || annotationDrag != nil { Self.moveCursor.set() } else { NSCursor.arrow.set() }
                case .ended: NSCursor.arrow.set()
                }
            }

            if !recognizedLines.isEmpty {
                OCRRegionOverlay(lines: recognizedLines, imageFrame: frame)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
                Canvas { context, _ in
                    for index in ocrSelection?.wrappedValue ?? [] where recognizedLines.indices.contains(index) {
                        let box = OCRRegionOverlay.displayBounds(for: recognizedLines[index], in: frame)
                        guard !box.isEmpty else { continue }
                        let outline = Path(roundedRect: box.insetBy(dx: -2, dy: -2).intersection(frame), cornerRadius: 2)
                        context.fill(outline, with: .color(StudioColors.primary.opacity(0.3)))
                        context.stroke(outline, with: .color(.white), lineWidth: 2)
                        context.stroke(outline, with: .color(StudioColors.primary), lineWidth: 1)
                    }
                    if let textSelectionFrame {
                        let outline = Path(textSelectionFrame)
                        context.fill(outline, with: .color(StudioColors.primary.opacity(0.12)))
                        context.stroke(outline, with: .color(.white), lineWidth: 2)
                        context.stroke(outline, with: .color(StudioColors.primary), style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                    }
                }
                .allowsHitTesting(false)
                .accessibilityHidden(true)
            }
            if let cropSelection {
                StudioCropOverlay(bounds: cropSelection, imageFrame: frame,
                                  pixelSize: CGSize(width: image.width, height: image.height))
            }
            if let textDraft, let draft = Binding(textDraft) {
                GeometryReader { viewport in
                    let position = draft.wrappedValue.origin
                    let note = draft.wrappedValue.note
                    let lineHeight = max(20, note.pixelSize * layout.scale * 1.35)
                    let rows = note.text.components(separatedBy: .newlines).count
                    let width = min(400, viewport.size.width)
                    let height = min(164, viewport.size.height, max(40, CGFloat(min(rows, 7)) * lineHeight + 14))
                    let x = max(0, min(viewport.size.width - width, frame.minX + position.x * layout.scale))
                    let y = max(0, min(viewport.size.height - height, frame.minY + position.y * layout.scale))
                    StudioInlineTextEditor(draft: draft, frame: CGRect(x: x, y: y, width: width, height: height), scale: layout.scale)
                }
            }
        }
    }

    private func inkGesture(in frame: CGRect, layout: SnipViewportLayout) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .updating($dragActive) { _, active, _ in active = true }
            .onChanged { event in
                if let annotationSelection {
                    moveAnnotation(for: event, in: frame, layout: layout, selected: annotationSelection)
                } else if erasesAnnotations {
                    eraseAnnotations(for: event, in: frame, layout: layout)
                } else {
                    addInk(for: event, in: frame, layout: layout)
                }
            }
            .onEnded { event in
                // A click can complete without a distinct drag update.
                if let annotationSelection {
                    moveAnnotation(for: event, in: frame, layout: layout, selected: annotationSelection)
                    if let annotationDrag, annotationDrag.revision == document.contentRevision {
                        document.translateAnnotation(id: annotationDrag.id, by: annotationDrag.translation)
                    }
                    annotationDrag = nil
                } else if erasesAnnotations {
                    eraseAnnotations(for: event, in: frame, layout: layout)
                    lastEraseLocation = nil
                } else {
                    addInk(for: event, in: frame, layout: layout)
                    document.finishStroke()
                }
            }
    }

    private func addInk(for event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout) {
        if !document.isDrawing {
            guard frame.contains(event.startLocation) else { return }
            let color = NSColor(inkColor).usingColorSpace(.sRGB)?.cgColor ?? NSColor.systemRed.cgColor
            let start = layout.imagePoint(event.startLocation, in: frame)
            if let activeShape {
                document.beginShape(at: start, color: color, width: inkWidth, shape: activeShape, lineStyle: lineStyle)
            } else {
                document.beginStroke(at: start, color: color, width: inkWidth)
            }
        }
        document.extendStroke(to: layout.imagePoint(event.location, in: frame))
    }

    private func eraseAnnotations(for event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout) {
        guard frame.contains(event.location), frame.contains(event.startLocation) else { return }
        if let lastEraseLocation, hypot(event.location.x - lastEraseLocation.x, event.location.y - lastEraseLocation.y) < 4 {
            return
        }
        lastEraseLocation = event.location
        document.eraseAnnotation(at: layout.imagePoint(event.location, in: frame), tolerance: 4 / layout.scale)
    }

    private func moveAnnotation(for event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout,
                                selected: Binding<UUID?>) {
        if annotationDrag == nil {
            guard frame.contains(event.startLocation), let stroke = document.annotation(
                at: layout.imagePoint(event.startLocation, in: frame), tolerance: 5 / layout.scale
            ) else {
                selected.wrappedValue = nil
                return
            }
            selected.wrappedValue = stroke.id
            annotationDrag = AnnotationDrag(id: stroke.id, revision: document.contentRevision)
        }
        annotationDrag?.translation = CGSize(width: event.translation.width / layout.scale,
                                             height: event.translation.height / layout.scale)
    }

    private func textSelectionGesture(in frame: CGRect) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { event in
                if hypot(event.translation.width, event.translation.height) >= 4 {
                    textSelectionFrame = CGRect(x: event.startLocation.x, y: event.startLocation.y,
                                                width: event.translation.width, height: event.translation.height)
                        .standardized.intersection(frame)
                }
            }
            .onEnded { event in
                defer { textSelectionFrame = nil }
                guard let ocrSelection else { return }
                let addToSelection = NSEvent.modifierFlags.contains(.shift)
                var selected = addToSelection ? ocrSelection.wrappedValue : []
                if let textSelectionFrame {
                    for index in recognizedLines.indices where OCRRegionOverlay.displayBounds(
                        for: recognizedLines[index], in: frame
                    ).intersects(textSelectionFrame) { selected.insert(index) }
                } else if let hit = recognizedLines.firstIndex(where: {
                    OCRRegionOverlay.displayBounds(for: $0, in: frame).insetBy(dx: -3, dy: -3).contains(event.location)
                }) {
                    if addToSelection && selected.contains(hit) { selected.remove(hit) } else { selected.insert(hit) }
                }
                ocrSelection.wrappedValue = selected
            }
    }

    private struct AnnotationDrag {
        let id: UUID
        let revision: UUID
        var translation = CGSize.zero
    }

    private static let moveCursor: NSCursor = {
        guard let image = NSImage(systemSymbolName: "arrow.up.and.down.and.arrow.left.and.right",
                                  accessibilityDescription: nil) else { return .openHand }
        return NSCursor(image: image, hotSpot: CGPoint(x: image.size.width / 2, y: image.size.height / 2))
    }()
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
