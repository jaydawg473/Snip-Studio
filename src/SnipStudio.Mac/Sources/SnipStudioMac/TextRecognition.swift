import CoreGraphics
import Vision

enum TextRecognition {
    static func recognize(_ image: CGImage) async throws -> String {
        try await Task.detached(priority: .userInitiated) {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = false

            let handler = VNImageRequestHandler(cgImage: image, orientation: .up)
            try handler.perform([request])

            let lines = (request.results ?? []).sorted { first, second in
                if first.boundingBox.midY == second.boundingBox.midY {
                    return first.boundingBox.minX < second.boundingBox.minX
                }
                return first.boundingBox.midY > second.boundingBox.midY
            }
            return lines.compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
        }.value
    }
}
