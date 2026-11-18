using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SnipStudio;

internal static class OverlayCanvasChecks
{
    private static int count;
    private static void Assert(bool value, string label) { if (!value) throw new Exception(label); count++; }
    private static void Field(object target, string name, object value)
    {
        typeof(EditorCanvas).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
    private static bool Equal(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }
    private static bool Key(EditorCanvas canvas, Keys key)
    {
        object[] arguments = { new Message(), key };
        return (bool)typeof(EditorCanvas).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, arguments);
    }
    private static Bitmap Paint(EditorCanvas canvas)
    {
        Bitmap painted = new Bitmap(canvas.Width, canvas.Height);
        using (Graphics graphics = Graphics.FromImage(painted))
            typeof(EditorCanvas).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas,
                new object[] { new PaintEventArgs(graphics, canvas.ClientRectangle) });
        return painted;
    }
    private static Color ImagePixel(EditorCanvas canvas, Bitmap painted, float x, float y)
    {
        PointF origin = (PointF)typeof(EditorCanvas).GetProperty("ImageOrigin", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas, null);
        return painted.GetPixel((int)Math.Round(origin.X + x * canvas.Zoom), (int)Math.Round(origin.Y + y * canvas.Zoom));
    }
    private static void WaitForRecognitionCue(EditorCanvas canvas)
    {
        FieldInfo visible = typeof(EditorCanvas).GetField("textFeedbackVisible", BindingFlags.Instance | BindingFlags.NonPublic);
        if (visible == null) throw new Exception("Recognition activation-cue state is unavailable");
        Stopwatch clock = Stopwatch.StartNew();
        while ((bool)visible.GetValue(canvas) && clock.ElapsedMilliseconds < 3000)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Assert(!(bool)visible.GetValue(canvas), "Recognition activation cue completes before steady-state pixel comparison");
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            TextLineRegion first = new TextLineRegion { Text = "PART-00123 15" };
            first.Words.Add(new TextWordRegion { Text = "PART-00123", Bounds = new RectangleF(15, 20, 90, 18) });
            first.Words.Add(new TextWordRegion { Text = "15", Bounds = new RectangleF(115, 20, 22, 18) });
            TextLineRegion second = new TextLineRegion { Text = "$125.00 exact" };
            second.Words.Add(new TextWordRegion { Text = "$125.00", Bounds = new RectangleF(15, 60, 70, 18) });
            second.Words.Add(new TextWordRegion { Text = "exact", Bounds = new RectangleF(95, 60, 45, 18) });
            TextRecognitionResult result = new TextRecognitionResult { Available = true };
            result.Lines.Add(first); result.Lines.Add(second);
            using (Bitmap fixture = new Bitmap(320, 180))
            using (EditorCanvas canvas = new EditorCanvas())
            {
                using (Graphics graphics = Graphics.FromImage(fixture))
                {
                    graphics.Clear(Color.White);
                    graphics.FillRectangle(Brushes.Black, 20, 24, 80, 9);
                    graphics.FillRectangle(Brushes.Black, 119, 24, 14, 9);
                }
                canvas.Size = new Size(500, 300);
                EditorDocument document = (EditorDocument)typeof(EditorCanvas).GetField("document", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas);
                document.SetImage(fixture);
                document.AddArrow(new PointF(180, 80), new PointF(240, 110), Color.Red, 3);
                Field(canvas, "recognizedBase", canvas.ImageIdentity);
                Field(canvas, "recognizedRevision", canvas.DocumentRevision);
                Field(canvas, "recognizedText", result);
                TextOverlaySelection selection = new TextOverlaySelection(result.Lines);
                Field(canvas, "textSelection", selection);
                canvas.FitToWindow();
                long revision = canvas.DocumentRevision;
                int changes = 0;
                canvas.TextSelectionChanged += delegate { changes++; };
                using (Bitmap before = canvas.RenderImage())
                using (Bitmap naturalPreview = Paint(canvas))
                {
                    canvas.TextActionsMode = true;
                    Assert(canvas.TextActionsMode && changes == 1, "Mode announces selection UI update");
                    Assert(canvas.AllRecognizedText == first.Text + Environment.NewLine + second.Text, "All text retains line breaks");
                    WaitForRecognitionCue(canvas);
                    using (Bitmap unselectedPreview = Paint(canvas))
                        Assert(Equal(naturalPreview, unselectedPreview), "Unselected text mode preserves the natural image without dimming or word frames");
                    selection.Select(3, 1);
                    Assert(canvas.HasSelectedRecognizedText && canvas.SelectedRecognizedText == "15" + Environment.NewLine + "$125.00 exact", "Reverse selected text is exposed");
                    IList<RectangleF> bands = selection.SelectedLineBounds();
                    Assert(bands.Count == 2 && bands[0] == new RectangleF(115, 20, 22, 18) && bands[1] == new RectangleF(15, 60, 125, 18),
                        "Selected words form one unpadded band per OCR line");
                    using (Bitmap exported = canvas.RenderImage()) Assert(Equal(before, exported), "Overlay and selection never alter exported pixels");
                    Assert(canvas.DocumentRevision == revision && document.AnnotationCount == 1, "Overlay does not create edit history");
                    using (Bitmap painted = Paint(canvas))
                    {
                        Assert(ImagePixel(canvas, painted, 200, 140).ToArgb() == Color.White.ToArgb(), "Unselected background stays at full contrast");
                        Assert(ImagePixel(canvas, painted, 30, 21).ToArgb() == Color.White.ToArgb(), "Unselected word pixels stay unchanged");
                        Color selectedPixel = ImagePixel(canvas, painted, 120, 36);
                        Assert(selectedPixel.R < 255 && selectedPixel.B > selectedPixel.R, "Only selected text receives the blue selection tint");
                    }
                    Assert(Key(canvas, Keys.Control | Keys.A) && canvas.SelectedRecognizedText == canvas.AllRecognizedText, "Ctrl+A selects all overlay text");
                    Assert(Key(canvas, Keys.Escape) && !canvas.HasSelectedRecognizedText && canvas.TextActionsMode, "First Escape clears selection");
                    Assert(selection.SelectedLineBounds().Count == 0, "Clearing selection removes its line bands");
                    using (Bitmap clearedPreview = Paint(canvas))
                        Assert(Equal(naturalPreview, clearedPreview), "Clearing selection restores the natural preview");
                    Assert(Key(canvas, Keys.Escape) && !canvas.TextActionsMode, "Second Escape exits text mode");
                    canvas.TextActionsMode = true;
                    selection.Select(0, 0);
                    typeof(EditorCanvas).GetMethod("ResetTextSelection", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(canvas, new object[] { null });
                    Assert(!canvas.HasSelectedRecognizedText && canvas.AllRecognizedText == "", "Image recognition reset clears stale words");
                    using (Bitmap exported = canvas.RenderImage()) Assert(Equal(before, exported), "Clearing text mode never modifies screenshot");
                }
            }
            Console.WriteLine("PASS: " + count + " inline canvas checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
