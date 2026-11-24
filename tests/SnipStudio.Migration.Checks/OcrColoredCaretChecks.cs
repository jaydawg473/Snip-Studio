using System.Drawing;
using SnipStudio;

internal static partial class Program
{
    private static async Task CheckColoredCaretRecoveryAsync()
    {
        using var image = new Bitmap(160, 95);
        var caret = new Rectangle(85, 57, 1, 19);
        var background = Color.FromArgb(232, 232, 255);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var highlight = new SolidBrush(background);
            graphics.FillRectangle(highlight, 5, 57, 140, 19);
            graphics.FillRectangle(Brushes.Black, 10, 62, 27, 10);
            graphics.FillRectangle(Brushes.Black, 49, 61, 35, 10);
        }
        Color mark = Color.FromArgb(128, 0, 255);
        PaintCaret(mark);
        TextRecognitionResult primary = WithNumber("00071", new RectangleF(48.5f, 57, 38, 19));
        string originalRaw = primary.RawText;
        int reads = 0;
        int changed = await TextRecognition.RecoverColoredCaretAsync(image, primary, (clean, _) =>
        {
            reads++;
            bool onlyCaret = true;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    onlyCaret &= clean.GetPixel(x, y).ToArgb() ==
                        (caret.Contains(x, y) ? background : image.GetPixel(x, y)).ToArgb();
            Check(onlyCaret, "OCR retry alters exactly the proven caret pixels in its private recognition copy");
            return Task.FromResult(WithNumber("0007", new RectangleF(48.5f, 60.5f, 36, 11)));
        }, CancellationToken.None);
        Check(changed == 1 && reads == 1 && primary.Text == "QTY  0007" && primary.RawText == originalRaw &&
            primary.RecoveryPasses == 1 && primary.RecoveryRawText == "QTY  0007" &&
            primary.Lines[0].Words[1].Text == "0007" && primary.Lines[0].Bounds.Right == 84.5f &&
            primary.ProtectedGraphics.SequenceEqual(new[] { (RectangleF)caret }) &&
            image.GetPixel(caret.Left, caret.Top).ToArgb() == mark.ToArgb(),
            "Corroborated colored caret removal preserves the other digits, whitespace, raw reads, and original source pixels");

        PaintCaret(Color.Black);
        primary = WithNumber("00071", new RectangleF(48.5f, 57, 38, 19));
        changed = await TextRecognition.RecoverColoredCaretAsync(image, primary,
            (_, _) => throw new InvalidOperationException("A genuine monochrome digit must not start a caret retry"), CancellationToken.None);
        Check(changed == 0 && primary.Text == "QTY  00071" && primary.ProtectedGraphics.Count == 0,
            "A genuine trailing 1 is preserved even with geometry matching a caret");

        PaintCaret(mark);
        primary = WithNumber("00071", new RectangleF(48.5f, 57, 38, 19));
        changed = await TextRecognition.RecoverColoredCaretAsync(image, primary,
            (_, _) => Task.FromResult(WithNumber("0008", new RectangleF(48.5f, 60.5f, 36, 11))), CancellationToken.None);
        Check(changed == 0 && primary.Text == "QTY  00071" && primary.ProtectedGraphics.Count == 0,
            "A retry that changes a real digit cannot override the original OCR text");

        primary = WithNumber("00071", new RectangleF(48.5f, 57, 38, 19));
        changed = await TextRecognition.RecoverColoredCaretAsync(image, primary,
            (_, _) => Task.FromResult(WithNumber("0007", new RectangleF(48.5f, 60.5f, 38, 11))), CancellationToken.None);
        Check(changed == 0 && primary.Text == "QTY  00071" && primary.ProtectedGraphics.Count == 0,
            "A shorter OCR string cannot be substituted if its glyph region still crosses the caret");

        void PaintCaret(Color color)
        {
            for (int y = caret.Top; y < caret.Bottom; y++) image.SetPixel(caret.Left, y, color);
        }

        static TextRecognitionResult WithNumber(string digits, RectangleF bounds)
        {
            var label = new RectangleF(9.5f, 61.5f, 28.5f, 11);
            string text = "QTY  " + digits;
            var line = new TextLineRegion { Text = text, Bounds = RectangleF.Union(label, bounds) };
            line.Words.Add(new TextWordRegion { Text = "QTY", Bounds = label });
            line.Words.Add(new TextWordRegion { Text = digits, Bounds = bounds });
            var result = new TextRecognitionResult { Text = text, RawText = text, Available = true };
            result.Lines.Add(line);
            return result;
        }
    }
}
