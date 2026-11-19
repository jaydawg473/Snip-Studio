using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

internal static class Program
{
    private enum Channels { Color, Green, Minimum, Maximum, Luminance, ThresholdGreen }
    private sealed record Variant(string Name, int Scale, InterpolationMode Interpolation, Channels Channels, bool Rows, int Padding, int Inset = 0);
    private sealed record Word(string Text, double X, double Y, double Width, double Height);
    private sealed record Line(string Text, Word[] Words);
    private sealed record NativeRead(string RawText, string Language, double? TextAngle, Line[] Lines);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        if ((args.Length != 4 && args.Length != 6) || args[0] != "--fixtures" || args[2] != "--output" ||
            (args.Length == 6 && (args[4] != "--set" || args[5] is not ("quick" or "all"))))
        {
            Console.Error.WriteLine("Usage: SnipStudio.Ocr.Probes --fixtures <private JSON manifest> --output <empty evidence directory> [--set quick|all]");
            return 2;
        }
        string output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use an empty evidence directory. Existing probe evidence is never overwritten.");
            return 2;
        }
        Directory.CreateDirectory(output);
        var results = new List<object>();
        var clock = Stopwatch.StartNew();
        string? failure = null;
        bool completed = false;
        try
        {
            using var wholeRun = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            Task.Run(() => RunAsync(args[1], output, args.Length == 6 && args[5] == "all", results, wholeRun.Token))
                .GetAwaiter().GetResult();
            completed = true;
            return 0;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            var evidence = new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                Completed = completed,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Scope = "Diagnostic Windows OCR preparation comparisons on private supplied images. Each result is uncorrected native output. Expected strings are used only for private ordinal comparison metrics, never for recognition, character substitution, variant selection or production output. No source image or document is modified.",
                Results = results,
                Failure = failure
            };
            File.WriteAllText(Path.Combine(output, "ocr-preparation-probes.json"), JsonSerializer.Serialize(evidence, JsonOptions), new UTF8Encoding(false));
        }
    }

    private static Variant[] Variants(bool all)
    {
        var variants = new List<Variant>
        {
            new("native-exact", 1, InterpolationMode.NearestNeighbor, Channels.Color, false, 0),
            new("native-padding", 1, InterpolationMode.NearestNeighbor, Channels.Color, false, 12),
            new("nearest-2", 2, InterpolationMode.NearestNeighbor, Channels.Color, false, 12),
            new("nearest-3", 3, InterpolationMode.NearestNeighbor, Channels.Color, false, 12),
            new("nearest-4", 4, InterpolationMode.NearestNeighbor, Channels.Color, false, 12),
            new("bicubic-2", 2, InterpolationMode.HighQualityBicubic, Channels.Color, false, 12),
            new("bilinear-3", 3, InterpolationMode.HighQualityBilinear, Channels.Color, false, 12),
            new("green-nearest-3", 3, InterpolationMode.NearestNeighbor, Channels.Green, false, 12),
            new("minimum-nearest-3", 3, InterpolationMode.NearestNeighbor, Channels.Minimum, false, 12),
            new("maximum-nearest-3", 3, InterpolationMode.NearestNeighbor, Channels.Maximum, false, 12),
            new("rows-native", 1, InterpolationMode.NearestNeighbor, Channels.Color, true, 12),
            new("rows-green-nearest-3", 3, InterpolationMode.NearestNeighbor, Channels.Green, true, 12)
        };
        if (all) variants.AddRange(new[]
        {
            new Variant("green-nearest-4", 4, InterpolationMode.NearestNeighbor, Channels.Green, false, 12),
            new Variant("bicubic-2-no-padding", 2, InterpolationMode.HighQualityBicubic, Channels.Color, false, 0),
            new Variant("threshold-green-nearest-3", 3, InterpolationMode.NearestNeighbor, Channels.ThresholdGreen, false, 12),
            new Variant("rows-maximum-nearest-4", 4, InterpolationMode.NearestNeighbor, Channels.Maximum, true, 12),
            new Variant("border-inset-bicubic-2", 2, InterpolationMode.HighQualityBicubic, Channels.Color, false, 12, 1)
        });
        return variants.ToArray();
    }

    private static async Task RunAsync(string manifestPath, string output, bool all, List<object> results, CancellationToken token)
    {
        byte[] manifest = File.ReadAllBytes(manifestPath);
        if (manifest.Length > 1024 * 1024) throw new ArgumentException("The fixture manifest is too large.");
        using JsonDocument document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 12 });
        JsonElement fixtures = document.RootElement.GetProperty("Fixtures");
        if (fixtures.ValueKind != JsonValueKind.Array || fixtures.GetArrayLength() is < 1 or > 8)
            throw new ArgumentException("Choose between one and eight private fixtures per bounded probe run.");
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) throw new InvalidOperationException("No Windows OCR profile language is installed.");
        int maximum = checked((int)OcrEngine.MaxImageDimension);
        File.WriteAllText(Path.Combine(output, "recognizer.json"), JsonSerializer.Serialize(new
        {
            Language = engine.RecognizerLanguage.LanguageTag,
            InstalledLanguages = OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray(),
            MaximumImageDimension = maximum,
            RuntimeVersion = Environment.Version.ToString(),
            Environment.OSVersion
        }, JsonOptions), new UTF8Encoding(false));
        int fixtureIndex = 0;
        foreach (JsonElement fixture in fixtures.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            string name = fixture.GetProperty("Name").GetString() ?? throw new ArgumentException("A fixture name is missing.");
            string path = fixture.GetProperty("InputPath").GetString() ?? throw new ArgumentException("A fixture input is missing.");
            string[] expected = fixture.TryGetProperty("ExpectedLines", out var values)
                ? values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : Array.Empty<string>();
            byte[] input = File.ReadAllBytes(path);
            using var stream = new MemoryStream(input, false);
            using var decoded = new Bitmap(stream);
            if (decoded.Width is < 1 or > 4096 || decoded.Height is < 1 or > 4096 || (long)decoded.Width * decoded.Height > 8L * 1024 * 1024)
                throw new ArgumentException("Use a diagnostic fixture of at most 4,096 pixels per side and eight million pixels.");
            using var source = decoded.Clone(new Rectangle(0, 0, decoded.Width, decoded.Height), PixelFormat.Format32bppArgb);
            Rectangle[] rowBands = FindInkRows(source);
            foreach (Variant variant in Variants(all))
            {
                token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                string prefix = $"{fixtureIndex:D2}-{variant.Name}";
                Rectangle[] regions = variant.Rows ? rowBands : new[] { new Rectangle(variant.Inset, variant.Inset,
                    source.Width - variant.Inset * 2, source.Height - variant.Inset * 2) };
                var reads = new List<object>();
                var actualLines = new List<string>();
                foreach (Rectangle region in regions)
                {
                    token.ThrowIfCancellationRequested();
                    if (region.Width < 1 || region.Height < 1)
                    {
                        reads.Add(new { Skipped = true, Reason = "The diagnostic inset does not fit this image.", SourceRegion = Bounds(region) });
                        continue;
                    }
                    if ((region.Width + variant.Padding * 2) * variant.Scale > maximum ||
                        (region.Height + variant.Padding * 2) * variant.Scale > maximum)
                    {
                        reads.Add(new { Skipped = true, Reason = "Prepared image exceeds Windows OCR maximum dimensions.", SourceRegion = Bounds(region) });
                        continue;
                    }
                    using Bitmap prepared = Prepare(source, region, variant);
                    string imagePath = Path.Combine(output, prefix + $"-{reads.Count:D3}.png");
                    prepared.Save(imagePath, ImageFormat.Png);
                    NativeRead read = await ReadNativeBoundedAsync(engine, prepared, variant, region, token);
                    reads.Add(new
                    {
                        SourceRegion = Bounds(region), PreparedWidth = prepared.Width, PreparedHeight = prepared.Height,
                        PreparedImageSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))),
                        read.RawText, read.Language, read.TextAngle, read.Lines
                    });
                    actualLines.AddRange(read.Lines.Select(line => line.Text));
                }
                int exactAtSamePosition = expected.Length == 0 ? 0 : expected.Where((line, index) =>
                    index < actualLines.Count && string.Equals(line, actualLines[index], StringComparison.Ordinal)).Count();
                var result = new
                {
                    FixtureIndex = fixtureIndex, FixtureName = name,
                    InputSHA256 = Convert.ToHexString(SHA256.HashData(input)), Width = source.Width, Height = source.Height,
                    Variant = variant.Name, variant.Scale, Interpolation = variant.Interpolation.ToString(), Channels = variant.Channels.ToString(),
                    variant.Padding, variant.Inset, RowSegmentation = variant.Rows, MeasuredRowBands = rowBands.Select(Bounds).ToArray(),
                    Reads = reads, ActualLines = actualLines, ExpectedLineCount = expected.Length,
                    ExactLinesAtOriginalPosition = exactAtSamePosition,
                    ExactOrdinalLineOrder = expected.Length != 0 && expected.SequenceEqual(actualLines, StringComparer.Ordinal),
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
                };
                results.Add(result);
                File.WriteAllText(Path.Combine(output, prefix + ".json"), JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
                Console.WriteLine($"{fixtureIndex:D2} {variant.Name}: {actualLines.Count} lines; {exactAtSamePosition}/{expected.Length} ordinal matches; {watch.Elapsed.TotalMilliseconds:0.0} ms");
            }
            fixtureIndex++;
        }
    }

    private static object Bounds(Rectangle value) => new { value.X, value.Y, value.Width, value.Height };

    private static Bitmap Prepare(Bitmap source, Rectangle region, Variant variant)
    {
        using Bitmap pixels = source.Clone(region, PixelFormat.Format32bppArgb);
        if (variant.Channels != Channels.Color) TransformChannels(pixels, variant.Channels);
        int width = checked((region.Width + variant.Padding * 2) * variant.Scale);
        int height = checked((region.Height + variant.Padding * 2) * variant.Scale);
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.White);
            graphics.InterpolationMode = variant.Interpolation;
            // At integer nearest-neighbor scales, HighQuality maps each source
            // pixel to an exact square. PixelOffset.None shifts these samples.
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(pixels,
                new Rectangle(variant.Padding * variant.Scale, variant.Padding * variant.Scale, region.Width * variant.Scale, region.Height * variant.Scale),
                0, 0, pixels.Width, pixels.Height, GraphicsUnit.Pixel);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void TransformChannels(Bitmap image, Channels channels)
    {
        BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[checked(image.Width * 4)];
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), row, 0, row.Length);
                for (int x = 0; x < row.Length; x += 4)
                {
                    int blue = row[x], green = row[x + 1], red = row[x + 2];
                    byte gray = channels switch
                    {
                        Channels.Green => (byte)green,
                        Channels.Minimum => (byte)Math.Min(red, Math.Min(green, blue)),
                        Channels.Maximum => (byte)Math.Max(red, Math.Max(green, blue)),
                        Channels.ThresholdGreen => green < 160 ? (byte)0 : (byte)255,
                        _ => (byte)((54 * red + 183 * green + 19 * blue + 128) / 256)
                    };
                    row[x] = row[x + 1] = row[x + 2] = gray;
                }
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, checked(y * data.Stride)), row.Length);
            }
        }
        finally { image.UnlockBits(data); }
    }

    private static Rectangle[] FindInkRows(Bitmap source)
    {
        var bands = new List<Rectangle>();
        int start = -1, last = -1;
        for (int y = 0; y < source.Height; y++)
        {
            int count = 0;
            // Do not let a one-pixel capture border create a continuous run.
            for (int x = 1; x < source.Width - 1; x++)
            {
                Color pixel = source.GetPixel(x, y);
                if (pixel.A >= 128 && pixel.G < 160) count++;
            }
            bool ink = count >= Math.Max(3, source.Width / 250);
            if (ink) { if (start < 0) start = y; last = y; }
            else if (start >= 0 && y - last > 2) { AddBand(); start = last = -1; }
        }
        if (start >= 0) AddBand();
        if (bands.Count > 256) throw new InvalidOperationException("This diagnostic image contains too many raster row bands.");
        return bands.ToArray();

        void AddBand()
        {
            // A thin border/underline alone does not establish a text line.
            if (last - start + 1 is < 3 or > 128) return;
            int top = Math.Max(0, start - 2), bottom = Math.Min(source.Height, last + 3);
            bands.Add(Rectangle.FromLTRB(0, top, source.Width, bottom));
        }
    }

    private static async Task<NativeRead> ReadNativeBoundedAsync(OcrEngine engine, Bitmap prepared, Variant variant,
        Rectangle sourceRegion, CancellationToken wholeRun)
    {
        int width = prepared.Width, height = prepared.Height;
        byte[] bytes = new byte[checked(width * height * 4)];
        BitmapData data = prepared.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), bytes, y * width * 4, width * 4);
        }
        finally { prepared.UnlockBits(data); }
        var request = CancellationTokenSource.CreateLinkedTokenSource(wholeRun);
        request.CancelAfter(TimeSpan.FromSeconds(10));
        Task<NativeRead> work = Task.Run(async () =>
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bytes);
            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            IAsyncOperation<OcrResult> operation = engine.RecognizeAsync(bitmap);
            OcrResult result = await AwaitRecognition(operation, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            var lines = result.Lines.Select(line => new Line(line.Text ?? "", line.Words.Select(word => new Word(word.Text ?? "",
                sourceRegion.Left + word.BoundingRect.X / variant.Scale - variant.Padding,
                sourceRegion.Top + word.BoundingRect.Y / variant.Scale - variant.Padding,
                word.BoundingRect.Width / variant.Scale, word.BoundingRect.Height / variant.Scale)).ToArray())).ToArray();
            return new NativeRead(result.Text ?? "", engine.RecognizerLanguage.LanguageTag, result.TextAngle, lines);
        }, CancellationToken.None);
        _ = ObserveAsync(work, request);
        // A timeout terminates the entire probe run, never starts another
        // operation over native resources that may still be in use. The owned
        // worker disposes its SoftwareBitmap only after native completion.
        return await work.WaitAsync(TimeSpan.FromSeconds(12), wholeRun).ConfigureAwait(false);
    }

    private static async Task ObserveAsync(Task<NativeRead> work, CancellationTokenSource request)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { /* Retain/observe ownership if a native provider completes after the bounded wait. */ }
        finally { request.Dispose(); }
    }

    private static Task<OcrResult> AwaitRecognition(IAsyncOperation<OcrResult> operation, CancellationToken token)
    {
        var completion = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = token.Register(() => { try { operation.Cancel(); } catch (Exception) { } });
        operation.Completed = (finished, status) =>
        {
            try
            {
                if (status == AsyncStatus.Completed) completion.TrySetResult(finished.GetResults());
                else if (status == AsyncStatus.Canceled) completion.TrySetCanceled(token);
                else completion.TrySetException(finished.ErrorCode ?? new InvalidOperationException("Windows OCR failed."));
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally
            {
                _ = registration.Unregister();
                try { finished.Close(); } catch (Exception) { }
            }
        };
        return completion.Task;
    }
}
