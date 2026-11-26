import Foundation

enum StudioOriginalCapture {
    @discardableResult
    static func save(_ snapshot: PngSnapshot, workingCopy: Bool = false) async throws -> URL {
        try await Task.detached(priority: .utility) {
            let files = FileManager.default
            let pictures = try files.url(for: .picturesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            let originals = pictures.appendingPathComponent("Snip Studio", isDirectory: true)
            let folder = workingCopy ? originals.appendingPathComponent("Working Copies", isDirectory: true) : originals
            try files.createDirectory(
                at: folder,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: NSNumber(value: 0o700)]
            )
            let timestamp = Date().ISO8601Format().replacingOccurrences(of: ":", with: "-")
            let url = folder.appendingPathComponent("Snip \(timestamp) \(UUID().uuidString).png")
            try await snapshot.writePNG(to: url)
            return url
        }.value
    }
}
