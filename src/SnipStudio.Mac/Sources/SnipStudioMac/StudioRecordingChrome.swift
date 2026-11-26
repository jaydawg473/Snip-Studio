import AppKit
import CoreGraphics
import SwiftUI

@MainActor
struct StudioRecordingChrome: NSViewRepresentable {
    let recordingFrame: CGRect?
    let isActive: Bool

    func makeNSView(context: Context) -> StudioRecordingChromeView {
        let view = StudioRecordingChromeView(frame: .zero)
        view.configure(recordingFrame: recordingFrame, isActive: isActive)
        return view
    }

    func updateNSView(_ view: StudioRecordingChromeView, context: Context) {
        view.configure(recordingFrame: recordingFrame, isActive: isActive)
    }

    static func dismantleNSView(_ view: StudioRecordingChromeView, coordinator: ()) {
        view.disconnect()
    }
}

@MainActor
final class StudioRecordingChromeView: NSView {
    private struct WindowStyle {
        let level: NSWindow.Level
        let collectionBehavior: NSWindow.CollectionBehavior
    }

    private weak var controlsWindow: NSWindow?
    private var originalStyle: WindowStyle?
    private var recordingFrame: CGRect?
    private var isActive = false
    private var positionedControls = false
    private var displayGeometryChanged = false
    private var borderWindows: [NSPanel] = []
    private var visibleStrips: [CGRect] = []

    func configure(recordingFrame: CGRect?, isActive: Bool) {
        self.recordingFrame = recordingFrame
        self.isActive = isActive
        apply()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard controlsWindow !== window else { return }
        disconnect()
        guard let window else { return }
        controlsWindow = window
        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(controlWindowWillClose),
                           name: NSWindow.willCloseNotification, object: window)
        center.addObserver(self, selector: #selector(screenParametersDidChange),
                           name: NSApplication.didChangeScreenParametersNotification, object: nil)
        apply()
    }

    func disconnect() {
        NotificationCenter.default.removeObserver(self)
        restoreWindowStyle()
        removeBorder()
        controlsWindow = nil
        positionedControls = false
        displayGeometryChanged = false
    }

    private func apply() {
        guard let controlsWindow else { return }
        guard isActive else {
            restoreWindowStyle()
            removeBorder()
            positionedControls = false
            displayGeometryChanged = false
            return
        }

        if originalStyle == nil {
            originalStyle = WindowStyle(level: controlsWindow.level, collectionBehavior: controlsWindow.collectionBehavior)
            if controlsWindow.level.rawValue < NSWindow.Level.floating.rawValue { controlsWindow.level = .floating }
            if !controlsWindow.styleMask.contains(.fullScreen) {
                var behavior = controlsWindow.collectionBehavior
                behavior.subtract([.moveToActiveSpace, .fullScreenPrimary, .fullScreenNone])
                behavior.formUnion([.canJoinAllSpaces, .fullScreenAuxiliary])
                controlsWindow.collectionBehavior = behavior
            }
        }

        guard !displayGeometryChanged, let recordingFrame,
              let area = StudioRecordingChromeGeometry.appKitFrame(
                  recordingFrame, primaryDisplayHeight: CGDisplayBounds(CGMainDisplayID()).height
              ),
              let screen = NSScreen.screens.first(where: { $0.frame.insetBy(dx: -0.5, dy: -0.5).contains(area) }) else {
            removeBorder()
            return
        }

        if !positionedControls {
            positionedControls = true
            if !controlsWindow.styleMask.contains(.fullScreen), !controlsWindow.isMiniaturized,
               let point = StudioRecordingChromeGeometry.controlOrigin(
                   controlsWindow.frame, outside: area, visibleFrame: screen.visibleFrame
               ) {
                controlsWindow.setFrameOrigin(point)
            }
        }
        showBorder(StudioRecordingChromeGeometry.borderStrips(around: area, on: screen.frame))
    }

    private func restoreWindowStyle() {
        if let controlsWindow, let originalStyle {
            controlsWindow.level = originalStyle.level
            controlsWindow.collectionBehavior = originalStyle.collectionBehavior
        }
        originalStyle = nil
    }

    private func showBorder(_ strips: [CGRect]) {
        guard strips != visibleStrips else { return }
        removeBorder()
        visibleStrips = strips
        borderWindows = strips.map { strip in
            // Small strip windows avoid a display-sized transparent surface.
            let panel = NSPanel(contentRect: strip, styleMask: [.borderless, .nonactivatingPanel],
                                backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.isFloatingPanel = true
            panel.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue - 1)
            panel.isExcludedFromWindowsMenu = true
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
            panel.backgroundColor = .systemRed
            panel.alphaValue = 0.9
            panel.isOpaque = false
            panel.hasShadow = false
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.orderFrontRegardless()
            return panel
        }
    }

    private func removeBorder() {
        borderWindows.forEach { $0.close() }
        borderWindows.removeAll()
        visibleStrips.removeAll()
    }

    @objc private func controlWindowWillClose(_ notification: Notification) {
        disconnect()
    }

    @objc private func screenParametersDidChange(_ notification: Notification) {
        guard isActive else { return }
        // An active capture may still be using the preceding display arrangement.
        displayGeometryChanged = true
        removeBorder()
    }
}

enum StudioRecordingChromeGeometry {
    static func appKitFrame(_ quartzFrame: CGRect, primaryDisplayHeight: CGFloat) -> CGRect? {
        guard primaryDisplayHeight.isFinite, primaryDisplayHeight > 0, !quartzFrame.isEmpty,
              quartzFrame.minX.isFinite, quartzFrame.maxX.isFinite,
              quartzFrame.minY.isFinite, quartzFrame.maxY.isFinite else { return nil }
        return CGRect(x: quartzFrame.minX, y: primaryDisplayHeight - quartzFrame.maxY,
                      width: quartzFrame.width, height: quartzFrame.height)
    }

    static func controlOrigin(_ currentFrame: CGRect, outside area: CGRect, visibleFrame: CGRect) -> CGPoint? {
        guard currentFrame.intersects(area), currentFrame.width <= visibleFrame.width,
              currentFrame.height <= visibleFrame.height else { return nil }
        let x = min(visibleFrame.maxX - currentFrame.width, max(visibleFrame.minX, area.midX - currentFrame.width / 2))
        let gap: CGFloat = 9
        let outsideFrames = [area.maxY + gap, area.minY - gap - currentFrame.height].map { y in
            CGRect(origin: CGPoint(x: x, y: y), size: currentFrame.size)
        }
        return outsideFrames.first(where: { visibleFrame.contains($0) })?.origin
    }

    static func borderStrips(around area: CGRect, on screen: CGRect) -> [CGRect] {
        // Keep markers outside a crop where possible. The display edge gets an inset marker.
        let thickness: CGFloat = 2
        let outer = area.insetBy(dx: -thickness, dy: -thickness).intersection(screen)
        guard outer.width > thickness * 2, outer.height > thickness * 2 else { return [] }
        return [
            CGRect(x: outer.minX, y: outer.minY, width: outer.width, height: thickness),
            CGRect(x: outer.minX, y: outer.maxY - thickness, width: outer.width, height: thickness),
            CGRect(x: outer.minX, y: outer.minY + thickness, width: thickness, height: outer.height - thickness * 2),
            CGRect(x: outer.maxX - thickness, y: outer.minY + thickness, width: thickness, height: outer.height - thickness * 2)
        ]
    }

    static func selfCheck() throws {
        let secondary = CGRect(x: -1260, y: -200, width: 500, height: 100)
        guard appKitFrame(secondary, primaryDisplayHeight: 900) == CGRect(x: -1260, y: 1000, width: 500, height: 100) else {
            throw CheckFailure.incorrectDisplayCoordinates
        }
        let visible = CGRect(x: 0, y: 23, width: 1200, height: 850)
        let region = CGRect(x: 100, y: 100, width: 800, height: 400)
        let controls = CGRect(x: 250, y: 300, width: 400, height: 95)
        guard controlOrigin(controls, outside: region, visibleFrame: visible) == CGPoint(x: 300, y: 509),
              controlOrigin(controls, outside: visible, visibleFrame: visible) == nil else {
            throw CheckFailure.incorrectControlPlacement
        }
        let screen = CGRect(x: 0, y: 0, width: 1200, height: 900)
        let strips = borderStrips(around: region, on: screen)
        guard strips.count == 4, strips.allSatisfy({ $0.intersection(region).isEmpty && screen.contains($0) }) else {
            throw CheckFailure.incorrectBoundaryPlacement
        }
    }

    private enum CheckFailure: Error {
        case incorrectDisplayCoordinates, incorrectControlPlacement, incorrectBoundaryPlacement
    }
}
