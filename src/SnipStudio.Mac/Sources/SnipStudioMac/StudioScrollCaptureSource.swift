import AppKit
import CoreGraphics
import Foundation
import ScreenCaptureKit

struct StudioScrollDisplay: Identifiable, Equatable, Sendable {
    let id: CGDirectDisplayID
    let title: String
    let size: CGSize
    fileprivate let pixelScale: CGFloat
    fileprivate let rotation: Double
}

@MainActor
enum StudioScrollCaptureSource {
    static func displays() async throws -> [StudioScrollDisplay] {
        try checkPermission()
        let content = try await shareableContent()
        let main = CGMainDisplayID()
        let screens = activeDisplays(in: content).sorted { lhs, rhs in
            if lhs.displayID == main { return rhs.displayID != main }
            if rhs.displayID == main { return false }
            return lhs.displayID < rhs.displayID
        }
        let descriptions = screens.enumerated().compactMap { index, display -> StudioScrollDisplay? in
            let filter = makeFilter(display: display, content: content)
            let size = filter.contentRect.size
            let scale = CGFloat(filter.pointPixelScale)
            let rotation = CGDisplayRotation(display.displayID)
            guard hasArea(size), scale.isFinite, scale > 0, rotation.isFinite else { return nil }
            let number = index + 1
            let name = NSScreen.screens.first {
                ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == display.displayID
            }?.localizedName
            let title = display.displayID == main ? "Main Display" : name.map { "\($0) (\(number))" } ?? "Display \(number)"
            return StudioScrollDisplay(id: display.displayID, title: title, size: size, pixelScale: scale, rotation: rotation)
        }
        guard !descriptions.isEmpty else { throw Failure.noDisplays }
        return descriptions
    }

    static func snapshot(display: StudioScrollDisplay, region: CGRect? = nil, preview: Bool = false) async throws -> CGImage {
        try checkPermission()
        let content = try await shareableContent()
        guard let screen = activeDisplays(in: content).first(where: { $0.displayID == display.id }) else {
            throw Failure.displayChanged
        }
        let filter = makeFilter(display: screen, content: content)
        let size = filter.contentRect.size
        let scale = CGFloat(filter.pointPixelScale)
        let originalBounds = CGDisplayBounds(display.id)
        let originalMode = CGDisplayCopyDisplayMode(display.id)?.ioDisplayModeID
        guard size == display.size, scale == display.pixelScale, CGDisplayRotation(display.id) == display.rotation else {
            throw Failure.displayChanged
        }
        let bounds = CGRect(origin: .zero, size: size)
        let area = region ?? bounds
        guard area.origin.x.isFinite, area.origin.y.isFinite, hasArea(area.size), bounds.contains(area) else {
            throw Failure.invalidArea
        }

        // Align both edges to the display's backing pixels before specifying the screen-point source rectangle.
        let left = (area.minX * scale).rounded(.down)
        let top = (area.minY * scale).rounded(.down)
        let right = min((bounds.maxX * scale).rounded(), (area.maxX * scale).rounded(.up))
        let bottom = min((bounds.maxY * scale).rounded(), (area.maxY * scale).rounded(.up))
        let nativeWidth = right - left
        let nativeHeight = bottom - top
        let previewScale = preview ? min(1, 1_600 / max(nativeWidth, nativeHeight)) : 1
        let width = max(1, (nativeWidth * previewScale).rounded())
        let height = max(1, (nativeHeight * previewScale).rounded())
        guard width.isFinite, height.isFinite, width * height <= 32_000_000 else { throw Failure.areaTooLarge }

        let config = SCStreamConfiguration()
        config.width = Int(width)
        config.height = Int(height)
        config.showsCursor = false
        if region != nil {
            config.sourceRect = CGRect(x: left / scale, y: top / scale, width: nativeWidth / scale, height: nativeHeight / scale)
        }
        let frame: CGImage = try await request(onMissingResult: .noFrame) { completion in
            SCScreenshotManager.captureImage(contentFilter: filter, configuration: config, completionHandler: completion)
        }
        guard CGDisplayIsActive(display.id) != 0, CGDisplayBounds(display.id) == originalBounds,
              CGDisplayRotation(display.id) == display.rotation,
              CGDisplayCopyDisplayMode(display.id)?.ioDisplayModeID == originalMode else { throw Failure.displayChanged }
        guard frame.width == config.width, frame.height == config.height else { throw Failure.unexpectedFrameSize }
        return frame
    }

    private static func checkPermission() throws {
        try Task.checkCancellation()
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else { throw Failure.permissionRequired }
    }

    private static func activeDisplays(in content: SCShareableContent) -> [SCDisplay] {
        content.displays.filter {
            CGDisplayIsActive($0.displayID) != 0 && CGDisplayMirrorsDisplay($0.displayID) == kCGNullDirectDisplay
        }
    }

    private static func makeFilter(display: SCDisplay, content: SCShareableContent) -> SCContentFilter {
        let pid = ProcessInfo.processInfo.processIdentifier
        let ownApps = content.applications.filter { $0.processID == pid }
        if ownApps.isEmpty {
            let ownWindows = content.windows.filter { $0.owningApplication?.processID == pid }
            return SCContentFilter(display: display, excludingWindows: ownWindows)
        }
        return SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])
    }

    private static func hasArea(_ size: CGSize) -> Bool {
        size.width.isFinite && size.height.isFinite && size.width >= 1 && size.height >= 1
    }

    private static func shareableContent() async throws -> SCShareableContent {
        try await request(onMissingResult: .noDisplays) { completion in
            SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true, completionHandler: completion)
        }
    }

    // ScreenCaptureKit doesn't expose cancellation of a single-image request. Bound the caller's wait
    // and ignore any late native response after timeout or cancellation.
    private static func request<Value>(
        onMissingResult: Failure,
        _ begin: (@escaping @Sendable (Value?, Error?) -> Void) -> Void
    ) async throws -> Value {
        let pending = Pending<Value>()
        let watchdog = Task { @MainActor [weak pending] in
            do {
                try await Task.sleep(for: .seconds(12))
                pending?.finish(.failure(Failure.timedOut))
            } catch { return }
        }
        defer {
            watchdog.cancel()
            withExtendedLifetime(pending) {}
        }
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            let value = try await withCheckedThrowingContinuation { continuation in
                pending.continuation = continuation
                begin { [weak pending] value, error in
                    Task { @MainActor [weak pending] in
                        if let error { pending?.finish(.failure(error)) }
                        else if let value { pending?.finish(.success(value)) }
                        else { pending?.finish(.failure(onMissingResult)) }
                    }
                }
            }
            try Task.checkCancellation()
            return value
        } onCancel: {
            Task { @MainActor [weak pending] in pending?.finish(.failure(CancellationError())) }
        }
    }

    @MainActor
    private final class Pending<Value> {
        var continuation: CheckedContinuation<Value, Error>?

        func finish(_ result: Result<Value, Error>) {
            let waiting = continuation
            continuation = nil
            waiting?.resume(with: result)
        }
    }

    private enum Failure: LocalizedError {
        case permissionRequired, noDisplays, displayChanged, invalidArea, areaTooLarge, noFrame, unexpectedFrameSize, timedOut

        var errorDescription: String? {
            switch self {
            case .permissionRequired: "Allow Snip Studio under System Settings → Privacy & Security → Screen & System Audio Recording. Reopen the app if macOS asks."
            case .noDisplays: "No active displays are available for scrolling capture."
            case .displayChanged: "The display changed. Select its scrolling area again."
            case .invalidArea: "Select a scrolling area inside the display."
            case .areaTooLarge: "Choose a scrolling area smaller than 32 million pixels."
            case .noFrame: "macOS did not return a screenshot. Try capturing the view again."
            case .unexpectedFrameSize: "The screenshot dimensions changed. Select the scrolling area again."
            case .timedOut: "macOS did not finish capturing the display. Try again."
            }
        }
    }
}
