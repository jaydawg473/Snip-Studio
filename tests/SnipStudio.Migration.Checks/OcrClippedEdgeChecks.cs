using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckClippedEdgeOcrRecovery()
    {
        Size size = new(700, 240);
        Rectangle source = new(236, 0, 143, 10);
        byte[] pixels = new byte[size.Width * size.Height];
        Array.Fill(pixels, (byte)245);
        for (int y = 0; y < source.Bottom; y++)
            Array.Fill(pixels, (byte)17, y * size.Width + source.Left, source.Width);
        foreach (Rectangle word in new[] { new Rectangle(244, 0, 22, 8), new(271, 0, 39, 8), new(314, 0, 12, 8), new(330, 0, 35, 8) })
        {
            for (int y = word.Top; y < word.Bottom; y++)
                for (int x = word.Left; x < word.Right; x++)
                    if ((x + y) % 3 == 0) pixels[y * size.Width + x] = 226;
        }

        var primary = new TextRecognitionResult
        {
            Available = true,
            Language = "en",
            RawText = "Windows diagnostic",
            RecoveryRawText = "Windows alternate diagnostic"
        };
        var clipped = new TextLineRegion { Text = "ana snaow at once!", Bounds = new RectangleF(244, 0, 121, 8) };
        clipped.Words.AddRange([
            new TextWordRegion { Text = "ana", Bounds = new RectangleF(244, 0, 22, 8) },
            new TextWordRegion { Text = "snaow", Bounds = new RectangleF(271, 0, 39, 8) },
            new TextWordRegion { Text = "at", Bounds = new RectangleF(314, 0, 12, 8) },
            new TextWordRegion { Text = "once!", Bounds = new RectangleF(330, 0, 35, 8) }
        ]);
        primary.Lines.Add(clipped);
        for (int y = 50; y <= 150; y += 50)
            primary.Lines.Add(new TextLineRegion { Text = "Neighbor unchanged", Bounds = new RectangleF(20, y, 80, 10) });

        var plan = OfflineOcrEngine.FindClippedEdgeText(primary, size, pixels, CancellationToken.None);
        Check(plan is { LineIndex: 0 } && plan.Source == source && plan.PaddedSize == new Size(151, 18),
            "A short proven dark edge phrase gets one bounded inverted reread");

        byte[] light = new byte[pixels.Length];
        Array.Fill(light, (byte)245);
        Check(OfflineOcrEngine.FindClippedEdgeText(primary, size, light, CancellationToken.None) is null,
            "White background does not schedule a clipped dark edge reread");

        clipped.RecognitionClipBounds = new RectangleF(240, 0, 125, 9);
        Check(OfflineOcrEngine.FindClippedEdgeText(primary, size, pixels, CancellationToken.None) is null,
            "A reread cannot cross an earlier recognition clip");
        clipped.RecognitionClipBounds = RectangleF.Empty;
        primary.ProtectedGraphics.Add(new RectangleF(371, 0, 5, 6));
        Check(OfflineOcrEngine.FindClippedEdgeText(primary, size, pixels, CancellationToken.None) is null,
            "Nearby known icon pixels invalidate the isolated read");
        primary.ProtectedGraphics.Clear();
        clipped.Words[0].Text = "O";
        Check(OfflineOcrEngine.FindClippedEdgeText(primary, size, pixels, CancellationToken.None) is null,
            "A detached one-letter or icon-like term rejects an otherwise viable row");
        clipped.Words[0].Text = "ana";

        var wordBoxes = new OfflineOcrEngine.Word[]
        {
            new("and", new OfflineOcrEngine.Box(12, 4, 22, 8)),
            new("shaow", new OfflineOcrEngine.Box(39, 4, 39, 8)),
            new("at", new OfflineOcrEngine.Box(82, 4, 12, 8)),
            new("once?", new OfflineOcrEngine.Box(98, 4, 35, 8))
        };
        var panel = new OfflineOcrEngine.Box(0, 0, 151, 18);
        var row = new OfflineOcrEngine.Row("and shaow at once?", new OfflineOcrEngine.Box(12, 4, 121, 8), wordBoxes, .999, 3);
        var response = new OfflineOcrEngine.Response("sparse-column", panel, [row]);
        var neighbors = primary.Lines.Skip(1).ToArray();

        Check(!OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response with { Rows = [row with { Agreement = 2 }] },
            pixels, size, CancellationToken.None), "Two local views alone cannot replace a clipped native row");
        Check(!OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response with { Panel = panel with { Height = 19 } },
            pixels, size, CancellationToken.None), "A local response outside the approved crop cannot replace text");
        Check(!OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response, new byte[pixels.Length], size, CancellationToken.None),
            "High model confidence cannot replace a word unsupported by original raster ink");

        var contradictoryWords = (OfflineOcrEngine.Word[])wordBoxes.Clone();
        contradictoryWords[0] = wordBoxes[0] with { Text = "try" };
        Check(!OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response with
            {
                Rows = [row with { Text = "try shaow at once?", Words = contradictoryWords }]
            }, pixels, size, CancellationToken.None) && ReferenceEquals(primary.Lines[0], clipped),
            "Uncorroborated word replacements retain the original text");
        var punctuationOnlyWords = (OfflineOcrEngine.Word[])wordBoxes.Clone();
        punctuationOnlyWords[0] = wordBoxes[0] with { Text = "ana" };
        punctuationOnlyWords[1] = wordBoxes[1] with { Text = "snaow" };
        Check(!OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response with
            {
                Rows = [row with { Text = "ana snaow at once?", Words = punctuationOnlyWords }]
            }, pixels, size, CancellationToken.None) && ReferenceEquals(primary.Lines[0], clipped),
            "Punctuation disagreement alone does not replace a native row");

        Check(OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response, pixels, size, CancellationToken.None) &&
            primary.Lines[0].Text == "and shaow at once?" && primary.Lines[0].Bounds == clipped.Bounds &&
            primary.Lines[0].RecognitionClipBounds == source && primary.Lines[0].Words.Zip(clipped.Words)
                .All(pair => pair.First.Bounds == pair.Second.Bounds) && primary.Lines.Skip(1).SequenceEqual(neighbors),
            "Three agreeing raster rereads correct the clipped phrase and preserve its original typo, geometry, and other rows");
        Check(primary.RawText == "Windows diagnostic" &&
            primary.RecoveryRawText == "Windows alternate diagnostic" + Environment.NewLine + row.Text &&
            primary.Text.StartsWith(row.Text + Environment.NewLine, StringComparison.Ordinal) &&
            primary.RecoveryPasses == 1 &&
            !OfflineOcrEngine.MergeClippedEdgeText(primary, plan!, response, pixels, size, CancellationToken.None),
            "Accepted reread preserves diagnostics, updates clipboard text, and is idempotent");
        completedGroups.Add("Clipped edge OCR reconstruction, source geometry, corroboration, and diagnostic preservation");
    }
}
