using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckAngledOcrSelection()
    {
        var frame = new Rectangle(0, 0, 140, 120);
        const double angle = 24;
        double radians = angle * Math.PI / 180;
        TextWordRegion first = MapWord("FIRST", 22, 40);
        TextWordRegion second = MapWord("SECOND", 22, 55);
        var firstPoint = new PointF(70, 63);
        var secondPoint = new PointF(70, 78);
        Check(first.Bounds.Contains(firstPoint) && second.Bounds.Contains(firstPoint) &&
            first.Bounds.Contains(secondPoint) && second.Bounds.Contains(secondPoint),
            "Parallel angled words reproduce the same overlapping boxes as the signed baseline");
        var result = new TextRecognitionResult { Available = true, TextAngle = angle };
        result.Lines.Add(Line(first));
        result.Lines.Add(Line(second));
        var overlay = new TextOverlaySelection(result.Lines);
        Check(overlay.HitTest(firstPoint, 0) == 0 && overlay.HitTest(secondPoint, 0) == 1,
            "A click selects the rotated word under its source point despite enclosing-box overlap");
        Check(overlay.HitTest(new PointF(90, 43), 8) == -1 &&
            overlay.HitTestNear(new PointF(70, 60), 2.5f) == 0 &&
            overlay.HitTestNear(new PointF(70, 58), 2.5f) == -1,
            "Angled proximity uses actual edge distance and ignores an empty corner");
        Check(overlay.Nearest(secondPoint) == 1, "An angled selection ends at the closest physical word");
        overlay.SelectArea(new PointF(89, 42), new PointF(91, 45));
        Check(!overlay.HasSelection, "Blank area selection ignores an empty enclosing-box corner");
        overlay.SelectArea(new PointF(68, 62), new PointF(72, 65));
        Check(overlay.SelectedText == "FIRST", "Angled area selection retains only the visible word intersected");

        using var session = new EditorSession();
        using var source = new Bitmap(frame.Width, frame.Height);
        using var png = new MemoryStream();
        source.Save(png, ImageFormat.Png);
        session.OpenImageBytes(png.ToArray());
        var baseline = session.GetCaptureBaseline();
        var snapshot = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);
        var start = new ImagePoint(firstPoint.X, firstPoint.Y);
        var end = new ImagePoint(secondPoint.X, secondPoint.Y);
        Check(session.SelectRecognizedText(snapshot, start, start).Text == "FIRST" &&
            session.SelectRecognizedText(snapshot, start, end).Text == "FIRST\r\nSECOND" &&
            session.Revision == baseline.Revision,
            "The editor snapshot retains source footprints and selects exact ordered text without changing pixels");

        // A nonuniform resize projects the rotated rectangle into a
        // parallelogram and clips it to the actual recognition crop.
        var crop = new Rectangle(100, 120, 200, 100);
        var prepared = new Size(100, 100);
        RectangleF clipped = TextRecognition.SourceWordGeometry(42, 41, 30, 80, prepared,
            crop, -30, true, out TextWordFootprint footprint);
        Check(clipped == TextRecognition.SourceWordBounds(42, 41, 30, 80, prepared, crop, -30) &&
            footprint != null && footprint.Contains(new PointF(200, 170)) &&
            !footprint.Contains(new PointF(261, 221)),
            "The original crop, nonuniform scale and clipping keep true source geometry without changing the public box");
        TextRecognition.SourceWordGeometry(42, 41, 30, 20, prepared, crop, 2, true, out TextWordFootprint smallAngle);
        Check(smallAngle == null, "The existing horizontal and slight-angle paths retain their rectangular behavior");

        TextWordRegion MapWord(string text, float sourceX, float sourceY)
        {
            // Invert the center rotation independently to obtain the native
            // deskewed origin of each word with known source-space positions.
            double x = 70 + (sourceX - 70) * Math.Cos(radians) + (sourceY - 60) * Math.Sin(radians);
            double y = 60 - (sourceX - 70) * Math.Sin(radians) + (sourceY - 60) * Math.Cos(radians);
            RectangleF bounds = TextRecognition.SourceWordGeometry(x, y, 83, 12, frame.Size, frame,
                angle, true, out TextWordFootprint projected);
            return new TextWordRegion { Text = text, Bounds = bounds, Footprint = projected };
        }

        static TextLineRegion Line(TextWordRegion word)
        {
            var line = new TextLineRegion { Text = word.Text, Bounds = word.Bounds };
            line.Words.Add(word);
            return line;
        }
    }
}
