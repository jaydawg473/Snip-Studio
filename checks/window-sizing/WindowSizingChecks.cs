using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SnipStudio;

internal static class WindowSizingChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static string output;
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Instance).GetValue(owner); }
    private static void Field(object owner, string name, object value)
    { owner.GetType().GetField(name, Instance).SetValue(owner, value); }
    private static object Call(object owner, string name, params object[] arguments)
    { return owner.GetType().GetMethod(name, Instance).Invoke(owner, arguments); }
    private static void Arrange(MainForm form)
    {
        form.PerformLayout();
        ((Control)Field(form, "chromeRoot")).PerformLayout();
        Call(form, "LayoutChrome");
        ((Control)Field(form, "workspace")).PerformLayout();
    }
    private static MainForm NewForm(float scale, string side, out EditorCanvas canvas, out EditorDocument document)
    {
        MainForm form = (MainForm)Activator.CreateInstance(typeof(MainForm), Instance, null, new object[] { null, true }, null);
        Field(form, "arrangingChrome", true);
        try
        {
            Field(form, "LayoutScaleOverride", scale);
            Field(form, "LayoutWorkingAreaOverride", new Rectangle(-4000, 0, 4000, 2600));
            Preferences preferences = (Preferences)Field(form, "preferences");
            preferences.ResizeToSnip = true;
            preferences.AnnotationToolbarPosition = side;
            form.MinimumSize = Size.Empty;
            form.Bounds = new Rectangle(-3950, 40, (int)(850 * scale), (int)(600 * scale));
        }
        finally { Field(form, "arrangingChrome", false); }
        canvas = (EditorCanvas)Field(form, "canvas");
        document = (EditorDocument)Field(canvas, "document");
        using (Bitmap image = new Bitmap(144, 121, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                graphics.FillEllipse(Brushes.OrangeRed, 18, 16, 35, 35);
                graphics.FillRectangle(Brushes.RoyalBlue, 75, 16, 35, 35);
                using (Font font = new Font("Segoe UI", 11, GraphicsUnit.Pixel))
                    graphics.DrawString("Small snip 00123", font, Brushes.Black, 8, 78);
            }
            document.SetImage(image);
        }
        Call(form, "UpdateDocumentState");
        Call(form, "TrackInitialImageView");
        Call(form, "ApplyInitialImageFit", true);
        Arrange(form);
        return form;
    }
    private static bool Same(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
        return true;
    }
    private static void Reachable(Panel panel, string label)
    {
        bool overflow = panel.AutoScrollMinSize.Width > panel.ClientSize.Width;
        if (overflow) Check(panel.AutoScroll, label + ": overflowing command row scrolls");
        Check(panel.Height > 0, label + ": toolbar has a visible-height band");
    }
    private static void Case(float scale, string side, bool manualZoom)
    {
        string label = side + " " + scale + "x " + (manualZoom ? "manual" : "fit");
        EditorCanvas canvas; EditorDocument document;
        using (MainForm form = NewForm(scale, side, out canvas, out document))
        using (Bitmap original = document.RenderImage())
        {
            Check(form.MinimumSize.Width > (int)(500 * scale), label + ": initial shell keeps preferred complete-command width");
            Size initialMinimum = form.MinimumSize;
            Call(form, "SelectTool", EditorTool.Eraser);
            Arrange(form);
            if (manualZoom) canvas.ZoomAt(1.4f, new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2));
            float zoom = canvas.Zoom;
            form.Size = new Size((int)(1400 * scale), (int)(900 * scale));
            Arrange(form);
            Size large = form.Size;
            Call(form, "InitialImageFitUserResizeBegin", form, EventArgs.Empty);
            Arrange(form);
            Check(form.Size == large, label + ": beginning manual resize does not forcibly shrink window");
            Check(form.MinimumSize.Width == (int)Math.Round(500 * scale), label + ": manual minimum width is compact");
            Check(form.MinimumSize.Height < initialMinimum.Height, label + ": manual height no longer reserves native-image/side-toolbar floor");
            form.Width = form.MinimumSize.Width;
            Arrange(form);
            Size compact = form.MinimumSize;
            form.Height = compact.Height;
            Arrange(form);
            Check(form.Size == compact, label + ": can shrink from enlarged frame to compact tracking size");
            Check(form.Width < large.Width && form.Height < large.Height, label + ": both axes reclaim open canvas");
            Check(canvas.ClientSize.Width > 0 && canvas.ClientSize.Height > 0, label + ": image viewport remains usable");
            Control footer = (Control)Field(form, "footerStrip");
            Check(footer.Bottom <= form.ClientSize.Height && footer.Top >= 0, label + ": footer remains inside client");
            Reachable((Panel)Field(form, "captureToolbar"), label + " capture");
            Panel annotation = (Panel)Field(form, "annotationToolbar");
            if (side == "Top" || side == "Bottom") Reachable(annotation, label + " annotation");
            else Check(annotation.AutoScroll && annotation.AutoScrollMinSize.Height > 0, label + ": side commands remain scrollable");
            if (manualZoom) Check(Math.Abs(canvas.Zoom - zoom) < .00001, label + ": manual resize keeps deliberate zoom");
            else Check(canvas.Zoom > 0 && canvas.ImageSize.Width * canvas.Zoom <= canvas.ClientSize.Width + 1 &&
                canvas.ImageSize.Height * canvas.Zoom <= canvas.ClientSize.Height + 1, label + ": fitted image remains contained");
            foreach (EditorTool tool in new EditorTool[] { EditorTool.Pen, EditorTool.Rectangle, EditorTool.Eraser })
            {
                Call(form, "SelectTool", tool);
                Arrange(form);
                Check(form.Size == compact && form.MinimumSize.Width == compact.Width, label + ": " + tool + " does not regrow manually compact frame");
            }
            using (Bitmap actual = document.RenderImage()) Check(Same(original, actual), label + ": resizing/options preserve every source pixel");
            Check(!canvas.CanUndo, label + ": resizing does not create edit history");
            if (scale == 1 && side == "Top" && !manualZoom)
            {
                document.ReplaceImageText(new Rectangle(8, 78, 115, 18), "AB-00123", Color.Black, Color.White, 11, 120);
                Call(form, "TrackInitialImageView"); Arrange(form);
                Check((bool)Field(form, "userSizedEditorWindow") && form.MinimumSize.Width == compact.Width,
                    "raster text edit preserves manual compact tracking policy");
                Call(form, "ResizeForSnip"); Arrange(form);
                Check(form.Size == compact, "text edit sizing policy keeps manually compact frame");
                document.Undo(); Call(form, "TrackInitialImageView"); Arrange(form);
                Check((bool)Field(form, "userSizedEditorWindow") && form.Size == compact,
                    "Undo to initial raster preserves user sizing intent");
                using (Bitmap restored = document.RenderImage()) Check(Same(original, restored), "raster edit Undo restores source pixels");
                Call(form, "ResizeForSnip"); Arrange(form);
                Check(form.Size == compact, "Undo sizing policy keeps manually compact frame");
                document.Crop(new Rectangle(0, 0, 120, 100));
                Call(form, "TrackInitialImageView"); Arrange(form);
                Check((bool)Field(form, "userSizedEditorWindow"), "crop keeps manual sizing intent");
                Call(form, "ResizeForSnip"); Arrange(form);
                Check(form.Size == compact, "crop sizing policy keeps manually compact frame");
                using (Bitmap next = new Bitmap(144, 121)) document.SetImage(next);
                Call(form, "TrackInitialImageView"); Arrange(form);
                Check(!(bool)Field(form, "userSizedEditorWindow") && form.MinimumSize.Width > compact.Width,
                    "fresh image restores preferred initial shell policy");
            }
            Check(!form.Visible, label + ": no top-level test window is shown");
            Console.WriteLine(label + " preferred=" + initialMinimum + " compact=" + compact + " large=" + large + " viewport=" + canvas.ClientSize);
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            output = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (float scale in new float[] { 1, 1.5f, 2 })
                foreach (string side in new string[] { "Top", "Bottom", "Left", "Right" })
                    foreach (bool manual in new bool[] { false, true }) Case(scale, side, manual);
            Console.WriteLine("PASS: " + checks + " window sizing assertions; production geometry and controls remain unshown; logical DPI overrides, not physical monitor testing.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
