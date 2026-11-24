using System.Drawing;
using SnipStudio;
using SnipStudio.WinUI.Backend;
using Box = SnipStudio.WinUI.Backend.OfflineOcrEngine.Box;
using Row = SnipStudio.WinUI.Backend.OfflineOcrEngine.Row;
using Word = SnipStudio.WinUI.Backend.OfflineOcrEngine.Word;

internal static partial class Program
{
    private static void CheckTerminalOcrPromptRecovery()
    {
        const string prompt = @"C:\Temp\Snips>";
        const int origin = 4, pitch = 6, earlierY = 25, promptY = 68;
        int right = origin + prompt.Length * pitch;
        var size = new Size(256, 78);
        var panel = new Box(origin, 0, size.Width - origin, size.Height);
        var wordBounds = new Box(origin, promptY, right - origin + pitch, 10);
        Row[] rows =
        [
            FixedPitch(5, "One", "two", "three", "four", "five"),
            FixedPitch(39, "Six", "seven", "eight", "nine", "ten"),
            new(prompt, wordBounds, [new Word(prompt, wordBounds)], .99, 1),
            new(prompt + "_", wordBounds, [new Word(prompt + "_", wordBounds)], .97, 1),
            new(prompt.Insert(2, " ") + "_", wordBounds,
                [new Word("C:", new Box(origin, promptY, 11, 10)),
                 new Word(prompt[2..] + "_", new Box(origin + 12, promptY, right - origin - 6, 10))], .97, 1)
        ];
        var response = new OfflineOcrEngine.Response("dark", panel, rows);
        var pixels = new byte[size.Width * size.Height];
        for (int index = 0; index < prompt.Length; index++)
        {
            // Recreate distinct glyph shapes at the same terminal cell in each row.
            Paint(origin + index * pitch + 1, earlierY + index % 3 + 2, 2, 4, 150);
            Paint(origin + index * pitch + 1, promptY + index % 3 + 2, 2, 4, 150);
        }
        Paint(right + 1, promptY + 7, 4, 3, 220);
        TextRecognitionResult supported = CreateResult();
        Check(TerminalOcrRecovery.Apply(supported, response, pixels, size, CancellationToken.None) &&
            supported.Lines.Count == 3 && supported.Lines[0].Text == prompt + "view" &&
            supported.Lines[1].Text == prompt && supported.Lines[1].Words.Count == 1 &&
            supported.Lines[1].Bounds.Right == right && supported.Lines[1].RecognitionClipBounds.Right == right &&
            supported.Lines[2].Text == "22" && supported.RawText == "unaltered main read",
            "Repeated terminal prompt recovery restores only corroborated cells and leaves its cursor and adjacent text untouched");

        Paint(right + 1, promptY + 7, 4, 3, 0);
        Paint(right + 1, promptY + 9, 4, 1, 160);
        TextRecognitionResult literalUnderscore = CreateResult();
        Check(!TerminalOcrRecovery.Apply(literalUnderscore, response, pixels, size, CancellationToken.None) &&
            literalUnderscore.Lines.Count == 4 && literalUnderscore.Lines[2].Text == "Snips>",
            "A normal-width terminal underscore keeps the source fragments unchanged");

        Paint(right + 1, promptY + 7, 4, 3, 220);
        Paint(origin + 2 * pitch + 1, promptY + 2, 2, 6, 0);
        TextRecognitionResult mismatchedGlyph = CreateResult();
        Check(!TerminalOcrRecovery.Apply(mismatchedGlyph, response, pixels, size, CancellationToken.None) &&
            mismatchedGlyph.Lines.Count == 4,
            "A single unmatched prompt cell prevents replacing known terminal text");

        Paint(origin + 2 * pitch + 1, promptY + 4, 2, 4, 150);
        TextRecognitionResult alternate = CreateResult();
        Row disputed = rows[3] with { Text = prompt.Replace("Temp", "Keep", StringComparison.Ordinal) + "_" };
        Check(!TerminalOcrRecovery.Apply(alternate, response with { Rows = [rows[0], rows[1], rows[2], disputed, rows[4]] },
            pixels, size, CancellationToken.None) && alternate.Lines.Count == 4,
            "Three terminal rereads must agree on exact path spelling and case");

        TextRecognitionResult neighboringText = CreateResult();
        neighboringText.Lines[2].Text = "Other>";
        neighboringText.Lines[2].Words[0].Text = "Other>";
        Check(!TerminalOcrRecovery.Apply(neighboringText, response, pixels, size, CancellationToken.None) &&
            neighboringText.Lines[2].Text == "Other>",
            "Repeated prompt recovery preserves conflicting native text in its source region");
        TextRecognitionResult wideFragment = CreateResult();
        var overhang = wideFragment.Lines[2].Bounds;
        overhang.Width += 1;
        wideFragment.Lines[2].Bounds = overhang;
        wideFragment.Lines[2].Words[0].Bounds = overhang;
        Check(!TerminalOcrRecovery.Apply(wideFragment, response, pixels, size, CancellationToken.None) &&
            wideFragment.Lines[2].Bounds.Right > right + 1,
            "A native fragment extending more than one pixel past the prompt grid remains untouched");
        completedGroups.Add("Terminal repeated prompt recovery protects unrelated text and ambiguous cursor ink");

        void Paint(int x, int y, int width, int height, byte value)
        {
            for (int row = y; row < y + height; row++)
                pixels.AsSpan(row * size.Width + x, width).Fill(value);
        }

        static Row FixedPitch(int y, params string[] text)
        {
            int x = origin;
            var words = new List<Word>();
            foreach (string word in text)
            {
                words.Add(new Word(word, new Box(x, y, word.Length * pitch - 1, 9)));
                x += (word.Length + 1) * pitch;
            }
            return new Row(string.Join(" ", text), new Box(origin, y, x - origin - pitch, 9), words.ToArray(), .999, 3);
        }

        static TextRecognitionResult CreateResult()
        {
            var result = new TextRecognitionResult { RawText = "unaltered main read", RecoveryRawText = "unaltered alternates" };
            result.Lines.Add(Line(prompt + "view", new RectangleF(origin, earlierY, (prompt.Length + 4) * pitch, 10)));
            result.Lines.Add(Line(":", new RectangleF(origin + pitch + 1, promptY + 2, 2, 5)));
            result.Lines.Add(Line("Snips>", new RectangleF(origin + 8 * pitch, promptY + 1, 6 * pitch + .5f, 7)));
            result.Lines.Add(Line("22", new RectangleF(origin + 28 * pitch, promptY + 1, 11, 7)));
            return result;
        }

        static TextLineRegion Line(string text, RectangleF bounds)
        {
            var line = new TextLineRegion { Text = text, Bounds = bounds };
            line.Words.Add(new TextWordRegion { Text = text, Bounds = bounds });
            return line;
        }
    }
}
