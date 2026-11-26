import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct StudioView: View {
    let applicationDelegate: StudioApplicationDelegate
    @StateObject private var document = EditorDocument()
    @AppStorage(StudioSettings.autoCopy) private var autoCopy = true
    @AppStorage(StudioSettings.saveOriginals) private var saveOriginals = true
    @AppStorage(StudioSettings.fitOnLoad) private var fitOnLoad = true
    @State private var captureMode = CaptureMode.region
    @State private var captureDelay = 0
    @State private var captureTask: Task<Void, Never>?
    @State private var tool = StudioTool.pen
    @State private var inkColor = Color.red
    @State private var inkWidth: CGFloat = 4
    @State private var highlightColor = Color.yellow
    @State private var highlightWidth: CGFloat = 14
    @State private var shape = SnipShape.rectangle
    @State private var lineStyle = SnipLineStyle.solid
    @State private var shapeColor = Color.red
    @State private var shapeWidth: CGFloat = 3
    @State private var showOptions = true
    @State private var zoomPercent: Int?
    @State private var displayedZoom = 100
    @State private var recognitionInProgress = false
    @State private var exportTask: Task<Void, Never>?
    @State private var automaticClipboardTask: Task<Void, Never>?
    @State private var openedImageRevision: UUID?
    @State private var recognizedLines: [RecognizedLine]?
    @State private var reviewText = false
    @State private var status = "Ready."
    @State private var errorMessage: String?

    private var busy: Bool { captureTask != nil || recognitionInProgress || exportTask != nil }
    private var recognizedText: String { recognizedLines?.map(\.text).joined(separator: "\n") ?? "" }
    private var drawingColor: Color {
        switch tool {
        case .highlight: highlightColor.opacity(0.42)
        case .shape: shapeColor
        default: inkColor
        }
    }

    private var drawingWidth: CGFloat {
        switch tool {
        case .highlight: highlightWidth
        case .shape: shapeWidth
        default: inkWidth
        }
    }
    private static let zoomPresets = [25, 50, 75, 100, 125, 150, 200, 300, 400]

    var body: some View {
        VStack(spacing: 0) {
            captureBar
            if document.hasImage {
                editingBar
                if showOptions { optionsBar }
                SnipCanvas(
                    document: document,
                    inkColor: drawingColor,
                    inkWidth: drawingWidth,
                    zoom: zoomPercent.map { CGFloat($0) / 100 },
                    allowsDrawing: tool != .move && tool != .textActions,
                    activeShape: tool == .shape ? shape : nil,
                    lineStyle: lineStyle,
                    erasesAnnotations: tool == .eraser,
                    recognizedLines: tool == .textActions ? recognizedLines ?? [] : []
                ) { scale in displayedZoom = max(1, Int((scale * 100).rounded())) }
            } else {
                Text("Press ⌘N to take a screenshot.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            statusBar
        }
        .background(StudioColors.canvas)
        .frame(minWidth: 520, minHeight: document.hasImage ? 320 : 260)
        .background {
            StudioCloseProtection(document: document, applicationDelegate: applicationDelegate)
                .frame(width: 0, height: 0)
                .accessibilityHidden(true)
        }
        .alert(
            "Snip Studio",
            isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })
        ) {
            Button("OK", role: .cancel) { errorMessage = nil }
        } message: {
            Text(errorMessage ?? "")
        }
        .sheet(isPresented: $reviewText) {
            textReview
        }
        .onDisappear {
            captureTask?.cancel()
            exportTask?.cancel()
            automaticClipboardTask?.cancel()
        }
        .onChange(of: document.contentRevision) { _, revision in
            scheduleAutomaticCopy(revision)
        }
        .onChange(of: autoCopy) { _, enabled in
            if !enabled { automaticClipboardTask?.cancel() }
        }
    }

    private var captureBar: some View {
        HStack(spacing: 6) {
            Button { capture() } label: {
                Label("New", systemImage: "plus")
                    .font(.system(size: 14, weight: .semibold))
                    .frame(width: 86, alignment: .leading)
            }
            .buttonStyle(StudioButtonStyle(primary: true))
            .keyboardShortcut("n", modifiers: .command)
            .disabled(busy)
            .help("New snip (⌘N)")

            Menu {
                Picker("Capture mode", selection: $captureMode) {
                    ForEach([CaptureMode.region, .window, .fullScreen, .allDisplays], id: \.self) { mode in
                        Label(mode.title, systemImage: mode.icon).tag(mode)
                    }
                }
            } label: {
                Label(captureMode.title, systemImage: captureMode.icon)
                    .font(.system(size: 12))
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Capture shape")
            .disabled(busy)

            Menu {
                Picker("Capture delay", selection: $captureDelay) {
                    Text("Off").tag(0)
                    ForEach([3, 5, 10], id: \.self) { seconds in
                        Text("\(seconds) seconds").tag(seconds)
                    }
                }
            } label: {
                Label(captureDelay == 0 ? "Off" : "\(captureDelay)s", systemImage: "clock")
                    .font(.system(size: 12))
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Capture delay")
            .disabled(busy)

            Spacer(minLength: 2)
            iconButton("Open image (⌘O)", symbol: "folder", action: openImage)
                .keyboardShortcut("o", modifiers: .command)
                .disabled(busy)
            iconButton("Paste image (⌘V)", symbol: "doc.on.clipboard", action: pasteImage)
                .keyboardShortcut("v", modifiers: .command)
                .disabled(busy)
            iconButton("Save PNG (⌘S)", symbol: "square.and.arrow.down", action: saveImage)
                .keyboardShortcut("s", modifiers: .command)
                .disabled(busy || !document.hasImage)
            iconButton("Copy image", symbol: "doc.on.doc") { exportImage() }
                .disabled(busy || !document.hasImage)
            Menu {
                Menu("Save image as…") {
                    ForEach(StudioFileFormat.allCases, id: \.self) { format in
                        Button(format.title) { saveImage(as: format) }
                    }
                }
                .disabled(busy || !document.hasImage)
                Button("Print image…") { printImage() }.disabled(busy || !document.hasImage)
                Divider()
                SettingsLink { Text("Settings…") }
            } label: {
                Image(systemName: "ellipsis").font(.system(size: 17))
                    .accessibilityLabel("More file actions")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("More file actions")
        }
        .padding(.horizontal, 12)
        .frame(height: 54)
        .background(StudioColors.toolbar)
    }

    private var editingBar: some View {
        HStack(spacing: 2) {
            toolButton(.move, label: "Move view", symbol: "arrow.up.and.down.and.arrow.left.and.right")
            toolButton(.pen, label: "Pen", symbol: "pencil.tip", color: inkColor)
            toolButton(.highlight, label: "Highlighter", symbol: "highlighter", color: highlightColor)
            Menu {
                ForEach(SnipShape.allCases, id: \.self) { selected in
                    Button {
                        shape = selected
                        selectTool(.shape)
                    } label: {
                        Label(selected.rawValue.capitalized, systemImage: selected.icon)
                    }
                }
            } label: {
                Image(systemName: shape.icon).font(.system(size: 17))
                    .padding(4)
                    .background(tool == .shape ? StudioColors.selected : .clear, in: RoundedRectangle(cornerRadius: 5))
                    .accessibilityLabel("Shapes")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Shapes")
            toolButton(.eraser, label: "Eraser", symbol: "eraser")
            Divider().frame(height: 20).padding(.horizontal, 4)
            iconButton("Undo annotation", symbol: "arrow.uturn.backward", action: document.undo)
                .disabled(!document.canUndo)
            iconButton("Redo annotation", symbol: "arrow.uturn.forward", action: document.redo)
                .disabled(!document.canRedo)
            iconButton("Text actions", symbol: "wand.and.stars", selected: tool == .textActions, action: toggleTextActions)
                .disabled(busy)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .frame(height: 36)
        .background(StudioColors.toolbar)
    }

    @ViewBuilder
    private var optionsBar: some View {
        switch tool {
        case .move:
            EmptyView()
        case .pen, .highlight, .shape:
            HStack(spacing: 10) {
                ColorPicker("Ink color", selection: tool == .shape ? $shapeColor : (tool == .highlight ? $highlightColor : $inkColor), supportsOpacity: false)
                    .labelsHidden()
                    .help("Ink color")
                Stepper(value: tool == .shape ? $shapeWidth : (tool == .highlight ? $highlightWidth : $inkWidth), in: 1...30, step: 1) {
                    Text("\(Int(drawingWidth)) px").monospacedDigit()
                }
                .fixedSize()
                .accessibilityLabel("Ink width")
                StudioStrokeSample(color: drawingColor, width: drawingWidth)
                if tool == .shape {
                    Picker("Line style", selection: $lineStyle) {
                        ForEach(SnipLineStyle.allCases, id: \.self) { style in
                            Text(style.rawValue.capitalized).tag(style)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                    .help("Line style")
                }
                Spacer(minLength: 0)
                iconButton("Hide options", symbol: "xmark") { showOptions = false }
            }
            .padding(.horizontal, 12)
            .frame(height: 40)
            .background(StudioColors.options)
        case .textActions:
            HStack(spacing: 6) {
                Button { copyText(recognizedText) } label: {
                    Label("Copy all text", systemImage: "doc.on.doc")
                }
                .buttonStyle(StudioButtonStyle())
                .disabled(recognizedText.isEmpty || exportTask != nil)
                Button { copyRecognizedTable() } label: {
                    Label("Copy as table", systemImage: "tablecells")
                }
                .buttonStyle(StudioButtonStyle())
                .disabled(recognizedText.isEmpty || exportTask != nil)
                Button { reviewText = true } label: {
                    Label("Review text…", systemImage: "text.alignleft")
                }
                .buttonStyle(StudioButtonStyle())
                .disabled(recognizedText.isEmpty)
                Spacer(minLength: 0)
                iconButton("Close text actions", symbol: "xmark") { selectTool(.pen) }
            }
            .padding(.horizontal, 12)
            .frame(height: 40)
            .background(StudioColors.options)
        case .eraser:
            HStack {
                Text("Click or drag over an annotation to erase it.")
                    .font(.system(size: 12))
                Spacer(minLength: 0)
                iconButton("Hide options", symbol: "xmark") { showOptions = false }
            }
            .padding(.horizontal, 12)
            .frame(height: 40)
            .background(StudioColors.options)
        }
    }

    private var statusBar: some View {
        HStack(spacing: 6) {
            if busy {
                ProgressView().controlSize(.small).accessibilityLabel("Working")
            }
            Text(status).lineLimit(1).truncationMode(.tail)
                .help(status)
            Spacer(minLength: 4)
            if captureTask != nil {
                Button("Cancel") { captureTask?.cancel() }
                    .controlSize(.small)
            }
            if let image = document.image {
                Text("\(image.width) × \(image.height) px").monospacedDigit().fixedSize()
                iconButton("Zoom out", symbol: "minus") { stepZoom(increasing: false) }
                    .disabled((zoomPercent ?? displayedZoom) <= (Self.zoomPresets.first ?? 25))
                Button("Fit") { zoomPercent = nil }
                    .buttonStyle(StudioButtonStyle(selected: zoomPercent == nil))
                    .help("Fit screenshot to window")
                Menu {
                    ForEach(Self.zoomPresets, id: \.self) { percent in
                        Button("\(percent)%") { zoomPercent = percent }
                    }
                } label: {
                    Text("\(zoomPercent ?? displayedZoom)%").monospacedDigit()
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .help("Zoom percentage")
                iconButton("Zoom in", symbol: "plus") { stepZoom(increasing: true) }
                    .disabled(zoomPercent == Self.zoomPresets.last)
            }
        }
        .font(.system(size: 12))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .frame(height: 34)
        .background(StudioColors.toolbar)
    }

    private func iconButton(
        _ title: String,
        symbol: String,
        selected: Bool = false,
        color: Color? = nil,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 17))
                .foregroundStyle(color ?? .primary)
                .frame(width: 20, height: 22)
        }
        .buttonStyle(StudioButtonStyle(selected: selected))
        .help(title)
        .accessibilityLabel(title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private func toolButton(_ value: StudioTool, label: String, symbol: String, color: Color? = nil) -> some View {
        iconButton(label, symbol: symbol, selected: tool == value, color: color) { selectTool(value) }
    }

    private var textReview: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Recognized text").font(.headline)
            ScrollView {
                Text(recognizedText)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
            HStack {
                Button("Copy all text") { copyText(recognizedText) }
                Spacer()
                Button("Done") { reviewText = false }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(18)
        .frame(minWidth: 420, minHeight: 280)
    }

    private func selectTool(_ value: StudioTool) {
        document.cancelStroke()
        tool = value
        showOptions = true
        switch value {
        case .move: status = "Scroll to move around a zoomed screenshot."
        case .pen: status = "Draw on the screenshot."
        case .highlight: status = "Drag to highlight an area."
        case .shape: status = "Drag to draw a \(shape.rawValue)."
        case .eraser: status = "Erase annotations. Use Undo to restore them."
        case .textActions: break
        }
    }

    private func stepZoom(increasing: Bool) {
        let current = zoomPercent ?? displayedZoom
        zoomPercent = increasing
            ? Self.zoomPresets.first(where: { $0 > current }) ?? Self.zoomPresets.last
            : Self.zoomPresets.last(where: { $0 < current }) ?? Self.zoomPresets.first
    }

    private func capture() {
        guard !busy, confirmDiscard(document, title: "Replace the current screenshot?") else { return }
        let mode = captureMode
        let delay = captureDelay
        captureTask = Task { @MainActor in
            defer { captureTask = nil }
            do {
                for seconds in (0..<delay).reversed() {
                    status = "Capture starts in \(seconds + 1)s."
                    try await Task.sleep(for: .seconds(1))
                }
                status = mode == .region || mode == .window ? "Select what to capture." : "Capturing the screen…"
                if let image = try await CaptureService.capture(mode) {
                    try load(image, alreadySaved: false)
                    if saveOriginals { saveOriginalCapture() }
                } else {
                    status = "Capture canceled."
                }
            } catch is CancellationError {
                status = "Capture canceled."
            } catch {
                report(error)
            }
        }
    }

    private func openImage() {
        guard !busy, confirmDiscard(document, title: "Replace the current screenshot?") else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let image = NSImage(contentsOf: url) else {
            report(StudioError.imageUnreadable)
            return
        }
        do { try load(image, alreadySaved: true) } catch { report(error) }
    }

    private func pasteImage() {
        guard !busy, confirmDiscard(document, title: "Replace the current screenshot?") else { return }
        guard let clipboardImage = NSImage(pasteboard: .general) else {
            report(StudioError.noClipboardImage)
            return
        }
        do { try load(clipboardImage, alreadySaved: false) } catch { report(error) }
    }

    private func load(_ image: NSImage, alreadySaved: Bool) throws {
        try document.replaceImage(image, alreadySaved: alreadySaved)
        openedImageRevision = alreadySaved ? document.contentRevision : nil
        recognizedLines = nil
        zoomPercent = fitOnLoad ? nil : 100
        selectTool(.pen)
        status = "Screenshot ready."
    }

    private func toggleTextActions() {
        if tool == .textActions {
            selectTool(.pen)
            return
        }
        guard !busy, let image = document.image else { return }
        selectTool(.textActions)
        if let recognizedLines {
            status = recognizedLines.isEmpty ? "No text found." : "Text ready to copy."
            return
        }

        recognitionInProgress = true
        status = "Recognizing text on this Mac…"
        Task { @MainActor in
            defer { recognitionInProgress = false }
            do {
                let lines = try await TextRecognition.recognizeLines(image)
                recognizedLines = lines
                if tool == .textActions { status = lines.isEmpty ? "No text found." : "Text ready to copy." }
            } catch {
                report(error)
            }
        }
    }

    private func copyText(_ text: String) {
        automaticClipboardTask?.cancel()
        do {
            let item = NSPasteboardItem()
            guard item.setString(text, forType: .string) else { throw StudioError.copyFailed }
            try copy(item)
            status = "Text copied."
        } catch { report(error) }
    }

    private func copyRecognizedTable() {
        guard let recognizedLines, !recognizedLines.isEmpty else { return }
        automaticClipboardTask?.cancel()
        do {
            let result = try OCRTableClipboard.write(recognizedLines)
            status = "Copied \(result.rowCount) rows × \(result.columnCount) columns."
        } catch { report(error) }
    }

    private func copy(_ item: NSPasteboardItem) throws {
        let clipboard = NSPasteboard.general
        clipboard.clearContents()
        guard clipboard.writeObjects([item]) else { throw StudioError.copyFailed }
    }

    private func scheduleAutomaticCopy(_ revision: UUID) {
        automaticClipboardTask?.cancel()
        if revision == openedImageRevision {
            openedImageRevision = nil
            return
        }
        guard autoCopy, let snapshot = try? document.pngSnapshot() else { return }
        let expectedChangeCount = NSPasteboard.general.changeCount
        automaticClipboardTask = Task { @MainActor in
            do {
                try await Task.sleep(for: .milliseconds(160))
                let png = try await snapshot.pngData()
                try Task.checkCancellation()
                guard document.contentRevision == revision,
                      NSPasteboard.general.changeCount == expectedChangeCount else { return }
                try copyPNG(png)
            } catch is CancellationError {
                return
            } catch {
                if document.contentRevision == revision { status = "Automatic clipboard copy failed. Use Copy image to retry." }
            }
        }
    }

    private func saveOriginalCapture() {
        guard let snapshot = try? document.pngSnapshot() else { return }
        let revision = document.contentRevision
        Task { @MainActor in
            do {
                try await StudioOriginalCapture.save(snapshot)
            } catch {
                if document.contentRevision == revision {
                    status = "Original capture could not be saved to Pictures/Snip Studio. Use Save to keep it."
                }
            }
        }
    }

    private func copyPNG(_ png: Data) throws {
        let item = NSPasteboardItem()
        guard item.setData(png, forType: .png) else { throw StudioError.copyFailed }
        try copy(item)
    }

    private func saveImage() {
        saveImage(as: .png)
    }

    private func saveImage(as format: StudioFileFormat) {
        guard !busy, document.hasImage else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [format.contentType]
        panel.nameFieldStringValue = "Snip.\(format.filenameExtension)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        exportImage(to: url, format: format)
    }

    private func printImage() {
        guard !busy, document.hasImage else { return }
        do {
            let snapshot = try document.pngSnapshot()
            status = "Preparing print preview…"
            exportTask = Task { @MainActor in
                defer { exportTask = nil }
                do {
                    status = try await StudioFileExport.print(snapshot) ? "Print job submitted." : "Printing did not finish."
                } catch is CancellationError {
                    status = "Printing canceled."
                } catch {
                    report(error)
                }
            }
        } catch { report(error) }
    }

    private func exportImage(to url: URL? = nil, format: StudioFileFormat = .png) {
        guard !busy, document.hasImage else { return }
        if url == nil { automaticClipboardTask?.cancel() }
        do {
            let snapshot = try document.pngSnapshot()
            status = url == nil ? "Preparing image for the clipboard…" : "Saving \(format.title)…"
            exportTask = Task { @MainActor in
                defer { exportTask = nil }
                do {
                    if let url {
                        try await StudioFileExport.write(snapshot, as: format, to: url)
                        document.markSaved(snapshot)
                        status = "Saved \(url.lastPathComponent)."
                        if document.hasUnsavedChanges {
                            status += " Newer edits are unsaved."
                        }
                    } else {
                        let png = try await snapshot.pngData()
                        try Task.checkCancellation()
                        try copyPNG(png)
                        status = "Image copied."
                    }
                } catch is CancellationError {
                    status = "Image export canceled."
                } catch {
                    report(error)
                }
            }
        } catch { report(error) }
    }

    private func report(_ error: Error) {
        status = "Action could not finish."
        errorMessage = error.localizedDescription
    }
}

private enum StudioError: LocalizedError {
    case imageUnreadable, noClipboardImage, copyFailed

    var errorDescription: String? {
        switch self {
        case .imageUnreadable: "Snip Studio could not read that image."
        case .noClipboardImage: "The clipboard doesn't contain an image."
        case .copyFailed: "The clipboard could not accept the result. Try copying again."
        }
    }
}
