import AppKit
import CoreGraphics
import CoreText
import Foundation
import ImageIO

@MainActor
enum TrialSelfCheck {
    static func run() async -> Int32 {
        do {
            try await checkEditor()
            try checkRowOrder()
            try await checkVision()
            return 0
        } catch {
            FileHandle.standardError.write(Data("Mac trial check failed: \(error)\n".utf8))
            return 1
        }
    }

    private static func checkEditor() async throws {
        let background = try whiteBitmap(width: 120, height: 80)
        guard let source = background.makeImage() else { throw CheckFailure("create editor fixture") }
        let document = EditorDocument()
        let image = NSImage(cgImage: source, size: NSSize(width: 60, height: 40))
        try document.replaceImage(image, alreadySaved: true)
        try require(!document.hasUnsavedChanges, "an opened image starts clean")

        let red = CGColor(red: 1, green: 0, blue: 0, alpha: 1)
        document.beginStroke(at: CGPoint(x: 20, y: 12), color: red, width: 14)
        document.extendStroke(to: CGPoint(x: 28, y: 12))
        document.finishStroke()
        try require(document.canUndo, "completed stroke is undoable")
        try require(document.hasUnsavedChanges, "drawing makes an opened snip dirty")

        let rendered = try await decodedPNG(from: document)
        try require(rendered.width == 120 && rendered.height == 80, "PNG retains original pixels at Retina scale")
        let top = try pixel(in: rendered, x: 22, y: 12)
        let bottom = try pixel(in: rendered, x: 22, y: 67)
        try require(top[0] > 235 && top[1] < 20 && top[2] < 20, "ink exports in the upper-left region")
        try require(bottom.prefix(3).allSatisfy { $0 > 235 }, "export leaves opposite edge intact")

        document.undo()
        let restored = try await decodedPNG(from: document)
        let restoredTop = try pixel(in: restored, x: 22, y: 12)
        try require(!document.canUndo, "Undo removes the only stroke")
        try require(!document.hasUnsavedChanges, "Undo to the opened image clears the dirty state")
        try require(restoredTop.prefix(3).allSatisfy { $0 > 235 }, "Undo restores the underlying pixels")
        print("PASS: native raster size, top-left ink export, and Undo")

        try document.replaceImage(image, alreadySaved: false)
        try require(document.hasUnsavedChanges, "a new capture is unsaved even without ink")
        let captured = try document.pngSnapshot()
        document.beginStroke(at: CGPoint(x: 16, y: 12), color: red, width: 4)
        document.finishStroke()
        document.markSaved(captured)
        try require(document.hasUnsavedChanges, "saving an older snapshot preserves the dirty state of a newer edit")

        document.undo()
        try require(!document.hasUnsavedChanges, "Undo back to the saved snapshot clears the dirty state")
        try document.replaceImage(image, alreadySaved: false)
        document.markSaved(captured)
        try require(document.hasUnsavedChanges, "an older capture cannot mark its replacement as saved")
        print("PASS: unsaved capture and annotation state across saves, Undo, and replacement")
    }

    private static func checkRowOrder() throws {
        let observations = [
            RecognizedLine(text: "RIGHT 0007", bounds: CGRect(x: 0.55, y: 0.79, width: 0.2, height: 0.08)),
            RecognizedLine(text: "BOTTOM", bounds: CGRect(x: 0.05, y: 0.55, width: 0.2, height: 0.04)),
            RecognizedLine(text: "LEFT  0027", bounds: CGRect(x: 0.05, y: 0.80, width: 0.2, height: 0.04))
        ]
        let order = TextRecognition.orderRows(observations).map(\.text)
        try require(order == ["LEFT  0027", "RIGHT 0007", "BOTTOM"], "same-row words read left to right with original spacing")
        print("PASS: OCR row order, spacing, and leading zeros")
    }

    private static func checkVision() async throws {
        let bitmap = try whiteBitmap(width: 760, height: 260)
        let font = CTFontCreateWithName("Menlo-Bold" as CFString, 64, nil)
        let attributes: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 0, alpha: 1)
        ]
        for (text, y) in [("SNIP STUDIO", 166.0), ("FILE 0027", 72.0)] {
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
            bitmap.textPosition = CGPoint(x: 42, y: y)
            CTLineDraw(line, bitmap)
        }
        guard let fixture = bitmap.makeImage() else { throw CheckFailure("create Vision fixture") }
        let output = try await TextRecognition.recognize(fixture)
        try require(output == "SNIP STUDIO\nFILE 0027", "Vision returns both fixture lines in order, including leading zeros")
        print("PASS: on-device Vision recognition")
    }

    private static func decodedPNG(from document: EditorDocument) async throws -> CGImage {
        let png = try await document.pngData()
        guard let source = CGImageSourceCreateWithData(png as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            throw CheckFailure("decode the exported PNG")
        }
        return image
    }

    private static func rgbBitmap(width: Int, height: Int) throws -> CGContext {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let bitmap = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                     bytesPerRow: 0, space: space,
                                     bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw CheckFailure("allocate a bitmap")
        }
        return bitmap
    }

    private static func whiteBitmap(width: Int, height: Int) throws -> CGContext {
        let bitmap = try rgbBitmap(width: width, height: height)
        bitmap.setFillColor(CGColor(gray: 1, alpha: 1))
        bitmap.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return bitmap
    }

    private static func pixel(in image: CGImage, x: Int, y: Int) throws -> [UInt8] {
        guard let cropped = image.cropping(to: CGRect(x: x, y: y, width: 1, height: 1)) else {
            throw CheckFailure("locate image pixel")
        }
        let bitmap = try rgbBitmap(width: 1, height: 1)
        bitmap.draw(cropped, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        guard let bytes = bitmap.data?.assumingMemoryBound(to: UInt8.self) else {
            throw CheckFailure("read image pixel")
        }
        return Array(UnsafeBufferPointer(start: bytes, count: 4))
    }

    private static func require(_ condition: @autoclosure () -> Bool, _ description: String) throws {
        guard condition() else { throw CheckFailure(description) }
    }

    private struct CheckFailure: Error, CustomStringConvertible {
        let description: String

        init(_ description: String) { self.description = description }
    }
}
