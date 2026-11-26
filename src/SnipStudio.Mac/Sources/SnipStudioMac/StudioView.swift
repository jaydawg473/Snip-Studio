import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct StudioView: View {
    let applicationDelegate: StudioApplicationDelegate
    let sessionID: UUID?
    @Environment(\.openWindow) private var openWindow
    @StateObject private var document = EditorDocument()
    @AppStorage(StudioSettings.autoCopy) private var autoCopy = true
    @AppStorage(StudioSettings.saveOriginals) private var saveOriginals = true
    @AppStorage(StudioSettings.fitOnLoad) private var fitOnLoad = true
    @State private var captureMode = CaptureMode.region
    @State private var videoMode = false
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
    @State private var cropBounds = CGRect.null
    @State private var selectedAnnotationID: UUID?
    @State private var textDraft: StudioTextDraft?
    @State private var textStyle = StudioTextAnnotation(text: "")
    @State private var textColor = Color.black
    @State private var showOptions = true
    @State private var zoomPercent: Int?
    @State private var displayedZoom = 100
    @State private var recognitionInProgress = false
    @State private var exportTask: Task<Void, Never>?
    @State private var automaticClipboardTask: Task<Void, Never>?
    @State private var openedImageRevision: UUID?
    @State private var recognizedLines: [RecognizedLine]?
    @State private var selectedOCRIndices: Set<Int> = []
    @State private var reviewText = false
    @State private var reviewedContacts: [OCRContact] = []
    @State private var reviewRedactions = false
    @State private var status = "Ready."
    @State private var errorMessage: String?
    @State private var updateTask: Task<Void, Never>?
    @State private var updateMessage: String?
    @State private var updateURL: URL?
    @State private var externalWorkingCopy: URL?
    @State private var recentFiles = NSDocumentController.shared.recentDocumentURLs

    private var busy: Bool { captureTask != nil || recognitionInProgress || exportTask != nil }
    private var fileActionsDisabled: Bool { busy || textDraft != nil }
    private var recognizedText: String { recognizedLines?.map(\.text).joined(separator: "\n") ?? "" }
    private var selectedOCRLines: [RecognizedLine] {
        guard let recognizedLines else { return [] }
        return recognizedLines.indices.filter { selectedOCRIndices.contains($0) }.map { recognizedLines[$0] }
    }
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
    private static let projectType = UTType(exportedAs: "com.automationforge.snipstudio.editable-mac")

    var body: some View {
        workspace
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
        .alert(
            "Snip Studio Updates",
            isPresented: Binding(get: { updateMessage != nil }, set: { if !$0 { updateMessage = nil } })
        ) {
            if let updateURL { Button("View release") { NSWorkspace.shared.open(updateURL) } }
            Button(updateURL == nil ? "OK" : "Close", role: .cancel) { updateMessage = nil }
        } message: {
            Text(updateMessage ?? "")
        }
        .sheet(isPresented: $reviewText) {
            textReview
        }
        .sheet(isPresented: $reviewRedactions) {
            if let image = document.image {
                StudioRedactionReview(image: image, contacts: reviewedContacts) { contacts in
                    let size = CGSize(width: image.width, height: image.height)
                    patchRaster(contacts.map { $0.pixelRegion(in: size) }, opaque: true)
                }
            }
        }
        .focusedSceneValue(\.studioNewSnip, startNew)
        .task {
            guard let sessionID, let capturedImage = applicationDelegate.takePendingSnip(sessionID) else { return }
            do { try finishCapture(with: capturedImage) } catch { report(error) }
        }
        .onDisappear {
            captureTask?.cancel()
            exportTask?.cancel()
            automaticClipboardTask?.cancel()
            updateTask?.cancel()
        }
        .onChange(of: document.contentRevision) { _, revision in
            scheduleAutomaticCopy(revision)
            if let selectedAnnotationID, document.annotation(withID: selectedAnnotationID) == nil {
                self.selectedAnnotationID = nil
            }
        }
        .onChange(of: autoCopy) { _, enabled in
            if !enabled { automaticClipboardTask?.cancel() }
        }
        .onChange(of: document.rasterRevision) { _, _ in
            recognizedLines = nil
            selectedOCRIndices.removeAll()
            if tool == .crop {
                cropBounds = fullImageBounds
            } else if tool == .removeArea {
                selectTool(.removeArea)
            } else if tool == .textActions {
                selectTool(.pen)
            }
        }
    }

    private var workspace: some View {
        VStack(spacing: 0) {
            captureBar
            if document.hasImage {
                editingBar
                if showOptions { optionsBar }
                editorCanvas
            } else {
                Text("Press ⌘N to take a screenshot.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            statusBar
        }
    }

    private var editorCanvas: SnipCanvas {
        let textTap: ((CGPoint) -> Void)? = tool == .text ? { beginText(at: $0) } : nil
        let zoom = zoomPercent.map { CGFloat($0) / 100 }
        let inkEnabled = tool == .pen || tool == .highlight || tool == .shape || tool == .eraser
        let ocrLines: [RecognizedLine] = tool == .textActions ? recognizedLines ?? [] : []
        return SnipCanvas(
            document: document,
            inkColor: drawingColor,
            inkWidth: drawingWidth,
            zoom: zoom,
            allowsDrawing: inkEnabled,
            activeShape: tool == .shape ? shape : nil,
            lineStyle: lineStyle,
            erasesAnnotations: tool == .eraser,
            cropSelection: tool == .crop || tool == .removeArea ? $cropBounds : nil,
            annotationSelection: tool == .move ? $selectedAnnotationID : nil,
            textDraft: tool == .text ? $textDraft : nil,
            onTextTap: textTap,
            recognizedLines: ocrLines,
            ocrSelection: tool == .textActions ? $selectedOCRIndices : nil,
            onZoomChanged: { scale in displayedZoom = max(1, Int((scale * 100).rounded())) }
        )
    }

    private var captureBar: some View {
        GeometryReader { geometry in
            captureRow(compact: geometry.size.width < 620)
        }
        .frame(height: 54)
        .background(StudioColors.toolbar)
    }

    private func captureRow(compact: Bool) -> some View {
        HStack(spacing: 6) {
            Button { startNew() } label: {
                Label("New", systemImage: "plus")
                    .font(.system(size: 14, weight: .semibold))
                    .frame(width: 86, alignment: .leading)
            }
            .buttonStyle(StudioButtonStyle(primary: true))
            .disabled(fileActionsDisabled)
            .help(videoMode ? "New recording (⌘N)" : "New snip (⌘N)")

            HStack(spacing: 0) {
                iconButton("Screenshot mode", symbol: "camera", selected: !videoMode) { videoMode = false }
                iconButton("Video mode", symbol: "video", selected: videoMode) { chooseVideoMode() }
            }
            .background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 6))
            .disabled(fileActionsDisabled)

            if !videoMode {
                screenshotOptions(compact: compact)
            }

            Spacer(minLength: 2)
            iconButton("Open image (⌘O)", symbol: "folder", action: openImage)
                .keyboardShortcut("o", modifiers: .command)
                .disabled(fileActionsDisabled)
            iconButton("Paste image (⌘V)", symbol: "doc.on.clipboard", action: pasteImage)
                .keyboardShortcut("v", modifiers: .command)
                .disabled(fileActionsDisabled)
            iconButton("Save PNG (⌘S)", symbol: "square.and.arrow.down", action: saveImage)
                .keyboardShortcut("s", modifiers: .command)
                .disabled(fileActionsDisabled || !document.hasImage)
            iconButton("Copy image", symbol: "doc.on.doc") { exportImage() }
                .disabled(fileActionsDisabled || !document.hasImage)
            iconButton("Share image", symbol: "square.and.arrow.up", action: shareImage)
                .disabled(fileActionsDisabled || !document.hasImage)
            fileOptions
        }
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func screenshotOptions(compact: Bool) -> some View {
        HStack(spacing: 6) {
            Menu {
                Picker("Capture mode", selection: $captureMode) {
                    ForEach([CaptureMode.region, .window, .fullScreen, .allDisplays], id: \.self) { mode in
                        Label(mode.title, systemImage: mode.icon).tag(mode)
                    }
                }
                Divider()
                Button("Freeform…") { openWindow(id: "freeform") }
                Button("Scrolling capture…") { openWindow(id: "scrolling") }
            } label: {
                if compact {
                    Image(systemName: captureMode.icon).font(.system(size: 16))
                        .accessibilityLabel("Capture shape: \(captureMode.title)")
                } else {
                    Label(captureMode.title, systemImage: captureMode.icon).font(.system(size: 12))
                }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Capture shape: \(captureMode.title)")
            .disabled(fileActionsDisabled)

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
            .disabled(fileActionsDisabled)
        }
    }

    private var fileOptions: some View {
        Menu {
            Button("Open video…") { openVideo() }
            Menu("Open Recent") {
                if recentFiles.isEmpty { Text("No recent files") }
                ForEach(recentFiles.prefix(10), id: \.self) { url in
                    Button {
                        openRecent(url)
                    } label: {
                        Text(url.lastPathComponent)
                    }
                }
            }
            .disabled(fileActionsDisabled)
            Menu("Save image as…") {
                ForEach(StudioFileFormat.allCases, id: \.self) { format in
                    Button(format.title) { saveImage(as: format) }
                }
            }
            .disabled(fileActionsDisabled || !document.hasImage)
            Button("Save editable Mac snip…") { saveProject() }.disabled(fileActionsDisabled || !document.hasImage)
            Button("Print image…") { printImage() }.disabled(fileActionsDisabled || !document.hasImage)
            Button("Import external editor copy…") { importWorkingCopy() }.disabled(fileActionsDisabled || externalWorkingCopy == nil)
            Divider()
            SettingsLink { Text("Settings…") }
            Button("Check for Updates…") { checkForUpdates() }.disabled(updateTask != nil)
            Button("Snip Studio Help") { openHelp() }
        } label: {
            Image(systemName: "ellipsis").font(.system(size: 17))
                .accessibilityLabel("More file actions")
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("More file actions")
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
            toolButton(.text, label: "Add or edit text", symbol: "textformat")
            toolButton(.eraser, label: "Eraser", symbol: "eraser")
            toolButton(.crop, label: "Crop", symbol: "crop").disabled(busy)
            toolButton(.removeArea, label: "Remove area", symbol: "rectangle.dashed").disabled(busy)
            Divider().frame(height: 20).padding(.horizontal, 4)
            iconButton("Undo annotation", symbol: "arrow.uturn.backward", action: document.undo)
                .disabled(!document.canUndo)
            iconButton("Redo annotation", symbol: "arrow.uturn.forward", action: document.redo)
                .disabled(!document.canRedo)
            iconButton("Text actions", symbol: "wand.and.stars", selected: tool == .textActions, action: toggleTextActions)
                .disabled(busy)
            Divider().frame(height: 20).padding(.horizontal, 4)
            Button { editInApp() } label: { Label("Edit in app", systemImage: "pencil") }
                .buttonStyle(StudioButtonStyle())
                .disabled(busy)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .frame(height: 36)
        .background(StudioColors.toolbar)
        .disabled(textDraft != nil)
    }

    @ViewBuilder
    private var optionsBar: some View {
        switch tool {
        case .move:
            if let selectedAnnotationID, let annotation = document.annotation(withID: selectedAnnotationID) {
                selectionOptions(annotation)
            } else {
                Text("Select or drag an annotation.")
                    .font(.system(size: 12))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 12)
                    .frame(height: 40)
                    .background(StudioColors.options)
            }
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
            GeometryReader { geometry in ocrOptions(compact: geometry.size.width < 650) }
                .frame(height: 40)
                .background(StudioColors.options)
        case .eraser:
            HStack {
                Button("Erase all annotations") { document.eraseAllAnnotations() }
                    .buttonStyle(StudioButtonStyle())
                    .disabled(document.strokes.isEmpty)
                Text("Click or drag over an annotation to erase it.")
                    .font(.system(size: 12))
                Spacer(minLength: 0)
                iconButton("Hide options", symbol: "xmark") { showOptions = false }
            }
            .padding(.horizontal, 12)
            .frame(height: 40)
            .background(StudioColors.options)
        case .crop, .removeArea:
            HStack(spacing: 6) {
                Button {
                    if tool == .crop { applyCrop() } else { patchRaster([cropBounds], opaque: false) }
                } label: {
                    Label(tool == .crop ? "Apply crop" : "Remove selected area", systemImage: "checkmark")
                }
                .buttonStyle(StudioButtonStyle())
                .keyboardShortcut(.defaultAction)
                .disabled(cropBounds.isEmpty || (tool == .crop && cropBounds == fullImageBounds) || busy)
                Button { selectTool(.pen) } label: {
                    Label("Cancel", systemImage: "xmark")
                }
                .buttonStyle(StudioButtonStyle())
                .keyboardShortcut(.cancelAction)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 12)
            .frame(height: 40)
            .background(StudioColors.options)
        case .text:
            textOptions
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

    private func ocrOptions(compact: Bool) -> some View {
        HStack(spacing: 5) {
            ocrButton("Copy all text", symbol: "doc.on.doc", compact: compact) { copyText(recognizedText) }
                .disabled(recognizedText.isEmpty || exportTask != nil)
            ocrButton("Copy selection", symbol: "selection.pin.in.out", compact: compact) {
                copyText(selectedOCRLines.map(\.text).joined(separator: "\n"))
            }
            .disabled(selectedOCRLines.isEmpty || exportTask != nil)
            ocrButton("Copy as table", symbol: "tablecells", compact: compact, action: copyRecognizedTable)
                .disabled(recognizedText.isEmpty || exportTask != nil)
            Menu {
                Button("Email addresses and phone numbers") { redactContacts() }
                Button("Email addresses") { redactContacts(kind: .email) }
                Button("Phone numbers") { redactContacts(kind: .phone) }
            } label: {
                Label("Quick redact", systemImage: "rectangle.and.pencil.and.ellipsis")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .disabled(recognizedText.isEmpty || busy)
            iconButton("Review text", symbol: "text.alignleft") { reviewText = true }
                .disabled(recognizedText.isEmpty)
            iconButton("Delete selected text pixels", symbol: "trash", action: deleteOCRSelection)
                .keyboardShortcut(.delete, modifiers: [])
                .disabled(selectedOCRLines.isEmpty || busy)
            Spacer(minLength: 0)
            iconButton("Close text actions", symbol: "xmark") { selectTool(.pen) }
        }
        .padding(.horizontal, 12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private func ocrButton(_ title: String, symbol: String, compact: Bool, action: @escaping () -> Void) -> some View {
        if compact {
            iconButton(title, symbol: symbol, action: action)
        } else {
            Button(action: action) { Label(title, systemImage: symbol) }.buttonStyle(StudioButtonStyle())
        }
    }

    private var textOptions: some View {
        let note = textDraft?.note ?? textStyle
        return HStack(spacing: 6) {
            ColorPicker("Text color", selection: Binding(get: { textDraft?.color ?? textColor }, set: { color in
                if textDraft == nil { textColor = color } else { textDraft?.color = color }
            }))
            .labelsHidden()
            .help("Text color")
            Menu {
                ForEach(["Helvetica", "Menlo-Regular", "ArialMT", "TimesNewRomanPSMT"], id: \.self) { name in
                    Button(NSFont(name: name, size: 12)?.displayName ?? name) { changeTextStyle { $0.fontName = name } }
                }
            } label: {
                Text(NSFont(name: note.fontName, size: 12)?.displayName ?? note.fontName).lineLimit(1)
            }
            .frame(width: 119)
            .menuStyle(.borderlessButton)
            .help("Text font")
            Stepper(value: Binding(get: { note.pixelSize }, set: { size in changeTextStyle { $0.pixelSize = size } }),
                    in: StudioTextAnnotation.pixelSizeRange) {
                Text("\(Int(note.pixelSize)) px").monospacedDigit()
            }
            .fixedSize()
            .accessibilityLabel("Text size")
            iconButton("Bold", symbol: "bold", selected: note.bold) { changeTextStyle { $0.bold.toggle() } }
            iconButton("Italic", symbol: "italic", selected: note.italic) { changeTextStyle { $0.italic.toggle() } }
            iconButton("Underline", symbol: "underline", selected: note.underline) { changeTextStyle { $0.underline.toggle() } }
            Spacer(minLength: 0)
            if textDraft != nil {
                iconButton("Apply text", symbol: "checkmark", action: applyText)
                    .disabled(note.text.isEmpty)
                iconButton("Cancel text", symbol: "xmark", action: cancelText)
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 40)
        .background(StudioColors.options)
    }

    private func selectionOptions(_ annotation: InkStroke) -> some View {
        HStack(spacing: 7) {
            iconButton("Rotate left", symbol: "rotate.left") {
                document.rotateAnnotation(id: annotation.id, clockwise: false)
            }
            iconButton("Rotate right", symbol: "rotate.right") {
                document.rotateAnnotation(id: annotation.id, clockwise: true)
            }
            Divider().frame(height: 20)
            ColorPicker("Annotation color", selection: Binding(get: { Color(cgColor: annotation.color) }, set: { color in
                guard let cgColor = NSColor(color).usingColorSpace(.sRGB)?.cgColor else { return }
                document.updateAnnotation(id: annotation.id, color: cgColor)
            }), supportsOpacity: true)
            .labelsHidden()
            .help("Annotation color and opacity")
            if annotation.textNote == nil {
                Stepper(value: Binding(get: { annotation.width }, set: { width in
                    document.updateAnnotation(id: annotation.id, width: width)
                }), in: 1...30, step: 1) {
                    Text("\(Int(annotation.width)) px").monospacedDigit()
                }
                .fixedSize()
                .accessibilityLabel("Annotation width")
            }
            if annotation.shape != nil {
                Picker("Line style", selection: Binding(get: { annotation.lineStyle }, set: { style in
                    document.updateAnnotation(id: annotation.id, lineStyle: style)
                })) {
                    ForEach(SnipLineStyle.allCases, id: \.self) { style in
                        Text(style.rawValue.capitalized).tag(style)
                    }
                }
                .labelsHidden()
                .fixedSize()
            }
            Spacer(minLength: 0)
            iconButton("Delete annotation", symbol: "trash") {
                document.deleteAnnotation(id: annotation.id)
            }
            .keyboardShortcut(.delete, modifiers: [])
            iconButton("Clear selection", symbol: "xmark") { selectedAnnotationID = nil }
        }
        .padding(.horizontal, 12)
        .frame(height: 40)
        .background(StudioColors.options)
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
        if value != .move { selectedAnnotationID = nil }
        showOptions = true
        switch value {
        case .move: status = "Scroll to move around a zoomed screenshot."
        case .pen: status = "Draw on the screenshot."
        case .highlight: status = "Drag to highlight an area."
        case .shape: status = "Drag to draw a \(shape.rawValue)."
        case .text: status = "Click the screenshot to add or edit text."
        case .eraser: status = "Erase annotations. Use Undo to restore them."
        case .crop:
            cropBounds = fullImageBounds
            zoomPercent = nil
            status = "Drag the crop edges, then select Apply crop."
        case .removeArea:
            let full = fullImageBounds
            cropBounds = full.insetBy(dx: full.width / 4, dy: full.height / 4).integral.intersection(full)
            zoomPercent = nil
            status = "Adjust the rectangle to choose which source pixels to remove."
        case .textActions: break
        }
    }

    private func beginText(at origin: CGPoint) {
        if let annotation = document.annotation(at: origin, tolerance: 3), let note = annotation.textNote {
            textDraft = StudioTextDraft(annotationID: annotation.id, origin: annotation.origin, note: note,
                                        color: Color(cgColor: annotation.color))
        } else {
            textDraft = StudioTextDraft(annotationID: nil, origin: origin, note: textStyle, color: textColor)
        }
        document.hasPendingTextChanges = true
        status = "Use the checkmark to apply text or X to cancel."
    }

    private func changeTextStyle(_ change: (inout StudioTextAnnotation) -> Void) {
        if var draft = textDraft {
            change(&draft.note)
            textDraft = draft
        } else {
            change(&textStyle)
        }
    }

    private func applyText() {
        guard let draft = textDraft, let color = NSColor(draft.color).usingColorSpace(.sRGB)?.cgColor else { return }
        do {
            try document.applyText(draft.note, at: draft.origin, color: color, updating: draft.annotationID)
            textStyle = draft.note
            textStyle.text = ""
            textColor = draft.color
            cancelText()
            status = "Text applied. Use the Text tool to edit it again."
        } catch { report(error) }
    }

    private func cancelText() {
        textDraft = nil
        document.hasPendingTextChanges = false
        status = "Click the screenshot to add or edit text."
    }

    private var fullImageBounds: CGRect {
        guard let image = document.image else { return .null }
        return CGRect(x: 0, y: 0, width: image.width, height: image.height)
    }

    private func applyCrop() {
        do {
            let cropped = try document.crop(to: cropBounds)
            selectTool(.pen)
            if cropped { status = "Screenshot cropped. Use Undo to restore it." }
        } catch { report(error) }
    }

    private func stepZoom(increasing: Bool) {
        let current = zoomPercent ?? displayedZoom
        zoomPercent = increasing
            ? Self.zoomPresets.first(where: { $0 > current }) ?? Self.zoomPresets.last
            : Self.zoomPresets.last(where: { $0 < current }) ?? Self.zoomPresets.first
    }

    private func capture() {
        guard !busy else { return }
        guard !applicationDelegate.captureInProgress else {
            status = "Finish the capture in the other editor first."
            return
        }
        applicationDelegate.captureInProgress = true
        let mode = captureMode
        let delay = captureDelay
        captureTask = Task { @MainActor in
            defer {
                applicationDelegate.captureInProgress = false
                captureTask = nil
            }
            do {
                for seconds in (0..<delay).reversed() {
                    status = "Capture starts in \(seconds + 1)s."
                    try await Task.sleep(for: .seconds(1))
                }
                status = mode == .region || mode == .window ? "Select what to capture." : "Capturing the screen…"
                if let image = try await CaptureService.capture(mode) {
                    if document.hasImage {
                        let newSession = applicationDelegate.storePendingSnip(image)
                        openWindow(id: "studio", value: newSession)
                        status = "Opened the capture in a new editor."
                    } else {
                        try finishCapture(with: image)
                    }
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

    private func chooseVideoMode() {
        if #available(macOS 15.0, *) {
            videoMode = true
            if !document.hasImage { status = "Select New to choose a recording source." }
        } else {
            errorMessage = "Video recording requires macOS 15 or later. Existing videos can still open for review."
        }
    }

    private func startNew() {
        guard !fileActionsDisabled else { return }
        if videoMode {
            openWindow(id: "recording")
        } else {
            capture()
        }
    }

    private func finishCapture(with image: NSImage) throws {
        try load(image, alreadySaved: false)
        if saveOriginals { saveOriginalCapture() }
    }

    private func openImage() {
        guard !busy, confirmDiscard(document, title: "Replace the current screenshot?") else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image, Self.projectType]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        openImageOrProject(url)
    }

    private func openImageOrProject(_ url: URL) {
        if url.pathExtension.caseInsensitiveCompare(StudioProjectFile.filenameExtension) == .orderedSame {
            openProject(url)
        } else {
            openImage(at: url)
        }
    }

    private func openProject(_ url: URL) {
        status = "Opening editable Mac snip…"
        exportTask = Task { @MainActor in
            defer { exportTask = nil }
            do {
                let contents = try await StudioProjectFile.load(from: url)
                try await document.openProject(contents)
                presentLoadedImage(alreadySaved: true)
                recordRecent(url)
            } catch is CancellationError {
                return
            } catch { report(error) }
        }
    }

    private func openImage(at url: URL, retainsWorkingCopy: Bool = false) {
        guard let image = NSImage(contentsOf: url) else {
            report(StudioError.imageUnreadable)
            return
        }
        do {
            try load(image, alreadySaved: true)
            if retainsWorkingCopy { externalWorkingCopy = url }
            recordRecent(url)
        } catch { report(error) }
    }

    private func pasteImage() {
        guard !busy, confirmDiscard(document, title: "Replace the current screenshot?") else { return }
        guard let clipboardImage = NSImage(pasteboard: .general) else {
            report(StudioError.noClipboardImage)
            return
        }
        do { try load(clipboardImage, alreadySaved: false) } catch { report(error) }
    }

    private func openVideo() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.movie]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        openRecentVideo(url)
    }

    private func openRecentVideo(_ url: URL) {
        recordRecent(url)
        openWindow(id: "video-review", value: url)
    }

    private func openRecent(_ url: URL) {
        guard !busy else { return }
        if UTType(filenameExtension: url.pathExtension)?.conforms(to: .movie) == true {
            openRecentVideo(url)
        } else if confirmDiscard(document, title: "Replace the current screenshot?") {
            openImageOrProject(url)
        }
    }

    private func recordRecent(_ url: URL) {
        let recent = NSDocumentController.shared
        recent.noteNewRecentDocumentURL(url)
        recentFiles = recent.recentDocumentURLs
    }

    private func load(_ image: NSImage, alreadySaved: Bool) throws {
        try document.replaceImage(image, alreadySaved: alreadySaved)
        presentLoadedImage(alreadySaved: alreadySaved)
    }

    private func presentLoadedImage(alreadySaved: Bool) {
        openedImageRevision = alreadySaved ? document.contentRevision : nil
        recognizedLines = nil
        externalWorkingCopy = nil
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
        let revision = document.rasterRevision
        Task { @MainActor in
            defer { recognitionInProgress = false }
            do {
                let lines = try await TextRecognition.recognizeLines(image)
                guard document.rasterRevision == revision else { return }
                recognizedLines = lines
                if tool == .textActions { status = lines.isEmpty ? "No text found." : "Text ready to copy." }
            } catch {
                report(error)
            }
        }
    }

    private func copyText(_ text: String) {
        automaticClipboardTask?.cancel()
        applicationDelegate.clipboardGeneration = UUID()
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
        applicationDelegate.clipboardGeneration = UUID()
        do {
            let result = try OCRTableClipboard.write(selectedOCRIndices.isEmpty ? recognizedLines : selectedOCRLines)
            status = "Copied \(result.rowCount) rows × \(result.columnCount) columns."
        } catch { report(error) }
    }

    private func deleteOCRSelection() {
        guard let image = document.image else { return }
        let frame = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let regions = selectedOCRLines.map { OCRRegionOverlay.displayBounds(for: $0, in: frame).insetBy(dx: -2, dy: -2) }
        patchRaster(regions, opaque: false)
    }

    private func redactContacts(kind: OCRContact.Kind? = nil) {
        reviewedContacts = (recognizedLines?.flatMap(\.contacts) ?? []).filter { kind == nil || $0.kind == kind }
        guard !reviewedContacts.isEmpty else {
            status = "No matching email addresses or phone numbers were recognized."
            return
        }
        reviewRedactions = true
    }

    private func patchRaster(_ regions: [CGRect], opaque: Bool) {
        guard !busy, !regions.isEmpty else { return }
        status = opaque ? "Applying redactions…" : "Removing selected pixels…"
        exportTask = Task { @MainActor in
            defer { exportTask = nil }
            do {
                if try await document.patchRaster(regions, opaque: opaque) {
                    selectTool(.pen)
                    status = opaque ? "Redactions applied. Review the screenshot before sharing." : "Area removed. Use Undo to restore it."
                }
            } catch is CancellationError {
                return
            } catch { report(error) }
        }
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
        let generation = UUID()
        applicationDelegate.clipboardGeneration = generation
        let expectedChangeCount = NSPasteboard.general.changeCount
        automaticClipboardTask = Task { @MainActor in
            do {
                try await Task.sleep(for: .milliseconds(160))
                let png = try await snapshot.pngData()
                try Task.checkCancellation()
                guard applicationDelegate.clipboardGeneration == generation, document.contentRevision == revision,
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

    private func saveProject() {
        guard !fileActionsDisabled, document.hasImage else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [Self.projectType]
        panel.nameFieldStringValue = "Snip.\(StudioProjectFile.filenameExtension)"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let snapshot = try document.pngSnapshot()
            status = "Saving editable Mac snip…"
            exportTask = Task { @MainActor in
                defer { exportTask = nil }
                do {
                    try await snapshot.writeProject(to: url)
                    document.markSaved(snapshot)
                    recordRecent(url)
                    status = document.hasUnsavedChanges ? "Saved an earlier revision. Newer edits are unsaved." : "Editable Mac snip saved."
                } catch is CancellationError {
                    status = "Save canceled."
                } catch { report(error) }
            }
        } catch { report(error) }
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

    private func shareImage() {
        guard !busy, let origin = NSApp.keyWindow?.contentView else { return }
        do {
            let snapshot = try document.pngSnapshot()
            status = "Preparing the screenshot to share…"
            exportTask = Task { @MainActor [weak origin] in
                defer { exportTask = nil }
                do {
                    let png = try await snapshot.pngData()
                    try Task.checkCancellation()
                    guard let origin, let image = NSImage(data: png) else { throw StudioError.imageUnreadable }
                    let y = origin.isFlipped ? origin.bounds.minY + 35 : origin.bounds.maxY - 35
                    let anchor = NSRect(x: origin.bounds.maxX - 60, y: y, width: 25, height: 20)
                    NSSharingServicePicker(items: [image]).show(relativeTo: anchor, of: origin, preferredEdge: .minY)
                    status = "Choose a sharing destination."
                } catch is CancellationError {
                    return
                } catch { report(error) }
            }
        } catch { report(error) }
    }

    private func editInApp() {
        guard !busy else { return }
        do {
            let snapshot = try document.pngSnapshot()
            status = "Preparing an editable working copy…"
            exportTask = Task { @MainActor in
                defer { exportTask = nil }
                do {
                    let copy = try await StudioOriginalCapture.save(snapshot, workingCopy: true)
                    try Task.checkCancellation()
                    externalWorkingCopy = copy
                    guard NSWorkspace.shared.open(copy) else { throw StudioError.externalEditorUnavailable }
                    status = "Use Import external editor copy after saving your changes."
                } catch is CancellationError {
                    return
                } catch { report(error) }
            }
        } catch { report(error) }
    }

    private func importWorkingCopy() {
        guard !busy, let externalWorkingCopy,
              confirmDiscard(document, title: "Replace the current screenshot?") else { return }
        openImage(at: externalWorkingCopy, retainsWorkingCopy: true)
    }

    private func exportImage(to url: URL? = nil, format: StudioFileFormat = .png) {
        guard !busy, document.hasImage else { return }
        if url == nil {
            automaticClipboardTask?.cancel()
            applicationDelegate.clipboardGeneration = UUID()
        }
        let clipboardGeneration = applicationDelegate.clipboardGeneration
        do {
            let snapshot = try document.pngSnapshot()
            status = url == nil ? "Preparing image for the clipboard…" : "Saving \(format.title)…"
            exportTask = Task { @MainActor in
                defer { exportTask = nil }
                do {
                    if let url {
                        try await StudioFileExport.write(snapshot, as: format, to: url)
                        if format != .pdf { recordRecent(url) }
                        document.markSaved(snapshot)
                        status = "Saved \(url.lastPathComponent)."
                        if document.hasUnsavedChanges {
                            status += " Newer edits are unsaved."
                        }
                    } else {
                        let png = try await snapshot.pngData()
                        try Task.checkCancellation()
                        guard applicationDelegate.clipboardGeneration == clipboardGeneration else {
                            status = "A newer clipboard action took precedence."
                            return
                        }
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

    private func checkForUpdates() {
        guard updateTask == nil else { return }
        status = "Checking for Snip Studio updates…"
        updateTask = Task { @MainActor in
            defer { updateTask = nil }
            do {
                let result = try await MacUpdateChecker.check()
                try Task.checkCancellation()
                updateURL = nil
                switch result {
                case .noMacRelease: updateMessage = "There is no published Mac release on the Snip Studio repository yet."
                case .upToDate: updateMessage = "This version of Snip Studio is up to date."
                case let .available(version, releasePage):
                    updateURL = releasePage
                    updateMessage = "Snip Studio \(version) is available for this Mac."
                }
                status = "Update check complete."
            } catch is CancellationError {
                return
            } catch { report(error) }
        }
    }

    private func openHelp() {
        guard let url = URL(string: "https://github.com/jaydawg473/Snip-Studio/tree/feature/macos-trial/src/SnipStudio.Mac") else { return }
        NSWorkspace.shared.open(url)
    }
}

private enum StudioError: LocalizedError {
    case imageUnreadable, noClipboardImage, copyFailed, externalEditorUnavailable

    var errorDescription: String? {
        switch self {
        case .imageUnreadable: "Snip Studio could not read that image."
        case .noClipboardImage: "The clipboard doesn't contain an image."
        case .copyFailed: "The clipboard could not accept the result. Try copying again."
        case .externalEditorUnavailable: "macOS could not open the working copy in an image editor."
        }
    }
}
