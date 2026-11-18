using System;
using System.Collections.Generic;
using System.Drawing;
using SnipStudio;

internal static class SnapChecks
{
    private static int count;
    private static void Assert(bool value, string message)
    { if (!value) throw new Exception(message); count++; }

    private static TextLineRegion Line(params RectangleF[] words)
    {
        TextLineRegion line = new TextLineRegion();
        foreach (RectangleF word in words) line.Words.Add(new TextWordRegion { Bounds = word, Text = "word" });
        return line;
    }

    private static int Main()
    {
        try
        {
            List<TextLineRegion> lines = new List<TextLineRegion>();
            lines.Add(Line(new RectangleF(10, 10, 40, 16), new RectangleF(60, 10, 45, 16), new RectangleF(115, 10, 50, 16)));
            lines.Add(Line(new RectangleF(10, 34, 45, 16), new RectangleF(65, 34, 35, 16), new RectangleF(110, 34, 60, 16)));
            lines.Add(Line(new RectangleF(10, 58, 60, 16), new RectangleF(80, 58, 45, 16)));
            Assert(TextSnapSelection.CanStart(lines, new PointF(15, 15), 4), "Start inside real word");
            Assert(TextSnapSelection.CanStart(lines, new PointF(7, 15), 4), "Small starting margin allowed");
            Assert(!TextSnapSelection.CanStart(lines, new PointF(300, 15), 4), "Blank distant start must stay freehand");
            List<RectangleF> same = TextSnapSelection.BuildBands(lines, new PointF(70, 15), new PointF(140, 15), 4);
            Assert(same.Count == 1 && same[0] == RectangleF.FromLTRB(58, 8, 167, 28), "One-line selection must trim unselected first word and pad");
            List<RectangleF> reverse = TextSnapSelection.BuildBands(lines, new PointF(140, 15), new PointF(70, 15), 4);
            Assert(reverse.Count == same.Count && reverse[0] == same[0], "Reverse drag must match forward selection");
            List<RectangleF> multi = TextSnapSelection.BuildBands(lines, new PointF(70, 15), new PointF(20, 65), 4);
            Assert(multi.Count == 3 && multi[0].Left == 58 && multi[1].Left == 8 && multi[1].Right == 172 && multi[2].Right == 72, "Multiline selection must trim first/last lines");
            List<RectangleF> backwardMulti = TextSnapSelection.BuildBands(lines, new PointF(20, 65), new PointF(70, 15), 4);
            Assert(backwardMulti.Count == 3 && backwardMulti[0] == multi[0] && backwardMulti[2] == multi[2], "Multiline reverse drag");
            List<RectangleF> click = TextSnapSelection.BuildBands(lines, new PointF(15, 15), new PointF(15, 15), 4);
            Assert(click.Count == 1 && click[0].Width == 44, "One-word click selection");
            Assert(TextSnapSelection.BuildBands(lines, new PointF(15, 15), new PointF(600, 400), 4).Count == 0, "Distant endpoint must return freehand fallback");
            List<TextLineRegion> separate = new List<TextLineRegion> { lines[0], Line(new RectangleF(10, 250, 45, 16)) };
            Assert(TextSnapSelection.BuildBands(separate, new PointF(15, 15), new PointF(20, 255), 4).Count == 0, "Separate distant text blocks must not bridge");
            List<TextLineRegion> columns = new List<TextLineRegion> { lines[0], Line(new RectangleF(400, 10, 45, 16)) };
            Assert(TextSnapSelection.BuildBands(columns, new PointF(15, 15), new PointF(405, 15), 4).Count == 0, "Separate columns must not bridge");
            Assert(TextSnapSelection.BuildBands(null, PointF.Empty, PointF.Empty, 4).Count == 0, "Missing OCR data safe");
            Assert(!TextSnapSelection.CanStart(lines, new PointF(float.NaN, 10), 4), "Invalid pointer rejected");
            ActualOcrFixture();
            Console.WriteLine("PASS: " + count + " snap checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void ActualOcrFixture()
    {
        using (Bitmap image = new Bitmap(1000, 200))
        {
            using (Graphics g = Graphics.FromImage(image))
            using (Font font = new Font("Segoe UI", 28))
            { g.Clear(Color.White); g.DrawString("Snap highlight to these words", font, Brushes.Black, 30, 40); }
            TextRecognitionResult result = TextRecognition.RecognizeAsync(image).GetAwaiter().GetResult();
            Assert(result.Available && result.Lines.Count > 0 && result.Lines[0].Words.Count >= 5, "Actual OCR fixture must have word boxes");
            TextLineRegion line = result.Lines[0];
            RectangleF first = line.Words[1].Bounds, last = line.Words[3].Bounds;
            PointF start = new PointF(first.Left + first.Width / 2, first.Top + first.Height / 2);
            PointF end = new PointF(last.Left + last.Width / 2, last.Top + last.Height / 2);
            List<RectangleF> bands = TextSnapSelection.BuildBands(result.Lines, start, end, 4);
            Assert(bands.Count == 1 && bands[0].Left <= first.Left && bands[0].Right >= last.Right && bands[0].Left > line.Words[0].Bounds.Right,
                "Actual OCR boxes must snap only selected word span");
        }
    }
}
