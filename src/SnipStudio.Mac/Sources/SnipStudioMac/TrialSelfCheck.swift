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
            try StudioTextAnnotationSelfCheck.run()
            try StudioEmojiRasterSelfCheck.run()
            try StudioScrollStitcher.selfCheck()
            try StudioAutomaticScroll.selfCheck()
            try StudioRecordingChromeGeometry.selfCheck()
            try StudioFreeformMask.selfCheck()
            try StudioOCRReplacement.selfCheck()
            try StudioTranslationServer.selfCheck()
            if #available(macOS 15.0, *) {
                try StudioTranslationReviewSelfCheck.run()
                let crop = try StudioScreenRecorder.displayRect(
                    for: CGRect(x: 40, y: 30, width: 200, height: 100),
                    windowFrame: CGRect(x: -1360, y: 70, width: 800, height: 500),
                    displayFrame: CGRect(x: -1440, y: -160, width: 1440, height: 900)
                )
                try require(crop == CGRect(x: 120, y: 260, width: 200, height: 100),
                            "recording a selected window region accounts for its secondary display origin")
            }
            try await StudioProjectFile.selfCheck()
            try StudioWindowsProjectAnnotations.selfCheck()
            try await checkWindowsProjects()
            try await checkRasterPatches()
            try await checkRedactionAndProjectReplacement()
            try await checkShapesAndEraser()
            try await checkCrop()
            try await checkObjectEditing()
            try checkViewport()
            try checkRowOrder()
            try checkOCRWordSelection()
            try checkOCRTable()
            try await checkVision()
            try await StudioFileExportSelfCheck.run()
            return 0
        } catch {
            FileHandle.standardError.write(Data("Mac trial check failed: \(error)\n".utf8))
            return 1
        }
    }

    private static func checkWindowsProjects() async throws {
        // Independent fixtures from Windows BinaryWriter, GDI+ 32bppArgb rows and .NET DeflateStream.
        let strokeFixture = "U05JUEVESVQBAAAAKgAAAJoETo/N17lgaXhwIsnLuwsWwgsL7qLiCZO8uV7alnwsAgAAAAIAAAABAAAAASBAgJoAAEA/AQIAAAAAAIA+AAAAPwAAwD8AAOA/GJ60E6sckLXQ2C4N+oOWnb/mcdxfm3DiETuaLKbf+q1jYPj/n+E/iACTDQA="
        let cardinalFixture = "U05JUEVESVQCAAAAMwAAAG5YXhRxn3zN7Myx4Bf0mG7T8j/r6NaixcS1pgPuW4JmAgAAAAIAAAABAAAAAgAAgD4AAAA+AACgPwAAQD8AALRCASBAYIAAAAAAQBkAAABTAAAAGJ60E6sckLXQ2C4N+oOWnb/mcdxfm3DiETuaLKbf+q1jYPj/n+E/iACTDQA="
        let outlineFixture = "U05JUEVESVQDAAAAOAAAABrGEcc2fHNf4bDOmK1eLEgR0MyN1ZEegiiLxk1L4/P5AgAAAAIAAAABAAAAAgAAgD4AAAA+AACgPwAAQD8AAAxCASBAYIABgEAgmgAAgD4ZAAAASwAAAAIYnrQTqxyQtdDYLg36g5adv+Zx3F+bcOIRO5ospt/6rWNg+P+f4T+IAJMNAA=="
        let bandsFixture = "U05JUEVESVQBAAAANQAAAOHg84SxVo4QA23LpXTQpUUB4LJK/spintx+ThTvPrZpBgAAAAYAAAABAAAACAAA/4ACAAAAAACAPwAAgD8AAEBAAAAAQAAAAEAAAABAAABAQAAAAECBxhHzW/95SRU4svfPIBx1l6ZhpcVJYzVBxivcivFhP2MYZAAA"
        let masksFixture = "U05JUEVESVQBAAAAJQAAABQvCk9rjI58KPJXAAo4oO1lhiodkzy6bexFZ4TWoRxsAgAAAAIAAAABAAAACQEAAAAAAIA+AAAAPwAAgD8AAAA/AAAA/xietBOrHJC10NguDfqDlp2/5nHcX5tw4hE7miym3/qtY2D4/5/hP4gAkw0A"
        let richTextFixture = "U05JUEVESVQBAAAAkQAAAKGBYxtibbPlunCcYRbvmMZQwkaI+d1l7j14/GuJmUwbAgAAAAIAAAABAAAABgAAgD4AAAA/AADAQQAAQEEIAAAAUVRZIDAwMDdkAAAAe1xydGYxXGFuc2lcZGVmZjB7XGZvbnR0Ymx7XGYwIFNlZ29lIFVJO319e1xjb2xvcnRibDtccmVkMFxncmVlbjBcYmx1ZTA7fVxmMFxmczE4XGNmMSBRVFkge1xiIDAwMDd9fRietBOrHJC10NguDfqDlp2/5nHcX5tw4hE7miym3/qtY2D4/5/hP4gAkw0A"
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("SnipStudioWindowsFixture-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: directory) }
        let input = directory.appendingPathComponent("test.snipstudio")

        func fixture(_ encoded: String) throws -> Data {
            guard let data = Data(base64Encoded: encoded) else { throw CheckFailure("decode Windows project fixture") }
            return data
        }

        func load(_ data: Data) async throws -> StudioProjectContents {
            try data.write(to: input, options: .atomic)
            return try await StudioProjectFile.load(from: input)
        }

        let stroke = try await load(fixture(strokeFixture))
        try require(stroke.annotations.count == 1, "Windows v1 stroke loads")
        try require(stroke.annotations[0].points == [CGPoint(x: 0.25, y: 0.5), CGPoint(x: 1.5, y: 1.75)] &&
                    stroke.annotations[0].alpha == CGFloat(80) / 255, "Windows v1 highlighter preserves points and legacy alpha")
        let cardinal = try await load(fixture(cardinalFixture))
        try require(cardinal.annotations.count == 1 && cardinal.annotations[0].shape == "rectangle" &&
                    cardinal.annotations[0].quarterTurns == 1, "Windows v2 keeps a cardinal shape rotation")
        let outlineBytes = try fixture(outlineFixture)
        let outline = try await load(outlineBytes)
        try require(outline.annotations.count == 1 && outline.annotations[0].lineStyle == "dotted" &&
                    outline.annotations[0].width == 0.25 && outline.annotations[0].rotationDegrees == 35 &&
                    outline.annotations[0].alpha == CGFloat(116) / 255 &&
                    outline.annotations[0].fill?.last == CGFloat(32) / 255,
                    "Windows v3 keeps arbitrary rotation, fractional width, dotted style and independent alpha")
        try require(outline.image.width == 2 && outline.image.height == 2, "Windows raster dimensions are exact")
        let red = try pixel(in: outline.image, x: 0, y: 0)
        let green = try pixel(in: outline.image, x: 1, y: 0)
        let blue = try pixel(in: outline.image, x: 0, y: 1)
        let yellow = try pixel(in: outline.image, x: 1, y: 1)
        try require(red == [255, 0, 0, 255] && green == [0, 255, 0, 255] && blue == [0, 0, 255, 255] &&
                    yellow == [128, 128, 0, 128], "Windows BGRA keeps row orientation, RGB channels, and straight alpha")

        let document = EditorDocument()
        try await document.openProject(outline)
        let roundTrip = directory.appendingPathComponent("converted.snipstudiomac")
        try await document.pngSnapshot().writeProject(to: roundTrip)
        let reopened = try await StudioProjectFile.load(from: roundTrip)
        try require(reopened.annotations == outline.annotations,
                    "the Mac copy reopens with the same editable geometry, stroke color and fill color")
        let sourceBytes = try Data(contentsOf: input)
        try require(sourceBytes == outlineBytes, "opening and saving a Mac copy preserves the original Windows project bytes")

        let bands = try await load(fixture(bandsFixture))
        let grouped = EditorDocument()
        try await grouped.openProject(bands)
        try require(grouped.strokes.count == 1 && grouped.strokes[0].highlightBands ==
                    [CGRect(x: 1, y: 1, width: 3, height: 2), CGRect(x: 2, y: 2, width: 3, height: 2)],
                    "Windows highlight bands remain one editable object in original paint order")
        let bandImage = try await decodedPNG(from: grouped)
        let singleBand = try pixel(in: bandImage, x: 1, y: 1)
        let overlap = try pixel(in: bandImage, x: 2, y: 2)
        let outside = try pixel(in: bandImage, x: 0, y: 0)
        try require(singleBand == [128, 0, 0, 128] && overlap == [192, 0, 0, 192] && outside == [0, 0, 0, 0],
                    "overlapping Windows highlight bands match GDI+ premultiplied output")
        try await grouped.pngSnapshot().writeProject(to: roundTrip)
        let restoredBands = try await StudioProjectFile.load(from: roundTrip)
        try require(restoredBands.annotations == bands.annotations, "a saved Mac copy preserves the grouped highlight records")

        var wrongMetadataHash = outlineBytes
        wrongMetadataHash[16] ^= 1
        var wrongPixelHash = outlineBytes
        wrongPixelHash[48 + 56] ^= 1
        let truncated = Data(outlineBytes.dropLast())
        let trailing = outlineBytes + Data([0])
        let bgra = Data([0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 255, 255, 128])
        // One final RFC 1951 stored block containing the expected raster plus one unexpected decoded byte.
        let excessPixels = Data(outlineBytes.prefix(80 + 56)) + Data([1, 17, 0, 238, 255]) + bgra + Data([0])
        for invalid in [wrongMetadataHash, wrongPixelHash, truncated, trailing, excessPixels,
                        try fixture(masksFixture), try fixture(richTextFixture)] {
            try invalid.write(to: input, options: .atomic)
            do {
                _ = try await StudioProjectFile.load(from: input)
                throw CheckFailure("damaged or unsupported Windows projects must be rejected as a whole")
            } catch let failure as CheckFailure {
                throw failure
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                // Rejection leaves the existing editable document available.
                try require(document.strokes.count == 1, "failed Windows loads do not modify the current snip")
            }
        }
        print("PASS: Windows project v1–v3 import, color and transparency, editable Mac copy and damaged-data rejection")
    }

    private static func checkRasterPatches() async throws {
        let background = try whiteBitmap(width: 30, height: 20)
        guard let source = background.makeImage() else { throw CheckFailure("create raster patch fixture") }
        let document = EditorDocument()
        try document.replaceImage(NSImage(cgImage: source, size: CGSize(width: 30, height: 20)), alreadySaved: true)
        let cut = CGRect(x: 2, y: 3, width: 9, height: 6)
        let cutApplied = try await document.patchRaster([cut], opaque: false)
        try require(cutApplied && document.hasUnsavedChanges, "transparent area removal creates an undoable edit")
        var exported = try await decodedPNG(from: document)
        let removed = try pixel(in: exported, x: 4, y: 5)
        let untouched = try pixel(in: exported, x: 4, y: 14)
        try require(removed[3] == 0, "pixels in the removed area are transparent")
        try require(untouched[3] == 255, "the opposite image row stays opaque")
        document.undo()
        try require(!document.hasUnsavedChanges, "Undo restores original raster identity")
        let redactApplied = try await document.patchRaster([cut], opaque: true)
        try require(redactApplied, "redaction applies")
        exported = try await decodedPNG(from: document)
        let redacted = try pixel(in: exported, x: 4, y: 5)
        try require(redacted == [0, 0, 0, 255], "redaction overwrites the actual exported color and alpha channels")
        print("PASS: pixels removed and redacted in output and Undo restores source")
    }

    private static func checkRedactionAndProjectReplacement() async throws {
        let bitmap = try whiteBitmap(width: 180, height: 80)
        guard let original = bitmap.makeImage() else { throw CheckFailure("create editable redaction fixture") }
        let document = EditorDocument()
        try document.replaceImage(NSImage(cgImage: original, size: CGSize(width: 180, height: 80)), alreadySaved: true)
        let openRevision = document.contentRevision
        let note = StudioTextAnnotation(text: "test@example.com", fontName: "Menlo-Regular", pixelSize: 12)
        try document.applyText(note, at: CGPoint(x: 12, y: 18), color: CGColor(gray: 0, alpha: 1), updating: nil)
        guard let visible = document.strokes.first, visible.textNote?.text == note.text else {
            throw CheckFailure("create editable contact text")
        }
        do {
            try await document.openProject(StudioProjectContents(image: original, annotations: []), replacing: openRevision)
            throw CheckFailure("project replacement must refuse newly added notes")
        } catch EditorDocumentError.documentChangedDuringOpen {
            try require(document.strokes.first?.id == visible.id, "new edits survive a conflicting project open")
        }

        let snapshot = try document.pngSnapshot()
        let revision = document.contentRevision
        let rendered = try await Task.detached { try snapshot.rasterForExport() }.value
        let patch = visible.bounds.insetBy(dx: -1, dy: -1)
        try await document.applyRedactions([patch], to: rendered, expectedRevision: revision)
        try require(document.strokes.isEmpty, "the redacted document contains no recoverable text annotation")
        let png = try await decodedPNG(from: document)
        let fill = try pixel(in: png, x: Int(patch.midX), y: Int(patch.midY))
        let outside = try pixel(in: png, x: 3, y: 3)
        try require(fill == [0, 0, 0, 255] && outside == [255, 255, 255, 255], "redaction is opaque and limited to the approved area")
        document.undo()
        try require(document.strokes.first?.id == visible.id, "Undo restores editable work after Quick Redact")
        print("PASS: installed projects respect newer edits and redaction removes recoverable note text")
    }

    private static func checkObjectEditing() async throws {
        let bitmap = try whiteBitmap(width: 120, height: 100)
        guard let raster = bitmap.makeImage() else { throw CheckFailure("create object-editing fixture") }
        let document = EditorDocument()
        try document.replaceImage(NSImage(cgImage: raster, size: NSSize(width: 120, height: 100)), alreadySaved: true)
        let originalRaster = document.rasterRevision
        let red = CGColor(red: 1, green: 0, blue: 0, alpha: 1)
        let blue = CGColor(red: 0, green: 0, blue: 1, alpha: 1)

        document.beginShape(at: CGPoint(x: 20, y: 20), color: red, width: 4, shape: .arrow, lineStyle: .solid)
        document.extendStroke(to: CGPoint(x: 80, y: 20))
        document.finishStroke()
        let originalPoint = CGPoint(x: 35, y: 20)
        guard let selected = document.annotation(at: originalPoint, tolerance: 0) else {
            throw CheckFailure("select an arrow by its visible outline")
        }
        let id = selected.id
        let originalRevision = document.contentRevision
        try require(document.annotation(withID: id)?.shape == .arrow, "selection resolves the stable identifier")
        try require(!document.translateAnnotation(id: id, by: .zero) && !document.updateAnnotation(id: id, width: 0),
                    "invalid or unchanged object edits are ignored")
        try require(document.contentRevision == originalRevision, "no-op object updates leave the history intact")

        let offset = CGSize(width: 20, height: 22)
        let preview = selected.offset(by: offset)
        try require(preview.bounds == selected.bounds.offsetBy(dx: 20, dy: 22) && document.contentRevision == originalRevision,
                    "drag preview moves the outline without changing the document")
        try require(document.translateAnnotation(id: id, by: offset), "moving the selected shape succeeds")
        let moved = CGPoint(x: 55, y: 42)
        try require(document.annotation(at: moved, tolerance: 0)?.id == id && document.annotation(at: originalPoint, tolerance: 0) == nil,
                    "hit testing follows an object after a move")
        let translatedRevision = document.contentRevision

        try require(document.rotateAnnotation(id: id, clockwise: true), "clockwise rotation succeeds")
        let downwardsTip = CGPoint(x: 64, y: 54)
        try require(document.annotation(at: downwardsTip, tolerance: 0)?.id == id,
                    "clockwise rotation points the arrow down in image coordinates")
        let rotationResult = try await decodedPNG(from: document)
        let movedPixel = try pixel(in: rotationResult, x: 55, y: 42)
        let rotatedPixel = try pixel(in: rotationResult, x: 64, y: 54)
        try require(isWhite(movedPixel) && isRed(rotatedPixel), "the rendered arrow rotates around its center")
        let rotatedRevision = document.contentRevision

        try require(document.updateAnnotation(id: id, color: blue, width: 3, lineStyle: .dashed),
                    "shape properties can be changed after insertion")
        let styledResult = try await decodedPNG(from: document)
        let styledInk = try pixel(in: styledResult, x: 70, y: 14)
        let styledGap = try pixel(in: styledResult, x: 70, y: 24)
        try require(styledInk[0] < 20 && styledInk[1] < 20 && styledInk[2] > 235 && isWhite(styledGap),
                    "the new color and dash pattern follow the rotated shape in the PNG")
        let styledRevision = document.contentRevision
        document.markSaved(try document.pngSnapshot())
        try require(document.deleteAnnotation(id: id) && document.annotation(withID: id) == nil,
                    "deleting the selected object removes its stable ID")
        document.undo()
        try require(document.contentRevision == styledRevision && !document.hasUnsavedChanges && document.annotation(withID: id)?.width == 3,
                    "Undo of deletion restores the saved styling and ID")
        document.undo()
        try require(document.contentRevision == rotatedRevision && document.annotation(withID: id)?.lineStyle == .solid,
                    "Undo of styling restores the rotated object properties")
        document.undo()
        try require(document.contentRevision == translatedRevision && document.annotation(at: moved, tolerance: 0)?.id == id,
                    "Undo of rotation restores the translated horizontal arrow")
        document.redo()
        document.redo()
        try require(document.contentRevision == styledRevision && !document.hasUnsavedChanges && document.rasterRevision == originalRaster,
                    "redoing annotation transforms returns to the saved revision without replacing the source")
        try require(document.rotateAnnotation(id: id, clockwise: false) && document.annotation(at: moved, tolerance: 4)?.id == id,
                    "counterclockwise rotation restores the original direction")
        print("PASS: object hit testing, movement, rotation, style, stable IDs, and edit history")
    }

    private static func checkCrop() async throws {
        let bitmap = try whiteBitmap(width: 120, height: 100)
        bitmap.setFillColor(CGColor(red: 0, green: 1, blue: 0, alpha: 1))
        bitmap.fill(CGRect(x: 26, y: 72, width: 8, height: 8))
        guard let raster = bitmap.makeImage() else { throw CheckFailure("create crop fixture") }
        let document = EditorDocument()
        try document.replaceImage(NSImage(cgImage: raster, size: NSSize(width: 60, height: 50)), alreadySaved: true)
        let originalRaster = document.rasterRevision
        let red = CGColor(red: 1, green: 0, blue: 0, alpha: 1)
        let blue = CGColor(red: 0, green: 0, blue: 1, alpha: 1)
        document.beginShape(at: CGPoint(x: 10, y: 40), color: red, width: 4, shape: .line, lineStyle: .solid)
        document.extendStroke(to: CGPoint(x: 110, y: 40))
        document.finishStroke()
        document.beginStroke(at: CGPoint(x: 12, y: 10), color: blue, width: 4)
        document.finishStroke()
        let originalContent = document.contentRevision
        let originalAnnotations = document.strokes.map(\.id)
        let oldSave = try document.pngSnapshot()

        let fullCrop = try document.crop(to: CGRect(x: -5, y: -5, width: 200, height: 200))
        let emptyCrop = try document.crop(to: CGRect(x: 5.2, y: 8.4, width: 0, height: 6))
        try require(!fullCrop && !emptyCrop && document.contentRevision == originalContent,
                    "full-image and empty crops leave the edit history intact")

        let didCrop = try document.crop(to: CGRect(x: 20.2, y: 15.7, width: 60.1, height: 40.1))
        try require(didCrop, "crop applies to a partial image selection")
        let croppedRaster = document.rasterRevision
        let croppedContent = document.contentRevision
        try require(croppedRaster != originalRaster && croppedContent != originalContent,
                    "crop changes both source and document revisions")
        try require(document.strokes.map(\.id) == Array(originalAnnotations.prefix(1)),
                    "crop retains the stable identifier of the crossing line and removes invisible marks")
        try require(document.strokes.first?.points == [CGPoint(x: -10, y: 25), CGPoint(x: 90, y: 25)],
                    "the remaining object is translated and still editable")
        let output = try await decodedPNG(from: document)
        try require(output.width == 61 && output.height == 41, "fractional crop includes each touched source pixel")
        let shiftedMarker = try pixel(in: output, x: 10, y: 8)
        try require(shiftedMarker[0] < 20 && shiftedMarker[1] > 235 && shiftedMarker[2] < 20,
                    "cropped image retains the exact selected top-left source coordinates")
        let leftInk = try pixel(in: output, x: 0, y: 25)
        let rightInk = try pixel(in: output, x: 60, y: 25)
        try require(isRed(leftInk) && isRed(rightInk), "crossing line is translated and clipped to both crop edges")

        document.markSaved(oldSave)
        try require(document.hasUnsavedChanges, "completion of an older save does not mark the crop saved")
        document.undo()
        try require(!document.hasUnsavedChanges && document.contentRevision == originalContent && document.rasterRevision == originalRaster,
                    "Undo restores source and content revisions and the saved state")
        try require(document.image?.width == 120 && document.strokes.map(\.id) == originalAnnotations,
                    "Undo restores the full raster and every editable object")
        document.redo()
        try require(document.hasUnsavedChanges && document.contentRevision == croppedContent && document.rasterRevision == croppedRaster,
                    "Redo restores the exact cropped state and revision")
        try require(document.eraseAnnotation(at: CGPoint(x: 8, y: 25), tolerance: 0),
                    "cropped annotations still participate in normal editing")
        let withoutLine = try await decodedPNG(from: document)
        let restoredCanvas = try pixel(in: withoutLine, x: 8, y: 25)
        try require(isWhite(restoredCanvas), "annotation pixels were not flattened into the crop")
        print("PASS: editable crop, exact source pixels, history, and save revisions")
    }

    private static func checkShapesAndEraser() async throws {
        let bitmap = try whiteBitmap(width: 160, height: 120)
        guard let source = bitmap.makeImage() else { throw CheckFailure("create shape fixture") }
        let document = EditorDocument()
        let image = NSImage(cgImage: source, size: NSSize(width: 160, height: 120))
        try document.replaceImage(image, alreadySaved: true)
        let red = CGColor(red: 1, green: 0, blue: 0, alpha: 1)
        let blue = CGColor(red: 0, green: 0, blue: 1, alpha: 1)

        document.beginShape(at: CGPoint(x: 10, y: 24), color: red, width: 4, shape: .arrow, lineStyle: .solid)
        document.extendStroke(to: CGPoint(x: 76, y: 38))
        document.extendStroke(to: CGPoint(x: 110, y: 24))
        let head = CGPoint(x: 94, y: 20)
        try require(document.draftStroke?.outline.contains(head) == true, "arrowhead is visible before the drag ends")
        try require(document.draftStroke?.points.count == 2, "a shape drag only retains its endpoints")
        document.finishStroke()

        document.beginShape(at: CGPoint(x: 10, y: 48), color: red, width: 4, shape: .line, lineStyle: .dashed)
        document.extendStroke(to: CGPoint(x: 110, y: 48))
        document.finishStroke()
        document.beginShape(at: CGPoint(x: 10, y: 62), color: red, width: 4, shape: .line, lineStyle: .dotted)
        document.extendStroke(to: CGPoint(x: 110, y: 62))
        document.finishStroke()

        document.beginShape(at: CGPoint(x: 144, y: 108), color: red, width: 4, shape: .rectangle, lineStyle: .solid)
        document.extendStroke(to: CGPoint(x: 86, y: 78))
        document.finishStroke()
        document.beginShape(at: CGPoint(x: 12, y: 78), color: red, width: 4, shape: .ellipse, lineStyle: .solid)
        document.extendStroke(to: CGPoint(x: 68, y: 108))
        document.finishStroke()

        let exported = try await decodedPNG(from: document)
        for (x, y) in [(94, 20), (14, 48), (10, 62), (92, 78), (40, 78)] {
            let rgba = try pixel(in: exported, x: x, y: y)
            try require(isRed(rgba), "red shape ink is visible at (\(x), \(y))")
        }
        for (x, y) in [(28, 48), (15, 62), (112, 92), (40, 94)] {
            let rgba = try pixel(in: exported, x: x, y: y)
            try require(isWhite(rgba), "shape gap or interior stays clear at (\(x), \(y))")
        }

        let testPoint = CGPoint(x: 42, y: 24)
        document.beginShape(at: CGPoint(x: 34, y: 24), color: blue, width: 6, shape: .line, lineStyle: .solid)
        document.extendStroke(to: CGPoint(x: 64, y: 24))
        document.finishStroke()
        document.markSaved(try document.pngSnapshot())
        let saved = document.contentRevision
        try require(!document.eraseAnnotation(at: CGPoint(x: 112, y: 92), tolerance: 0), "eraser ignores the empty interior of an outline")
        try require(document.contentRevision == saved, "an eraser miss preserves the content revision")
        try require(document.eraseAnnotation(at: testPoint, tolerance: 0), "eraser removes the uppermost visible shape")
        let erased = try await decodedPNG(from: document)
        let erasedPixel = try pixel(in: erased, x: 42, y: 24)
        let erasedRevision = document.contentRevision
        try require(isRed(erasedPixel), "erasing the blue line reveals the underlying red arrow")
        try require(document.hasUnsavedChanges, "erasing a saved shape makes the snip dirty")
        document.undo()
        try require(!document.hasUnsavedChanges && document.contentRevision == saved, "Undo restores the saved shape layer and revision")
        let restored = try await decodedPNG(from: document)
        let restoredPixel = try pixel(in: restored, x: 42, y: 24)
        try require(restoredPixel[0] < 20 && restoredPixel[1] < 20 && restoredPixel[2] > 235, "Undo restores the blue top layer")
        document.redo()
        try require(document.contentRevision == erasedRevision, "Redo restores the erase revision")
        try require(document.eraseAnnotation(at: CGPoint(x: 42, y: 28), tolerance: 3), "eraser can hit a nearby visible edge")
        let erasedBoth = try await decodedPNG(from: document)
        let backgroundPixel = try pixel(in: erasedBoth, x: 42, y: 24)
        try require(isWhite(backgroundPixel), "erasing both layers restores the original image")
        try require(document.strokes.count == 4, "erasing overlapping arrows and lines retains unrelated shapes")

        try document.replaceImage(image, alreadySaved: true)
        document.beginStroke(at: testPoint, color: blue, width: 4)
        document.finishStroke()
        try require(document.eraseAnnotation(at: testPoint, tolerance: 0), "eraser removes the sole mark")
        try require(document.strokes.isEmpty && document.canUndo, "erasing the last visible mark remains undoable")
        print("PASS: shape preview and raster, dashed and dotted strokes, and reversible annotation erasure")
    }

    private static func isRed(_ rgba: [UInt8]) -> Bool { rgba[0] > 235 && rgba[1] < 20 && rgba[2] < 20 }
    private static func isWhite(_ rgba: [UInt8]) -> Bool { rgba.prefix(3).allSatisfy { $0 > 235 } }

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

        let annotated = try document.pngSnapshot()
        document.undo()
        let restored = try await decodedPNG(from: document)
        let restoredTop = try pixel(in: restored, x: 22, y: 12)
        try require(!document.canUndo, "Undo removes the only stroke")
        try require(document.canRedo, "Undo puts the stroke in redo history")
        try require(!document.hasUnsavedChanges, "Undo to the opened image clears the dirty state")
        try require(restoredTop.prefix(3).allSatisfy { $0 > 235 }, "Undo restores the underlying pixels")

        let blue = CGColor(red: 0, green: 0, blue: 1, alpha: 1)
        document.beginStroke(at: CGPoint(x: 22, y: 12), color: blue, width: 8)
        document.cancelStroke()
        try require(document.canRedo, "a cancelled draft leaves redo history intact")
        document.redo()
        let reapplied = try await decodedPNG(from: document)
        let reappliedTop = try pixel(in: reapplied, x: 22, y: 12)
        try require(reappliedTop == top, "Redo restores the exact exported ink pixel")
        try require(document.canUndo && !document.canRedo, "Redo consumes the only undone stroke")
        document.markSaved(annotated)
        try require(!document.hasUnsavedChanges, "Redo restores the annotated revision recorded in the saved snapshot")

        document.beginStroke(at: CGPoint(x: 22, y: 12), color: blue, width: 8)
        document.finishStroke()
        document.undo()
        document.undo()
        try require(!document.canUndo && document.hasUnsavedChanges, "Undo before the saved revision makes the snip dirty")
        document.redo()
        try require(!document.hasUnsavedChanges, "the first Redo returns to the saved revision")
        document.redo()
        let layered = try await decodedPNG(from: document)
        let layeredTop = try pixel(in: layered, x: 22, y: 12)
        try require(layeredTop[0] < 20 && layeredTop[1] < 20 && layeredTop[2] > 235,
                    "repeated Redo restores the original layer order")
        try require(document.hasUnsavedChanges, "Redo after the saved revision makes the snip dirty")

        document.undo()
        document.beginStroke(at: CGPoint(x: 32, y: 12), color: blue, width: 8)
        document.finishStroke()
        try require(!document.canRedo, "a newly committed stroke clears redo history")
        document.undo()
        try require(document.canRedo, "the replacement stroke can be redone after Undo")
        print("PASS: native raster size, top-left ink export, and Undo/Redo")

        try document.replaceImage(image, alreadySaved: false)
        try require(!document.canUndo && !document.canRedo, "replacing the image clears stroke history")
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
        let frame = CGRect(x: 10, y: 20, width: 200, height: 100)
        let regions = OCRRegionOverlay.revealRegions(observations, in: frame)
        try require(regions.count == 3, "each OCR result is visible on the image")
        try require(regions[0].maxX < regions[1].minX, "regions stay in their original columns")
        try require(regions[0].minY == regions[1].minY && regions[0].height == regions[1].height,
                    "tall and short text on a row use matching reveals")
        try require(regions[1].maxY < regions[2].minY, "Vision's bottom-left origin maps to top-first canvas rows")
        try require(regions.allSatisfy { frame.contains($0) }, "OCR regions stay within the snip border")
        print("PASS: OCR row order, spacing, leading zeros, and canvas geometry")
    }

    private static func checkOCRTable() throws {
        let input = [
            RecognizedLine(text: "0001", bounds: CGRect(x: 0.65, y: 0.8, width: 0.14, height: 0.05)),
            RecognizedLine(text: "Missing", bounds: CGRect(x: 0.10, y: 0.4, width: 0.34, height: 0.05)),
            RecognizedLine(text: "Part", bounds: CGRect(x: 0.10, y: 0.6, width: 0.34, height: 0.05)),
            RecognizedLine(text: "0007", bounds: CGRect(x: 0.65, y: 0.6, width: 0.14, height: 0.05)),
            RecognizedLine(text: "Part", bounds: CGRect(x: 0.10, y: 0.8, width: 0.34, height: 0.05))
        ]
        let table = OCRTableClipboard.makeTable(from: input)
        try require(table?.cells == [["Part", "0001"], ["Part", "0007"], ["Missing", ""]],
                    "Copy as Table keeps row order, duplicate labels, leading zeros, and blank cells")
        try require(table?.tabSeparatedText == "Part\t0001\nPart\t0007\nMissing\t",
                    "plain-table output uses a tab for every column and one line per row")
        let formula = OCRTable(cells: [["0007", "  \u{200B}=1+1"]])
        try require(formula.tabSeparatedText == nil, "plain-table output refuses formula-like text")
        let markup = OCRTable(cells: [["<a&\">\n next"]])
        try require(markup.tabSeparatedText == nil && markup.html.contains("&lt;a&amp;&quot;&gt;"),
                    "rich-table output escapes cell markup and the plain form refuses embedded separators")
        print("PASS: OCR table alignment, exact cell content, and clipboard encoding")
    }

    private static func checkViewport() throws {
        let viewport = CGSize(width: 480, height: 280)
        let raster = CGSize(width: 1200, height: 800)
        guard let fit = SnipViewportLayout(imageSize: raster, viewport: viewport, zoom: nil, displayScale: 2),
              let actual = SnipViewportLayout(imageSize: raster, viewport: viewport, zoom: 1, displayScale: 2) else {
            throw CheckFailure("set up a Retina viewport")
        }
        let fittedFrame = fit.imageFrame(surfaceOrigin: .zero, viewportOrigin: .zero)
        try require(abs(fittedFrame.midX - 240) < 0.001 && abs(fittedFrame.midY - 140) < 0.001 &&
                    abs(fittedFrame.height - 232) < 0.001,
                    "Fit centers the raster and leaves its 24-point margins")
        try require(actual.surfaceSize == CGSize(width: 648, height: 448) && actual.usesNativePixels,
                    "100 percent on Retina maps one source pixel per backing pixel and scrolls both axes")
        let imageFrame = actual.imageFrame(surfaceOrigin: CGPoint(x: -80, y: -20), viewportOrigin: .zero)
        try require(actual.imagePoint(CGPoint(x: 44, y: 54), in: imageFrame) == CGPoint(x: 200, y: 100),
                    "Panning and zoom map a pen gesture back to the source pixel")
        print("PASS: Fit, native-pixel zoom, and scrolled ink coordinates")
    }

    private static func checkOCRWordSelection() throws {
        let original = "ID  0027   Printer   C:\\Temp\\A0.txt"
        let names = ["ID", "0027", "Printer", "C:\\Temp\\A0.txt"]
        let words = names.enumerated().map { index, text in
            OCRWord(text: text, utf16Range: (original as NSString).range(of: text),
                    bounds: CGRect(x: CGFloat(index) / 4, y: 0.4, width: 0.2, height: 0.2))
        }
        let lines = [RecognizedLine(text: original, bounds: CGRect(x: 0, y: 0.4, width: 1, height: 0.2), words: words)]
        let units = StudioOCRSelection.units(for: lines, words: true)
        let first = StudioOCRSelection.selected([0, 1], from: units, lines: lines)
        let disjoint = StudioOCRSelection.selected([1, 3], from: units, lines: lines)
        try require(first.map(\.text) == ["ID  0027"], "copying consecutive words preserves the exact recognized spacing")
        try require(disjoint.map(\.text) == ["0027", "C:\\Temp\\A0.txt"], "separated selections retain paths without including intervening words")
        print("PASS: word selection and preserving exact recognized text")
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
        let result = try await TextRecognition.recognizeLines(fixture)
        let output = result.map(\.text).joined(separator: "\n")
        try require(output == "SNIP STUDIO\nFILE 0027", "Vision returns both fixture lines in order, including leading zeros")
        let words = result[1].words
        try require(words.map(\.text) == ["FILE", "0027"] && words[0].bounds.maxX <= words[1].bounds.minX,
                    "native word snapping separates the filename label and zero-padded token")
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
