import CoreGraphics
import Foundation

struct StudioOCRReplacement: Sendable {
    let region: CGRect
    let background: CGColor
    let foreground: CGColor
    let origin: CGPoint
    let note: StudioTextAnnotation

    private static let backgroundMargin = 3
    private static let backgroundVariation = 12
    private static let visibleContrast = 22

    static func prepare(_ line: RecognizedLine, in source: CGImage) throws -> StudioOCRReplacement {
        let unit = CGRect(x: 0, y: 0, width: 1, height: 1)
        let box = line.bounds
        guard box.minX.isFinite, box.minY.isFinite, box.maxX.isFinite, box.maxY.isFinite,
              box.width > 0, box.height > 0, unit.contains(box),
              line.text.contains(where: { !$0.isWhitespace }),
              !line.text.contains(where: \.isNewline),
              line.text.utf16.prefix(StudioTextAnnotation.maximumUTF16Length + 1).count <=
                StudioTextAnnotation.maximumUTF16Length else { throw Failure.invalidLine }

        let sourceWidth = CGFloat(source.width)
        let sourceHeight = CGFloat(source.height)
        // Vision uses a bottom-left origin. Image crops and editor notes use top-left pixels.
        let region = CGRect(x: box.minX * sourceWidth, y: (1 - box.maxY) * sourceHeight,
                            width: box.width * sourceWidth, height: box.height * sourceHeight)
            .integral.insetBy(dx: -1, dy: -1)
        let margin = CGFloat(backgroundMargin)
        let sample = region.insetBy(dx: -margin, dy: -margin)
        let imageBounds = CGRect(x: 0, y: 0, width: sourceWidth, height: sourceHeight)
        guard imageBounds.contains(sample), sample.width <= 8192, sample.height <= 512,
              sample.width * sample.height <= 1_048_576 else { throw Failure.noSafeMargin }

        let bitmap = try SampleBitmap(source: source, region: sample)
        let background = try sampleBackground(in: bitmap)
        let ink = try sampleInk(in: bitmap, over: background)
        let footprint = ink.bounds.offsetBy(dx: sample.minX, dy: sample.minY)
        let placement = try fitText(line.text, to: footprint)
        return StudioOCRReplacement(region: region, background: background.color, foreground: ink.color.color,
                                    origin: placement.origin, note: placement.note)
    }

    private static func sampleBackground(in bitmap: SampleBitmap) throws -> RGB {
        var sample = ColorSamples()
        for y in 0..<bitmap.height {
            for x in 0..<bitmap.width where !bitmap.isInterior(x: x, y: y) {
                sample.add(try bitmap.pixel(x: x, y: y))
            }
        }
        guard sample.variation <= backgroundVariation, let mean = sample.mean else {
            throw Failure.nonuniformBackground
        }
        return mean
    }

    private static func sampleInk(in bitmap: SampleBitmap, over background: RGB) throws -> (bounds: CGRect, color: RGB) {
        let margin = backgroundMargin
        var contrastCounts = [Int](repeating: 0, count: 256)
        var lowX = bitmap.width
        var highX = 0
        var lowY = bitmap.height
        var highY = 0
        var markedCount = 0
        for y in margin..<(bitmap.height - margin) {
            for x in margin..<(bitmap.width - margin) {
                let difference = try bitmap.pixel(x: x, y: y).distance(from: background)
                guard difference >= visibleContrast else { continue }
                // A foreground pixel at the edit boundary can be a clipped glyph or nearby art.
                guard x > margin, x < bitmap.width - margin - 1,
                      y > margin, y < bitmap.height - margin - 1 else { throw Failure.noSafeMargin }
                contrastCounts[difference] += 1
                markedCount += 1
                lowX = min(lowX, x)
                highX = max(highX, x)
                lowY = min(lowY, y)
                highY = max(highY, y)
            }
        }

        guard markedCount >= 5, highX > lowX, highY > lowY else { throw Failure.noClearInk }
        // Estimate ink from the bulk of the strongest pixels, omitting isolated peaks.
        let target = max(1, markedCount / 8)
        var included = 0
        let likelyPeak = contrastCounts.indices.reversed().first { difference in
            included += contrastCounts[difference]
            return included >= target
        } ?? 0
        guard likelyPeak >= 46 else { throw Failure.noClearInk }
        let coreContrast = max(38, likelyPeak * 3 / 4)

        var core = ColorSamples()
        for y in lowY...highY {
            for x in lowX...highX {
                let pixel = try bitmap.pixel(x: x, y: y)
                if pixel.distance(from: background) >= coreContrast { core.add(pixel) }
            }
        }
        // Reject mixed foreground colors. A limited spread permits ordinary antialiasing.
        guard let color = core.mean, core.variation <= 72, color.distance(from: background) >= 40 else {
            throw Failure.noClearInk
        }
        let bounds = CGRect(x: lowX, y: lowY, width: highX - lowX + 1, height: highY - lowY + 1)
        return (bounds, color)
    }

    private static func fitText(_ text: String, to footprint: CGRect) throws -> (origin: CGPoint, note: StudioTextAnnotation) {
        var closest: (note: StudioTextAnnotation, mismatch: Double)?
        for font in ["Helvetica", "Menlo-Regular"] {
            var note = StudioTextAnnotation(text: text, fontName: font, pixelSize: 36)
            let outline = note.makeOutline(at: .zero)
            let reference = outline.boundingBoxOfPath
            guard !outline.isEmpty, reference.width > 0, reference.height > 0 else { continue }

            let scaleX = footprint.width / reference.width
            let scaleY = footprint.height / reference.height
            let aspect = scaleX / scaleY
            let size = note.pixelSize * Double(min(scaleX, scaleY))
            guard (0.62...1.62).contains(aspect), StudioTextAnnotation.pixelSizeRange.contains(size) else { continue }
            note.pixelSize = size
            let mismatch = abs(log(Double(aspect)))
            if closest.map({ mismatch < $0.mismatch }) ?? true { closest = (note, mismatch) }
        }

        guard let closest else { throw Failure.unsupportedGeometry }
        let fitted = closest.note.makeOutline(at: .zero).boundingBoxOfPath
        let origin = CGPoint(x: footprint.minX - fitted.minX, y: footprint.minY - fitted.minY)
        return (origin, closest.note)
    }

    private struct RGB {
        let red: Int
        let green: Int
        let blue: Int

        func distance(from other: RGB) -> Int {
            max(abs(red - other.red), abs(green - other.green), abs(blue - other.blue))
        }

        var color: CGColor {
            CGColor(srgbRed: CGFloat(red) / 255, green: CGFloat(green) / 255,
                    blue: CGFloat(blue) / 255, alpha: 1)
        }
    }

    private struct ColorSamples {
        private var count = 0
        private var totalRed = 0
        private var totalGreen = 0
        private var totalBlue = 0
        private var low = RGB(red: 255, green: 255, blue: 255)
        private var high = RGB(red: 0, green: 0, blue: 0)

        mutating func add(_ pixel: RGB) {
            count += 1
            totalRed += pixel.red
            totalGreen += pixel.green
            totalBlue += pixel.blue
            low = RGB(red: min(low.red, pixel.red), green: min(low.green, pixel.green), blue: min(low.blue, pixel.blue))
            high = RGB(red: max(high.red, pixel.red), green: max(high.green, pixel.green), blue: max(high.blue, pixel.blue))
        }

        var variation: Int { low.distance(from: high) }
        var mean: RGB? {
            guard count > 0 else { return nil }
            return RGB(red: (totalRed + count / 2) / count, green: (totalGreen + count / 2) / count,
                       blue: (totalBlue + count / 2) / count)
        }
    }

    private struct SampleBitmap {
        let width: Int
        let height: Int
        private let context: CGContext

        init(source: CGImage, region: CGRect) throws {
            guard let crop = source.cropping(to: region),
                  let space = CGColorSpace(name: CGColorSpace.sRGB),
                  let context = CGContext(data: nil, width: crop.width, height: crop.height, bitsPerComponent: 8,
                                          bytesPerRow: crop.width * 4, space: space,
                                          bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue |
                                            CGImageAlphaInfo.premultipliedLast.rawValue) else {
                throw Failure.unreadableSource
            }
            width = crop.width
            height = crop.height
            self.context = context
            context.setBlendMode(.copy)
            context.draw(crop, in: CGRect(x: 0, y: 0, width: width, height: height))
        }

        func isInterior(x: Int, y: Int) -> Bool {
            let margin = StudioOCRReplacement.backgroundMargin
            return x >= margin && x < width - margin && y >= margin && y < height - margin
        }

        func pixel(x: Int, y: Int) throws -> RGB {
            guard let bytes = context.data?.assumingMemoryBound(to: UInt8.self) else { throw Failure.unreadableSource }
            let index = y * context.bytesPerRow + x * 4
            guard bytes[index + 3] == 255 else { throw Failure.nonuniformBackground }
            return RGB(red: Int(bytes[index]), green: Int(bytes[index + 1]), blue: Int(bytes[index + 2]))
        }
    }

    private enum Failure: LocalizedError {
        case invalidLine, noSafeMargin, nonuniformBackground, noClearInk, unsupportedGeometry, unreadableSource

        var errorDescription: String? {
            switch self {
            case .invalidLine: "Choose one recognized line within the screenshot."
            case .noSafeMargin: "This line has too little clear space around it for an in-place edit."
            case .nonuniformBackground: "This line crosses a background that cannot be sampled as one opaque color."
            case .noClearInk: "The original ink color could not be estimated from this line."
            case .unsupportedGeometry: "The recognized line could not be placed accurately with the available fonts."
            case .unreadableSource: "The screenshot pixels could not be sampled."
            }
        }
    }

    static func selfCheck() throws {
        let text = "  0007-file.txt"
        let darkInk = RGB(red: 25, green: 41, blue: 67)
        let lightGround = RGB(red: 244, green: 246, blue: 248)
        let light = try fixture(text: text, ink: darkInk, background: lightGround)
        let proposed = try prepare(light.line, in: light.image)
        let fitted = proposed.note.makeOutline(at: proposed.origin).boundingBoxOfPath
        let components = proposed.foreground.components ?? []
        guard Array(proposed.note.text.utf16) == Array(text.utf16), proposed.region.contains(light.footprint),
              abs(fitted.minX - light.footprint.minX) < 2, abs(fitted.minY - light.footprint.minY) < 2,
              fitted.width > light.footprint.width * 0.8, fitted.height > light.footprint.height * 0.8,
              components.count == 4,
              abs(components[0] - CGFloat(darkInk.red) / 255) < 0.16,
              abs(components[1] - CGFloat(darkInk.green) / 255) < 0.16,
              abs(components[2] - CGFloat(darkInk.blue) / 255) < 0.16 else { throw CheckFailure.placement }

        let dark = try fixture(text: text, ink: lightGround, background: darkInk)
        let onDark = try prepare(dark.line, in: dark.image)
        guard let darkBackground = onDark.background.components?.first, darkBackground < 0.2,
              let lightForeground = onDark.foreground.components?.first, lightForeground > 0.75 else {
            throw CheckFailure.placement
        }

        let textured = try fixture(text: text, ink: darkInk, background: lightGround, borderMark: true)
        do {
            _ = try prepare(textured.line, in: textured.image)
            throw CheckFailure.unsafeBackground
        } catch Failure.nonuniformBackground {
            // The nearby shape is outside the OCR box and must remain untouched.
        }
        print("PASS: OCR replacement preserves text, locates light/dark ink, and rejects textured borders")
    }

    private static func fixture(text: String, ink: RGB, background: RGB, borderMark: Bool = false) throws ->
        (image: CGImage, line: RecognizedLine, footprint: CGRect) {
        let width = 512
        let height = 256
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: width * 4, space: space,
                                      bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue |
                                        CGImageAlphaInfo.premultipliedLast.rawValue) else { throw CheckFailure.placement }
        context.setFillColor(background.color)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        let original = StudioTextAnnotation(text: text, fontName: "Menlo-Regular", pixelSize: 29)
        let outline = original.makeOutline(at: CGPoint(x: 40, y: 63))
        guard !outline.isEmpty else { throw CheckFailure.placement }
        let footprint = outline.boundingBoxOfPath.integral
        context.setFillColor(ink.color)
        context.addPath(outline)
        context.fillPath()
        if borderMark {
            context.fill(CGRect(x: footprint.minX - 3, y: floor(footprint.midY), width: 2, height: 1))
        }
        guard let source = context.makeImage() else { throw CheckFailure.placement }
        let normalized = CGRect(x: footprint.minX / CGFloat(width), y: 1 - footprint.maxY / CGFloat(height),
                                width: footprint.width / CGFloat(width), height: footprint.height / CGFloat(height))
        return (source, RecognizedLine(text: text, bounds: normalized), footprint)
    }

    private enum CheckFailure: Error {
        case placement, unsafeBackground
    }
}
