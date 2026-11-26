import SwiftUI

extension OCRContact {
    func pixelRegion(in size: CGSize) -> CGRect {
        let raw = CGRect(x: bounds.minX * size.width, y: (1 - bounds.maxY) * size.height,
                         width: bounds.width * size.width, height: bounds.height * size.height)
        return raw.insetBy(dx: -3, dy: -3).intersection(CGRect(origin: .zero, size: size)).integral
    }
}

struct StudioRedactionReview: View {
    let image: CGImage
    let contacts: [OCRContact]
    let apply: ([OCRContact]) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var selected = Set<UUID>()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Review quick redactions").font(.headline)
            Text("Check that the marked areas cover the information you want to hide.")
                .font(.callout).foregroundStyle(.secondary)
            GeometryReader { viewport in
                let size = CGSize(width: image.width, height: image.height)
                let scale = min(viewport.size.width / size.width, viewport.size.height / size.height)
                let imageFrame = CGRect(x: (viewport.size.width - size.width * scale) / 2,
                                        y: (viewport.size.height - size.height * scale) / 2,
                                        width: size.width * scale, height: size.height * scale)
                Canvas { context, _ in
                    context.draw(Image(decorative: image, scale: 1), in: imageFrame)
                    for item in contacts where selected.contains(item.id) {
                        let patch = item.pixelRegion(in: size)
                        let frame = CGRect(x: imageFrame.minX + patch.minX * scale,
                                           y: imageFrame.minY + patch.minY * scale,
                                           width: patch.width * scale, height: patch.height * scale)
                        let outline = Path(frame)
                        context.fill(outline, with: .color(.black.opacity(0.58)))
                        context.stroke(outline, with: .color(.orange), lineWidth: 2)
                    }
                }
                .accessibilityLabel("Preview of the selected redactions")
            }
            .background(StudioColors.canvas)
            ScrollView {
                VStack(alignment: .leading, spacing: 7) {
                    ForEach(contacts) { contact in
                        Toggle(isOn: Binding(get: { selected.contains(contact.id) }, set: { include in
                            if include { selected.insert(contact.id) } else { selected.remove(contact.id) }
                        })) {
                            Text(contact.value).lineLimit(1)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: 108)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Apply redactions") {
                    apply(contacts.filter { selected.contains($0.id) })
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selected.isEmpty)
            }
        }
        .padding(16)
        .frame(width: 660, height: 550)
        .onAppear { selected = Set(contacts.map(\.id)) }
    }
}
