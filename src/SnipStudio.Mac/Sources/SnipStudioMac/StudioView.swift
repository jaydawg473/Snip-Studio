import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct StudioView: View {
    @StateObject private var document = EditorDocument()
    @State private var inkColor = Color.red
    @State private var inkWidth: CGFloat = 4
    @State private var captureInProgress = false
    @State private var recognitionInProgress = false
    @State private var recognizedText: String?
    @State private var savedAnnotationRevision = 0
    @State private var status = "Capture a region or window to start."
    @State private var errorMessage: String?

    private var busy: Bool { captureInProgress || recognitionInProgress }

    var body: some View {
        VStack(spacing: 0) {
            if document.hasImage {
                if let recognizedText {
                    HSplitView {
                        canvas.frame(minWidth: 360)
                        textPanel(recognizedText).frame(minWidth: 220, idealWidth: 260)
                    }
                } else {
                    canvas
                }
            } else {
                ContentUnavailableView(
                    "No Screenshot",
                    systemImage: "camera.viewfinder",
                    description: Text("Capture a region or window, or open an image.")
                )
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }

            Divider()
            HStack(spacing: 8) {
                if busy {
                    ProgressView().controlSize(.small).accessibilityLabel("Working")
                }
                Text(status).lineLimit(1)
                Spacer()
                if let image = document.image {
                    Text("\(image.width) × \(image.height) px").monospacedDigit()
                }
            }
            .font(.footnote)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 14)
            .padding(.vertical, 9)
        }
        .frame(minWidth: 650, minHeight: 390)
        .toolbar {
            ToolbarItemGroup(placement: .automatic) {
                Menu("Capture", systemImage: "camera.viewfinder") {
                    Button("Capture Region") { capture(.region) }
                        .keyboardShortcut("n", modifiers: [.command, .shift])
                    Button("Capture Window") { capture(.window) }
                }
                .disabled(busy)

                Button("Open Image", systemImage: "folder") { openImage() }
                    .disabled(busy)

                Button("Undo Annotation", systemImage: "arrow.uturn.backward") { document.undo() }
                    .disabled(!document.canUndo)

                Button("Recognize Text", systemImage: "text.viewfinder") { recognizeText() }
                    .disabled(busy || !document.hasImage)

                Button("Copy Image", systemImage: "doc.on.doc") { copyImage() }
                    .disabled(!document.hasImage)

                Button("Save PNG", systemImage: "square.and.arrow.down") { saveImage() }
                    .disabled(!document.hasImage)
            }
        }
        .alert(
            "Snip Studio",
            isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })
        ) {
            Button("OK", role: .cancel) { errorMessage = nil }
        } message: {
            Text(errorMessage ?? "")
        }
    }

    private var canvas: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                ColorPicker("Pen", selection: $inkColor, supportsOpacity: false)
                    .fixedSize()
                Slider(value: $inkWidth, in: 1...30, step: 1) { Text("Pen width") }
                    .frame(width: 96)
                Text("\(Int(inkWidth)) px").monospacedDigit()
                Spacer()
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            Divider()
            SnipCanvas(document: document, inkColor: inkColor, inkWidth: inkWidth)
                .padding(14)
                .background(Color(nsColor: .controlBackgroundColor))
        }
    }

    private func textPanel(_ text: String) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Recognized Text").font(.headline)
                Spacer()
                Button("Close Text", systemImage: "xmark") { recognizedText = nil }
                    .labelStyle(.iconOnly)
            }
            ScrollView {
                Text(text.isEmpty ? "No text found." : text)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
            Button("Copy Text", systemImage: "doc.on.doc") { copyText(text) }
                .disabled(text.isEmpty)
        }
        .padding(14)
    }

    private func capture(_ mode: CaptureMode) {
        guard !busy, confirmImageReplacement() else { return }
        captureInProgress = true
        status = "Select the area to capture."
        Task { @MainActor in
            defer { captureInProgress = false }
            do {
                if let image = try await CaptureService.capture(mode) {
                    try load(image)
                } else {
                    status = "Capture canceled."
                }
            } catch {
                report(error)
            }
        }
    }

    private func openImage() {
        guard !busy, confirmImageReplacement() else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let image = NSImage(contentsOf: url) else {
            report(StudioError.imageUnreadable)
            return
        }
        do { try load(image) } catch { report(error) }
    }

    private func load(_ image: NSImage) throws {
        try document.replaceImage(image)
        recognizedText = nil
        savedAnnotationRevision = 0
        status = "Screenshot ready."
    }

    private func confirmImageReplacement() -> Bool {
        guard document.annotationRevision != savedAnnotationRevision else { return true }
        let alert = NSAlert()
        alert.messageText = "Replace the current screenshot?"
        alert.informativeText = "Save or copy the edited screenshot first if you want to keep it."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Replace")
        return alert.runModal() == .alertSecondButtonReturn
    }

    private func recognizeText() {
        guard !busy, let image = document.image else { return }
        recognitionInProgress = true
        status = "Recognizing text on this Mac…"
        Task { @MainActor in
            defer { recognitionInProgress = false }
            do {
                let result = try await TextRecognition.recognize(image)
                recognizedText = result
                status = result.isEmpty ? "No text found." : "Text ready to copy."
            } catch {
                report(error)
            }
        }
    }

    private func copyImage() {
        do {
            let png = try document.pngData()
            let item = NSPasteboardItem()
            guard item.setData(png, forType: .png) else { throw StudioError.copyFailed }
            try copy(item)
            status = "Image copied."
        } catch { report(error) }
    }

    private func copyText(_ text: String) {
        do {
            let item = NSPasteboardItem()
            guard item.setString(text, forType: .string) else { throw StudioError.copyFailed }
            try copy(item)
            status = "Text copied."
        } catch { report(error) }
    }

    private func copy(_ item: NSPasteboardItem) throws {
        let clipboard = NSPasteboard.general
        clipboard.clearContents()
        guard clipboard.writeObjects([item]) else { throw StudioError.copyFailed }
    }

    private func saveImage() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = "Snip.png"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try document.pngData().write(to: url, options: .atomic)
            savedAnnotationRevision = document.annotationRevision
            status = "Saved \(url.lastPathComponent)."
        } catch { report(error) }
    }

    private func report(_ error: Error) {
        status = "Action could not finish."
        errorMessage = error.localizedDescription
    }
}

private enum StudioError: LocalizedError {
    case imageUnreadable, copyFailed

    var errorDescription: String? {
        switch self {
        case .imageUnreadable: "Snip Studio could not read that image."
        case .copyFailed: "The clipboard could not accept the result. Try copying again."
        }
    }
}
