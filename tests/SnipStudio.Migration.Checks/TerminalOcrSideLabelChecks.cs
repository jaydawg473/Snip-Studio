using System.Drawing;
using SnipStudio;
using SnipStudio.WinUI.Backend;
using Box = SnipStudio.WinUI.Backend.OfflineOcrEngine.Box;
using Row = SnipStudio.WinUI.Backend.OfflineOcrEngine.Row;
using Word = SnipStudio.WinUI.Backend.OfflineOcrEngine.Word;

internal static partial class Program
{
    private static void CheckTerminalOcrSideLabels()
    {
        const string disputed = "doc;t";
        const string reread = "docol";
        const int panelLeft = 28;
        var size = new Size(340, 110);
        var panel = new Box(panelLeft, 1, size.Width - panelLeft, 108);
        Row[] rows = [FixedPitch(9, "One", "two", "three", "four"),
            FixedPitch(26, "Five", "six", "seven", "eight")];
        var response = new OfflineOcrEngine.Response("dark", panel, rows);
        using var source = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.DarkSlateGray, 0, 0, panelLeft, size.Height);
        }
        Color strongInk = Color.FromArgb(199, 199, 199);
        for (int y = 57; y < 63; y++)
        {
            source.SetPixel(0, y, strongInk);
            source.SetPixel(17, y, strongInk);
        }
        var selected = new RectangleF(0, 54, 18, 10);
        var shortLabel = new RectangleF(0, 43, 11, 9);
        var verticalLabel = new RectangleF(3, 74, 8, 26);
        TextRecognitionResult result = CreateResult();
        var found = TerminalOcrRecovery.FindClippedSideLabel(result, response, source, CancellationToken.None);
        Check(found is { } candidate && candidate.Line.Bounds == selected && candidate.Focus == new Rectangle(0, 53, 25, 13),
            "Only the contradictory box receives a focused read beside a proven narrow terminal panel");
        Check(!TerminalOcrRecovery.AbstainFromSideLabel(result, found!.Value.Line, reread, "docel") &&
            result.Lines.Any(line => line.Text == disputed), "Disagreement between focused rereads keeps the native text");
        Check(!TerminalOcrRecovery.AbstainFromSideLabel(result, found.Value.Line, disputed, disputed),
            "Corroborated clipped interface text stays readable");
        Check(!TerminalOcrRecovery.AbstainFromSideLabel(result, found.Value.Line, "dotol", "dotol"),
            "Changing different visible glyphs cannot be used to exclude a clipped result");
        string raw = result.RawText;
        Check(TerminalOcrRecovery.AbstainFromSideLabel(result, found.Value.Line, reread, reread) &&
            result.RawText == raw && result.RecoveryRawText.EndsWith(reread, StringComparison.Ordinal) && result.HasUncertainClippedText &&
            result.ProtectedGraphics.Contains(selected) && !result.Lines.Any(line => line.Text == reread || line.Text == disputed),
            "The contradictory label is excluded from editable text without inventing a replacement or altering diagnostics");
        Check(result.Lines.Any(line => line.Bounds == shortLabel) && result.Lines.Any(line => line.Bounds == verticalLabel) &&
            source.GetPixel(17, 59) == strongInk, "Other short and vertical edge labels and source pixels remain intact");

        source.SetPixel(16, 57, Color.FromArgb(134, 134, 134));
        Check(TerminalOcrRecovery.FindClippedSideLabel(CreateResult(), response, source, CancellationToken.None) is null,
            "Antialiased ink next to the stem can be a genuine clipped crossbar and is kept");
        source.SetPixel(16, 57, Color.DarkSlateGray);
        Check(TerminalOcrRecovery.FindClippedSideLabel(CreateResult(), response with { Kind = "light" }, source,
                CancellationToken.None) is null &&
            TerminalOcrRecovery.FindClippedSideLabel(CreateResult(), response with { Rows = [rows[0], rows[1] with { Agreement = 1 }] },
                source, CancellationToken.None) is null,
            "Mixed content without independently verified terminal pitch never activates the side-label gate");
        completedGroups.Add("Clipped terminal labels preserve corroborated edge text, raw diagnostics, and source ink");

        TextRecognitionResult CreateResult()
        {
            var output = new TextRecognitionResult { RawText = "visible native strings", RecoveryRawText = "prior rereads" };
            Add("ket", shortLabel);
            Add(disputed, selected);
            Add("XY", verticalLabel);
            Add("A longer verified console line", new RectangleF(panelLeft, 54, 180, 12));
            return output;

            void Add(string text, RectangleF bounds)
            {
                var line = new TextLineRegion { Text = text, Bounds = bounds };
                line.Words.Add(new TextWordRegion { Text = text, Bounds = bounds });
                output.Lines.Add(line);
            }
        }

        static Row FixedPitch(int y, params string[] text)
        {
            int x = panelLeft;
            var words = new List<Word>();
            foreach (string word in text)
            {
                words.Add(new Word(word, new Box(x, y, word.Length * 6 - 1, 9)));
                x += (word.Length + 1) * 6;
            }
            return new Row(string.Join(' ', text), new Box(panelLeft, y, x - panelLeft - 6, 9), words.ToArray(), .999, 3);
        }
    }
}
