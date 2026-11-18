using System;
using System.Collections.Generic;
using System.Drawing;
using SnipStudio;

internal static class SelectionChecks
{
    private static int count;
    private static void Assert(bool value, string label) { if (!value) throw new Exception(label); count++; }
    private static TextWordRegion Word(string value, float x, float y, float width)
    {
        return new TextWordRegion { Text = value, Bounds = new RectangleF(x, y, width, 12) };
    }
    private static int Main()
    {
        try
        {
            TextLineRegion a = new TextLineRegion { Text = "PART-00123 15 units" };
            a.Words.Add(Word("PART-00123", 10, 10, 80));
            a.Words.Add(Word("15", 100, 10, 16));
            a.Words.Add(Word("units", 125, 10, 34));
            TextLineRegion b = new TextLineRegion { Text = "$125.00 exact" };
            b.Words.Add(Word("$125.00", 10, 35, 60));
            b.Words.Add(Word("exact", 80, 35, 40));
            TextOverlaySelection model = new TextOverlaySelection(new List<TextLineRegion> { a, b });
            Assert(model.Count == 5, "all valid words");
            Assert(model.AllText == "PART-00123 15 units" + Environment.NewLine + "$125.00 exact", "line text order");
            Assert(!model.HasSelection && model.SelectedText == "", "empty selection");
            Assert(model.HitTest(new PointF(15, 15), 0) == 0, "word hit");
            Assert(model.HitTest(new PointF(500, 500), 4) == -1, "far start rejected");
            Assert(model.HitTest(new PointF(8, 15), 3) == 0, "small margin hit");
            Assert(model.HitTest(new PointF(Single.NaN, 15), 3) == -1, "nonfinite hit");
            Assert(model.Nearest(new PointF(130, 45)) == 4, "drag endpoint nearest");
            Assert(model.Nearest(new PointF(500, 40)) == 4, "far-right drag follows the nearest text line");
            model.Select(0, 0);
            Assert(model.SelectedText == "PART-00123", "single word leading zeros");
            model.Select(1, 2);
            Assert(model.SelectedText == "15 units", "same-line spaces");
            model.Select(4, 1);
            Assert(model.SelectedText == "15 units" + Environment.NewLine + "$125.00 exact", "reverse multiline range");
            Assert(model.IsSelected(1) && model.IsSelected(4) && !model.IsSelected(0), "selected bounds");
            model.Select(0, 8);
            Assert(!model.HasSelection, "invalid selection clears");
            model.Select(0, 2); model.Clear();
            Assert(!model.HasSelection, "explicit clear");
            a.Words[0].Text = "MUTATED"; a.Words[0].Bounds = RectangleF.Empty; a.Text = "changed";
            model.Select(0, 0);
            Assert(model.SelectedText == "PART-00123" && model.WordBounds(0).Width == 80, "snapshot ownership");
            Assert(model.AllText.StartsWith("PART-00123"), "line snapshot ownership");
            TextLineRegion invalid = new TextLineRegion();
            invalid.Words.Add(null);
            invalid.Words.Add(Word("", 0, 0, 10));
            invalid.Words.Add(new TextWordRegion { Text = "bad", Bounds = new RectangleF(0, 0, Single.NaN, 10) });
            invalid.Words.Add(new TextWordRegion { Text = "zero", Bounds = RectangleF.Empty });
            invalid.Words.Add(Word("valid", 3, 3, 25));
            TextOverlaySelection clean = new TextOverlaySelection(new List<TextLineRegion> { null, invalid });
            Assert(clean.Count == 1 && clean.AllText == "valid", "invalid words skipped/fallback text");
            Assert(clean.WordBounds(-1) == RectangleF.Empty && clean.WordBounds(8) == RectangleF.Empty, "invalid bounds safe");
            TextOverlaySelection empty = new TextOverlaySelection(null);
            Assert(empty.Count == 0 && empty.Nearest(new PointF(1, 1)) == -1 && empty.AllText == "", "empty model");
            Console.WriteLine("PASS: " + count + " text overlay selection checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
