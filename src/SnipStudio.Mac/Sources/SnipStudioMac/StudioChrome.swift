import AppKit
import SwiftUI

enum StudioTool {
    case move, pen, highlight, shape, text, eraser, crop, removeArea, textActions
}

struct StudioTextDraft {
    let annotationID: UUID?
    let origin: CGPoint
    var note: StudioTextAnnotation
    var color: Color
}

struct StudioInlineTextEditor: View {
    @Binding var draft: StudioTextDraft
    let frame: CGRect
    let scale: CGFloat
    @FocusState private var isFocused: Bool

    var body: some View {
        let fontSize = max(10, min(56, draft.note.pixelSize * scale))
        let regular = Font.custom(draft.note.fontName, size: fontSize)
            .weight(draft.note.bold ? .bold : .regular)
        TextEditor(text: $draft.note.text)
            .font(draft.note.italic ? regular.italic() : regular)
            .foregroundStyle(draft.color)
            .scrollContentBackground(.hidden)
            .padding(2)
            .background(Color(nsColor: .textBackgroundColor))
            .overlay { RoundedRectangle(cornerRadius: 3).strokeBorder(StudioColors.primary, lineWidth: 1) }
            .frame(width: frame.width, height: frame.height)
            .position(x: frame.midX, y: frame.midY)
            .focused($isFocused)
            .onAppear { isFocused = true }
            .accessibilityLabel("Editable text annotation")
    }
}

extension SnipShape {
    var icon: String {
        switch self {
        case .rectangle: "rectangle"
        case .ellipse: "oval"
        case .line: "line.diagonal"
        case .arrow: "arrow.up.right"
        }
    }
}

extension CaptureMode {
    var title: String {
        switch self {
        case .region: "Rectangle"
        case .window: "Window"
        case .fullScreen: "Full screen"
        case .allDisplays: "All monitors"
        }
    }

    var icon: String {
        switch self {
        case .region: "rectangle.dashed"
        case .window: "macwindow"
        case .fullScreen: "arrow.up.left.and.arrow.down.right"
        case .allDisplays: "display.2"
        }
    }
}

enum StudioColors {
    static let primary = Color(red: 0, green: 103 / 255, blue: 192 / 255)
    static let toolbar = adaptive(0xffffff, dark: 0x202020, contrast: .windowBackgroundColor)
    static let options = adaptive(0xeef5fe, dark: 0x263444, contrast: .windowBackgroundColor)
    static let canvas = adaptive(0xf1f4f8, dark: 0x141a24, contrast: .controlBackgroundColor)
    static let selected = adaptive(0xddedfd, dark: 0x244a70, contrast: .selectedControlColor)

    private static func adaptive(_ light: UInt32, dark: UInt32, contrast: NSColor) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let match = appearance.bestMatch(from: [.accessibilityHighContrastAqua, .accessibilityHighContrastDarkAqua, .aqua, .darkAqua])
            if match == .accessibilityHighContrastAqua || match == .accessibilityHighContrastDarkAqua {
                return contrast
            }
            let value = match == .darkAqua ? dark : light
            return NSColor(
                srgbRed: CGFloat((value >> 16) & 255) / 255,
                green: CGFloat((value >> 8) & 255) / 255,
                blue: CGFloat(value & 255) / 255,
                alpha: 1
            )
        })
    }
}

struct StudioButtonStyle: ButtonStyle {
    var primary = false
    var selected = false

    func makeBody(configuration: Configuration) -> some View {
        StudioButtonFace(label: configuration.label, primary: primary, selected: selected, pressed: configuration.isPressed)
    }
}

private struct StudioButtonFace<Label: View>: View {
    let label: Label
    let primary: Bool
    let selected: Bool
    let pressed: Bool

    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var hovered = false

    var body: some View {
        label
            .padding(.horizontal, primary ? 9 : 7)
            .frame(height: primary ? 34 : 30)
            .foregroundStyle(primary ? Color.white : Color.primary)
            .background(background, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(RoundedRectangle(cornerRadius: 6))
            .opacity(enabled ? 1 : 0.46)
            .onHover { hovered = $0 }
            .animation(reduceMotion ? nil : .easeOut(duration: 0.10), value: hovered)
    }

    private var background: Color {
        if primary { return pressed ? StudioColors.primary.opacity(0.8) : StudioColors.primary }
        if selected { return StudioColors.selected }
        if enabled && pressed { return Color.primary.opacity(0.14) }
        if enabled && hovered { return Color.primary.opacity(0.08) }
        return .clear
    }
}

struct StudioStrokeSample: View {
    let color: Color
    let width: CGFloat

    var body: some View {
        Canvas { context, size in
            let radius = width / 2
            let waveHeight = max(0, (size.height - width - 2) / 2)
            let center = size.height / 2
            let span = size.width - width - 4
            let start = radius + 2
            var path = Path()
            path.move(to: CGPoint(x: start, y: center))
            path.addCurve(
                to: CGPoint(x: start + span, y: center),
                control1: CGPoint(x: start + span * 0.32, y: center - 2.4 * waveHeight),
                control2: CGPoint(x: start + span * 0.68, y: center + 2.4 * waveHeight)
            )
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: width, lineCap: .round))
        }
        .frame(width: 93, height: 32)
        .accessibilityLabel("Stroke preview")
    }
}
