import SwiftUI

struct OCRRegionOverlay: View {
    let lines: [RecognizedLine]
    let imageFrame: CGRect

    var body: some View {
        let regions = Self.revealRegions(lines, in: imageFrame)
        Canvas { context, _ in
            guard !regions.isEmpty else { return }
            var revealed = Path()
            for region in regions {
                let radius = min(3, region.width / 2, region.height / 4)
                revealed.addPath(Path(roundedRect: region, cornerRadius: radius))
            }
            // Inverse clipping keeps overlapping reveals clear without touching the original pixels.
            context.clip(to: revealed, options: .inverse)
            let scrim = Color(red: 7.0 / 255, green: 16.0 / 255, blue: 38.0 / 255).opacity(72.0 / 255)
            context.fill(Path(imageFrame), with: .color(scrim))
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    static func revealRegions(_ lines: [RecognizedLine], in imageFrame: CGRect) -> [CGRect] {
        guard imageFrame.width > 0, imageFrame.height > 0,
              imageFrame.minX.isFinite, imageFrame.minY.isFinite,
              imageFrame.maxX.isFinite, imageFrame.maxY.isFinite else { return [] }
        let imageBounds = CGRect(x: 0, y: 0, width: 1, height: 1)
        let boundedLines = lines.compactMap { line -> RecognizedLine? in
            let bounds = line.bounds
            guard bounds.minX.isFinite, bounds.minY.isFinite, bounds.maxX.isFinite, bounds.maxY.isFinite,
                  !line.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            let clipped = bounds.intersection(imageBounds)
            guard !clipped.isEmpty else { return nil }
            return RecognizedLine(text: line.text, bounds: clipped)
        }
        let bands = TextRecognition.groupRows(boundedLines).map { row in
            RevealBand(boxes: row.map { line in
                let bounds = line.bounds
                return CGRect(
                    x: imageFrame.minX + bounds.minX * imageFrame.width,
                    y: imageFrame.minY + (1 - bounds.maxY) * imageFrame.height,
                    width: bounds.width * imageFrame.width,
                    height: bounds.height * imageFrame.height
                )
            })
        }
        let heights = paddedHeights(for: bands)
        return bands.indices.flatMap { index in
            let band = bands[index]
            // Where two bands are close, keep the gutter visible while still exposing their ink.
            let topLimit = index == 0 ? imageFrame.minY :
                min(band.top, (bands[index - 1].bottom + band.top) / 2 + 0.5)
            let bottomLimit = index == bands.count - 1 ? imageFrame.maxY :
                max(band.bottom, (band.bottom + bands[index + 1].top) / 2 - 0.5)
            let height = min(heights[index], bottomLimit - topLimit)
            let top = min(bottomLimit - height, max(topLimit, (band.top + band.bottom - height) / 2))
            let horizontalPadding = min(5, max(2, band.height * 0.22))
            return band.boxes.map { box in
                let left = max(imageFrame.minX, box.minX - horizontalPadding)
                let right = min(imageFrame.maxX, box.maxX + horizontalPadding)
                return CGRect(x: left, y: top, width: right - left, height: height)
            }
        }
    }

    private static func paddedHeights(for bands: [RevealBand]) -> [CGFloat] {
        let indicesByHeight = bands.indices.sorted { bands[$0].height < bands[$1].height }
        var heights = Array(repeating: CGFloat.zero, count: bands.count)
        var start = 0
        while start < indicesByHeight.count {
            let reference = bands[indicesByHeight[start]].height
            let comparableHeight = reference + max(3, reference / 3)
            var end = start + 1
            while end < indicesByHeight.count, bands[indicesByHeight[end]].height <= comparableHeight {
                end += 1
            }
            let tallest = bands[indicesByHeight[end - 1]].height
            let padded = tallest + min(5, max(2, tallest * 0.24)) * 2
            for index in indicesByHeight[start..<end] { heights[index] = padded }
            start = end
        }
        return heights
    }

    private struct RevealBand {
        let boxes: [CGRect]
        let top: CGFloat
        let bottom: CGFloat
        var height: CGFloat { bottom - top }

        init(boxes: [CGRect]) {
            self.boxes = boxes
            top = boxes.map(\.minY).min() ?? 0
            bottom = boxes.map(\.maxY).max() ?? 0
        }
    }
}
