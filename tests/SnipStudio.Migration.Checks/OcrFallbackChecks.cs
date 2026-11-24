using System.Drawing;
using SnipStudio.WinUI.Backend;
using NativeResult = SnipStudio.TextRecognitionResult;
using NativeLine = SnipStudio.TextLineRegion;
using NativeWord = SnipStudio.TextWordRegion;
using AlternateBox = SnipStudio.WinUI.Backend.OfflineOcrEngine.Box;
using AlternateRow = SnipStudio.WinUI.Backend.OfflineOcrEngine.Row;
using AlternateWord = SnipStudio.WinUI.Backend.OfflineOcrEngine.Word;

internal static partial class Program
{
    private static void CheckOfflineOcrRecovery()
    {
        var result = new NativeResult { Available = true, Language = "en-US", RawText = "native raw" };
        result.Lines.Add(Line("heading", new(14, 6, 42, 10)));
        result.Lines.Add(Line("C:", new(12, 21, 10, 9)));
        result.Lines.Add(Line("log", new(130, 21, 13, 9)));
        result.Lines.Add(Line("A001", new(12, 57, 30, 10)));
        result.Lines.Add(Line("@", new(12, 74, 9, 7)));
        result.ProtectedGraphics.Add(new RectangleF(12, 74, 9, 7));
        const string path = @"C:\WINDOWS\Logs\DISM\dism.log";
        var rows = new[]
        {
            Row(path, 20, 136, 2),
            Row("0007_Aa.pdf", 40, 76, 2),
            Row("A007", 56, 35, 2),
            Row("A00I", 56, 35, 2),
            Row("photo", 73, 44, 3),
            Row("invented", 93, 58, 1)
        };
        OfflineOcrEngine.Merge(result, new("dark", new(4, 0, 192, 100), rows), new(200, 100));
        Check(result.Lines.Select(line => line.Text).SequenceEqual(new[] { "heading", path, "0007_Aa.pdf", "A001", "@" }),
            "Offline OCR replaces only verified complete rows and inserts missing rows in their original position");
        Check(result.RawText == "native raw" && result.RecoveryRawText.Contains(path, StringComparison.Ordinal),
            "Alternative recognition preserves both direct source readings without editing characters");
        Check(result.ProtectedGraphics.Single() == new RectangleF(12, 74, 9, 7) &&
            result.Lines[1].RecognitionClipBounds == new RectangleF(4, 0, 192, 100),
            "Offline recognition keeps protected icons and bounds writeback to the original panel");

        static NativeLine Line(string text, RectangleF bounds)
        {
            var line = new NativeLine { Text = text, Bounds = bounds };
            line.Words.Add(new NativeWord { Text = text, Bounds = bounds });
            return line;
        }

        static AlternateRow Row(string text, int top, int width, int agreement)
        {
            var box = new AlternateBox(11, top, width, 11);
            return new AlternateRow(text, box, [new AlternateWord(text, box)], .98, agreement);
        }
    }
}
