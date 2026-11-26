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
    let shapeFill: Color?
    let erasesAnnotations: Bool
    let cropSelection: Binding<CGRect>?
    let annotationSelection: Binding<UUID?>?
    let onAutomaticMove: ((UUID) -> Void)?
    let onTransformError: ((Error) -> Void)?
    let textDraft: Binding<StudioTextDraft?>?
    let onTextTap: ((CGPoint) -> Void)?
    let recognizedLines: [RecognizedLine]
    let ocrSelection: Binding<Set<Int>>?
    let selectableOCRBounds: [CGRect]
    let snapWordRegions: [CGRect]
    let showsRecognitionGlow: Bool
    let onZoomChanged: (CGFloat) -> Void

    @Environment(\.displayScale) private var displayScale
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @GestureState private var dragActive = false
    @State private var lastEraseLocation: CGPoint?
    @State private var annotationDrag: AnnotationDrag?
    @State private var textSelectionFrame: CGRect?
    @State private var snappedWordIndices = Set<Int>()
    @State private var lastSnapLocation: CGPoint?
    @State private var panningDrag = false
    @Namespace private var viewport

    init(document: EditorDocument, inkColor: Color, inkWidth: CGFloat, zoom: CGFloat? = nil,
         allowsDrawing: Bool = true, activeShape: SnipShape? = nil, lineStyle: SnipLineStyle = .solid, shapeFill: Color? = nil,
         erasesAnnotations: Bool = false, cropSelection: Binding<CGRect>? = nil,
         annotationSelection: Binding<UUID?>? = nil, onAutomaticMove: ((UUID) -> Void)? = nil,
         onTransformError: ((Error) -> Void)? = nil,
         textDraft: Binding<StudioTextDraft?>? = nil,
         onTextTap: ((CGPoint) -> Void)? = nil, recognizedLines: [RecognizedLine] = [], ocrSelection: Binding<Set<Int>>? = nil,
         selectableOCRBounds: [CGRect] = [], snapWordRegions: [CGRect] = [], showsRecognitionGlow: Bool = false,
         onZoomChanged: @escaping (CGFloat) -> Void = { _ in }) {
        _document = ObservedObject(wrappedValue: document)
        self.inkColor = inkColor
        self.inkWidth = inkWidth
        self.zoom = zoom
        self.allowsDrawing = allowsDrawing
        self.activeShape = activeShape
        self.lineStyle = lineStyle
        self.shapeFill = shapeFill
        self.erasesAnnotations = erasesAnnotations
        self.cropSelection = cropSelection
        self.annotationSelection = annotationSelection
        self.onAutomaticMove = onAutomaticMove
        self.onTransformError = onTransformError
        self.textDraft = textDraft
        self.onTextTap = onTextTap
        self.recognizedLines = recognizedLines
        self.ocrSelection = ocrSelection
        self.selectableOCRBounds = selectableOCRBounds
        self.snapWordRegions = snapWordRegions
        self.showsRecognitionGlow = showsRecognitionGlow
        self.onZoomChanged = onZoomChanged
    }

    var body: some View {
        GeometryReader { proxy in
            if let image = document.image,
               let layout = SnipViewportLayout(
                   imageSize: CGSize(width: CGFloat(image.width), height: CGFloat(image.height)), viewport: proxy.size,
                   zoom: zoom, displayScale: displayScale
               ) {
                StudioCanvasScroll { pan in
                    GeometryReader { surface in
                        let origin = surface.frame(in: .named(viewport)).origin
                        let imageFrame = layout.imageFrame(
                            surfaceOrigin: origin, viewportOrigin: proxy.frame(in: .global).origin
                        )
                        // The drawing surface stays the size of the visible viewport at every zoom.
                        raster(image, frame: imageFrame, layout: layout, pan: pan)
                            .frame(width: proxy.size.width, height: proxy.size.height)
                            .position(x: proxy.size.width / 2 - origin.x, y: proxy.size.height / 2 - origin.y)
                    }
                    .frame(width: layout.surfaceSize.width, height: layout.surfaceSize.height)
                }
                .coordinateSpace(name: viewport)
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
                    resetSnapDrag()
                    panningDrag = false
                }
            }
        }
        .onDisappear {
            document.cancelStroke()
            if annotationSelection != nil || onAutomaticMove != nil { NSCursor.arrow.set() }
        }
        .onChange(of: zoom) { _, _ in
            document.cancelStroke()
            annotationDrag = nil
            textSelectionFrame = nil
            resetSnapDrag()
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

    private func raster(_ image: CGImage, frame: CGRect, layout: SnipViewportLayout, pan: StudioPanAction?) -> some View {
        let editedID = textDraft?.wrappedValue?.annotationID
        let strokes = document.strokes
        let draft = document.draftStroke
        let selected = annotationSelection?.wrappedValue ?? annotationDrag?.id
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
                    var layer = ink
                    if let drag, drag.id == stroke.id {
                        if let resize = drag.resize {
                            layer.translateBy(x: resize.anchor.x, y: resize.anchor.y)
                            layer.scaleBy(x: drag.factor, y: drag.factor)
                            layer.translateBy(x: -resize.anchor.x, y: -resize.anchor.y)
                        } else {
                            layer.translateBy(x: drag.translation.width, y: drag.translation.height)
                        }
                    }
                    draw(stroke, in: layer)
                    if selected == stroke.id {
                        let scale = layout.scale * (drag?.id == stroke.id ? drag?.factor ?? 1 : 1)
                        let padding = 4 / scale
                        let bounds = Path(stroke.bounds.insetBy(dx: -padding, dy: -padding))
                        layer.stroke(bounds, with: .color(.white), lineWidth: 2 / scale)
                        layer.stroke(bounds, with: .color(StudioColors.primary), style: StrokeStyle(
                            lineWidth: 1 / scale, dash: [4 / scale, 3 / scale]
                        ))
                        if annotationSelection != nil {
                            for grip in ResizeGrip.corners(of: stroke.bounds, padding: padding) {
                                let handle = Path(CGRect(x: grip.point.x - 3 / scale, y: grip.point.y - 3 / scale,
                                                         width: 6 / scale, height: 6 / scale))
                                layer.fill(handle, with: .color(.white))
                                layer.stroke(handle, with: .color(StudioColors.primary), lineWidth: 1 / scale)
                            }
                        }
                    }
                }
                if let draft, snappedWordIndices.isEmpty {
                    draw(draft, in: ink)
                } else {
                    for index in snappedWordIndices where snapWordRegions.indices.contains(index) {
                        let region = snapWordRegions[index]
                        ink.fill(Path(region), with: .color(inkColor))
                    }
                }

                let borderWidth = layout.usesNativePixels ? 1 / displayScale : 1
                let borderRect = frame.insetBy(dx: -borderWidth / 2, dy: -borderWidth / 2)
                context.stroke(Path(borderRect), with: .color(border), lineWidth: borderWidth)
            }
            .contentShape(Rectangle())
            .accessibilityLabel("Snip editor canvas")
            .gesture(inkGesture(in: frame, layout: layout, pan: pan), including: allowsDrawing || annotationSelection != nil ? .all : .none)
            .gesture(SpatialTapGesture().onEnded { event in
                guard frame.contains(event.location), textDraft?.wrappedValue == nil else { return }
                onTextTap?(layout.imagePoint(event.location, in: frame))
            }, including: onTextTap == nil ? .none : .all)
            .gesture(textSelectionGesture(in: frame), including: ocrSelection == nil || selectableOCRBounds.isEmpty ? .none : .all)
            .onContinuousHover { phase in
                guard annotationSelection != nil || onAutomaticMove != nil else { return }
                switch phase {
                case let .active(location):
                    let point = layout.imagePoint(location, in: frame)
                    if annotationDrag?.resize != nil || (frame.insetBy(dx: -10, dy: -10).contains(location) &&
                                                         resizeGrip(around: point, layout: layout) != nil) {
                        NSCursor.crosshair.set()
                        return
                    }
                    let hit = frame.contains(location) && (
                        annotationSelection != nil
                        ? document.annotation(at: point, tolerance: 5 / layout.scale) != nil
                        : !NSEvent.modifierFlags.contains(.option) && document.annotationOnPerimeter(at: point, tolerance: 5 / layout.scale) != nil
                    )
                    if hit || annotationDrag != nil { Self.moveCursor.set() } else { NSCursor.arrow.set() }
                case .ended: NSCursor.arrow.set()
                }
            }

            if !recognizedLines.isEmpty {
                OCRRegionOverlay(lines: recognizedLines, imageFrame: frame)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
                Canvas { context, _ in
                    for index in ocrSelection?.wrappedValue ?? [] where selectableOCRBounds.indices.contains(index) {
                        let box = OCRRegionOverlay.displayBounds(for: selectableOCRBounds[index], in: frame)
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
            if showsRecognitionGlow { recognitionGlow(around: frame) }
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

    private func recognitionGlow(around frame: CGRect) -> some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { time in
            let position = reduceMotion ? 0 : time.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.5) * 240
            let colors: [Color] = [.cyan, .blue, .purple, .pink, .cyan]
            RoundedRectangle(cornerRadius: 4)
                .strokeBorder(AngularGradient(colors: colors, center: .center,
                                              startAngle: .degrees(position), endAngle: .degrees(position + 360)), lineWidth: 3)
                .shadow(color: .blue.opacity(0.75), radius: 5)
                .frame(width: frame.width + 9, height: frame.height + 9)
                .position(x: frame.midX, y: frame.midY)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func draw(_ stroke: InkStroke, in ink: GraphicsContext) {
        if let bitmap = stroke.bitmap {
            var layer = ink
            layer.concatenate(stroke.bitmapTransform)
            layer.draw(Image(decorative: bitmap, scale: 1), in: stroke.bitmapFrame)
            return
        }
        if let fill = stroke.fillColor, let area = stroke.fillOutline {
            ink.fill(Path(area), with: .color(Color(cgColor: fill)))
        }
        ink.fill(Path(stroke.outline), with: .color(Color(cgColor: stroke.color)))
    }

    private func inkGesture(in frame: CGRect, layout: SnipViewportLayout, pan: StudioPanAction?) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .updating($dragActive) { _, active, _ in active = true }
            .onChanged { event in
                if let annotationSelection {
                    if panningDrag {
                        pan?(.changed(event.translation))
                        return
                    }
                    moveAnnotation(for: event, in: frame, layout: layout, selected: annotationSelection)
                    if annotationDrag == nil, let pan, hypot(event.translation.width, event.translation.height) >= 3 {
                        panningDrag = true
                        pan(.began)
                        pan(.changed(event.translation))
                    }
                } else if automaticMove(for: event, in: frame, layout: layout) {
                    return
                } else if erasesAnnotations {
                    eraseAnnotations(for: event, in: frame, layout: layout)
                } else {
                    addInk(for: event, in: frame, layout: layout)
                    trackSnappedWords(event, in: frame, layout: layout)
                }
            }
            .onEnded { event in
                // A click can complete without a distinct drag update.
                if let annotationSelection {
                    if panningDrag {
                        pan?(.ended)
                        panningDrag = false
                        return
                    }
                    moveAnnotation(for: event, in: frame, layout: layout, selected: annotationSelection)
                    finishAnnotationMove(automatic: false)
                } else if automaticMove(for: event, in: frame, layout: layout) {
                    finishAnnotationMove(automatic: true)
                } else if erasesAnnotations {
                    eraseAnnotations(for: event, in: frame, layout: layout)
                    lastEraseLocation = nil
                } else {
                    addInk(for: event, in: frame, layout: layout)
                    trackSnappedWords(event, in: frame, layout: layout)
                    let snapped = snappedWordIndices.sorted().compactMap { index in
                        snapWordRegions.indices.contains(index) ? snapWordRegions[index] : nil
                    }
                    if snapped.isEmpty {
                        let placed = document.finishStroke()
                        if activeShape != nil, let placed { onAutomaticMove?(placed) }
                    } else {
                        document.cancelStroke()
                        let color = NSColor(inkColor).usingColorSpace(.sRGB)?.cgColor ?? NSColor.systemYellow.withAlphaComponent(0.42).cgColor
                        document.highlightRegions(snapped, color: color)
                    }
                    resetSnapDrag()
                }
            }
    }

    private func trackSnappedWords(_ event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout) {
        guard !snapWordRegions.isEmpty, frame.contains(event.startLocation), !NSEvent.modifierFlags.contains(.shift) else {
            resetSnapDrag()
            return
        }
        let start = lastSnapLocation ?? layout.imagePoint(event.startLocation, in: frame)
        let end = layout.imagePoint(event.location, in: frame)
        let radius = max(1, inkWidth / 3)
        let sample = CGRect(x: start.x, y: start.y, width: end.x - start.x, height: end.y - start.y)
            .standardized.insetBy(dx: -radius, dy: -radius)
        var candidates = snappedWordIndices
        for index in snapWordRegions.indices where snapWordRegions[index].intersects(sample) {
            candidates.insert(index)
        }
        if candidates != snappedWordIndices { snappedWordIndices = candidates }
        lastSnapLocation = end
    }

    private func resetSnapDrag() {
        if lastSnapLocation != nil { lastSnapLocation = nil }
        if !snappedWordIndices.isEmpty { snappedWordIndices.removeAll() }
    }

    private func addInk(for event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout) {
        if !document.isDrawing {
            guard frame.contains(event.startLocation) else { return }
            let color = NSColor(inkColor).usingColorSpace(.sRGB)?.cgColor ?? NSColor.systemRed.cgColor
            let start = layout.imagePoint(event.startLocation, in: frame)
            if let activeShape {
                let fill = shapeFill.flatMap { NSColor($0).usingColorSpace(.sRGB)?.cgColor }
                document.beginShape(at: start, color: color, width: inkWidth, shape: activeShape, lineStyle: lineStyle, fill: fill)
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
            let imagePoint = layout.imagePoint(event.startLocation, in: frame)
            if frame.insetBy(dx: -10, dy: -10).contains(event.startLocation),
               let id = selected.wrappedValue, let stroke = document.annotation(withID: id),
               let grip = resizeGrip(around: imagePoint, layout: layout) {
                let nativeSize = stroke.textNote.map { CGFloat($0.pixelSize) } ?? stroke.width
                let minimumSize: CGFloat = stroke.textNote != nil || stroke.emoji != nil ? 1 : 0.1
                annotationDrag = AnnotationDrag(id: id, revision: document.contentRevision, resize: grip,
                                                minFactor: min(1, max(0.12, minimumSize / nativeSize)),
                                                maxFactor: max(1, min(8, 512 / nativeSize)))
            } else {
                guard frame.contains(event.startLocation), let stroke = document.annotation(
                    at: imagePoint, tolerance: 5 / layout.scale
                ) else {
                    selected.wrappedValue = nil
                    return
                }
                selected.wrappedValue = stroke.id
                annotationDrag = AnnotationDrag(id: stroke.id, revision: document.contentRevision)
            }
        }
        let movement = CGSize(width: event.translation.width / layout.scale, height: event.translation.height / layout.scale)
        if var drag = annotationDrag, let resize = drag.resize {
            let x = resize.point.x - resize.anchor.x
            let y = resize.point.y - resize.anchor.y
            let dot = (x * movement.width + y * movement.height) / max(1, x * x + y * y)
            drag.factor = max(drag.minFactor, min(drag.maxFactor, 1 + dot))
            annotationDrag = drag
        } else {
            annotationDrag?.translation = movement
        }
    }

    private func resizeGrip(around point: CGPoint, layout: SnipViewportLayout) -> ResizeGrip? {
        guard let id = annotationSelection?.wrappedValue, let stroke = document.annotation(withID: id) else { return nil }
        let handlePadding = 4 / layout.scale
        let nearest = ResizeGrip.corners(of: stroke.bounds, padding: handlePadding)
            .map { ($0, hypot($0.point.x - point.x, $0.point.y - point.y)) }
            .min { $0.1 < $1.1 }
        return nearest.flatMap { $0.1 <= 6 / layout.scale ? $0.0 : nil }
    }

    private func automaticMove(for event: DragGesture.Value, in frame: CGRect, layout: SnipViewportLayout) -> Bool {
        guard onAutomaticMove != nil, !document.isDrawing, frame.contains(event.startLocation) else { return false }
        if annotationDrag == nil {
            guard !NSEvent.modifierFlags.contains(.option), let stroke = document.annotationOnPerimeter(
                at: layout.imagePoint(event.startLocation, in: frame), tolerance: 5 / layout.scale
            ) else { return false }
            annotationDrag = AnnotationDrag(id: stroke.id, revision: document.contentRevision)
        }
        annotationDrag?.translation = CGSize(width: event.translation.width / layout.scale,
                                             height: event.translation.height / layout.scale)
        return true
    }

    private func finishAnnotationMove(automatic: Bool) {
        defer { annotationDrag = nil }
        guard let drag = annotationDrag, drag.revision == document.contentRevision else { return }
        if let resize = drag.resize {
            do { try document.resizeAnnotation(id: drag.id, factor: drag.factor, around: resize.anchor) }
            catch { onTransformError?(error) }
        } else {
            document.translateAnnotation(id: drag.id, by: drag.translation)
        }
        if automatic { onAutomaticMove?(drag.id) }
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
                    for index in selectableOCRBounds.indices where OCRRegionOverlay.displayBounds(
                        for: selectableOCRBounds[index], in: frame
                    ).intersects(textSelectionFrame) { selected.insert(index) }
                } else if let hit = selectableOCRBounds.firstIndex(where: {
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
        var resize: ResizeGrip? = nil
        var minFactor: CGFloat = 0.12
        var maxFactor: CGFloat = 8
        var factor: CGFloat = 1
        var translation = CGSize.zero
    }

    private struct ResizeGrip {
        let point: CGPoint
        let anchor: CGPoint

        static func corners(of box: CGRect, padding: CGFloat) -> [ResizeGrip] {
            let outer = box.insetBy(dx: -padding, dy: -padding)
            let positions = [CGPoint(x: outer.minX, y: outer.minY), CGPoint(x: outer.maxX, y: outer.minY),
                             CGPoint(x: outer.maxX, y: outer.maxY), CGPoint(x: outer.minX, y: outer.maxY)]
            let anchors = [CGPoint(x: box.maxX, y: box.maxY), CGPoint(x: box.minX, y: box.maxY),
                           CGPoint(x: box.minX, y: box.minY), CGPoint(x: box.maxX, y: box.minY)]
            return zip(positions, anchors).map { ResizeGrip(point: $0.0, anchor: $0.1) }
        }
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
