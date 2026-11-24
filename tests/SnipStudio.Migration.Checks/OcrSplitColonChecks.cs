using System.Drawing;
using SnipStudio;

internal static partial class Program
{
    private static async Task CheckSplitOcrColonRecoveryAsync()
    {
        using var image = new Bitmap(450, 160);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            for (int index = 0; index < 3; index++)
            {
                int dotTop = 23 + 28 * index;
                graphics.FillRectangle(Brushes.Black, 288, dotTop, 2, 2);
                graphics.FillRectangle(Brushes.Black, 288, dotTop + 7, 2, 2);
            }
        }

        TextRecognitionResult primary = Source();
        int reads = 0;
        int changed = await OcrSplitColonRecovery.RecoverAsync(image, primary, 4096, (crop, _, _) =>
        {
            reads++;
            int index = (crop.Top - 12) / 28;
            return Task.FromResult(Alternative(index, "0004" + index));
        }, CancellationToken.None);
        Check(changed == 3 && reads == 6 && primary.RecoveryPasses == 6 &&
            primary.Lines.Select(line => line.Text).SequenceEqual(new[]
            {
                "Other column 000Z",
                "Batch: complete 00040",
                "Batch: complete 00041",
                "Batch: complete 00042"
            }, StringComparer.Ordinal) &&
            primary.Lines.Skip(1).All(line => line.Words.Count == 3 && line.RecognitionClipBounds.Contains(line.Bounds)) &&
            primary.RawText == "Original diagnostic" && primary.RecoveryRawText.Split(Environment.NewLine).Length == 6,
            "Two corroborated views and original colon ink merge split text in place without changing another column or leading zeros");

        primary = Source();
        reads = 0;
        changed = await OcrSplitColonRecovery.RecoverAsync(image, primary, 4096, (crop, size, _) =>
        {
            reads++;
            int index = (crop.Top - 12) / 28;
            string digits = index == 0 && size.Width == crop.Width * 2 ? "00070" : "0004" + index;
            return Task.FromResult(Alternative(index, digits));
        }, CancellationToken.None);
        Check(changed == 2 && reads == 6 && primary.Lines.Any(line => line.Text == "• complete 00040") &&
            primary.Lines.Any(line => line.Text == "Batch.") && !primary.Lines.Any(line => line.Text.Contains("00070")),
            "A colon reread cannot silently change digits despite exact geometry");

        primary = Source();
        primary.Lines[1].Text = "• complete  00040";
        changed = await OcrSplitColonRecovery.RecoverAsync(image, primary, 4096, (crop, _, _) =>
        {
            int index = (crop.Top - 12) / 28;
            return Task.FromResult(Alternative(index, "0004" + index));
        }, CancellationToken.None);
        Check(changed == 2 && primary.Lines.Any(line => line.Text == "• complete  00040"),
            "A colon reread cannot discard existing whitespace between recognized words");

        primary = Source();
        using (Graphics graphics = Graphics.FromImage(image))
            graphics.FillRectangle(Brushes.White, 288, 30, 2, 2);
        changed = await OcrSplitColonRecovery.RecoverAsync(image, primary, 4096, (crop, _, _) =>
        {
            int index = (crop.Top - 12) / 28;
            return Task.FromResult(Alternative(index, "0004" + index));
        }, CancellationToken.None);
        Check(changed == 2 && primary.RecoveryPasses == 4 && primary.Lines.Any(line => line.Text == "• complete 00040"),
            "A missing lower dot keeps the original split rows even if the OCR engine would agree");

        primary = Source();
        primary.ProtectedGraphics.Add(new RectangleF(288, 78, 5, 10));
        changed = await OcrSplitColonRecovery.RecoverAsync(image, primary, 4096,
            (_, _, _) => throw new InvalidOperationException("Protected and nonrepeated pairs must not be reread"), CancellationToken.None);
        Check(changed == 0 && primary.Lines.Count == 7,
            "Protected source pixels and isolated pairs block recovery before any provider call");

        static TextRecognitionResult Source()
        {
            var result = new TextRecognitionResult { Available = true, RawText = "Original diagnostic" };
            result.Lines.Add(Line(("Other column 000Z", new RectangleF(19, 18, 119, 18))));
            for (int index = 0; index < 3; index++)
            {
                int y = 18 + 28 * index;
                result.Lines.Add(Line(("•", new RectangleF(288, y + 5, 2, 2)),
                    ("complete", new RectangleF(297, y, 66, 18)), ("0004" + index, new RectangleF(369, y + 1, 51, 13))));
                result.Lines.Add(Line(("Batch.", new RectangleF(240, y + 1, 50, 13))));
            }
            return result;
        }

        static TextRecognitionResult Alternative(int index, string digits)
        {
            int y = 18 + 28 * index;
            var result = new TextRecognitionResult { Available = true, RawText = "Independent diagnostic" };
            result.Lines.Add(Line(("Batch:", new RectangleF(239.5f, y + .5f, 50.5f, 14)),
                ("complete", new RectangleF(297, y, 66, 18)), (digits, new RectangleF(369, y + 1, 51, 13))));
            return result;
        }

        static TextLineRegion Line(params (string Text, RectangleF Bounds)[] words)
        {
            var line = new TextLineRegion
            {
                Text = string.Join(" ", words.Select(word => word.Text)),
                Bounds = words.Select(word => word.Bounds).Aggregate(RectangleF.Union)
            };
            foreach (var word in words)
                line.Words.Add(new TextWordRegion { Text = word.Text, Bounds = word.Bounds });
            return line;
        }
    }
}

