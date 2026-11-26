import AppKit
import Combine
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

struct InkStroke: Sendable {
    let revision = UUID()
    let color: CGColor
    let width: CGFloat
    var points: [CGPoint]

    // Both renderers consume the same image-space outline.
    var outline: CGPath {
        let path = CGMutablePath()
        guard let first = points.first else { return path }
        if points.count == 1 {
            let radius = width / 2
            path.addEllipse(in: CGRect(
                x: first.x - radius, y: first.y - radius, width: width, height: width
            ))
            return path
        }

        path.addLines(between: points)
        return path.copy(strokingWithWidth: width, lineCap: .round, lineJoin: .round, miterLimit: 1)
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
    private var imageRevision = UUID()
    private var savedRevision: UUID?

    var hasImage: Bool { image != nil }
    var canUndo: Bool { !strokes.isEmpty }
    var isDrawing: Bool { draftStroke != nil }
    var hasUnsavedChanges: Bool { hasImage && savedRevision != revision }
    private var revision: UUID { strokes.last?.revision ?? imageRevision }

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
        strokes.removeAll()
        imageRevision = UUID()
        savedRevision = alreadySaved ? imageRevision : nil
        image = raster
    }

    func beginStroke(at point: CGPoint, color: CGColor, width: CGFloat) {
        guard image != nil, !isDrawing, width.isFinite, width > 0 else { return }
        draftStroke = InkStroke(color: color, width: width, points: [point])
    }

    func extendStroke(to point: CGPoint) {
        guard let lastPoint = draftStroke?.points.last, lastPoint != point else { return }
        draftStroke?.points.append(point)
    }

    func finishStroke() {
        guard let stroke = draftStroke else { return }
        strokes.append(stroke)
        draftStroke = nil
    }

    func cancelStroke() {
        if draftStroke != nil { draftStroke = nil }
    }

    func undo() {
        cancelStroke()
        guard !strokes.isEmpty else { return }
        strokes.removeLast()
    }

    func markSaved(_ snapshot: PngSnapshot) {
        guard snapshot.imageRevision == imageRevision else { return }
        savedRevision = snapshot.revision
    }

    func pngSnapshot() throws -> PngSnapshot {
        guard let image else { throw EditorDocumentError.noImage }
        return PngSnapshot(image: image, strokes: strokes, imageRevision: imageRevision, revision: revision)
    }

    func pngData() async throws -> Data {
        try await pngSnapshot().pngData()
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
            let raster = try self.renderedImage()
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

    private func renderedImage() throws -> CGImage {
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
