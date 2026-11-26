import CoreGraphics
import CryptoKit
import Darwin
import Foundation
import ImageIO
import UniformTypeIdentifiers

struct StudioProjectAnnotation: Codable, Sendable, Equatable {
    let points: [CGPoint]
    let red: CGFloat
    let green: CGFloat
    let blue: CGFloat
    let alpha: CGFloat
    let width: CGFloat
    let shape: String?
    let lineStyle: String
    let quarterTurns: Int
    let text: StudioTextAnnotation?
    var fill: [CGFloat]? = nil
    var emoji: String? = nil
}

struct StudioProjectContents: Sendable {
    let image: CGImage
    let annotations: [StudioProjectAnnotation]
}

enum StudioProjectFile {
    static let filenameExtension = "snipstudiomac"

    private static let magic = Data("SNIPMAC\0".utf8)
    private static let baseVersion: UInt64 = 1
    private static let decoratedVersion: UInt64 = 2
    private static let digestLength = 32
    private static let pixelDigestOffset = 64
    private static let headerSize = 96
    private static let maximumMetadataBytes = 32 * 1024 * 1024
    private static let maximumPNGBytes = 272 * 1024 * 1024
    private static let maximumPixels = 64_000_000
    private static let maximumAnnotations = 15_000
    private static let maximumPoints = 750_000
    private static let chunkSize = 256 * 1024

    static func save(image: CGImage, annotations: [StudioProjectAnnotation], to url: URL) async throws {
        try await onWorker {
            guard url.isFileURL else { throw Failure.invalidPath }
            try validateDimensions(width: image.width, height: image.height)
            try validateAnnotations(annotations)
            let annotationData = try JSONEncoder().encode(annotations)
            guard annotationData.count <= maximumMetadataBytes else { throw Failure.projectTooLarge }

            let directory = try privateDirectory(in: url.deletingLastPathComponent())
            defer { try? FileManager.default.removeItem(at: directory) }
            let png = directory.appendingPathComponent("source.png")
            let staged = directory.appendingPathComponent("staged.\(filenameExtension)")
            try encodePNG(image, at: png)

            let source = try openRegularFile(png)
            defer { try? source.close() }
            let pngBytes = try size(of: source)
            guard pngBytes > 0, pngBytes <= UInt64(maximumPNGBytes) else { throw Failure.projectTooLarge }
            let output = try openPrivateOutput(staged)
            do {
                var header = magic
                header.appendBigEndian(annotations.contains { $0.fill != nil || $0.emoji != nil } ? decoratedVersion : baseVersion)
                header.appendBigEndian(UInt64(annotationData.count))
                header.appendBigEndian(pngBytes)
                header.append(Data(SHA256.hash(data: annotationData)))
                header.append(Data(repeating: 0, count: digestLength))
                try output.write(contentsOf: header)
                try output.write(contentsOf: annotationData)
                let digest = try transfer(pngBytes, from: source, to: output)
                try output.seek(toOffset: UInt64(pixelDigestOffset))
                try output.write(contentsOf: digest)
                try output.synchronize()
                try output.close()
            } catch {
                try? output.close()
                throw error
            }
            try Task.checkCancellation()
            try replaceAtomically(staged, destination: url)
        }
    }

    static func load(from url: URL) async throws -> StudioProjectContents {
        try await onWorker {
            guard url.isFileURL else { throw Failure.invalidPath }
            let input = try openRegularFile(url)
            defer { try? input.close() }
            let fileBytes = try size(of: input)
            guard fileBytes <= UInt64(headerSize + maximumMetadataBytes + maximumPNGBytes) else {
                throw Failure.projectTooLarge
            }
            let header = try readExactly(headerSize, from: input)
            guard header.prefix(magic.count).elementsEqual(magic) else { throw Failure.wrongFormat }
            let version = header.bigEndian(at: 8)
            guard version == baseVersion || version == decoratedVersion else { throw Failure.unsupportedVersion }
            let annotationBytes = header.bigEndian(at: 16)
            let pngBytes = header.bigEndian(at: 24)
            guard annotationBytes > 0, annotationBytes <= UInt64(maximumMetadataBytes),
                  pngBytes > 0, pngBytes <= UInt64(maximumPNGBytes),
                  UInt64(headerSize) + annotationBytes + pngBytes == fileBytes else { throw Failure.corrupt }

            let annotationData = try readExactly(Int(annotationBytes), from: input)
            guard SHA256.hash(data: annotationData).elementsEqual(header[32..<pixelDigestOffset]) else {
                throw Failure.corrupt
            }
            let annotations: [StudioProjectAnnotation]
            do { annotations = try JSONDecoder().decode([StudioProjectAnnotation].self, from: annotationData) }
            catch { throw Failure.corrupt }
            try validateAnnotations(annotations)
            if version == baseVersion && annotations.contains(where: { $0.fill != nil || $0.emoji != nil }) {
                throw Failure.corrupt
            }

            let directory = try privateDirectory(in: FileManager.default.temporaryDirectory)
            defer { try? FileManager.default.removeItem(at: directory) }
            let png = directory.appendingPathComponent("source.png")
            let output = try openPrivateOutput(png)
            let digest: Data
            do {
                digest = try transfer(pngBytes, from: input, to: output)
                try output.close()
            } catch {
                try? output.close()
                throw error
            }
            guard digest.elementsEqual(header[pixelDigestOffset..<headerSize]),
                  try size(of: input) == fileBytes else { throw Failure.corrupt }
            let image = try decodePNG(at: png)
            try Task.checkCancellation()
            return StudioProjectContents(image: image, annotations: annotations)
        }
    }

    static func selfCheck() async throws {
        let directory = try privateDirectory(in: FileManager.default.temporaryDirectory)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("fixture.\(filenameExtension)")
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let bitmap = CGContext(data: nil, width: 18, height: 12, bitsPerComponent: 8, bytesPerRow: 0,
                                     space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw Failure.couldNotEncode
        }
        bitmap.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        bitmap.fill(CGRect(x: 0, y: 0, width: 9, height: 12))
        guard let image = bitmap.makeImage() else { throw Failure.couldNotEncode }
        let records = [
            StudioProjectAnnotation(points: [CGPoint(x: 1.25, y: 2.5), CGPoint(x: 8, y: 9)],
                                    red: 0.125, green: 0.25, blue: 0.375, alpha: 0.5, width: 3,
                                    shape: "arrow", lineStyle: "dotted", quarterTurns: 3, text: nil),
            StudioProjectAnnotation(points: [CGPoint(x: 6.5, y: 3.125)],
                                    red: 0, green: 0, blue: 0, alpha: 1, width: 1,
                                    shape: nil, lineStyle: "solid", quarterTurns: 0,
                                    text: StudioTextAnnotation(text: "  FILE 0027\r\nQTY 007 ", pixelSize: 17.5, bold: true))
        ]
        try await save(image: image, annotations: records, to: url)
        let result = try await load(from: url)
        guard result.image.width == 18, result.image.height == 12, result.annotations == records,
              try matchesFixturePixels(result.image) else { throw Failure.corrupt }

        let saved = try Data(contentsOf: url)
        let invalid = StudioProjectAnnotation(points: [], red: 0, green: 0, blue: 0, alpha: 1, width: 1,
                                              shape: nil, lineStyle: "solid", quarterTurns: 0, text: nil)
        do {
            try await save(image: image, annotations: [invalid], to: url)
            throw CheckFailure.unexpectedlyAcceptedInvalidSave
        } catch Failure.invalidAnnotations {
            guard try Data(contentsOf: url) == saved else { throw Failure.corrupt }
        }
        for offset in [headerSize, saved.count - 1] {
            var damaged = saved
            damaged[offset] ^= 1
            try damaged.write(to: url, options: .atomic)
            do {
                _ = try await load(from: url)
                throw CheckFailure.unexpectedlyAcceptedDamagedFile
            } catch Failure.corrupt {
                // A one-byte change in either payload must be detected before it is opened.
            }
        }
        print("PASS: editable Mac project records, transparency, damaged-file detection, and safe replacement")
    }

    private static func matchesFixturePixels(_ image: CGImage) throws -> Bool {
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let buffer = CGContext(data: nil, width: 18, height: 12, bitsPerComponent: 8, bytesPerRow: 18 * 4,
                                     space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let raw = buffer.data else { throw Failure.corrupt }
        buffer.draw(image, in: CGRect(x: 0, y: 0, width: 18, height: 12))
        let bytes = UnsafeRawBufferPointer(start: raw, count: 18 * 12 * 4)
        let red = (5 * 18 + 4) * 4
        let clear = (5 * 18 + 14) * 4
        return bytes[red] == 255 && bytes[red + 1] == 0 && bytes[red + 2] == 0 && bytes[red + 3] == 255 && bytes[clear + 3] == 0
    }

    private enum CheckFailure: Error { case unexpectedlyAcceptedDamagedFile, unexpectedlyAcceptedInvalidSave }

    private static func onWorker<Result: Sendable>(
        _ work: @escaping @Sendable () throws -> Result
    ) async throws -> Result {
        try Task.checkCancellation()
        let task = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            return try work()
        }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }

    private static func validateAnnotations(_ annotations: [StudioProjectAnnotation]) throws {
        guard annotations.count <= maximumAnnotations else { throw Failure.projectTooLarge }
        let shapes = ["rectangle", "ellipse", "line", "arrow"]
        let styles = ["solid", "dashed", "dotted"]
        var totalPoints = 0
        for annotation in annotations {
            try Task.checkCancellation()
            guard annotation.points.count <= maximumPoints - totalPoints else { throw Failure.projectTooLarge }
            totalPoints += annotation.points.count
            guard !annotation.points.isEmpty,
                  annotation.points.allSatisfy({ abs($0.x) <= 128_000_000 && abs($0.y) <= 128_000_000 }),
                  [annotation.red, annotation.green, annotation.blue, annotation.alpha].allSatisfy({ (0...1).contains($0) }),
                  annotation.width > 0, annotation.width <= 512,
                  styles.contains(annotation.lineStyle), (0...3).contains(annotation.quarterTurns) else {
                throw Failure.invalidAnnotations
            }
            if let shape = annotation.shape {
                guard shapes.contains(shape), annotation.text == nil, annotation.emoji == nil, annotation.points.count == 2 else {
                    throw Failure.invalidAnnotations
                }
            } else if let note = annotation.text {
                guard annotation.emoji == nil, annotation.points.count == 1,
                      (1...StudioTextAnnotation.maximumUTF16Length).contains(note.text.utf16.count),
                      (1...256).contains(note.fontName.utf16.count), !note.fontName.contains("\0"),
                      StudioTextAnnotation.pixelSizeRange.contains(note.pixelSize) else {
                    throw Failure.invalidAnnotations
                }
            }
            if let fill = annotation.fill {
                guard (annotation.shape == "rectangle" || annotation.shape == "ellipse"), fill.count == 4,
                      fill.allSatisfy({ (0...1).contains($0) }) else { throw Failure.invalidAnnotations }
            }
            if let glyph = annotation.emoji {
                guard annotation.points.count == 1, StudioEmojiRaster.stickers.contains(glyph) else { throw Failure.invalidAnnotations }
            }
        }
    }

    private static func validateDimensions(width: Int, height: Int) throws {
        guard width > 0, height > 0 else { throw Failure.corrupt }
        guard width <= maximumPixels / height else { throw Failure.projectTooLarge }
    }

    private static func encodePNG(_ image: CGImage, at url: URL) throws {
        guard let output = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw Failure.couldNotEncode
        }
        CGImageDestinationAddImage(output, image, nil)
        guard CGImageDestinationFinalize(output) else { throw Failure.couldNotEncode }
        try Task.checkCancellation()
    }

    private static func decodePNG(at url: URL) throws -> CGImage {
        let noCache = [kCGImageSourceShouldCache: false] as CFDictionary
        guard let source = CGImageSourceCreateWithURL(url as CFURL, noCache),
              let sourceType = CGImageSourceGetType(source), sourceType as String == UTType.png.identifier,
              CGImageSourceGetCount(source) == 1,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, noCache) as? [String: Any],
              let width = (properties[kCGImagePropertyPixelWidth as String] as? NSNumber)?.intValue,
              let height = (properties[kCGImagePropertyPixelHeight as String] as? NSNumber)?.intValue else {
            throw Failure.corrupt
        }
        try validateDimensions(width: width, height: height)
        try Task.checkCancellation()
        let decodeNow = [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
        guard let image = CGImageSourceCreateImageAtIndex(source, 0, decodeNow),
              image.width == width, image.height == height else { throw Failure.corrupt }
        return image
    }

    private static func privateDirectory(in parent: URL) throws -> URL {
        let directory = parent.appendingPathComponent(".snipstudio-project-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        return directory
    }

    private static func openRegularFile(_ url: URL) throws -> FileHandle {
        try url.withUnsafeFileSystemRepresentation { path in
            guard let path else { throw Failure.invalidPath }
            let descriptor = Darwin.open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC)
            guard descriptor >= 0 else { throw posixError() }
            var information = stat()
            guard fstat(descriptor, &information) == 0 else {
                let error = posixError()
                Darwin.close(descriptor)
                throw error
            }
            guard information.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
                Darwin.close(descriptor)
                throw Failure.invalidPath
            }
            return FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        }
    }

    private static func openPrivateOutput(_ url: URL) throws -> FileHandle {
        try url.withUnsafeFileSystemRepresentation { path in
            guard let path else { throw Failure.invalidPath }
            let descriptor = Darwin.open(path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, mode_t(0o600))
            guard descriptor >= 0 else { throw posixError() }
            return FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        }
    }

    private static func size(of handle: FileHandle) throws -> UInt64 {
        let size = try handle.seekToEnd()
        try handle.seek(toOffset: 0)
        return size
    }

    private static func readExactly(_ count: Int, from handle: FileHandle) throws -> Data {
        var data = Data()
        data.reserveCapacity(count)
        while data.count < count {
            try Task.checkCancellation()
            guard let next = try handle.read(upToCount: min(chunkSize, count - data.count)), !next.isEmpty else {
                throw Failure.corrupt
            }
            data.append(next)
        }
        return data
    }

    private static func transfer(_ count: UInt64, from source: FileHandle, to output: FileHandle) throws -> Data {
        var remainder = count
        var digest = SHA256()
        while remainder > 0 {
            try Task.checkCancellation()
            guard let chunk = try source.read(upToCount: min(chunkSize, Int(remainder))), !chunk.isEmpty else {
                throw Failure.corrupt
            }
            digest.update(data: chunk)
            try output.write(contentsOf: chunk)
            remainder -= UInt64(chunk.count)
        }
        return Data(digest.finalize())
    }

    private static func replaceAtomically(_ staged: URL, destination: URL) throws {
        try staged.withUnsafeFileSystemRepresentation { from in
            try destination.withUnsafeFileSystemRepresentation { to in
                guard let from, let to else { throw Failure.invalidPath }
                guard Darwin.rename(from, to) == 0 else { throw posixError() }
            }
        }
    }

    private static func posixError() -> NSError {
        NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }

    private enum Failure: LocalizedError {
        case invalidPath, wrongFormat, unsupportedVersion, corrupt, invalidAnnotations, projectTooLarge, couldNotEncode

        var errorDescription: String? {
            switch self {
            case .invalidPath: "Choose a regular file on this Mac."
            case .wrongFormat: "This file is not an editable Snip Studio project for Mac."
            case .unsupportedVersion: "This editable project uses an unsupported format version."
            case .corrupt: "This editable project is incomplete or damaged."
            case .invalidAnnotations: "This editable project contains invalid annotation data."
            case .projectTooLarge: "The editable project exceeds the supported size."
            case .couldNotEncode: "Snip Studio could not encode the project’s source image."
            }
        }
    }
}

private extension Data {
    mutating func appendBigEndian(_ value: UInt64) {
        for shift in stride(from: 56, through: 0, by: -8) {
            append(UInt8(truncatingIfNeeded: value >> shift))
        }
    }

    func bigEndian(at offset: Int) -> UInt64 {
        self[offset..<(offset + 8)].reduce(0) { ($0 << 8) | UInt64($1) }
    }
}
