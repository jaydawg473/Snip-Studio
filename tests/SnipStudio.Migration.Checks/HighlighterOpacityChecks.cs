using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckInkOpacityAsync(string output)
    {
        const uint ink = 0xFF000000;
        const byte customAlpha = 160;
        using var paper = new Bitmap(220, 80);
        using (Graphics graphics = Graphics.FromImage(paper)) graphics.Clear(Color.White);
        using var source = new MemoryStream();
        paper.Save(source, ImageFormat.Png);
        using EditorSession editor = new();
        editor.OpenImageBytes(source.ToArray());

        ImagePoint[] stroke = [new(10, 12), new(60, 12)];
        editor.AddStroke(stroke, ink, 10, highlighter: true);
        Check(IsGray(await PixelAt(editor, 20, 12), 175), "Freehand highlighter without an opacity retains the original alpha");
        editor.Undo();
        editor.AddStroke(stroke, ink, 10, highlighter: true, highlighterAlpha: customAlpha);
        Check(IsGray(await PixelAt(editor, 20, 12), 95), "Freehand highlighter honors the selected opacity");
        string strokePixels = PixelHash((await editor.RenderPreviewAsync()).PngBytes);
        Check(editor.MoveAnnotation(0, new ImagePoint(0, 14)) &&
            IsGray(await PixelAt(editor, 20, 12), 255) && IsGray(await PixelAt(editor, 20, 26), 95),
            "Moving a freehand highlighter preserves its opacity and reveals the original pixels");
        editor.Undo();
        Check(PixelHash((await editor.RenderPreviewAsync()).PngBytes) == strokePixels,
            "Undo restores the exact highlighter pixels");

        var word = new global::SnipStudio.TextWordRegion { Text = "Sample", Bounds = new(10, 44, 42, 12) };
        var line = new global::SnipStudio.TextLineRegion { Text = word.Text, Bounds = word.Bounds };
        line.Words.Add(word);
        var recognition = new global::SnipStudio.TextRecognitionResult { Available = true, Text = word.Text };
        recognition.Lines.Add(line);
        var baseline = editor.GetCaptureBaseline();
        var recognized = new OcrSnapshot(editor, baseline.ImageIdentity, baseline.Revision, recognition);
        ImagePoint first = new(14, 48), last = new(45, 48);
        Check(editor.HighlightRecognizedText(recognized, first, last, ink) &&
            IsGray(await PixelAt(editor, 20, 48), 175), "Snapped highlights retain the original alpha by default");
        editor.Undo();
        Check(editor.HighlightRecognizedText(recognized, first, last, ink, highlighterAlpha: customAlpha) &&
            IsGray(await PixelAt(editor, 20, 48), 95), "Snapped highlights honor the selected opacity");

        Check(editor.AddShape(AnnotationShape.Line, new(10, 68), new(60, 68), ink, 6, outlineOpacity: 40) &&
            editor.GetAnnotation(2)?.Shape is { OutlineArgb: ink, OutlineOpacity: 40 } &&
            IsGray(await PixelAt(editor, 20, 68), 153),
            "Drawing a shape keeps the selected opacity separate from the stored color");

        string path = Path.Combine(output, "highlighter-opacity.snipstudio");
        string finishedPixels = PixelHash((await editor.RenderPreviewAsync()).PngBytes);
        await editor.SaveEditableProjectAsync(editor.PrepareEditableProjectSaveTarget(path));
        Check(BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8, 4)) == 3,
            "Custom freehand opacity is marked as an annotation-style project");
        using EditorSession reopened = new();
        await reopened.OpenEditableProjectAsync(path);
        Check(PixelHash((await reopened.RenderPreviewAsync()).PngBytes) == finishedPixels,
            "Editable projects preserve the exact freehand and snapped highlight pixels");

        using EditorSession snapEditor = new();
        snapEditor.OpenImageBytes(source.ToArray());
        var left = new global::SnipStudio.TextWordRegion { Text = "Sample", Bounds = new(40, 20, 50, 10) };
        var right = new global::SnipStudio.TextWordRegion { Text = "Column", Bounds = new(142, 20, 42, 10) };
        var columns = new global::SnipStudio.TextLineRegion
        {
            Text = "Sample Column",
            Bounds = RectangleF.Union(left.Bounds, right.Bounds)
        };
        columns.Words.Add(left);
        columns.Words.Add(right);
        var columnRecognition = new global::SnipStudio.TextRecognitionResult { Available = true, Text = columns.Text };
        columnRecognition.Lines.Add(columns);
        var snapBaseline = snapEditor.GetCaptureBaseline();
        var snapshot = new OcrSnapshot(snapEditor, snapBaseline.ImageIdentity, snapBaseline.Revision, columnRecognition);
        ImagePoint outsideLeft = new(25, 25), outsideRight = new(106, 25);
        OcrBounds[] leftBand = [new(38, 18, 54, 14)];
        OcrBounds[] bothBands = [leftBand[0], new(140, 18, 46, 14)];

        Check(!snapEditor.HighlightRecognizedText(snapshot, outsideLeft, outsideRight, ink) &&
            snapEditor.PreviewSnappedHighlight(snapshot, outsideLeft, outsideRight, 8).SequenceEqual(leftBand) &&
            snapEditor.PreviewSnappedHighlight(snapshot, outsideRight, outsideLeft, 8).SequenceEqual(leftBand),
            "A highlighter stroke with both endpoints outside a word snaps to its full bounds in either direction");
        Check(snapEditor.PreviewSnappedHighlight(snapshot, outsideLeft, new(199, 25), 8).SequenceEqual(bothBands) &&
            snapEditor.PreviewSnappedHighlight(snapshot, new(50, 25), new(153, 25), 8).SequenceEqual(bothBands),
            "Snapped highlighter leaves a wide column gap open for endpoint and stroke-based matches");
        Check(snapEditor.PreviewSnappedHighlight(snapshot, new(25, 34), new(106, 34), 8).Count == 0 &&
            snapEditor.ApplySnappedHighlight(snapshot, new(25, 34), new(106, 34), 8, ink) == null && snapEditor.AnnotationCount == 0,
            "A highlighter stroke through blank space does not add a snapped annotation");

        var refreshed = snapEditor.ApplySnappedHighlight(snapshot, outsideLeft, outsideRight, 8, ink, customAlpha);
        Check(refreshed != null && !snapEditor.IsOcrSnapshotCurrent(snapshot) && snapEditor.IsOcrSnapshotCurrent(refreshed) &&
            refreshed.Text == snapshot.Text &&
            snapEditor.PreviewSnappedHighlight(refreshed, outsideLeft, outsideRight, 8).SequenceEqual(leftBand),
            "Applying a snapped highlight refreshes its OCR revision while preserving text and word geometry");
        Check(snapEditor.AnnotationCount == 1 && IsGray(await PixelAt(snapEditor, 40, 25), 95) &&
            IsGray(await PixelAt(snapEditor, 89, 25), 95) && IsGray(await PixelAt(snapEditor, 35, 25), 255) &&
            IsGray(await PixelAt(snapEditor, 145, 25), 255),
            "Snapped highlight uses the preview bounds and selected opacity without coloring nearby blank space or words");

        static async Task<Color> PixelAt(EditorSession editor, int x, int y)
        {
            using var stream = new MemoryStream((await editor.RenderPreviewAsync()).PngBytes);
            using var rendered = new Bitmap(stream);
            return rendered.GetPixel(x, y);
        }

        static bool IsGray(Color color, int expected) =>
            color.A == 255 && Math.Abs(color.R - expected) <= 1 && color.R == color.G && color.G == color.B;
    }
}
