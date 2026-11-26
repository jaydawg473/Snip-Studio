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
            if words, !line.words.isEmpty {
                return line.words.map { Unit(lineIndex: index, range: $0.utf16Range, bounds: $0.bounds) }
            }
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
}
