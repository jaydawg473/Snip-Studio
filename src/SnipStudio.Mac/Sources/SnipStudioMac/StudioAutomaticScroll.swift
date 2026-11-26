import AppKit
import CoreGraphics
import Foundation

@MainActor
enum StudioAutomaticScroll {
    struct Target {
        let title: String
        fileprivate let displayID: CGDirectDisplayID
        fileprivate let displayBounds: CGRect
        fileprivate let displayModeID: Int32?
        fileprivate let displayRotation: Double
        fileprivate let window: Window
        fileprivate let launchedAt: Date?
        fileprivate let region: CGRect
    }

    // Only call this from the explicit Start Automatic Capture action.
    static func target(display: StudioScrollDisplay, region: CGRect) throws -> Target {
        let displayBounds = CGDisplayBounds(display.id)
        guard CGDisplayIsActive(display.id) != 0, displayBounds.size == display.size,
              let desktopRegion = globalRegion(region, within: displayBounds) else {
            throw Failure.displayChanged
        }
        guard let window = topWindow(over: desktopRegion), window.bounds.contains(desktopRegion),
              let application = NSRunningApplication(processIdentifier: window.pid),
              application.activationPolicy == .regular, !application.isTerminated else {
            throw Failure.noWindow
        }
        guard CGPreflightPostEventAccess() || CGRequestPostEventAccess() else {
            throw Failure.eventPermission
        }
        return Target(title: application.localizedName ?? "the selected app", displayID: display.id,
                      displayBounds: displayBounds, displayModeID: CGDisplayCopyDisplayMode(display.id)?.ioDisplayModeID,
                      displayRotation: CGDisplayRotation(display.id),
                      window: window, launchedAt: application.launchDate, region: desktopRegion)
    }

    static func validate(_ target: Target) throws {
        try Task.checkCancellation()
        guard CGPreflightPostEventAccess() else { throw Failure.eventPermission }
        guard CGDisplayIsActive(target.displayID) != 0, CGDisplayBounds(target.displayID) == target.displayBounds,
              CGDisplayCopyDisplayMode(target.displayID)?.ioDisplayModeID == target.displayModeID,
              CGDisplayRotation(target.displayID) == target.displayRotation else {
            throw Failure.displayChanged
        }
        guard let window = topWindow(over: target.region), window == target.window,
              window.bounds.contains(target.region) else { throw Failure.windowChanged }
        guard let front = NSWorkspace.shared.frontmostApplication, !front.isTerminated,
              front.processIdentifier == target.window.pid, front.launchDate == target.launchedAt else {
            throw Failure.windowNotActive
        }
    }

    static func scroll(_ target: Target, regionHeight: CGFloat) throws {
        guard regionHeight == target.region.height, let distance = scrollDistance(for: regionHeight),
              let event = CGEvent(scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1,
                                  wheel1: -distance, wheel2: 0, wheel3: 0) else {
            throw Failure.noScrollEvent
        }
        // Event location is scoped to this event. It does not reposition the user's pointer.
        event.location = CGPoint(x: target.region.midX, y: target.region.midY)
        try validate(target)
        event.postToPid(target.window.pid)
    }

    fileprivate struct Window: Equatable {
        let id: CGWindowID
        let pid: pid_t
        let bounds: CGRect
    }

    private static func topWindow(over region: CGRect) -> Window? {
        // Quartz returns onscreen windows in front-to-back order. Skip our own capture controls.
        guard let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements],
                                                      kCGNullWindowID) as? [[String: Any]] else { return nil }
        let ownPID = ProcessInfo.processInfo.processIdentifier
        for info in windows {
            guard let layer = info[kCGWindowLayer as String] as? NSNumber, layer.intValue == 0,
                  let alpha = info[kCGWindowAlpha as String] as? NSNumber, alpha.doubleValue > 0,
                  let owner = info[kCGWindowOwnerPID as String] as? NSNumber, owner.int32Value > 0, owner.int32Value != ownPID,
                  let identifier = info[kCGWindowNumber as String] as? NSNumber, identifier.uint32Value != kCGNullWindowID,
                  let rawBounds = info[kCGWindowBounds as String] as? [String: Any] else { continue }
            var bounds = CGRect.zero
            guard CGRectMakeWithDictionaryRepresentation(rawBounds as CFDictionary, &bounds),
                  isUsable(bounds), !bounds.intersection(region).isEmpty else { continue }
            return Window(id: identifier.uint32Value, pid: owner.int32Value, bounds: bounds)
        }
        return nil
    }

    private static func globalRegion(_ local: CGRect, within displayBounds: CGRect) -> CGRect? {
        guard isUsable(displayBounds), isUsable(local),
              CGRect(origin: .zero, size: displayBounds.size).contains(local) else { return nil }
        return local.offsetBy(dx: displayBounds.minX, dy: displayBounds.minY)
    }

    private static func isUsable(_ rect: CGRect) -> Bool {
        rect.origin.x.isFinite && rect.origin.y.isFinite && rect.maxX.isFinite && rect.maxY.isFinite &&
            rect.width.isFinite && rect.height.isFinite && rect.width > 0 && rect.height > 0
    }

    private static func scrollDistance(for height: CGFloat) -> Int32? {
        guard height.isFinite, height >= 2 else { return nil }
        // Keep overlap for stitching, with a cap on very tall display selections.
        return Int32(max(1, min(2_000, (height * 0.65).rounded(.down))))
    }

    static func selfCheck() throws {
        let display = CGRect(x: -1_440, y: -180, width: 1_440, height: 900)
        let area = CGRect(x: 24, y: 32, width: 320, height: 400)
        guard globalRegion(area, within: display) == CGRect(x: -1_416, y: -148, width: 320, height: 400),
              globalRegion(area.offsetBy(dx: -25, dy: 0), within: display) == nil,
              scrollDistance(for: area.height) == 260, scrollDistance(for: 4_000) == 2_000,
              scrollDistance(for: .infinity) == nil else { throw Failure.invalidFixture }
    }

    private enum Failure: LocalizedError {
        case displayChanged, noWindow, eventPermission, windowChanged, windowNotActive, noScrollEvent, invalidFixture

        var errorDescription: String? {
            switch self {
            case .displayChanged: "The display or selected area changed. Select the area again."
            case .noWindow: "Select an area fully inside one visible app window for automatic scrolling."
            case .eventPermission: "Allow Snip Studio in System Settings under Privacy & Security → Accessibility to use automatic scrolling."
            case .windowChanged: "The selected window moved, resized, closed, or became covered."
            case .windowNotActive: "The selected app is no longer active. Bring it forward before restarting automatic capture."
            case .noScrollEvent: "Snip Studio could not prepare a scroll for this selection."
            case .invalidFixture: "Automatic scroll geometry did not pass its self-check."
            }
        }
    }
}
