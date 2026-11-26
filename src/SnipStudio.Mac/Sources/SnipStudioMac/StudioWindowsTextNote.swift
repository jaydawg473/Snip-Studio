import CoreGraphics
import CoreText
import Foundation

// A text-only subset of the format emitted by WinForms RichEdit. Decode locally
// so links, objects and unrecognized formatting cannot reach an external parser.
enum StudioWindowsTextNote {
    struct Decoded {
        let note: StudioTextAnnotation
        let red: CGFloat
        let green: CGFloat
        let blue: CGFloat
        let alpha: CGFloat = 1
    }

    enum Failure: LocalizedError {
        case unsupportedRTF, differentText, missingFont

        var errorDescription: String? {
            switch self {
            case .unsupportedRTF: "The Windows note uses formatting this Mac editor cannot preserve."
            case .differentText: "The Windows note's text does not match its formatting record."
            case .missingFont: "The Windows note uses a font or face that is unavailable on this Mac."
            }
        }
    }

    static func decode(plain: String, rtf: String) throws -> Decoded {
        let original = plain.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        guard (1...StudioTextAnnotation.maximumUTF16Length).contains(plain.utf16.count),
              rtf.utf8.count <= 1_048_576 else { throw Failure.unsupportedRTF }
        var input = Reader(bytes: Array(rtf.utf8))
        let header = try input.readHeader()
        let style = try input.readBody(header: header, matching: Array(original.utf16))
        let size = Double(style.halfPoints) * 2 / 3
        var requested: CTFontSymbolicTraits = []
        if style.bold { requested.insert(.traitBold) }
        if style.italic { requested.insert(.traitItalic) }
        let font = CTFontCreateWithName(style.family as CFString, CGFloat(size), nil)
        guard (CTFontCopyFamilyName(font) as String).caseInsensitiveCompare(style.family) == .orderedSame else {
            throw Failure.missingFont
        }
        var face = font
        if !requested.isEmpty {
            guard let styled = CTFontCreateCopyWithSymbolicTraits(font, 0, nil, requested, requested),
                  (CTFontCopyFamilyName(styled) as String).caseInsensitiveCompare(style.family) == .orderedSame,
                  CTFontGetSymbolicTraits(styled).isSuperset(of: requested) else { throw Failure.missingFont }
            face = styled
        }
        let coverage = CTFontCopyCharacterSet(face)
        guard !CTFontGetSymbolicTraits(face).contains(.traitColorGlyphs), original.unicodeScalars.allSatisfy({
            $0.value == 10 || CFCharacterSetIsLongCharacterMember(coverage, $0.value)
        }) else { throw Failure.missingFont }
        let note = StudioTextAnnotation(text: plain, fontName: style.family, pixelSize: size,
                                        bold: style.bold, italic: style.italic, underline: style.underline)
        return Decoded(note: note, red: CGFloat(style.color.red) / 255, green: CGFloat(style.color.green) / 255,
                       blue: CGFloat(style.color.blue) / 255)
    }

    private struct RGB: Equatable {
        let red: UInt8
        let green: UInt8
        let blue: UInt8
    }

    private struct Appearance: Equatable {
        let family: String
        let halfPoints: Int
        let color: RGB
        let bold: Bool
        let italic: Bool
        let underline: Bool
    }

    private struct Header {
        let defaultFont: Int
        let fonts: [Int: String]
        let colors: [RGB]
        let fallbackLength: Int
    }

    private enum Token: Equatable {
        case open, close, star, character(UInt16), word(String, Int?)
    }

    private struct Reader {
        let bytes: [UInt8]
        private var offset = 0

        init(bytes: [UInt8]) { self.bytes = bytes }

        mutating func readHeader() throws -> Header {
            try require(.open)
            try require(.word("rtf", 1))
            try require(.word("ansi", nil))
            try require(.word("ansicpg", 1252))
            var defaultFont: Int?
            var fonts: [Int: String] = [:]
            var colors: [RGB] = []
            var fallbackLength = 1
            while let token = try next() {
                switch token {
                case .word("pard", nil):
                    guard let defaultFont, fonts[defaultFont] != nil else { throw Failure.unsupportedRTF }
                    return Header(defaultFont: defaultFont, fonts: fonts, colors: colors, fallbackLength: fallbackLength)
                case .word("deff", let value?):
                    guard defaultFont == nil, (0...1023).contains(value) else { throw Failure.unsupportedRTF }
                    defaultFont = value
                case .word("nouicompat", nil), .word("viewkind", 4): break
                case .word("deflang", let value?) where (0...65_535).contains(value): break
                case .word("uc", let value?) where (0...1).contains(value): fallbackLength = value
                case .open:
                    switch try next() {
                    case .word("fonttbl", nil) where fonts.isEmpty: fonts = try readFonts()
                    case .word("colortbl", nil) where colors.isEmpty: colors = try readColors()
                    case .star: try readGenerator()
                    default: throw Failure.unsupportedRTF
                    }
                default: throw Failure.unsupportedRTF
                }
            }
            throw Failure.unsupportedRTF
        }

        mutating func readBody(header: Header, matching expected: [UInt16]) throws -> Appearance {
            let black = RGB(red: 0, green: 0, blue: 0)
            var font = header.defaultFont
            var halfPoints: Int?
            var color = black
            var bold = false
            var italic = false
            var underline = false
            var fallbackLength = header.fallbackLength
            var text: [UInt16] = []
            var appearance: Appearance?
            var paragraphs: [(index: Int, style: Appearance?)] = []
            while let token = try next() {
                try Task.checkCancellation()
                let character: UInt16
                let isParagraph: Bool
                switch token {
                case .close:
                    // RichEdit writes one final paragraph marker beyond the user's text.
                    guard try next() == nil, let appearance, text.last == 10,
                          paragraphs.last?.index == expected.count, text.dropLast().elementsEqual(expected) else {
                        throw Failure.differentText
                    }
                    guard paragraphs.dropLast().allSatisfy({ $0.style == appearance }) else {
                        throw Failure.unsupportedRTF
                    }
                    return appearance
                case .character(let value): character = value; isParagraph = false
                case .word("par", nil): character = 10; isParagraph = true
                case .word("u", let value?) where (-32_768...32_767).contains(value):
                    character = UInt16(bitPattern: Int16(value))
                    isParagraph = false
                    guard character >= 32, !(127...159).contains(character),
                          character != 0x2028, character != 0x2029 else { throw Failure.unsupportedRTF }
                    if fallbackLength == 1 {
                        guard case .character = try next() else { throw Failure.unsupportedRTF }
                    }
                case .word("pard", nil), .word("ql", nil), .word("ltrpar", nil), .word("ltrch", nil): continue
                case .word("lang", let value?) where (0...65_535).contains(value): continue
                case .word("uc", let value?) where (0...1).contains(value): fallbackLength = value; continue
                case .word("f", let value?) where header.fonts[value] != nil: font = value; continue
                case .word("fs", let value?) where (2...768).contains(value): halfPoints = value; continue
                case .word("cf", 0): color = black; continue
                case .word("cf", let value?) where value > 0 && header.colors.indices.contains(value):
                    color = header.colors[value]; continue
                case .word("b", let value) where value == nil || value == 0 || value == 1: bold = value != 0; continue
                case .word("i", let value) where value == nil || value == 0 || value == 1: italic = value != 0; continue
                case .word("ul", let value) where value == nil || value == 0 || value == 1: underline = value != 0; continue
                case .word("ulnone", nil): underline = false; continue
                default: throw Failure.unsupportedRTF
                }
                guard text.count <= expected.count else { throw Failure.differentText }
                let style = header.fonts[font].flatMap { family in
                    halfPoints.map { Appearance(family: family, halfPoints: $0, color: color,
                                                bold: bold, italic: italic, underline: underline) }
                }
                if isParagraph {
                    paragraphs.append((text.count, style))
                } else {
                    guard let style, appearance == nil || appearance == style else { throw Failure.unsupportedRTF }
                    appearance = style
                }
                text.append(character)
            }
            throw Failure.unsupportedRTF
        }

        private mutating func readFonts() throws -> [Int: String] {
            var fonts: [Int: String] = [:]
            while let token = try next() {
                if token == .close, !fonts.isEmpty { return fonts }
                guard token == .open, case .word("f", let index?) = try next(), (0...1023).contains(index),
                      fonts[index] == nil else { throw Failure.unsupportedRTF }
                var units: [UInt16] = []
                while let field = try next(), field != .character(59) {
                    switch field {
                    case .word(let family, nil) where units.isEmpty && Self.fontFamilies.contains(family): break
                    case .word("fcharset", let value?) where units.isEmpty && (value == 0 || value == 1): break
                    case .word("fprq", let value?) where units.isEmpty && (0...2).contains(value): break
                    case .character(let value) where units.count < 256: units.append(value)
                    default: throw Failure.unsupportedRTF
                    }
                }
                try require(.close)
                let family = String(decoding: units, as: UTF16.self)
                guard !family.isEmpty, family == family.trimmingCharacters(in: .whitespaces) else {
                    throw Failure.unsupportedRTF
                }
                fonts[index] = family
            }
            throw Failure.unsupportedRTF
        }

        private static let fontFamilies: Set<String> = ["fnil", "froman", "fswiss", "fmodern", "fscript", "fdecor", "ftech", "fbidi"]

        private mutating func readColors() throws -> [RGB] {
            try require(.character(59)) // index zero is RichEdit's automatic black foreground
            var colors = [RGB(red: 0, green: 0, blue: 0)]
            while let token = try next() {
                if token == .close { return colors }
                guard colors.count < 1024,
                      case .word("red", let red?) = token, let r = UInt8(exactly: red),
                      case .word("green", let green?) = try next(), let g = UInt8(exactly: green),
                      case .word("blue", let blue?) = try next(), let b = UInt8(exactly: blue) else {
                    throw Failure.unsupportedRTF
                }
                try require(.character(59))
                colors.append(RGB(red: r, green: g, blue: b))
            }
            throw Failure.unsupportedRTF
        }

        private mutating func readGenerator() throws {
            try require(.word("generator", nil))
            for _ in 0..<128 {
                switch try next() {
                case .close: return
                case .character(let value) where (32...126).contains(value): continue
                default: throw Failure.unsupportedRTF
                }
            }
            throw Failure.unsupportedRTF
        }

        private mutating func require(_ token: Token) throws {
            guard try next() == token else { throw Failure.unsupportedRTF }
        }

        private mutating func next() throws -> Token? {
            while let first = take() {
                switch first {
                case 10, 13: continue // line breaks in RTF source are formatting whitespace
                case 123: return .open
                case 125: return .close
                case 92: return try escape()
                case 32...126: return .character(UInt16(first))
                default: throw Failure.unsupportedRTF
                }
            }
            return nil
        }

        private mutating func escape() throws -> Token {
            guard let start = take() else { throw Failure.unsupportedRTF }
            switch start {
            case 42: return .star
            case 92, 123, 125: return .character(UInt16(start))
            case 39:
                guard let high = take(), let low = take(), let a = Self.hex(high), let b = Self.hex(low),
                      let decoded = Self.windowsCharacter(a * 16 + b) else { throw Failure.unsupportedRTF }
                return .character(decoded)
            case 97...122: break
            default: throw Failure.unsupportedRTF
            }
            let begin = offset - 1
            while let byte = peek, (97...122).contains(byte), offset - begin < 32 { offset += 1 }
            let name = String(decoding: bytes[begin..<offset], as: UTF8.self)
            let numberStart = offset
            if peek == 45 { offset += 1 }
            while let byte = peek, (48...57).contains(byte), offset - numberStart < 11 { offset += 1 }
            let value = numberStart == offset ? nil : Int(String(decoding: bytes[numberStart..<offset], as: UTF8.self))
            guard (numberStart == offset || value != nil),
                  peek.map({ !(65...90).contains($0) && !(97...122).contains($0) && !(48...57).contains($0) }) ?? true else {
                throw Failure.unsupportedRTF
            }
            if peek == 32 { offset += 1 }
            return .word(name, value)
        }

        private var peek: UInt8? { offset < bytes.count ? bytes[offset] : nil }

        private mutating func take() -> UInt8? {
            guard let byte = peek else { return nil }
            offset += 1
            return byte
        }

        private static func hex(_ byte: UInt8) -> UInt8? {
            switch byte {
            case 48...57: byte - 48
            case 65...70: byte - 55
            case 97...102: byte - 87
            default: nil
            }
        }

        private static func windowsCharacter(_ byte: UInt8) -> UInt16? {
            switch byte {
            case 32...126, 160...255: UInt16(byte)
            case 128...159: windows1252[Int(byte) - 128]
            default: nil
            }
        }

        private static let windows1252: [UInt16?] = [
            0x20ac, nil, 0x201a, 0x0192, 0x201e, 0x2026, 0x2020, 0x2021,
            0x02c6, 0x2030, 0x0160, 0x2039, 0x0152, nil, 0x017d, nil,
            nil, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2013, 0x2014,
            0x02dc, 0x2122, 0x0161, 0x203a, 0x0153, nil, 0x017e, 0x0178
        ]
    }

    static func selfCheck() throws {
        let nativeRTF = #"""
        {\rtf1\ansi\ansicpg1252\deff0\nouicompat\deflang1033{\fonttbl{\f0\fnil\fcharset0 Arial;}{\f1\fnil Segoe UI;}}
        {\colortbl ;\red10\green65\blue123;}
        {\*\generator Riched20 10.0.26100}\viewkind4\uc1
        \pard\cf1\ul\b\i\fs24   FILE 0027\par
        \par
        C:\\work\\r\'e9sum\'e9.exe \cf0\ulnone\b0\i0\f1\fs18\par
        }
        """#
        let plain = "  FILE 0027\r\n\r\nC:\\work\\résumé.exe "
        let imported = try decode(plain: plain, rtf: nativeRTF)
        guard imported.note.text.utf16.elementsEqual(plain.utf16), imported.note.fontName == "Arial",
              imported.note.pixelSize == 16, imported.note.bold, imported.note.italic, imported.note.underline,
              imported.red == CGFloat(10) / 255, imported.green == CGFloat(65) / 255, imported.blue == CGFloat(123) / 255,
              imported.alpha == 1 else { throw CheckFailure() }
        let unicode = nativeRTF.replacingOccurrences(of: #"\'e9"#, with: #"\u233?"#)
        guard try decode(plain: plain, rtf: unicode).note == imported.note else { throw CheckFailure() }

        let refusals = [
            (plain + "0", nativeRTF),
            (plain, nativeRTF.replacingOccurrences(of: "FILE 0027", with: #"FILE \b0 0027"#)),
            (plain, nativeRTF.replacingOccurrences(of: #"\pard"#, with: #"\pard\qc"#)),
            (plain, nativeRTF.replacingOccurrences(of: #"\pard"#, with: #"\pard\strike"#)),
            (plain, nativeRTF.replacingOccurrences(of: #"{\*\generator"#, with: #"{\*\object"#)),
            (plain, nativeRTF.replacingOccurrences(of: "Arial", with: "Snip Studio Unavailable Test Font")),
            (plain + "\t", nativeRTF.replacingOccurrences(of: #"\cf0\ulnone"#, with: #"\tab\cf0\ulnone"#)),
            (plain + "😀", nativeRTF.replacingOccurrences(of: #"\cf0\ulnone"#, with: #"\u-10179?\u-8704?\cf0\ulnone"#)),
            (plain, String(nativeRTF.dropLast())),
            (plain, nativeRTF + "data")
        ]
        for (plain, rtf) in refusals {
            do {
                _ = try decode(plain: plain, rtf: rtf)
                throw CheckFailure()
            } catch is Failure { continue }
        }
        print("PASS: Windows text notes preserve identifiers, line breaks, font, style and color, and reject unsupported RTF")
    }

    private struct CheckFailure: Error {}
}
