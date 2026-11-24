using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio;

internal static partial class Program
{
    private static async Task CheckCorroboratedOcrTimestampRecoveryAsync()
    {
        using Bitmap source = new(262, 218, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.White);
            for (int index = 0; index < 8; index++)
            {
                int y = 14 + index * 24;
                graphics.FillRectangle(Brushes.Black, 132, y + 1, 51, 8);
                graphics.FillRectangle(Brushes.Black, 190, y + 1, 4, 8);
                graphics.FillRectangle(Brushes.Black, 197, y + 2, 1, 2);
                graphics.FillRectangle(Brushes.Black, 197, y + 6, 1, 2);
                graphics.FillRectangle(Brushes.Black, 202, y + 1, 6, 8);
                graphics.FillRectangle(Brushes.Black, 214, y + 1, 4, 8);
                graphics.FillRectangle(Brushes.Black, 221, y + 1, 8, 8);
            }
        }

        var primary = TimestampReviewOriginal();
        var initial = primary.Lines.ToArray();
        var raw = primary.RawText;
        int reads = 0;
        int applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096, (crop, size, token) =>
        {
            token.ThrowIfCancellationRequested();
            reads++;
            return Task.FromResult(TimestampReviewView(crop));
        }, CancellationToken.None);
        string literal = "06/07/2026 4:38 PM";
        Check(applied == 8 && reads == 2 && primary.RecoveryPasses == 2 && primary.RawText == raw &&
            primary.RecoveryRawText.Split(Environment.NewLine).Length == 2 &&
            primary.Lines.Skip(1).All(line => line.Text == literal && line.Words.Count == 3) &&
            ReferenceEquals(primary.Lines[0], initial[0]),
            "Two distinct observed source views recover visible date punctuation and separate suffix ink, preserving names, duplicates, and raw output");

        primary = TimestampReviewOriginal();
        initial = primary.Lines.ToArray();
        reads = 0;
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096, (crop, size, token) =>
        {
            reads++;
            var view = TimestampReviewView(crop);
            if (crop.Height > 30)
                view.Lines.RemoveAll(line => line.Bounds.Top == initial[2].Bounds.Top);
            return Task.FromResult(view);
        }, CancellationToken.None);
        Check(applied == 8 && reads == 4 && primary.Lines[2].Text == literal &&
            primary.Lines[2].RecognitionClipBounds.Height < source.Height,
            "Only an uncorroborated cell receives two isolated optical scales and a cell-local replacement clip");

        primary = TimestampReviewOriginal();
        reads = 0;
        int firstColumnLeft = -1;
        bool usedPaddedCell = false;
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096, (crop, size, _) =>
        {
            reads++;
            var view = TimestampReviewView(crop);
            if (reads == 1) firstColumnLeft = crop.Left;
            if (reads == 2) view.Lines.RemoveAt(view.Lines.Count - 1);
            if (crop.Height < source.Height / 2)
            {
                usedPaddedCell = crop.Left == firstColumnLeft && size.Width == crop.Width * 2;
                view.TextAngle = usedPaddedCell ? 1.5 : 2.1;
            }
            return Task.FromResult(view);
        }, CancellationToken.None);
        Check(applied == 8 && usedPaddedCell && reads == 3 && primary.Lines[^1].Text == literal,
            "A complete column value and one padded isolated read corroborate the last cell without widening the angle limit or taking extra optical passes");

        primary = TimestampReviewOriginal();
        initial = primary.Lines.ToArray();
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096,
            (crop, size, _) => Task.FromResult(TimestampReviewView(crop, wrongDigitRow: 3)), CancellationToken.None);
        Check(applied == 7 && ReferenceEquals(primary.Lines[4], initial[4]) && primary.Lines[4].Text == "06/07/2026438 PM",
            "Two agreeing reads cannot change an existing date or clock digit");

        primary = TimestampReviewOriginal();
        initial = primary.Lines.ToArray();
        reads = 0;
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096, (crop, size, _) =>
        {
            reads++;
            return Task.FromResult(TimestampReviewView(crop, lastSuffix: reads == 2 ? "AM" : "PM"));
        }, CancellationToken.None);
        Check(applied == 7 && reads == 2 && ReferenceEquals(primary.Lines[^1], initial[^1]),
            "A disagreement between literal observed suffixes does not authorize an additional or majority read");

        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.FillRectangle(Brushes.White, 197, 16, 1, 6); // remove the two source dots in the first row
            graphics.FillRectangle(Brushes.Black, 187, 39, 1, 8); // darken the date/time gap in the second
            graphics.FillRectangle(Brushes.White, 214, 182, 16, 10); // remove the unrecognized suffix in the last
        }
        primary = TimestampReviewOriginal();
        initial = primary.Lines.ToArray();
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096,
            (crop, size, _) => Task.FromResult(TimestampReviewView(crop)), CancellationToken.None);
        Check(applied == 5 && ReferenceEquals(primary.Lines[1], initial[1]) && ReferenceEquals(primary.Lines[2], initial[2]) &&
            ReferenceEquals(primary.Lines[^1], initial[^1]),
            "Recognized punctuation without colon pixels, a touching date/time boundary, and a missing suffix image all leave source rows intact");

        primary = TimestampReviewOriginal();
        primary.ProtectedGraphics.Add(new RectangleF(170, 20, 12, 12));
        reads = 0;
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096, (crop, size, _) =>
        {
            reads++;
            return Task.FromResult(TimestampReviewView(crop));
        }, CancellationToken.None);
        Check(applied == 0 && reads == 0, "An overlapping protected graphic excludes the complete timestamp crop");

        primary = TimestampReviewOriginal();
        primary.Lines.Add(TimestampReviewLine(("Other", new RectangleF(225, 182, 19, 10))));
        applied = await OcrTimestampRecovery.RecoverAsync(source, primary, 4096, (crop, size, _) =>
        {
            reads++;
            return Task.FromResult(TimestampReviewView(crop));
        }, CancellationToken.None);
        Check(applied == 0 && reads == 0, "A neighboring recognized row fragment cannot donate its ink as a timestamp suffix");
        completedGroups.Add("Source-backed timestamp separators and suffixes, duplicate rows, and word-level conflicts");
    }

    private static TextRecognitionResult TimestampReviewOriginal()
    {
        var value = new TextRecognitionResult { Available = true, Language = "en-US", RawText = "original exact diagnostics" };
        value.Lines.Add(TimestampReviewLine(("Name leading 0008", new RectangleF(12, 14, 74, 10))));
        for (int index = 0; index < 8; index++)
        {
            int y = 14 + index * 24;
            value.Lines.Add(index == 7
                ? TimestampReviewLine(("06/07/2026", new RectangleF(130, y, 56, 10)), ("438", new RectangleF(190, y, 20, 10)))
                : TimestampReviewLine(("06/07/2026438", new RectangleF(130, y, 80, 10)), ("PM", new RectangleF(214, y, 16, 10))));
        }
        value.Text = string.Join(Environment.NewLine, value.Lines.Select(line => line.Text));
        return value;
    }

    private static TextRecognitionResult TimestampReviewView(Rectangle crop, int wrongDigitRow = -1, string lastSuffix = "PM")
    {
        var value = new TextRecognitionResult { Available = true, Language = "en-US", RawText = "retained alternate diagnostic" };
        for (int index = 0; index < 8; index++)
        {
            int y = 14 + index * 24;
            var row = TimestampReviewLine(("06/07/2026", new RectangleF(130, y, 56, 10)),
                (index == wrongDigitRow ? "4:39" : "4:38", new RectangleF(190, y, 20, 10)),
                (index == 7 ? lastSuffix : "PM", new RectangleF(214, y, 16, 10)));
            if (((RectangleF)crop).Contains(row.Bounds)) value.Lines.Add(row);
        }
        return value;
    }

    private static TextLineRegion TimestampReviewLine(params (string Text, RectangleF Bounds)[] words)
    {
        var row = new TextLineRegion { Text = string.Join(" ", words.Select(word => word.Text)),
            Bounds = words.Select(word => word.Bounds).Aggregate(RectangleF.Union) };
        foreach (var word in words) row.Words.Add(new TextWordRegion { Text = word.Text, Bounds = word.Bounds });
        return row;
    }
}
