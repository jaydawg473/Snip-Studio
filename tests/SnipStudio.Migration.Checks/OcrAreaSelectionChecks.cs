using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckOcrBlankStartSelection()
    {
        const string identifier = "00007_Aa.pdf";
        const string path = @"C:\WINDOWS\Logs\DISM\dism.log";
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage(420, 140));
        var baseline = session.GetCaptureBaseline();
        var native = new global::SnipStudio.TextRecognitionResult { Available = true };
        AddLine(identifier + "\t" + path, (identifier, 42, 29, 71), (path, 120, 29, 150));
        AddLine("unrelated sidebar", ("unrelated sidebar", 2, 47, 27));
        AddLine(identifier, (identifier, 42, 58, 71));
        AddLine("trailing bytes", ("trailing bytes", 42, 95, 80));
        native.Text = string.Join(Environment.NewLine, native.Lines.Select(line => line.Text));
        native.ProtectedGraphics.Add(new RectangleF(31, 28, 8, 12));
        var source = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, native);

        ImagePoint topLeft = new(35, 21), bottomRight = new(281, 75);
        OcrSelectionSnapshot area = session.SelectRecognizedText(source, topLeft, bottomRight);
        Check(area.Text == identifier + "\t" + path + Environment.NewLine + identifier &&
            area.LineIndices.SequenceEqual(new[] { 0, 2 }) && area.Bands.Count == 2,
            "Blank-start area drag isolates the intended column while preserving exact tabs, paths and duplicate rows");
        Check(session.PreviewRecognizedText(source, topLeft, bottomRight).SequenceEqual(area.Bands),
            "Line selection continues to supply the same sparse rows to editing and conversion");
        Check(area.WordBands.SequenceEqual(new[] { new OcrBounds(42, 29, 71, 10),
                new OcrBounds(120, 29, 150, 10), new OcrBounds(42, 58, 71, 10) }) &&
            session.PreviewRecognizedWordSelection(source, topLeft, bottomRight).SequenceEqual(area.WordBands),
            "OCR highlights follow the selected words live and after release without filling the gap between them");
        OcrSelectionSnapshot reverse = session.SelectRecognizedText(source, bottomRight, topLeft);
        Check(reverse.Text == area.Text && reverse.Bands.SequenceEqual(area.Bands),
            "Reversing a blank-start drag preserves exact selected text and row geometry");
        OcrTextReview review = session.PrepareRecognizedTextReview(source, area);
        Check(review.Lines.Select(line => line.LineIndex).SequenceEqual(new[] { 0, 2 }) &&
            review.Lines.Select(line => line.Text).SequenceEqual(new[] { identifier + "\t" + path, identifier }),
            "Area review uses exactly the selected native rows without reintroducing neighboring text");

        OcrSelectionSnapshot pathOnly = session.SelectRecognizedText(source, new(116, 22), new(280, 44));
        Check(pathOnly.Text == path && pathOnly.SelectionStart == identifier.Length + 1 &&
            pathOnly.SelectionLength == path.Length && pathOnly.Bands.SequenceEqual(new[] { new OcrBounds(120, 29, 150, 10) }),
            "A blank-start drag at a word boundary preserves the exact path range and highlights only that word");
        Check(session.SelectRecognizedText(source, new(70, 21), new(70, 76)).Text ==
            identifier + Environment.NewLine + identifier,
            "A zero-width vertical drag selects the text in that column without reaching a neighbor");
        Check(session.SelectRecognizedText(source, new(32, 34), new(32, 34)).Text.Length == 0 &&
            session.SelectRecognizedText(source, new(301, 24), new(392, 82)).Text.Length == 0,
            "Clicking a known graphic and dragging through an empty rectangle both leave selection empty");
        Check(session.SelectRecognizedText(source, new(32, 34), bottomRight).Text == area.Text,
            "Dragging from a protected file icon starts an area selection of neighboring words");
        Check(session.SelectRecognizedText(source, new(40, 34), new(55, 61)).LineIndices.SequenceEqual(new[] { 0, 1, 2 }) &&
            session.SelectRecognizedText(source, new(40, 27), new(55, 61)).LineIndices.SequenceEqual(new[] { 0, 2 }),
            "A small near-word start snaps to reading order while a diagonal blank start remains spatial");
        Check(session.SelectRecognizedText(source, new(64, 33), new(55, 61)).LineIndices.SequenceEqual(new[] { 0, 1, 2 }),
            "A new word-start drag resets area state and follows the original contiguous native reading order");
        Check(session.Revision == baseline.Revision && session.AnnotationCount == 0,
            "Area selection, review and live highlight leave the snip pixels and edit history intact");
        CheckSparseWordsInOneRow();
        CheckWideGapBetweenAdjacentOcrWords();

        void AddLine(string text, params (string Text, int X, int Y, int Width)[] words)
        {
            var line = new global::SnipStudio.TextLineRegion { Text = text, Bounds = RectangleF.Empty };
            foreach (var word in words)
            {
                var bounds = new RectangleF(word.X, word.Y, word.Width, 10);
                line.Words.Add(new global::SnipStudio.TextWordRegion { Text = word.Text, Bounds = bounds });
                line.Bounds = line.Bounds.IsEmpty ? bounds : RectangleF.Union(line.Bounds, bounds);
            }
            native.Lines.Add(line);
        }
    }

    private static void CheckSparseWordsInOneRow()
    {
        const string recognized = ".FIRST\t00007  LAST.pdf";
        ImagePoint dragFrom = new(20, 26), dragTo = new(190, 37);
        var left = new Rectangle(40, 30, 35, 12);
        var right = new Rectangle(145, 30, 30, 12);
        var mark = new Rectangle(104, 33, 11, 4);

        foreach (Point location in new Point[] { new(92, 39), new(92, 55), new(72, 39) })
        {
            using var session = new EditorSession();
            var middle = new Rectangle(location, new Size(33, 11));
            using var image = new Bitmap(240, 90);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Black, left);
                graphics.FillRectangle(Brushes.Red, middle);
                graphics.FillRectangle(Brushes.Black, right);
                graphics.FillRectangle(Brushes.Blue, mark);
            }
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            byte[] original = png.ToArray();
            session.OpenImageBytes(original);
            var baseline = session.GetCaptureBaseline();

            var line = new TextLineRegion { Text = recognized, Bounds = Rectangle.Union(Rectangle.Union(left, middle), right) };
            line.Words.Add(new TextWordRegion { Text = ".FIRST", Bounds = left });
            line.Words.Add(new TextWordRegion { Text = "00007", Bounds = middle });
            line.Words.Add(new TextWordRegion { Text = "LAST.pdf", Bounds = right });
            var result = new TextRecognitionResult { Available = true, Text = recognized };
            result.Lines.Add(line);
            var snapshot = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);

            OcrSelectionSnapshot area = session.SelectRecognizedText(snapshot, dragFrom, dragTo);
            OcrBounds[] expectedWordBands = [new(40, 30, 35, 12), new(145, 30, 30, 12)];
            Check(area.Text == ".FIRST LAST.pdf" && area.WordBands.SequenceEqual(expectedWordBands) &&
                area.Bands.SequenceEqual(new[] { new OcrBounds(40, 30, 135, 12) }) && area.SelectionLength == 0,
                "Blank-start OCR includes only words intersecting its area and exposes no false single text range");
            Check(session.PreviewRecognizedWordSelection(snapshot, dragFrom, dragTo).SequenceEqual(expectedWordBands) &&
                session.SelectRecognizedText(snapshot, dragTo, dragFrom).WordBands.SequenceEqual(expectedWordBands),
                "Sparse area highlights are exact during drag and when dragging in reverse");
            OcrSelectionSnapshot contiguous = session.SelectRecognizedText(snapshot, new(54, 34), new(154, 34));
            Check(contiguous.Text == recognized && contiguous.SelectionLength == recognized.Length,
                "A word-start span preserves native punctuation, tabs, repeated spaces, and leading zeros");

            bool reviewRefused = false;
            try { session.PrepareRecognizedTextReview(snapshot, area); }
            catch (InvalidOperationException error) when (error.Message.Contains("Select one continuous group", StringComparison.Ordinal) &&
                error.Message.Contains("Copy selection", StringComparison.Ordinal)) { reviewRefused = true; }
            Check(reviewRefused && session.Revision == baseline.Revision,
                "Review declines separated source words and explains how to edit or copy them safely");

            bool erased = false, eraseRefused = false;
            try { erased = session.DeleteRecognizedSelection(snapshot, area); }
            catch (InvalidOperationException error) when (error.Message.Contains("neighboring text", StringComparison.Ordinal))
            { eraseRefused = true; }
            byte[] after = session.RenderPreviewAsync().GetAwaiter().GetResult().PngBytes;
            if (left.IntersectsWith(middle))
                Check(eraseRefused && !erased && session.Revision == baseline.Revision &&
                    PixelHash(after) == PixelHash(original),
                    "Delete refuses a selected word mask that overlaps an unselected word and preserves the image atomically");
            else
            {
                Check(erased && !eraseRefused && CropPixelHash(after, middle) == CropPixelHash(original, middle) &&
                    CropPixelHash(after, mark) == CropPixelHash(original, mark) &&
                    Pixel(after, 50, 34) == 0xFFFFFFFF && Pixel(after, 151, 34) == 0xFFFFFFFF,
                    "A safe Delete erases the selected words and keeps every pixel of the omitted word and gap mark");
                session.Undo();
                Check(PixelHash(session.RenderPreviewAsync().GetAwaiter().GetResult().PngBytes) == PixelHash(original),
                    "One Undo restores both erased word regions and retains all original source pixels");
                session.Redo();
                Check(PixelHash(session.RenderPreviewAsync().GetAwaiter().GetResult().PngBytes) == PixelHash(after),
                    "One Redo restores the exact selected word deletion");
            }
        }
    }

    private static void CheckWideGapBetweenAdjacentOcrWords()
    {
        const string recognized = "FIRST\tLAST.pdf";
        using var session = new EditorSession();
        using var image = new Bitmap(240, 90);
        var left = new Rectangle(40, 30, 35, 12);
        var right = new Rectangle(145, 30, 30, 12);
        var mark = new Rectangle(104, 33, 11, 4);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, left);
            graphics.FillRectangle(Brushes.Black, right);
            graphics.FillRectangle(Brushes.Blue, mark);
        }

        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        byte[] original = png.ToArray();
        session.OpenImageBytes(original);
        var baseline = session.GetCaptureBaseline();
        var line = new TextLineRegion { Text = recognized, Bounds = Rectangle.Union(left, right) };
        line.Words.Add(new TextWordRegion { Text = "FIRST", Bounds = left });
        line.Words.Add(new TextWordRegion { Text = "LAST.pdf", Bounds = right });
        var result = new TextRecognitionResult { Available = true, Text = recognized };
        result.Lines.Add(line);
        var snapshot = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);

        OcrSelectionSnapshot selection = session.SelectRecognizedText(snapshot, new(54, 34), new(154, 34));
        Check(selection.Text == recognized && selection.WordBands.Count == 2 &&
            session.PrepareRecognizedTextReview(snapshot, selection).Lines[0].Text == recognized,
            "Consecutive native words preserve their exact selection and review text across a wide gap");
        Check(session.DeleteRecognizedSelection(snapshot, selection),
            "Delete succeeds for consecutive selected words separated by a wide physical gap");
        byte[] after = session.RenderPreviewAsync().GetAwaiter().GetResult().PngBytes;
        Check(CropPixelHash(after, mark) == CropPixelHash(original, mark) &&
            Pixel(after, 50, 34) == 0xFFFFFFFF && Pixel(after, 151, 34) == 0xFFFFFFFF,
            "Both consecutive words erase while the image mark in their wide physical gap stays unchanged");
    }
}
