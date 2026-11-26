import Compression
import CoreGraphics
import CryptoKit
import Foundation

enum StudioWindowsProjectFile {
    static let filenameExtension = "snipstudio"
    static let magic = Data("SNIPEDIT".utf8)

    private static let maximumFileBytes: UInt64 = 300 * 1024 * 1024
    private static let maximumMetadataBytes: UInt32 = 24 * 1024 * 1024
    private static let maximumDimension: UInt32 = 32_768
    private static let maximumPixels: UInt64 = 64_000_000
    private static let maximumMaskWrites = 128_000_000
    private static let digestBytes = 32
    private static let fixedBytes: UInt64 = 80
    private static let chunkBytes = 256 * 1024

    // The caller opened a regular file and consumed the eight-byte format signature.
    static func read(afterMagicFrom input: FileHandle, fileBytes: UInt64) throws -> StudioProjectContents {
        guard fileBytes <= maximumFileBytes else { throw Failure.tooLarge }
        let header = try StudioProjectFile.readExactly(8 + digestBytes, from: input)
        let version = littleEndian32(header, at: 0)
        guard (1...3).contains(version) else { throw Failure.unsupportedVersion }
        let count = littleEndian32(header, at: 4)
        guard (12...maximumMetadataBytes).contains(count), fixedBytes + UInt64(count) < fileBytes else {
            throw Failure.corrupt
        }
        let metadata = try StudioProjectFile.readExactly(Int(count), from: input)
        guard SHA256.hash(data: metadata).elementsEqual(header.dropFirst(8)) else { throw Failure.corrupt }
        let width = littleEndian32(metadata, at: 0)
        let height = littleEndian32(metadata, at: 4)
        let area = UInt64(width) * UInt64(height)
        guard width > 0, height > 0 else { throw Failure.corrupt }
        guard width <= maximumDimension, height <= maximumDimension, area <= maximumPixels else { throw Failure.tooLarge }

        let decoded = try StudioWindowsProjectAnnotations.decode(Data(metadata.dropFirst(8)), version: version)
        let expectedDigest = try StudioProjectFile.readExactly(digestBytes, from: input)
        var pixels = try inflate(from: input, bytesRemaining: fileBytes - fixedBytes - UInt64(count), pixelBytes: Int(area) * 4)
        guard SHA256.hash(data: pixels).elementsEqual(expectedDigest) else { throw Failure.corrupt }
        try Task.checkCancellation()
        // Windows puts source masks beneath all editable objects. Bake them into the copy so
        // removing or moving an annotation cannot expose the original covered source pixels.
        if !decoded.sourceMasks.isEmpty {
            try applySourceMasks(decoded.sourceMasks, to: &pixels, width: Int(width), height: Int(height))
        }

        // Format32bppArgb stores straight BGRA bytes in logical top-to-bottom row order.
        let pixelFormat = CGBitmapInfo.byteOrder32Little.union(CGBitmapInfo(rawValue: CGImageAlphaInfo.first.rawValue))
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB), let provider = CGDataProvider(data: pixels as CFData),
              let image = CGImage(width: Int(width), height: Int(height), bitsPerComponent: 8, bitsPerPixel: 32,
                                  bytesPerRow: Int(width) * 4, space: colorSpace, bitmapInfo: pixelFormat,
                                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) else {
            throw Failure.corrupt
        }
        return StudioProjectContents(image: image, annotations: decoded.annotations,
                                     containsBakedWindowsSourceMasks: !decoded.sourceMasks.isEmpty)
    }

    private static func applySourceMasks(_ masks: [StudioWindowsProjectAnnotations.SourceMask], to pixels: inout Data,
                                         width: Int, height: Int) throws {
        let bounds = CGRect(x: 0, y: 0, width: width, height: height)
        var remainingWrites = maximumMaskWrites
        try pixels.withUnsafeMutableBytes { (raster: UnsafeMutableRawBufferPointer) in
            guard let destination = raster.baseAddress else { throw Failure.corrupt }
            for mask in masks {
                try Task.checkCancellation()
                // Windows GDI+ antialiases the right and bottom edge into one more pixel.
                // Replace those pixels too, even if the Windows view only partially covered them.
                let rect = mask.rect
                let covered = CGRect(x: rect.minX, y: rect.minY, width: rect.width + 1, height: rect.height + 1)
                let box = covered.intersection(bounds)
                guard !box.isEmpty else { continue }
                let x = Int(box.minX)
                let y = Int(box.minY)
                let columns = Int(box.width)
                let rows = Int(box.height)
                let writes = columns * rows
                guard writes <= remainingWrites else { throw Failure.tooManyMaskPixels }
                remainingWrites -= writes
                // The little-endian bytes of the stored ARGB word are already in bitmap BGRA order.
                let row = [UInt32](repeating: mask.argb.littleEndian, count: columns)
                try row.withUnsafeBytes { source in
                    guard let start = source.baseAddress else { throw Failure.corrupt }
                    for offset in 0..<rows {
                        if offset % 256 == 0 { try Task.checkCancellation() }
                        destination.advanced(by: ((y + offset) * width + x) * 4)
                            .copyMemory(from: start, byteCount: source.count)
                    }
                }
            }
        }
    }

    private static func inflate(from input: FileHandle, bytesRemaining: UInt64, pixelBytes: Int) throws -> Data {
        let stream = UnsafeMutablePointer<compression_stream>.allocate(capacity: 1)
        defer { stream.deallocate() }
        guard compression_stream_init(stream, COMPRESSION_STREAM_DECODE, COMPRESSION_ZLIB) == COMPRESSION_STATUS_OK else {
            throw Failure.corrupt
        }
        defer { compression_stream_destroy(stream) }

        // Reserve an extra byte to allow the end marker to be read and detect surplus output.
        var pixels = Data(count: pixelBytes + 1)
        try pixels.withUnsafeMutableBytes { output in
            guard let destination = output.bindMemory(to: UInt8.self).baseAddress else { throw Failure.corrupt }
            stream.pointee.dst_ptr = destination
            stream.pointee.dst_size = output.count
            var remaining = bytesRemaining
            var finished = false
            while !finished {
                try Task.checkCancellation()
                let block = try StudioProjectFile.readExactly(min(chunkBytes, Int(remaining)), from: input)
                remaining -= UInt64(block.count)
                finished = try block.withUnsafeBytes { source in
                    stream.pointee.src_ptr = source.bindMemory(to: UInt8.self).baseAddress ?? UnsafePointer(destination)
                    stream.pointee.src_size = source.count
                    let flags = remaining == 0 ? Int32(COMPRESSION_STREAM_FINALIZE.rawValue) : 0
                    while true {
                        try Task.checkCancellation()
                        let inputBefore = stream.pointee.src_size
                        let outputBefore = stream.pointee.dst_size
                        let status = compression_stream_process(stream, flags)
                        if status == COMPRESSION_STATUS_END {
                            guard remaining == 0, stream.pointee.src_size == 0, stream.pointee.dst_size == 1 else {
                                throw Failure.corrupt
                            }
                            return true
                        }
                        guard status == COMPRESSION_STATUS_OK, stream.pointee.dst_size > 0,
                              stream.pointee.src_size < inputBefore || stream.pointee.dst_size < outputBefore else {
                            throw Failure.corrupt
                        }
                        if stream.pointee.src_size == 0, remaining > 0 { return false }
                    }
                }
            }
        }
        pixels.removeLast()
        return pixels
    }

    private static func littleEndian32(_ data: Data, at offset: Int) -> UInt32 {
        data.withUnsafeBytes { UInt32(littleEndian: $0.loadUnaligned(fromByteOffset: offset, as: UInt32.self)) }
    }

    private enum Failure: LocalizedError {
        case unsupportedVersion, corrupt, tooLarge, tooManyMaskPixels

        var errorDescription: String? {
            switch self {
            case .unsupportedVersion: "This Windows editable snip uses an unsupported format version."
            case .corrupt: "This Windows editable snip is incomplete or damaged."
            case .tooLarge: "This Windows editable snip exceeds the supported file or image size."
            case .tooManyMaskPixels: "This Windows editable snip contains too many overlapping or large source-text masks to open on Mac."
            }
        }
    }
}
