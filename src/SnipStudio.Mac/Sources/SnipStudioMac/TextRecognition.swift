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

struct RecognizedLine: Sendable {
    let text: String
    let bounds: CGRect
    let contacts: [OCRContact]

    init(text: String, bounds: CGRect, contacts: [OCRContact] = []) {
        self.text = text
        self.bounds = bounds
        self.contacts = contacts
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
                return RecognizedLine(text: candidate.string, bounds: observation.boundingBox,
                                      contacts: detector.map { contacts(in: candidate, with: $0) } ?? [])
            }
            return orderRows(lines)
        }.value
    }

    private static func contacts(in candidate: VNRecognizedText, with detector: NSDataDetector) -> [OCRContact] {
        let text = candidate.string
        let imageBounds = CGRect(x: 0, y: 0, width: 1, height: 1)
        return detector.matches(in: text, range: NSRange(location: 0, length: text.utf16.count)).compactMap { match in
            let kind: OCRContact.Kind
            switch match.resultType {
            case .link where match.url?.scheme?.lowercased() == "mailto": kind = .email
            case .phoneNumber where match.phoneNumber != nil: kind = .phone
            default: return nil
            }
            guard let range = Range(match.range, in: text), !range.isEmpty,
                  let observation = try? candidate.boundingBox(for: range) else { return nil }
            let bounds = observation.boundingBox
            guard bounds.minX.isFinite, bounds.minY.isFinite, bounds.maxX.isFinite, bounds.maxY.isFinite else {
                return nil
            }
            let clipped = bounds.intersection(imageBounds)
            guard !clipped.isNull, !clipped.isEmpty else { return nil }
            return OCRContact(kind: kind, value: String(text[range]), bounds: clipped)
        }
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
