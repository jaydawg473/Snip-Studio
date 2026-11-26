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

struct InkStroke: Sendable {
    let color: CGColor
    let width: CGFloat
    private(set) var points: [CGPoint]
    private let shape: SnipShape?
    private let lineStyle: SnipLineStyle
    private var committedOutline: CommittedOutline?

    init(color: CGColor, width: CGFloat, at point: CGPoint,
         shape: SnipShape? = nil, lineStyle: SnipLineStyle = .solid) {
        self.color = color
        self.width = width
        points = [point]
        self.shape = shape
        self.lineStyle = lineStyle
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
        let rendered = outline
        guard !rendered.isEmpty, let immutable = rendered.copy() else { return false }
        committedOutline = CommittedOutline(path: immutable, bounds: immutable.boundingBoxOfPath)
        return true
    }

    // Both renderers consume the same image-space outline.
    var outline: CGPath {
        if let committedOutline { return committedOutline.path }
        let path = CGMutablePath()
        let first = points[0]
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

    func contains(_ point: CGPoint, tolerance: CGFloat) -> Bool {
        let path = outline
        let bounds = committedOutline?.bounds ?? path.boundingBoxOfPath
        guard bounds.insetBy(dx: -tolerance, dy: -tolerance).contains(point) else { return false }
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
    case renderFailed
    case encodeFailed

    var errorDescription: String? {
        switch self {
        case .noImage: "Capture or open a snip first."
        case .unreadableImage: "The image could not be opened."
        case .imageTooLarge: "This image is too large to edit. Choose an image up to 64 million pixels."
        case .renderFailed: "The edited snip could not be rendered."
        case .encodeFailed: "The snip could not be encoded as a PNG."
        }
    }
}

@MainActor
final class EditorDocument: ObservableObject {
    @Published private(set) var image: CGImage?
    @Published private(set) var strokes: [InkStroke] = []
    @Published private(set) var draftStroke: InkStroke?
    private var undoHistory: [AnnotationEdit] = []
    private var redoHistory: [AnnotationEdit] = []
    private var imageRevision = UUID()
    private var savedRevision: UUID?

    var hasImage: Bool { image != nil }
    var canUndo: Bool { !undoHistory.isEmpty }
    var canRedo: Bool { !redoHistory.isEmpty }
    var isDrawing: Bool { draftStroke != nil }
    var hasUnsavedChanges: Bool { hasImage && savedRevision != contentRevision }
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
        guard raster.width <= 64_000_000 / raster.height else {
            throw EditorDocumentError.imageTooLarge
        }
        draftStroke = nil
        undoHistory.removeAll()
        redoHistory.removeAll()
        imageRevision = UUID()
        savedRevision = alreadySaved ? imageRevision : nil
        strokes.removeAll()
        image = raster
    }

    func beginStroke(at point: CGPoint, color: CGColor, width: CGFloat) {
        beginAnnotation(at: point, color: color, width: width)
    }

    func beginShape(at point: CGPoint, color: CGColor, width: CGFloat, shape: SnipShape, lineStyle: SnipLineStyle) {
        beginAnnotation(at: point, color: color, width: width, shape: shape, lineStyle: lineStyle)
    }

    private func beginAnnotation(at point: CGPoint, color: CGColor, width: CGFloat,
                                 shape: SnipShape? = nil, lineStyle: SnipLineStyle = .solid) {
        guard image != nil, !isDrawing, width.isFinite, width > 0, point.x.isFinite, point.y.isFinite else { return }
        draftStroke = InkStroke(color: color, width: width, at: point, shape: shape, lineStyle: lineStyle)
    }

    func extendStroke(to point: CGPoint) {
        guard point.x.isFinite, point.y.isFinite, draftStroke?.points.last != point else { return }
        draftStroke?.extend(to: point)
    }

    func finishStroke() {
        guard var stroke = draftStroke else { return }
        draftStroke = nil
        guard stroke.prepareForCommit() else { return }
        commit(.insert(stroke))
    }

    func cancelStroke() {
        if draftStroke != nil { draftStroke = nil }
    }

    @discardableResult
    func eraseAnnotation(at point: CGPoint, tolerance: CGFloat) -> Bool {
        guard !isDrawing, tolerance.isFinite, tolerance >= 0, point.x.isFinite, point.y.isFinite,
              let index = strokes.lastIndex(where: { $0.contains(point, tolerance: tolerance) }) else { return false }
        commit(.erase(index: index, stroke: strokes[index]))
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
        case let .erase(index, _): strokes.remove(at: index)
        }
    }

    func undo() {
        cancelStroke()
        guard let edit = undoHistory.popLast() else { return }
        redoHistory.append(edit)
        switch edit.operation {
        case .insert: strokes.removeLast()
        case let .erase(index, stroke): strokes.insert(stroke, at: index)
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
        case erase(index: Int, stroke: InkStroke)
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
            context.setFillColor(stroke.color)
            context.addPath(stroke.outline)
            context.fillPath()
        }
        guard let rendered = context.makeImage() else { throw EditorDocumentError.renderFailed }
        return rendered
    }
}
