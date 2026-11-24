using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckRecognizedTextReviewAsync()
    {
        using EditorSession session = new();
        using var sourceImage = new Bitmap(510, 150);
        using var png = new MemoryStream();
        var native = new global::SnipStudio.TextRecognitionResult { Available = true };
        RectangleF prefix;
        RectangleF suffix;
        RectangleF leadingDigits;
        RectangleF firstSelection;
        RectangleF secondSelection;
        using (Graphics graphics = Graphics.FromImage(sourceImage))
        {
            graphics.Clear(Color.White);
            prefix = DrawWord("KEEP", 20, 30);
            leadingDigits = DrawWord("0008", 124, 30);
            firstSelection = DrawWord("TARGET", 205, 30, 0xFF2450B0);
            secondSelection = DrawWord("SOURCE", 20, 91, 0xFFB02438);
            suffix = DrawWord("TAIL", 330, 91);
            AddLine(("KEEP", prefix), ("0008", leadingDigits), ("TARGET", firstSelection));
            AddLine(("SOURCE", secondSelection), ("TAIL", suffix));

            RectangleF DrawWord(string text, int x, int y, uint argb = 0xFF101010)
            {
                string rtf = session.CreateTextRtf(text, "Segoe UI", 25, TextFontStyle.Regular, argb);
                var position = new PointF(x, y);
                global::SnipStudio.RichTextLayout.Draw(graphics, rtf, 400, position);
                RectangleF result = global::SnipStudio.RichTextLayout.Measure(rtf, 400).InkBounds;
                result.Offset(position);
                return result;
            }
        }
        native.Text = "KEEP  0008  TARGET\r\nSOURCE  TAIL";
        sourceImage.Save(png, ImageFormat.Png);
        session.OpenImageBytes(png.ToArray());
        var baseline = session.GetCaptureBaseline();
        var recognized = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, native);
        var picked = session.SelectRecognizedText(recognized, Center(firstSelection), Center(secondSelection));
        var review = session.PrepareRecognizedTextReview(recognized, picked);
        Check(review.Lines.Select(line => line.Text).SequenceEqual(new[] { "TARGET", "SOURCE" }, StringComparer.Ordinal),
            "Translation scope contains only the selected words across lines");
        var reverse = session.SelectRecognizedText(recognized, Center(secondSelection), Center(leadingDigits));
        var reverseReview = session.PrepareRecognizedTextReview(recognized, reverse);
        Check(reverseReview.Lines.Select(line => line.Text).SequenceEqual(new[] { "0008  TARGET", "SOURCE" }, StringComparer.Ordinal),
            "Reverse selection preserves native reading order, leading zeros, and exact separators");
        Check(session.PrepareRecognizedTextReview(recognized).Lines.Select(line => line.Text).SequenceEqual(
            new[] { "KEEP  0008  TARGET", "SOURCE  TAIL" }, StringComparer.Ordinal),
            "Whole-snippet review retains all native text and whitespace");

        string originalHash = PixelHash(png.ToArray());
        OcrConversionResult invalid = await session.ApplyRecognizedTextReviewAsync(review, ["I\nI"]);
        Check(!invalid.Applied && session.Revision == baseline.Revision && session.AnnotationCount == 0,
            "A different edited line count leaves the snip and Undo history intact");
        OcrConversionResult unchanged = await session.ApplyRecognizedTextReviewAsync(review, ["TARGET", "SOURCE"]);
        Check(!unchanged.Applied && session.Revision == baseline.Revision,
            "Opening and applying an unchanged review does not redraw the recognized words");

        native.ProtectedGraphics.Add(secondSelection);
        var protectedRecognition = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, native);
        var protectedPicked = session.SelectRecognizedText(protectedRecognition, Center(firstSelection), Center(secondSelection));
        var protectedReview = session.PrepareRecognizedTextReview(protectedRecognition, protectedPicked);
        OcrConversionResult blocked = await session.ApplyReviewedTranslationAsync(protectedReview, ["I", "I"]);
        Check(!blocked.Applied && blocked.Message.Contains("Line 2", StringComparison.Ordinal) && session.Revision == baseline.Revision && session.AnnotationCount == 0,
            "A protected word region prevents all reviewed lines from applying");

        OcrConversionResult applied = await session.ApplyReviewedTranslationAsync(review, ["I", "I"]);
        Check(applied.Applied && applied.ConvertedLineCount == 2 && session.Revision != baseline.Revision,
            "Both reviewed partial lines apply in one operation");
        using var appliedPng = new MemoryStream((await session.RenderPreviewAsync()).PngBytes);
        using var appliedBitmap = new Bitmap(appliedPng);
        Check(SameRegion(prefix, appliedBitmap) && SameRegion(leadingDigits, appliedBitmap) && SameRegion(suffix, appliedBitmap),
            "Pixels around unselected prefix and suffix words remain exactly unchanged");
        Check(!SameRegion(firstSelection, appliedBitmap) && !SameRegion(secondSelection, appliedBitmap),
            "Both selected word regions changed in the rendered snip");
        Check(!session.IsOcrSnapshotCurrent(review.Source), "A committed source edit makes the old review stale");
        session.Undo();
        Check(originalHash == PixelHash((await session.RenderPreviewAsync()).PngBytes) && session.AnnotationCount == 0,
            "Undo restores the full image from both edited regions");
        OcrConversionResult corrected = await session.ApplyRecognizedTextReviewAsync(review, ["I", "SOURCE"]);
        Check(corrected.Applied && corrected.ConvertedLineCount == 1 && session.GetAnnotation(corrected.LastAnnotationIndex)?.Text?.Text == "I",
            "Batch corrections omit unchanged rows and keep edited text exact");
        session.Undo();
        Check(originalHash == PixelHash((await session.RenderPreviewAsync()).PngBytes),
            "One Undo restores a batch correction");

        OcrConversionResult converted = await session.ConvertRecognizedTextReviewAsync(review);
        TextAnnotationSnapshot? firstNote = session.GetAnnotation(converted.LastAnnotationIndex - 1)?.Text;
        TextAnnotationSnapshot? secondNote = session.GetAnnotation(converted.LastAnnotationIndex)?.Text;
        Check(converted.Applied && converted.ConvertedLineCount == 2 &&
            firstNote?.Text == "TARGET" && secondNote?.Text == "SOURCE",
            "Direct conversion turns only the selected words into editable text");
        Check(firstNote is { } blue && secondNote is { } red &&
            (blue.Argb & 255) > ((blue.Argb >> 16) & 255) + 32 &&
            ((red.Argb >> 16) & 255) > (red.Argb & 255) + 32 &&
            AnchoredToSource(blue.InkBounds, firstSelection) && AnchoredToSource(red.InkBounds, secondSelection),
            "Selected conversion retains separate foreground colors and anchors the editable ink to the source words");
        using var convertedPng = new MemoryStream((await session.RenderPreviewAsync()).PngBytes);
        using var convertedBitmap = new Bitmap(convertedPng);
        Check(SameRegion(prefix, convertedBitmap) && SameRegion(leadingDigits, convertedBitmap) && SameRegion(suffix, convertedBitmap),
            "Direct conversion leaves all other words and their source pixels intact");
        session.Undo();
        Check(originalHash == PixelHash((await session.RenderPreviewAsync()).PngBytes) && session.AnnotationCount == 0,
            "One Undo restores the exact source after converting selected words");
        await CheckQuickRedactAsync(session, png.ToArray());

        bool SameRegion(RectangleF bounds, Bitmap rendered)
        {
            Rectangle region = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                    if (sourceImage.GetPixel(x, y) != rendered.GetPixel(x, y)) return false;
            return true;
        }

        void AddLine(params (string Text, RectangleF Bounds)[] words)
        {
            var line = new global::SnipStudio.TextLineRegion
            {
                Text = string.Join("  ", words.Select(word => word.Text)),
                Bounds = words.Select(word => word.Bounds).Aggregate(RectangleF.Union)
            };
            foreach (var word in words)
                line.Words.Add(new global::SnipStudio.TextWordRegion { Text = word.Text, Bounds = word.Bounds });
            native.Lines.Add(line);
        }

        static ImagePoint Center(RectangleF bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        static bool AnchoredToSource(ImageRectangle ink, RectangleF source) =>
            Math.Abs(ink.X - source.X) <= .5 && Math.Abs(ink.Y - source.Y) <= .5;
    }

    private static async Task CheckQuickRedactAsync(EditorSession session, byte[] sourceImage)
    {
        var result = new global::SnipStudio.TextRecognitionResult { Available = true };
        var contacts = new global::SnipStudio.TextLineRegion
        {
            Text = "<support@example.com> (585) 555-0123",
            Bounds = new(20, 10, 285, 12)
        };
        contacts.Words.Add(new() { Text = "<support@example.com>", Bounds = new(20, 10, 140, 12) });
        contacts.Words.Add(new() { Text = "(585)", Bounds = new(200, 10, 40, 12) });
        contacts.Words.Add(new() { Text = "555-0123", Bounds = new(245, 10, 60, 12) });
        result.Lines.Add(contacts);
        var versions = new global::SnipStudio.TextLineRegion
        {
            Text = "10.0.26100.8737  00001234",
            Bounds = new(20, 49, 200, 12)
        };
        versions.Words.Add(new() { Text = "10.0.26100.8737", Bounds = new(20, 49, 108, 12) });
        versions.Words.Add(new() { Text = "00001234", Bounds = new(140, 49, 80, 12) });
        result.Lines.Add(versions);
        var unlocated = new global::SnipStudio.TextLineRegion { Text = "Contact ghost@example.com" };
        unlocated.Words.Add(new() { Text = "Contact", Bounds = new(20, 70, 62, 10) });
        result.Lines.Add(unlocated);
        var baseline = session.GetCaptureBaseline();
        var snapshot = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);
        OcrRedactionSuggestions suggestions = session.SuggestOcrRedactions(snapshot);
        Check(suggestions.EmailAddresses.SequenceEqual(new[] { new ImageRectangle(17, 7, 146, 18) }) &&
            suggestions.PhoneNumbers.SequenceEqual(new[] { new ImageRectangle(197, 7, 111, 18) }),
            "Quick redact maps contact formats to padded source bounds without taking version numbers or unlocated characters");
        PreviewSnapshot flattened = await session.RenderPreviewAsync();
        RedactedImage masked = await EditorSession.RenderRedactedCopyAsync(flattened, suggestions.EmailAddresses);
        using var maskedBytes = new MemoryStream(masked.PngBytes.ToArray());
        using var maskedImage = new Bitmap(maskedBytes);
        Check(maskedImage.GetPixel(30, 18).ToArgb() == Color.Black.ToArgb() &&
            maskedImage.GetPixel(220, 18).ToArgb() == Color.White.ToArgb() &&
            PixelHash(flattened.PngBytes) == PixelHash((await session.RenderPreviewAsync()).PngBytes) &&
            session.Revision == baseline.Revision,
            "Selected quick redaction category covers its opaque export pixels while the other category and original stay untouched");
        session.OpenImageBytes(sourceImage);
        Expect<InvalidOperationException>(() => session.SuggestOcrRedactions(snapshot), "Quick redact rejects a snapshot from an earlier image");
    }
}
