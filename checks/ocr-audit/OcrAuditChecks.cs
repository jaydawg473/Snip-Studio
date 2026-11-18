using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio OCR Audit Checks")]
[assembly: AssemblyDescription("Regression checks and measured local OCR/style/selection benchmarks")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Compile, sign and verify this executable before running it. The parent audit
// owns signing and execution. No application entrypoint, desktop/clipboard,
// recording, installed settings or supplied private image is accessed here.
internal static class OcrAuditChecks
{
    private static int assertions;
    private static readonly List<object> measurements = new List<object>();
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool pass, string name)
    { if (!pass) throw new InvalidOperationException(name); assertions++; }

    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (Stream input = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
    }

    private static TextLineRegion Line(string native, params string[] tokens)
    {
        TextLineRegion line = new TextLineRegion { Text = native };
        for (int index = 0; index < tokens.Length; index++)
            line.Words.Add(new TextWordRegion { Text = tokens[index], Bounds = new RectangleF(5 + index * 70, 10, 60, 12) });
        return line;
    }

    private static void SelectionCase(string native, string[] tokens, int first, int last, string expected)
    {
        TextLineRegion line = Line(native, tokens);
        TextOverlaySelection selection = new TextOverlaySelection(new List<TextLineRegion> { line });
        selection.Select(first, last);
        Check(selection.AllText == native, "Copy all preserves native line text");
        Check(selection.SelectedText == expected, "Copy selection preserves native separators");
        int start, length;
        Check(selection.TryGetSelectedCharacterRange(native, out start, out length), "Selection has a native character range");
        Check(native.Substring(start, length) == expected, "Copy selection agrees with the inline edit character range");
        string snapshot = selection.SelectedText;
        line.Text = "changed caller state";
        foreach (TextWordRegion word in line.Words) word.Text = "changed";
        Check(selection.SelectedText == snapshot, "Selection separators and words are immutable snapshots");
    }

    private static void SelectionRegressions()
    {
        SelectionCase("PART-00123_Technical  Details.pdf", new string[] { "PART-00123_", "Technical", "Details.pdf" }, 0, 2,
            "PART-00123_Technical  Details.pdf");
        SelectionCase("PART-00123_Technical  Details.pdf", new string[] { "PART-00123_", "Technical", "Details.pdf" }, 2, 1,
            "Technical  Details.pdf");
        SelectionCase("PART-00123_Technical.pdf", new string[] { "PART-00123", "Technical.pdf" }, 0, 1,
            "PART-00123_Technical.pdf");
        SelectionCase("零件名称", new string[] { "零件", "名称" }, 0, 1, "零件名称");
        SelectionCase("N0001\t\tN0001   00042", new string[] { "N0001", "N0001", "00042" }, 0, 2,
            "N0001\t\tN0001   00042");
        SelectionCase("alpha beta gamma", new string[] { "alpha", "beta", "gamma" }, 0, 2, "alpha beta gamma");
        TextLineRegion first = Line("A  B", "A", "B"), second = Line("C\tD", "C", "D");
        TextOverlaySelection multiline = new TextOverlaySelection(new List<TextLineRegion> { first, second });
        multiline.Select(0, 3);
        Check(multiline.SelectedText == "A  B" + Environment.NewLine + "C\tD", "Multiline selection preserves each line's separators and reading order");
        TextOverlaySelection unmapped = new TextOverlaySelection(new List<TextLineRegion> { Line("OCR spelling differs", "UNKNOWN", "TOKEN") });
        unmapped.Select(0, 1);
        Check(unmapped.SelectedText == "UNKNOWN TOKEN", "Unmappable native positions retain the established conservative fallback");
    }

    private static Bitmap TextImage(int width, int height, int rows)
    {
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (Font font = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            for (int row = 0; row < rows; row++)
                TextRenderer.DrawText(graphics, "Sample document 00123 row " + row + " keeps source text and values", font,
                    new Point(20, 20 + row * 25), Color.Black, Color.White, flags);
        }
        return bitmap;
    }

    private static TextRecognitionResult Recognize(Bitmap bitmap)
    {
        Task<TextRecognitionResult> pending = TextRecognition.RecognizeAsync(bitmap);
        Stopwatch timeout = Stopwatch.StartNew();
        while (!pending.IsCompleted && timeout.ElapsedMilliseconds < 45000)
        { Application.DoEvents(); Thread.Sleep(2); }
        Check(pending.IsCompleted, "Local OCR finishes within the audit's bounded wait");
        return pending.GetAwaiter().GetResult();
    }

    private static void OcrBenchmarks()
    {
        foreach (int[] dimensions in new int[][] { new int[] { 900, 400, 10 }, new int[] { 3200, 1800, 60 } })
        using (Bitmap source = TextImage(dimensions[0], dimensions[1], dimensions[2]))
        {
            List<long> elapsed = new List<long>();
            int count = 0, passes = 0;
            long before = Process.GetCurrentProcess().PrivateMemorySize64;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Stopwatch clock = Stopwatch.StartNew();
                TextRecognitionResult result = Recognize(source);
                elapsed.Add(clock.ElapsedMilliseconds);
                Check(result.Available, "Native OCR is available on this host");
                Check(result.Text.IndexOf("Sample", StringComparison.OrdinalIgnoreCase) >= 0, "Generated ordinary text remains recognized");
                count = result.Lines.Count; passes = result.RecoveryPasses;
            }
            measurements.Add(new { Kind = "LocalOcr", Width = dimensions[0], Height = dimensions[1],
                ElapsedMilliseconds = elapsed, ResultLines = count, RecoveryPasses = passes,
                ProcessPrivateBytesBefore = before, ProcessPrivateBytesAfter = Process.GetCurrentProcess().PrivateMemorySize64 });
        }
    }

    private static void CanceledQueueRegression()
    {
        FieldInfo gateField = typeof(TextRecognition).GetField("Gate", Static);
        SemaphoreSlim gate = (SemaphoreSlim)gateField.GetValue(null);
        using (Bitmap source = TextImage(2560, 1440, 20))
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            gate.Wait();
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                Task<TextRecognitionResult> queued = TextRecognition.RecognizeAsync(source, cancellation.Token);
                long synchronousSnapshotMilliseconds = clock.ElapsedMilliseconds;
                cancellation.Cancel();
                while (!queued.IsCompleted && clock.ElapsedMilliseconds < 5000)
                { Application.DoEvents(); Thread.Sleep(1); }
                Check(queued.IsCanceled, "Canceling an OCR request waiting behind another snip returns promptly");
                measurements.Add(new { Kind = "CanceledOcrQueue", Width = source.Width, Height = source.Height,
                    SynchronousSnapshotMilliseconds = synchronousSnapshotMilliseconds, TotalCancellationMilliseconds = clock.ElapsedMilliseconds });
            }
            finally { gate.Release(); }
        }
    }

    private static void StyleBenchmarks()
    {
        const string text = "README-Update-00123_Technical_Details.pdf";
        using (Bitmap source = new Bitmap(460, 30, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(source))
        using (Font font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            TextRenderer.DrawText(graphics, text, font, new Point(4, 3), Color.Black, Color.White,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            graphics.Flush();
            MethodInfo refine = typeof(SourceTextMatcher).GetMethod("RefineSizeOnUi", Static);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Stopwatch worker = Stopwatch.StartNew();
                SourceTextStyle style = Task.Run(delegate { return SourceTextMatcher.Estimate(source, new Rectangle(Point.Empty, source.Size),
                    text, Color.Black, Color.White, CancellationToken.None); }).GetAwaiter().GetResult();
                worker.Stop();
                Stopwatch ui = Stopwatch.StartNew();
                SourceTextStyle refined = (SourceTextStyle)refine.Invoke(null, new object[] { source, new Rectangle(Point.Empty, source.Size),
                    text, Color.Black, Color.White, style, CancellationToken.None });
                ui.Stop();
                Check(Math.Abs(refined.FontSizePixels - 11) <= 2, "Style benchmark stays near the generated source size");
                measurements.Add(new { Kind = "SourceStyle", Iteration = iteration, WorkerMilliseconds = worker.ElapsedMilliseconds,
                    SynchronousUiRefinementMilliseconds = ui.ElapsedMilliseconds, FontFamily = refined.FontFamily,
                    FontPixels = refined.FontSizePixels, Scope = "Generated 11px GDI source; timings are host-specific, font identity is not a universal claim." });
            }
        }
    }

    private static void SelectionBenchmark()
    {
        List<TextLineRegion> lines = new List<TextLineRegion>();
        for (int row = 0; row < 300; row++)
        {
            TextLineRegion line = new TextLineRegion { Text = "Generated source row" };
            for (int word = 0; word < 12; word++) line.Words.Add(new TextWordRegion { Text = "word", Bounds = new RectangleF(10 + word * 60, row * 18, 50, 12) });
            lines.Add(line);
        }
        Stopwatch clock = Stopwatch.StartNew();
        int generationZero = GC.CollectionCount(0), bands = 0;
        MethodInfo allocated = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
        long before = allocated == null ? 0 : (long)allocated.Invoke(null, null);
        for (int sample = 0; sample < 60; sample++)
            bands += TextSnapSelection.BuildBands(lines, new PointF(15, 5), new PointF(200, 100 + sample), 4).Count;
        long after = allocated == null ? 0 : (long)allocated.Invoke(null, null);
        clock.Stop();
        Check(bands > 0, "Large-document highlight benchmark selects valid bands");
        measurements.Add(new { Kind = "HighlightGeometry", Words = 3600, PointerSamples = 60, ElapsedMilliseconds = clock.ElapsedMilliseconds,
            AllocatedBytesOnCallingThread = allocated == null ? (long?)null : after - before, GenerationZeroCollections = GC.CollectionCount(0) - generationZero });
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
        try
        {
            if (args.Length < 2) throw new ArgumentException("Expected application SHA256 and output JSON path.");
            string appPath = typeof(TextRecognition).Assembly.Location;
            bool benchmarkOnly = args.Length > 2 && args[2] == "--benchmark-only";
            Check(Hash(appPath) == args[0].ToUpperInvariant(), "Audit is bound to the selected application bytes");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (!benchmarkOnly) SelectionRegressions();
            CanceledQueueRegression();
            SelectionBenchmark();
            OcrBenchmarks();
            StyleBenchmarks();
            Check(Hash(appPath) == args[0].ToUpperInvariant(), "Selected application bytes remain unchanged");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { Passed = true, Assertions = assertions,
                CheckedUtc = DateTimeOffset.UtcNow.ToString("o"), ApplicationSHA256 = args[0].ToUpperInvariant(),
                ApplicationVersion = typeof(TextRecognition).Assembly.GetName().Version.ToString(), BenchmarkOnly = benchmarkOnly, Measurements = measurements,
                Scope = "Signed harness required by launcher. Generated data, production OCR/style/selection methods, no app entrypoint or physical device acceptance." }));
            Console.WriteLine("PASS: " + assertions + " OCR audit assertions; measured timings saved.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
