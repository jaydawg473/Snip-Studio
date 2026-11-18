using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio OCR Audit Regressions")]
[assembly: AssemblyDescription("Selection geometry and measured optimization equivalence checks")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Must be signed and signature-verified before execution. Generated data only.
internal static class OcrAuditRegressions
{
    private static int assertions;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); assertions++; }
    private static string Hash(string path)
    { using (SHA256 sha = SHA256.Create()) using (Stream input = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""); }
    private static TextLineRegion Line(string text, RectangleF bounds)
    {
        TextLineRegion line = new TextLineRegion { Text = text, Bounds = bounds };
        line.Words.Add(new TextWordRegion { Text = text, Bounds = bounds });
        return line;
    }

    private static void RowSelection()
    {
        TextOverlaySelection model = new TextOverlaySelection(new List<TextLineRegion> {
            Line("PART-00123.pdf", new RectangleF(10, 10, 200, 9)),
            Line("PART-00042.pdf", new RectangleF(10, 38, 200, 9)),
            Line("11/18/2026", new RectangleF(400, 10, 120, 12)),
            Line("11/17/2026", new RectangleF(400, 38, 120, 12)) });
        Check(model.Nearest(new PointF(100, 21)) == 0, "Dragging below short filename glyphs stays in its column despite taller date glyphs");
        Check(model.Nearest(new PointF(100, 49)) == 1, "Same-row glyph tolerance also handles a later filename row");
        Check(model.Nearest(new PointF(420, 21)) == 2, "Point over date text still selects the date column");
        int end = model.Nearest(new PointF(100, 49));
        model.Select(0, end);
        Check(model.SelectedText == "PART-00123.pdf" + Environment.NewLine + "PART-00042.pdf", "Filename drag excludes neighboring date fields");
        TextOverlaySelection legacy = new TextOverlaySelection(new List<TextLineRegion> {
            Line("first", new RectangleF(10, 10, 149, 12)), Line("second", new RectangleF(10, 35, 110, 12)) });
        Check(legacy.Nearest(new PointF(500, 40)) == 1, "Far-right drag continues the nearest physical row");
        Check(legacy.Nearest(new PointF(Single.NaN, 40)) == -1, "Nonfinite drag position remains rejected");
        TextOverlaySelection tightlySpaced = new TextOverlaySelection(new List<TextLineRegion> {
            Line("short upper row", new RectangleF(10, 10, 90, 10)),
            Line("longer lower row", new RectangleF(10, 22, 390, 10)) });
        Check(tightlySpaced.Nearest(new PointF(200, 19)) == 0, "Far-right drag stays on a tightly spaced upper row rather than jumping to the longer row below");
        Check(tightlySpaced.Nearest(new PointF(200, 23)) == 1, "Moving into the next tight row follows that actual row");
        TextOverlaySelection indented = new TextOverlaySelection(new List<TextLineRegion> {
            Line("upper row", new RectangleF(10, 10, 80, 12)),
            Line("indented lower row", new RectangleF(150, 24, 80, 12)) });
        Check(indented.Nearest(new PointF(20, 24)) == 1, "Drag on an indented lower row does not jump to the closer word on a disjoint prior row");
    }

    private static void HighlightGeometry()
    {
        TextLineRegion first = Line("first", new RectangleF(10, 10, 50, 12));
        TextLineRegion second = Line("second", new RectangleF(10, 40, 50, 12));
        List<TextLineRegion> lines = new List<TextLineRegion> { first, second };
        PointF start = new PointF(15, 15), end = new PointF(15, 45);
        IList<RectangleF> initial = TextSnapSelection.BuildBands(lines, start, end, 4);
        Check(initial.Count == 2 && initial[0] == RectangleF.FromLTRB(8, 8, 62, 24) && initial[1] == RectangleF.FromLTRB(8, 38, 62, 54), "Initial cached highlight bands match exact expected source geometry");
        IList<RectangleF> warm = TextSnapSelection.BuildBands(lines, start, end, 4);
        Check(warm.Count == 2 && warm[0] == initial[0] && warm[1] == initial[1], "Repeated highlight calls preserve exact bands");
        first.Words[0].Bounds = new RectangleF(300, 10, 50, 12);
        Check(!TextSnapSelection.CanStart(lines, start, 4), "Mutating a word rectangle invalidates cached start geometry");
        Check(TextSnapSelection.CanStart(lines, new PointF(305, 15), 4), "New rectangle is used without replacing the list object");
        Check(TextSnapSelection.BuildBands(lines, new PointF(305, 15), end, 4).Count == 0, "Changed disjoint columns no longer bridge in a cached highlight");
        first.Words[0].Bounds = new RectangleF(10, 10, 50, 12);
        Check(TextSnapSelection.BuildBands(lines, start, end, 4).Count == 2, "Restored geometry invalidates and rebuilds correctly");
        second.Words.Clear();
        Check(!TextSnapSelection.CanStart(lines, end, 4), "Removing a word invalidates cached geometry");
        second.Words.Add(new TextWordRegion { Text = "restored", Bounds = new RectangleF(10, 40, 50, 12) });
        Check(TextSnapSelection.CanStart(lines, end, 4), "Adding a word restores valid start geometry");
        lines.Insert(0, new TextLineRegion());
        Check(TextSnapSelection.BuildBands(lines, start, end, 4).Count == 2, "Inserting an empty source line preserves source-order bands");
        second.Words[0].Bounds = new RectangleF(Single.NaN, 40, 50, 12);
        Check(!TextSnapSelection.CanStart(lines, end, 4), "Invalid geometry cannot survive a cache hit");
        Check(TextSnapSelection.BuildBands(null, start, end, 4).Count == 0, "Null source remains empty");
    }

    private static void ClearNativeCache()
    {
        Type matcher = typeof(SourceTextMatcher);
        ((IDictionary)matcher.GetField("NativeCache", Static).GetValue(null)).Clear();
        object order = matcher.GetField("NativeCacheOrder", Static).GetValue(null);
        order.GetType().GetMethod("Clear").Invoke(order, null);
        matcher.GetField("nativeCacheBytes", Static).SetValue(null, 0);
    }

    private static Bitmap StyleImage(float size, FontStyle style, Color color)
    {
        Bitmap bitmap = new Bitmap(460, 40, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (Font font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            TextRenderer.DrawText(graphics, "Sample source 00123_Technical.pdf", font, new Point(4, 3), color, Color.White,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        return bitmap;
    }

    private static SourceTextStyle Refine(Bitmap source, Color color, SourceTextStyle initial, CancellationToken token)
    {
        try
        {
            return (SourceTextStyle)typeof(SourceTextMatcher).GetMethod("RefineSizeOnUi", Static).Invoke(null,
                new object[] { source, new Rectangle(Point.Empty, source.Size), "Sample source 00123_Technical.pdf", color, Color.White, initial, token });
        }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    private static void NativeCacheEquivalence()
    {
        ClearNativeCache();
        foreach (object[] parameters in new object[][] {
            new object[] { 11f, FontStyle.Regular, Color.Black },
            new object[] { 13f, FontStyle.Regular, Color.Black },
            new object[] { 11f, FontStyle.Regular, Color.FromArgb(0, 94, 184) },
            new object[] { 11f, FontStyle.Bold, Color.Black } })
        using (Bitmap source = StyleImage((float)parameters[0], (FontStyle)parameters[1], (Color)parameters[2]))
        {
            Color color = (Color)parameters[2];
            SourceTextStyle initial = SourceTextMatcher.Estimate(source, new Rectangle(Point.Empty, source.Size),
                "Sample source 00123_Technical.pdf", color, Color.White, CancellationToken.None);
            SourceTextStyle observed = Refine(source, color, initial, CancellationToken.None);
            SourceTextStyle warm = Refine(source, color, initial, CancellationToken.None);
            ClearNativeCache();
            SourceTextStyle fresh = Refine(source, color, initial, CancellationToken.None);
            Check(observed.FontFamily == fresh.FontFamily && observed.FontSizePixels == fresh.FontSizePixels && observed.Style == fresh.Style,
                "Prior source inputs do not contaminate a different raster/color/model refinement");
            Check(warm.FontFamily == fresh.FontFamily && warm.FontSizePixels == fresh.FontSizePixels && warm.Style == fresh.Style &&
                warm.Confidence == fresh.Confidence && warm.Description == fresh.Description, "Cached native refinement equals a fresh complete evidence search");
            using (CancellationTokenSource canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                bool rejected = false;
                try { Refine(source, color, initial, canceled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "A completed native cache entry does not bypass cancellation");
            }
        }
        ClearNativeCache();
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected selected application SHA256 and output JSON path.");
            string app = typeof(TextRecognition).Assembly.Location;
            Check(Hash(app) == args[0].ToUpperInvariant(), "Regressions bind exact selected app bytes");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            RowSelection(); HighlightGeometry(); NativeCacheEquivalence();
            Check(Hash(app) == args[0].ToUpperInvariant(), "Selected app bytes remain unchanged");
            string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { Passed = true, Assertions = assertions,
                ApplicationSHA256 = args[0].ToUpperInvariant(), CheckedUtc = DateTimeOffset.UtcNow.ToString("o"),
                Scope = "Signed harness required by launcher. Source geometry/separator correctness and exact cache equivalence; generated data only." }));
            Console.WriteLine("PASS: " + assertions + " OCR selection/cache regressions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
