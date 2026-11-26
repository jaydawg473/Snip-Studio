import CoreGraphics
import CoreText
import Foundation

enum StudioEmojiRaster {
    static let pixelSizeRange: ClosedRange<CGFloat> = 1...512

    // Keep the Windows sticker order and accessible names, with the system's native Mac artwork.
    private static let catalog: [(glyph: String, name: String)] = [
        ("😀", "Smile"), ("😄", "Laugh"), ("🙂", "Happy"), ("😉", "Wink"), ("😍", "Love"), ("🤔", "Thinking"),
        ("👍", "Thumbs up"), ("👎", "Thumbs down"), ("👏", "Applause"), ("🙏", "Thank you"), ("💪", "Strong"), ("👀", "Look"),
        ("✅", "Check mark"), ("❌", "Cross"), ("⚠️", "Warning"), ("❗", "Important"), ("❓", "Question"), ("💡", "Idea"),
        ("⭐", "Star"), ("❤️", "Heart"), ("🔥", "Fire"), ("🎉", "Celebrate"), ("📌", "Pin"), ("➡️", "Right arrow")
    ]

    static let stickers = catalog.map(\.glyph)

    static func name(for glyph: String) -> String {
        catalog.first(where: { $0.glyph == glyph })?.name ?? glyph
    }

    static func image(for glyph: String, pixelSize: CGFloat) throws -> CGImage {
        guard pixelSizeRange.contains(pixelSize), glyph.utf16.prefix(65).count <= 64, glyph.count == 1 else {
            throw Failure.invalidSticker
        }
        let scalars = glyph.unicodeScalars
        guard scalars.contains(where: { $0.properties.isEmoji }),
              scalars.contains(where: { $0.properties.isEmojiPresentation || $0.value == 0xfe0f || $0.value == 0x20e3 }) else {
            throw Failure.invalidSticker
        }

        let side = Int(pixelSize.rounded(.up))
        let fontSize = CGFloat(max(side, 96))
        let font = CTFontCreateWithName("AppleColorEmoji" as CFString, fontSize, nil)
        let attributes: [NSAttributedString.Key: Any] = [NSAttributedString.Key(kCTFontAttributeName as String): font]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: glyph, attributes: attributes))
        guard usesColorGlyphs(line) else { throw Failure.unavailableArtwork }

        // Measure the native bitmap's alpha, which includes detail outside typographic glyph bounds.
        let probeSize = Int(3 * fontSize)
        let probe = try bitmap(side: probeSize)
        probe.textMatrix = .identity
        probe.textPosition = CGPoint(x: fontSize / 2, y: fontSize)
        CTLineDraw(line, probe)
        guard let visible = alphaBounds(in: probe), visible.minX > 0, visible.minY > 0,
              visible.maxX < CGFloat(probeSize), visible.maxY < CGFloat(probeSize),
              let source = probe.makeImage()?.cropping(to: visible) else { throw Failure.unavailableArtwork }

        let output = try bitmap(side: side)
        let padding: CGFloat = side >= 8 ? 1 : 0
        let available = CGFloat(side) - 2 * padding
        let scale = available / max(visible.width, visible.height)
        let width = visible.width * scale
        let height = visible.height * scale
        output.interpolationQuality = .high
        output.draw(source, in: CGRect(x: (CGFloat(side) - width) / 2, y: (CGFloat(side) - height) / 2,
                                       width: width, height: height))
        guard alphaBounds(in: output) != nil, let image = output.makeImage() else { throw Failure.unavailableArtwork }
        return image
    }

    fileprivate static func bitmap(side: Int) throws -> CGContext {
        let format = CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
                                      bytesPerRow: side * 4, space: colorSpace, bitmapInfo: format) else {
            throw Failure.allocation
        }
        context.clear(CGRect(x: 0, y: 0, width: side, height: side))
        return context
    }

    private static func alphaBounds(in context: CGContext) -> CGRect? {
        guard let data = context.data?.assumingMemoryBound(to: UInt8.self) else { return nil }
        var left = context.width
        var right = -1
        var top = context.height
        var bottom = -1
        for y in 0..<context.height {
            let row = data.advanced(by: y * context.bytesPerRow)
            for x in 0..<context.width where row[x * 4 + 3] != 0 {
                left = min(left, x)
                right = max(right, x)
                top = min(top, y)
                bottom = max(bottom, y)
            }
        }
        guard left <= right, top <= bottom else { return nil }
        return CGRect(x: left, y: top, width: right - left + 1, height: bottom - top + 1)
    }

    private static func usesColorGlyphs(_ line: CTLine) -> Bool {
        let runs = CTLineGetGlyphRuns(line)
        let fontKey = Unmanaged.passUnretained(kCTFontAttributeName).toOpaque()
        var hasGlyphs = false
        for index in 0..<CFArrayGetCount(runs) {
            guard let rawRun = CFArrayGetValueAtIndex(runs, index) else { return false }
            let run = Unmanaged<CTRun>.fromOpaque(rawRun).takeUnretainedValue()
            guard CTRunGetGlyphCount(run) > 0 else { continue }
            let attributes = CTRunGetAttributes(run)
            guard let rawFont = CFDictionaryGetValue(attributes, fontKey),
                  CFGetTypeID(Unmanaged<CFTypeRef>.fromOpaque(rawFont).takeUnretainedValue()) == CTFontGetTypeID() else {
                return false
            }
            let font = Unmanaged<CTFont>.fromOpaque(rawFont).takeUnretainedValue()
            guard CTFontGetSymbolicTraits(font).contains(.traitColorGlyphs) else { return false }
            hasGlyphs = true
        }
        return hasGlyphs
    }

    private enum Failure: LocalizedError {
        case invalidSticker, unavailableArtwork, allocation

        var errorDescription: String? {
            switch self {
            case .invalidSticker: "Choose one emoji sticker between 1 and 512 pixels."
            case .unavailableArtwork: "This Mac could not render the sticker in color. Choose another emoji."
            case .allocation: "Snip Studio could not allocate the emoji image. Try a smaller sticker."
            }
        }
    }
}

enum StudioEmojiRasterSelfCheck {
    static func run() throws {
        guard StudioEmojiRaster.stickers.count == 24,
              Set(StudioEmojiRaster.stickers).count == 24,
              StudioEmojiRaster.name(for: "⚠️") == "Warning",
              (try? StudioEmojiRaster.image(for: "😀😀", pixelSize: 48)) == nil,
              (try? StudioEmojiRaster.image(for: "A", pixelSize: 48)) == nil,
              (try? StudioEmojiRaster.image(for: "😀", pixelSize: 513)) == nil else {
            throw ValidationError.catalog
        }

        for glyph in ["😀", "⚠️", "❤️"] {
            let image = try StudioEmojiRaster.image(for: glyph, pixelSize: 64)
            let context = try StudioEmojiRaster.bitmap(side: 64)
            context.draw(image, in: CGRect(x: 0, y: 0, width: 64, height: 64))
            guard image.width == 64, image.height == 64,
                  let pixels = context.data?.assumingMemoryBound(to: UInt8.self), pixels[3] == 0 else {
                throw ValidationError.raster
            }
            let hasOpaqueColor = (0..<64).contains { y in
                let row = pixels.advanced(by: y * context.bytesPerRow)
                return (0..<64).contains { x in
                    let pixel = row.advanced(by: x * 4)
                    let spread = Int(max(pixel[0], pixel[1], pixel[2])) - Int(min(pixel[0], pixel[1], pixel[2]))
                    return pixel[3] >= 220 && spread >= 32
                }
            }
            guard hasOpaqueColor else { throw ValidationError.raster }
        }
        print("PASS: color emoji catalog, transparent padding, variant glyphs, and input limits")
    }

    private enum ValidationError: Error {
        case catalog, raster
    }
}
