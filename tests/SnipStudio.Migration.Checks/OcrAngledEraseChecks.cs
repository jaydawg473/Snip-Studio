using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckAngledOcrEraseAsync()
    {
        using var session = new EditorSession();
        using var image = new Bitmap(140, 120);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.TranslateTransform(22, 40);
            graphics.RotateTransform(24);
            graphics.FillRectangle(Brushes.Black, 0, 0, 83, 12);
            graphics.ResetTransform();
            // The marker is in the enclosing word box, well outside the angled ink.
            graphics.FillRectangle(Brushes.Red, 88, 43, 5, 5);
        }

        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        session.OpenImageBytes(png.ToArray());
        byte[] original = (await session.RenderPreviewAsync()).PngBytes;
        var baseline = session.GetCaptureBaseline();
        ImagePoint word = new(56, 63);

        foreach (double angle in new[] { 24d, -4.5, double.NaN })
        {
            var result = new TextRecognitionResult { Available = true, TextAngle = angle, Text = "TEST" };
            var line = new TextLineRegion { Text = "TEST", Bounds = new RectangleF(17, 40, 81, 45) };
            line.Words.Add(new TextWordRegion { Text = "TEST", Bounds = line.Bounds });
            result.Lines.Add(line);
            var snapshot = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);
            var selection = session.SelectRecognizedText(snapshot, word, word);
            Check(selection.Text == "TEST", "Rectangular OCR bounds still allow copying angled text");
            bool refused = false;
            try
            {
                session.DeleteRecognizedSelection(snapshot, selection);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("Rotate the snip", StringComparison.Ordinal))
            {
                refused = true;
            }

            Check(refused && session.Revision == baseline.Revision &&
                PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original),
                "Deleting angled or invalid OCR geometry preserves the whole raster, including unrelated pixels inside its bounding box");
        }
        completedGroups.Add("Angled OCR copy works and rectangular erase cannot cover neighboring pixels");
    }
}

