using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static int assertions;
    private static readonly List<string> completedGroups = new();
    private static readonly List<object> performance = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--replace-baseline" && args[1] == "--output") return RunReplaceBaseline(args[2]);
        if (args.Length == 3 && args[0] == "--settings-recovery" && args[1] == "--output") return RunSettingsRecoveryChecks(args[2]);
        if (args.Length == 4 && args[0] == "--translation-smoke" && args[2] == "--output") return RunTranslationSmoke(args[1], args[3]);
        if (args.Length == 4 && args[0] == "--video-smoke" && args[2] == "--output") return RunVideoSmoke(args[1], args[3]);
        if (args.Length == 4 && args[0] == "--ocr-fixtures" && args[2] == "--output") return RunOcrFixtures(args[1], args[3]);
        if (args.Length == 2 && args[0] == "--settings-worker") return RunSettingsWorker(args[1]);
        if (args.Length == 2 && args[0] == "--verify-settings-workers") return VerifySettingsWorkers(args[1]);
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.Error.WriteLine("Usage: SnipStudio.Migration.Checks --output <empty evidence directory>");
            return 2;
        }
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use an empty evidence directory; these checks never replace existing evidence.");
            return 2;
        }
        Directory.CreateDirectory(output);
        using StaContext context = new();
        SynchronizationContext.SetSynchronizationContext(context);
        Stopwatch clock = Stopwatch.StartNew();
        string? failure = null;
        bool passed = false;
        try
        {
            context.Run(CheckWorkflowsAsync(output));
            passed = true;
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
            clock.Stop();
            var evidence = new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                Passed = passed,
                Assertions = assertions,
                CompletedGroups = completedGroups,
                Performance = performance,
                ElapsedMilliseconds = clock.ElapsedMilliseconds,
                Scope = "Public editing backend on an STA context, exact values, rendered pixels/history, a hidden-control message loop, OCR ownership and cancellation, capture validation, synthetic scrolling partial-result and taskbar restoration checks, and atomic video copy with opaque fixtures. No native WinUI interaction, live OCR accuracy, live capture/scrolling, recording/audio/playback, installation, or physical monitor acceptance.",
                Failure = failure
            };
            string json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "migration-checks.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private static async Task CheckWorkflowsAsync(string output)
    {
        CheckSavedCaptureBaseline();
        completedGroups.Add("Autosave marks only the exact captured revision and rejects stale image identity");
        await CheckShapeSelectionAndMovementAsync();
        completedGroups.Add("Shape pixels, selection, movement and history");
        await CheckAnnotationDragPreviewAsync();
        completedGroups.Add("Detached object drag layers preserve stacking, transparency and history");
        CheckObjectGestureGeometry();
        completedGroups.Add("Object drag threshold, rotation, anchored resize, arrow preview, and visible placement point");
        CheckCropFrameGeometry();
        await CheckCropAndSourceRemovalAsync(output);
        await CheckBoundImageOpenAndFinalPublicationAsync(output);
        completedGroups.Add("Crop, source-area removal, atomic export and reopening");
        await CheckTextFidelityAsync();
        completedGroups.Add("Exact text values, source style values, edits and history");
        await CheckIndependentMessageLoopLifecycleAsync();
        completedGroups.Add("Rich-text editing survives another STA's bounded message-loop startup and exit");
        await CheckEmojiAndEraseAsync();
        CheckEmojiClickPlacement();
        completedGroups.Add("Emoji artwork, topmost erasing, delete and erase-all history");
        await CheckSelectionOwnershipAndContinuousEraseAsync();
        completedGroups.Add("Stale selection ownership, resize and continuous eraser transaction");
        await CheckClippedAndLinePropertiesAsync();
        completedGroups.Add("Horizontal/vertical line and partially cropped object properties preserve geometry");
        await CheckEndpointPropertiesAsync();
        completedGroups.Add("Line and arrow endpoints plus styling commit as one undoable edit");
        CheckOcrDtoOwnership();
        CheckOcrVerticalReadingOrder();
        completedGroups.Add("Public OCR line and word ownership and exact strings");
        CheckNativeOcrSelectionIndex();
        completedGroups.Add("OCR selection preserves original row indices and duplicate-word character offsets");
        CheckOcrBlankStartSelection();
        completedGroups.Add("Blank-start OCR drags select an area without pulling in neighboring text");
        CheckRecognizedTextSpotlightLayout();
        completedGroups.Add("Recognized-text reveal bands preserve original ink and dense Explorer spacing");
        await CheckTaskbarDiscardProtocolAsync();
        completedGroups.Add("Taskbar discard protocol requires confirmed consent and rechecks each target");
        CheckRecognizedTable();
        completedGroups.Add("OCR table geometry, exact cells, and safe clipboard formats");
        CheckOfflineOcrRecovery();
        CheckOcrFilenameSpacing();
        await CheckColoredCaretRecoveryAsync();
        CheckOcrExtensionFootprint();
        CheckTerminalOcrRecovery();
        CheckTerminalOcrPathReview();
        CheckTerminalOcrSwitchReview();
        CheckTerminalOcrPromptRecovery();
        CheckTerminalOcrSideLabels();
        CheckSparseOcrColumnRecovery();
        CheckCorroboratedInteriorOcrRecovery();
        CheckGraphicAdjacentOcrRecovery();
        await CheckCorroboratedOcrTimestampRecoveryAsync();
        await CheckSplitOcrColonRecoveryAsync();
        CheckClippedEdgeOcrRecovery();
        CheckInterruptedLineOcrRecovery();
        await CheckFragmentedOcrRowsAsync();
        CheckOcrRichTextStability();
        CheckAngledOcrSelection();
        await CheckAngledOcrEraseAsync();
        CheckOfflinePrivacyBoundaries();
        completedGroups.Add("Independent OCR row, timestamp, terminal and clipped-edge agreement, reading order and protected geometry");
        CheckDocumentIconRaster();
        completedGroups.Add("OCR excludes raster-proven icons and preserves original text and source clipping");
        await CheckOcrNoteEditRoutingAsync();
        completedGroups.Add("Editable note ink takes priority over another note's frame and ambiguous OCR overlaps preserve the image");
        await CheckReviewedTranslationWritebackAsync();
        completedGroups.Add("Reviewed translation updates eligible notes and raster text together with one Undo");
        await CheckRecognizedTextReviewAsync();
        completedGroups.Add("Batch OCR editing and selected-word translation preserve surrounding pixels and undo together");
        await CheckInkOpacityAsync(output);
        completedGroups.Add("Shapes and freehand or snapped highlights preserve chosen opacity");
        await CheckNativeOcrCancellationAsync();
        completedGroups.Add("Windows OCR cancellation and completion return independently before cleanup");
        await CheckCancellationAndInvalidEditsAsync();
        completedGroups.Add("Pre-canceled OCR and invalid edits preserve current work");
        await CheckCaptureRequestCancellationAsync();
        completedGroups.Add("Capture request rejection and pre-canceled calls without desktop input");
        await CheckCaptureFailureRecoveryAsync();
        CheckRecordingAdjustmentRepaint();
        completedGroups.Add("Recording area moves and resizes repaint the same desktop pixels as a clean render");
        await CheckVideoCopyAsync(output);
        completedGroups.Add("Video-copy file transaction, exact bytes, cancellation and failure preservation (non-video fixture)");
        await CheckPreferencesAsync(output);
        CheckLegacySettingsImport(output);
        completedGroups.Add("Settings stale per-field merges, parallel recent writes, recovery and unknown JSON preservation");
        CheckDesktopShellContracts();
        completedGroups.Add("Taskbar capture arguments and exact repository installer links");
        PrepareSettingsWorkerFixture(output);
        await MeasureLargeImageWorkAsync(output);
        completedGroups.Add("Controlled large-image operation timings and sampled process memory");
    }

    private static void CheckSavedCaptureBaseline()
    {
        using EditorSession session = new();
        byte[] source = CreateImage(160, 100);
        session.OpenImageBytes(source);
        var baseline = session.GetCaptureBaseline();
        Check(session.IsDirty, "A new capture starts without a saved baseline");
        Check(session.TryRecordSavedCapture(baseline) && !session.IsDirty && session.Revision == baseline.Revision,
            "Completing the save of the exact original marks it clean without changing history");

        session.OpenImageBytes(source);
        baseline = session.GetCaptureBaseline();
        Check(session.AddShape(AnnotationShape.Rectangle, new(20, 20), new(100, 70), 0xFFFF0000, 2),
            "An annotation can be added while the original is saving");
        long editedRevision = session.Revision;
        Check(session.TryRecordSavedCapture(baseline) && session.IsDirty && session.Revision == editedRevision,
            "The original still belongs to the edited capture and its newer changes stay dirty");

        session.OpenImageBytes(source);
        Check(!session.TryRecordSavedCapture(baseline) && session.IsDirty,
            "Identical replacement bytes are a distinct document and cannot inherit a stale autosave");
    }

    private static async Task CheckShapeSelectionAndMovementAsync()
    {
        byte[] source = CreateImage(360, 240);
        using EditorSession session = new();
        session.OpenImageBytes(source);
        Check(session.AnnotationCount == 0 && session.HitTest(new(50, 50)) == -1, "Source pixels are not editable annotations");
        long initialRevision = session.Revision;
        Check(session.AddShape(AnnotationShape.Rectangle, new(120, 90), new(24, 20), 0xFFFF0000, 4, 0xFF123456), "Reverse rectangle drag commits");
        Check(session.AnnotationCount == 1 && session.Revision > initialRevision, "One shape creates one annotation and revision");
        var shape = session.GetAnnotation(0) ?? throw new InvalidOperationException("Missing rectangle snapshot");
        Check(shape.Shape?.Shape == AnnotationShape.Rectangle && shape.Text is null && shape.Emoji is null, "Detached annotation identifies its concrete shape");
        Check(shape.Shape!.FillArgb == 0xFF123456 && shape.Shape.OutlineArgb == 0xFFFF0000 && shape.Shape.OutlineWidth == 4, "Shape stores exact chosen colors and width");
        Check(shape.Bounds.X <= 24 && shape.Bounds.Y <= 20 && shape.Bounds.Width >= 96 && shape.Bounds.Height >= 70, "Reverse drag keeps source geometry");
        Check(session.HitTest(new(60, 50), 1) == 0, "Filled interior is selectable");
        byte[] rectangle = (await session.RenderPreviewAsync()).PngBytes;
        Check(Pixel(rectangle, 60, 50) == 0xFF123456, "Filled shape renders the requested source color");
        Check(Pixel(rectangle, 10, 10) == Pixel(source, 10, 10), "Shape preserves source pixels outside its bounds");
        Check(session.MoveAnnotation(0, new(130, 0)), "Moving a selected annotation commits");
        var moved = session.GetAnnotation(0)!;
        Check(moved.Bounds.X == shape.Bounds.X + 130 && moved.Bounds.Y == shape.Bounds.Y, "Move keeps exact source-pixel translation");
        Check(shape.Bounds.X != moved.Bounds.X && shape.Revision < moved.Revision, "Prior snapshot remains detached from a move");
        byte[] movedPixels = (await session.RenderPreviewAsync()).PngBytes;
        Check(Pixel(movedPixels, 60, 50) == Pixel(source, 60, 50), "Moving reveals original pixels at the old location");
        Check(Pixel(movedPixels, 190, 50) == 0xFF123456, "Moved object renders at the destination");
        session.Undo();
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(rectangle), "Undo move restores exact previous pixels");
        session.Redo();
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(movedPixels), "Redo move restores exact destination pixels");
        long beforeNoOp = session.Revision;
        Check(!session.MoveAnnotation(0, new(0, 0)) && session.Revision == beforeNoOp, "Zero-distance move creates no history");

        foreach (AnnotationShape kind in new[] { AnnotationShape.Line, AnnotationShape.Arrow, AnnotationShape.Ellipse })
        {
            using EditorSession drawing = new();
            drawing.OpenImageBytes(source);
            Check(drawing.AddShape(kind, new(30, 30), new(220, 160), 0xFF008000, 4, kind == AnnotationShape.Ellipse ? 0xFFFF8000 : null), "Shape commits: " + kind);
            Check(drawing.GetAnnotation(0)?.Shape?.Shape == kind, "Shape snapshot preserves kind: " + kind);
            byte[] rendered = (await drawing.RenderPreviewAsync()).PngBytes;
            Check(PixelHash(rendered) != PixelHash(source), "Shape changes rendered pixels: " + kind);
            if (kind == AnnotationShape.Ellipse) Check(Pixel(rendered, 125, 95) == 0xFFFF8000, "Ellipse center uses the requested fill color");
            drawing.Undo();
            Check(PixelHash((await drawing.RenderPreviewAsync()).PngBytes) == PixelHash(source), "Undo shape restores every source pixel: " + kind);
            drawing.Redo();
            Check(PixelHash((await drawing.RenderPreviewAsync()).PngBytes) == PixelHash(rendered), "Redo shape reproduces every rendered pixel: " + kind);
        }
    }

    private static async Task CheckCropAndSourceRemovalAsync(string output)
    {
        byte[] source = CreateImage(360, 240);
        using EditorSession session = new();
        session.OpenImageBytes(source);
        session.AddShape(AnnotationShape.Rectangle, new(90, 80), new(160, 140), 0xFF000000, 2, 0xFF804020);
        var before = session.GetAnnotation(0)!;
        byte[] original = (await session.RenderPreviewAsync()).PngBytes;
        Check(session.Crop(new(290, 200), new(40, 30)), "Reverse crop drag commits");
        Check(session.ImageWidth == 250 && session.ImageHeight == 170, "Crop keeps the requested source dimensions");
        var after = session.GetAnnotation(0)!;
        Check(after.Bounds.X == before.Bounds.X - 40 && after.Bounds.Y == before.Bounds.Y - 30, "Crop translates annotation geometry with the image");
        byte[] cropped = (await session.RenderPreviewAsync()).PngBytes;
        Check(PixelHash(cropped) == CropPixelHash(original, new Rectangle(40, 30, 250, 170)), "Crop preserves every retained rendered pixel");
        session.Undo();
        Check(session.ImageWidth == 360 && session.ImageHeight == 240 && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(original), "Undo crop restores original size and pixels");
        session.Redo();
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(cropped), "Redo crop restores retained pixels");
        long revision = session.Revision;
        Check(!session.Crop(new(0, 0), new(250, 170)) && session.Revision == revision, "Cropping to the entire image adds no undo step");
        Expect<ArgumentException>(() => session.Crop(new(10, 10), new(11, 11)), "Sub-two-pixel crop is rejected");
        Check(session.Revision == revision && session.ImageWidth == 250, "Rejected crop preserves size and history");
        string saved = Path.Combine(output, "cropped.png");
        SaveSnapshotResult savedResult = await session.SaveAsync(saved);
        Check(savedResult.SavedCurrentRevision && !session.IsDirty, "Saving cropped work records its current revision");
        using EditorSession reopened = new();
        reopened.Open(saved);
        Check(reopened.ImageWidth == 250 && reopened.ImageHeight == 170 && reopened.AnnotationCount == 0, "Flattened export reopens as an image of the correct dimensions");
        Check(PixelHash((await reopened.RenderPreviewAsync()).PngBytes) == PixelHash(cropped), "Export and reopen retain every cropped rendered pixel");

        using EditorSession removing = new();
        removing.OpenImageBytes(source);
        Check(removing.RemoveArea(new(100, 100), new(20, 20), 0x00000000), "Removing a source area accepts a reverse drag and transparent background");
        byte[] removed = (await removing.RenderPreviewAsync()).PngBytes;
        Check(Pixel(removed, 50, 50) == 0x00000000, "Remove area writes the requested transparent pixel");
        Check(Pixel(removed, 15, 15) == Pixel(source, 15, 15) && Pixel(removed, 110, 110) == Pixel(source, 110, 110), "Source-area removal preserves pixels outside the region");
        removing.Undo();
        Check(PixelHash((await removing.RenderPreviewAsync()).PngBytes) == PixelHash(source), "Undo source-area removal restores every source pixel");
        removing.Redo();
        Check(PixelHash((await removing.RenderPreviewAsync()).PngBytes) == PixelHash(removed), "Redo source-area removal preserves alpha and pixels");
    }

    private static async Task CheckTextFidelityAsync()
    {
        const string text = "T0000DEMO0000A0O1lI_Example_Details.pdf\r\nDEMO0000000001AaBb00_Example_Details (1).pdf\r\n00001234 A0O1lI\r\n00001234 A0O1lI";
        using EditorSession session = new();
        byte[] source = CreateImage(640, 240);
        session.OpenImageBytes(source);
        int index = session.AddText(text, new(12, 15), 0xFF175AA0, 12, 610, minimumHeight: 90);
        Check(index == 0 && session.AnnotationCount == 1, "Text creates an editable annotation");
        var original = session.GetAnnotation(index)!;
        Check(original.Text is not null && original.Shape is null && original.Emoji is null, "Text snapshot has the expected type");
        Check(original.Text!.Text == text, "Plain text preserves identifiers, leading zeros, case, whitespace and duplicate lines exactly");
        Check(original.Text.Argb == 0xFF175AA0 && original.Text.FontSize == 12 && original.Text.LayoutWidth == 610 && original.Text.MinimumHeight == 90, "Text keeps chosen color, source-pixel font size, width and height");
        Check(!string.IsNullOrWhiteSpace(original.Text.Rtf) && !string.IsNullOrWhiteSpace(original.Text.FontFamily), "Text exposes detached formatting for continued editing");
        Check(original.Bounds.X == 12 && original.Bounds.Y == 15, "Text starts at its requested source location");
        byte[] firstPixels = (await session.RenderPreviewAsync()).PngBytes;
        Check(PixelHash(firstPixels) != PixelHash(source), "Text produces visible rendered output");
        Check(session.MoveAnnotation(index, new(8, 5)), "Text can be moved as an object");
        var moved = session.GetAnnotation(index)!;
        Check(moved.Text!.Text == text && moved.Text.Rtf == original.Text.Rtf && moved.Text.FontSize == 12 && moved.Text.Argb == 0xFF175AA0, "Moving text preserves exact content and source style");
        Check(moved.Bounds.X == 20 && moved.Bounds.Y == 20, "Text move changes only the location");
        const string edited = "00000042_Aa-01.pdf\r\n00000042_Aa-01.pdf\r\nQuantity 0007";
        Check(session.UpdateText(index, edited, 0xFF8B008B, 16, 600, minimumHeight: 85), "Text edit commits");
        var updated = session.GetAnnotation(index)!;
        Check(updated.Text!.Text == edited && updated.Text.Argb == 0xFF8B008B && updated.Text.FontSize == 16 && updated.Text.LayoutWidth == 600 && updated.Text.MinimumHeight == 85, "Text edit retains exact user changes and formatting values");
        Check(original.Text.Text == text && original.Text.FontSize == 12, "Older text snapshot remains unchanged after editing");
        byte[] editedPixels = (await session.RenderPreviewAsync()).PngBytes;
        Check(PixelHash(editedPixels) != PixelHash(firstPixels), "Edited text is reflected in rendered output");
        session.Undo();
        var undone = session.GetAnnotation(index)!;
        Check(undone.Text!.Text == text && undone.Text.Argb == 0xFF175AA0 && undone.Text.FontSize == 12 && undone.Bounds.X == 20, "Undo text edit restores content and formatting while retaining the preceding move");
        session.Undo();
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(firstPixels), "Undo text move restores exact prior pixels");
        session.Redo(); session.Redo();
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(editedPixels), "Redo text movement and edit restore exact rendered pixels");
        long beforeFailure = session.Revision;
        Expect<ArgumentException>(() => session.UpdateText(index, "different plain value", 0xFF000000, 16, 600, original.Text.Rtf), "Mismatched rich text is rejected");
        Check(session.Revision == beforeFailure && session.GetAnnotation(index)!.Text!.Text == edited, "Failed rich-text replacement preserves the user's current edit and history");
    }

    private static void CheckEmojiClickPlacement()
    {
        using EditorSession session = new();
        session.OpenImageBytes(CreateImage(62, 34));
        Check(session.PlaceEmoji("smile", new(0, 0)), "A single image point places the chosen emoji");
        var first = session.GetAnnotation(0)?.Emoji;
        Check(first is { Key: "smile", Frame: { X: 0, Y: 0, Width: > 0 and <= 62, Height: > 0 and <= 34 } },
            "Click placement keeps the sticker inside a small image at the top-left corner");
        Check(session.PlaceEmoji("smile", new(61, 33)), "The same sticker can be placed again without reselecting it");
        var last = session.GetAnnotation(1)?.Emoji;
        Check(last is not null && Math.Abs(last.Frame.X + last.Frame.Width - 62) < 0.01 &&
            Math.Abs(last.Frame.Y + last.Frame.Height - 34) < 0.01, "Far-edge placement clamps the entire sticker to the image");
        session.Undo();
        Check(session.AnnotationCount == 1, "Undo removes only the most recent sticker");
    }

    private static async Task CheckEmojiAndEraseAsync()
    {
        byte[] source = CreateImage(360, 240);
        using EditorSession session = new();
        session.OpenImageBytes(source);
        Check(session.AddEmoji("smile", new(40, 40), new(120, 120), 25), "Embedded emoji artwork commits");
        var emoji = session.GetAnnotation(0)!;
        Check(emoji.Emoji?.Key == "smile" && emoji.Emoji.RotationDegrees == 25 && emoji.Emoji.Frame.Width == 80 && emoji.Emoji.Frame.Height == 80, "Emoji snapshot preserves catalog identity and geometry");
        byte[] sticker = (await session.RenderPreviewAsync()).PngBytes;
        Check(PixelHash(sticker) != PixelHash(source), "Emoji artwork renders without a font substitution");
        Check(session.RemoveAnnotation(0) && session.AnnotationCount == 0, "Selected-object delete removes the emoji");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(source), "Deleting the annotation reveals every original source pixel");
        session.Undo();
        Check(session.AnnotationCount == 1 && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(sticker), "Undo delete restores exact artwork and placement");
        Check(!session.RemoveAnnotation(-1) && session.GetAnnotation(-1) is null, "Missing selected object is not mistaken for the first annotation");
        session.AddShape(AnnotationShape.Rectangle, new(150, 40), new(260, 130), 0xFF000000, 2, 0xFFFF0000);
        session.AddShape(AnnotationShape.Ellipse, new(170, 60), new(240, 120), 0xFF000000, 2, 0xFF008000);
        byte[] layered = (await session.RenderPreviewAsync()).PngBytes;
        Check(session.HitTest(new(205, 90), 1) == 2, "Selection chooses the visible topmost overlapping annotation");
        Check(session.EraseAt(new(205, 90), 1) && session.AnnotationCount == 2, "Eraser removes one topmost object per operation");
        Check(Pixel((await session.RenderPreviewAsync()).PngBytes, 205, 90) == 0xFFFF0000, "Erasing the top object preserves the underlying annotation");
        session.Undo();
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(layered), "Undo erasing restores the exact layered render");
        Check(session.EraseAllAnnotations() && session.AnnotationCount == 0, "Erase-all removes every annotation in one action");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(source), "Erase-all retains every captured source pixel");
        long revision = session.Revision;
        Check(!session.EraseAllAnnotations() && session.Revision == revision, "Repeated erase-all on an empty annotation set creates no history");
        session.Undo();
        Check(session.AnnotationCount == 3 && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(layered), "Single Undo restores all erased annotations");
    }

    private static void CheckOcrDtoOwnership()
    {
        const string identifier = "00001234_Aa0O1lI.pdf";
        var word = new OcrWordSnapshot(identifier, new OcrBounds(10, 20, 160, 14));
        var words = new List<OcrWordSnapshot> { word, new(identifier, new(180, 20, 160, 14)) };
        var line = new OcrLineSnapshot(7, identifier + " " + identifier, new(10, 20, 330, 14), words);
        words.Clear();
        Check(line.Index == 7 && line.Text == identifier + " " + identifier, "OCR line DTO retains its source index, leading zeros, case and duplicate text");
        Check(line.Words.Count == 2 && line.Words[0].Text == identifier && line.Words[1].Text == identifier, "OCR line owns a copy and retains duplicate words in source order");
        Check(line.Words[0].Bounds.X == 10 && line.Words[1].Bounds.X == 180 && line.Bounds.Width == 330, "OCR DTO preserves word and line geometry without merging");
        if (line.Words is IList<OcrWordSnapshot> mutableView)
        {
            Expect<NotSupportedException>(() => mutableView.Clear(), "OCR read-only word collection rejects caller mutation");
            Check(line.Words.Count == 2, "Rejected OCR collection mutation preserves recognized words");
        }

        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage(360, 120));
        var baseline = session.GetCaptureBaseline();
        const string firstRow = identifier + "  0042";
        const string raw = firstRow + " " + identifier + " " + identifier;
        var result = new global::SnipStudio.TextRecognitionResult { Available = true, Text = raw, RawText = raw };
        result.Lines.Add(new() { Text = firstRow });
        result.Lines.Add(new() { Text = identifier });
        result.Lines.Add(new() { Text = identifier });
        var snapshot = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);
        Check(snapshot.Text == firstRow + "\r\n" + identifier + "\r\n" + identifier,
            "Copy-all text preserves Windows line breaks, exact spacing, leading zeros and repeated rows even when the provider aggregate is flat");
        Check(snapshot.RawText == raw, "Formatting copy-all text retains the original provider output for diagnostics");

        result.Lines.Clear();
        result.Text = "first\nsecond\rthird";
        var textOnly = new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);
        Check(textOnly.Text == "first\r\nsecond\r\nthird", "Copy-all preserves line breaks in text-only recognition without word geometry");
    }

    private static void CheckOcrVerticalReadingOrder()
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage(760, 300));
        var baseline = session.GetCaptureBaseline();
        var heading = Row("First in the image", 220, 16);
        var timestamp = Row("4:16 PM", 645, 44, 54);
        var body = Row("Details  0008", 10, 80);
        var duplicate = Row("Details  0008", 24, 111);
        var fragment = Row("Separate run on the same row", 655, 109, 95);
        var next = Row("Next row", 28, 147);
        var detail = Row("Another row", 10, 183);
        var indented = Row("Label after two icons", 51, 217);
        var beforeLast = Row("A second body row", 10, 250);
        var last = Row("End", 10, 280);
        var source = new global::SnipStudio.TextRecognitionResult { Available = true, RawText = "Unchanged provider result" };
        source.Lines.AddRange([timestamp, heading, body, duplicate, fragment, next, detail, indented, beforeLast, last]);

        OcrSnapshot snapshot = Snapshot(source);
        string expected = string.Join("\r\n", new[] { heading, timestamp, body, duplicate, fragment, next, detail, indented, beforeLast, last }.Select(line => line.Text));
        Check(snapshot.Text == expected && snapshot.RawText == source.RawText && ReferenceEquals(source.Lines[0], timestamp),
            "A timestamp no longer jumps above the heading in a single vertical flow, while row fragments and OCR output remain intact");
        var selection = session.SelectRecognizedText(snapshot, new(230, 20), new(20, 285));
        Check(selection.Text == expected && selection.LineIndices.SequenceEqual(Enumerable.Range(0, 10)) &&
            snapshot.Lines[1].Bounds.X == 645, "Copy and on-image selection use the same line order and preserve duplicate text and positions");

        foreach (int offset in new[] { 0, 7 })
        {
            var left = new[] { Row("A", 10, 32), Row("B", 10, 68), Row("C", 10, 104), Row("D", 10, 140) };
            var right = new[] { Row("E", 240, 68 + offset), Row("F", 240, 104 + offset) };
            source.Lines.Clear();
            source.Lines.AddRange(left.Concat(right));
            Check(Snapshot(source).Text == "A\r\nB\r\nC\r\nD\r\nE\r\nF",
                offset == 0 ? "Aligned table columns keep provider order" : "Staggered columns keep provider order");
        }

        source.Lines.Clear();
        source.Lines.AddRange([next, body, last]);
        source.TextAngle = 15;
        Check(Snapshot(source).Text == "Next row\r\nDetails  0008\r\nEnd",
            "Angled text keeps provider order");
        source.TextAngle = null;
        source.Lines.Add(Row("Missing bounds", float.NaN, 22));
        Check(Snapshot(source).Lines[0].Text == next.Text, "Invalid geometry keeps provider order");

        OcrSnapshot Snapshot(global::SnipStudio.TextRecognitionResult result) =>
            new(session, baseline.ImageIdentity, baseline.Revision, result);

        static global::SnipStudio.TextLineRegion Row(string text, float x, float y, float width = 130)
        {
            RectangleF bounds = new(x, y, width, 12);
            var row = new global::SnipStudio.TextLineRegion { Text = text, Bounds = bounds };
            row.Words.Add(new global::SnipStudio.TextWordRegion { Text = text, Bounds = bounds });
            return row;
        }
    }

    private static void CheckNativeOcrSelectionIndex()
    {
        const string identifier = "00007_Aa.pdf";
        var blank = new global::SnipStudio.TextLineRegion { Text = "", Bounds = new RectangleF(0, 0, 1, 1) };
        var invalid = new global::SnipStudio.TextLineRegion { Text = "not selectable", Bounds = new RectangleF(0, 10, 100, 10) };
        invalid.Words.Add(new global::SnipStudio.TextWordRegion { Text = "not selectable", Bounds = new RectangleF(float.NaN, 10, 100, 10) });
        var duplicated = new global::SnipStudio.TextLineRegion { Text = identifier + "\t" + identifier, Bounds = new RectangleF(10, 30, 240, 14) };
        duplicated.Words.Add(new global::SnipStudio.TextWordRegion { Text = identifier, Bounds = new RectangleF(10, 30, 100, 14) });
        duplicated.Words.Add(new global::SnipStudio.TextWordRegion { Text = identifier, Bounds = new RectangleF(150, 30, 100, 14) });
        var next = new global::SnipStudio.TextLineRegion { Text = identifier, Bounds = new RectangleF(10, 60, 100, 14) };
        next.Words.Add(new global::SnipStudio.TextWordRegion { Text = identifier, Bounds = new RectangleF(10, 60, 100, 14) });
        var selection = new global::SnipStudio.TextOverlaySelection(new List<global::SnipStudio.TextLineRegion> { blank, invalid, duplicated, next });
        Check(selection.Count == 3, "OCR selection excludes blank and invalid-geometry words without creating selectable placeholders");
        selection.Select(selection.HitTest(new PointF(30, 35), 0), selection.HitTest(new PointF(30, 35), 0));
        Check(selection.SelectedLineIndex == 2 && selection.SelectedText == identifier, "First selectable word retains original line index after skipped rows");
        selection.Select(1, 1);
        Check(selection.SelectedLineIndex == 2 && selection.TryGetSelectedCharacterRange(duplicated.Text, out int start, out int length) && start == identifier.Length + 1 && length == identifier.Length,
            "Selecting the second duplicate word uses its actual character offset");
        selection.Select(0, 1);
        Check(selection.SelectedText == duplicated.Text, "Selection preserves the exact native tab between duplicate words");
        selection.Select(2, 2);
        Check(selection.SelectedLineIndex == 3 && selection.SelectedText == identifier, "Following row retains its original index rather than a filtered-list index");
        selection.Select(2, 0);
        Check(selection.SelectedLineIndex == -1 && selection.SelectedText == duplicated.Text + Environment.NewLine + identifier, "Reverse multi-row selection preserves reading order and duplicate rows");
    }

    private static async Task CheckClippedAndLinePropertiesAsync()
    {
        byte[] source = CreateImage(360, 240);
        foreach (bool vertical in new[] { false, true })
        {
            using EditorSession session = new();
            session.OpenImageBytes(source);
            session.AddShape(AnnotationShape.Line, new(30, 40), vertical ? new(30, 180) : new(220, 40), 0xFF000000, 3);
            var line = session.GetAnnotation(0)!;
            long revision = session.Revision;
            Check(!session.UpdateAnnotationProperties(line, line.Shape!.GeometryBounds) && session.Revision == revision,
                "Unchanged " + (vertical ? "vertical" : "horizontal") + " line frame is a no-op");
            Check(session.UpdateAnnotationProperties(line, line.Shape.GeometryBounds, new ShapeStyleEdit(null, 0xFF8B008B, 4)),
                "A " + (vertical ? "vertical" : "horizontal") + " line accepts a style-only edit with its zero-span frame");
            var changed = session.GetAnnotation(0)!;
            Check(changed.Shape!.GeometryBounds == line.Shape.GeometryBounds && changed.Shape.Endpoints.SequenceEqual(line.Shape.Endpoints) && changed.Shape.OutlineArgb == 0xFF8B008B,
                "Style-only line editing preserves exact endpoints and direction");
        }

        using EditorSession cropped = new();
        cropped.OpenImageBytes(source);
        cropped.AddShape(AnnotationShape.Rectangle, new(10, 10), new(100, 100), 0xFF000000, 2, 0xFFFF0000);
        cropped.Crop(new(40, 40), new(200, 180));
        var clipped = cropped.GetAnnotation(0)!;
        Check(clipped.Shape!.GeometryBounds == new ImageRectangle(-30, -30, 90, 90), "Crop retains partially visible object geometry beyond the new image edge");
        byte[] before = (await cropped.RenderPreviewAsync()).PngBytes;
        Check(cropped.UpdateAnnotationProperties(clipped, clipped.Shape.GeometryBounds, new ShapeStyleEdit(0xFF008000, 0xFF000000, 2)),
            "Partially cropped object accepts a style-only edit without moving its hidden portion");
        var styled = cropped.GetAnnotation(0)!;
        Check(styled.Shape!.GeometryBounds == clipped.Shape.GeometryBounds && Pixel((await cropped.RenderPreviewAsync()).PngBytes, 10, 10) == 0xFF008000,
            "Cropped object preserves geometry while its visible fill changes");
        cropped.Undo();
        Check(PixelHash((await cropped.RenderPreviewAsync()).PngBytes) == PixelHash(before), "Undo style-only editing retains the crop and exact prior clipped render");
    }

    private static async Task CheckSelectionOwnershipAndContinuousEraseAsync()
    {
        byte[] source = CreateImage(360, 240);
        using EditorSession first = new();
        using EditorSession second = new();
        first.OpenImageBytes(source);
        second.OpenImageBytes(source);
        first.AddShape(AnnotationShape.Rectangle, new(30, 30), new(80, 80), 0xFF000000, 2, 0xFF0000FF);
        second.AddShape(AnnotationShape.Rectangle, new(30, 30), new(80, 80), 0xFF000000, 2, 0xFF008000);
        var foreign = second.GetAnnotation(0)!;
        Check(first.Revision == second.Revision, "Ownership fixture uses independently equal revision values");
        Check(!first.IsAnnotationCurrent(foreign), "An equal revision in another editor does not identify this editor's selected object");
        Expect<InvalidOperationException>(() => first.RemoveAnnotation(foreign), "Cross-session selected-object deletion is rejected");
        Check(first.AnnotationCount == 1 && first.GetAnnotation(0)!.Shape!.FillArgb == 0xFF0000FF, "Rejected foreign selection preserves the local annotation");
        var selected = first.GetAnnotation(0)!;
        Check(first.IsAnnotationCurrent(selected), "A fresh local selection is current");
        Check(first.ResizeAnnotation(0, new(100, 80, 70, 50)), "Selected closed shape can be resized");
        var resized = first.GetAnnotation(0)!;
        Check(resized.Shape!.GeometryBounds == new ImageRectangle(100, 80, 70, 50), "Resize keeps exact requested closed-shape geometry");
        Check(!first.IsAnnotationCurrent(selected), "Prior selection becomes stale after an edit");
        long revision = first.Revision;
        Expect<InvalidOperationException>(() => first.MoveAnnotation(selected, new(1, 1)), "Stale selected-object move is rejected");
        Check(first.Revision == revision && first.GetAnnotation(0)!.Bounds == resized.Bounds, "Rejected stale move preserves current object geometry and revision");
        byte[] beforeProperties = (await first.RenderPreviewAsync()).PngBytes;
        Check(first.UpdateAnnotationProperties(resized, new(190, 100, 80, 60), new ShapeStyleEdit(0xFFFF8000, 0xFF8B008B, 5, 65, 90)),
            "Geometry and shape styling can be applied together");
        var properties = first.GetAnnotation(0)!;
        Check(properties.Shape!.GeometryBounds == new ImageRectangle(190, 100, 80, 60) && properties.Shape.FillArgb == 0xFFFF8000 &&
            properties.Shape.OutlineArgb == 0xFF8B008B && properties.Shape.OutlineWidth == 5 && properties.Shape.FillOpacity == 65 && properties.Shape.OutlineOpacity == 90,
            "Combined properties retain exact reviewed geometry, colors, widths and opacity values");
        byte[] afterProperties = (await first.RenderPreviewAsync()).PngBytes;
        first.Undo();
        Check(first.GetAnnotation(0)!.Shape!.GeometryBounds == resized.Shape!.GeometryBounds && first.GetAnnotation(0)!.Shape!.FillArgb == 0xFF0000FF &&
            PixelHash((await first.RenderPreviewAsync()).PngBytes) == PixelHash(beforeProperties), "One Undo restores both geometry and style from the properties action");
        first.Redo();
        Check(PixelHash((await first.RenderPreviewAsync()).PngBytes) == PixelHash(afterProperties), "Redo restores the complete combined properties render");

        using EditorSession emojiSession = new();
        emojiSession.OpenImageBytes(source);
        emojiSession.AddEmoji("smile", new(30, 30), new(100, 100), 10);
        var emojiBefore = emojiSession.GetAnnotation(0)!;
        byte[] emojiPixels = (await emojiSession.RenderPreviewAsync()).PngBytes;
        Check(emojiSession.UpdateAnnotationProperties(emojiBefore, new(160, 80, 90, 100), rotationDegrees: 50), "Emoji geometry and rotation can be applied together");
        var emojiAfter = emojiSession.GetAnnotation(0)!;
        Check(emojiAfter.Emoji!.Frame == new ImageRectangle(160, 80, 90, 100) && emojiAfter.Emoji.RotationDegrees == 50 && emojiAfter.Emoji.Key == "smile",
            "Combined emoji properties retain identity, exact geometry and rotation");
        emojiSession.Undo();
        Check(emojiSession.GetAnnotation(0)!.Emoji!.Frame == emojiBefore.Emoji!.Frame && emojiSession.GetAnnotation(0)!.Emoji!.RotationDegrees == 10 &&
            PixelHash((await emojiSession.RenderPreviewAsync()).PngBytes) == PixelHash(emojiPixels), "One Undo restores both emoji geometry and rotation");

        using EditorSession erasing = new();
        erasing.OpenImageBytes(source);
        erasing.AddShape(AnnotationShape.Rectangle, new(30, 90), new(60, 120), 0xFF000000, 2, 0xFFFF0000);
        erasing.AddShape(AnnotationShape.Rectangle, new(150, 90), new(180, 120), 0xFF000000, 2, 0xFF008000);
        erasing.AddShape(AnnotationShape.Rectangle, new(270, 90), new(300, 120), 0xFF000000, 2, 0xFF0000FF);
        byte[] three = (await erasing.RenderPreviewAsync()).PngBytes;
        Check(erasing.ErasePath(new[] { new ImagePoint(10, 105), new ImagePoint(330, 105) }, 3), "Continuous eraser intersects objects between sparse pointer samples");
        Check(erasing.AnnotationCount == 0 && PixelHash((await erasing.RenderPreviewAsync()).PngBytes) == PixelHash(source), "Continuous eraser removes all touched objects and preserves source pixels");
        erasing.Undo();
        Check(erasing.AnnotationCount == 3 && PixelHash((await erasing.RenderPreviewAsync()).PngBytes) == PixelHash(three), "One Undo restores the entire continuous eraser gesture");
        long beforeBadGesture = erasing.Revision;
        Expect<ArgumentOutOfRangeException>(() => erasing.ErasePath(new[] { new ImagePoint(10, 105), new ImagePoint(double.NaN, 105) }, 3), "A malformed later eraser point rejects the whole gesture");
        Check(erasing.Revision == beforeBadGesture && erasing.AnnotationCount == 3, "Invalid continuous eraser input cannot partially erase objects");
    }

    private static async Task CheckCancellationAndInvalidEditsAsync()
    {
        using EditorSession session = new();
        session.OpenImageBytes(CreateImage(360, 240));
        session.AddText("0007 Aa-01", new(20, 20), 0xFF000000, 14);
        long revision = session.Revision;
        byte[] before = (await session.RenderPreviewAsync()).PngBytes;
        Expect<ArgumentOutOfRangeException>(() => session.AddShape(AnnotationShape.Arrow, new(double.NaN, 1), new(20, 30), 0xFF000000, 3), "Nonfinite shape coordinates are rejected");
        Expect<ArgumentOutOfRangeException>(() => session.MoveAnnotation(0, new(double.PositiveInfinity, 1)), "Nonfinite move delta is rejected");
        Expect<ArgumentOutOfRangeException>(() => session.AddText("must not replace", new(1, 1), 0xFF000000, float.NaN), "Nonfinite text size is rejected");
        Expect<ArgumentOutOfRangeException>(() => session.EraseAt(new(20, 20), 0), "Zero eraser radius is rejected");
        Check(session.Revision == revision && session.AnnotationCount == 1 && session.GetAnnotation(0)!.Text!.Text == "0007 Aa-01", "Invalid mutations retain current text, count and revision");
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => session.RecognizeTextAsync(canceled.Token), "Pre-canceled OCR does not begin recognition");
        Check(session.Revision == revision && PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(before), "Canceled OCR preserves all document content and rendered pixels");
    }

    private static async Task MeasureLargeImageWorkAsync(string output)
    {
        // This deliberately bounded fixture is a grid of opaque colored panels,
        // not a photograph or a UI screenshot. Encoding characteristics differ
        // from those inputs, so its timings cannot establish all-image speed.
        const int width = 4096, height = 2160, editCount = 8;
        byte[] source;
        using (Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                for (int row = 0; row < 12; row++)
                    for (int column = 0; column < 16; column++)
                    {
                        using Brush brush = new SolidBrush(Color.FromArgb(255, (column * 31 + row * 17) % 256,
                            (column * 13 + row * 41) % 256, (column * 7 + row * 29) % 256));
                        graphics.FillRectangle(brush, column * 256 + 5, row * 180 + 5, 246, 170);
                    }
            }
            using MemoryStream encoded = new();
            bitmap.Save(encoded, ImageFormat.Png);
            source = encoded.ToArray();
        }

        List<object> samples = new();
        samples.Add(SampleMemory("Before session"));
        using (EditorSession session = new())
        {
            Stopwatch clock = Stopwatch.StartNew();
            session.OpenImageBytes(source);
            clock.Stop();
            performance.Add(new { Operation = "OpenImageBytes", Input = "Opaque 16×12 colored-panel PNG grid", Width = width, Height = height,
                InputBytes = source.Length, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
            Check(session.ImageWidth == width && session.ImageHeight == height, "Large fixture opens at its original dimensions");
            samples.Add(SampleMemory("After open"));

            clock.Restart();
            PreviewSnapshot preview = await session.RenderPreviewAsync();
            clock.Stop();
            performance.Add(new { Operation = "RenderPreviewAsync", Width = width, Height = height, OutputBytes = preview.PngBytes.Length,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
            Check(preview.Revision == session.Revision && preview.PngBytes.Length > 0, "Large preview identifies the current document");
            samples.Add(SampleMemory("After first preview"));

            var editTimes = new List<double>();
            for (int index = 0; index < editCount; index++)
            {
                clock.Restart();
                session.AddStroke(new[] { new ImagePoint(32, 40 + index * 220), new ImagePoint(width - 32, 40 + index * 220) },
                    0xFFE24046, 4, false);
                preview = await session.RenderPreviewAsync();
                clock.Stop();
                editTimes.Add(clock.Elapsed.TotalMilliseconds);
            }
            performance.Add(new { Operation = "AddStroke plus RenderPreviewAsync", Width = width, Height = height,
                Iterations = editCount, EachElapsedMilliseconds = editTimes, TotalElapsedMilliseconds = editTimes.Sum(),
                FinalPreviewBytes = preview.PngBytes.Length });
            Check(session.AnnotationCount == editCount && preview.Revision == session.Revision, "Repeated large-image edits remain committed and previewed");
            samples.Add(SampleMemory("After eight edit/preview operations"));

            string path = Path.Combine(output, "performance-large.png");
            clock.Restart();
            SaveSnapshotResult result = await session.SaveAsync(path);
            clock.Stop();
            performance.Add(new { Operation = "Atomic PNG SaveAsync", Width = width, Height = height,
                OutputBytes = new FileInfo(path).Length, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
            Check(result.SavedCurrentRevision && !session.IsDirty, "Large-image atomic save completes for the current revision");
            using Bitmap saved = new(path);
            Check(saved.Width == width && saved.Height == height && saved.GetPixel(2048, 40).ToArgb() == unchecked((int)0xFFE24046),
                "Large-image saved output retains dimensions and a committed source-pixel stroke");
            samples.Add(SampleMemory("After save and independent decode"));
        }
        samples.Add(SampleMemory("After session disposal; no forced collection"));
        performance.Add(new { Operation = "Process memory samples", Samples = samples,
            Interpretation = "Working set and private bytes for the entire check process, including runtime, prior checks and transient native/managed allocations. Point samples do not establish peak memory or absence of a leak." });
    }

    private static async Task CheckCaptureRequestCancellationAsync()
    {
        // Only invalid or pre-canceled requests are passed. These paths must
        // exit before a capture thread, overlay, pixel read or desktop input.
        Expect<ArgumentNullException>(() => CaptureCoordinator.CaptureAsync(null!), "Missing capture request is rejected");
        Expect<ArgumentOutOfRangeException>(() => CaptureCoordinator.CaptureAsync(new CaptureRequest { Kind = (CaptureKind)999 }), "Unknown capture mode is rejected");
        Expect<ArgumentOutOfRangeException>(() => CaptureCoordinator.CaptureAsync(new CaptureRequest { DelaySeconds = -1 }), "Negative capture delay is rejected");
        Expect<ArgumentOutOfRangeException>(() => CaptureCoordinator.CaptureScrollingAsync(new CaptureRequest { DelaySeconds = 11 }), "Out-of-range scrolling capture delay is rejected");
        Expect<ArgumentException>(() => CaptureCoordinator.SelectRecordingRegionAsync(new CaptureRequest { Kind = CaptureKind.Freeform }), "Recording rejects an unsupported region mode");
        Expect<ArgumentException>(() => CaptureCoordinator.AdjustRecordingRegionAsync(0, new RecordingRegion(0, 0, 1, 200)),
            "Recording area adjustment rejects a region too narrow to capture");
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        var request = new CaptureRequest { OwnerWindow = 0, Kind = CaptureKind.Rectangle };
        await ExpectAsync<OperationCanceledException>(() => CaptureCoordinator.CaptureAsync(request, canceled.Token), "Pre-canceled screenshot capture exits without selecting a region");
        await ExpectAsync<OperationCanceledException>(() => CaptureCoordinator.CaptureScrollingAsync(request, cancellationToken: canceled.Token), "Pre-canceled scrolling capture exits without selecting a region");
        await ExpectAsync<OperationCanceledException>(() => CaptureCoordinator.SelectRecordingRegionAsync(request, canceled.Token), "Pre-canceled recording-region selection exits without selecting a region");
        await ExpectAsync<OperationCanceledException>(() => CaptureCoordinator.AdjustRecordingRegionAsync(0,
            new RecordingRegion(-600, -200, 320, 180), canceled.Token),
            "Pre-canceled adjustment accepts negative desktop coordinates and exits before opening the overlay");
    }

    private static async Task CheckVideoCopyAsync(string output)
    {
        // Save-copy is a byte transaction, so these opaque bytes deliberately
        // are not passed into a decoder or described as a playable video.
        byte[] opaque = new byte[400001];
        for (int index = 0; index < opaque.Length; index++) opaque[index] = (byte)((index * 37 + 11) % 251);
        string source = Path.Combine(output, "opaque-copy-fixture.mp4");
        string copy = Path.Combine(output, "opaque-copy-result.mp4");
        File.WriteAllBytes(source, opaque);
        Expect<ArgumentException>(() => VideoReviewService.SaveCopyAsync(source, source), "Save-copy refuses to overwrite its original");
        Expect<ArgumentException>(() => VideoReviewService.SaveCopyAsync(source, Path.Combine(output, "not-converted.avi")), "Save-copy cannot masquerade as format conversion");
        Check(!File.Exists(Path.Combine(output, "not-converted.avi")) && File.ReadAllBytes(source).SequenceEqual(opaque), "Rejected copy requests leave source bytes and filenames intact");
        await VideoReviewService.SaveCopyAsync(source, copy);
        Check(File.ReadAllBytes(copy).SequenceEqual(opaque), "Buffered save-copy preserves every source byte across buffer boundaries");
        byte[] existing = Encoding.UTF8.GetBytes("Previous user file must survive cancellation and publication failure.");
        File.WriteAllBytes(copy, existing);
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => VideoReviewService.SaveCopyAsync(source, copy, canceled.Token), "Pre-canceled video copy is rejected");
        }
        Check(File.ReadAllBytes(copy).SequenceEqual(existing), "Canceled video copy preserves the previous destination");
        using (FileStream locked = new(copy, FileMode.Open, FileAccess.Read, FileShare.None))
            await ExpectAsync<IOException>(() => VideoReviewService.SaveCopyAsync(source, copy), "Locked destination prevents copy publication");
        Check(File.ReadAllBytes(copy).SequenceEqual(existing) && File.ReadAllBytes(source).SequenceEqual(opaque), "Failed copy publication preserves previous destination and original source");
        await ExpectAsync<FileNotFoundException>(() => VideoReviewService.SaveCopyAsync(Path.Combine(output, "not-present.mp4"), copy), "Missing source reports a copy failure");
        Check(File.ReadAllBytes(copy).SequenceEqual(existing), "Missing copy source cannot replace an existing destination");
        Check(!Directory.EnumerateFiles(output, ".snip-video-copy-*", SearchOption.TopDirectoryOnly).Any(), "Canceled and failed copies leave no temporary file");
        await VideoReviewService.SaveCopyAsync(source, copy);
        Check(File.ReadAllBytes(copy).SequenceEqual(opaque), "Copy retries successfully after a failed atomic replacement");

        string contested = Path.Combine(output, "opaque-video-copy-contested.mp4");
        ImageSaveTarget cleared = AtomicImageWriter.PrepareDestination(contested);
        File.WriteAllBytes(contested, existing);
        await ExpectAsync<IOException>(() => Task.Run(() =>
            VideoReviewService.SavePreparedCopy(source, cleared, CancellationToken.None, maximumBytes: null)),
            "Video copy cannot replace a destination created after selection");
        Check(File.ReadAllBytes(contested).SequenceEqual(existing), "A later-created video destination keeps every competing byte");

        ImageSaveTarget chosen = AtomicImageWriter.PrepareDestination(contested);
        DateTime written = File.GetLastWriteTimeUtc(contested);
        DateTime created = File.GetCreationTimeUtc(contested);
        byte[] later = (byte[])existing.Clone();
        later[^1] ^= 1;
        File.WriteAllBytes(contested, later);
        File.SetLastWriteTimeUtc(contested, written);
        Check(File.GetCreationTimeUtc(contested) == created && File.GetLastWriteTimeUtc(contested) == written &&
            new FileInfo(contested).Length == existing.Length, "The competing video update preserves the original file metadata");
        await ExpectAsync<IOException>(() => Task.Run(() =>
            VideoReviewService.SavePreparedCopy(source, chosen, CancellationToken.None, maximumBytes: null)),
            "Video copy rejects an external edit that preserves timestamps and length");
        Check(File.ReadAllBytes(contested).SequenceEqual(later), "The conflicting video destination remains byte-exact");

        ImageSaveTarget confirmed = AtomicImageWriter.PrepareDestination(contested);
        await Task.Run(() => VideoReviewService.SavePreparedCopy(source, confirmed, CancellationToken.None, maximumBytes: null));
        Check(File.ReadAllBytes(contested).SequenceEqual(opaque) && File.ReadAllBytes(source).SequenceEqual(opaque),
            "An explicitly reselected video destination accepts the complete staged copy");
        Check(!Directory.EnumerateFiles(output, ".snip-video-copy-*").Any() &&
            !Directory.EnumerateFiles(output, ".snip-winui-recovery-*").Any(),
            "Safe video replacement leaves no staging files or obsolete recovery copies");
    }

    private static object SampleMemory(string stage)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new { Stage = stage, SampledUtc = DateTimeOffset.UtcNow, WorkingSetBytes = process.WorkingSet64,
            PrivateMemoryBytes = process.PrivateMemorySize64, ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false) };
    }

    private static byte[] CreateImage(int width, int height)
    {
        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, (x * 13 + y * 3) % 256, (x * 5 + y * 7) % 256, (x * 11 + y * 17) % 256));
        using MemoryStream encoded = new();
        bitmap.Save(encoded, ImageFormat.Png);
        return encoded.ToArray();
    }

    private static uint Pixel(byte[] png, int x, int y)
    {
        using MemoryStream stream = new(png, writable: false);
        using Bitmap bitmap = new(stream);
        return unchecked((uint)bitmap.GetPixel(x, y).ToArgb());
    }

    private static string PixelHash(byte[] png)
    {
        using MemoryStream stream = new(png, writable: false);
        using Bitmap bitmap = new(stream);
        return PixelHash(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
    }

    private static string CropPixelHash(byte[] png, Rectangle crop)
    {
        using MemoryStream stream = new(png, writable: false);
        using Bitmap bitmap = new(stream);
        return PixelHash(bitmap, crop);
    }

    private static string PixelHash(Bitmap bitmap, Rectangle area)
    {
        byte[] pixels = new byte[checked(area.Width * area.Height * 4)];
        int index = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                uint argb = unchecked((uint)bitmap.GetPixel(x, y).ToArgb());
                pixels[index++] = (byte)(argb >> 24);
                pixels[index++] = (byte)(argb >> 16);
                pixels[index++] = (byte)(argb >> 8);
                pixels[index++] = (byte)argb;
            }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + message);
        assertions++;
    }

    private static void Expect<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { Check(true, message); return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + message);
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { Check(true, message); return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + message);
    }

    private sealed class StaContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        internal void Run(Task task)
        {
            while (!task.IsCompleted)
                if (queue.TryTake(out var work, 100)) work.Callback(work.State);
            task.GetAwaiter().GetResult();
        }
        internal T RunWithResult<T>(Task<T> task)
        {
            Run(task);
            return task.GetAwaiter().GetResult();
        }
        public void Dispose() => queue.Dispose();
    }
}
