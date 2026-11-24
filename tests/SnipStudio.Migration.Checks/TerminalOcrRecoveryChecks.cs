using System.Drawing;
using SnipStudio;
using SnipStudio.WinUI.Backend;
using Box = SnipStudio.WinUI.Backend.OfflineOcrEngine.Box;
using Row = SnipStudio.WinUI.Backend.OfflineOcrEngine.Row;
using Word = SnipStudio.WinUI.Backend.OfflineOcrEngine.Word;

internal static partial class Program
{
    private static void CheckTerminalOcrRecovery()
    {
        var size = new Size(400, 120);
        var panel = new Box(8, 0, 392, 120);
        var bounds = new Box(9, 56, 359, 10);
        byte[] pixels = new byte[size.Width * size.Height];
        Paint(new Rectangle(9, 57, 16, 7));
        Paint(new Rectangle(176, 58, 23, 6));
        Paint(new Rectangle(364, 56, 4, 10));
        Row progress = new("[==5.0%]", bounds, [new Word("[==5.0%]", bounds)], .774, 3);
        Row[] rows =
        [
            FixedPitch(10, "The", "operation", "completed", "successfully."),
            FixedPitch(28, "Deployment", "Image", "Servicing", "and", "Management", "tool"),
            progress
        ];
        var baseline = new OfflineOcrEngine.Response("dark", panel, rows);
        TextRecognitionResult result = Primary();
        bool changed = TerminalOcrRecovery.Apply(result, baseline, pixels, size, CancellationToken.None);
        Check(changed && result.Lines.Count == 3 &&
            result.Lines[0].Text == "[==" + new string(' ', 25) + "5.0%" + new string(' ', 27) + "]" &&
            result.Lines[0].Words.Select(word => word.Text).SequenceEqual(new[] { "[==", "5.0%", "]" }) &&
            result.Lines[0].Words[1].Bounds == new RectangleF(176, 58, 23, 6),
            "Terminal OCR restores all three progress tokens and the exact raster-proven gaps");
        Check(result.Lines[1].Text == @"c:\PaTh\0001.txt" && result.Lines[2].Text == @"c: \PaTh\0001.txt" &&
            result.RawText == "first pass" && result.RecoveryRawText == "alternate pass",
            "Terminal drive roots preserve case and leading zeros and keep a real source space");

        TextRecognitionResult uncertain = Primary();
        OfflineOcrEngine.Response disputed = baseline with
        {
            Rows = [.. rows, progress with { Text = "[==5.8%]", Agreement = 1 }]
        };
        TerminalOcrRecovery.Apply(uncertain, disputed, pixels, size, CancellationToken.None);
        Check(uncertain.Lines[0].Text == "s.øx", "Competing terminal progress digits leave original recognition intact");

        TextRecognitionResult protectedImage = Primary();
        protectedImage.ProtectedGraphics.Add(new RectangleF(362, 56, 8, 11));
        TerminalOcrRecovery.Apply(protectedImage, baseline, pixels, size, CancellationToken.None);
        Check(protectedImage.Lines[0].Text == "s.øx", "Terminal progress never replaces protected image regions");

        TextRecognitionResult nonMonospace = Primary();
        OfflineOcrEngine.Response proportional = baseline with
        {
            Rows = [rows[0], rows[1] with
            {
                Words = rows[1].Words.Select((word, index) => index == 2
                    ? word with { Bounds = word.Bounds with { X = word.Bounds.X - 8 } }
                    : word).ToArray()
            }, progress]
        };
        Check(!TerminalOcrRecovery.Apply(nonMonospace, proportional, pixels, size, CancellationToken.None) &&
            nonMonospace.Lines[0].Text == "s.øx" && nonMonospace.Lines[1].Text == @"c: \PaTh\0001.txt",
            "Terminal recovery requires independent lines with a consistent fixed pitch");

        TextRecognitionResult edge = new();
        OfflineOcrEngine.Response fractional = baseline with
        {
            Rows = [rows[0], rows[1], progress with { Bounds = bounds with { Y = 116.5f, Height = 3.5f } }]
        };
        Check(!TerminalOcrRecovery.Apply(edge, fractional, pixels, size, CancellationToken.None) &&
            edge.Lines.Count == 0, "Fractional OCR bounds at the image edge cannot read beyond the last pixel");

        Row[] crossing =
        [
            new("The log is A001", new Box(8, 75, 83, 9),
                [new("The", new(8, 75, 17, 9)), new("log", new(32, 75, 17, 9)),
                 new("is", new(56, 75, 11, 9)), new("A001", new(74, 75, 17, 9))], .98, 1),
            new("The log is A00I", new Box(8, 75, 83, 9),
                [new("The", new(8, 75, 17, 9)), new("log", new(32, 75, 17, 9)),
                 new("is", new(56, 75, 11, 9)), new("A00I", new(74, 75, 17, 9))], .98, 1),
            new("The log is A00l", new Box(8, 75, 83, 9),
                [new("The", new(8, 75, 17, 9)), new("log", new(32, 75, 17, 9)),
                 new("is", new(56, 75, 11, 9)), new("A00l", new(74, 75, 17, 9))], .98, 1)
        ];
        var mixed = new OfflineOcrEngine.Response("dark", panel, [rows[0], rows[1], .. crossing]);
        TextRecognitionResult clipped = MixedLine(0);
        var desktop = new TextLineRegion { Text = "Notes", Bounds = new RectangleF(0, 54, 8, 10) };
        desktop.Words.Add(new TextWordRegion { Text = desktop.Text, Bounds = desktop.Bounds });
        clipped.Lines.Add(desktop);
        Check(TerminalOcrRecovery.Apply(clipped, mixed, pixels, size, CancellationToken.None) &&
            clipped.Lines[0].Text == "The log is" && clipped.Lines[0].Bounds == new RectangleF(8, 75, 59, 9) &&
            clipped.Lines[0].RecognitionClipBounds == panel.ToRectangle() && clipped.RawText == "eThe log is\r\nNotes" &&
            clipped.RecoveryRawText == "unmodified alternate reads",
            "An independently read terminal word can recover from off-panel contamination without adding disputed text");
        Check(clipped.Lines.Count == 2 && ReferenceEquals(clipped.Lines[1], desktop) &&
            desktop.Text == "Notes" && desktop.Words[0].Text == "Notes",
            "Terminal boundary recovery keeps independently recognized desktop text and word geometry");
        TextRecognitionResult validPrefix = MixedLine(8);
        Check(!TerminalOcrRecovery.Apply(validPrefix, mixed, pixels, size, CancellationToken.None) &&
            validPrefix.Lines[0].Text == "eThe log is",
            "The same recognized suffix cannot replace a leading token wholly inside the panel");
        TextRecognitionResult disagreed = MixedLine(0);
        OfflineOcrEngine.Response conflictingPrefix = mixed with
        {
            Rows = [rows[0], rows[1], crossing[0], crossing[1], crossing[2] with
            {
                Words = [new Word("He", crossing[2].Words[0].Bounds), .. crossing[2].Words.Skip(1)]
            }]
        };
        Check(!TerminalOcrRecovery.Apply(disagreed, conflictingPrefix, pixels, size, CancellationToken.None) &&
            disagreed.Lines[0].Text == "eThe log is",
            "A conflicting independent read keeps a mixed-source token unchanged");

        static TextRecognitionResult MixedLine(float left)
        {
            var result = new TextRecognitionResult
            {
                RawText = "eThe log is\r\nNotes",
                RecoveryRawText = "unmodified alternate reads"
            };
            var line = new TextLineRegion { Text = "eThe log is", Bounds = new RectangleF(left, 72, 67 - left, 16) };
            line.Words.Add(new TextWordRegion { Text = "eThe", Bounds = new RectangleF(left, 72, 25 - left, 16) });
            line.Words.Add(new TextWordRegion { Text = "log", Bounds = new RectangleF(32, 75, 17, 9) });
            line.Words.Add(new TextWordRegion { Text = "is", Bounds = new RectangleF(56, 75, 11, 9) });
            result.Lines.Add(line);
            return result;
        }

        void Paint(Rectangle rectangle)
        {
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
                Array.Fill(pixels, (byte)140, y * size.Width + rectangle.Left, rectangle.Width);
        }

        static Row FixedPitch(int y, params string[] text)
        {
            int x = 8;
            var words = new List<Word>();
            foreach (string word in text)
            {
                words.Add(new Word(word, new Box(x, y, word.Length * 6 - 1, 9)));
                x += (word.Length + 1) * 6;
            }
            return new Row(string.Join(" ", text), new Box(8, y, x - 15, 9), words.ToArray(), .999, 3);
        }

        static TextRecognitionResult Primary()
        {
            var result = new TextRecognitionResult { RawText = "first pass", RecoveryRawText = "alternate pass" };
            var mistakenProgress = new TextLineRegion { Text = "s.øx", Bounds = new RectangleF(176, 58, 23, 6) };
            mistakenProgress.Words.Add(new TextWordRegion { Text = mistakenProgress.Text, Bounds = mistakenProgress.Bounds });
            result.Lines.Add(mistakenProgress);
            result.Lines.Add(DrivePathAt(80, 12));
            result.Lines.Add(DrivePathAt(100, 18));
            return result;
        }

        static TextLineRegion DrivePathAt(int y, int advance)
        {
            var drive = new TextWordRegion { Text = "c:", Bounds = new RectangleF(12, y, 10, 9) };
            var path = new TextWordRegion { Text = @"\PaTh\0001.txt", Bounds = new RectangleF(12 + advance, y, 79, 9) };
            var line = new TextLineRegion
            {
                Text = drive.Text + " " + path.Text,
                Bounds = RectangleF.Union(drive.Bounds, path.Bounds)
            };
            line.Words.Add(drive);
            line.Words.Add(path);
            return line;
        }
    }
}
