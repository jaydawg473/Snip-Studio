using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SnipStudio;

internal static class SpacingChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo LocalState = typeof(Control).GetMethod("GetState", Instance);
    private static readonly List<string> samples = new List<string>();
    private static int assertions, measuredDefects;
    private static bool baseline, renderOnly;
    private static string output;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); assertions++; }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Instance).GetValue(owner); }
    private static void Field(object owner, string name, object value)
    { owner.GetType().GetField(name, Instance).SetValue(owner, value); }
    private static object Call(object owner, string name, params object[] args)
    { return owner.GetType().GetMethod(name, Instance).Invoke(owner, args); }
    private static bool LocalVisible(Control control)
    {
        // Control.Visible includes the visibility of its ancestors. This reads
        // only the local STATE_VISIBLE bit while the test form stays unshown.
        if (LocalState == null) throw new InvalidOperationException("Local Control visibility state is unavailable.");
        return control != null && !control.IsDisposed && (bool)LocalState.Invoke(control, new object[] { 0x2 });
    }
    private static void Arrange(MainForm form)
    {
        form.PerformLayout();
        ((Control)Field(form, "chromeRoot")).PerformLayout();
        Call(form, "LayoutChrome");
        ((Control)Field(form, "workspace")).PerformLayout();
    }
    private static bool Same(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
        return true;
    }
    private static PointF Absolute(Control control, MainForm form)
    {
        PointF location = PointF.Empty;
        for (Control current = control; current != null && current != form; current = current.Parent)
        { location.X += current.Left; location.Y += current.Top; }
        return location;
    }
    private static float VisibleTopBottom(MainForm form, string dock)
    {
        float bottom = 0;
        foreach (string name in new string[] { "captureToolbar", "contextToolbar" })
        {
            Control control = (Control)Field(form, name);
            if (LocalVisible(control) && control.Height > 0) bottom = Math.Max(bottom, Absolute(control, form).Y + control.Height);
        }
        Control annotation = (Control)Field(form, "annotationToolbar");
        if (dock == "Top" && LocalVisible(annotation)) bottom = Math.Max(bottom, Absolute(annotation, form).Y + annotation.Height);
        Control options = (Control)Field(form, "toolOptionsPopup");
        if ((bool)Field(form, "toolOptionsOpen") && LocalVisible(options)) bottom = Math.Max(bottom, Absolute(options, form).Y + options.Height);
        return bottom;
    }
    private static float VisibleBottomTop(MainForm form, string dock)
    {
        Control footer = (Control)Field(form, "footerStrip");
        float top = Absolute(footer, form).Y;
        Control annotation = (Control)Field(form, "annotationToolbar");
        if (dock == "Bottom" && LocalVisible(annotation)) top = Math.Min(top, Absolute(annotation, form).Y);
        return top;
    }
    private static PointF CenterOffset(EditorCanvas canvas)
    {
        PointF origin = (PointF)canvas.GetType().GetProperty("ImageOrigin", Instance).GetValue(canvas, null);
        return new PointF(origin.X + canvas.ImageSize.Width * canvas.Zoom / 2f - canvas.ClientSize.Width / 2f,
            origin.Y + canvas.ImageSize.Height * canvas.Zoom / 2f - canvas.ClientSize.Height / 2f);
    }
    private static void SaveActualClient(MainForm form, string name)
    {
        Control root = (Control)Field(form, "chromeRoot");
        Control oldParent = root.Parent;
        DockStyle oldDock = root.Dock;
        Rectangle oldBounds = root.Bounds;
        bool oldVisible = LocalVisible(root);
        using (Panel host = new Panel())
        using (Bitmap image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb))
        {
            host.Size = form.ClientSize; host.Font = form.Font; host.BackColor = form.BackColor; host.Visible = true;
            try
            {
                // Render the real production client tree in an unattached
                // visible Panel. No top-level form or desktop is shown/captured.
                root.Parent = host; root.Dock = DockStyle.Fill; root.Visible = true;
                host.CreateControl(); host.PerformLayout(); root.PerformLayout();
                host.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                int first = image.GetPixel(0, 0).ToArgb(), changed = 0;
                for (int y = 0; y < image.Height; y += 17)
                    for (int x = 0; x < image.Width; x += 17)
                        if (image.GetPixel(x, y).ToArgb() != first) changed++;
                Check(changed > 20, name + ": actual-control render contains visible content");
                image.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
                Console.WriteLine("RENDER: " + name + ".png; production client controls; no Form.Show or desktop capture");
            }
            finally
            {
                root.Parent = oldParent; root.Dock = oldDock; root.Bounds = oldBounds; root.Visible = oldVisible;
                Arrange(form);
            }
        }
    }
    private static void Measure(MainForm form, EditorCanvas canvas, EditorDocument document, Bitmap source,
        Rectangle window, long revision, object identity, string dock, float scale, string view, string state, PointF manualOffset)
    {
        Arrange(form);
        PointF origin = (PointF)canvas.GetType().GetProperty("ImageOrigin", Instance).GetValue(canvas, null);
        PointF position = Absolute(canvas, form);
        float actualTop = VisibleTopBottom(form, dock), actualBottom = VisibleBottomTop(form, dock);
        float top = position.Y + origin.Y, bottom = top + canvas.ImageSize.Height * canvas.Zoom;
        float gapTop = top - actualTop, gapBottom = actualBottom - bottom;
        Control workspace = (Control)Field(form, "workspace");
        float unused = Absolute(workspace, form).Y - actualTop;
        PointF offset = CenterOffset(canvas);
        string label = dock + " " + scale + " " + view + " " + state;
        // Record observations before checking policy so a failed assertion
        // retains the exact measured gaps and source-center quantization.
        samples.Add(String.Join(",", new string[] { dock, scale.ToString(CultureInfo.InvariantCulture), view, state,
            canvas.ImageSize.Width.ToString(), canvas.ImageSize.Height.ToString(), form.Width.ToString(), form.Height.ToString(),
            actualTop.ToString(CultureInfo.InvariantCulture), Absolute(workspace, form).Y.ToString(CultureInfo.InvariantCulture),
            actualBottom.ToString(CultureInfo.InvariantCulture), top.ToString(CultureInfo.InvariantCulture), bottom.ToString(CultureInfo.InvariantCulture),
            gapTop.ToString(CultureInfo.InvariantCulture), gapBottom.ToString(CultureInfo.InvariantCulture), unused.ToString(CultureInfo.InvariantCulture),
            canvas.Zoom.ToString(CultureInfo.InvariantCulture), offset.X.ToString(CultureInfo.InvariantCulture), offset.Y.ToString(CultureInfo.InvariantCulture) }));
        Check(form.Bounds == window, label + ": tool/options changes preserve window bounds");
        Check(canvas.DocumentRevision == revision && Object.ReferenceEquals(canvas.ImageIdentity, identity), label + ": source identity/revision stay unchanged");
        using (Bitmap rendered = document.RenderImage()) Check(Same(source, rendered), label + ": every original source pixel stays unchanged");
        Check(!form.Visible, label + ": test never shows a top-level form");
        if (baseline)
        {
            if (dock == "Top" && scale == 1 && view == "fit" && (state == "PenClosed" || state == "HighlightOpen") && unused > 1)
            {
                measuredDefects++;
                Console.WriteLine("BASELINE GAP " + state + ": hidden=" + unused + " top=" + gapTop + " bottom=" + gapBottom);
            }
        }
        else
        {
            Check(Math.Abs(unused) <= 1, label + ": workspace starts immediately after actual visible controls");
            if (view == "panned100")
            {
                Check(Math.Abs(canvas.Zoom - 1) <= .00001f, label + ": explicit manual zoom remains100 percent");
                Check(Math.Abs(offset.X - manualOffset.X) <= 1 && Math.Abs(offset.Y - manualOffset.Y) <= 1,
                    label + ": deliberate pan remains relative to viewport center");
            }
            else
            {
                if (view == "manual100")
                {
                    // A manually fixed scale retains its fractional center
                    // model. A half-pixel tie after an odd viewport change can
                    // project one rendered pixel off center (two gap pixels).
                    Check(Math.Abs(offset.X) <= 1 && Math.Abs(offset.Y) <= 1,
                        label + ": centered manual100 stays within one rendered pixel of viewport center");
                    Check(Math.Abs(offset.X - manualOffset.X) <= 1 && Math.Abs(offset.Y - manualOffset.Y) <= 1,
                        label + ": manual100 source-center anchor stays within one rendered pixel");
                    Check(Math.Abs(canvas.Zoom - 1) <= .00001f, label + ": manual100 percent is not refitted");
                }
                else Check(Math.Abs(gapTop - gapBottom) <= 1.01f, label + ": gaps measured from actual controls balance within one pixel");
                if (view == "fit" || view == "tallfit")
                {
                    bool requested = (bool)canvas.GetType().GetProperty("FitRequested", Instance).GetValue(canvas, null);
                    Check(requested, label + ": fitted view retains fit intent");
                    Check(origin.X >= -1 && origin.Y >= -1 && origin.X + canvas.ImageSize.Width * canvas.Zoom <= canvas.ClientSize.Width + 1 &&
                        origin.Y + canvas.ImageSize.Height * canvas.Zoom <= canvas.ClientSize.Height + 1, label + ": fitted source stays fully contained after viewport change");
                    Check(!canvas.HorizontalScroll.Visible && !canvas.VerticalScroll.Visible, label + ": fitted view has no unnecessary native scrollbars");
                }
            }
        }
        if (renderOnly && dock == "Top" && scale == 1 && view == "fit" && (state == "PenClosed" || state == "HighlightOpen"))
            SaveActualClient(form, state == "PenClosed" ? "closed-pen-client" : "open-highlight-client");
    }
    private static void Case(string dock, float scale, string view)
    {
        MainForm form = (MainForm)Activator.CreateInstance(typeof(MainForm), Instance, null, new object[] { null, true }, null);
        using (form)
        using (Bitmap image = new Bitmap(759, view == "tallfit" ? 480 : 301, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.SteelBlue, 20, 20, 180, 70);
                graphics.DrawRectangle(Pens.OrangeRed, 4, 4, image.Width - 9, image.Height - 9);
            }
            Field(form, "arrangingChrome", true);
            try
            {
                Field(form, "LayoutScaleOverride", scale);
                Field(form, "LayoutWorkingAreaOverride", new Rectangle(-5000, 0, 5000, 3200));
                Preferences preferences = (Preferences)Field(form, "preferences");
                preferences.ResizeToSnip = false; preferences.AnnotationToolbarPosition = dock;
                form.MinimumSize = Size.Empty;
                form.Bounds = new Rectangle(-4900, 30, (int)Math.Round(870 * scale), (int)Math.Round(606 * scale));
            }
            finally { Field(form, "arrangingChrome", false); }
            EditorCanvas canvas = (EditorCanvas)Field(form, "canvas");
            Field(canvas, "snapHighlightToText", false);
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            document.SetImage(image);
            document.MarkSaved();
            Call(form, "UpdateDocumentState");
            Call(form, "TrackInitialImageView");
            Call(form, "InitialImageFitUserResizeBegin", form, EventArgs.Empty);
            Call(form, "SelectTool", EditorTool.Pen); Call(form, "CloseToolOptions"); Arrange(form);
            canvas.FitToWindow(); Arrange(form);
            if (view == "manual100" || view == "panned100")
            {
                canvas.ZoomAt(1, new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2));
                if (view == "panned100") Call(canvas, "PanView", (float)17, (float)-13);
            }
            Arrange(form);
            PointF manualOffset = CenterOffset(canvas);
            Rectangle window = form.Bounds;
            long revision = canvas.DocumentRevision;
            object identity = canvas.ImageIdentity;
            Measure(form, canvas, document, image, window, revision, identity, dock, scale, view, "PenClosed", manualOffset);
            Call(form, "SelectTool", EditorTool.Highlighter); Arrange(form);
            Check((bool)Field(form, "toolOptionsOpen"), "Highlight options are actually open");
            Measure(form, canvas, document, image, window, revision, identity, dock, scale, view, "HighlightOpen", manualOffset);
            Call(form, "SelectTool", EditorTool.Eraser); Arrange(form);
            Measure(form, canvas, document, image, window, revision, identity, dock, scale, view, "EraserOpen", manualOffset);
            Call(form, "SelectTool", EditorTool.Text);
            MethodInfo begin = typeof(EditorCanvas).GetMethod("BeginInlineTextNote", Instance, null, new Type[] { typeof(RectangleF) }, null);
            Check((bool)begin.Invoke(canvas, new object[] { new RectangleF(30, 110, 240, 60) }), "Inline text context starts");
            Arrange(form);
            Check(((Control)Field(form, "contextToolbar")).Height > 0 && (bool)Field(form, "toolOptionsOpen"), "Text context and options are simultaneously present");
            Measure(form, canvas, document, image, window, revision, identity, dock, scale, view, "TextInline", manualOffset);
            canvas.CancelInlineText();
            Call(form, "SelectTool", EditorTool.Crop); Arrange(form);
            Check(((Control)Field(form, "contextToolbar")).Height > 0, "Crop context is actually present");
            Measure(form, canvas, document, image, window, revision, identity, dock, scale, view, "Crop", manualOffset);
            Call(form, "SelectTool", EditorTool.Pen); Call(form, "CloseToolOptions"); Arrange(form);
            Measure(form, canvas, document, image, window, revision, identity, dock, scale, view, "PenClosedAgain", manualOffset);
            Check(!canvas.CanUndo && !canvas.IsDirty, "Tool/context cycles do not commit an edit or create history");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(".");
            baseline = args.Length > 1 && args[1] == "--baseline";
            renderOnly = args.Length > 1 && args[1] == "--render";
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            samples.Add("Dock,Scale,View,State,ImageWidth,ImageHeight,WindowWidth,WindowHeight,ActualTop,WorkspaceTop,ActualBottom,ImageTop,ImageBottom,TopGap,BottomGap,HiddenBand,Zoom,CenterOffsetX,CenterOffsetY");
            if (baseline)
            {
                Case("Top", 1, "fit"); Case("Top", 1, "manual100");
                Check(measuredDefects >= 2, "Signed baseline reproduces the closed and open hidden top band");
            }
            else if (renderOnly) Case("Top", 1, "fit");
            else
                foreach (string dock in new string[] { "Top", "Bottom", "Left", "Right" })
                    foreach (float scale in new float[] { 1, 1.5f, 2 })
                        foreach (string view in new string[] { "fit", "manual100", "panned100", "tallfit" }) Case(dock, scale, view);
            File.WriteAllLines(Path.Combine(output, "spacing-observations.csv"), samples.ToArray());
            Console.WriteLine("PASS: " + assertions + " spacing assertions; mode=" + (baseline ? "baseline" : renderOnly ? "render" : "fixed") + "; measured baseline defects=" + measuredDefects + "; unshown production controls, actual local visibility and logical DPI overrides.");
            return 0;
        }
        catch (Exception error)
        {
            if (output != null) File.WriteAllLines(Path.Combine(output, "spacing-observations.csv"), samples.ToArray());
            Console.Error.WriteLine("FAIL after " + assertions + " assertions: " + error);
            return 1;
        }
    }
}
