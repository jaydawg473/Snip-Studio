using System.Drawing;
using SnipStudio;
using SnipStudio.WinUI.Backend;
using Box = SnipStudio.WinUI.Backend.OfflineOcrEngine.Box;
using Row = SnipStudio.WinUI.Backend.OfflineOcrEngine.Row;
using Word = SnipStudio.WinUI.Backend.OfflineOcrEngine.Word;

internal static partial class Program
{
    private static void CheckTerminalOcrPathReview()
    {
        var size = new Size(400, 115);
        var panel = new Box(8, 0, 392, 115);
        byte[] pixels = new byte[size.Width * size.Height];
        Row[] references = [RowAt(12, 3, "The wall fell here"), RowAt(30, 3, "17 Plain lil data"),
            RowAt(48, 3, "All 1 here again")];
        const string exact = @"File can be found at C: \WINWORDS\Log.log";
        Row literal = RowAt(67, 1, exact);
        Row[] alternatives = [MoveSplit(RowAt(67, 1, @"File can be found at C: \WINwORDS\Log.1og"), 3, 1),
            RowAt(67, 1, @"File can be found at C: \WINwORDS\Log.log"), literal];
        var response = new OfflineOcrEngine.Response("dark", panel, [.. references, .. alternatives]);
        foreach (Row trusted in references.Append(literal)) Paint(trusted);

        TextRecognitionResult primary = Partial();
        Check(TerminalOcrRecovery.Apply(primary, response, pixels, size, CancellationToken.None) &&
            primary.Lines[0].Text == exact.Replace(": ", ":", StringComparison.Ordinal) &&
            primary.Lines[0].Words[^1].Text == @"\WINWORDS\Log.log" && primary.RawText == "File can be found at",
            "The same literal terminal path is recovered when rereads split the drive boundary a few pixels differently");
        TextRecognitionResult protectedText = Partial();
        protectedText.ProtectedGraphics.Add(new RectangleF(230, 67, 4, 8));
        Check(!TerminalOcrRecovery.Apply(protectedText, response, pixels, size, CancellationToken.None),
            "Terminal path review preserves protected source regions");
        Check(!TerminalOcrRecovery.Apply(Partial(), response, new byte[pixels.Length], size, CancellationToken.None),
            "OCR alternatives cannot resolve a path without source raster corroboration");
        Row changedDigits = RowAt(67, 1, @"File can be found at C: \WINwORDS\Log.2og");
        Check(!TerminalOcrRecovery.Apply(Partial(), response with { Rows = [.. references, literal, alternatives[1], changedDigits] },
            pixels, size, CancellationToken.None), "Unsupported glyph disagreements retain native recognition");
        Row shiftedBoundary = MoveSplit(alternatives[0], 1, 1);
        Check(!TerminalOcrRecovery.Apply(Partial(), response with { Rows = [.. references, shiftedBoundary, alternatives[1], literal] },
            pixels, size, CancellationToken.None), "A materially different drive/path boundary prevents recovery");
        completedGroups.Add("Terminal path variants require exact source glyph evidence and preserve protected regions");

        TextRecognitionResult Partial()
        {
            var result = new TextRecognitionResult { RawText = "File can be found at" };
            var line = new TextLineRegion { Text = result.RawText };
            foreach (var word in literal.Words[..5])
                line.Words.Add(new TextWordRegion { Text = word.Text, Bounds = word.Bounds.ToRectangle() });
            line.Bounds = RectangleF.Union(line.Words[0].Bounds, line.Words[^1].Bounds);
            result.Lines.Add(line);
            return result;
        }

        static Row RowAt(int y, int agreement, string text)
        {
            var words = new List<Word>();
            int x = 8;
            foreach (string token in text.Split(' '))
            {
                words.Add(new Word(token, new Box(x, y, token.Length * 6 - 1, 9)));
                x += (token.Length + 1) * 6;
            }
            if (words[^1].Text.StartsWith('\\'))
                words[^1] = words[^1] with { Bounds = words[^1].Bounds with { X = words[^1].Bounds.X - 6 } };
            return new Row(text, new Box(8, y, words[^1].Bounds.X + words[^1].Bounds.Width - 8, 9),
                words.ToArray(), .99, agreement);
        }

        static Row MoveSplit(Row row, int drivePixels, int pathPixels)
        {
            Word[] words = (Word[])row.Words.Clone();
            words[^2] = words[^2] with { Bounds = words[^2].Bounds with { Width = words[^2].Bounds.Width - drivePixels } };
            var path = words[^1].Bounds;
            words[^1] = words[^1] with { Bounds = path with { X = path.X - pathPixels, Width = path.Width + pathPixels } };
            return row with { Words = words };
        }

        void Paint(Row row)
        {
            foreach (var word in row.Words)
            for (int index = 0; index < word.Text.Length; index++)
            {
                int x = (int)word.Bounds.X + index * 6, y = (int)row.Bounds.Y;
                switch (word.Text[index])
                {
                    case 'W': Dot(1, 1); Dot(5, 1); break;
                    case 'w': Dot(1, 2); Dot(5, 2); break;
                    case 'l': for (int offset = 0; offset < 8; offset++) Dot(1, offset); break;
                    case '1': for (int offset = 0; offset < 8; offset++) Dot(4, offset); Dot(2, 0); break;
                }
                void Dot(int dx, int dy) => pixels[(y + dy) * size.Width + x + dx] = 200;
            }
        }
    }
}
