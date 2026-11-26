import AppKit
import SwiftUI

@MainActor
func confirmDiscard(_ document: EditorDocument?, title: String) -> Bool {
    guard document?.hasUnsavedChanges == true else { return true }
    let alert = NSAlert()
    alert.messageText = title
    alert.informativeText = "Choose Cancel to keep editing or save the screenshot as a PNG."
    alert.addButton(withTitle: "Cancel").keyEquivalent = "\u{1b}"
    alert.addButton(withTitle: "Discard").keyEquivalent = ""
    return alert.runModal() == .alertSecondButtonReturn
}

@MainActor
final class StudioApplicationDelegate: NSObject, NSApplicationDelegate {
    fileprivate weak var editorWindow: StudioWindowDelegate?
    var hasActiveRecording = false

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if hasActiveRecording {
            let alert = NSAlert()
            alert.messageText = "Stop recording before quitting."
            alert.informativeText = "Open Record video and select Stop to save the recording."
            alert.addButton(withTitle: "OK")
            alert.runModal()
            return .terminateCancel
        }
        return confirmDiscard(editorWindow?.document, title: "Quit without saving?") ? .terminateNow : .terminateCancel
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
    private weak var applicationDelegate: StudioApplicationDelegate?
    private weak var originalDelegate: (any NSWindowDelegate)?

    init(document: EditorDocument, applicationDelegate: StudioApplicationDelegate) {
        self.document = document
        self.applicationDelegate = applicationDelegate
    }

    func install(on window: NSWindow) {
        guard window.delegate !== self else { return }
        originalDelegate = window.delegate
        window.delegate = self
        applicationDelegate?.editorWindow = self
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
        if applicationDelegate?.editorWindow === self {
            applicationDelegate?.editorWindow = nil
        }
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
