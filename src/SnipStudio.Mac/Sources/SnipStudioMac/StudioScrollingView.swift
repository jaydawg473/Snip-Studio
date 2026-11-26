import AppKit
import SwiftUI

@MainActor
struct StudioScrollingView: View {
    let applicationDelegate: StudioApplicationDelegate

    @Environment(\.openWindow) private var openWindow
    @State private var displays: [StudioScrollDisplay] = []
    @State private var displayID: CGDirectDisplayID?
    @State private var selectedArea: CGRect?
    @State private var session = StudioScrollSession()
    @State private var acceptedFrames = 0
    @State private var outputHeight = 0
    @State private var busy = false
    @State private var showsSelection = false
    @State private var confirmReset = false
    @State private var review: StitchReview?
    @State private var showStitchJoins = false
    @State private var status = "Select the part of a display that you want to capture."

    private var display: StudioScrollDisplay? { displays.first { $0.id == displayID } }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                Picker("Display", selection: $displayID) {
                    if displays.isEmpty { Text("No display available").tag(nil as CGDirectDisplayID?) }
                    ForEach(displays) { display in Text(display.title).tag(Optional(display.id)) }
                }
                .disabled(busy || acceptedFrames > 0)
                Button("Select area…") { showsSelection = true }
                    .disabled(display == nil || busy || acceptedFrames > 0)
            }
            HStack(spacing: 8) {
                Button(acceptedFrames == 0 ? "Capture first view" : "Capture next view") { capture() }
                    .buttonStyle(StudioButtonStyle(primary: true))
                    .disabled(display == nil || selectedArea == nil || busy)
                Button("Review capture…") { finish() }
                    .disabled(acceptedFrames == 0 || busy)
                Spacer()
                Button("Restart…") { confirmReset = true }
                    .disabled(acceptedFrames == 0 || busy)
            }
            if acceptedFrames > 0 {
                Text("\(acceptedFrames) captured views · \(outputHeight) px high")
                    .monospacedDigit().font(.caption).foregroundStyle(.secondary)
            }
            HStack(alignment: .top, spacing: 6) {
                if busy { ProgressView().controlSize(.small).accessibilityLabel("Working") }
                Text(status).font(.callout).fixedSize(horizontal: false, vertical: true)
            }
            .frame(minHeight: 38, alignment: .topLeading)
            Spacer(minLength: 0)
        }
        .padding(16)
        .frame(minWidth: 480, minHeight: 190)
        .background(StudioColors.toolbar)
        .task {
            do {
                displays = try await StudioScrollCaptureSource.displays()
                displayID = displays.first?.id
            } catch is CancellationError {
                return
            } catch {
                status = error.localizedDescription
            }
        }
        .onChange(of: displayID) { _, _ in selectedArea = nil }
        .confirmationDialog("Discard the current scrolling capture?", isPresented: $confirmReset) {
            Button("Discard", role: .destructive) { reset() }
            Button("Cancel", role: .cancel) {}
        }
        .sheet(isPresented: $showsSelection) {
            if let display { StudioScrollAreaPicker(display: display, selection: $selectedArea) }
        }
        .sheet(item: $review) { result in
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text("\(result.image.width) × \(result.image.height) px · \(acceptedFrames) captured views")
                        .font(.callout).foregroundStyle(.secondary)
                    Spacer()
                    Toggle("Show stitch joins", isOn: $showStitchJoins)
                        .disabled(result.joins.isEmpty)
                }
                ScrollView {
                    Image(decorative: result.image, scale: 1).resizable().scaledToFit()
                        .accessibilityLabel("Assembled scrolling capture")
                        .overlay {
                            if showStitchJoins {
                                Canvas { context, size in
                                    var guides = Path()
                                    for row in result.joins {
                                        let y = CGFloat(row) * size.height / CGFloat(result.image.height)
                                        guides.move(to: CGPoint(x: 0, y: y))
                                        guides.addLine(to: CGPoint(x: size.width, y: y))
                                    }
                                    context.stroke(guides, with: .color(.white), lineWidth: 2)
                                    context.stroke(guides, with: .color(StudioColors.primary),
                                                   style: StrokeStyle(lineWidth: 1, dash: [5, 4]))
                                }
                                .allowsHitTesting(false)
                                .accessibilityHidden(true)
                            }
                        }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(StudioColors.canvas)
                HStack {
                    Spacer()
                    Button("Keep capturing") { review = nil }.keyboardShortcut(.cancelAction)
                    Button("Use capture") { useCapture(result.image) }.keyboardShortcut(.defaultAction)
                }
            }
            .padding(16)
            .frame(width: 620, height: 530)
        }
    }

    private func capture() {
        guard let display, let selectedArea, !busy else { return }
        busy = true
        status = "Capturing this view…"
        Task { @MainActor in
            defer { busy = false }
            do {
                let frame = try await StudioScrollCaptureSource.snapshot(display: display, region: selectedArea)
                let result = try await session.append(frame)
                acceptedFrames = result.count
                outputHeight = result.height
                switch result.outcome {
                case .firstFrame, .appended:
                    status = "Scroll down within the source window, keep part of the last view visible, then capture the next view."
                case .repeated:
                    status = "This view matches the last capture. Scroll down before capturing again."
                case .ambiguous, .noMatch:
                    status = "This view did not align. Scroll back to restore overlap, or review the accepted capture."
                case .sizeLimit:
                    status = "The capture has reached the image size limit. Review the accepted capture."
                }
            } catch {
                status = error.localizedDescription
            }
        }
    }

    private func finish() {
        guard !busy else { return }
        busy = true
        Task { @MainActor in
            defer { busy = false }
            do {
                if let result = try await session.makeReview() {
                    showStitchJoins = false
                    review = StitchReview(image: result.image, joins: result.joins)
                }
            } catch { status = error.localizedDescription }
        }
    }

    private func useCapture(_ image: CGImage) {
        let raster = NSImage(cgImage: image, size: CGSize(width: image.width, height: image.height))
        let id = applicationDelegate.storePendingSnip(raster)
        openWindow(id: "studio", value: id)
        reset()
        status = "Opened the scrolling capture in a new editor."
    }

    private func reset() {
        review = nil
        session = StudioScrollSession()
        acceptedFrames = 0
        outputHeight = 0
        status = "Select an area, or capture the first view again."
    }

    private struct StitchReview: Identifiable {
        let id = UUID()
        let image: CGImage
        let joins: [Int]
    }
}

private actor StudioScrollSession {
    private var stitcher = StudioScrollStitcher()

    func append(_ frame: CGImage) throws -> (outcome: StudioScrollStitcher.Outcome, count: Int, height: Int) {
        let result = try stitcher.add(frame)
        return (result, stitcher.frameCount, stitcher.outputHeight)
    }

    func makeReview() throws -> (image: CGImage, joins: [Int])? {
        guard let image = try stitcher.makeImage() else { return nil }
        return (image, stitcher.joinRows)
    }
}

@MainActor
private struct StudioScrollAreaPicker: View {
    let display: StudioScrollDisplay
    @Binding var selection: CGRect?

    @Environment(\.dismiss) private var dismiss
    @State private var bounds = CGRect.null
    @State private var preview: CGImage?
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Select scrolling area").font(.headline)
            GeometryReader { geometry in
                if let preview {
                    let scale = min(geometry.size.width / display.size.width, geometry.size.height / display.size.height)
                    let size = CGSize(width: display.size.width * scale, height: display.size.height * scale)
                    let frame = CGRect(x: (geometry.size.width - size.width) / 2,
                                       y: (geometry.size.height - size.height) / 2, width: size.width, height: size.height)
                    ZStack(alignment: .topLeading) {
                        Image(decorative: preview, scale: 1).resizable()
                            .frame(width: frame.width, height: frame.height)
                            .offset(x: frame.minX, y: frame.minY)
                            .accessibilityHidden(true)
                        StudioCropOverlay(bounds: $bounds, imageFrame: frame, pixelSize: display.size)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let error {
                    Text(error).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    ProgressView("Preparing the display preview…").frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .background(StudioColors.canvas)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Use area") { selection = bounds; dismiss() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(preview == nil || bounds.isEmpty)
            }
        }
        .padding(16)
        .frame(width: 620, height: 440)
        .task {
            bounds = selection ?? CGRect(origin: .zero, size: display.size)
            do { preview = try await StudioScrollCaptureSource.snapshot(display: display, preview: true) }
            catch is CancellationError { return }
            catch { self.error = error.localizedDescription }
        }
    }
}
