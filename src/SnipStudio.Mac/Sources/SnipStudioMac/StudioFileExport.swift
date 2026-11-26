import AppKit
import CoreGraphics
import Darwin
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum StudioFileFormat: String, CaseIterable, Identifiable, Sendable {
    case png, jpeg, bmp, pdf

    var id: Self { self }

    var title: String { rawValue.uppercased() }

    var filenameExtension: String { self == .jpeg ? "jpg" : rawValue }

    var contentType: UTType {
        switch self {
        case .png: .png
        case .jpeg: .jpeg
        case .bmp: .bmp
        case .pdf: .pdf
        }
    }
}

enum StudioFileExport {
    static func write(_ snapshot: PngSnapshot, as format: StudioFileFormat, to url: URL) async throws {
        guard url.isFileURL else { throw StudioFileExportError.invalidDestination }
        if format == .png {
            try await snapshot.writePNG(to: url)
            return
        }

        try await performOffMain {
            let image = try snapshot.rasterForExport()
            try Task.checkCancellation()

            let fileManager = FileManager.default
            let workspace = url.deletingLastPathComponent().appendingPathComponent(
                ".snipstudio-export-\(UUID().uuidString)", isDirectory: true
            )
            try fileManager.createDirectory(
                at: workspace, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700]
            )
            defer { try? fileManager.removeItem(at: workspace) }

            let staged = workspace.appendingPathComponent("image.\(format.filenameExtension)")
            if format == .pdf {
                try writePDF(image, to: staged)
            } else {
                try writeImage(image, as: format, to: staged)
            }
            try Task.checkCancellation()
            try publish(staged, to: url)
        }
    }

    @MainActor
    static func print(_ snapshot: PngSnapshot) async throws -> Bool {
        let image = try await performOffMain { try snapshot.rasterForExport() }
        try Task.checkCancellation()
        guard NSPrintOperation.current == nil else { throw StudioFileExportError.printInProgress }

        let info = NSPrintInfo()
        info.orientation = image.width > image.height ? .landscape : .portrait
        info.horizontalPagination = .fit
        info.verticalPagination = .fit
        info.isHorizontallyCentered = true
        info.isVerticallyCentered = true

        let view = SnipPrintView(image: image, frame: pageBounds(for: image))
        let operation = NSPrintOperation(view: view, printInfo: info)
        operation.jobTitle = "Snip Desk"
        operation.showsPrintPanel = true
        operation.showsProgressPanel = true

        guard let window = NSApp.keyWindow else { return operation.run() }
        let delegate = SnipPrintCompletion()
        let result = await withCheckedContinuation { continuation in
            delegate.continuation = continuation
            operation.runModal(
                for: window,
                delegate: delegate,
                didRun: #selector(SnipPrintCompletion.didRun(_:success:contextInfo:)),
                contextInfo: nil
            )
        }
        withExtendedLifetime((operation, delegate)) {}
        return result
    }

    private static func performOffMain<Result: Sendable>(
        _ action: @escaping @Sendable () throws -> Result
    ) async throws -> Result {
        try Task.checkCancellation()
        let worker = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            return try action()
        }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            worker.cancel()
        }
    }

    private static func writeImage(_ image: CGImage, as format: StudioFileFormat, to url: URL) throws {
        // A white backing makes transparent snips readable in both opaque formats.
        let raster = try flattenOnWhite(image)
        try Task.checkCancellation()
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, format.contentType.identifier as CFString, 1, nil)
        else { throw StudioFileExportError.encodeFailed(format) }

        let properties = [kCGImageDestinationLossyCompressionQuality: 0.92] as CFDictionary
        CGImageDestinationAddImage(destination, raster, format == .jpeg ? properties : nil)
        guard CGImageDestinationFinalize(destination) else { throw StudioFileExportError.encodeFailed(format) }
    }

    private static func flattenOnWhite(_ image: CGImage) throws -> CGImage {
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(
                  data: nil, width: image.width, height: image.height, bitsPerComponent: 8,
                  bytesPerRow: 0, space: colorSpace, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
              ) else { throw EditorDocumentError.renderFailed }
        let bounds = CGRect(x: 0, y: 0, width: CGFloat(image.width), height: CGFloat(image.height))
        drawOnWhite(image, in: bounds, using: context)
        guard let raster = context.makeImage() else { throw EditorDocumentError.renderFailed }
        return raster
    }

    private static func writePDF(_ image: CGImage, to url: URL) throws {
        var bounds = pageBounds(for: image)
        guard let context = CGContext(url as CFURL, mediaBox: &bounds, nil) else {
            throw StudioFileExportError.encodeFailed(.pdf)
        }
        context.beginPDFPage(nil)
        drawOnWhite(image, in: bounds, using: context)
        context.endPDFPage()
        context.closePDF()
        guard let document = CGPDFDocument(url as CFURL), document.numberOfPages == 1,
              let page = document.page(at: 1) else {
            throw StudioFileExportError.encodeFailed(.pdf)
        }
        let media = page.getBoxRect(.mediaBox)
        guard abs(media.width - bounds.width) < 0.1, abs(media.height - bounds.height) < 0.1 else {
            throw StudioFileExportError.encodeFailed(.pdf)
        }
    }

    private static func pageBounds(for image: CGImage) -> CGRect {
        let width = CGFloat(image.width)
        let height = CGFloat(image.height)
        let pointsPerPixel = min(72.0 / 96.0, 14_400 / max(width, height))
        return CGRect(x: 0, y: 0, width: width * pointsPerPixel, height: height * pointsPerPixel)
    }

    fileprivate static func drawOnWhite(_ image: CGImage, in bounds: CGRect, using context: CGContext) {
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(bounds)
        context.interpolationQuality = .high
        context.draw(image, in: bounds)
    }

    private static func publish(_ staged: URL, to destination: URL) throws {
        // Both paths are on the same volume. Replace only after the encoder closes the file.
        try staged.withUnsafeFileSystemRepresentation { source in
            try destination.withUnsafeFileSystemRepresentation { target in
                guard let source, let target else { throw StudioFileExportError.invalidDestination }
                guard Darwin.rename(source, target) == 0 else {
                    let failure = errno
                    throw NSError(domain: NSPOSIXErrorDomain, code: Int(failure))
                }
            }
        }
    }
}

@MainActor
private final class SnipPrintView: NSView {
    private let image: CGImage

    init(image: CGImage, frame: CGRect) {
        self.image = image
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        StudioFileExport.drawOnWhite(image, in: bounds, using: context)
    }
}

@MainActor
private final class SnipPrintCompletion: NSObject {
    var continuation: CheckedContinuation<Bool, Never>?

    @objc func didRun(_ operation: NSPrintOperation, success: Bool, contextInfo: UnsafeMutableRawPointer?) {
        let pending = continuation
        continuation = nil
        pending?.resume(returning: success)
    }
}

private enum StudioFileExportError: LocalizedError {
    case invalidDestination
    case encodeFailed(StudioFileFormat)
    case printInProgress

    var errorDescription: String? {
        switch self {
        case .invalidDestination: "Choose a destination for the exported image."
        case .encodeFailed(let format): "Snip Desk could not encode the image as \(format.title)."
        case .printInProgress: "Wait for the current print job to finish before starting another."
        }
    }
}

@MainActor
enum StudioFileExportSelfCheck {
    static func run() async throws {
        let bitmap = try fixtureBitmap()
        guard let transparent = bitmap.makeImage() else { throw Failure("create transparent source") }
        let document = EditorDocument()
        try document.replaceImage(NSImage(cgImage: transparent, size: NSSize(width: 120, height: 80)), alreadySaved: false)
        document.beginStroke(at: CGPoint(x: 24, y: 18), color: CGColor(red: 1, green: 0, blue: 0, alpha: 1), width: 16)
        document.extendStroke(to: CGPoint(x: 46, y: 18))
        document.finishStroke()
        let snapshot = try document.pngSnapshot()

        let files = FileManager.default
        let workspace = files.temporaryDirectory.appendingPathComponent("SnipStudioExportCheck-\(UUID().uuidString)")
        try files.createDirectory(at: workspace, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? files.removeItem(at: workspace) }

        for format in StudioFileFormat.allCases {
            let url = workspace.appendingPathComponent("snip.\(format.filenameExtension)")
            try await StudioFileExport.write(snapshot, as: format, to: url)
            let image = try readback(format, from: url)
            try expect(image.width == 120 && image.height == 80, "\(format.title) retains the source dimensions")
            let ink = try pixel(image, x: 34, y: 18)
            try expect(ink[0] > 215 && ink[1] < 48 && ink[2] < 48 && ink[3] > 240,
                       "\(format.title) preserves the upper-left red stroke")
            let background = try pixel(image, x: 100, y: 62)
            if format == .png {
                try expect(background[3] == 0, "PNG keeps the source alpha")
            } else {
                try expect(background.allSatisfy { $0 >= 248 }, "\(format.title) composites transparent pixels on white")
            }
        }

        let retained = workspace.appendingPathComponent("snip.jpg")
        let previous = try Data(contentsOf: retained)
        let cancelled = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            try await StudioFileExport.write(snapshot, as: .jpeg, to: retained)
        }
        do {
            try await cancelled.value
            throw Failure("canceled export must throw")
        } catch is CancellationError {}
        let stillPresent = try Data(contentsOf: retained)
        try expect(stillPresent == previous, "canceled export preserves an existing output")
        try expect(try files.contentsOfDirectory(atPath: workspace.path).count == 4,
                   "export removes private staging data")
        Swift.print("PASS: PNG, JPEG, BMP and PDF readback, annotation orientation, transparency, and safe cancellation")
    }

    private static func readback(_ format: StudioFileFormat, from url: URL) throws -> CGImage {
        if format == .pdf {
            guard let document = CGPDFDocument(url as CFURL), document.numberOfPages == 1,
                  let page = document.page(at: 1) else { throw Failure("open a single-page PDF") }
            let size = page.getBoxRect(.mediaBox).size
            try expect(size == CGSize(width: 90, height: 60), "PDF uses the snip aspect and 96-DPI physical size")
            let bitmap = try fixtureBitmap()
            bitmap.scaleBy(x: 4.0 / 3.0, y: 4.0 / 3.0)
            bitmap.drawPDFPage(page)
            guard let image = bitmap.makeImage() else { throw Failure("render exported PDF page") }
            return image
        }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let identifier = CGImageSourceGetType(source), identifier as String == format.contentType.identifier,
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw Failure("reopen exported \(format.title)")
        }
        return image
    }

    private static func fixtureBitmap(width: Int = 120, height: Int = 80) throws -> CGContext {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let bitmap = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                     space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw Failure("allocate an export fixture bitmap")
        }
        bitmap.clear(CGRect(x: 0, y: 0, width: width, height: height))
        return bitmap
    }

    private static func pixel(_ image: CGImage, x: Int, y: Int) throws -> [UInt8] {
        guard let crop = image.cropping(to: CGRect(x: x, y: y, width: 1, height: 1)) else {
            throw Failure("locate readback pixel")
        }
        let sample = try fixtureBitmap(width: 1, height: 1)
        sample.draw(crop, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        guard let storage = sample.data?.assumingMemoryBound(to: UInt8.self) else { throw Failure("read export pixel bytes") }
        return Array(UnsafeBufferPointer(start: storage, count: 4))
    }

    private static func expect(_ condition: @autoclosure () throws -> Bool, _ label: String) throws {
        guard try condition() else { throw Failure(label) }
    }

    private struct Failure: Error, CustomStringConvertible {
        let description: String

        init(_ description: String) { self.description = description }
    }
}
