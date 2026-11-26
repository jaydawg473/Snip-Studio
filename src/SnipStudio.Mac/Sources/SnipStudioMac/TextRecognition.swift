import CoreGraphics
import Foundation
import Vision

struct OCRContact: Identifiable, Sendable {
    enum Kind: Hashable, Sendable { case email, phone }

    let id = UUID()
    let kind: Kind
    let value: String
    // Vision reports approximate word bounds in normalized, bottom-left coordinates.
    let bounds: CGRect
}

struct OCRWord: Sendable {
    let text: String
    // Vision reports bounds in normalized, bottom-left coordinates.
    let bounds: CGRect
}

struct RecognizedLine: Sendable {
    let text: String
    let bounds: CGRect
    let contacts: [OCRContact]
    let words: [OCRWord]

    init(text: String, bounds: CGRect, contacts: [OCRContact] = [], words: [OCRWord] = []) {
        self.text = text
        self.bounds = bounds
        self.contacts = contacts
        self.words = words
    }
}

enum TextRecognition {
    static func recognize(_ image: CGImage) async throws -> String {
        let lines = try await recognizeLines(image)
        return lines.map(\.text).joined(separator: "\n")
    }

    static func recognizeLines(_ image: CGImage) async throws -> [RecognizedLine] {
        try await Task.detached(priority: .userInitiated) {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = false

            let handler = VNImageRequestHandler(cgImage: image, orientation: .up)
            try handler.perform([request])

            let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue |
                                                NSTextCheckingResult.CheckingType.phoneNumber.rawValue)
            let lines = (request.results ?? []).compactMap { observation -> RecognizedLine? in
                guard let candidate = observation.topCandidates(1).first else { return nil }
                let text = candidate.string
                return RecognizedLine(text: text, bounds: observation.boundingBox,
                                      contacts: detector.map { contacts(in: text, candidate: candidate, with: $0) } ?? [],
                                      words: words(in: text, candidate: candidate))
            }
            return orderRows(lines)
        }.value
    }

    private static func contacts(in text: String, candidate: VNRecognizedText, with detector: NSDataDetector) -> [OCRContact] {
        return detector.matches(in: text, range: NSRange(location: 0, length: text.utf16.count)).compactMap { match in
            let kind: OCRContact.Kind
            switch match.resultType {
            case .link where match.url?.scheme?.lowercased() == "mailto": kind = .email
            case .phoneNumber where match.phoneNumber != nil: kind = .phone
            default: return nil
            }
            guard let range = Range(match.range, in: text), let bounds = nativeBounds(for: range, in: candidate) else {
                return nil
            }
            return OCRContact(kind: kind, value: String(text[range]), bounds: bounds)
        }
    }

    private static func words(in text: String, candidate: VNRecognizedText) -> [OCRWord] {
        let words = text.split(whereSeparator: \.isWhitespace).compactMap { token -> OCRWord? in
            guard let bounds = nativeBounds(for: token.startIndex..<token.endIndex, in: candidate) else { return nil }
            return OCRWord(text: String(token), bounds: bounds)
        }
        // Vision may return the same span for several requests. A single line's words need distinct horizontal spans.
        let byLeftEdge = words.indices.sorted { words[$0].bounds.minX < words[$1].bounds.minX }
        var rightmost: Int?
        var ambiguous = Set<Int>()
        for index in byLeftEdge {
            let bounds = words[index].bounds
            if let previous = rightmost {
                let rightEdge = words[previous].bounds.maxX
                if bounds.minX < rightEdge {
                    ambiguous.insert(previous)
                    ambiguous.insert(index)
                }
                if bounds.maxX > rightEdge { rightmost = index }
            } else {
                rightmost = index
            }
        }
        return words.indices.compactMap { ambiguous.contains($0) ? nil : words[$0] }
    }

    private static func nativeBounds(for range: Range<String.Index>, in candidate: VNRecognizedText) -> CGRect? {
        guard !range.isEmpty, let observation = try? candidate.boundingBox(for: range) else { return nil }
        let bounds = observation.boundingBox
        guard bounds.minX.isFinite, bounds.minY.isFinite, bounds.maxX.isFinite, bounds.maxY.isFinite else { return nil }
        let clipped = bounds.intersection(CGRect(x: 0, y: 0, width: 1, height: 1))
        return clipped.width > 0 && clipped.height > 0 ? clipped : nil
    }

    static func orderRows(_ lines: [RecognizedLine]) -> [RecognizedLine] {
        groupRows(lines).flatMap { $0 }
    }

    static func groupRows(_ lines: [RecognizedLine]) -> [[RecognizedLine]] {
        // Vision coordinates begin at the bottom left. Sort bands from top to bottom,
        // then read across each band even when the boxes have different heights.
        let verticalOrder = lines.sorted { $0.bounds.midY > $1.bounds.midY }
        var rows: [[RecognizedLine]] = []
        for line in verticalOrder {
            if let anchor = rows.last?.first, sharesRow(line.bounds, anchor.bounds) {
                rows[rows.count - 1].append(line)
            } else {
                rows.append([line])
            }
        }
        return rows.map { row in row.sorted { $0.bounds.minX < $1.bounds.minX } }
    }

    private static func sharesRow(_ box: CGRect, _ anchor: CGRect) -> Bool {
        // An anchored comparison prevents small overlaps from chaining distinct rows.
        let overlap = min(box.maxY, anchor.maxY) - max(box.minY, anchor.minY)
        let shorterHeight = min(box.height, anchor.height)
        return shorterHeight > 0 && overlap >= shorterHeight / 2
    }
}
