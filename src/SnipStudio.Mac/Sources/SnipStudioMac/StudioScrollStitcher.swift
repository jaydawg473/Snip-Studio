import CoreGraphics
import Foundation

/// Stitches successive views of the same viewport after a downward scroll.
/// A rejected sample leaves the last accepted viewport and image intact.
struct StudioScrollStitcher: Sendable {
    enum Outcome: Equatable, Sendable {
        case firstFrame
        case appended(rows: Int)
        case repeated
        case ambiguous
        case noMatch
        case sizeLimit
    }

    private static let maximumPixels = 64_000_000
    private static let maximumWidth = 16_384
    private static let maximumHeight = 30_000
    private static let maximumFrames = 100
    private static let bitmapInfo = CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue

    private var strips: [CGImage] = []
    private var previous: SampleFrame?
    private(set) var outputHeight = 0
    private(set) var frameCount = 0

    var joinRows: [Int] {
        var row = 0
        return strips.dropLast().map { strip in
            row += strip.height
            return row
        }
    }

    mutating func add(_ frame: CGImage) throws -> Outcome {
        try Task.checkCancellation()
        if let previous {
            guard previous.width == frame.width, previous.height == frame.height else { return .noMatch }
        }
        guard frame.width >= 32, frame.height >= 64 else { throw Failure.areaTooSmall }
        guard frame.width <= Self.maximumWidth, frame.height <= Self.maximumHeight,
              frame.width <= Self.maximumPixels / frame.height,
              frameCount < Self.maximumFrames else { return .sizeLimit }

        let next = try SampleFrame(frame)
        guard let previous, let first = strips.first else {
            strips = [frame]
            self.previous = next
            outputHeight = frame.height
            frameCount = 1
            return .firstFrame
        }
        if try Self.unchanged(previous, next) { return .repeated }

        let top = Self.stableEdge(previous, next, fromTop: true)
        let bottom = Self.stableEdge(previous, next, fromTop: false)
        let matches = try Self.matchOffsets(previous, next, top: top, bottom: bottom)
        guard let best = matches.first else { return .noMatch }
        let comparableError = best.error + max(0.65, best.error * 0.65)
        if matches.dropFirst().contains(where: { $0.error < comparableError }) { return .ambiguous }
        let backwards = try Self.matchOffsets(next, previous, top: top, bottom: bottom)
        if backwards.contains(where: { $0.error < comparableError }) { return .ambiguous }

        let height = outputHeight + best.offset
        guard height <= Self.maximumHeight, frame.width <= Self.maximumPixels / height else { return .sizeLimit }

        // Drop the old stationary footer and append the newly exposed rows and the latest footer.
        let colorSpace = try Self.outputColorSpace(for: first)
        let addition = try Self.copyRows(
            from: frame, start: frame.height - bottom - best.offset,
            count: bottom + best.offset, colorSpace: colorSpace
        )
        var revised = strips
        var toDiscard = bottom
        while toDiscard > 0, let tail = revised.popLast() {
            if tail.height > toDiscard {
                revised.append(try Self.copyRows(from: tail, start: 0, count: tail.height - toDiscard,
                                                 colorSpace: colorSpace))
                break
            }
            toDiscard -= tail.height
        }
        revised.append(addition)
        try Task.checkCancellation()
        strips = revised
        self.previous = next
        outputHeight = height
        frameCount += 1
        return .appended(rows: best.offset)
    }

    func makeImage() throws -> CGImage? {
        try Task.checkCancellation()
        guard let first = strips.first else { return nil }
        if strips.count == 1 { return first }
        let colorSpace = try Self.outputColorSpace(for: first)
        let drawing = try Self.makeBitmap(width: first.width, height: outputHeight, colorSpace: colorSpace)
        var top = 0
        for strip in strips {
            try Task.checkCancellation()
            drawing.draw(strip, in: CGRect(x: 0, y: outputHeight - top - strip.height,
                                          width: first.width, height: strip.height))
            top += strip.height
        }
        guard let image = drawing.makeImage() else { throw Failure.cannotRender }
        return image
    }

    private static func copyRows(from image: CGImage, start: Int, count: Int, colorSpace: CGColorSpace) throws -> CGImage {
        if start == 0, count == image.height { return image }
        guard let crop = image.cropping(to: CGRect(x: 0, y: start, width: image.width, height: count)) else {
            throw Failure.cannotRender
        }
        // Materialize the crop so a short strip cannot retain every source viewport's pixels.
        let drawing = try makeBitmap(width: image.width, height: count, colorSpace: colorSpace)
        drawing.draw(crop, in: CGRect(x: 0, y: 0, width: image.width, height: count))
        guard let strip = drawing.makeImage() else { throw Failure.cannotRender }
        return strip
    }

    private static func outputColorSpace(for image: CGImage) throws -> CGColorSpace {
        if let colorSpace = image.colorSpace, colorSpace.model == .rgb { return colorSpace }
        guard let sRGB = CGColorSpace(name: CGColorSpace.sRGB) else { throw Failure.cannotRender }
        return sRGB
    }

    private static func makeBitmap(width: Int, height: Int, colorSpace: CGColorSpace) throws -> CGContext {
        guard let drawing = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: colorSpace, bitmapInfo: bitmapInfo) else {
            throw Failure.cannotRender
        }
        drawing.setBlendMode(.copy)
        drawing.interpolationQuality = .none
        return drawing
    }

    private static func unchanged(_ old: SampleFrame, _ next: SampleFrame) throws -> Bool {
        var total = 0.0
        var changed = 0
        let totalSamples = old.height * old.columns
        let changeLimit = max(1, totalSamples / 500)
        for row in 0..<old.height {
            if row.isMultiple(of: 64) { try Task.checkCancellation() }
            let error = old.error(row: row, against: next, row: row)
            total += error.mean
            changed += error.changed
            if changed > changeLimit || total > Double(old.height) * 0.15 { return false }
        }
        return true
    }

    private static func stableEdge(_ old: SampleFrame, _ next: SampleFrame, fromTop: Bool) -> Int {
        let limit = old.height * 45 / 100
        for offset in 0..<limit {
            let row = fromTop ? offset : old.height - 1 - offset
            let error = old.error(row: row, against: next, row: row)
            if error.mean > 0.8 || error.changed > max(1, old.columns / 50) { return offset }
        }
        return limit
    }

    private struct Match {
        let offset: Int
        let error: Double
    }

    private static func matchOffsets(_ old: SampleFrame, _ next: SampleFrame, top: Int, bottom: Int) throws -> [Match] {
        let height = old.height
        let body = height - top - bottom
        let maximumOffset = min(height * 4 / 5, body - max(24, height / 10))
        guard body >= max(48, height / 5), maximumOffset >= 1 else { return [] }
        var candidates: [Match] = []
        for offset in 1...maximumOffset {
            if offset.isMultiple(of: 16) { try Task.checkCancellation() }
            let overlap = body - offset
            if let error = score(old, next, offset: offset, top: top, overlap: overlap, verify: false) {
                candidates.append(Match(offset: offset, error: error))
            }
        }
        candidates.sort { $0.error < $1.error }
        var verified: [Match] = []
        for candidate in candidates.prefix(48) {
            try Task.checkCancellation()
            let overlap = body - candidate.offset
            if let error = score(old, next, offset: candidate.offset, top: top, overlap: overlap, verify: true) {
                verified.append(Match(offset: candidate.offset, error: error))
            }
        }
        // Too many plausible offsets are already evidence of a repeated pattern.
        if candidates.count > 48, let sample = verified.first { return [sample, sample] }
        return verified.sorted { $0.error < $1.error }
    }

    private static func score(_ old: SampleFrame, _ next: SampleFrame, offset: Int, top: Int,
                              overlap: Int, verify: Bool) -> Double? {
        let rows = verify ? overlap : min(overlap, 56)
        let edge = max(5, min(24, overlap / 12))
        var features = 0
        var firstFeature = -1
        var lastFeature = -1
        var errors = 0.0
        var featureErrors = 0.0
        var badRows = 0
        var changedPixels = 0
        for index in 0..<rows {
            let position = index * (overlap - 1) / (rows - 1)
            let row = top + position
            let error = old.error(row: row + offset, against: next, row: row, columnStride: verify ? 1 : 4)
            if verify && (position < edge || position >= overlap - edge) &&
                (error.mean > 0.8 || error.changed > max(1, old.columns / 50)) { return nil }
            errors += error.mean
            changedPixels += error.changed
            if error.mean > 5 { badRows += 1 }
            if max(old.energy[row + offset], next.energy[row]) >= 0.6 {
                features += 1
                featureErrors += error.mean
                if firstFeature < 0 { firstFeature = position }
                lastFeature = position
            }
            if errors > Double(rows) * (verify ? 2 : 3.5) || badRows > max(2, rows / (verify ? 25 : 9)) {
                return nil
            }
        }
        guard features >= min(8, max(4, rows / 4)), lastFeature - firstFeature >= min(48, overlap / 3),
              !verify || changedPixels <= max(2, rows * old.columns / 40) else { return nil }
        let result = max(featureErrors / Double(features), errors / Double(rows))
        return result <= (verify ? 2 : 3.5) ? result : nil
    }

    private struct SampleFrame: Sendable {
        let width: Int
        let height: Int
        let columns: Int
        let colors: [UInt8]
        let energy: [Double]

        init(_ image: CGImage) throws {
            let width = image.width
            let height = image.height
            let left = max(1, width / 100)
            let right = width - 1 - max(1, width / 50)
            let columns = min(128, right - left + 1)
            guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else { throw Failure.cannotRender }
            let bitmap = try StudioScrollStitcher.makeBitmap(width: width, height: height, colorSpace: colorSpace)
            bitmap.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            guard let pixels = bitmap.data?.assumingMemoryBound(to: UInt8.self) else { throw Failure.cannotRender }
            let xSamples = (0..<columns).map { x in 4 * (left + x * (right - left) / (columns - 1)) }
            let stride = bitmap.bytesPerRow
            var colors = [UInt8](repeating: 0, count: height * columns * 4)
            var energy = [Double](repeating: 0, count: height)
            for row in 0..<height {
                if row.isMultiple(of: 64) { try Task.checkCancellation() }
                let base = row * columns * 4
                let source = pixels.advanced(by: row * stride)
                var variation = 0
                for column in 0..<columns {
                    let destination = base + column * 4
                    let position = xSamples[column]
                    for channel in 0..<4 {
                        let target = destination + channel
                        let value = source[position + channel]
                        colors[target] = value
                        if column > 0 { variation += abs(Int(value) - Int(colors[target - 4])) }
                        if row > 0 { variation += abs(Int(value) - Int(colors[target - 4 * columns])) }
                    }
                }
                energy[row] = Double(variation) / Double(max(1, columns - 1) * 8)
            }
            self.width = width
            self.height = height
            self.columns = columns
            self.colors = colors
            self.energy = energy
        }

        func error(row: Int, against other: SampleFrame, row otherRow: Int, columnStride: Int = 1)
            -> (mean: Double, changed: Int) {
            let offset = row * columns * 4
            let otherOffset = otherRow * columns * 4
            var absoluteError = 0
            var changed = 0
            for column in stride(from: 0, to: columns, by: columnStride) {
                let index = offset + column * 4
                let target = otherOffset + column * 4
                let delta = abs(Int(colors[index]) - Int(other.colors[target]))
                    + abs(Int(colors[index + 1]) - Int(other.colors[target + 1]))
                    + abs(Int(colors[index + 2]) - Int(other.colors[target + 2]))
                    + abs(Int(colors[index + 3]) - Int(other.colors[target + 3]))
                absoluteError += delta
                if delta > 18 { changed += 1 }
            }
            let samples = (columns + columnStride - 1) / columnStride
            return (Double(absoluteError) / Double(samples * 4), changed)
        }
    }

    private enum Failure: LocalizedError {
        case areaTooSmall
        case cannotRender

        var errorDescription: String? {
            switch self {
            case .areaTooSmall: "Choose a scrolling area at least 32 pixels wide and 64 pixels tall."
            case .cannotRender: "Snip Desk could not render the scrolling capture."
            }
        }
    }
}

extension StudioScrollStitcher {
    static func selfCheck() throws {
        let first = try fixture(height: 128, offset: 0)
        let second = try fixture(height: 128, offset: 38)
        let third = try fixture(height: 128, offset: 64)
        var stitch = StudioScrollStitcher()
        try check(stitch.add(first) == .firstFrame, "accept the initial viewport")
        try check(stitch.add(first) == .repeated, "reject an unchanged sample")
        try check(stitch.add(second) == .appended(rows: 38), "align the first scroll")
        try check(stitch.add(first) == .noMatch, "refuse a reverse scroll")
        let mismatched = try fixture(height: 128, offset: 64, horizontalOffset: 9)
        try check(stitch.add(mismatched) == .noMatch, "refuse horizontal movement")
        try check(stitch.frameCount == 2 && stitch.outputHeight == 166, "rejected input preserves accepted data")
        try check(stitch.add(third) == .appended(rows: 26), "keep matching the last accepted viewport")
        try check(stitch.joinRows == [118, 156], "keep the stitch boundaries separate from output pixels")
        guard let stitched = try stitch.makeImage() else { throw CheckFailure("render accepted rows") }
        let expected = try fixture(height: 192, offset: 0)
        try check(stitched.width == expected.width && stitched.height == expected.height, "preserve output dimensions")
        try check(normalizedBytes(stitched) == normalizedBytes(expected), "preserve exact pixels and show fixed chrome once")

        var repeated = StudioScrollStitcher()
        _ = try repeated.add(fixture(height: 128, offset: 0, period: 20))
        try check(repeated.add(fixture(height: 128, offset: 8, period: 20)) == .ambiguous,
                  "refuse multiple plausible alignments")
        try check(repeated.frameCount == 1 && repeated.outputHeight == 128, "ambiguity keeps the first frame")
        print("PASS: scrolling overlap, fixed chrome, pixel preservation, and rejected samples")
    }

    private static func check(_ condition: Bool, _ detail: String) throws {
        if !condition { throw CheckFailure(detail) }
    }

    private static func fixture(height: Int, offset: Int, horizontalOffset: Int = 0, period: Int? = nil) throws -> CGImage {
        let width = 80
        var bytes = [UInt8]()
        bytes.reserveCapacity(width * height * 4)
        for y in 0..<height {
            let bodyRow = y - 8 + offset
            let row: Int
            if y < 8 { row = 6_500 + y }
            else if y >= height - 10 { row = 8_500 + y - (height - 10) }
            else { row = period.map { bodyRow % $0 } ?? bodyRow }
            for x in 0..<width {
                // Vary both axes so an accidental vertical match cannot explain horizontal motion.
                var value = UInt32((row + 1) * 25_873 + (x + horizontalOffset + 1) * 17_209)
                value = (value ^ (value >> 16)) &* 0x7feb352d
                value = (value ^ (value >> 15)) &* 0x846ca68b
                value ^= value >> 16
                bytes.append(UInt8(truncatingIfNeeded: value))
                bytes.append(UInt8(truncatingIfNeeded: value >> 8))
                bytes.append(UInt8(truncatingIfNeeded: value >> 16))
                bytes.append(255)
            }
        }
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let provider = CGDataProvider(data: Data(bytes) as CFData),
              let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                                  bytesPerRow: width * 4, space: colorSpace, bitmapInfo: CGBitmapInfo(rawValue: bitmapInfo),
                                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) else {
            throw CheckFailure("create synthetic viewport")
        }
        return image
    }

    private static func normalizedBytes(_ image: CGImage) throws -> Data {
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else { throw CheckFailure("create reference color space") }
        let context = try makeBitmap(width: image.width, height: image.height, colorSpace: colorSpace)
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let data = context.data else { throw CheckFailure("read stitched pixels") }
        var bytes = Data()
        for row in 0..<image.height {
            bytes.append(data.advanced(by: row * context.bytesPerRow).assumingMemoryBound(to: UInt8.self),
                         count: image.width * 4)
        }
        return bytes
    }

    private struct CheckFailure: LocalizedError {
        let detail: String
        init(_ detail: String) { self.detail = detail }
        var errorDescription: String? { "Scrolling capture check could not \(detail)." }
    }
}
