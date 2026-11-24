using System.Drawing;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckSparseOcrColumnRecovery()
    {
        var size = new Size(420, 260);
        var frame = new Rectangle(Point.Empty, size);
        var primary = new TextRecognitionResult { Available = true, RawText = "Provider original" };
        primary.Lines.AddRange([
            Run("Folder 0007", 43, 20, 72),
            Run("C++", 43, 104, 22),
            Run("Folder 0007", 43, 216, 72)
        ]);
        for (int row = 0; row < 8; row++) primary.Lines.Add(Run("11/23/2026 4:38 PM", 288, 21 + row * 28, 106));
        primary.Lines.AddRange([Run("suffix", 136, 20, 40), Run("suffix", 136, 104, 40), Run("suffix", 136, 216, 40)]);

        var plan = RecognizedTextRecovery.FindSparseColumnRecovery(primary, size, CancellationToken.None);
        Check(plan != null && plan.Column.Left > 30 && plan.Column.Right < 288 && plan.MissingRows.Count == 5,
            "Sparse text recovery ignores detached filename runs and plans only unfilled rows supported by a regularly aligned neighbor");

        var workerPrimary = new TextRecognitionResult { Available = true, RawText = primary.RawText };
        workerPrimary.Lines.AddRange(primary.Lines);
        Rectangle column = plan!.Column;
        OfflineOcrEngine.MergeSparse(workerPrimary,
            new("sparse-column", new(column.X, column.Y, column.Width, column.Height),
            [
                WorkerRun("es", 47, 1, .99),
                WorkerRun("cs", 76, 3, .94),
                WorkerRun("ja", 132, 2, .99),
                WorkerRun("ia", 132, 2, .98),
                WorkerRun("pt-BR", 159, 3, .98)
            ]),
            plan, size, CancellationToken.None);
        Check(workerPrimary.Lines.Count == primary.Lines.Count + 1 && workerPrimary.Lines.Any(line => line.Text == "pt-BR") &&
              !workerPrimary.Lines.Any(line => line.Text is "es" or "cs" or "ja" or "ia") && workerPrimary.RawText == primary.RawText,
            "Sparse worker output needs sufficient agreement and confidence and cannot decide a tied cell");

        primary.ProtectedGraphics.Add(new RectangleF(46, 158, 10, 12));
        primary.Lines.Add(Run("Existing 009", 102, 132, 62));
        var reread = new TextRecognitionResult { Available = true };
        reread.Lines.AddRange([
            Run("Folder 0077", 43, 20, 72),                 // Existing cell must stay intact.
            Run("cs", 43, 47, 11),                         // One supported row, a short exact label.
            Run("de", 43, 76, 12),                         // Neighbor support, same left anchor.
            Run("es", 45, 159, 13),                        // Protected source graphic.
            Run("tr", 43, 187, 11), Run("it", 43, 190, 11), // Ambiguous reads in one slot.
            Run("ko", 43, 132, 11),                        // A stale plan cannot fill the newly occupied slot.
            Run("shifted date", 250, 132, 31),            // Unrelated text in the right gutter.
            Run("fr", 43, 145, 11),                        // Outside a supported row.
            Run("11/23/2026 4:38 PM", 288, 133, 106)       // Already recognized neighboring column.
        ]);
        int added = RecognizedTextRecovery.MergeSparseColumnRecovery(primary, reread, plan!, frame, CancellationToken.None);
        Check(added == 2 && primary.Lines.Take(5).Select(line => line.Text).SequenceEqual(["Folder 0007", "cs", "de", "C++", "Folder 0007"]),
            "Short labels fill vacant aligned rows while existing values, duplicate labels and column order remain intact");
        Check(primary.RawText == "Provider original" && primary.Lines.Count(line => line.Text == "11/23/2026 4:38 PM") == 8 &&
              primary.Lines.Any(line => line.Text == "Existing 009") &&
              !primary.Lines.Any(line => line.Text is "es" or "tr" or "it" or "ko" or "shifted date" or "fr"),
            "Sparse recovery preserves provider output and leaves ambiguous, graphic, shifted and blank slots alone");
        Check(primary.Lines.Where(line => line.Text is "cs" or "de").All(line =>
                  plan!.MissingRows.Contains(line.RecognitionClipBounds) && line.RecognitionClipBounds.Contains(line.Bounds)),
            "Recovered runs carry a pixel-aligned clip around their own row for writeback");

        var full = new TextRecognitionResult { Available = true };
        for (int row = 0; row < 8; row++)
        {
            full.Lines.Add(Run("Complete", 43, 20 + row * 28, 46));
            full.Lines.Add(Run("Aligned", 288, 21 + row * 28, 45));
        }
        Check(RecognizedTextRecovery.FindSparseColumnRecovery(full, size, CancellationToken.None) is null,
            "A fully populated layout skips the sparse-column pass");

        var irregular = new TextRecognitionResult { Available = true };
        irregular.Lines.AddRange(primary.Lines.Take(5));
        foreach (int y in new[] { 20, 38, 70, 89, 125, 148, 198, 216 })
            irregular.Lines.Add(Run("Adjacent", 288, y, 60));
        Check(RecognizedTextRecovery.FindSparseColumnRecovery(irregular, size, CancellationToken.None) is null,
            "Irregular two-column content does not become an inferred list");

        var supported = new TextRecognitionResult { Available = true };
        for (int row = 0; row < 5; row++) supported.Lines.Add(Run("head-", 43, 20 + row * 28, 20));
        for (int row = 0; row < 3; row++) supported.Lines.Add(Run("tail", 64, 20 + row * 28, 25));
        for (int row = 0; row < 8; row++) supported.Lines.Add(Run("Aligned", 288, 21 + row * 28, 45));
        var stronger = RecognizedTextRecovery.FindSparseColumnRecovery(supported, size, CancellationToken.None);
        Check(stronger != null && stronger.LabelLeft == 43 && stronger.MissingRows.Count == 3,
            "A stronger aligned name column wins over a detached suffix that appears to have more missing rows");

        var occupied = new TextRecognitionResult { Available = true, RawText = "Preserved original provider bytes" };
        occupied.Lines.AddRange([
            Run(".x-", 43, 20, 20), Split("report. pdf", 64, 20, 2),
            Run("Iibvlc", 43, 48, 37),
            Run("Item-B", 43, 76, 40),
            Run(".y-", 43, 104, 20), Split("report. pdf", 64, 104, 4),
            Run("Asset1", 43, 132, 45),
            Run("head-", 43, 160, 20), Run("tail", 88, 160, 27),
            Run("ABC8", 43, 188, 33),
            Run("Item-B", 43, 216, 40)
        ]);
        for (int row = 0; row < 8; row++) occupied.Lines.Add(Run("11/23/2026 4:38 PM", 288, 21 + row * 28, 106));
        var review = RecognizedTextRecovery.FindSparseColumnRecovery(occupied, size, CancellationToken.None, reviewExisting: true);
        Check(review != null && review.MissingRows.Count == 0 && review.ReviewCells.Any(cell =>
                  cell.Runs.Count == 2 && cell.Runs[0].Text == ".x-") &&
              !review.ReviewCells.Any(cell => cell.Runs.Any(line => line.Text == "head-")),
            "Occupied-name review requires consecutive punctuation-adjacent runs in a proven lane, even when no rows are missing");

        occupied.ProtectedGraphics.Add(new RectangleF(65, 189, 11, 10));
        occupied.Lines.Add(Run("Later cell", 190, 132, 73));
        var unrelated = new TextRecognitionResult { Available = true };
        unrelated.Lines.AddRange([
            Run(".x-rep0rt.pdf", 43, 20, 88),
            Run("ltem-8", 43, 76, 40),
            Run(".y-report.pdf", 43, 104, 90),
            Run("AssetI", 43, 132, 45),
            Run("head-tail", 43, 160, 72),
            Run("ABCB", 43, 188, 33)
        ]);
        Check(RecognizedTextRecovery.MergeSparseColumnReview(occupied, unrelated, review!, frame, CancellationToken.None) == 0,
            "Occupied review cannot rewrite a different identifier, remove real spacing, cross a gutter, cover a graphic or use stale occupancy");

        var reviewPanel = new OfflineOcrEngine.Box(review!.Column.X, review.Column.Y, review.Column.Width, review.Column.Height);
        OfflineOcrEngine.MergeSparse(occupied, new("sparse-column", reviewPanel,
            [WorkerRun(".x-report.pdf", 20, 2, .999, 88)]), review, size, CancellationToken.None);
        Check(occupied.Lines[0].Text == ".x-", "An alternate reader needs all three views to agree before rejoining an occupied label");
        OfflineOcrEngine.MergeSparse(occupied, new("sparse-column", reviewPanel,
            [WorkerRun(".x-report.pdf", 20, 3, .999, 88), WorkerRun("libvlc", 48, 3, .999, 37)]), review, size, CancellationToken.None);
        Check(occupied.Lines[0].Text == ".x-report.pdf" && occupied.Lines[1].Text == "Iibvlc",
            "Alternate reads can rejoin a verified split label but cannot change a single already recognized glyph");

        var corroborated = new TextRecognitionResult { Available = true };
        corroborated.Lines.Add(Run("libvlc", 43, 48, 37));
        int repaired = RecognizedTextRecovery.MergeSparseColumnReview(occupied, corroborated, review!, frame, CancellationToken.None);
        Check(repaired == 1 && occupied.Lines.Take(3).Select(line => line.Text).SequenceEqual([".x-report.pdf", "libvlc", "Item-B"]) &&
              occupied.Lines.Count(line => line.Text == "Item-B") == 2 && occupied.RawText == "Preserved original provider bytes" &&
              occupied.Lines.Take(2).All(line => line.RecognitionClipBounds.Contains(line.Bounds)),
            "Corroborated occupied review rejoins identical split characters and corrects a single confusable glyph without losing duplicates or raw text");

        static TextLineRegion Run(string text, int x, int y, int width)
        {
            var bounds = new RectangleF(x, y, width, 12);
            var line = new TextLineRegion { Bounds = bounds, Text = text };
            line.Words.Add(new TextWordRegion { Bounds = bounds, Text = text });
            return line;
        }

        static TextLineRegion Split(string text, int x, int y, int punctuationGap)
        {
            var line = new TextLineRegion { Text = text, Bounds = new RectangleF(x, y, 65 + punctuationGap, 12) };
            line.Words.Add(new TextWordRegion { Text = "report.", Bounds = new RectangleF(x, y, 45, 12) });
            line.Words.Add(new TextWordRegion { Text = "pdf", Bounds = new RectangleF(x + 45 + punctuationGap, y, 20, 12) });
            return line;
        }

        static OfflineOcrEngine.Row WorkerRun(string text, int y, int agreement, double confidence, int width = 24)
        {
            var bounds = new OfflineOcrEngine.Box(43, y, width, 12);
            return new OfflineOcrEngine.Row(text, bounds, [new OfflineOcrEngine.Word(text, bounds)], confidence, agreement);
        }
    }
}
