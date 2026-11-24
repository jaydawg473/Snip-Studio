#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckFragmentedOcrRowsAsync()
    {
        Size size = new(130, 90);
        var pixels = new byte[size.Width * size.Height];
        PaintRows(44);

        TextLineRegion first = Fragment("TEST-", 12, 8, 30);
        TextLineRegion second = Fragment("0007 .pdf", 44, 8, 51);
        TextLineRegion third = Fragment("TEST-0007", 12, 27, 66);
        TextLineRegion fourth = Fragment(". pdf", 84, 27, 27);
        TextLineRegion intact = Fragment("REAL 00071", 12, 46, 83);
        TextLineRegion protectedLine = Fragment("QTY 0007", 12, 65, 83);
        var primary = new TextRecognitionResult { Text = "original", RawText = "raw", RecoveryRawText = "prior" };
        primary.Lines.AddRange([second, first, fourth, intact, third, protectedLine]);
        primary.ProtectedGraphics.Add(new RectangleF(93, 64, 2, 13));
        TextLineRegion[] original = primary.Lines.ToArray();
        int reads = 0;

        Check(!await Run(crop => crop.Top == 4 ? Reply(crop) : null) && reads == 2 &&
            primary.Lines.SequenceEqual(original) && primary.Text == "original" && primary.RecoveryRawText == "prior",
            "A failed late row reread leaves every original fragment and diagnostic untouched");

        reads = 0;
        Check(!await Run(crop => Reply(crop, disagreement: true)) && reads == 1 && primary.Lines.SequenceEqual(original),
            "Competing close OCR readings are left unresolved");

        reads = 0;
        Check(!await Run(crop => Reply(crop, wrongDigit: true)) && reads == 1 && primary.Lines.SequenceEqual(original),
            "The focused read cannot change the native digit sequence");

        PaintRows(74);
        reads = 0;
        Check(!await Run(crop => Reply(crop)) && reads == 0 && primary.Lines.SequenceEqual(original),
            "A physical table gutter vetoes whole-row recovery");

        PaintRows(44);
        reads = 0;
        primary.ProtectedGraphics.Add(new RectangleF(15, 7, 4, 13));
        Check(!await Run(crop => Reply(crop)) && reads == 0 && primary.Lines.SequenceEqual(original),
            "A protected graphic inside a suspected row stops rereading");
        primary.ProtectedGraphics.RemoveAt(1);

        using var cancellation = new CancellationTokenSource();
        bool canceled = false;
        try
        {
            await OfflineOcrEngine.RecoverFragmentedRowsAsync(primary, size, pixels, (crop, _, _) =>
            {
                if (crop.Top > 4) cancellation.Cancel();
                return Task.FromResult<OfflineOcrEngine.Response?>(Reply(crop));
            }, cancellation.Token);
        }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && primary.Lines.SequenceEqual(original) && primary.Text == "original",
            "Canceling the final focused read cannot expose a partially repaired result");

        Check(await Run(crop => Reply(crop)) && reads == 2 &&
            primary.Lines.Select(line => line.Text).SequenceEqual(["TEST-0007.pdf", "TEST-0007 .pdf", "REAL 00071", "QTY 0007"]) &&
            primary.Text == string.Join(Environment.NewLine, primary.Lines.Select(line => line.Text)) &&
            primary.Lines[0].Bounds == new RectangleF(12, 8, 83, 9) &&
            primary.Lines[1].RecognitionClipBounds == new RectangleF(9, 23, 109, 17) &&
            ReferenceEquals(primary.Lines[2], intact) && ReferenceEquals(primary.Lines[3], protectedLine) &&
            primary.ProtectedGraphics.Count == 1 && primary.RawText == "raw" && primary.RecoveryPasses == 2,
            "Focused row consensus preserves order, literal spacing, leading zeroes, source geometry, and the protected caret");

        Task<bool> Run(Func<Rectangle, OfflineOcrEngine.Response?> read) =>
            OfflineOcrEngine.RecoverFragmentedRowsAsync(primary, size, pixels, (crop, clipped, _) =>
            {
                reads++;
                Check(clipped.Length == crop.Width * crop.Height && clipped[4 * crop.Width + 3] == 20,
                    "Focused OCR receives the original source row pixels");
                return Task.FromResult(read(crop));
            }, CancellationToken.None);

        void PaintRows(int secondColumn)
        {
            Array.Fill(pixels, (byte)255);
            Paint(12, 8, 30);
            Paint(secondColumn, 8, 95 - secondColumn);
            Paint(12, 27, 66);
            Paint(84, 27, 27);
            Paint(12, 46, 83);
            Paint(12, 65, 83);
            Array.Fill(pixels, (byte)20, 19 * size.Width + 76, 4);
        }

        void Paint(int left, int top, int width)
        {
            for (int y = top; y < top + 9; y++)
                Array.Fill(pixels, (byte)20, y * size.Width + left, width);
        }
    }

    private static TextLineRegion Fragment(string text, int left, int top, int width) =>
        new() { Text = text, Bounds = new RectangleF(left, top, width, 9) };

    private static OfflineOcrEngine.Response Reply(Rectangle crop, bool disagreement = false, bool wrongDigit = false)
    {
        var panel = new OfflineOcrEngine.Box(0, 0, crop.Width, crop.Height);
        var first = new OfflineOcrEngine.Word(wrongDigit ? "TEST-0008.pdf" : "TEST-0007.pdf", new OfflineOcrEngine.Box(3, 4, 83, 9));
        OfflineOcrEngine.Word[] words = crop.Top == 4 ? [first] :
            [new("TEST-0007", new(3, 4, 66, 9)), new(".pdf", new(75, 4, 27, 9))];
        var winner = new OfflineOcrEngine.Row(string.Join(" ", words.Select(word => word.Text)), panel, words, .99, 5);
        if (!disagreement) return new OfflineOcrEngine.Response("gap-anchor", panel, [winner]);
        var other = winner with { Text = "TEST-0007 .pdf", Agreement = 4 };
        return new OfflineOcrEngine.Response("gap-anchor", panel, [winner, other]);
    }
}
