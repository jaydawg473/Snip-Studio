#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NativeOcr = SnipStudio.TextRecognition;
using NativeLine = SnipStudio.TextLineRegion;
using NativeStyle = SnipStudio.SourceTextStyle;
using NativeFamily = SnipStudio.SourceTextFamilyContext;

namespace SnipStudio.WinUI.Backend;

public sealed partial class EditorSession
{
    private bool ocrWorkActive;
    private Task<global::SnipStudio.TextRecognitionResult>? ocrNativePending;

    /// <summary>
    /// Recognizes a detached view of the current image locally. The legacy
    /// recognizer owns its copy, enforces one native recognizer at a time, and
    /// shares a thirty-second budget across initial and dense-row recovery.
    /// </summary>
    public async Task<OcrSnapshot> RecognizeTextAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        RequireImage();
        RequireOcrContext();
        if (ocrWorkActive) throw new InvalidOperationException("Text recognition or source-style matching is already running.");
        if (ocrNativePending is { IsCompleted: false })
            throw new InvalidOperationException("The previous Windows recognizer is still finishing. Your work is kept; try recognition again later.");
        ocrWorkActive = true;
        object identity = document.BaseIdentity;
        long revision = document.Revision;
        try
        {
            var request = new CancellationTokenSource();
            // Forward cancellation asynchronously: a broken native provider's
            // Cancel implementation must not run in the window's button event.
            var forwardCancellation = cancellationToken.Register(static state =>
                CancelOcrRequestAsync((CancellationTokenSource)state!), request);
            request.CancelAfter(TimeSpan.FromSeconds(35));
            Task<global::SnipStudio.TextRecognitionResult> work;
            try
            {
                using Bitmap snapshot = document.RenderImage();
                work = NativeOcr.RecognizeAsync(snapshot, request.Token);
            }
            catch { forwardCancellation.Dispose(); request.Dispose(); throw; }
            ocrNativePending = work;
            _ = ObserveOcrNativeCompletionAsync(work, request, forwardCancellation);
            global::SnipStudio.TextRecognitionResult result;
            try { result = await work.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken); }
            catch (TimeoutException)
            {
                // A native provider may ignore cancellation. Its detached copy
                // remains owned by the recognizer; no overlapping request is
                // started until it actually completes.
                CancelOcrRequestAsync(request);
                throw new TimeoutException("Windows text recognition did not finish within 35 seconds. Your current image is kept.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Windows text recognition reached its time limit. Your current image is kept.");
            }
            VerifyAccess();
            cancellationToken.ThrowIfCancellationRequested();
            // Return the recognized text even if the editor changed. The
            // snapshot identity prevents that result from modifying a new snip.
            return new OcrSnapshot(this, identity, revision, result);
        }
        finally { ocrWorkActive = false; }
    }

    private static void CancelOcrRequestAsync(CancellationTokenSource request)
    {
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { /* The recognizer already completed. */ }
        }, request);
    }

    private async Task ObserveOcrNativeCompletionAsync(Task<global::SnipStudio.TextRecognitionResult> work,
        CancellationTokenSource request, CancellationTokenRegistration forwardCancellation)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { /* Observe a late failure after the bounded UI wait. */ }
        finally
        {
            forwardCancellation.Dispose();
            request.Dispose();
            _ = Interlocked.CompareExchange(ref ocrNativePending, null, work);
        }
    }

    public bool IsOcrSnapshotCurrent(OcrSnapshot snapshot)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(snapshot);
        return OcrSnapshotMatches(snapshot);
    }

    /// <summary>Returns native reading-order text and visual bands; it never changes image pixels.</summary>
    public OcrSelectionSnapshot SelectRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end)
    {
        var selection = SelectOcrGeometry(snapshot, start, end);
        if (!selection.HasSelection)
            return new OcrSelectionSnapshot(snapshot, "", Array.Empty<OcrBounds>(), Array.Empty<int>());
        var bands = selection.SelectedLineBounds().Select(OcrSnapshot.FromRectangle).ToArray();
        var lines = new List<int>();
        var selectedWords = new List<(int LineIndex, int WordIndex)>();
        int flatIndex = 0;
        for (int index = 0; index < snapshot.NativeResult.Lines.Count; index++)
        {
            bool selectedLine = false;
            for (int wordIndex = 0; wordIndex < snapshot.NativeResult.Lines[index].Words.Count; wordIndex++)
            {
                var word = snapshot.NativeResult.Lines[index].Words[wordIndex];
                if (word == null || string.IsNullOrWhiteSpace(word.Text) || !ValidOcrBounds(word.Bounds)) continue;
                bool chosen = selection.IsSelected(flatIndex++);
                selectedLine |= chosen;
                if (chosen) selectedWords.Add((index, wordIndex));
            }
            if (selectedLine) lines.Add(index);
        }
        int lineIndex = selection.SelectedLineIndex;
        if (lineIndex >= 0 &&
            selection.TryGetSelectedCharacterRange(snapshot.Lines[lineIndex].Text, out int rangeStart, out int rangeLength))
            return new OcrSelectionSnapshot(snapshot, selection.SelectedText, bands, lines, selectedWords, rangeStart, rangeLength);
        return new OcrSelectionSnapshot(snapshot, selection.SelectedText, bands, lines, selectedWords);
    }

    /// <summary>Returns only the live highlight geometry while a user drags across recognized words.</summary>
    public IReadOnlyList<OcrBounds> PreviewRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end)
    {
        var selection = SelectOcrGeometry(snapshot, start, end);
        return selection.HasSelection
            ? selection.SelectedLineBounds().Select(OcrSnapshot.FromRectangle).ToArray()
            : Array.Empty<OcrBounds>();
    }

    private global::SnipStudio.TextOverlaySelection SelectOcrGeometry(OcrSnapshot snapshot, ImagePoint start, ImagePoint end)
    {
        VerifyAccess();
        RequireCurrentOcr(snapshot);
        PointF first = ConvertPoint(start, nameof(start));
        PointF last = ConvertPoint(end, nameof(end));
        var selection = snapshot.SelectionGeometry;
        int anchor = selection.HitTest(first, 8);
        if (anchor < 0) selection.Clear();
        else selection.Select(anchor, first == last ? anchor : selection.Nearest(last));
        return selection;
    }

    public bool DeleteRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end) =>
        DeleteRecognizedSelection(snapshot, SelectRecognizedText(snapshot, start, end));

    /// <summary>Erases only selected native word bands; all other source typography remains raster-exact.</summary>
    public bool DeleteRecognizedSelection(OcrSnapshot snapshot, OcrSelectionSnapshot selection)
    {
        VerifyCanMutate();
        ArgumentNullException.ThrowIfNull(selection);
        RequireCurrentOcr(snapshot);
        if (!ReferenceEquals(selection.Source, snapshot))
            throw new InvalidOperationException("The text selection belongs to a different extraction. Select text again before deleting it.");
        if (selection.Bands.Count == 0) return false;
        if (selection.Bands.Count != selection.LineIndices.Count || selection.Bands.Count > 4096)
            throw new InvalidOperationException("The text selection no longer matches its source lines. Nothing was deleted.");
        var masks = new List<Rectangle>(selection.Bands.Count);
        var backgrounds = new List<Color>(selection.Bands.Count);
        for (int at = 0; at < selection.Bands.Count; at++)
        {
            OcrBounds value = selection.Bands[at];
            RectangleF selected = new((float)value.X, (float)value.Y, (float)value.Width, (float)value.Height);
            int sourceIndex = selection.LineIndices[at];
            if (sourceIndex < 0 || sourceIndex >= snapshot.NativeResult.Lines.Count || !ValidOcrBounds(selected))
                throw new InvalidOperationException("The text selection has invalid source geometry. Nothing was deleted.");
            if (FindExistingOcrTextNote(selected) >= 0)
                throw new InvalidOperationException("This selection overlaps entered text. Select the original text note and edit it directly; no source pixels were deleted.");
            Rectangle mask = Rectangle.Intersect(new Rectangle(Point.Empty, document.ImageSize), Rectangle.FromLTRB(
                (int)Math.Floor(selected.Left) - 2, (int)Math.Floor(selected.Top) - 2,
                (int)Math.Ceiling(selected.Right) + 2, (int)Math.Ceiling(selected.Bottom) + 2));
            NativeLine sourceLine = snapshot.NativeResult.Lines[sourceIndex];
            if (!sourceLine.RecognitionClipBounds.IsEmpty)
                mask = Rectangle.Intersect(mask, Rectangle.Ceiling(sourceLine.RecognitionClipBounds));
            for (int lineIndex = 0; lineIndex < snapshot.NativeResult.Lines.Count; lineIndex++)
            {
                NativeLine line = snapshot.NativeResult.Lines[lineIndex];
                mask = ProtectOcrPadding(mask, selected, line.ExcludedGraphicBounds);
                // Native line geometry remains protective evidence even when
                // one of its word boxes is empty, missing or invalid. Missing
                // word boxes never establish blank pixels beside a selection.
                if (lineIndex != sourceIndex && ValidOcrBounds(line.Bounds))
                    mask = ProtectOcrPadding(mask, selected, line.Bounds);
                for (int wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
                {
                    var word = line.Words[wordIndex];
                    if (word == null || !ValidOcrBounds(word.Bounds)) continue;
                    // Use exact native word indices, not floating-point union
                    // containment. Fractional OCR boxes can lose a boundary ULP
                    // when converted to a rectangle's Width and back to Right.
                    if (lineIndex == sourceIndex && selection.IncludesWord(lineIndex, wordIndex)) continue;
                    mask = ProtectOcrPadding(mask, selected, word.Bounds);
                }
            }
            if (mask.Width < 1 || mask.Height < 1)
                throw new InvalidOperationException("This selection overlaps protected graphics or neighboring text. Nothing was deleted.");
            SampleOcrColors(mask, out Color background, out _);
            masks.Add(mask);
            backgrounds.Add(background);
        }
        RequireCurrentOcr(snapshot);
        return document.EraseRecognizedRegions(masks, backgrounds, snapshot.ImageIdentity, snapshot.Revision);
    }

    /// <summary>
    /// Converts verified source regions in one Undo transaction. It prepares
    /// every font, mask and native layout before changing the document; a stale
    /// result, cancellation or unsafe fit leaves all original pixels intact.
    /// </summary>
    public Task<OcrConversionResult> ConvertRecognizedTextAsync(OcrSnapshot snapshot,
        IReadOnlyList<int>? lineIndices = null, CancellationToken cancellationToken = default) =>
        ConvertRecognizedTextCoreAsync(snapshot, lineIndices, null, cancellationToken);

    /// <summary>Estimates an editable line in place without replacing the source pixels or creating an Undo step.</summary>
    public async Task<OcrLineEditSnapshot> PrepareRecognizedLineEditAsync(OcrSnapshot source, int lineIndex,
        CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        RequireCurrentOcr(source);
        RequireOcrContext();
        if (!source.Available)
            throw new InvalidOperationException("Recognize the image before editing a source line.");
        if ((uint)lineIndex >= (uint)source.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));
        if (ocrWorkActive)
            throw new InvalidOperationException("Text recognition or source-style matching is already running.");

        NativeLine line = source.NativeResult.Lines[lineIndex];
        if (string.IsNullOrWhiteSpace(line.Text) || line.Text.Length > MaximumTextCharacters || !ValidOcrBounds(line.Bounds))
            throw new InvalidOperationException("This recognized line cannot be edited in place. You can still copy the extracted text.");
        int existing = FindEditableOcrNote(line);
        if (existing >= 0 && document.GetTextAnnotation(existing) is { } note)
            return new OcrLineEditSnapshot(source, existing, note.Text, note.Rtf, note.FontFamily,
                (TextFontStyle)note.FontStyle, note.Location, note.Color, note.FontSize, note.LayoutWidth, note.MinimumHeight);
        if (source.TextAngle.HasValue && (!double.IsFinite(source.TextAngle.Value) || Math.Abs(source.TextAngle.Value) > 2))
            throw new InvalidOperationException("Angled source text cannot be edited in place. Copy it or rotate the snip and try again.");

        Rectangle mask = BuildOcrMask(source, lineIndex);
        if (mask.Width < 1 || mask.Height < 1)
            throw new InvalidOperationException("This line overlaps protected graphics or neighboring text. The original image was kept.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken token = budget.Token;
        ocrWorkActive = true;
        try
        {
            NativeFamily? family = await EstimateOcrFamilyAsync(source, token);
            var (foreground, background, style, layout) = await MatchOcrLineStyleAsync(source, lineIndex, mask, family, token);
            float width = Math.Min(MaximumImageDimension, layout.Width);
            var fitted = PrepareOcrReplacement(line.Text, line.Bounds, mask, layout, foreground, background, style, false, token);
            float fontSize = fitted?.FontSize ?? Math.Clamp(style.FontSizePixels, 1, 1024);
            string rtf = fitted?.Rtf ??
                global::SnipStudio.RichTextLayout.CreatePlainRtf(line.Text, style.FontFamily, fontSize, style.Style, foreground);
            PointF location;
            if (fitted != null) location = fitted.TextLocation;
            else
            {
                // Even an overlong OCR misread can open so its text can be
                // corrected. Apply still validates the edited ink against the source.
                RectangleF ink = global::SnipStudio.RichTextLayout.Measure(rtf, width).InkBounds;
                if (ink.IsEmpty)
                    throw new InvalidOperationException("This line has no editable text that the renderer can display.");
                location = new PointF(line.Bounds.Left - ink.Left, line.Bounds.Top - ink.Top);
            }
            return new OcrLineEditSnapshot(source, -1, line.Text, rtf, style.FontFamily, (TextFontStyle)style.Style,
                location, foreground, fontSize, width, 0, line.Bounds, mask, layout, background);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Source-style matching reached its time limit. The original line was kept.");
        }
        finally { ocrWorkActive = false; }
    }

    /// <summary>Commits changed text as one Undo step, retaining the original ink position and protected source area.</summary>
    public int ApplyRecognizedLineEdit(OcrLineEditSnapshot draft, string text, string rtf, uint argb, float fontSize)
    {
        VerifyCanMutate();
        ArgumentNullException.ThrowIfNull(draft);
        RequireCurrentOcr(draft.Source);
        ValidateText(text, fontSize, draft.LayoutWidth, draft.MinimumHeight, rtf);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Enter text to keep this edit. To remove source words, cancel and use Delete selection.", nameof(text));
        if (string.Equals(text, draft.Text, StringComparison.Ordinal) && string.Equals(rtf, draft.Rtf, StringComparison.Ordinal) &&
            argb == draft.Argb && fontSize == draft.FontSize)
            return -1;
        if (draft.ExistingAnnotationIndex >= 0)
            return UpdateText(draft.ExistingAnnotationIndex, text, argb, fontSize, draft.LayoutWidth, rtf, draft.MinimumHeight)
                ? draft.ExistingAnnotationIndex : -1;

        ArgumentException.ThrowIfNullOrWhiteSpace(rtf);
        RectangleF ink = global::SnipStudio.RichTextLayout.Measure(rtf, draft.LayoutWidth).InkBounds;
        RectangleF layout = draft.ProtectedLayout;
        RectangleF sourceInk = draft.SourceInk;
        if (ink.IsEmpty || sourceInk.Left < layout.Left || sourceInk.Top < layout.Top ||
            sourceInk.Left + ink.Width > layout.Right || sourceInk.Top + ink.Height > layout.Bottom)
            throw new InvalidOperationException("The edit needs more room than this source line has. Shorten the text or reduce its font size.");
        PointF location = new(sourceInk.Left - ink.Left, sourceInk.Top - ink.Top);
        var replacement = new global::SnipStudio.RecognizedTextReplacement(draft.SourceMask, location, text, rtf,
            FromArgb(argb), FromArgb(draft.BackgroundArgb), fontSize, draft.LayoutWidth, layout, ink);
        int index = document.ReplaceRecognizedTextBlocks(new[] { replacement }, draft.Source.ImageIdentity, draft.Source.Revision);
        if (index < 0)
            throw new InvalidOperationException("The snip changed. Recognize the current image before editing it.");
        return index;
    }

    /// <summary>
    /// Applies only explicitly reviewed, position-matched translation lines.
    /// Unchanged lines retain their exact source pixels. This method does not
    /// run a model or make implicit corrections to identifiers or spelling.
    /// </summary>
    public Task<OcrConversionResult> ApplyReviewedTranslationAsync(OcrSnapshot snapshot,
        IReadOnlyList<int> lineIndices, IReadOnlyList<string> reviewedSegments, CancellationToken cancellationToken = default)
    {
        VerifyCanMutate();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(lineIndices);
        ArgumentNullException.ThrowIfNull(reviewedSegments);
        RequireCurrentOcr(snapshot);
        if (lineIndices.Count == 0 || lineIndices.Count != reviewedSegments.Count)
            throw new ArgumentException("Every reviewed translation needs its original source line.");
        var reviewed = new Dictionary<int, string>();
        for (int index = 0; index < lineIndices.Count; index++)
        {
            string text = reviewedSegments[index] ?? throw new ArgumentException("A reviewed translation line is missing.", nameof(reviewedSegments));
            if (string.IsNullOrWhiteSpace(text) || text.Length > 16000)
                throw new ArgumentException("Each reviewed translation must contain 1 to 16,000 characters.", nameof(reviewedSegments));
            if (!reviewed.TryAdd(lineIndices[index], text))
                throw new ArgumentException("A source line was selected more than once. Nothing was replaced.", nameof(lineIndices));
        }
        return ConvertRecognizedTextCoreAsync(snapshot, lineIndices, reviewed, cancellationToken);
    }

    private async Task<OcrConversionResult> ConvertRecognizedTextCoreAsync(OcrSnapshot snapshot,
        IReadOnlyList<int>? lineIndices, IReadOnlyDictionary<int, string>? reviewedTranslation, CancellationToken cancellationToken)
    {
        VerifyCanMutate();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireCurrentOcr(snapshot);
        RequireOcrContext();
        if (!snapshot.Available) return OcrFailure(snapshot.Message.Length == 0 ? "Windows text recognition is unavailable." : snapshot.Message);
        if (snapshot.TextAngle.HasValue && (!double.IsFinite(snapshot.TextAngle.Value) || Math.Abs(snapshot.TextAngle.Value) > 2))
            return OcrFailure("Angled text cannot be replaced safely. Copy the extracted text or rotate the snip and recognize it again.");
        if (ocrWorkActive) throw new InvalidOperationException("Text recognition or source-style matching is already running.");
        var requested = new SortedSet<int>();
        if (lineIndices == null)
            for (int i = 0; i < snapshot.Lines.Count; i++) requested.Add(i);
        else foreach (int index in lineIndices)
        {
            if (index < 0 || index >= snapshot.Lines.Count)
                throw new ArgumentOutOfRangeException(nameof(lineIndices), "Choose lines from this recognition result.");
            requested.Add(index);
        }
        if (requested.Count == 0) return OcrFailure("No recognized lines were selected.");
        if (requested.Count > 4096 || requested.Sum(index => (long)(reviewedTranslation == null ? snapshot.Lines[index].Text.Length : reviewedTranslation[index].Length)) > 512 * 1024)
            return OcrFailure("This selection is too large to convert in one edit. Select fewer lines; the complete extraction is kept.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken token = budget.Token;
        ocrWorkActive = true;
        try
        {
            var replacements = new List<global::SnipStudio.RecognizedTextReplacement>();
            int keptNotes = 0, keptOriginals = 0, lastExistingNote = -1;
            NativeFamily? family = null;
            bool familyPrepared = false;
            foreach (int index in requested)
            {
                token.ThrowIfCancellationRequested();
                RequireCurrentOcr(snapshot);
                NativeLine line = snapshot.NativeResult.Lines[index];
                if (string.IsNullOrWhiteSpace(line.Text)) continue;
                string replacementText = reviewedTranslation == null ? line.Text : reviewedTranslation[index];
                if (reviewedTranslation != null && string.Equals(replacementText, line.Text, StringComparison.Ordinal))
                {
                    keptOriginals++;
                    continue;
                }
                if (line.Text.Length > 16000)
                    return OcrFailure("One recognized line exceeds the 16,000-character text-note limit. No text was replaced.");
                if (!ValidOcrBounds(line.Bounds)) return OcrFailure("A recognized line has invalid positions. No text was replaced.");
                int existing = FindExistingOcrTextNote(line.Bounds);
                if (existing >= 0)
                {
                    // An OCR guess must never replace or recolor entered work.
                    // Edit the existing annotation directly instead.
                    keptNotes++;
                    lastExistingNote = existing;
                    continue;
                }
                if (!familyPrepared)
                {
                    family = await EstimateOcrFamilyAsync(snapshot, token);
                    familyPrepared = true;
                    token.ThrowIfCancellationRequested();
                    RequireCurrentOcr(snapshot);
                }
                Rectangle mask = BuildOcrMask(snapshot, index);
                if (mask.Width < 1 || mask.Height < 1)
                    return OcrFailure("A source text region overlaps protected graphics or neighboring text. No text was replaced.");
                var (foreground, background, style, layout) = await MatchOcrLineStyleAsync(snapshot, index, mask, family, token);
                var replacement = PrepareOcrReplacement(replacementText, line.Bounds, mask, layout, foreground, background,
                    style, reviewedTranslation != null, token);
                if (replacement == null)
                    return OcrFailure("Recognized line " + (index + 1) + " does not fit its protected source region. No text was replaced; copy or edit the extraction separately.");
                replacements.Add(replacement);
            }
            token.ThrowIfCancellationRequested();
            RequireCurrentOcr(snapshot);
            if (replacements.Count == 0)
                return keptNotes > 0
                    ? new OcrConversionResult(true, lastExistingNote, 0, "Existing editable text was kept. Select the text note to edit it directly.")
                    : keptOriginals > 0
                        ? new OcrConversionResult(true, -1, 0, "The reviewed translation is unchanged. Original screenshot pixels were kept.")
                        : OcrFailure("No source text was available to convert.");
            int lastIndex = document.ReplaceRecognizedTextBlocks(replacements, snapshot.ImageIdentity, snapshot.Revision);
            if (lastIndex < 0) return OcrFailure("The snip changed. No text was replaced; recognize the current image again.");
            return new OcrConversionResult(true, lastIndex, replacements.Count,
                (reviewedTranslation == null ? "Converted " : "Applied reviewed translation to ") + replacements.Count +
                " line" + (replacements.Count == 1 ? "" : "s") +
                (reviewedTranslation == null
                    ? " to editable text. Recognized strings were preserved verbatim; compare them with the image. Fonts are estimated. Undo restores the original pixels."
                    : ". Font appearance is estimated; unchanged lines retain their original pixels. Undo restores the original.") +
                (keptNotes == 0 ? "" : " Existing text notes were kept."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OcrFailure("Source-style matching reached its time limit. No text was replaced; select fewer lines and try again.");
        }
        finally { ocrWorkActive = false; }
    }

    private async Task<(Color Foreground, Color Background, NativeStyle Style, RectangleF Layout)> MatchOcrLineStyleAsync(
        OcrSnapshot snapshot, int index, Rectangle mask, NativeFamily? family, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireCurrentOcr(snapshot);
        NativeLine line = snapshot.NativeResult.Lines[index];
        SampleOcrColors(mask, out Color background, out Color foreground);
        Rectangle bounds = Rectangle.Intersect(new Rectangle(Point.Empty, document.ImageSize), Rectangle.Ceiling(line.Bounds));
        using Bitmap source = document.CopyBaseRegion(bounds);
        Rectangle local = new(Point.Empty, source.Size);
        NativeStyle style = await Task.Run(() => global::SnipStudio.SourceTextMatcher.EstimateWithFamilyContext(
            source, local, line.Text, foreground, background, token, family), CancellationToken.None);
        token.ThrowIfCancellationRequested();
        RequireCurrentOcr(snapshot);
        // RichEdit refinement stays on the editor's STA after the background match.
        style = global::SnipStudio.SourceTextMatcher.RefineSizeOnUi(source, local, line.Text, foreground, background, style, token);
        RectangleF layout = VerifiedOcrLayout(snapshot, index, mask, background, token);
        return (foreground, background, style, layout);
    }

    /// <summary>Highlights native word bands without changing their reading order or replacing any text.</summary>
    public bool HighlightRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end, uint argb)
    {
        VerifyCanMutate();
        RequireCurrentOcr(snapshot);
        var bands = global::SnipStudio.TextSnapSelection.BuildBands(snapshot.NativeResult.Lines,
            ConvertPoint(start, nameof(start)), ConvertPoint(end, nameof(end)), 8);
        if (bands.Count == 0) return false;
        document.AddHighlightBands(bands, Color.FromArgb(unchecked((int)argb)));
        return true;
    }

    private bool OcrSnapshotMatches(OcrSnapshot snapshot) => !disposed && ReferenceEquals(snapshot.Owner, this) &&
        document.HasImage && snapshot.Revision == document.Revision && ReferenceEquals(snapshot.ImageIdentity, document.BaseIdentity);

    private void RequireCurrentOcr(OcrSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!OcrSnapshotMatches(snapshot))
            throw new InvalidOperationException("The snip changed after recognition. The extraction is kept; recognize the current image before selecting or replacing its text.");
    }

    private static void RequireOcrContext()
    {
        if (SynchronizationContext.Current == null)
            throw new InvalidOperationException("Start text recognition from the editor's UI context.");
    }

    private static OcrConversionResult OcrFailure(string message) => new(false, -1, 0, message);

    private int FindExistingOcrTextNote(RectangleF bounds)
    {
        for (int index = document.AnnotationCount - 1; index >= 0; index--)
        {
            var note = document.GetTextAnnotation(index);
            if (note != null && note.InkBounds.IntersectsWith(bounds)) return index;
        }
        return -1;
    }

    private int FindEditableOcrNote(NativeLine line)
    {
        bool overlapsNote = false;
        string recognized = string.Concat(line.Text.Where(c => !char.IsWhiteSpace(c)));
        for (int index = document.AnnotationCount - 1; index >= 0; index--)
        {
            var note = document.GetTextAnnotation(index);
            if (note == null) continue;
            RectangleF overlap = RectangleF.Intersect(line.Bounds, note.InkBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) continue;
            overlapsNote = true;
            // The legacy editor only opens an existing note when its text and
            // geometry identify that note as the source of this OCR line.
            if (overlap.Width * overlap.Height >= line.Bounds.Width * line.Bounds.Height * .7f &&
                string.Concat(note.Text.Where(c => !char.IsWhiteSpace(c))).Contains(recognized, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        if (overlapsNote)
            throw new InvalidOperationException("This line overlaps an editable note. Use the Text tool to edit that note directly.");
        return -1;
    }

    private Rectangle BuildOcrMask(OcrSnapshot snapshot, int sourceIndex)
    {
        NativeLine line = snapshot.NativeResult.Lines[sourceIndex];
        RectangleF text = line.Bounds;
        Rectangle mask = Rectangle.Intersect(new Rectangle(Point.Empty, document.ImageSize), Rectangle.FromLTRB(
            (int)Math.Floor(text.Left) - 2, (int)Math.Floor(text.Top) - 2,
            (int)Math.Ceiling(text.Right) + 2, (int)Math.Ceiling(text.Bottom) + 2));
        mask = ProtectOcrPadding(mask, text, line.ExcludedGraphicBounds);
        if (!line.RecognitionClipBounds.IsEmpty)
            mask = Rectangle.Intersect(mask, Rectangle.Ceiling(line.RecognitionClipBounds));
        for (int index = 0; index < snapshot.NativeResult.Lines.Count; index++)
        {
            NativeLine other = snapshot.NativeResult.Lines[index];
            if (index != sourceIndex && ValidOcrBounds(other.Bounds))
                mask = ProtectOcrPadding(mask, text, other.Bounds);
            if (!other.ExcludedGraphicBounds.IsEmpty)
                mask = ProtectOcrPadding(mask, text, other.ExcludedGraphicBounds);
        }
        return mask;
    }

    private static Rectangle ProtectOcrPadding(Rectangle mask, RectangleF text, RectangleF obstacle)
    {
        if (obstacle.IsEmpty || !mask.IntersectsWith(Rectangle.Ceiling(obstacle))) return mask;
        if (obstacle.Right <= text.Left) return Rectangle.FromLTRB(Math.Max(mask.Left, (int)Math.Ceiling(obstacle.Right)), mask.Top, mask.Right, mask.Bottom);
        if (obstacle.Left >= text.Right) return Rectangle.FromLTRB(mask.Left, mask.Top, Math.Min(mask.Right, (int)Math.Floor(obstacle.Left)), mask.Bottom);
        if (obstacle.Bottom <= text.Top) return Rectangle.FromLTRB(mask.Left, Math.Max(mask.Top, (int)Math.Ceiling(obstacle.Bottom)), mask.Right, mask.Bottom);
        if (obstacle.Top >= text.Bottom) return Rectangle.FromLTRB(mask.Left, mask.Top, mask.Right, Math.Min(mask.Bottom, (int)Math.Floor(obstacle.Top)));
        // Ambiguous overlap must not be erased or treated as proof of text.
        return Rectangle.Empty;
    }

    private void SampleOcrColors(Rectangle bounds, out Color background, out Color foreground, bool requireOpaque = true)
    {
        Rectangle region = bounds;
        region.Inflate(2, 2);
        region.Intersect(new Rectangle(Point.Empty, document.ImageSize));
        using Bitmap image = document.CopyBaseRegion(region);
        Rectangle local = bounds;
        local.Offset(-region.Left, -region.Top);
        var edges = new Dictionary<int, int>();
        int stepX = Math.Max(1, local.Width / 100), stepY = Math.Max(1, local.Height / 30);
        for (int x = local.Left; x < local.Right; x += stepX)
        {
            CountOcrColor(edges, image.GetPixel(x, Math.Max(0, local.Top - 1)));
            CountOcrColor(edges, image.GetPixel(x, Math.Min(image.Height - 1, local.Bottom)));
        }
        for (int y = local.Top; y < local.Bottom; y += stepY)
        {
            CountOcrColor(edges, image.GetPixel(Math.Max(0, local.Left - 1), y));
            CountOcrColor(edges, image.GetPixel(Math.Min(image.Width - 1, local.Right), y));
        }
        background = MostCommonOcrColor(edges, Color.White);
        using Bitmap colors = document.CopyBaseRegion(bounds);
        if (requireOpaque) RequireOpaqueOcrMask(colors);
        foreground = global::SnipStudio.SourceTextColor.Estimate(colors, background,
            background.GetBrightness() > .5f ? Color.Black : Color.White);
    }

    private static void RequireOpaqueOcrMask(Bitmap source)
    {
        // The original mask renderer fills opaque flat color. Refuse a
        // transparent region rather than destroying the source alpha channel.
        BitmapData data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[checked(source.Width * 4)];
            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), row, 0, row.Length);
                for (int x = 3; x < row.Length; x += 4)
                    if (row[x] != 255) throw new InvalidOperationException("This source text region is transparent. Copy the extracted text instead; the original transparency has been kept.");
            }
        }
        finally { source.UnlockBits(data); }
    }

    private static void CountOcrColor(Dictionary<int, int> colors, Color value)
    {
        int key = Color.FromArgb(255, value).ToArgb();
        colors.TryGetValue(key, out int count);
        colors[key] = count + 1;
    }

    private static Color MostCommonOcrColor(Dictionary<int, int> colors, Color fallback)
    {
        int count = 0;
        foreach (var entry in colors)
            if (entry.Value > count) { count = entry.Value; fallback = Color.FromArgb(entry.Key); }
        return fallback;
    }

    private async Task<NativeFamily?> EstimateOcrFamilyAsync(OcrSnapshot snapshot, CancellationToken token)
    {
        var eligible = snapshot.NativeResult.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Text) &&
            line.Text.Length <= 160 && line.Text.Count(char.IsLetterOrDigit) >= 6 && line.Bounds.Width >= 12 &&
            line.Bounds.Height >= 5 && line.Bounds.Width <= 2048 && line.Bounds.Height <= 128 &&
            new RectangleF(PointF.Empty, document.ImageSize).Contains(line.Bounds) && FindExistingOcrTextNote(line.Bounds) < 0).ToList();
        if (eligible.Count == 0) return null;
        var chosen = new List<NativeLine> { eligible.OrderByDescending(line => line.Text.Length).First() };
        NativeLine small = eligible.OrderBy(line => line.Bounds.Height).ThenByDescending(line => line.Text.Length).First();
        NativeLine large = eligible.OrderByDescending(line => line.Bounds.Height).ThenByDescending(line => line.Text.Length).First();
        if (!chosen.Contains(small)) chosen.Add(small);
        if (!chosen.Contains(large)) chosen.Add(large);
        foreach (NativeLine line in eligible.OrderByDescending(line => line.Text.Length))
        {
            if (chosen.Count >= 3) break;
            if (!chosen.Contains(line)) chosen.Add(line);
        }
        Rectangle union = Rectangle.Empty;
        foreach (NativeLine line in chosen)
            union = union.IsEmpty ? Rectangle.Ceiling(line.Bounds) : Rectangle.Union(union, Rectangle.Ceiling(line.Bounds));
        if (union.IsEmpty || (long)union.Width * union.Height > 16L * 1024 * 1024) return null;
        var samples = new List<global::SnipStudio.SourceTextFamilySample>();
        foreach (NativeLine line in chosen)
        {
            token.ThrowIfCancellationRequested();
            Rectangle bounds = Rectangle.Ceiling(line.Bounds);
            SampleOcrColors(bounds, out Color background, out Color foreground, requireOpaque: false);
            bounds.Offset(-union.Left, -union.Top);
            samples.Add(new global::SnipStudio.SourceTextFamilySample(bounds, line.Text, foreground, background));
        }
        using Bitmap source = document.CopyBaseRegion(union);
        NativeFamily context = await Task.Run(() => global::SnipStudio.SourceTextMatcher.EstimateFamilyContext(source, samples, token), CancellationToken.None);
        token.ThrowIfCancellationRequested();
        RequireCurrentOcr(snapshot);
        return context;
    }

    private RectangleF VerifiedOcrLayout(OcrSnapshot snapshot, int sourceIndex, Rectangle mask, Color background, CancellationToken token)
    {
        // Extra horizontal room is allowed only where the original pixels are
        // exactly the sampled background. This prevents a long filename from
        // painting over the neighboring date/type column or an annotation.
        int right = Math.Min(document.ImageSize.Width, mask.Right + (2 * 1024 * 1024) / mask.Height);
        NativeLine line = snapshot.NativeResult.Lines[sourceIndex];
        foreach (NativeLine other in snapshot.NativeResult.Lines)
        {
            if (ReferenceEquals(other, line)) continue;
            right = LimitOcrRight(other.Bounds, mask, right);
            right = LimitOcrRight(other.ExcludedGraphicBounds, mask, right);
        }
        for (int index = 0; index < document.AnnotationCount; index++)
            right = LimitOcrRight(document.GetAnnotationBounds(index), mask, right);
        if (!line.RecognitionClipBounds.IsEmpty) right = Math.Min(right, (int)Math.Floor(line.RecognitionClipBounds.Right));
        if (right <= mask.Right) return mask;
        Rectangle scan = Rectangle.FromLTRB(mask.Right, mask.Top, right, mask.Bottom);
        using Bitmap pixels = document.CopyBaseRegion(scan);
        BitmapData data = pixels.LockBits(new Rectangle(Point.Empty, pixels.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[][] rows = new byte[pixels.Height][];
            for (int y = 0; y < rows.Length; y++)
            {
                token.ThrowIfCancellationRequested();
                rows[y] = new byte[checked(pixels.Width * 4)];
                Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), rows[y], 0, rows[y].Length);
            }
            for (int x = 0; x < pixels.Width; x++)
            {
                if ((x & 31) == 0) token.ThrowIfCancellationRequested();
                int at = x * 4;
                foreach (byte[] row in rows)
                    if (row[at] != background.B || row[at + 1] != background.G || row[at + 2] != background.R || row[at + 3] != 255)
                        return RectangleF.FromLTRB(mask.Left, mask.Top, Math.Max(mask.Right, mask.Right + x - 1), mask.Bottom);
            }
        }
        finally { pixels.UnlockBits(data); }
        return RectangleF.FromLTRB(mask.Left, mask.Top, Math.Max(mask.Right, right < document.ImageSize.Width ? right - 1 : right), mask.Bottom);
    }

    private static int LimitOcrRight(RectangleF obstacle, Rectangle mask, int right)
    {
        if (obstacle.IsEmpty || obstacle.Bottom <= mask.Top || obstacle.Top >= mask.Bottom || obstacle.Right <= mask.Right || obstacle.Left >= right) return right;
        return Math.Min(right, Math.Max(mask.Right, (int)Math.Floor(obstacle.Left) - 2));
    }

    private static global::SnipStudio.RecognizedTextReplacement? PrepareOcrReplacement(string text, RectangleF sourceInk, Rectangle mask,
        RectangleF layout, Color foreground, Color background, NativeStyle style, bool allowTranslationFit, CancellationToken token)
    {
        // Start at the measured source size. A small, bounded fit adjustment
        // handles renderer differences; it never changes the recognized string.
        float measuredSize = Math.Clamp(style.FontSizePixels, 1, 1024);
        if (allowTranslationFit)
        {
            var initial = TryPrepareOcrReplacement(text, sourceInk, mask, layout, foreground, background, style, measuredSize);
            if (initial != null) return initial;
            float lower = Math.Min(6, measuredSize), upper = measuredSize;
            var fitted = TryPrepareOcrReplacement(text, sourceInk, mask, layout, foreground, background, style, lower);
            if (fitted == null) return null;
            for (int attempt = 0; attempt < 8 && upper - lower >= .5f; attempt++)
            {
                token.ThrowIfCancellationRequested();
                float candidate = (float)Math.Floor((lower + upper) * 2) / 4;
                var result = TryPrepareOcrReplacement(text, sourceInk, mask, layout, foreground, background, style, candidate);
                if (result != null) { fitted = result; lower = candidate; }
                else upper = candidate;
            }
            return fitted;
        }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            token.ThrowIfCancellationRequested();
            float size = Math.Max(1, measuredSize * (1 - attempt * .025f));
            var result = TryPrepareOcrReplacement(text, sourceInk, mask, layout, foreground, background, style, size);
            if (result != null) return result;
        }
        return null;
    }

    private static global::SnipStudio.RecognizedTextReplacement? TryPrepareOcrReplacement(string text,
        RectangleF sourceInk, Rectangle mask, RectangleF layout, Color foreground, Color background, NativeStyle style, float size)
    {
        try
        {
            string rtf = global::SnipStudio.RichTextLayout.CreatePlainRtf(text, style.FontFamily, size, style.Style, foreground);
            var metrics = global::SnipStudio.RichTextLayout.Measure(rtf, Math.Min(32768, layout.Width));
            RectangleF ink = metrics.InkBounds;
            if (ink.IsEmpty || sourceInk.Left < layout.Left || sourceInk.Top < layout.Top ||
                sourceInk.Left + ink.Width > layout.Right || sourceInk.Top + ink.Height > layout.Bottom) return null;
            // Match the OCR ink origin. Padded masks give native ascenders and
            // antialiasing room without shifting the editable text to mask.Left.
            // Source-space coordinates survive clipping and icon protection;
            // padding is not a new text origin.
            PointF location = new(sourceInk.Left - ink.Left, sourceInk.Top - ink.Top);
            return new global::SnipStudio.RecognizedTextReplacement(mask, location, text, rtf,
                foreground, background, size, Math.Min(32768, layout.Width), layout, ink);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool ValidOcrBounds(RectangleF bounds) => float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom) && bounds.Width > 0 && bounds.Height > 0;
}
