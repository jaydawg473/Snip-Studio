import CoreGraphics
import Vision

struct RecognizedLine {
    let text: String
    let bounds: CGRect
}

enum TextRecognition {
    static func recognize(_ image: CGImage) async throws -> String {
        try await Task.detached(priority: .userInitiated) {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = false

            let handler = VNImageRequestHandler(cgImage: image, orientation: .up)
            try handler.perform([request])

            let lines = (request.results ?? []).compactMap { observation -> RecognizedLine? in
                guard let text = observation.topCandidates(1).first?.string else { return nil }
                return RecognizedLine(text: text, bounds: observation.boundingBox)
            }
            return orderRows(lines).map(\.text).joined(separator: "\n")
        }.value
    }

    static func orderRows(_ lines: [RecognizedLine]) -> [RecognizedLine] {
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
        return rows.flatMap { row in row.sorted { $0.bounds.minX < $1.bounds.minX } }
    }

    private static func sharesRow(_ box: CGRect, _ anchor: CGRect) -> Bool {
        // An anchored comparison prevents small overlaps from chaining distinct rows.
        let overlap = min(box.maxY, anchor.maxY) - max(box.minY, anchor.minY)
        let shorterHeight = min(box.height, anchor.height)
        return shorterHeight > 0 && overlap >= shorterHeight / 2
    }
}
