using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio;

internal static partial class Program
{
    private static void CheckOcrExtensionFootprint()
    {
        const string convertedText = "QTY 00071";
        const string peerText = "FILE";
        const string family = "Segoe UI";
        const float font = 12;
        const float layoutWidth = 120;
        const int inkLeft = 20, inkTop = 45;
        var rtf = RichTextLayout.CreatePlainRtf(convertedText, family, font, FontStyle.Regular, Color.Black);
        RectangleF measured = RichTextLayout.Measure(rtf, layoutWidth).InkBounds;
        int inkRight = (int)Math.Ceiling(inkLeft + measured.Width);
        int maskRight = (inkLeft + inkRight) / 2;
        var mask = Rectangle.FromLTRB(15, 35, maskRight, 76);
        var layout = new RectangleF(mask.Left, mask.Top, layoutWidth, mask.Height);
        var target = new RecognizedTextReplacement(mask, new PointF(inkLeft - measured.Left, inkTop - measured.Top),
            convertedText, rtf, Color.Black, Color.White, font, layoutWidth, layout, measured);
        Check(measured.Width > 8 && measured.Height > 2 && inkRight > maskRight + 2 &&
            inkTop + measured.Height < mask.Bottom && inkRight < layout.Right,
            "Fixture measures a real raster extension beyond the OCR mask with room for its safety column");

        using var source = new Bitmap(180, 90);
        using (Graphics paint = Graphics.FromImage(source)) paint.Clear(Color.White);
        source.SetPixel(mask.Left, mask.Top, Color.DarkRed);
        source.SetPixel(165, 45, Color.Blue);
        using var editor = new EditorDocument();
        editor.SetImage(source);
        long revision = editor.Revision;
        string original = Raster(editor);

        int applied = editor.ReplaceRecognizedTextBlocks([target, Peer(40)], editor.BaseIdentity, revision);
        Check(applied == 2 && editor.AnnotationCount == 3 &&
            editor.GetTextAnnotation(1)?.Text == convertedText && editor.GetTextAnnotation(2)?.Text == peerText &&
            Raster(editor) != original && editor.CanUndo,
            "A neighboring mask may intersect vertical padding outside the rendered extension while both source lines convert");
        using (Bitmap rendered = editor.RenderImage())
            Check(rendered.GetPixel(165, 45).ToArgb() == Color.Blue.ToArgb(),
                "Conversion keeps independent source pixels outside its verified raster regions");
        editor.Undo();
        Check(editor.Revision == revision && editor.AnnotationCount == 0 && !editor.CanUndo && Raster(editor) == original,
            "One Undo restores the exact source raster after converting the two lines");

        editor.SetImage(source);
        Check(RefusesWithoutEditing(editor, [target, Peer(inkTop + 1)], "overlap another source line"),
            "An extension that reaches the neighboring source mask on a rendered ink row leaves image and history intact");

        source.SetPixel(inkRight, inkTop + 1, Color.Magenta);
        editor.SetImage(source);
        Check(RefusesWithoutEditing(editor, [target, Peer(40)], "contains source pixels"),
            "The extra column beyond rendered ink still protects source pixels and prevents a partial mutation");

        RecognizedTextReplacement Peer(int bottom)
        {
            var bounds = Rectangle.FromLTRB(maskRight + 1, 13, 133, bottom);
            string peerRtf = RichTextLayout.CreatePlainRtf(peerText, family, 10, FontStyle.Regular, Color.Black);
            var peerInk = RichTextLayout.Measure(peerRtf, bounds.Width).InkBounds;
            var position = new PointF(bounds.Left + 2 - peerInk.Left, 19 - peerInk.Top);
            return new RecognizedTextReplacement(bounds, position, peerText, peerRtf, Color.Black,
                Color.White, 10, bounds.Width, bounds, peerInk);
        }

        static bool RefusesWithoutEditing(EditorDocument document, IList<RecognizedTextReplacement> changes, string reason)
        {
            string before = Raster(document);
            long revision = document.Revision;
            try { document.ReplaceRecognizedTextBlocks(changes, document.BaseIdentity, revision); }
            catch (ArgumentException error)
            {
                return error.Message.Contains(reason, StringComparison.Ordinal) && document.Revision == revision &&
                    document.AnnotationCount == 0 && !document.CanUndo && !document.CanRedo && Raster(document) == before;
            }
            return false;
        }

        static string Raster(EditorDocument document)
        {
            using Bitmap image = document.RenderImage();
            using var bytes = new MemoryStream();
            image.Save(bytes, ImageFormat.Png);
            return PixelHash(bytes.ToArray());
        }
    }
}
