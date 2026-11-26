import AppKit
import CoreGraphics
import Foundation

enum CaptureMode: Hashable {
    case region
    case window
}

struct CaptureService {
    @MainActor
    static func capture(_ mode: CaptureMode) async throws -> NSImage? {
        try Task.checkCancellation()
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else {
            throw CaptureFailure.permissionRequired
        }

        let application = NSApplication.shared
        let wasVisible = !application.isHidden
        if wasVisible { application.hide(nil) }
        defer {
            if wasVisible {
                application.unhide(nil)
                application.activate(ignoringOtherApps: true)
            }
        }
        try await Task.sleep(for: .milliseconds(150))

        let files = FileManager.default
        let workspace = files.temporaryDirectory.appendingPathComponent(
            "SnipStudioCapture-\(UUID().uuidString)",
            isDirectory: true
        )
        try files.createDirectory(
            at: workspace,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: NSNumber(value: 0o700)]
        )
        let capture = Process()
        var launched = false
        defer {
            if launched && capture.isRunning {
                // Termination is asynchronous. Let the selector finish with its files first.
                capture.terminationHandler = { _ in
                    try? FileManager.default.removeItem(at: workspace)
                }
                if capture.isRunning {
                    capture.terminate()
                } else {
                    // The process may have exited before the handler was registered.
                    try? files.removeItem(at: workspace)
                }
            } else {
                try? files.removeItem(at: workspace)
            }
        }

        let output = workspace.appendingPathComponent("capture.png")
        let diagnostic = workspace.appendingPathComponent("capture.log")
        try Data().write(to: diagnostic, options: .withoutOverwriting)
        let diagnosticWriter = try FileHandle(forWritingTo: diagnostic)
        defer { try? diagnosticWriter.close() }

        capture.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        let selectionFlags = mode == .region ? ["-s"] : ["-w", "-o"]
        capture.arguments = ["-i", "-x", "-t", "png"] + selectionFlags + [output.path]
        capture.standardInput = FileHandle.nullDevice
        capture.standardOutput = FileHandle.nullDevice
        capture.standardError = diagnosticWriter
        try capture.run()
        launched = true

        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(120))
        while capture.isRunning {
            guard clock.now < deadline else { throw CaptureFailure.timedOut }
            try await Task.sleep(for: .milliseconds(100))
        }
        try Task.checkCancellation()

        let status = capture.terminationStatus
        guard capture.terminationReason == .exit else { throw CaptureFailure.systemFailure(status) }
        if !files.fileExists(atPath: output.path) {
            // An escaped selection ordinarily leaves no image and no error message.
            let reader = try FileHandle(forReadingFrom: diagnostic)
            defer { try? reader.close() }
            let outputErrors = try reader.read(upToCount: 4096) ?? Data()
            guard (status == 0 || status == 1) && outputErrors.isEmpty else {
                throw CaptureFailure.systemFailure(status)
            }
            return nil
        }

        guard status == 0 else { throw CaptureFailure.systemFailure(status) }
        let imageData = try Data(contentsOf: output)
        guard let image = NSImage(data: imageData), image.isValid else {
            throw CaptureFailure.unreadableImage
        }
        return image
    }
}

private enum CaptureFailure: LocalizedError {
    case permissionRequired
    case timedOut
    case systemFailure(Int32)
    case unreadableImage

    var errorDescription: String? {
        switch self {
        case .permissionRequired:
            "Allow Snip Studio in System Settings under Privacy & Security → Screen & System Audio Recording. Then reopen the app and try again."
        case .timedOut:
            "The capture selection timed out. Start a new capture to try again."
        case .systemFailure(let status):
            "macOS could not complete the capture (status \(status))."
        case .unreadableImage:
            "The captured image could not be opened."
        }
    }
}
