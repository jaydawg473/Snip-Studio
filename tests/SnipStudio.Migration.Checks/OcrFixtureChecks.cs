using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private sealed class OcrFixtureManifest
    {
        public OcrFixtureManifest() { }
        public List<OcrFixtureInput> Fixtures { get; set; } = new();
    }

    private sealed class OcrFixtureInput
    {
        public OcrFixtureInput() { }
        public string Name { get; set; } = "";
        public string InputPath { get; set; } = "";
        public List<string> ExpectedLines { get; set; } = new();
    }

    private static int RunOcrFixtures(string manifestPath, string evidenceDirectory)
    {
        string output = Path.GetFullPath(evidenceDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use a new empty OCR evidence directory.");
            return 2;
        }
        Directory.CreateDirectory(output);
        var fixtures = new List<object>();
        object? lineEdit = null;
        bool passed = false;
        string? failure = null;
        Stopwatch clock = Stopwatch.StartNew();
        using StaContext context = new();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            if (!Path.IsPathFullyQualified(manifestPath)) throw new ArgumentException("Use an absolute private fixture-manifest path.");
            if (new FileInfo(manifestPath).Length is < 1 or > 1024 * 1024) throw new ArgumentException("The private fixture manifest is empty or exceeds 1 MB.");
            byte[] json = File.ReadAllBytes(manifestPath);
            var manifest = JsonSerializer.Deserialize<OcrFixtureManifest>(json) ?? throw new ArgumentException("The fixture manifest is empty.");
            if (manifest.Fixtures.Count is < 1 or > 16) throw new ArgumentException("Supply between 1 and 16 OCR fixtures.");
            foreach (OcrFixtureInput item in manifest.Fixtures)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 || !Path.IsPathFullyQualified(item.InputPath))
                    throw new ArgumentException("Every fixture needs a short name and an absolute input path.");
                if (item.ExpectedLines is null || item.ExpectedLines.Count is < 1 or > 4096 || item.ExpectedLines.Any(value => value is null))
                    throw new ArgumentException("Supply exact expected lines, including legitimate duplicates.");
                if (new FileInfo(item.InputPath).Length is < 1 or > 128L * 1024 * 1024)
                    throw new ArgumentException("A fixture is empty or exceeds 128 MB.");
            }
            lineEdit = context.RunWithResult(CheckRecognizedLineDraftAsync());
            bool allPassed = true;
            for (int index = 0; index < manifest.Fixtures.Count; index++)
            {
                // Execute sequentially. A native timeout stops the run rather
                // than starting another OCR provider while the first is late.
                OcrFixtureInput fixture = manifest.Fixtures[index];
                OcrFixtureOutcome outcome = context.RunWithResult(CheckOcrFixtureAsync(fixture, output, index));
                fixtures.Add(outcome.Evidence);
                allPassed &= outcome.Passed;
                if (outcome.StopAfterNativeTimeout)
                {
                    failure = "Stopped after a native OCR/source-style timeout; no subsequent fixture was started. A late native operation may still be finishing inside this process.";
                    break;
                }
            }
            passed = allPassed && fixtures.Count == manifest.Fixtures.Count;
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            clock.Stop();
            var result = new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                Passed = passed,
                FixtureCount = fixtures.Count,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Scope = "Real installed Windows OCR on one generated source-line draft and explicitly supplied local image fixtures. Checks cover draft/apply/Undo, exact ordinal lines, public OCR selection, source-style conversion/render and Undo/Redo. No desktop capture, WinUI Text-tool activation or native pointer/clipboard acceptance. Extraction mismatches remain unchanged and are recorded, never guessed or corrected.",
                RecognizedLineDraft = lineEdit,
                Fixtures = fixtures,
                Failure = failure
            };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "ocr-fixture-checks.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private sealed record OcrFixtureOutcome(bool Passed, bool StopAfterNativeTimeout, object Evidence);

    private static async Task<object> CheckRecognizedLineDraftAsync()
    {
        using var image = new Bitmap(500, 100);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var font = new Font("Segoe UI", 28, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.DrawString("EDITABLE LINE 42", font, Brushes.Black, 16, 20);
        }
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        using EditorSession session = new();
        session.OpenImageBytes(png.ToArray());
        long revision = session.Revision;
        byte[] original = (await session.RenderPreviewAsync()).PngBytes;
        using var recognitionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        OcrSnapshot recognized = await session.RecognizeTextAsync(recognitionBudget.Token);
        OcrLineSnapshot line = recognized.Lines.Single(item => item.Words.Count > 0);
        using var matchingBudget = new CancellationTokenSource(TimeSpan.FromSeconds(95));
        OcrLineEditSnapshot draft = await session.PrepareRecognizedLineEditAsync(recognized, line.Index, matchingBudget.Token);
        Check(draft.ExistingAnnotationIndex < 0 && draft.Text == line.Text && draft.Mask.Width > 0,
            "Preparing a raster text draft keeps the original recognized string and its source mask");
        Check(session.Revision == revision && !session.CanUndo && session.AnnotationCount == 0 &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original),
            "Opening a recognized draft leaves pixels, revision and Undo unchanged");
        Check(session.ApplyRecognizedLineEdit(draft, draft.Text, draft.Rtf, draft.Argb, draft.FontSize) < 0 &&
            session.Revision == revision && !session.CanUndo,
            "Applying an unchanged recognized draft does not commit reconstructed text");

        const string replacement = "I";
        const uint color = 0xFF243982;
        const float fontSize = 8;
        string rtf = session.CreateTextRtf(replacement, draft.FontFamily, fontSize, TextFontStyle.Regular, color);
        int edited = session.ApplyRecognizedLineEdit(draft, replacement, rtf, color, fontSize);
        var note = session.GetAnnotation(edited)?.Text;
        Check(note is { Text: replacement, Argb: color, FontSize: fontSize } && session.CanUndo,
            "Applying a changed recognized draft keeps its exact text, color and size");
        Expect<InvalidOperationException>(() => session.ApplyRecognizedLineEdit(draft, replacement, rtf, color, fontSize),
            "A committed source draft cannot modify a newer revision again");
        session.Undo();
        Check(session.Revision == revision && session.AnnotationCount == 0 && !session.CanUndo &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original),
            "One Undo restores the original image and all pre-draft history");
        return new { Passed = true, OriginalLine = line.Text, EditedText = replacement, UndoRestoredOriginalPixels = true };
    }

    private static async Task<OcrFixtureOutcome> CheckOcrFixtureAsync(OcrFixtureInput fixture, string output, int index)
    {
        string safeName = new string(fixture.Name.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        string prefix = $"{index:00}-{safeName}";
        string? failure = null;
        bool passed = false, stop = false;
        OcrSnapshot? recognized = null;
        OcrSelectionSnapshot? selection = null;
        OcrConversionResult? conversion = null;
        string[] actualLines = Array.Empty<string>(), annotationText = Array.Empty<string>();
        string[] missing = Array.Empty<string>(), unexpected = Array.Empty<string>();
        bool exactOrder = false, selectionIndicesMatch = false, undoPixelsMatch = false, redoPixelsMatch = false, convertedTextMatches = false;
        double recognitionMilliseconds = 0, conversionMilliseconds = 0, renderMilliseconds = 0;
        int width = 0, height = 0;
        long originalRevision = 0;
        byte[] input = File.ReadAllBytes(fixture.InputPath);
        List<object> memory = new() { SampleMemory("Before OCR fixture") };
        using EditorSession session = new();
        try
        {
            session.OpenImageBytes(input);
            width = session.ImageWidth; height = session.ImageHeight;
            originalRevision = session.Revision;
            byte[] original = (await session.RenderPreviewAsync()).PngBytes;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            Stopwatch step = Stopwatch.StartNew();
            try { recognized = await session.RecognizeTextAsync(cancellation.Token); }
            finally { step.Stop(); recognitionMilliseconds = step.Elapsed.TotalMilliseconds; }
            actualLines = recognized.Lines.Select(line => line.Text).ToArray();
            exactOrder = fixture.ExpectedLines.SequenceEqual(actualLines, StringComparer.Ordinal);
            missing = MissingRows(fixture.ExpectedLines, actualLines);
            unexpected = MissingRows(actualLines, fixture.ExpectedLines);
            File.WriteAllText(Path.Combine(output, prefix + "-raw.txt"), recognized.RawText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(output, prefix + "-recovery-raw.txt"), recognized.RecoveryRawText, new UTF8Encoding(false));
            memory.Add(SampleMemory("After Windows OCR"));
            if (!recognized.Available) throw new InvalidOperationException("Windows OCR unavailable: " + recognized.Message);

            OcrLineSnapshot[] selectable = recognized.Lines.Where(line => line.Words.Any(word => SelectableWord(word, width, height))).ToArray();
            if (selectable.Length > 0)
            {
                OcrWordSnapshot first = selectable[0].Words.First(word => SelectableWord(word, width, height));
                OcrWordSnapshot last = selectable[^1].Words.Last(word => SelectableWord(word, width, height));
                selection = session.SelectRecognizedText(recognized,
                    new(first.Bounds.X + first.Bounds.Width / 2, first.Bounds.Y + first.Bounds.Height / 2),
                    new(last.Bounds.X + last.Bounds.Width / 2, last.Bounds.Y + last.Bounds.Height / 2));
                selectionIndicesMatch = selection.LineIndices.SequenceEqual(selectable.Select(line => line.Index));
            }

            using var conversionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            step.Restart();
            try { conversion = await session.ConvertRecognizedTextAsync(recognized, cancellationToken: conversionCancellation.Token); }
            finally { step.Stop(); conversionMilliseconds = step.Elapsed.TotalMilliseconds; }
            if (!conversion.Applied) throw new InvalidOperationException("Conversion was not applied: " + conversion.Message);
            annotationText = session.GetAnnotations().Where(item => item.Text is not null).Select(item => item.Text!.Text).ToArray();
            convertedTextMatches = annotationText.SequenceEqual(actualLines.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
            step.Restart();
            byte[] converted = (await session.RenderPreviewAsync()).PngBytes;
            step.Stop(); renderMilliseconds = step.Elapsed.TotalMilliseconds;
            File.WriteAllBytes(Path.Combine(output, prefix + "-converted.png"), converted);
            memory.Add(SampleMemory("After source-style conversion and render"));
            session.Undo();
            undoPixelsMatch = session.Revision == originalRevision && session.AnnotationCount == 0 &&
                PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original);
            session.Redo();
            redoPixelsMatch = PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(converted);
            passed = exactOrder && selectionIndicesMatch && convertedTextMatches && undoPixelsMatch && redoPixelsMatch;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            failure = exception.ToString();
            stop = exception is TimeoutException or OperationCanceledException;
        }
        memory.Add(SampleMemory("After OCR fixture operations; no forced collection"));
        return new OcrFixtureOutcome(passed, stop, new
        {
            fixture.Name,
            InputFileName = Path.GetFileName(fixture.InputPath),
            InputSHA256 = Convert.ToHexString(SHA256.HashData(input)),
            InputBytes = input.Length,
            Width = width, Height = height,
            OriginalRevision = originalRevision,
            ExpectedLines = fixture.ExpectedLines,
            ActualLines = actualLines,
            MissingExpectedRows = missing,
            UnexpectedActualRows = unexpected,
            ExactOrdinalLineOrder = exactOrder,
            Available = recognized?.Available,
            Language = recognized?.Language,
            Message = recognized?.Message,
            RawText = recognized?.RawText,
            RecoveryRawText = recognized?.RecoveryRawText,
            TextAngle = recognized?.TextAngle,
            WasResized = recognized?.WasResized,
            RecoveryPasses = recognized?.RecoveryPasses,
            SelectionText = selection?.Text,
            SelectionLineIndices = selection?.LineIndices,
            SelectionOriginalIndicesMatch = selectionIndicesMatch,
            Conversion = conversion,
            ConvertedAnnotationText = annotationText,
            ConvertedTextExactlyPreservedInOrder = convertedTextMatches,
            UndoRestoredOriginalPixelsAndRevision = undoPixelsMatch,
            RedoRestoredConvertedPixels = redoPixelsMatch,
            RecognitionMilliseconds = recognitionMilliseconds,
            SourceStyleConversionMilliseconds = conversionMilliseconds,
            ConvertedRenderMilliseconds = renderMilliseconds,
            ProcessMemorySamples = memory,
            Passed = passed,
            StopAfterNativeTimeout = stop,
            Failure = failure
        });
    }

    private static bool SelectableWord(OcrWordSnapshot word, int width, int height)
    {
        OcrBounds bounds = word.Bounds;
        return !string.IsNullOrWhiteSpace(word.Text) && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0 &&
            bounds.X >= 0 && bounds.Y >= 0 && bounds.X + bounds.Width <= width && bounds.Y + bounds.Height <= height;
    }

    private static string[] MissingRows(IReadOnlyList<string> requested, IReadOnlyList<string> available)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string row in available) counts[row] = counts.GetValueOrDefault(row) + 1;
        var missing = new List<string>();
        foreach (string row in requested)
        {
            if (counts.GetValueOrDefault(row) > 0) counts[row]--;
            else missing.Add(row);
        }
        return missing.ToArray();
    }
}
