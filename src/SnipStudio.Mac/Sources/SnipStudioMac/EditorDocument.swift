import AppKit
import Combine
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum SnipShape: String, CaseIterable, Sendable {
    case rectangle, ellipse, line, arrow
}

enum SnipLineStyle: String, CaseIterable, Sendable {
    case solid, dashed, dotted
}

struct InkStroke: Identifiable, Sendable {
    let id = UUID()
    private(set) var color: CGColor
    private(set) var width: CGFloat
    private(set) var points: [CGPoint]
    let shape: SnipShape?
    private(set) var textNote: StudioTextAnnotation?
    private(set) var fillColor: CGColor?
    private(set) var emoji: String?
    private(set) var bitmap: CGImage?
    private(set) var highlightBands: [CGRect]?
    private(set) var lineStyle: SnipLineStyle
    private var quarterTurns = 0
    private var rotationDegrees: CGFloat?
    private var emojiFrameSize: CGSize?
    private var committedOutline: CommittedOutline?

    var bounds: CGRect { committedOutline?.bounds ?? outline.boundingBoxOfPath }
    var origin: CGPoint { points[0] }

    init(color: CGColor, width: CGFloat, at point: CGPoint,
         shape: SnipShape? = nil, lineStyle: SnipLineStyle = .solid, fill: CGColor? = nil) {
        self.color = color
        self.width = width
        points = [point]
        self.shape = shape
        self.lineStyle = lineStyle
        fillColor = fill
    }

    fileprivate init(note: StudioTextAnnotation, color: CGColor, at origin: CGPoint) {
        self.init(color: color, width: 1, at: origin)
        textNote = note
    }

    fileprivate init(emoji: String, size: CGFloat, at origin: CGPoint) throws {
        self.init(color: CGColor(gray: 0, alpha: 0), width: size, at: origin)
        self.emoji = emoji
        bitmap = try StudioEmojiRaster.image(for: emoji, pixelSize: size)
    }

    fileprivate init(projectRecord record: StudioProjectAnnotation) throws {
        guard let first = record.points.first, let lineStyle = SnipLineStyle(rawValue: record.lineStyle),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let color = CGColor(colorSpace: space, components: [record.red, record.green, record.blue, record.alpha]) else {
            throw EditorDocumentError.invalidProjectAnnotation
        }
        let shape = record.shape.flatMap(SnipShape.init(rawValue:))
        guard record.shape == nil || shape != nil else { throw EditorDocumentError.invalidProjectAnnotation }
        self.init(color: color, width: record.width, at: first, shape: shape, lineStyle: lineStyle)
        points = record.points
        quarterTurns = record.quarterTurns
        rotationDegrees = record.rotationDegrees
        emojiFrameSize = record.emojiFrameSize
        highlightBands = record.highlightBands
        textNote = record.text
        if let fill = record.fill {
            guard fill.count == 4, let restoredFill = CGColor(colorSpace: space, components: fill) else {
                throw EditorDocumentError.invalidProjectAnnotation
            }
            fillColor = restoredFill
        }
        if let emoji = record.emoji {
            self.emoji = emoji
            bitmap = try StudioEmojiRaster.image(for: emoji, pixelSize: width)
        }
        guard prepareForCommit() else { throw EditorDocumentError.invalidProjectAnnotation }
    }

    fileprivate func projectRecord() throws -> StudioProjectAnnotation {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB) else { throw EditorDocumentError.invalidProjectAnnotation }
        func components(inSRGB ink: CGColor) throws -> [CGFloat] {
            let values: [CGFloat]?
            if let current = ink.colorSpace, CFEqual(current, space) {
                values = ink.components
            } else {
                values = ink.converted(to: space, intent: .defaultIntent, options: nil)?.components
            }
            guard let values, values.count == 4 else { throw EditorDocumentError.invalidProjectAnnotation }
            return values
        }
        let components = try components(inSRGB: color)
        let fill = try fillColor.map { try components(inSRGB: $0) }
        return StudioProjectAnnotation(points: points, red: components[0], green: components[1],
                                       blue: components[2], alpha: components[3], width: width,
                                       shape: shape?.rawValue, lineStyle: lineStyle.rawValue,
                                       quarterTurns: quarterTurns, text: textNote,
                                       fill: fill,
                                       emoji: emoji, rotationDegrees: rotationDegrees, emojiFrameSize: emojiFrameSize,
                                       highlightBands: highlightBands)
    }

    mutating func extend(to point: CGPoint) {
        guard points.last != point else { return }
        committedOutline = nil
        if shape != nil && points.count == 2 {
            points[1] = point
        } else {
            points.append(point)
        }
    }

    mutating func prepareForCommit() -> Bool {
        if committedOutline != nil { return true }
        let rendered = outline
        guard !rendered.isEmpty, let immutable = rendered.copy() else { return false }
        committedOutline = CommittedOutline(path: immutable, bounds: immutable.boundingBoxOfPath)
        return true
    }

    func offset(by delta: CGSize) -> InkStroke {
        var copy = self
        copy.points = points.map { CGPoint(x: $0.x + delta.width, y: $0.y + delta.height) }
        if let highlightBands {
            copy.setHighlightBands(highlightBands.map { $0.offsetBy(dx: delta.width, dy: delta.height) })
        }
        copy.committedOutline = nil
        if let committedOutline {
            var translation = CGAffineTransform(translationX: delta.width, y: delta.height)
            if let path = committedOutline.path.copy(using: &translation) {
                copy.committedOutline = CommittedOutline(path: path, bounds: committedOutline.bounds.offsetBy(dx: delta.width, dy: delta.height))
            }
        }
        return copy
    }

    fileprivate func rotated(clockwise: Bool) -> InkStroke {
        var copy = self
        if let rotationDegrees {
            let rotated = rotationDegrees + (clockwise ? 90 : -90)
            if rotated > 180 { copy.rotationDegrees = rotated - 360 }
            else if rotated < -180 { copy.rotationDegrees = rotated + 360 }
            else { copy.rotationDegrees = rotated }
        } else {
            copy.quarterTurns = (quarterTurns + (clockwise ? 1 : 3)) % 4
        }
        copy.committedOutline = nil
        return copy
    }

    fileprivate func replacingText(with note: StudioTextAnnotation, color: CGColor) -> InkStroke {
        var copy = self
        copy.textNote = note
        copy.color = color
        copy.committedOutline = nil
        return copy
    }

    fileprivate func filled(with color: CGColor?) -> InkStroke? {
        guard shape == .rectangle || shape == .ellipse else { return nil }
        if let fillColor, let color, CFEqual(fillColor, color) { return nil }
        if fillColor == nil && color == nil { return nil }
        var copy = self
        copy.fillColor = color
        return copy
    }

    fileprivate func scaled(by factor: CGFloat, around anchor: CGPoint? = nil) throws -> InkStroke? {
        guard factor.isFinite, factor > 0, factor != 1 else { return nil }
        var copy = self
        copy.committedOutline = nil
        let pivot = anchor ?? CGPoint(x: bounds.midX, y: bounds.midY)
        copy.points = points.map { CGPoint(x: pivot.x + ($0.x - pivot.x) * factor,
                                           y: pivot.y + ($0.y - pivot.y) * factor) }
        if let highlightBands {
            copy.setHighlightBands(highlightBands.map { band in
                CGRect(x: pivot.x + (band.minX - pivot.x) * factor, y: pivot.y + (band.minY - pivot.y) * factor,
                       width: band.width * factor, height: band.height * factor)
            })
        } else if var note = textNote {
            note.pixelSize *= factor
            guard StudioTextAnnotation.pixelSizeRange.contains(note.pixelSize) else { return nil }
            copy.textNote = note
        } else if let emoji {
            if let frame = emojiFrameSize {
                let scaled = CGSize(width: frame.width * factor, height: frame.height * factor)
                guard scaled.width > 0, scaled.height > 0, scaled.width <= 32_768, scaled.height <= 32_768 else { return nil }
                copy.emojiFrameSize = scaled
                copy.width = min(512, max(1, copy.width * factor))
            } else {
                copy.width *= factor
            }
            copy.bitmap = try StudioEmojiRaster.image(for: emoji, pixelSize: copy.width)
        } else {
            copy.width *= factor
            guard (0.1...512).contains(copy.width) else { return nil }
        }
        return copy
    }

    private mutating func setHighlightBands(_ rectangles: [CGRect]) {
        highlightBands = rectangles
        let box = rectangles.reduce(CGRect.null) { $0.union($1) }
        points = [box.origin, CGPoint(x: box.maxX, y: box.maxY)]
    }

    var bitmapFrame: CGRect {
        guard let bitmap else { return .null }
        return CGRect(origin: origin, size: emojiFrameSize ?? CGSize(width: bitmap.width, height: bitmap.height))
    }

    var bitmapDrawingFrame: CGRect {
        let frame = bitmapFrame
        guard emojiFrameSize != nil, !frame.isNull else { return frame }
        let side = min(frame.width, frame.height)
        return CGRect(x: frame.midX - side / 2, y: frame.midY - side / 2, width: side, height: side)
    }

    var paintTransform: CGAffineTransform { rotationTransform }

    var fillOutline: CGPath? {
        guard fillColor != nil, let shape, (shape == .rectangle || shape == .ellipse), let last = points.last else { return nil }
        let area = CGRect(x: origin.x, y: origin.y, width: last.x - origin.x, height: last.y - origin.y).standardized
        guard !area.isEmpty else { return nil }
        var transform = rotationTransform
        if shape == .rectangle { return CGPath(rect: area, transform: &transform) }
        return CGPath(ellipseIn: area, transform: &transform)
    }

    fileprivate func restyled(color: CGColor?, width: CGFloat?, lineStyle: SnipLineStyle?) -> InkStroke? {
        let newColor = color ?? self.color
        let newWidth = highlightBands == nil ? width ?? self.width : self.width
        let newLineStyle = highlightBands == nil ? lineStyle ?? self.lineStyle : self.lineStyle
        guard newWidth.isFinite, newWidth > 0,
              !CFEqual(newColor, self.color) || newWidth != self.width || newLineStyle != self.lineStyle else { return nil }
        var copy = self
        copy.color = newColor
        copy.width = newWidth
        copy.lineStyle = newLineStyle
        copy.committedOutline = nil
        return copy
    }

    // Both renderers consume the same image-space outline.
    var outline: CGPath {
        if let committedOutline { return committedOutline.path }
        let path = unrotatedOutline
        guard quarterTurns != 0 || rotationDegrees != nil else { return path }
        var rotation = rotationTransform
        return path.copy(using: &rotation) ?? path
    }

    private var unrotatedOutline: CGPath {
        let path = CGMutablePath()
        let first = points[0]
        if let highlightBands {
            for band in highlightBands { path.addRect(band) }
            return path
        }
        if bitmap != nil { return CGPath(rect: bitmapFrame, transform: nil) }
        if let textNote { return textNote.makeOutline(at: first) }
        if let shape { return shapeOutline(shape, from: first, to: points[points.count - 1]) }
        if points.count == 1 {
            let radius = width / 2
            path.addEllipse(in: CGRect(
                x: first.x - radius, y: first.y - radius, width: width, height: width
            ))
            return path
        }

        path.addLines(between: points)
        return stroked(path, cap: .round)
    }

    private var rotationTransform: CGAffineTransform {
        guard quarterTurns != 0 || rotationDegrees != nil else { return .identity }
        let start = points[0]
        var minimum = start
        var maximum = start
        for point in points.dropFirst() {
            minimum.x = min(minimum.x, point.x)
            minimum.y = min(minimum.y, point.y)
            maximum.x = max(maximum.x, point.x)
            maximum.y = max(maximum.y, point.y)
        }
        let textBounds = bitmap == nil ? textNote?.bounds(at: start) : bitmapFrame
        let x = textBounds?.midX ?? minimum.x + (maximum.x - minimum.x) / 2
        let y = textBounds?.midY ?? minimum.y + (maximum.y - minimum.y) / 2
        if let rotationDegrees {
            let radians = rotationDegrees * .pi / 180
            let cosine = cos(radians)
            let sine = sin(radians)
            return CGAffineTransform(a: cosine, b: sine, c: -sine, d: cosine,
                                     tx: x - x * cosine + y * sine, ty: y - x * sine - y * cosine)
        }
        switch quarterTurns {
        case 1: return CGAffineTransform(a: 0, b: 1, c: -1, d: 0, tx: x + y, ty: y - x)
        case 2: return CGAffineTransform(a: -1, b: 0, c: 0, d: -1, tx: 2 * x, ty: 2 * y)
        case 3: return CGAffineTransform(a: 0, b: -1, c: 1, d: 0, tx: x - y, ty: x + y)
        default: return .identity
        }
    }

    func contains(_ point: CGPoint, tolerance: CGFloat) -> Bool {
        guard bounds.insetBy(dx: -tolerance, dy: -tolerance).contains(point) else { return false }
        if fillOutline?.contains(point) == true { return true }
        return outlineContains(point, tolerance: tolerance)
    }

    func isNearPerimeter(_ point: CGPoint, tolerance: CGFloat) -> Bool {
        let outline = bounds
        guard outline.insetBy(dx: -tolerance, dy: -tolerance).contains(point) else { return false }
        if bitmap != nil || textNote != nil {
            return !outline.insetBy(dx: tolerance, dy: tolerance).contains(point)
        }
        return outlineContains(point, tolerance: tolerance)
    }

    private func outlineContains(_ point: CGPoint, tolerance: CGFloat) -> Bool {
        let path = outline
        if path.contains(point) { return true }
        guard tolerance > 0 else { return false }
        let border = path.copy(strokingWithWidth: 2 * tolerance, lineCap: .round, lineJoin: .round, miterLimit: 1)
        return border.contains(point)
    }

    private func shapeOutline(_ shape: SnipShape, from start: CGPoint, to end: CGPoint) -> CGPath {
        let path = CGMutablePath()
        guard start != end else { return path }
        let cap: CGLineCap = lineStyle == .dotted ? .round : .butt
        switch shape {
        case .line:
            path.addLines(between: [start, end])
        case .arrow:
            return arrowOutline(from: start, to: end, cap: cap)
        case .rectangle, .ellipse:
            let bounds = CGRect(x: min(start.x, end.x), y: min(start.y, end.y),
                                width: abs(end.x - start.x), height: abs(end.y - start.y))
            guard !bounds.isEmpty else { return path }
            if shape == .rectangle {
                path.addRect(bounds)
            } else {
                path.addEllipse(in: bounds)
            }
        }
        return stroked(path, cap: cap)
    }

    private func arrowOutline(from start: CGPoint, to end: CGPoint, cap: CGLineCap) -> CGPath {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let length = hypot(dx, dy)
        let unitX = dx / length
        let unitY = dy / length
        // Match the Windows editor's filled arrowhead proportions, including short arrows.
        let capWidth = min(width, length / 5)
        let base = CGPoint(x: end.x - unitX * (5 * capWidth), y: end.y - unitY * (5 * capWidth))
        let halfHead = 2 * capWidth
        let left = CGPoint(x: base.x + unitY * halfHead, y: base.y - unitX * halfHead)
        let right = CGPoint(x: base.x - unitY * halfHead, y: base.y + unitX * halfHead)
        let path = CGMutablePath()
        path.addLines(between: [left, end, right])
        path.closeSubpath()

        let shaftLength = length - 5 * capWidth - (cap == .round ? width / 2 : 0)
        if shaftLength > 0 {
            let shaftEnd = CGPoint(x: start.x + unitX * shaftLength, y: start.y + unitY * shaftLength)
            let shaft = CGMutablePath()
            shaft.addLines(between: [start, shaftEnd])
            path.addPath(stroked(shaft, cap: cap))
        }
        return path
    }

    private func stroked(_ path: CGPath, cap: CGLineCap) -> CGPath {
        let centerline: CGPath
        switch lineStyle {
        case .solid:
            centerline = path
        case .dashed:
            centerline = path.copy(dashingWithPhase: 0, lengths: [3 * width, 2 * width])
        case .dotted:
            centerline = path.copy(dashingWithPhase: 0, lengths: [0.2 * width, 2.5 * width])
        }
        return centerline.copy(strokingWithWidth: width, lineCap: cap, lineJoin: .round, miterLimit: 1)
    }

    // The shared path is an immutable Core Graphics copy that is only read during rendering.
    private struct CommittedOutline: @unchecked Sendable {
        let path: CGPath
        let bounds: CGRect
    }
}

enum EditorDocumentError: LocalizedError {
    case noImage
    case unreadableImage
    case imageTooLarge
    case cropFailed
    case renderFailed
    case encodeFailed
    case unsupportedText
    case invalidProjectAnnotation
    case rasterChanged
    case overlappingOCRText
    case documentChangedDuringOpen

    var errorDescription: String? {
        switch self {
        case .noImage: "Capture or open a snip first."
        case .unreadableImage: "The image could not be opened."
        case .imageTooLarge: "This image is too large to edit. Choose an image up to 64 million pixels."
        case .cropFailed: "The selected area could not be cropped."
        case .renderFailed: "The edited snip could not be rendered."
        case .encodeFailed: "The snip could not be encoded as a PNG."
        case .unsupportedText: "Enter text that the selected font can display. Bitmap-only emoji and unsupported characters can’t be applied as editable text."
        case .invalidProjectAnnotation: "The editable snip contains an annotation that could not be restored safely."
        case .rasterChanged: "The screenshot changed before this action finished. Try again."
        case .overlappingOCRText: "The selected text overlaps another selection or annotation. Edit that annotation directly, or select a single line."
        case .documentChangedDuringOpen: "This snip changed while the project was opening. Your edits are still here. Open the project again when you’re ready to replace this snip."
        }
    }
}

@MainActor
final class EditorDocument: ObservableObject {
    @Published private(set) var image: CGImage?
    @Published private(set) var strokes: [InkStroke] = []
    @Published private(set) var draftStroke: InkStroke?
    private(set) var rasterRevision = UUID()
    private var undoHistory: [AnnotationEdit] = []
    private var redoHistory: [AnnotationEdit] = []
    private var imageRevision = UUID()
    private var savedRevision: UUID?
    var hasPendingTextChanges = false

    var hasImage: Bool { image != nil }
    var canUndo: Bool { !undoHistory.isEmpty }
    var canRedo: Bool { !redoHistory.isEmpty }
    var isDrawing: Bool { draftStroke != nil }
    var hasUnsavedChanges: Bool { hasImage && (hasPendingTextChanges || savedRevision != contentRevision) }
    var contentRevision: UUID { undoHistory.last?.revision ?? imageRevision }

    func replaceImage(_ source: NSImage, alreadySaved: Bool) throws {
        // Prefer the full raster representation so Retina captures retain their native pixels.
        let nativeRaster = source.representations
            .compactMap { ($0 as? NSBitmapImageRep)?.cgImage }
            .max { lhs, rhs in Double(lhs.width) * Double(lhs.height) < Double(rhs.width) * Double(rhs.height) }
        guard let raster = nativeRaster ?? source.cgImage(forProposedRect: nil, context: nil, hints: nil),
              raster.width > 0, raster.height > 0 else {
            throw EditorDocumentError.unreadableImage
        }
        try replace(raster: raster, annotations: [], alreadySaved: alreadySaved)
    }

    func openProject(_ contents: StudioProjectContents, replacing revision: UUID? = nil) async throws {
        let annotations = try await Task.detached(priority: .userInitiated) {
            try contents.annotations.map(InkStroke.init(projectRecord:))
        }.value
        try Task.checkCancellation()
        if let revision, contentRevision != revision || isDrawing || hasPendingTextChanges {
            throw EditorDocumentError.documentChangedDuringOpen
        }
        try replace(raster: contents.image, annotations: annotations, alreadySaved: true)
    }

    private func replace(raster: CGImage, annotations: [InkStroke], alreadySaved: Bool) throws {
        guard raster.width <= 64_000_000 / raster.height else {
            throw EditorDocumentError.imageTooLarge
        }
        draftStroke = nil
        hasPendingTextChanges = false
        undoHistory.removeAll()
        redoHistory.removeAll()
        imageRevision = UUID()
        rasterRevision = imageRevision
        savedRevision = alreadySaved ? imageRevision : nil
        strokes = annotations
        image = raster
    }

    @discardableResult
    func crop(to imageRect: CGRect) throws -> Bool {
        guard let image else { throw EditorDocumentError.noImage }
        let requested = imageRect.standardized
        guard !isDrawing, !requested.isEmpty, requested.minX.isFinite, requested.maxX.isFinite,
              requested.minY.isFinite, requested.maxY.isFinite else { return false }
        let imageBounds = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let selected = requested.intersection(imageBounds)
        guard !selected.isEmpty else { return false }
        // Core Graphics image crops use top-left pixels and include any touched pixel.
        let pixels = selected.integral
        guard pixels != imageBounds else { return false }
        guard let cropped = image.cropping(to: pixels) else { throw EditorDocumentError.cropFailed }

        let offset = CGSize(width: -pixels.minX, height: -pixels.minY)
        let remaining = strokes.filter { $0.bounds.intersects(pixels) }.map { $0.offset(by: offset) }
        // The canvas and exporter clip surviving objects to the cropped raster's bounds.
        let original = RasterState(image: image, revision: rasterRevision, strokes: strokes)
        let result = RasterState(image: cropped, revision: UUID(), strokes: remaining)
        commit(.raster(before: original, after: result))
        return true
    }

    @discardableResult
    func patchRaster(_ regions: [CGRect], opaque: Bool) async throws -> Bool {
        guard let source = image else { throw EditorDocumentError.noImage }
        let stamp = rasterRevision
        let bounds = CGRect(x: 0, y: 0, width: source.width, height: source.height)
        let rects = regions.compactMap { region -> CGRect? in
            let clipped = region.standardized.intersection(bounds)
            guard !clipped.isEmpty, clipped.minX.isFinite, clipped.minY.isFinite,
                  clipped.maxX.isFinite, clipped.maxY.isFinite else { return nil }
            return clipped.integral
        }
        guard !rects.isEmpty else { return false }
        let color: CGColor? = opaque ? CGColor(gray: 0, alpha: 1) : nil
        let result = try await Self.render(source, patches: rects.map { RasterPatch(rect: $0, color: color) })
        try Task.checkCancellation()
        guard rasterRevision == stamp else { throw EditorDocumentError.rasterChanged }
        cancelStroke()
        let original = RasterState(image: source, revision: rasterRevision, strokes: strokes)
        let edited = RasterState(image: result, revision: UUID(), strokes: strokes)
        commit(.raster(before: original, after: edited))
        return true
    }

    func replaceOCRText(_ edits: [StudioOCRTextEdit], expectedRevision: UUID) async throws {
        guard let source = image, rasterRevision == expectedRevision else { throw EditorDocumentError.rasterChanged }
        guard !edits.isEmpty, edits.count <= 256 else { throw EditorDocumentError.unsupportedText }
        var patches: [RasterPatch] = []
        var newNotes: [InkStroke] = []
        for edit in edits {
            let proposal = edit.proposal
            guard !strokes.contains(where: { $0.bounds.intersects(proposal.region) }),
                  !patches.contains(where: { $0.rect.intersects(proposal.region) }) else {
                throw EditorDocumentError.overlappingOCRText
            }
            var note = InkStroke(note: edit.note, color: edit.foreground, at: proposal.origin)
            guard note.prepareForCommit() else { throw EditorDocumentError.unsupportedText }
            patches.append(RasterPatch(rect: proposal.region, color: proposal.background))
            newNotes.append(note)
        }
        let editorStamp = contentRevision
        let result = try await Self.render(source, patches: patches)
        try Task.checkCancellation()
        guard rasterRevision == expectedRevision, contentRevision == editorStamp else { throw EditorDocumentError.rasterChanged }
        cancelStroke()
        let original = RasterState(image: source, revision: rasterRevision, strokes: strokes)
        let edited = RasterState(image: result, revision: UUID(), strokes: strokes + newNotes)
        commit(.raster(before: original, after: edited))
    }

    func applyRedactions(_ regions: [CGRect], to rendered: CGImage, expectedRevision: UUID) async throws {
        guard let originalImage = image, contentRevision == expectedRevision, !isDrawing, !hasPendingTextChanges,
              rendered.width == originalImage.width, rendered.height == originalImage.height else {
            throw EditorDocumentError.rasterChanged
        }
        let bounds = CGRect(x: 0, y: 0, width: rendered.width, height: rendered.height)
        let patches = regions.compactMap { region -> RasterPatch? in
            let pixels = region.standardized.intersection(bounds).integral
            guard !pixels.isEmpty, pixels.minX.isFinite, pixels.minY.isFinite,
                  pixels.maxX.isFinite, pixels.maxY.isFinite else { return nil }
            return RasterPatch(rect: pixels, color: CGColor(gray: 0, alpha: 1))
        }
        guard !patches.isEmpty else { return }
        let redacted = try await Self.render(rendered, patches: patches)
        try Task.checkCancellation()
        guard contentRevision == expectedRevision, !isDrawing, !hasPendingTextChanges else { throw EditorDocumentError.rasterChanged }
        let original = RasterState(image: originalImage, revision: rasterRevision, strokes: strokes)
        // Black out the rendered image and discard recoverable annotation text from the current document.
        commit(.raster(before: original, after: RasterState(image: redacted, revision: UUID(), strokes: [])))
    }

    private struct RasterPatch: Sendable {
        let rect: CGRect
        let color: CGColor?
    }

    private nonisolated static func render(_ source: CGImage, patches: [RasterPatch]) async throws -> CGImage {
        try await Task.detached(priority: .userInitiated) {
            let bounds = CGRect(x: 0, y: 0, width: source.width, height: source.height)
            guard let space = CGColorSpace(name: CGColorSpace.sRGB),
                  let context = CGContext(data: nil, width: source.width, height: source.height,
                                          bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
                throw EditorDocumentError.renderFailed
            }
            context.draw(source, in: bounds)
            for patch in patches {
                let rect = patch.rect
                let flipped = CGRect(x: rect.minX, y: bounds.height - rect.maxY, width: rect.width, height: rect.height)
                if let color = patch.color {
                    context.setFillColor(color)
                    context.fill(flipped)
                } else {
                    context.clear(flipped)
                }
            }
            guard let rendered = context.makeImage() else { throw EditorDocumentError.renderFailed }
            return rendered
        }.value
    }

    func eraseAllAnnotations() {
        cancelStroke()
        if !strokes.isEmpty { commit(.eraseAll(strokes)) }
    }

    func highlightRegions(_ regions: [CGRect], color: CGColor) {
        guard image != nil, !regions.isEmpty else { return }
        let highlights = regions.compactMap { region -> InkStroke? in
            guard !region.isEmpty, !region.isInfinite else { return nil }
            let start = CGPoint(x: region.minX, y: region.minY)
            let end = CGPoint(x: region.maxX, y: region.maxY)
            var stroke = InkStroke(color: CGColor(gray: 0, alpha: 0), width: 0.5, at: start,
                                   shape: .rectangle, fill: color)
            stroke.extend(to: end)
            return stroke.prepareForCommit() ? stroke : nil
        }
        if !highlights.isEmpty { commit(.insertMany(highlights)) }
    }

    func beginStroke(at point: CGPoint, color: CGColor, width: CGFloat) {
        beginAnnotation(at: point, color: color, width: width)
    }

    func applyText(_ note: StudioTextAnnotation, at origin: CGPoint, color: CGColor, updating id: UUID?) throws {
        guard image != nil else { throw EditorDocumentError.noImage }
        guard let layout = note.layout(at: origin), !layout.hasUnsupportedGlyphs, !layout.outline.isEmpty else {
            throw EditorDocumentError.unsupportedText
        }
        if let id {
            guard let existing = annotation(withID: id), let previousNote = existing.textNote else { return }
            guard previousNote != note || !CFEqual(existing.color, color) else { return }
            _ = updateAnnotation(id: id) { $0.replacingText(with: note, color: color) }
        } else {
            var stroke = InkStroke(note: note, color: color, at: origin)
            if stroke.prepareForCommit() { commit(.insert(stroke)) }
        }
    }

    func beginShape(at point: CGPoint, color: CGColor, width: CGFloat, shape: SnipShape, lineStyle: SnipLineStyle,
                    fill: CGColor? = nil) {
        beginAnnotation(at: point, color: color, width: width, shape: shape, lineStyle: lineStyle, fill: fill)
    }

    private func beginAnnotation(at point: CGPoint, color: CGColor, width: CGFloat,
                                 shape: SnipShape? = nil, lineStyle: SnipLineStyle = .solid, fill: CGColor? = nil) {
        guard image != nil, !isDrawing, width.isFinite, width > 0, point.x.isFinite, point.y.isFinite else { return }
        draftStroke = InkStroke(color: color, width: width, at: point, shape: shape, lineStyle: lineStyle, fill: fill)
    }

    @discardableResult
    func addEmoji(_ emoji: String, at origin: CGPoint, size: CGFloat) throws -> UUID? {
        guard image != nil, origin.x.isFinite, origin.y.isFinite else { throw EditorDocumentError.noImage }
        var sticker = try InkStroke(emoji: emoji, size: size, at: origin)
        guard sticker.prepareForCommit() else { return nil }
        commit(.insert(sticker))
        return sticker.id
    }

    func setFill(id: UUID, color: CGColor?) {
        _ = updateAnnotation(id: id) { $0.filled(with: color) }
    }

    func resizeAnnotation(id: UUID, factor: CGFloat, around anchor: CGPoint? = nil) throws {
        guard let stroke = annotation(withID: id), let changed = try stroke.scaled(by: factor, around: anchor) else { return }
        _ = updateAnnotation(id: id) { _ in changed }
    }

    func extendStroke(to point: CGPoint) {
        guard point.x.isFinite, point.y.isFinite, draftStroke?.points.last != point else { return }
        draftStroke?.extend(to: point)
    }

    @discardableResult
    func finishStroke() -> UUID? {
        guard var stroke = draftStroke else { return nil }
        draftStroke = nil
        guard stroke.prepareForCommit() else { return nil }
        commit(.insert(stroke))
        return stroke.id
    }

    func cancelStroke() {
        if draftStroke != nil { draftStroke = nil }
    }

    @discardableResult
    func eraseAnnotation(at point: CGPoint, tolerance: CGFloat) -> Bool {
        guard !isDrawing, let index = annotationIndex(at: point, tolerance: tolerance) else { return false }
        commit(.erase(index: index, stroke: strokes[index]))
        return true
    }

    func annotation(at point: CGPoint, tolerance: CGFloat) -> InkStroke? {
        annotationIndex(at: point, tolerance: tolerance).map { strokes[$0] }
    }

    func annotation(withID id: UUID) -> InkStroke? {
        strokes.first { $0.id == id }
    }

    func annotationOnPerimeter(at point: CGPoint, tolerance: CGFloat) -> InkStroke? {
        strokes.last(where: { $0.isNearPerimeter(point, tolerance: tolerance) })
    }

    private func annotationIndex(at point: CGPoint, tolerance: CGFloat) -> Int? {
        guard tolerance.isFinite, tolerance >= 0, point.x.isFinite, point.y.isFinite else { return nil }
        return strokes.lastIndex { $0.contains(point, tolerance: tolerance) }
    }

    @discardableResult
    func translateAnnotation(id: UUID, by delta: CGSize) -> Bool {
        guard delta != .zero, delta.width.isFinite, delta.height.isFinite else { return false }
        return updateAnnotation(id: id) { $0.offset(by: delta) }
    }

    @discardableResult
    func rotateAnnotation(id: UUID, clockwise: Bool) -> Bool {
        updateAnnotation(id: id) { $0.rotated(clockwise: clockwise) }
    }

    @discardableResult
    func updateAnnotation(id: UUID, color: CGColor? = nil, width: CGFloat? = nil, lineStyle: SnipLineStyle? = nil) -> Bool {
        updateAnnotation(id: id) { $0.restyled(color: color, width: width, lineStyle: lineStyle) }
    }

    @discardableResult
    func deleteAnnotation(id: UUID) -> Bool {
        guard !isDrawing, let index = strokes.firstIndex(where: { $0.id == id }) else { return false }
        commit(.erase(index: index, stroke: strokes[index]))
        return true
    }

    private func updateAnnotation(id: UUID, transform: (InkStroke) -> InkStroke?) -> Bool {
        guard !isDrawing, let index = strokes.firstIndex(where: { $0.id == id }) else { return false }
        let original = strokes[index]
        guard var changed = transform(original),
              changed.points.allSatisfy({ $0.x.isFinite && $0.y.isFinite }),
              changed.prepareForCommit() else { return false }
        commit(.replace(index: index, before: original, after: changed))
        return true
    }

    private func commit(_ operation: AnnotationOperation) {
        redoHistory.removeAll()
        undoHistory.append(AnnotationEdit(operation: operation))
        apply(operation)
    }

    private func apply(_ operation: AnnotationOperation) {
        switch operation {
        case let .insert(stroke): strokes.append(stroke)
        case let .insertMany(marks): strokes.append(contentsOf: marks)
        case let .erase(index, _): strokes.remove(at: index)
        case let .replace(index, _, after): strokes[index] = after
        case let .raster(_, after): setRaster(after)
        case .eraseAll: strokes.removeAll()
        }
    }

    private func setRaster(_ state: RasterState) {
        rasterRevision = state.revision
        strokes = state.strokes
        image = state.image
    }

    func undo() {
        cancelStroke()
        guard let edit = undoHistory.popLast() else { return }
        redoHistory.append(edit)
        switch edit.operation {
        case .insert: strokes.removeLast()
        case let .insertMany(marks): strokes.removeLast(marks.count)
        case let .erase(index, stroke): strokes.insert(stroke, at: index)
        case let .replace(index, before, _): strokes[index] = before
        case let .raster(before, _): setRaster(before)
        case let .eraseAll(original): strokes = original
        }
    }

    func redo() {
        cancelStroke()
        guard let edit = redoHistory.popLast() else { return }
        undoHistory.append(edit)
        apply(edit.operation)
    }

    func markSaved(_ snapshot: PngSnapshot) {
        guard snapshot.imageRevision == imageRevision else { return }
        savedRevision = snapshot.revision
    }

    func pngSnapshot() throws -> PngSnapshot {
        guard let image else { throw EditorDocumentError.noImage }
        return PngSnapshot(image: image, strokes: strokes, imageRevision: imageRevision, revision: contentRevision)
    }

    func pngData() async throws -> Data {
        try await pngSnapshot().pngData()
    }

    private enum AnnotationOperation {
        case insert(InkStroke)
        case insertMany([InkStroke])
        case erase(index: Int, stroke: InkStroke)
        case replace(index: Int, before: InkStroke, after: InkStroke)
        case raster(before: RasterState, after: RasterState)
        case eraseAll([InkStroke])
    }

    private struct RasterState {
        let image: CGImage
        let revision: UUID
        let strokes: [InkStroke]
    }

    private struct AnnotationEdit {
        let operation: AnnotationOperation
        let revision = UUID()
    }
}

struct PngSnapshot: Sendable {
    fileprivate let image: CGImage
    fileprivate let strokes: [InkStroke]
    fileprivate let imageRevision: UUID
    fileprivate let revision: UUID

    func pngData() async throws -> Data {
        try await encodePNG { $0 }
    }

    func writePNG(to url: URL) async throws {
        try await encodePNG { try $0.write(to: url, options: .atomic) }
    }

    func writeProject(to url: URL) async throws {
        try await StudioProjectFile.save(image: image, annotations: strokes.map { try $0.projectRecord() }, to: url)
    }

    private func encodePNG<Result: Sendable>(_ consume: @escaping @Sendable (Data) throws -> Result) async throws -> Result {
        try Task.checkCancellation()
        let worker = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            let raster = try self.rasterForExport()
            try Task.checkCancellation()

            guard let encoded = CFDataCreateMutable(nil, 0),
                  let destination = CGImageDestinationCreateWithData(encoded, UTType.png.identifier as CFString, 1, nil) else {
                throw EditorDocumentError.encodeFailed
            }
            CGImageDestinationAddImage(destination, raster, nil)
            guard CGImageDestinationFinalize(destination) else { throw EditorDocumentError.encodeFailed }

            let png = encoded as Data
            try Task.checkCancellation()
            return try consume(png)
        }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
        }
    }

    // Compositing can allocate the full image. Call from a background task.
    func rasterForExport() throws -> CGImage {
        guard !strokes.isEmpty else { return image }
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(
                  data: nil, width: image.width, height: image.height,
                  bitsPerComponent: 8, bytesPerRow: 0, space: colorSpace,
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else {
            throw EditorDocumentError.renderFailed
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: CGFloat(image.width), height: CGFloat(image.height)))

        // Ink uses the canvas's top-left origin. Only flip the annotation drawing.
        context.translateBy(x: 0, y: CGFloat(image.height))
        context.scaleBy(x: 1, y: -1)
        for stroke in strokes {
            try Task.checkCancellation()
            if let bitmap = stroke.bitmap {
                let frame = stroke.bitmapDrawingFrame
                context.saveGState()
                context.concatenate(stroke.paintTransform)
                context.translateBy(x: frame.minX, y: frame.maxY)
                context.scaleBy(x: 1, y: -1)
                context.draw(bitmap, in: CGRect(origin: .zero, size: frame.size))
                context.restoreGState()
                continue
            }
            if let bands = stroke.highlightBands {
                context.saveGState()
                context.concatenate(stroke.paintTransform)
                context.setFillColor(stroke.color)
                for band in bands { context.fill(band) }
                context.restoreGState()
                continue
            }
            if let fill = stroke.fillColor, let area = stroke.fillOutline {
                context.setFillColor(fill)
                context.addPath(area)
                context.fillPath()
            }
            context.setFillColor(stroke.color)
            context.addPath(stroke.outline)
            context.fillPath()
        }
        guard let rendered = context.makeImage() else { throw EditorDocumentError.renderFailed }
        return rendered
    }
}
