import AppKit
import SwiftUI

@MainActor
struct StudioCropOverlay: View {
    @Binding var bounds: CGRect
    let imageFrame: CGRect
    let pixelSize: CGSize

    @Namespace private var coordinates
    @GestureState private var dragging = false
    @State private var drag: CropDrag?
    @FocusState private var focusedGrip: StudioCropGrip?

    var body: some View {
        GeometryReader { viewport in
            let frame = StudioCropGeometry.displayFrame(bounds, in: imageFrame, pixelSize: pixelSize)
            ZStack(alignment: .topLeading) {
                Canvas { context, _ in
                    var mask = Path(imageFrame)
                    mask.addRect(frame)
                    context.fill(mask, with: .color(.black.opacity(0.48)), style: FillStyle(eoFill: true))
                    let outline = Path(frame)
                    context.stroke(outline, with: .color(.black.opacity(0.8)), lineWidth: 3)
                    context.stroke(outline, with: .color(.white), lineWidth: 1)
                }
                .allowsHitTesting(false)
                .accessibilityHidden(true)

                if !frame.isEmpty {
                    let visibleSelection = frame.intersection(CGRect(origin: .zero, size: viewport.size))
                    if !visibleSelection.isEmpty {
                        Rectangle()
                            .fill(.clear)
                            .contentShape(Rectangle())
                            .frame(width: visibleSelection.width, height: visibleSelection.height)
                            .position(x: visibleSelection.midX, y: visibleSelection.midY)
                            .gesture(cropGesture(for: .move))
                            .onHover { over in (over ? NSCursor.openHand : .arrow).set() }
                            .accessibilityHidden(true)
                    }

                    ForEach(StudioCropGrip.handles, id: \.self) { grip in
                        let position = grip.position(in: frame)
                        if viewportContains(position, in: viewport.size) {
                            handle(for: grip)
                                .position(position)
                        }
                    }
                }
            }
            .frame(width: viewport.size.width, height: viewport.size.height)
            .contentShape(Rectangle())
            .coordinateSpace(name: coordinates)
            .clipped()
        }
        .onChange(of: dragging) { _, active in
            guard !active else { return }
            // The system can cancel a drag without calling onEnded.
            Task { @MainActor in
                if !dragging { drag = nil }
            }
        }
        .onDisappear { NSCursor.arrow.set() }
    }

    private func handle(for grip: StudioCropGrip) -> some View {
        RoundedRectangle(cornerRadius: 1)
            .fill(focusedGrip == grip ? Color.accentColor : .white)
            .overlay(RoundedRectangle(cornerRadius: 1).strokeBorder(.black.opacity(0.82), lineWidth: 1))
            .frame(width: 9, height: 9)
            .frame(width: 20, height: 20)
            .contentShape(Rectangle())
            .gesture(cropGesture(for: grip))
            .onHover { over in (over ? grip.cursor : .arrow).set() }
            .focusable()
            .focused($focusedGrip, equals: grip)
            .onMoveCommand { direction in
                if let offset = pixelOffset(for: direction) {
                    bounds = StudioCropGeometry.changed(bounds, by: offset, using: grip, within: pixelSize)
                }
            }
            .accessibilityLabel("\(grip.label) crop handle")
    }

    private func cropGesture(for grip: StudioCropGrip) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named(coordinates))
            .updating($dragging) { _, active, _ in active = true }
            .onChanged { event in updateDrag(event.translation, using: grip) }
            .onEnded { event in
                updateDrag(event.translation, using: grip)
                drag = nil
            }
    }

    private func updateDrag(_ translation: CGSize, using grip: StudioCropGrip) {
        guard imageFrame.width > 0, imageFrame.height > 0 else { return }
        if drag?.grip != grip { drag = CropDrag(grip: grip, bounds: bounds) }
        guard let drag else { return }
        let offset = CGSize(width: translation.width * pixelSize.width / imageFrame.width,
                            height: translation.height * pixelSize.height / imageFrame.height)
        bounds = StudioCropGeometry.changed(drag.bounds, by: offset, using: grip, within: pixelSize)
    }

    private func viewportContains(_ point: CGPoint, in size: CGSize) -> Bool {
        CGRect(origin: .zero, size: size).insetBy(dx: -10, dy: -10).contains(point)
    }

    private func pixelOffset(for direction: MoveCommandDirection) -> CGSize? {
        switch direction {
        case .left: CGSize(width: -1, height: 0)
        case .right: CGSize(width: 1, height: 0)
        case .up: CGSize(width: 0, height: -1)
        case .down: CGSize(width: 0, height: 1)
        @unknown default: nil
        }
    }

    private struct CropDrag {
        let grip: StudioCropGrip
        let bounds: CGRect
    }
}

enum StudioCropGrip: Hashable {
    case topLeft, top, topRight, left, right, bottomLeft, bottom, bottomRight, move

    static let handles: [Self] = [.topLeft, .top, .topRight, .left, .right, .bottomLeft, .bottom, .bottomRight]

    var horizontal: CGFloat {
        switch self {
        case .topLeft, .left, .bottomLeft: -1
        case .topRight, .right, .bottomRight: 1
        case .top, .bottom, .move: 0
        }
    }

    var vertical: CGFloat {
        switch self {
        case .topLeft, .top, .topRight: -1
        case .bottomLeft, .bottom, .bottomRight: 1
        case .left, .right, .move: 0
        }
    }

    var label: String {
        switch self {
        case .topLeft: "Top left"
        case .top: "Top"
        case .topRight: "Top right"
        case .left: "Left"
        case .right: "Right"
        case .bottomLeft: "Bottom left"
        case .bottom: "Bottom"
        case .bottomRight: "Bottom right"
        case .move: "Move"
        }
    }

    @MainActor
    var cursor: NSCursor {
        switch self {
        case .left, .right: .resizeLeftRight
        case .top, .bottom: .resizeUpDown
        case .topLeft, .topRight, .bottomLeft, .bottomRight: .crosshair
        case .move: .openHand
        }
    }

    func position(in frame: CGRect) -> CGPoint {
        // Keep the hit targets separate when the selection is narrower than the grips.
        CGPoint(x: frame.midX + horizontal * max(20, frame.width / 2),
                y: frame.midY + vertical * max(20, frame.height / 2))
    }
}

enum StudioCropGeometry {
    static func changed(_ bounds: CGRect, by offset: CGSize, using grip: StudioCropGrip, within pixelSize: CGSize) -> CGRect {
        guard pixelSize.width >= 1, pixelSize.height >= 1 else { return .zero }
        let dx = offset.width.rounded()
        let dy = offset.height.rounded()
        if grip == .move {
            let x = clamp(bounds.minX + dx, from: 0, to: pixelSize.width - bounds.width)
            let y = clamp(bounds.minY + dy, from: 0, to: pixelSize.height - bounds.height)
            return CGRect(origin: CGPoint(x: x, y: y), size: bounds.size)
        }

        var left = bounds.minX, right = bounds.maxX
        var top = bounds.minY, bottom = bounds.maxY
        if grip.horizontal < 0 { left = clamp(left + dx, from: 0, to: right - 1) }
        if grip.horizontal > 0 { right = clamp(right + dx, from: left + 1, to: pixelSize.width) }
        if grip.vertical < 0 { top = clamp(top + dy, from: 0, to: bottom - 1) }
        if grip.vertical > 0 { bottom = clamp(bottom + dy, from: top + 1, to: pixelSize.height) }
        return CGRect(x: left, y: top, width: right - left, height: bottom - top)
    }

    static func displayFrame(_ bounds: CGRect, in imageFrame: CGRect, pixelSize: CGSize) -> CGRect {
        guard pixelSize.width > 0, pixelSize.height > 0 else { return .zero }
        let horizontalScale = imageFrame.width / pixelSize.width
        let verticalScale = imageFrame.height / pixelSize.height
        return CGRect(x: imageFrame.minX + bounds.minX * horizontalScale,
                      y: imageFrame.minY + bounds.minY * verticalScale,
                      width: bounds.width * horizontalScale, height: bounds.height * verticalScale)
    }

    private static func clamp(_ value: CGFloat, from lower: CGFloat, to upper: CGFloat) -> CGFloat {
        min(upper, max(lower, value))
    }
}
