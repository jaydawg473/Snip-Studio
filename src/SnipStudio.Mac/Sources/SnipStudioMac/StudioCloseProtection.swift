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

    func makeNSView(context: Context) -> StudioCloseProtectionView {
        StudioCloseProtectionView(document: document, applicationDelegate: applicationDelegate)
    }

    func updateNSView(_ view: StudioCloseProtectionView, context: Context) {
        view.install()
    }
}

@MainActor
final class StudioCloseProtectionView: NSView {
    private let closeDelegate: StudioWindowDelegate

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

    func install() {
        guard let window else { return }
        closeDelegate.install(on: window)
    }
}

@MainActor
fileprivate final class StudioWindowDelegate: NSObject, NSWindowDelegate {
    weak var document: EditorDocument?
    private(set) weak var window: NSWindow?
    private weak var applicationDelegate: StudioApplicationDelegate?
    private weak var originalDelegate: (any NSWindowDelegate)?

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

    private func unregisterWindow() {
        applicationDelegate?.unregister(self)
        window = nil
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
