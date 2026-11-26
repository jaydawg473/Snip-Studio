import AppKit
import SwiftUI

@MainActor
struct StudioFreeformCaptureView: View {
    let applicationDelegate: StudioApplicationDelegate

    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var displays: [StudioScrollDisplay] = []
    @State private var displayID: CGDirectDisplayID?
    @State private var refreshID = UUID()
    @State private var snapshotID = UUID()
    @State private var frozenImage: CGImage?
    @State private var selection: [CGPoint] = []
    @State private var trace: [CGPoint] = []
    @GestureState private var tracing = false
    @State private var loadsImage = true
    @State private var appliesSelection = false
    @State private var applyTask: Task<Void, Never>?
    @State private var status = "Loading displays…"

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Picker("Display", selection: $displayID) {
                    if displays.isEmpty { Text("No display available").tag(nil as CGDirectDisplayID?) }
                    ForEach(displays) { display in Text(display.title).tag(Optional(display.id)) }
                }
                .fixedSize(horizontal: true, vertical: false)
                .disabled(appliesSelection || displays.isEmpty)
                Button("Refresh preview") { refreshID = UUID() }.disabled(appliesSelection)
                Spacer(minLength: 0)
                Button("Capture entire display") {
                    if let frozenImage { openCapture(frozenImage) }
                }
                .disabled(frozenImage == nil || loadsImage || appliesSelection)
            }

            GeometryReader { viewport in
                if let frozenImage {
                    let layout = FreeformPreview(image: frozenImage, viewport: viewport.size)
                    preview(frozenImage, layout: layout)
                } else {
                    ProgressView()
                        .opacity(loadsImage ? 1 : 0)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .accessibilityLabel("Preparing the display preview")
                }
            }
            .background(StudioColors.canvas)
            .clipped()

            HStack(spacing: 8) {
                Text(status).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                Button("Clear") { selection = []; trace = [] }
                    .disabled(selection.isEmpty || appliesSelection)
                Button("Cancel") { dismissWindow(id: "freeform") }.keyboardShortcut(.cancelAction)
                Button("Use capture") { apply() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(selection.count < 3 || tracing || loadsImage || appliesSelection)
            }
        }
        .padding(14)
        .frame(minWidth: 520, minHeight: 360)
        .background(StudioColors.toolbar)
        .task(id: refreshID) { await refreshDisplays() }
        .task(id: displayID) { if displayID != nil { await freezeDisplay() } }
        .onChange(of: tracing) { _, active in
            guard !active else { return }
            Task { @MainActor in
                if !tracing { trace = [] }
            }
        }
        .onDisappear {
            applyTask?.cancel()
            NSCursor.arrow.set()
        }
    }

    private func preview(_ image: CGImage, layout: FreeformPreview) -> some View {
        let points = trace.isEmpty ? selection : trace
        return Canvas { context, _ in
            context.draw(Image(decorative: image, scale: 1), in: layout.frame)
            guard let start = points.first else { return }
            var loop = Path()
            loop.move(to: layout.viewportPoint(start))
            for point in points.dropFirst() { loop.addLine(to: layout.viewportPoint(point)) }
            if points.count >= 3 {
                loop.closeSubpath()
                var outside = Path(layout.frame)
                outside.addPath(loop)
                context.fill(outside, with: .color(.black.opacity(0.5)), style: FillStyle(eoFill: true))
            }
            context.stroke(loop, with: .color(.black.opacity(0.9)), lineWidth: 3)
            context.stroke(loop, with: .color(.white), lineWidth: 1)
        }
        .contentShape(Rectangle())
        .accessibilityLabel("Frozen display preview")
        .accessibilityHint("Drag around the area to capture.")
        .onHover { hovering in (hovering && !appliesSelection ? NSCursor.crosshair : .arrow).set() }
        .gesture(traceGesture(in: layout), including: appliesSelection ? .none : .all)
    }

    private func traceGesture(in layout: FreeformPreview) -> some Gesture {
        DragGesture(minimumDistance: 2)
            .updating($tracing) { _, active, _ in active = true }
            .onChanged { gesture in
                guard layout.frame.contains(gesture.startLocation) else { return }
                if trace.isEmpty { trace = [layout.pixelPoint(gesture.startLocation)] }
                let point = layout.pixelPoint(gesture.location)
                if let last = trace.last, hypot(point.x - last.x, point.y - last.y) * layout.scale < 1 { return }
                appendTrace(point)
            }
            .onEnded { gesture in
                defer { trace = [] }
                guard layout.frame.contains(gesture.startLocation), !trace.isEmpty else { return }
                let finalPoint = layout.pixelPoint(gesture.location)
                if finalPoint != trace.last { appendTrace(finalPoint) }
                guard trace.count >= 3 else { return }
                selection = trace
                status = "Review the selection, then choose Use capture."
            }
    }

    private func appendTrace(_ point: CGPoint) {
        if trace.count < StudioFreeformMask.maximumPoints {
            trace.append(point)
        } else {
            trace[trace.count - 1] = point
        }
    }

    private func refreshDisplays() async {
        do {
            let available = try await StudioScrollCaptureSource.displays()
            try Task.checkCancellation()
            displays = available
            if let displayID, available.contains(where: { $0.id == displayID }) {
                await freezeDisplay()
            } else {
                displayID = available.first?.id
            }
        } catch is CancellationError {
            return
        } catch {
            loadsImage = false
            status = error.localizedDescription
        }
    }

    private func freezeDisplay() async {
        guard let display = displays.first(where: { $0.id == displayID }) else { return }
        let request = UUID()
        snapshotID = request
        loadsImage = true
        frozenImage = nil
        selection = []
        trace = []
        status = "Preparing the display preview…"
        do {
            let image = try await StudioScrollCaptureSource.snapshot(display: display)
            try Task.checkCancellation()
            guard display.id == displayID, request == snapshotID else { return }
            frozenImage = image
            loadsImage = false
            status = "Drag around the area you want to capture."
        } catch is CancellationError {
            return
        } catch {
            guard display.id == displayID, request == snapshotID else { return }
            loadsImage = false
            status = error.localizedDescription
        }
    }

    private func apply() {
        guard let frozenImage, selection.count >= 3, !appliesSelection else { return }
        let polygon = selection
        appliesSelection = true
        status = "Preparing the freeform capture…"
        applyTask = Task { @MainActor in
            defer { appliesSelection = false; applyTask = nil }
            do {
                let result = try await Task.detached(priority: .userInitiated) {
                    try StudioFreeformMask.extract(frozenImage, polygon: polygon)
                }.value
                try Task.checkCancellation()
                openCapture(result)
            } catch is CancellationError {
                return
            } catch {
                status = error.localizedDescription
            }
        }
    }

    private func openCapture(_ result: CGImage) {
        let output = NSImage(cgImage: result, size: CGSize(width: result.width, height: result.height))
        let id = applicationDelegate.storePendingSnip(output)
        openWindow(id: "studio", value: id)
        dismissWindow(id: "freeform")
    }
}

private struct FreeformPreview {
    let frame: CGRect
    let scale: CGFloat
    private let dimensions: CGSize

    init(image: CGImage, viewport: CGSize) {
        dimensions = CGSize(width: image.width, height: image.height)
        scale = max(0.000001, min(viewport.width / dimensions.width, viewport.height / dimensions.height))
        let width = dimensions.width * scale
        let height = dimensions.height * scale
        frame = CGRect(x: (viewport.width - width) / 2, y: (viewport.height - height) / 2,
                       width: width, height: height)
    }

    func pixelPoint(_ point: CGPoint) -> CGPoint {
        CGPoint(x: min(dimensions.width, max(0, (point.x - frame.minX) / scale)),
                y: min(dimensions.height, max(0, (point.y - frame.minY) / scale)))
    }

    func viewportPoint(_ point: CGPoint) -> CGPoint {
        CGPoint(x: frame.minX + point.x * scale, y: frame.minY + point.y * scale)
    }
}

enum StudioFreeformMask {
    static let maximumPoints = 8_192

    static func extract(_ image: CGImage, polygon: [CGPoint]) throws -> CGImage {
        guard (3...maximumPoints).contains(polygon.count),
              polygon.allSatisfy({ $0.x.isFinite && $0.y.isFinite }) else { throw Failure.invalidOutline }

        let width = CGFloat(image.width)
        let height = CGFloat(image.height)
        let points = polygon.map { CGPoint(x: min(width, max(0, $0.x)), y: min(height, max(0, $0.y))) }
        let fullOutline = CGMutablePath()
        fullOutline.addLines(between: points)
        fullOutline.closeSubpath()
        let pixels = fullOutline.boundingBoxOfPath.integral.intersection(CGRect(x: 0, y: 0, width: width, height: height))
        guard !pixels.isEmpty, enclosesArea(points), let raster = image.cropping(to: pixels) else {
            throw Failure.invalidOutline
        }

        let colorSpace = raster.colorSpace?.model == .rgb ? raster.colorSpace : CGColorSpace(name: CGColorSpace.sRGB)
        guard let colorSpace, let context = CGContext(
            data: nil, width: raster.width, height: raster.height, bitsPerComponent: 8,
            bytesPerRow: 0, space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { throw Failure.renderFailed }
        let outputOutline = CGMutablePath()
        // Cocoa image math uses a top-left selection; the bitmap drawing context is bottom-left.
        outputOutline.addLines(between: points.map { CGPoint(x: $0.x - pixels.minX, y: pixels.maxY - $0.y) })
        outputOutline.closeSubpath()
        let resultBounds = CGRect(x: 0, y: 0, width: raster.width, height: raster.height)
        context.clear(resultBounds)
        context.addPath(outputOutline)
        context.clip(using: .evenOdd)
        context.interpolationQuality = .none
        context.draw(raster, in: resultBounds)
        guard let image = context.makeImage() else { throw Failure.renderFailed }
        return image
    }

    private static func enclosesArea(_ points: [CGPoint]) -> Bool {
        guard let origin = points.first, let second = points.first(where: { $0 != origin }) else { return false }
        let dx = second.x - origin.x
        let dy = second.y - origin.y
        return points.contains { abs(dx * ($0.y - origin.y) - dy * ($0.x - origin.x)) > 0.01 }
    }

    static func selfCheck() throws {
        let space = CGColorSpace(name: CGColorSpace.sRGB)
        guard let space, let fixture = CGContext(data: nil, width: 16, height: 16, bitsPerComponent: 8,
                                                 bytesPerRow: 0, space: space,
                                                 bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw Failure.renderFailed
        }
        fixture.setFillColor(CGColor(red: 0, green: 1, blue: 0, alpha: 1))
        fixture.fill(CGRect(x: 0, y: 0, width: 16, height: 16))
        fixture.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        fixture.fill(CGRect(x: 0, y: 8, width: 16, height: 8))
        guard let source = fixture.makeImage() else { throw Failure.renderFailed }
        let polygon = [CGPoint(x: 3, y: 4), CGPoint(x: 12, y: 4), CGPoint(x: 12, y: 8),
                       CGPoint(x: 8, y: 8), CGPoint(x: 8, y: 13), CGPoint(x: 3, y: 13)]
        let output = try extract(source, polygon: polygon)
        guard output.width == 9, output.height == 9,
              try sample(output, x: 7, y: 1) == [255, 0, 0, 255],
              try sample(output, x: 2, y: 7) == [0, 255, 0, 255],
              try sample(output, x: 7, y: 7)[3] == 0 else { throw Failure.fixtureMismatch }
    }

    private static func sample(_ image: CGImage, x: Int, y: Int) throws -> [UInt8] {
        guard let crop = image.cropping(to: CGRect(x: x, y: y, width: 1, height: 1)),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let sample = CGContext(data: nil, width: 1, height: 1, bitsPerComponent: 8,
                                     bytesPerRow: 4, space: space,
                                     bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let data = sample.data else { throw Failure.renderFailed }
        sample.draw(crop, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        return Array(UnsafeBufferPointer(start: data.assumingMemoryBound(to: UInt8.self), count: 4))
    }

    private enum Failure: LocalizedError {
        case invalidOutline, renderFailed, fixtureMismatch

        var errorDescription: String? {
            switch self {
            case .invalidOutline: "Draw around a visible area and try again."
            case .renderFailed: "Snip Studio could not prepare the freeform capture."
            case .fixtureMismatch: "The freeform capture fixture did not preserve the expected pixels."
            }
        }
    }
}
