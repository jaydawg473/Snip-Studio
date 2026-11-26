import Foundation

enum StudioOriginalCapture {
    static func save(_ snapshot: PngSnapshot) async throws {
        try await Task.detached(priority: .utility) {
            let files = FileManager.default
            let pictures = try files.url(for: .picturesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            let folder = pictures.appendingPathComponent("Snip Studio", isDirectory: true)
            try files.createDirectory(
                at: folder,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: NSNumber(value: 0o700)]
            )
            let timestamp = Date().ISO8601Format().replacingOccurrences(of: ":", with: "-")
            let url = folder.appendingPathComponent("Snip \(timestamp) \(UUID().uuidString).png")
            try await snapshot.writePNG(to: url)
        }.value
    }
}
