import AppKit
import CoreGraphics
import Foundation
import ScreenCaptureKit

enum CaptureMode: Hashable {
    case region
    case window
    case fullScreen
    case allDisplays
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

        switch mode {
        case .region:
            return try await captureWithSystemTool(options: ["-i", "-s"], interactive: true)
        case .window:
            return try await captureWithSystemTool(options: ["-i", "-w", "-o"], interactive: true)
        case .fullScreen:
            return try await captureWithSystemTool(options: ["-m"], interactive: false)
        case .allDisplays:
            return try await captureAllDisplays()
        }
    }

    @MainActor
    private static func captureWithSystemTool(options: [String], interactive: Bool) async throws -> NSImage? {
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
        capture.arguments = ["-x", "-t", "png"] + options + [output.path]
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
            guard interactive, (status == 0 || status == 1), outputErrors.isEmpty else {
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

    @MainActor
    private static func captureAllDisplays() async throws -> NSImage {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let displays = content.displays.filter {
            CGDisplayIsActive($0.displayID) != 0 && CGDisplayMirrorsDisplay($0.displayID) == kCGNullDirectDisplay
        }
        guard !displays.isEmpty else { throw CaptureFailure.noDisplays }

        let appProcessID = ProcessInfo.processInfo.processIdentifier
        let excludedApps = content.applications.filter { $0.processID == appProcessID }
        let sources = displays.map { display in
            (
                id: display.displayID,
                frame: CGDisplayBounds(display.displayID),
                filter: SCContentFilter(display: display, excludingApplications: excludedApps, exceptingWindows: [])
            )
        }
        guard sources.allSatisfy({ !$0.frame.isInfinite && !$0.frame.isEmpty && $0.filter.pointPixelScale > 0 }) else {
            throw CaptureFailure.displayLayoutChanged
        }

        let layout = sources.reduce(CGRect.null) { $0.union($1.frame) }
        let scale = sources.map { CGFloat($0.filter.pointPixelScale) }.max() ?? 1
        let width = (layout.width * scale).rounded()
        let height = (layout.height * scale).rounded()
        guard width >= 1, height >= 1, width * height <= 64_000_000 else {
            throw CaptureFailure.displayCaptureTooLarge
        }
        guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB),
              let bitmap = CGContext(
                  data: nil, width: Int(width), height: Int(height), bitsPerComponent: 8,
                  bytesPerRow: 0, space: colorSpace,
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else { throw CaptureFailure.unreadableImage }

        for source in sources {
            try Task.checkCancellation()
            let configuration = SCStreamConfiguration()
            let nativeScale = CGFloat(source.filter.pointPixelScale)
            configuration.width = max(1, Int((source.frame.width * nativeScale).rounded()))
            configuration.height = max(1, Int((source.frame.height * nativeScale).rounded()))
            configuration.showsCursor = false
            let image = try await SCScreenshotManager.captureImage(contentFilter: source.filter, configuration: configuration)

            // Global display coordinates start at the top. CGContext uses a bottom-left origin.
            let left = ((source.frame.minX - layout.minX) * scale).rounded()
            let right = ((source.frame.maxX - layout.minX) * scale).rounded()
            let bottom = ((layout.maxY - source.frame.maxY) * scale).rounded()
            let top = ((layout.maxY - source.frame.minY) * scale).rounded()
            bitmap.draw(image, in: CGRect(x: left, y: bottom, width: right - left, height: top - bottom))
        }

        try Task.checkCancellation()
        guard sources.allSatisfy({ CGDisplayBounds($0.id) == $0.frame }) else {
            throw CaptureFailure.displayLayoutChanged
        }
        guard let image = bitmap.makeImage() else { throw CaptureFailure.unreadableImage }
        return NSImage(cgImage: image, size: layout.size)
    }
}

private enum CaptureFailure: LocalizedError {
    case permissionRequired
    case timedOut
    case systemFailure(Int32)
    case unreadableImage
    case noDisplays
    case displayLayoutChanged
    case displayCaptureTooLarge

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
        case .noDisplays:
            "macOS did not report an active display to capture."
        case .displayLayoutChanged:
            "The display arrangement changed during capture. Try again when the displays are ready."
        case .displayCaptureTooLarge:
            "The combined capture exceeds 64 million pixels. Capture one display or a region instead."
        }
    }
}
