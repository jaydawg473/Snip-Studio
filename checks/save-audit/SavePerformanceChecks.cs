using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio Save Performance Checks")]
[assembly: AssemblyDescription("Synthetic large-image render, PNG and PDF baseline measurements")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Compile, sign and verify before execution. No app entrypoint, user files,
// clipboard, preferences, desktop capture, registry writes or displayed forms.
internal static class SavePerformanceChecks
{
    private static readonly List<object> measurements = new List<object>();
    private static int assertions;
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); assertions++; }
    private static string Hash(string file)
    {
        using (SHA256 sha = SHA256.Create())
        using (Stream input = File.OpenRead(file))
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
    }
    private static void Measure(string fixture, string operation, Action action, string output)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        long before = process.PrivateMemorySize64, peak = before;
        object gate = new object();
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        Stopwatch clock = Stopwatch.StartNew();
        using (Timer sample = new Timer(delegate
        {
            try
            {
                using (Process current = Process.GetCurrentProcess())
                { long bytes = current.PrivateMemorySize64; lock (gate) if (bytes > peak) peak = bytes; }
            }
            catch (InvalidOperationException) { }
        }, null, 0, 10)) action();
        clock.Stop(); process.Refresh();
        lock (gate) peak = Math.Max(peak, process.PrivateMemorySize64);
        measurements.Add(new { Fixture = fixture, Width = 6000, Height = 4000, Operation = operation,
            ElapsedMilliseconds = clock.ElapsedMilliseconds, PrivateBytesBefore = before,
            PrivateBytesAfter = process.PrivateMemorySize64, SampledPeakPrivateBytes = peak,
            SampleIntervalMilliseconds = 10, Gen0Collections = GC.CollectionCount(0) - gc0,
            Gen1Collections = GC.CollectionCount(1) - gc1, Gen2Collections = GC.CollectionCount(2) - gc2,
            OutputBytes = output == null ? 0 : new FileInfo(output).Length });
        Console.WriteLine(fixture + " " + operation + ": " + clock.ElapsedMilliseconds + " ms");
        process.Dispose();
    }
    private static Bitmap Fixture(bool noisy)
    {
        Bitmap image = new Bitmap(6000, 4000, PixelFormat.Format32bppArgb);
        if (!noisy)
        {
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                for (int index = 0; index < 80; index++)
                {
                    graphics.FillRectangle(index % 2 == 0 ? Brushes.RoyalBlue : Brushes.LightGray,
                        50 + (index % 10) * 580, 50 + (index / 10) * 480, 400, 250);
                }
            }
        }
        else
        {
            Random random = new Random(20261118);
            byte[] row = new byte[image.Width * 4];
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < image.Height; y++)
                {
                    random.NextBytes(row);
                    for (int x = 3; x < row.Length; x += 4) row[x] = 255;
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally { image.UnlockBits(data); }
        }
        return image;
    }
    private static void CheckPng(Bitmap source, string path)
    {
        using (Bitmap loaded = new Bitmap(path))
        {
            Check(loaded.Size == source.Size, "PNG preserves all source dimensions");
            for (int index = 0; index < 20; index++)
            {
                int x = (index * 311 + 13) % source.Width, y = (index * 197 + 29) % source.Height;
                Check(loaded.GetPixel(x, y).ToArgb() == source.GetPixel(x, y).ToArgb(), "PNG sample pixels remain lossless");
            }
        }
    }
    private static void CheckPdf(string path)
    {
        using (FileStream input = File.OpenRead(path))
        {
            byte[] head = new byte[Math.Min(4096, (int)input.Length)];
            input.Read(head, 0, head.Length);
            string header = Encoding.ASCII.GetString(head);
            Check(header.StartsWith("%PDF-1.4"), "PDF header is complete");
            Check(header.Contains("/Width 6000 /Height 4000"), "PDF retains all source pixels in image dimensions");
            input.Position = Math.Max(0, input.Length - 100);
            byte[] tail = new byte[input.Length - input.Position]; input.Read(tail, 0, tail.Length);
            Check(Encoding.ASCII.GetString(tail).Contains("%%EOF"), "PDF trailer is complete");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected app SHA256 and unique output directory.");
            string app = typeof(EditorDocument).Assembly.Location;
            Check(Hash(app).Equals(args[0], StringComparison.OrdinalIgnoreCase), "Exact selected production app");
            string folder = Path.GetFullPath(args[1]);
            if (Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0) throw new IOException("Output directory must be empty; preserve previous evidence.");
            Directory.CreateDirectory(folder);
            foreach (bool noisy in new bool[] { false, true })
            using (Bitmap source = Fixture(noisy))
            using (EditorDocument document = new EditorDocument())
            {
                string fixture = noisy ? "opaque-noise" : "compressible";
                document.SetImage(source);
                Bitmap rendered = null;
                Measure(fixture, "RenderImage", delegate { rendered = document.RenderImage(); }, null);
                using (rendered)
                {
                    string png = Path.Combine(folder, fixture + ".png"), pdf = Path.Combine(folder, fixture + ".pdf");
                    Measure(fixture, "PngEncodeAndWrite", delegate { rendered.Save(png, ImageFormat.Png); }, png);
                    CheckPng(rendered, png);
                    Measure(fixture, "PdfFlattenCompressAndWrite", delegate { PdfExportService.Save(rendered, pdf); }, pdf);
                    CheckPdf(pdf);
                    Check(Directory.GetFiles(folder, ".snip-*.tmp").Length == 0, "Completed save leaves no temporary file");
                }
            }
            object report = new { Passed = true, Assertions = assertions, AppSHA256 = args[0].ToUpperInvariant(),
                AppVersion = typeof(EditorDocument).Assembly.GetName().Version.ToString(), CheckedUtc = DateTimeOffset.UtcNow.ToString("o"),
                Measurements = measurements, Scope = "Single local synthetic 24MP run per content/format. Encoding was called synchronously on the test STA, matching SaveImage's current encode path. PNG validation samples twenty pixels; PDF validation covers header, image dimensions and trailer only. Sampled memory is not an exact native peak. No physical UI, app entrypoint or installed settings." };
            File.WriteAllText(Path.Combine(folder, "save-performance.json"), new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            Console.WriteLine("PASS: " + assertions + " save benchmark checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
