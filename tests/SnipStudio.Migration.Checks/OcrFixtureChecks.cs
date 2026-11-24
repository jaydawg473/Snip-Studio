using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckOcrNoteEditRoutingAsync()
    {
        using EditorSession session = new();
        session.OpenImageBytes(CreateImage(440, 160));
        const string noteText = "Actual note 0008";
        int target = session.AddText(noteText, new(130, 36), 0xFF175AA0, 16, 150);
        int other = session.AddText("X", new(16, 36), 0xFFAA1735, 16, 310);
        TextAnnotationSnapshot note = session.GetAnnotation(target)!.Text!;
        ImageRectangle ink = note.InkBounds;
        var notePoint = new ImagePoint(ink.X + ink.Width / 2, ink.Y + ink.Height / 2);

        Check(session.HitTest(notePoint, 3) == other,
            "Move can still select the top note's frame above the other note's ink");
        Check(session.HitTestTextInk(notePoint)?.Index == target,
            "Text editing chooses the touched note's ink instead of the broad upper frame");
        Check(session.HitTestTextInk(new ImagePoint(300, notePoint.Y)) is null,
            "Blank space inside a broad frame does not claim OCR text for that note");

        byte[] pixels = (await session.RenderPreviewAsync()).PngBytes;
        long revision = session.Revision;
        OcrLineEditSnapshot? linked = await session.PrepareRecognizedLineEditAsync(Recognition(noteText), 0);
        Check(linked is not null && linked.ExistingAnnotationIndex == target && linked.Text == noteText &&
            linked.Rtf == note.Rtf && linked.Argb == note.Argb && linked.Mask.Width == 0,
            "A uniquely identified note opens with its exact text and formatting and no raster mask");

        OcrLineEditSnapshot? blocked = await session.PrepareRecognizedLineEditAsync(Recognition("OCR misread"), 0);
        Check(blocked is null, "A differently recognized string crossing an existing note cannot edit it or create a raster draft");
        Check(session.Revision == revision && session.AnnotationCount == 2 &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(pixels),
            "Blocked OCR routing leaves the document revision and rendered pixels intact");

        int duplicate = session.AddText(noteText, note.Location, note.Argb, note.FontSize, note.LayoutWidth, note.Rtf);
        Check(session.HitTestTextInk(notePoint)?.Index == duplicate,
            "A direct click chooses the topmost of overlapping notes with identical text");
        Check(await session.PrepareRecognizedLineEditAsync(Recognition(noteText), 0) is null,
            "An OCR line without a direct click does not guess between two matching notes");

        OcrSnapshot Recognition(string text)
        {
            var baseline = session.GetCaptureBaseline();
            var native = new global::SnipStudio.TextRecognitionResult { Available = true, Text = text };
            native.Lines.Add(new global::SnipStudio.TextLineRegion
            {
                Text = text,
                Bounds = new RectangleF((float)ink.X, (float)ink.Y, (float)ink.Width, (float)ink.Height)
            });
            return new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, native);
        }
    }

    private static async Task CheckReviewedTranslationWritebackAsync()
    {
        const string originalNote = "Invoice 0008";
        const string updatedNote = "Facture 0008";
        const string originalRaster = "ORIGINAL IMAGE 42";
        const string updatedRaster = "I";
        const uint noteColor = 0xFF184989;
        const TextFontStyle noteStyle = TextFontStyle.Bold | TextFontStyle.Italic;

        using EditorSession session = new();
        string rasterRtf = session.CreateTextRtf(originalRaster, "Segoe UI", 26, TextFontStyle.Regular, 0xFF000000);
        var rasterLocation = new PointF(20, 92);
        RectangleF rasterInk = global::SnipStudio.RichTextLayout.Measure(rasterRtf, 340).InkBounds;
        rasterInk.Offset(rasterLocation);
        using var bitmap = new Bitmap(420, 160);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            global::SnipStudio.RichTextLayout.Draw(graphics, rasterRtf, 340, rasterLocation);
        }
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        session.OpenImageBytes(png.ToArray());
        int noteIndex = session.AddText(originalNote, new(20, 20), noteColor, 18, 220,
            session.CreateTextRtf(originalNote, "Segoe UI", 18, noteStyle, noteColor), minimumHeight: 35);
        TextAnnotationSnapshot note = session.GetAnnotation(noteIndex)!.Text!;
        long before = session.Revision;
        string originalPixels = PixelHash((await session.RenderPreviewAsync()).PngBytes);

        OcrSnapshot recognized = Recognition(originalNote);
        OcrConversionResult unchanged = await session.ApplyReviewedTranslationAsync(recognized, [0], [originalNote]);
        Check(!unchanged.Applied && unchanged.ConvertedLineCount == 0 && session.Revision == before,
            "Unchanged review does not claim a writeback or create Undo history");
        OcrConversionResult noteOnly = await session.ApplyReviewedTranslationAsync(recognized, [0], [updatedNote]);
        Check(noteOnly.Applied && noteOnly.ConvertedLineCount == 1 && noteOnly.LastAnnotationIndex == noteIndex &&
            session.AnnotationCount == 1, "A reviewed existing note is updated in its original annotation slot");
        TextAnnotationSnapshot edited = session.GetAnnotation(noteIndex)!.Text!;
        Check(edited.Text == updatedNote && edited.Location == note.Location && edited.LayoutWidth == note.LayoutWidth &&
            edited.MinimumHeight == note.MinimumHeight && edited.Argb == note.Argb && edited.FontSize == note.FontSize &&
            edited.FontFamily == note.FontFamily && edited.FontStyle == note.FontStyle &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) != originalPixels,
            "Note writeback changes rendered text and preserves authored geometry, color and style");
        session.Undo();
        Check(session.Revision == before && PixelHash((await session.RenderPreviewAsync()).PngBytes) == originalPixels,
            "One Undo restores note-only writeback exactly");

        OcrConversionResult caseMisread = await session.ApplyReviewedTranslationAsync(Recognition("invoice 0008"), [0], [updatedNote]);
        Check(!caseMisread.Applied && caseMisread.Message.Contains("Line 1", StringComparison.Ordinal) &&
            session.GetAnnotation(noteIndex)!.Text!.Text == originalNote,
            "A case mismatch does not identify the note for reviewed translation");
        OcrConversionResult tooLong = await session.ApplyReviewedTranslationAsync(recognized, [0], [new string('X', 16001)]);
        Check(!tooLong.Applied && tooLong.Message.Contains("Line 1", StringComparison.Ordinal) && session.Revision == before,
            "An oversized reviewed row is rejected with its source line and no change");
        OcrConversionResult unfittable = await session.ApplyReviewedTranslationAsync(recognized, [0], [new string('W', 256)]);
        Check(!unfittable.Applied && unfittable.Message.Contains("does not fit", StringComparison.Ordinal) && session.Revision == before,
            "A reviewed row must fit the authored note without resizing or obscuring the image");
        OcrConversionResult protectedRaster = await session.ApplyReviewedTranslationAsync(Recognition(originalNote, protectRaster: true),
            [0, 1], [updatedNote, updatedRaster]);
        Check(!protectedRaster.Applied && protectedRaster.Message.Contains("Line 2", StringComparison.Ordinal) &&
            session.Revision == before && PixelHash((await session.RenderPreviewAsync()).PngBytes) == originalPixels,
            "A protected raster row rejects the complete batch, including the already planned note");

        OcrConversionResult mixed = await session.ApplyReviewedTranslationAsync(recognized, [0, 1], [updatedNote, updatedRaster]);
        Check(mixed.Applied && mixed.ConvertedLineCount == 2 && session.GetAnnotations().Any(item => item.Text?.Text == updatedNote) &&
            session.GetAnnotation(mixed.LastAnnotationIndex)?.Text?.Text == updatedRaster &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) != originalPixels,
            "One reviewed operation writes both the note and the raster line visibly");
        session.Undo();
        Check(session.Revision == before && session.AnnotationCount == 1 && session.GetAnnotation(noteIndex)!.Text!.Text == originalNote &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == originalPixels,
            "One Undo restores both source rows and the original captured pixels");

        OcrSnapshot Recognition(string noteText, bool protectRaster = false)
        {
            var baseline = session.GetCaptureBaseline();
            var native = new global::SnipStudio.TextRecognitionResult { Available = true, Text = noteText + "\n" + originalRaster };
            ImageRectangle ink = note.InkBounds;
            native.Lines.Add(new global::SnipStudio.TextLineRegion { Text = noteText,
                Bounds = new RectangleF((float)ink.X, (float)ink.Y, (float)ink.Width, (float)ink.Height) });
            native.Lines.Add(new global::SnipStudio.TextLineRegion { Text = originalRaster, Bounds = rasterInk });
            if (protectRaster) native.ProtectedGraphics.Add(rasterInk);
            return new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, native);
        }
    }

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
        bool iconRasterPassed = false;
        bool sourcePixelPlacementPassed = false;
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
            CheckDocumentIconRaster();
            iconRasterPassed = true;
            CheckFractionalOcrSourcePixels();
            sourcePixelPlacementPassed = true;
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
                IconRasterPassed = iconRasterPassed,
                SourcePixelPlacementPassed = sourcePixelPlacementPassed,
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

    private static void CheckDocumentIconRaster()
    {
        using var image = new Bitmap(200, 126, PixelFormat.Format32bppArgb);
        string[] iconPixels = [".########...", "#........#..", "#.........#.", "#..........#",
            "#....#.....#", "#...#.#....#", "#...###....#", "#...###....#", "#...#.####.#",
            "#.######.#.#", "#.#.#..###.#", "#..##......#", "#..........#", "#..........#", ".##########."];
        using (Graphics graphics = Graphics.FromImage(image))
        using (var font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            for (int y = 0; y < iconPixels.Length; y++)
                for (int x = 0; x < iconPixels[y].Length; x++)
                    if (iconPixels[y][x] == '#')
                    {
                        graphics.FillRectangle(Brushes.Red, 12 + x, 10 + y, 1, 1);
                        graphics.FillRectangle(Brushes.Red, 49 + x, 82 + y, 1, 1);
                        graphics.FillRectangle(Brushes.Red, 135 + x, 82 + y, 1, 1);
                    }
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.DrawString("00042_Aa.pdf", font, Brushes.Black, 29, 10);
            graphics.DrawString("@ A1", font, Brushes.Red, 11, 47);
            graphics.DrawString("0008", font, Brushes.Black, 12, 83);
            graphics.DrawString("@ A1", font, Brushes.Red, 69, 83);
        }

        static global::SnipStudio.TextLineRegion Line(string text, params (string Text, RectangleF Bounds)[] words)
        {
            var line = new global::SnipStudio.TextLineRegion { Text = text };
            foreach ((string value, RectangleF bounds) in words)
            {
                line.Words.Add(new global::SnipStudio.TextWordRegion { Text = value, Bounds = bounds });
                line.Bounds = line.Bounds.IsEmpty ? bounds : RectangleF.Union(line.Bounds, bounds);
            }
            return line;
        }

        // Seed native geometry so the tests exercise the exact boundary case
        // independent of Windows OCR language-pack and version differences.
        var refine = typeof(EditorSession).Assembly.GetType("SnipStudio.RecognizedTextRegions", throwOnError: true)!
            .GetMethod("Refine", BindingFlags.Static | BindingFlags.NonPublic)!;
        const string raw = "@ 00042_Aa.pdf\n@ A1";
        var merged = new global::SnipStudio.TextRecognitionResult { Available = true, Text = raw };
        merged.Lines.Add(Line("@ 00042_Aa.pdf", ("@", new RectangleF(12.5f, 11, 11, 13)),
            ("00042_Aa.pdf", new RectangleF(30, 13, 82, 13))));
        merged.Lines.Add(Line("@ A1", ("@", new RectangleF(12, 49, 12, 13)), ("A1", new RectangleF(26, 49, 15, 13))));
        refine.Invoke(null, [merged, image]);
        Check(merged.RawText == raw && merged.Lines.Select(line => line.Text).SequenceEqual(["00042_Aa.pdf", "@ A1"]),
            "An isolated folded document is excluded without altering genuine punctuation, identifiers or raw OCR");
        Check(merged.Lines[0].Words.Count == 1 && merged.Lines[0].ExcludedGraphicBounds == new RectangleF(12, 10, 12, 15),
            "The icon and its padded raster bounds are kept outside the selectable filename");

        var standalone = new global::SnipStudio.TextRecognitionResult { Available = true, Text = "@" };
        standalone.Lines.Add(Line("@", ("@", new RectangleF(12, 10, 12, 15))));
        refine.Invoke(null, [standalone, image]);
        Check(standalone.Lines.Count == 0 && standalone.Text.Length == 0 && standalone.RawText == "@" &&
            image.GetPixel(12, 13).ToArgb() == Color.Red.ToArgb(), "A standalone icon cannot be converted and its source pixels are untouched");

        const string toolbarRaw = "0008 @ @ A1 Z]";
        var toolbar = new global::SnipStudio.TextRecognitionResult { Available = true, Text = toolbarRaw };
        toolbar.Lines.Add(Line(toolbarRaw, ("0008", new RectangleF(12, 85, 29, 13)),
            ("@", new RectangleF(49, 83, 12, 13)), ("@", new RectangleF(69, 86, 13, 12)),
            ("A1", new RectangleF(85, 86, 14, 12)), ("Z]", new RectangleF(135, 83, 12, 13))));
        toolbar.Lines[0].RecognitionClipBounds = new RectangleF(8, 79, 145, 22);
        refine.Invoke(null, [toolbar, image]);
        Check(toolbar.RawText == toolbarRaw && toolbar.Lines.Select(line => line.Text).SequenceEqual(["0008", "@ A1"]),
            "Proven middle and trailing icons are excluded while literal red punctuation and leading zeros are kept");
        Check(toolbar.ProtectedGraphics.Count == 2 && toolbar.Lines.All(line =>
                line.RecognitionClipBounds == new RectangleF(8, 79, 145, 22) &&
                toolbar.ProtectedGraphics.All(icon => !icon.IntersectsWith(line.Bounds))),
            "OCR fragments retain source clipping and avoid spanning either protected icon with an edit mask");
        refine.Invoke(null, [toolbar, image]);
        Check(toolbar.RawText == toolbarRaw && toolbar.Lines.Select(line => line.Text).SequenceEqual(["0008", "@ A1"]) &&
            toolbar.ProtectedGraphics.Count == 2, "Repeated refinement preserves the verified labels and protection");
        CheckJoinedGraphicPrefixes();
    }

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
        OcrLineEditSnapshot draft = await session.PrepareRecognizedLineEditAsync(recognized, line.Index, matchingBudget.Token)
            ?? throw new InvalidOperationException("The original-image fixture must have an editable source line.");
        Check(draft.ExistingAnnotationIndex < 0 && draft.Text == line.Text && draft.Mask.Width > 0,
            "Preparing a raster text draft keeps the original recognized string and its source mask");
        Check(IsSourcePixel(draft.Location), "The in-place OCR draft starts on the source-pixel grid");
        Check(session.Revision == revision && !session.CanUndo && session.AnnotationCount == 0 &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original),
            "Opening a recognized draft leaves pixels, revision and Undo unchanged");
        Check(session.ApplyRecognizedLineEdit(draft, draft.Text, draft.Rtf, draft.Argb, draft.FontSize) < 0 &&
            session.Revision == revision && !session.CanUndo,
            "Applying an unchanged recognized draft does not commit reconstructed text");

        const string replacement = "I";
        const uint color = 0xFF243982;
        const uint fill = 0xFFFFF1D6;
        const float fontSize = 8;
        string rtf = session.CreateTextRtf(replacement, draft.FontFamily, fontSize, TextFontStyle.Regular, color);
        float width = draft.LayoutWidth - 2;
        Expect<ArgumentOutOfRangeException>(() => session.ApplyRecognizedLineEdit(draft, replacement, rtf,
            color, fontSize, draft.LayoutWidth + 2, fill), "Custom source width cannot exceed its protected layout");
        Expect<ArgumentException>(() => session.ApplyRecognizedLineEdit(draft, replacement, rtf,
            color, fontSize, width, 0x80FFF1D6), "Custom source fill must be opaque");
        Check(session.Revision == revision && !session.CanUndo && session.AnnotationCount == 0,
            "A rejected width or fill leaves the source text and history unchanged");
        int edited = session.ApplyRecognizedLineEdit(draft, replacement, rtf, color, fontSize, width, fill);
        var note = session.GetAnnotation(edited)?.Text;
        Check(note is { Text: replacement, Argb: color, FontSize: fontSize } && session.CanUndo,
            "Applying a changed recognized draft keeps its exact text, color and size");
        byte[] changed = (await session.RenderPreviewAsync()).PngBytes;
        Check(note?.LayoutWidth == width && Pixel(changed, (int)(draft.Mask.X + draft.Mask.Width - 1),
            (int)(draft.Mask.Y + draft.Mask.Height - 1)) == fill && Pixel(changed, 0, 0) == Pixel(original, 0, 0),
            "Advanced text keeps its custom width and fills the original source mask while unrelated pixels stay unchanged");
        Check(note != null && IsSourcePixel(note.Location), "Applying recognized text keeps whole-pixel placement in the exported image");
        Expect<InvalidOperationException>(() => session.ApplyRecognizedLineEdit(draft, replacement, rtf, color, fontSize),
            "A committed source draft cannot modify a newer revision again");
        session.Undo();
        Check(session.Revision == revision && session.AnnotationCount == 0 && !session.CanUndo &&
            PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original),
            "One Undo restores the original image and all pre-draft history");
        return new { Passed = true, OriginalLine = line.Text, EditedText = replacement, UndoRestoredOriginalPixels = true,
            AlignedToSourcePixels = true, CustomWidthAndFill = true };
    }

    private static bool IsSourcePixel(ImagePoint point) => point.X == Math.Truncate(point.X) && point.Y == Math.Truncate(point.Y);

    private static void CheckFractionalOcrSourcePixels()
    {
        Type backend = typeof(EditorSession);
        var enclose = backend.GetMethod("EncloseSourcePixels", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sample = (Rectangle)enclose.Invoke(null, [new RectangleF(10.25f, 11.75f, 8.5f, 5.5f)])!;
        Check(sample == Rectangle.FromLTRB(10, 11, 19, 18), "Style samples include both partially covered source edges");

        var protect = backend.GetMethod("ProtectOcrPadding", BindingFlags.Static | BindingFlags.NonPublic)!;
        var protectedMask = (Rectangle)protect.Invoke(null,
            [new Rectangle(9, 9, 2, 4), new RectangleF(9, 10, 1, 1), new RectangleF(10.25f, 10, 1, 1)])!;
        Check(protectedMask == new Rectangle(9, 9, 1, 4),
            "A fractional icon edge protects the entire touched pixel from the source mask");

        var align = backend.GetMethod("TryAlignOcrInk", BindingFlags.Static | BindingFlags.NonPublic)!;
        RectangleF ink = new(2, 3, 9, 6);
        object[] open = [new PointF(10.25f, 11.75f), new RectangleF(2, 3, 30, 22), ink, PointF.Empty];
        Check(align.Invoke(null, open) is true && (PointF)open[3] == new PointF(8, 9),
            "Fractional OCR ink snaps to the nearest original pixel without shifting the glyph bitmap");
        object[] tight = [new PointF(10.5f, 11.5f), new RectangleF(2, 3, 17, 14), ink, PointF.Empty];
        Check(align.Invoke(null, tight) is true && (PointF)tight[3] == new PointF(8, 8),
            "When a protected right or bottom edge blocks a rounding tie, use the other nearest pixel");
        tight[0] = new PointF(10.625f, 11.625f);
        Check(align.Invoke(null, tight) is false, "Do not relocate ink by more than a half-pixel to evade protection");
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
        bool exactOrder = false, selectionIndicesMatch = false, undoPixelsMatch = false, redoPixelsMatch = false, convertedTextMatches = false, convertedTextAligned = false;
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
            var notes = session.GetAnnotations().Where(item => item.Text is not null).Select(item => item.Text!).ToArray();
            annotationText = notes.Select(note => note.Text).ToArray();
            convertedTextAligned = notes.Length > 0 && notes.All(note => IsSourcePixel(note.Location));
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
            passed = exactOrder && selectionIndicesMatch && convertedTextMatches && convertedTextAligned && undoPixelsMatch && redoPixelsMatch;
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
            ConvertedTextAlignedToSourcePixels = convertedTextAligned,
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
