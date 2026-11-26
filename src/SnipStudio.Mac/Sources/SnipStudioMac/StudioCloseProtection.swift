import AppKit
import SwiftUI

@MainActor
func confirmDiscard(_ document: EditorDocument?, title: String) -> Bool {
    guard document?.hasUnsavedChanges == true else { return true }
    return confirmDiscard(title: title, detail: "Choose Cancel to keep editing or save the screenshot as a PNG.")
}

@MainActor
private func confirmDiscard(title: String, detail: String) -> Bool {
    let alert = NSAlert()
    alert.messageText = title
    alert.informativeText = detail
    alert.addButton(withTitle: "Cancel").keyEquivalent = "\u{1b}"
    alert.addButton(withTitle: "Discard").keyEquivalent = ""
    return alert.runModal() == .alertSecondButtonReturn
}

@MainActor
final class StudioApplicationDelegate: NSObject, NSApplicationDelegate {
    private let editors = NSHashTable<StudioWindowDelegate>.weakObjects()
    private var pendingSnips: [(id: UUID, image: NSImage)] = []
    var hasActiveRecording = false
    var captureInProgress = false
    var clipboardGeneration = UUID()

    fileprivate func register(_ editor: StudioWindowDelegate) {
        editors.add(editor)
    }

    fileprivate func unregister(_ editor: StudioWindowDelegate) {
        editors.remove(editor)
    }

    func storePendingSnip(_ image: NSImage) -> UUID {
        // Cap captures that SwiftUI did not deliver to a window. Open documents own their images separately.
        if pendingSnips.count == 4 { pendingSnips.removeFirst() }
        let id = UUID()
        pendingSnips.append((id, image))
        return id
    }

    func takePendingSnip(_ id: UUID) -> NSImage? {
        guard let index = pendingSnips.firstIndex(where: { $0.id == id }) else { return nil }
        return pendingSnips.remove(at: index).image
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if hasActiveRecording {
            let alert = NSAlert()
            alert.messageText = "Stop recording before quitting."
            alert.informativeText = "Open Record video and select Stop to save the recording."
            alert.addButton(withTitle: "OK")
            alert.runModal()
            return .terminateCancel
        }
        guard editors.allObjects.contains(where: { $0.document?.hasUnsavedChanges == true }) else {
            return .terminateNow
        }
        return confirmDiscard(title: "Quit without saving?", detail: "Any unsaved changes to open snips will be lost.")
            ? .terminateNow : .terminateCancel
    }

    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let closeSnips = menu.addItem(withTitle: "Close All Snips Without Saving…",
                                      action: #selector(closeAllSnipsFromDock(_:)), keyEquivalent: "")
        closeSnips.target = self
        closeSnips.isEnabled = !openSnips.isEmpty
        return menu
    }

    @objc func closeAllSnipsWithoutSaving() {
        let snips = openSnips
        guard !snips.isEmpty else { return }
        if snips.contains(where: { $0.document?.hasUnsavedChanges == true }) {
            guard confirmDiscard(title: "Close all snips without saving?",
                                 detail: "Any unsaved changes to these snips will be lost.") else { return }
        }
        // This deliberate batch action already confirmed discarding any unsaved work.
        for editor in snips { editor.window?.close() }
    }

    @objc private func closeAllSnipsFromDock(_ sender: Any?) {
        closeAllSnipsWithoutSaving()
    }

    private var openSnips: [StudioWindowDelegate] {
        editors.allObjects.filter { $0.window != nil && $0.document?.hasImage == true }
    }
}

struct StudioCloseProtection: NSViewRepresentable {
    let document: EditorDocument
    let applicationDelegate: StudioApplicationDelegate
    var imageSize: CGSize? = nil
    var resizeToSnip = true

    func makeNSView(context: Context) -> StudioCloseProtectionView {
        StudioCloseProtectionView(document: document, applicationDelegate: applicationDelegate)
    }

    func updateNSView(_ view: StudioCloseProtectionView, context: Context) {
        view.update(imageSize: imageSize, resizeToSnip: resizeToSnip)
    }
}

@MainActor
final class StudioCloseProtectionView: NSView {
    private let closeDelegate: StudioWindowDelegate
    private var imageSize: CGSize?
    private var pendingImageSize: CGSize?
    private var resizeScheduled = false

    init(document: EditorDocument, applicationDelegate: StudioApplicationDelegate) {
        closeDelegate = StudioWindowDelegate(document: document, applicationDelegate: applicationDelegate)
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { nil }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        if let window, window !== newWindow { closeDelegate.remove(from: window) }
        super.viewWillMove(toWindow: newWindow)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        // Let SwiftUI finish setting up its window delegate before wrapping it.
        Task { @MainActor [weak self] in self?.install() }
    }

    func update(imageSize: CGSize?, resizeToSnip: Bool) {
        if self.imageSize != imageSize {
            self.imageSize = imageSize
            pendingImageSize = imageSize
        }
        if !resizeToSnip { pendingImageSize = nil }
        install()
    }

    private func install() {
        guard let window else { return }
        closeDelegate.install(on: window)
        scheduleResize()
    }

    private func scheduleResize() {
        guard pendingImageSize != nil, !resizeScheduled else { return }
        resizeScheduled = true
        Task { @MainActor [weak self] in
            // Set the native frame after SwiftUI has applied the editor's content minimum.
            await Task.yield()
            guard let self else { return }
            self.resizeScheduled = false
            guard let imageSize = self.pendingImageSize, self.window != nil else { return }
            self.pendingImageSize = nil
            self.closeDelegate.resizeToFit(imageSize)
        }
    }
}

@MainActor
fileprivate final class StudioWindowDelegate: NSObject, NSWindowDelegate {
    weak var document: EditorDocument?
    private(set) weak var window: NSWindow?
    private weak var applicationDelegate: StudioApplicationDelegate?
    private weak var originalDelegate: (any NSWindowDelegate)?
    private var liveResizeStartSize: CGSize?
    private var wasResizedManually = false
    private var lastAutomaticFrame: CGRect?

    init(document: EditorDocument, applicationDelegate: StudioApplicationDelegate) {
        self.document = document
        self.applicationDelegate = applicationDelegate
    }

    func install(on window: NSWindow) {
        guard window.delegate !== self else { return }
        self.window = window
        originalDelegate = window.delegate
        window.delegate = self
        applicationDelegate?.register(self)
    }

    func remove(from window: NSWindow) {
        if window.delegate === self { window.delegate = originalDelegate }
        originalDelegate = nil
        unregisterWindow()
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard confirmDiscard(document, title: "Close without saving?") else { return false }
        return originalDelegate?.windowShouldClose?(sender) ?? true
    }

    func windowWillClose(_ notification: Notification) {
        unregisterWindow()
        originalDelegate?.windowWillClose?(notification)
    }

    func windowWillStartLiveResize(_ notification: Notification) {
        liveResizeStartSize = window?.frame.size
        originalDelegate?.windowWillStartLiveResize?(notification)
    }

    func windowDidEndLiveResize(_ notification: Notification) {
        if let startSize = liveResizeStartSize, let window, window.frame.size != startSize {
            wasResizedManually = true
        }
        liveResizeStartSize = nil
        originalDelegate?.windowDidEndLiveResize?(notification)
    }

    func resizeToFit(_ imageSize: CGSize) {
        guard let window, !wasResizedManually, liveResizeStartSize == nil, !window.inLiveResize,
              !window.isMiniaturized, !window.styleMask.contains(.fullScreen),
              let workArea = (window.screen ?? NSScreen.main)?.visibleFrame,
              imageSize.width.isFinite, imageSize.height.isFinite,
              imageSize.width > 0, imageSize.height > 0, workArea.width > 0, workArea.height > 0 else { return }

        let currentFrame = window.frame
        // AppKit also reports zoomed if an earlier automatic frame filled the work area.
        guard !window.isZoomed || lastAutomaticFrame == currentFrame else { return }
        let contentFrame = window.contentRect(forFrameRect: currentFrame)
        let frameWidth = max(0, currentFrame.width - contentFrame.width)
        let frameHeight = max(0, currentFrame.height - contentFrame.height)
        let padding: CGFloat = 48
        // Capture, editor, tool options and status rows are measured in points.
        let controlHeight: CGFloat = 54 + 36 + 40 + 34
        let displayScale = max(1, window.backingScaleFactor)
        let nativeWidth = imageSize.width / displayScale
        let nativeHeight = imageSize.height / displayScale
        let fit = min(1, max(1, workArea.width - frameWidth - padding) / nativeWidth,
                      max(1, workArea.height - frameHeight - controlHeight - padding) / nativeHeight)

        let minimumWidth = max(520, window.contentMinSize.width) + frameWidth
        let minimumHeight = max(320, window.contentMinSize.height) + frameHeight
        let width = min(workArea.width, max(minimumWidth, ceil(nativeWidth * fit + padding + frameWidth)))
        let height = min(workArea.height, max(minimumHeight, ceil(nativeHeight * fit + padding + frameHeight + controlHeight)))
        let frame = CGRect(
            x: max(workArea.minX, min(currentFrame.minX, workArea.maxX - width)),
            y: max(workArea.minY, min(currentFrame.maxY - height, workArea.maxY - height)),
            width: width, height: height
        )
        if frame != currentFrame {
            window.setFrame(frame, display: true)
            lastAutomaticFrame = window.frame
        }
    }

    private func unregisterWindow() {
        applicationDelegate?.unregister(self)
        window = nil
        liveResizeStartSize = nil
    }

    // Preserve SwiftUI's optional window delegate callbacks.
    nonisolated override func responds(to selector: Selector!) -> Bool {
        if super.responds(to: selector) { return true }
        guard Thread.isMainThread else { return false }
        return MainActor.assumeIsolated { originalDelegate?.responds(to: selector) == true }
    }

    nonisolated override func forwardingTarget(for selector: Selector!) -> Any? {
        if Thread.isMainThread, let target = MainActor.assumeIsolated({
            originalDelegate?.responds(to: selector) == true ? originalDelegate : nil
        }) {
            return target
        }
        return super.forwardingTarget(for: selector)
    }
}
