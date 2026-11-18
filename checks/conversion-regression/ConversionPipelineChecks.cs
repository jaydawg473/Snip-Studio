using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SnipStudio;

internal static class ConversionPipelineChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static readonly int[] Sizes = { 11, 12, 13 };
    private static readonly string[] Names = { "Sample document number one.pdf", "Sample document number two.pdf", "Sample document number three.pdf" };
    private static readonly int[] Tops = { 60, 110, 160 };
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Instance).GetValue(owner); }
    private static void Field(object owner, string name, object value)
    { owner.GetType().GetField(name, Instance).SetValue(owner, value); }
    private static object Invoke(object owner, string name, params object[] args)
    { return owner.GetType().GetMethod(name, Instance).Invoke(owner, args); }
    private static bool Same(Bitmap first, Bitmap second, Rectangle region)
    {
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
        return true;
    }
    private static void PumpUntil(Func<bool> predicate, string label)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < 60000)
        { Application.DoEvents(); Thread.Sleep(10); }
        Check(predicate(), label);
    }
    private static Bitmap Fixture()
    {
        Bitmap image = new Bitmap(900, 600, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            for (int row = 0; row < Sizes.Length; row++)
            {
                int top = Tops[row];
                using (Pen pen = new Pen(Color.Red))
                {
                    graphics.DrawLines(pen, new Point[] { new Point(20, top), new Point(27, top), new Point(32, top + 5), new Point(32, top + 15), new Point(20, top + 15), new Point(20, top) });
                    graphics.DrawLines(pen, new Point[] { new Point(27, top), new Point(27, top + 5), new Point(32, top + 5) });
                    graphics.DrawLine(pen, 22, top + 11, 29, top + 7);
                    graphics.DrawLine(pen, 24, top + 5, 28, top + 13);
                }
                using (Font font = new Font("Segoe UI", Sizes[row], FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                    TextRenderer.DrawText(graphics, Names[row], font, new Point(44, top + 2), Color.Black, Color.White, flags);
                    TextRenderer.DrawText(graphics, "11/18/2026 2:43 PM", font, new Point(440, top + 2), Color.Black, Color.White, flags);
                    TextRenderer.DrawText(graphics, "PDF Document", font, new Point(650, top + 2), Color.Black, Color.White, flags);
                }
            }
        }
        return image;
    }
    private static TextLineRegion SourceLine(TextRecognitionResult result, int row)
    {
        TextLineRegion best = null;
        foreach (TextLineRegion line in result.Lines)
        {
            if (line.Bounds.Top < Tops[row] - 3 || line.Bounds.Top > Tops[row] + 16 || line.Bounds.Left > 100) continue;
            if (line.Text.IndexOf("Sample", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (best == null || line.Text.Length > best.Text.Length) best = line;
        }
        return best;
    }
    private static void RunRow(Bitmap image, TextRecognitionResult result, int row, string output)
    {
        TextLineRegion line = SourceLine(result, row);
        Check(line != null, Sizes[row] + "px: native OCR returns the filename row");
        Console.WriteLine("OCR " + Sizes[row] + "px: " + line.Text + " " + line.Bounds);
        Check(line.Bounds.Left >= 40, Sizes[row] + "px: icon stays outside editable text bounds");
        Check(line.Bounds.Right < 440, Sizes[row] + "px: neighboring date column is separate");
        using (EditorCanvas canvas = new EditorCanvas())
        {
            canvas.Size = new Size(1100, 760);
            canvas.CreateControl();
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            document.SetImage(image);
            Field(canvas, "recognizedText", result);
            Field(canvas, "recognizedBase", canvas.ImageIdentity);
            Field(canvas, "recognizedRevision", canvas.DocumentRevision);
            Field(canvas, "textSelection", new TextOverlaySelection(result.Lines));
            canvas.FitToWindow();
            canvas.Tool = EditorTool.Text;
            canvas.TextActionsMode = true;
            PointF origin = (PointF)canvas.GetType().GetProperty("ImageOrigin", Instance).GetValue(canvas, null);
            TextWordRegion target = null;
            foreach (TextWordRegion word in line.Words)
                if (word.Text.IndexOf("Sample", StringComparison.OrdinalIgnoreCase) >= 0) { target = word; break; }
            Check(target != null, Sizes[row] + "px: native OCR has a filename click target");
            Point click = new Point((int)Math.Round(origin.X + (target.Bounds.Left + target.Bounds.Width / 2) * canvas.Zoom),
                (int)Math.Round(origin.Y + (target.Bounds.Top + target.Bounds.Height / 2) * canvas.Zoom));
            MouseEventArgs mouse = new MouseEventArgs(MouseButtons.Left, 1, click.X, click.Y, 0);
            Invoke(canvas, "OnMouseDown", mouse);
            Invoke(canvas, "OnMouseUp", mouse);
            PumpUntil(delegate { return canvas.InlineTextEditing && !canvas.IsMatchingSourceText; }, Sizes[row] + "px: actual mouse route opens matched source text");
            RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor");
            draft.Select(0, 1);
            using (Font font = draft.SelectionFont)
            {
                Check(font != null, Sizes[row] + "px: source draft has a font");
                float pixels = font.SizeInPoints * 96f / 72f;
                Color color = draft.SelectionColor;
                Console.WriteLine("DRAFT " + Sizes[row] + "px => " + font.Name + " " + pixels + "px RGB " + color.R + "," + color.G + "," + color.B);
                Check(Math.Abs(pixels - Sizes[row]) <= 1.5f, Sizes[row] + "px: source font remains within 1.5 source pixels");
                Check(Math.Max(color.R, Math.Max(color.G, color.B)) <= 45 && Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) <= 20,
                    Sizes[row] + "px: black filename stays dark and neutral");
            }
            using (Bitmap unchanged = document.RenderImage())
                Check(Same(image, unchanged, new Rectangle(Point.Empty, image.Size)), Sizes[row] + "px: opening a clean matched draft preserves every source pixel");
            draft.Select(draft.TextLength, 0);
            draft.SelectedText = " revised";
            Check(canvas.CommitInlineText(), Sizes[row] + "px: modified recognized text commits");
            Check(document.AnnotationCount == 1 && canvas.CanUndo, Sizes[row] + "px: text edit creates one editable note and Undo");
            using (Bitmap edited = canvas.RenderImage())
            {
                edited.Save(System.IO.Path.Combine(output, "edited-" + Sizes[row] + ".png"), ImageFormat.Png);
                for (int other = 0; other < Sizes.Length; other++)
                {
                    Check(Same(image, edited, new Rectangle(18, Tops[other] - 2, 18, 21)), Sizes[row] + "px: PDF icon " + other + " retains exact original pixels");
                    if (other != row) Check(Same(image, edited, new Rectangle(0, Tops[other] - 3, image.Width, 30)), Sizes[row] + "px: neighboring row " + other + " retains exact original pixels");
                }
                Check(Same(image, edited, new Rectangle(436, Tops[row] - 3, 464, 30)), Sizes[row] + "px: neighboring columns retain exact original pixels");
                Check(!Same(image, edited, Rectangle.Ceiling(line.Bounds)), Sizes[row] + "px: committed edit actually changes filename pixels");
            }
            canvas.Undo();
            using (Bitmap restored = canvas.RenderImage())
                Check(Same(image, restored, new Rectangle(Point.Empty, image.Size)), Sizes[row] + "px: Undo restores the entire original image exactly");
        }
    }
    private static void PaneBoundary(string output)
    {
        using (Bitmap image = new Bitmap(320, 80, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(image))
            using (Font font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawLine(Pens.Red, 33, 18, 33, 38);
                TextRenderer.DrawText(graphics, "Sample.pdf", font, new Point(34, 20), Color.Black, Color.White,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            int left = 320, top = 80, right = -1, bottom = -1;
            for (int y = 0; y < image.Height; y++) for (int x = 34; x < image.Width; x++)
            {
                Color color = image.GetPixel(x, y);
                if (Math.Max(color.R, Math.Max(color.G, color.B)) > 96) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
            Check(left >= 34 && right > left, "pane boundary: known GDI filename has measured source ink");
            foreach (bool edit in new bool[] { false, true })
            using (EditorCanvas canvas = new EditorCanvas())
            {
                string label = "pane boundary " + (edit ? "edit" : "delete");
                canvas.Size = new Size(700, 300); canvas.CreateControl();
                EditorDocument document = (EditorDocument)Field(canvas, "document"); document.SetImage(image);
                TextLineRegion line = new TextLineRegion { Text = "Sample.pdf", Bounds = RectangleF.FromLTRB(left, top, right + 1, bottom + 1),
                    RecognitionClipBounds = new RectangleF(34, 0, 286, 80) };
                line.Words.Add(new TextWordRegion { Text = line.Text, Bounds = line.Bounds });
                TextRecognitionResult result = new TextRecognitionResult { Available = true, Text = line.Text }; result.Lines.Add(line);
                TextOverlaySelection selection = new TextOverlaySelection(result.Lines);
                Field(canvas, "recognizedText", result); Field(canvas, "recognizedBase", canvas.ImageIdentity);
                Field(canvas, "recognizedRevision", canvas.DocumentRevision); Field(canvas, "textSelection", selection);
                canvas.ActualSize(); canvas.Tool = EditorTool.Text; canvas.TextActionsMode = true;
                MethodInfo padding = typeof(EditorCanvas).GetMethod("PaddedTextBounds", Instance, null, new Type[] { typeof(RectangleF), typeof(RectangleF), typeof(RectangleF) }, null);
                Rectangle padded = (Rectangle)padding.Invoke(canvas, new object[] { line.Bounds, RectangleF.Empty, line.RecognitionClipBounds });
                Check(padded.Left >= 34, label + ": source crop clips erasure before the icon pixel");
                TranslationSourceSnapshot translation = canvas.CaptureTranslationSource(false);
                Check(translation.Blocks.Count == 1 && translation.Blocks[0].RecognitionClipBounds == line.RecognitionClipBounds,
                    label + ": translation snapshot retains the source crop boundary");
                if (edit)
                {
                    PointF origin = (PointF)canvas.GetType().GetProperty("ImageOrigin", Instance).GetValue(canvas, null);
                    MouseEventArgs mouse = new MouseEventArgs(MouseButtons.Left, 1, (int)Math.Round(origin.X + (left + 3) * canvas.Zoom), (int)Math.Round(origin.Y + (top + 3) * canvas.Zoom), 0);
                    Invoke(canvas, "OnMouseDown", mouse); Invoke(canvas, "OnMouseUp", mouse);
                    PumpUntil(delegate { return canvas.InlineTextEditing && !canvas.IsMatchingSourceText; }, label + ": actual source edit opens");
                    RichTextBox draft = (RichTextBox)Field(canvas, "inlineTextEditor"); draft.Select(draft.TextLength, 0); draft.SelectedText = " X";
                    Check(canvas.CommitInlineText(), label + ": modified source commits");
                }
                else
                {
                    selection.Select(0, 0);
                    Check(canvas.CanDeleteRecognizedText, label + ": selected source word is deletable");
                    canvas.DeleteRecognizedText();
                }
                using (Bitmap changed = canvas.RenderImage())
                {
                    Check(Same(image, changed, new Rectangle(33, 18, 1, 21)), label + ": the one-pixel icon edge stays exact");
                    Check(!Same(image, changed, Rectangle.Ceiling(line.Bounds)), label + ": actual filename pixels change");
                    changed.Save(System.IO.Path.Combine(output, "pane-boundary-" + (edit ? "edit" : "delete") + ".png"), ImageFormat.Png);
                }
                canvas.Undo();
                using (Bitmap restored = canvas.RenderImage()) Check(Same(image, restored, new Rectangle(Point.Empty, image.Size)), label + ": Undo restores every source pixel");
            }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string output = args.Length > 0 ? args[0] : ".";
            System.IO.Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Bitmap image = Fixture())
            {
                image.Save(System.IO.Path.Combine(output, "original.png"), ImageFormat.Png);
                Task<TextRecognitionResult> pending = TextRecognition.RecognizeAsync(image);
                PumpUntil(delegate { return pending.IsCompleted; }, "native OCR completes");
                TextRecognitionResult result = pending.GetAwaiter().GetResult();
                Check(result.Available, "native OCR available on this computer");
                System.IO.File.WriteAllText(System.IO.Path.Combine(output, "ocr.txt"), result.RawText + Environment.NewLine + "REFINED" + Environment.NewLine + result.Text);
                for (int row = 0; row < Sizes.Length; row++) RunRow(image, result, row, output);
            }
            PaneBoundary(output);
            Console.WriteLine("PASS: " + checks + " conversion pipeline checks; real Windows OCR, GDI TextRenderer fixture and production mouse/style/draft/commit/Undo paths; unshown controls.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
