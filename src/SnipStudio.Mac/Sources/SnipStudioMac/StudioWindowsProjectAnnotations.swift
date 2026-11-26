import CoreGraphics
import Foundation

// Mirrors the annotation stream written by Windows EditorDocument.EditableProject.cs.
// The caller verifies the SNIPEDIT container, metadata digest, and bitmap separately.
enum StudioWindowsProjectAnnotations {
    private static let maximumBytes = 24 * 1024 * 1024 - 8
    private static let windowsAnnotationLimit = 16_384
    private static let nativeAnnotationLimit = 15_000
    private static let nativePointLimit = 750_000
    private static let maximumCoordinate: Float = 1_048_576

    static func decode(_ data: Data, version: UInt32) throws -> [StudioProjectAnnotation] {
        guard (1...3).contains(version) else { throw Failure.unsupportedVersion }
        guard (4...maximumBytes).contains(data.count) else { throw Failure.invalidMetadata }

        var input = Reader(data)
        let count = try input.integer()
        guard (0...windowsAnnotationLimit).contains(count), count <= input.remaining else {
            throw Failure.invalidMetadata
        }
        guard count <= nativeAnnotationLimit else { throw Failure.tooManyObjects }

        var result: [StudioProjectAnnotation] = []
        result.reserveCapacity(count)
        var remainingPoints = nativePointLimit
        var budget = RenderingBudget()
        for _ in 0..<count {
            try Task.checkCancellation()
            let annotation = try readAnnotation(from: &input, version: version, remainingPoints: remainingPoints)
            guard annotation.points.count <= remainingPoints else { throw Failure.tooManyObjects }
            remainingPoints -= annotation.points.count
            try budget.reserve(annotation)
            result.append(annotation)
        }
        guard input.remaining == 0 else { throw Failure.invalidMetadata }
        return result
    }

    private static func readAnnotation(from input: inout Reader, version: UInt32, remainingPoints: Int) throws -> StudioProjectAnnotation {
        switch try input.byte() {
        case 1:
            var ink = try input.color()
            let width = try input.number(in: 0.001...1024)
            let highlighter = try input.boolean()
            if highlighter && version < 3 { ink.alpha = 80 }
            let count = try input.elementCount(itemBytes: 8)
            guard count <= remainingPoints else { throw Failure.tooManyObjects }
            var points: [CGPoint] = []
            points.reserveCapacity(count)
            for index in 0..<count {
                if index % 1024 == 0 { try Task.checkCancellation() }
                points.append(try input.point())
            }
            return try annotation(points: points, ink: ink, width: width)
        case 2:
            return try readClosedShape(.rectangle, from: &input, version: version)
        case 3:
            return try readClosedShape(.ellipse, from: &input, version: version)
        case 4:
            return try readOpenShape(.line, from: &input, version: version)
        case 5:
            return try readOpenShape(.arrow, from: &input, version: version)
        case 6:
            _ = try input.point()
            _ = try input.number(in: 0...32768)
            _ = try input.number(in: 0...32768)
            _ = try input.string(maximumCharacters: 16_000)
            _ = try input.string(maximumCharacters: 1_048_576)
            throw Failure.unsupportedObject("Windows formatted text")
        case 7:
            return try readEmoji(from: &input)
        case 8:
            let fill = try input.color()
            let count = try input.elementCount(itemBytes: 16)
            guard count == 1 else { throw Failure.unsupportedObject("a grouped highlight") }
            let box = try input.rectangle()
            return try annotation(points: corners(of: box), ink: .clear, width: 1, shape: .rectangle, fill: fill)
        case 9:
            _ = try input.elementCount(itemBytes: 20)
            // On Windows these objects permanently track their source-pixel locations and are not selectable.
            throw Failure.unsupportedObject("a Windows source-text mask")
        default:
            throw Failure.unsupportedObject("an unknown Windows annotation")
        }
    }

    private static func readClosedShape(_ shape: SnipShape, from input: inout Reader, version: UInt32) throws -> StudioProjectAnnotation {
        let box = try input.rectangle()
        let degrees = version >= 2 ? try input.number(in: -180...180) : 0
        let style = try Style(from: &input, version: version)
        return try annotation(points: corners(of: box), ink: style.outline, width: style.width, shape: shape,
                              lineStyle: style.pattern, rotation: Rotation(degrees), fill: style.fill)
    }

    private static func readOpenShape(_ shape: SnipShape, from input: inout Reader, version: UInt32) throws -> StudioProjectAnnotation {
        let points = [try input.point(), try input.point()]
        let style = try Style(from: &input, version: version)
        // The Windows line/arrow renderer only uses the outline.
        return try annotation(points: points, ink: style.outline, width: style.width, shape: shape, lineStyle: style.pattern)
    }

    private static func readEmoji(from input: inout Reader) throws -> StudioProjectAnnotation {
        let key = try input.string(maximumCharacters: 256).lowercased()
        let box = try input.rectangle()
        let degrees = try input.number(in: -180...180)
        guard box.width >= 1, box.height >= 1 else { throw Failure.invalidMetadata }
        let normalized = key.replacingOccurrences(of: "\u{fe0f}", with: "")
        let glyph = emojiByWindowsKey[key] ?? StudioEmojiRaster.stickers.first {
            $0.replacingOccurrences(of: "\u{fe0f}", with: "") == normalized
        }
        guard let glyph else { throw Failure.unsupportedObject("an unrecognized Windows sticker") }
        let rasterSize = min(512, max(box.width, box.height).rounded(.up))
        return try annotation(points: [box.origin], ink: .clear, width: rasterSize,
                              rotation: Rotation(degrees), emoji: glyph, emojiFrameSize: box.size)
    }

    private static func annotation(points: [CGPoint], ink: Ink, width: CGFloat, shape: SnipShape? = nil,
                                   lineStyle: SnipLineStyle = .solid, rotation: Rotation = Rotation(0),
                                   fill: Ink? = nil, emoji: String? = nil, emojiFrameSize: CGSize? = nil) throws -> StudioProjectAnnotation {
        guard width <= 512 else { throw Failure.unsupportedObject("an outline or stroke wider than 512 pixels") }
        if let shape, points[0] == points[1] ||
            ((shape == .rectangle || shape == .ellipse) && (points[0].x == points[1].x || points[0].y == points[1].y)) {
            throw Failure.unsupportedObject("a shape with empty geometry")
        }
        return StudioProjectAnnotation(points: points, red: Ink.unit(ink.red), green: Ink.unit(ink.green),
                                       blue: Ink.unit(ink.blue), alpha: Ink.unit(ink.alpha), width: width,
                                       shape: shape?.rawValue, lineStyle: lineStyle.rawValue, quarterTurns: rotation.quarterTurns,
                                       text: nil, fill: fill?.components, emoji: emoji,
                                       rotationDegrees: rotation.degrees, emojiFrameSize: emojiFrameSize)
    }

    private static func corners(of rect: CGRect) -> [CGPoint] {
        [rect.origin, CGPoint(x: rect.maxX, y: rect.maxY)]
    }

    private struct Rotation {
        let quarterTurns: Int
        let degrees: CGFloat?

        init(_ degrees: CGFloat) {
            let turns = degrees / 90
            if turns == turns.rounded() {
                quarterTurns = (Int(turns) + 4) % 4
                self.degrees = nil
            } else {
                quarterTurns = 0
                self.degrees = degrees
            }
        }
    }

    private struct RenderingBudget {
        // Every imported sticker gets a detached bitmap and every dash becomes path geometry.
        private var availableStickerPixels = 16_000_000
        private var availablePatternMarks: CGFloat = 250_000

        mutating func reserve(_ annotation: StudioProjectAnnotation) throws {
            if annotation.emoji != nil {
                let side = Int(annotation.width.rounded(.up))
                let pixels = side * side
                guard pixels <= availableStickerPixels else { throw Failure.renderingLimit }
                availableStickerPixels -= pixels
            }
            guard let shape = annotation.shape, annotation.lineStyle != SnipLineStyle.solid.rawValue else { return }
            let first = annotation.points[0]
            let last = annotation.points[1]
            let dx = abs(last.x - first.x)
            let dy = abs(last.y - first.y)
            let length = shape == SnipShape.rectangle.rawValue || shape == SnipShape.ellipse.rawValue
                ? 2 * (dx + dy) : hypot(dx, dy)
            // Even the shortest supported repeating mark consumes at least two stroke widths.
            let estimatedMarks = (length / (2 * annotation.width)).rounded(.up) + 1
            guard estimatedMarks <= availablePatternMarks else { throw Failure.renderingLimit }
            availablePatternMarks -= estimatedMarks
        }
    }

    // Windows persists catalog keys rather than the native glyphs or embedded sticker artwork.
    private static let emojiByWindowsKey: [String: String] = [
        "smile": "😀", "laugh": "😄", "happy": "🙂", "wink": "😉", "love": "😍", "thinking": "🤔",
        "thumbs-up": "👍", "thumbs-down": "👎", "applause": "👏", "thank-you": "🙏", "strong": "💪", "look": "👀",
        "check-mark": "✅", "cross": "❌", "warning": "⚠️", "important": "❗", "question": "❓", "idea": "💡",
        "star": "⭐", "heart": "❤️", "fire": "🔥", "celebrate": "🎉", "pin": "📌", "right-arrow": "➡️"
    ]

    private struct Style {
        let fill: Ink?
        let outline: Ink
        let width: CGFloat
        let pattern: SnipLineStyle

        init(from input: inout Reader, version: UInt32) throws {
            let fill = try input.optionalColor()
            let outline = try input.optionalColor()
            width = try input.number(in: Float.leastNonzeroMagnitude...1024)
            let fillOpacity = try input.integer()
            let outlineOpacity = try input.integer()
            guard (0...100).contains(fillOpacity), (0...100).contains(outlineOpacity) else {
                throw Failure.invalidMetadata
            }
            self.fill = fill?.applying(opacity: fillOpacity)
            self.outline = outline?.applying(opacity: outlineOpacity) ?? .clear
            if version >= 3 {
                switch try input.byte() {
                case 0: pattern = .solid
                case 1: pattern = .dashed
                case 2: pattern = .dotted
                default: throw Failure.invalidMetadata
                }
            } else {
                pattern = .solid
            }
        }
    }

    private struct Ink {
        let red: UInt8
        let green: UInt8
        let blue: UInt8
        var alpha: UInt8

        static let clear = Ink(red: 0, green: 0, blue: 0, alpha: 0)

        var components: [CGFloat] { [Self.unit(red), Self.unit(green), Self.unit(blue), Self.unit(alpha)] }

        static func unit(_ value: UInt8) -> CGFloat { CGFloat(value) / 255 }

        func applying(opacity: Int) -> Ink {
            var effective = self
            effective.alpha = UInt8((Double(alpha) * Double(opacity) / 100).rounded(.toNearestOrEven))
            return effective
        }
    }

    private struct Reader {
        private let bytes: Data
        private var offset: Data.Index

        init(_ bytes: Data) {
            self.bytes = bytes
            offset = bytes.startIndex
        }

        var remaining: Int { bytes.endIndex - offset }

        mutating func byte() throws -> UInt8 {
            guard remaining > 0 else { throw Failure.invalidMetadata }
            defer { offset += 1 }
            return bytes[offset]
        }

        mutating func boolean() throws -> Bool {
            let value = try byte()
            guard value <= 1 else { throw Failure.invalidMetadata }
            return value == 1
        }

        mutating func integer() throws -> Int {
            Int(Int32(bitPattern: try word()))
        }

        private mutating func word() throws -> UInt32 {
            guard remaining >= 4 else { throw Failure.invalidMetadata }
            let low = UInt32(bytes[offset]) | (UInt32(bytes[offset + 1]) << 8)
            let high = UInt32(bytes[offset + 2]) | (UInt32(bytes[offset + 3]) << 8)
            offset += 4
            return low | (high << 16)
        }

        mutating func number(in range: ClosedRange<Float>) throws -> CGFloat {
            let value = Float(bitPattern: try word())
            guard value.isFinite, range.contains(value) else { throw Failure.invalidMetadata }
            return CGFloat(value)
        }

        mutating func point() throws -> CGPoint {
            let range = -maximumCoordinate...maximumCoordinate
            let x = try number(in: range)
            let y = try number(in: range)
            return CGPoint(x: x, y: y)
        }

        mutating func rectangle() throws -> CGRect {
            let location = try point()
            let range = Float.leastNonzeroMagnitude...maximumCoordinate
            let width = try number(in: range)
            let height = try number(in: range)
            return CGRect(origin: location, size: CGSize(width: width, height: height))
        }

        mutating func color() throws -> Ink {
            let argb = try word()
            return Ink(red: UInt8(truncatingIfNeeded: argb >> 16), green: UInt8(truncatingIfNeeded: argb >> 8),
                       blue: UInt8(truncatingIfNeeded: argb), alpha: UInt8(truncatingIfNeeded: argb >> 24))
        }

        mutating func optionalColor() throws -> Ink? {
            guard try boolean() else { return nil }
            return try color()
        }

        mutating func elementCount(itemBytes: Int) throws -> Int {
            let count = try integer()
            guard (1...100_000).contains(count), count <= remaining / itemBytes else { throw Failure.invalidMetadata }
            return count
        }

        mutating func string(maximumCharacters: Int) throws -> String {
            let count = try integer()
            guard (0...(maximumCharacters * 4)).contains(count), count <= remaining else { throw Failure.invalidMetadata }
            let end = offset + count
            let decoded = String(data: bytes[offset..<end], encoding: .utf8)
            offset = end
            guard let decoded, decoded.utf16.count <= maximumCharacters else { throw Failure.invalidMetadata }
            return decoded
        }
    }

    private enum Failure: LocalizedError {
        case unsupportedVersion, invalidMetadata, tooManyObjects, renderingLimit, unsupportedObject(String)

        var errorDescription: String? {
            switch self {
            case .unsupportedVersion: "This Windows editable snip uses an unsupported version."
            case .invalidMetadata: "This Windows editable snip contains incomplete or invalid annotation data."
            case .tooManyObjects: "This Windows editable snip exceeds the Mac project’s annotation limit."
            case .renderingLimit: "This Windows editable snip contains too much sticker or patterned-outline detail to open safely on Mac."
            case let .unsupportedObject(reason): "This snip contains \(reason) that the Mac editor cannot preserve. Open it in Snip Studio for Windows to keep the original annotations."
            }
        }
    }

    static func selfCheck() throws {
        // The Windows writer persists the same stroke bytes in all versions, but v1/v2
        // intentionally replace the stored alpha for highlighter strokes on readback.
        var strokes = Fixture(count: 1)
        strokes.stroke(color: 0x3311_2233, width: 5, highlighter: true, points: [(-12.5, 30), (20, 50)])
        for version in [UInt32(1), 2, 3] {
            let decoded = try decode(strokes.data, version: version)
            try check(decoded.count == 1, "one stroke was stored")
            let stroke = decoded[0]
            try check(stroke.alpha == Ink.unit(version == 3 ? 0x33 : 80), "highlighter alpha follows the file version")
            try check(stroke.width == 5 && stroke.red == Ink.unit(0x11) && stroke.blue == Ink.unit(0x33), "stroke style and channels")
            try check(stroke.points == [CGPoint(x: -12.5, y: 30), CGPoint(x: 20, y: 50)], "stroke coordinates and order")
        }

        var shapes = Fixture(count: 4)
        shapes.data.append(3)
        shapes.rectangle(20, 25, 80, 40)
        shapes.float(-37.5)
        shapes.style(fill: 0xFF00_FF66, outline: 0x0510_2030, width: 3, fillOpacity: 50, outlineOpacity: 50, pattern: 2)
        shapes.data.append(4)
        shapes.point(9, 8)
        shapes.point(7, 6)
        shapes.style(fill: nil, outline: 0x8043_3221, width: 2, fillOpacity: 100, outlineOpacity: 100, pattern: 1)
        shapes.data.append(7)
        shapes.string("CHECK-MARK")
        shapes.rectangle(2, 3, 40.25, 30.75)
        shapes.float(-23)
        shapes.data.append(8)
        shapes.word(0x5011_2233)
        shapes.word(1)
        shapes.rectangle(3, 4, 9, 8)
        let decoded = try decode(shapes.data, version: 3)
        try check(decoded.map(\.shape) == ["ellipse", "line", nil, "rectangle"], "annotation paint order")
        try check(decoded[0].rotationDegrees == -37.5 && decoded[0].quarterTurns == 0, "arbitrary clockwise shape geometry")
        try check(decoded[0].lineStyle == "dotted" && decoded[0].alpha == Ink.unit(2), "dotted outline and ties-to-even alpha")
        try check(decoded[0].fill == [0, 1, Ink.unit(0x66), Ink.unit(128)], "fill opacity and RGB")
        try check(decoded[1].lineStyle == "dashed" && decoded[1].points.first == CGPoint(x: 9, y: 8), "line pattern and endpoints")
        try check(decoded[2].emoji == "✅" && decoded[2].emojiFrameSize == CGSize(width: 40.25, height: 30.75)
                  && decoded[2].rotationDegrees == -23 && decoded[2].width == 41, "non-square sticker frame independently of raster size")
        try check(decoded[3].fill == [Ink.unit(0x11), Ink.unit(0x22), Ink.unit(0x33), Ink.unit(80)] && decoded[3].alpha == 0,
                  "single highlight preserves its fill without an outline")

        var rotated = Fixture(count: 1)
        rotated.data.append(2)
        rotated.rectangle(1, 2, 10, 20)
        rotated.float(-90)
        rotated.style(fill: nil, outline: 0xff00_0000, width: 2, fillOpacity: 100, outlineOpacity: 100)
        let v2 = try decode(rotated.data, version: 2)
        try check(v2[0].quarterTurns == 3 && v2[0].rotationDegrees == nil && v2[0].lineStyle == "solid", "version 2 cardinal shape rotation")

        var notes = Fixture(count: 1)
        notes.data.append(6)
        notes.point(1, 2)
        notes.float(20)
        notes.float(10)
        notes.string("ID 0027")
        notes.string("{\\rtf1 ID 0027}")
        do {
            _ = try decode(notes.data, version: 1)
            throw CheckFailure.failed("formatted Windows notes are rejected intact")
        } catch Failure.unsupportedObject("Windows formatted text") {}

        var extra = shapes.data
        extra.append(0)
        try checkInvalid(extra, version: 3)
        try checkInvalid(Data(shapes.data.dropLast()), version: 3)
        var short = Fixture(count: 2)
        short.data.append(1)
        try checkInvalid(short.data, version: 1)

        var excessive = Fixture(count: 1)
        excessive.data.append(4)
        excessive.point(0, 0)
        excessive.point(1_048_576, 0)
        excessive.style(fill: nil, outline: 0xff00_0000, width: 0.000001, fillOpacity: 100, outlineOpacity: 100, pattern: 2)
        do {
            _ = try decode(excessive.data, version: 3)
            throw CheckFailure.failed("unbounded dotted path generation is rejected before opening")
        } catch Failure.renderingLimit {}
        print("PASS: Windows project annotation versions, geometry, opacity, and malformed records")
    }

    private static func check(_ condition: @autoclosure () -> Bool, _ description: String) throws {
        guard condition() else { throw CheckFailure.failed(description) }
    }

    private static func checkInvalid(_ data: Data, version: UInt32) throws {
        do {
            _ = try decode(data, version: version)
            throw CheckFailure.failed("malformed Windows annotation metadata is rejected")
        } catch Failure.invalidMetadata {}
    }

    private enum CheckFailure: LocalizedError {
        case failed(String)

        var errorDescription: String? {
            switch self { case let .failed(description): "Windows project annotation check failed: \(description)." }
        }
    }

    private struct Fixture {
        var data = Data()

        init(count: UInt32) { word(count) }

        mutating func word(_ value: UInt32) {
            for shift in stride(from: 0, through: 24, by: 8) {
                data.append(UInt8(truncatingIfNeeded: value >> shift))
            }
        }

        mutating func float(_ value: Float) { word(value.bitPattern) }

        mutating func point(_ x: Float, _ y: Float) {
            float(x)
            float(y)
        }

        mutating func rectangle(_ x: Float, _ y: Float, _ width: Float, _ height: Float) {
            point(x, y)
            point(width, height)
        }

        mutating func stroke(color: UInt32, width: Float, highlighter: Bool, points: [(Float, Float)]) {
            data.append(1)
            word(color)
            float(width)
            data.append(highlighter ? 1 : 0)
            word(UInt32(points.count))
            for (x, y) in points { point(x, y) }
        }

        mutating func style(fill: UInt32?, outline: UInt32?, width: Float, fillOpacity: UInt32,
                            outlineOpacity: UInt32, pattern: UInt8? = nil) {
            data.append(fill == nil ? 0 : 1)
            if let fill { word(fill) }
            data.append(outline == nil ? 0 : 1)
            if let outline { word(outline) }
            float(width)
            word(fillOpacity)
            word(outlineOpacity)
            if let pattern { data.append(pattern) }
        }

        mutating func string(_ value: String) {
            word(UInt32(value.utf8.count))
            data.append(contentsOf: value.utf8)
        }
    }
}
