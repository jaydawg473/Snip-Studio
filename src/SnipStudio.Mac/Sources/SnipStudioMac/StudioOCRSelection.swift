import Foundation
import CoreGraphics

enum StudioOCRSelection {
    struct Unit {
        let lineIndex: Int
        let range: NSRange?
        let bounds: CGRect
    }

    static func units(for lines: [RecognizedLine], words: Bool) -> [Unit] {
        lines.enumerated().flatMap { index, line in
            if words, line.supportsWordSelection {
                return line.words.map { Unit(lineIndex: index, range: $0.utf16Range, bounds: $0.bounds) }
            }
            // A whole-line selection keeps every token available when Vision localized only part of the line.
            return [Unit(lineIndex: index, range: nil, bounds: line.bounds)]
        }
    }

    static func selected(_ indices: Set<Int>, from units: [Unit], lines: [RecognizedLine]) -> [RecognizedLine] {
        let selected = indices.sorted().filter(units.indices.contains).map { units[$0] }
        let groups = Dictionary(grouping: selected, by: \.lineIndex)
        return groups.keys.sorted().flatMap { index -> [RecognizedLine] in
            guard lines.indices.contains(index), let marks = groups[index] else { return [] }
            let line = lines[index]
            if marks.contains(where: { $0.range == nil }) { return [line] }
            var portions: [(range: NSRange, bounds: CGRect)] = []
            for mark in marks {
                guard let range = mark.range else { continue }
                if let prior = portions.last {
                    let end = NSMaxRange(prior.range)
                    let gap = NSRange(location: end, length: max(0, range.location - end))
                    if end <= range.location, let between = Range(gap, in: line.text),
                       line.text[between].allSatisfy(\.isWhitespace) {
                        let combined = NSUnionRange(prior.range, range)
                        portions[portions.count - 1] = (combined, prior.bounds.union(mark.bounds))
                        continue
                    }
                }
                portions.append((range, mark.bounds))
            }
            return portions.compactMap { portion in
                guard let range = Range(portion.range, in: line.text) else { return nil }
                return RecognizedLine(text: String(line.text[range]), bounds: portion.bounds)
            }
        }
    }

    static func selfCheck() throws {
        let path = "C:\\Temp\\A0.txt"
        let text = "📁  0007   \(path)"
        let bounds = CGRect(x: 0.1, y: 0.2, width: 0.8, height: 0.1)
        let folder = OCRWord(text: "📁", utf16Range: NSRange(location: 0, length: 2),
                             bounds: CGRect(x: 0.1, y: 0.2, width: 0.1, height: 0.1))
        let quantity = OCRWord(text: "0007", utf16Range: NSRange(location: 4, length: 4),
                               bounds: CGRect(x: 0.3, y: 0.2, width: 0.1, height: 0.1))
        let filename = OCRWord(text: path, utf16Range: NSRange(location: 11, length: 14),
                               bounds: CGRect(x: 0.6, y: 0.2, width: 0.3, height: 0.1))

        let partial = RecognizedLine(text: text, bounds: bounds, words: [folder, filename])
        let partialUnits = units(for: [partial], words: true)
        let partialSelection = selected([0], from: partialUnits, lines: [partial])
        guard partialUnits.count == 1, partialUnits[0].range == nil, partialUnits[0].bounds == bounds,
              partialSelection.map(\.text) == [text], partialSelection.map(\.bounds) == [bounds] else {
            throw CheckFailure.partialWordGeometry
        }

        let complete = RecognizedLine(text: text, bounds: bounds, words: [folder, quantity, filename])
        let completeUnits = units(for: [complete], words: true)
        let filenameSelection = selected([2], from: completeUnits, lines: [complete])
        let fullSelection = selected([0, 1, 2], from: completeUnits, lines: [complete])
        guard completeUnits.count == 3, filenameSelection.map(\.text) == [path],
              filenameSelection.map(\.bounds) == [filename.bounds], fullSelection.map(\.text) == [text] else {
            throw CheckFailure.completeWordGeometry
        }
        print("PASS: OCR word selection retains complete text when some Vision word bounds are missing")
    }

    private enum CheckFailure: Error {
        case partialWordGeometry, completeWordGeometry
    }
}
