import CoreGraphics
import CoreText
import Foundation

struct StudioTextAnnotation: Codable, Equatable, Sendable {
    var text: String
    var fontName = "Helvetica"
    var pixelSize = 18.0
    var bold = false
    var italic = false
    var underline = false

    static let maximumUTF16Length = 8_192
    static let pixelSizeRange = 1.0...512.0

    struct Layout {
        let outline: CGPath
        let bounds: CGRect
        let hasUnsupportedGlyphs: Bool
    }

    func makeOutline(at origin: CGPoint) -> CGPath {
        guard let layout = layout(at: origin), !layout.hasUnsupportedGlyphs else { return CGMutablePath() }
        return layout.outline
    }

    func bounds(at origin: CGPoint) -> CGRect {
        layout(at: origin)?.bounds ?? .null
    }

    var hasUnsupportedGlyphs: Bool {
        layout(at: .zero)?.hasUnsupportedGlyphs ?? false
    }

    func layout(at origin: CGPoint) -> Layout? {
        guard origin.x.isFinite, origin.y.isFinite, Self.pixelSizeRange.contains(pixelSize),
              !fontName.isEmpty, fontName.utf16.prefix(257).count <= 256,
              text.utf16.prefix(Self.maximumUTF16Length + 1).count <= Self.maximumUTF16Length else { return nil }

        let font = makeFont()
        let attributes: [NSAttributedString.Key: Any] = [NSAttributedString.Key(kCTFontAttributeName as String): font]
        let outline = CGMutablePath()
        var requestedWidth: CGFloat = 0
        var lineTop = origin.y
        var hasUnsupportedGlyphs = false

        // Split by grapheme so CR/LF is a single break. Empty lines and the original text are retained.
        for segment in text.split(omittingEmptySubsequences: false, whereSeparator: \.isNewline) {
            let lineText = String(segment)
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: lineText, attributes: attributes))
            var ascent: CGFloat = 0
            var descent: CGFloat = 0
            var leading: CGFloat = 0
            let width = CGFloat(CTLineGetTypographicBounds(line, &ascent, &descent, &leading))
            ascent = max(ascent, CTFontGetAscent(font))
            descent = max(descent, CTFontGetDescent(font))
            leading = max(leading, CTFontGetLeading(font), 0)
            let baseline = lineTop + ascent

            if Self.appendLine(line, text: lineText, x: origin.x, baseline: baseline, to: outline) {
                hasUnsupportedGlyphs = true
            }
            if underline && width > 0 {
                let thickness = max(1, CTFontGetUnderlineThickness(font))
                let y = baseline - CTFontGetUnderlinePosition(font) - thickness / 2
                outline.addRect(CGRect(x: origin.x, y: y, width: width, height: thickness))
            }

            requestedWidth = max(requestedWidth, width)
            lineTop += max(1, ceil(ascent + descent + leading))
        }

        let logicalBounds = CGRect(x: origin.x, y: origin.y, width: requestedWidth, height: lineTop - origin.y)
        let bounds = outline.isEmpty ? logicalBounds : logicalBounds.union(outline.boundingBoxOfPath)
        return Layout(outline: outline, bounds: bounds, hasUnsupportedGlyphs: hasUnsupportedGlyphs)
    }

    private func makeFont() -> CTFont {
        let font = CTFontCreateWithName(fontName as CFString, CGFloat(pixelSize), nil)
        var traits: CTFontSymbolicTraits = []
        if bold { traits.insert(.traitBold) }
        if italic { traits.insert(.traitItalic) }
        guard !traits.isEmpty else { return font }
        if let styled = CTFontCreateCopyWithSymbolicTraits(font, 0, nil, traits, traits) { return styled }

        // If there is no combined face, use an available requested face of the same family.
        for trait in [CTFontSymbolicTraits.traitBold, .traitItalic] where traits.contains(trait) {
            if let styled = CTFontCreateCopyWithSymbolicTraits(font, 0, nil, trait, trait) { return styled }
        }
        return font
    }

    private static func appendLine(_ line: CTLine, text: String, x: CGFloat, baseline: CGFloat,
                                   to path: CGMutablePath) -> Bool {
        let runs = CTLineGetGlyphRuns(line)
        let characters = Array(text.utf16)
        var hasUnsupportedGlyphs = false

        for index in 0..<CFArrayGetCount(runs) {
            guard let rawRun = CFArrayGetValueAtIndex(runs, index) else { continue }
            let run = Unmanaged<CTRun>.fromOpaque(rawRun).takeUnretainedValue()
            let count = CTRunGetGlyphCount(run)
            guard count > 0 else { continue }
            guard let font = runFont(run) else {
                hasUnsupportedGlyphs = true
                continue
            }

            let range = CFRange(location: 0, length: count)
            var glyphs = [CGGlyph](repeating: 0, count: count)
            var positions = [CGPoint](repeating: .zero, count: count)
            var indices = [CFIndex](repeating: 0, count: count)
            CTRunGetGlyphs(run, range, &glyphs)
            CTRunGetPositions(run, range, &positions)
            CTRunGetStringIndices(run, range, &indices)

            let colorFont = CTFontGetSymbolicTraits(font).contains(.traitColorGlyphs)
            let matrix = CTRunGetTextMatrix(run)
            let glyphMatrix = CGAffineTransform(a: matrix.a, b: matrix.b, c: matrix.c, d: matrix.d, tx: 0, ty: 0)
            for glyphIndex in glyphs.indices {
                guard isVisibleSource(at: indices[glyphIndex], in: characters) else { continue }
                let glyph = glyphs[glyphIndex]
                guard glyph != 0, !colorFont, let glyphPath = CTFontCreatePathForGlyph(font, glyph, nil), !glyphPath.isEmpty else {
                    // The vector editor cannot represent a color bitmap or a missing glyph faithfully.
                    hasUnsupportedGlyphs = true
                    continue
                }
                let position = positions[glyphIndex]
                let imageMatrix = CGAffineTransform(a: 1, b: 0, c: 0, d: -1, tx: x + position.x, ty: baseline - position.y)
                path.addPath(glyphPath, transform: glyphMatrix.concatenating(imageMatrix))
            }
        }
        return hasUnsupportedGlyphs
    }

    private static func runFont(_ run: CTRun) -> CTFont? {
        // Core Text may substitute a font for a run, so its glyph IDs must use that exact font.
        let attributes = CTRunGetAttributes(run)
        let key = Unmanaged.passUnretained(kCTFontAttributeName).toOpaque()
        guard let rawFont = CFDictionaryGetValue(attributes, key),
              CFGetTypeID(Unmanaged<CFTypeRef>.fromOpaque(rawFont).takeUnretainedValue()) == CTFontGetTypeID() else { return nil }
        return Unmanaged<CTFont>.fromOpaque(rawFont).takeUnretainedValue()
    }

    private static func isVisibleSource(at index: Int, in text: [UInt16]) -> Bool {
        guard text.indices.contains(index) else { return true }
        let unit = text[index]
        if (0xfe00...0xfe0f).contains(unit) { return false }
        guard let scalar = UnicodeScalar(unit) else { return true }
        return !CharacterSet.whitespacesAndNewlines.contains(scalar) &&
            scalar.properties.generalCategory != .control && scalar.properties.generalCategory != .format
    }
}

enum StudioTextAnnotationSelfCheck {
    static func run() throws {
        let sample = StudioTextAnnotation(text: "0007\r\n\r\n  File.txt", fontName: "Menlo-Regular", pixelSize: 24)
        let singleLine = StudioTextAnnotation(text: "0007", fontName: sample.fontName, pixelSize: sample.pixelSize)
        let origin = CGPoint(x: 17, y: 31)
        guard let layout = sample.layout(at: origin), let single = singleLine.layout(at: origin),
              !layout.hasUnsupportedGlyphs, !layout.outline.isEmpty,
              layout.bounds.height > single.bounds.height * 2.5,
              layout.bounds.width > single.bounds.width,
              let moved = sample.layout(at: CGPoint(x: 37, y: 71)),
              abs(moved.bounds.minX - layout.bounds.minX - 20) < 0.01,
              abs(moved.bounds.minY - layout.bounds.minY - 40) < 0.01,
              abs(moved.outline.boundingBoxOfPath.minY - layout.outline.boundingBoxOfPath.minY - 40) < 0.01 else {
            throw ValidationError.geometry
        }

        var underlined = singleLine
        underlined.underline = true
        guard underlined.makeOutline(at: origin).boundingBoxOfPath.maxY > single.outline.boundingBoxOfPath.maxY,
              StudioTextAnnotation(text: "too large", pixelSize: 513).layout(at: .zero) == nil,
              StudioTextAnnotation(text: String(repeating: "a", count: 8_193)).layout(at: .zero) == nil else {
            throw ValidationError.geometry
        }

        let roundTrip = try JSONDecoder().decode(StudioTextAnnotation.self, from: JSONEncoder().encode(sample))
        guard roundTrip == sample, Array(roundTrip.text.utf16) == Array(sample.text.utf16) else {
            throw ValidationError.preservation
        }
        print("PASS: text annotation layout, underline, input limits, and verbatim encoding")
    }

    private enum ValidationError: Error {
        case geometry, preservation
    }
}
