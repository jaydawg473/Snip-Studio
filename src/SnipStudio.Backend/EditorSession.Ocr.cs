#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    /// recognizer owns its copy. Small English images can also receive an
    /// independent, bounded local read before the snapshot is returned.
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
                work = OfflineOcrEngine.RecognizeAsync(snapshot, request.Token);
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
            return new OcrSelectionSnapshot(snapshot, "", Array.Empty<OcrBounds>(), Array.Empty<OcrBounds>(), Array.Empty<int>());
        var bands = selection.SelectedLineBounds().Select(OcrSnapshot.FromRectangle).ToArray();
        var wordBands = new List<OcrBounds>();
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
                if (chosen)
                {
                    selectedWords.Add((index, wordIndex));
                    wordBands.Add(OcrSnapshot.FromRectangle(word.Bounds));
                }
            }
            if (selectedLine) lines.Add(index);
        }
        int lineIndex = selection.SelectedLineIndex;
        if (lineIndex >= 0 &&
            selection.TryGetSelectedCharacterRange(snapshot.Lines[lineIndex].Text, out int rangeStart, out int rangeLength))
            return new OcrSelectionSnapshot(snapshot, selection.SelectedText, bands, wordBands, lines, selectedWords, rangeStart, rangeLength);
        return new OcrSelectionSnapshot(snapshot, selection.SelectedText, bands, wordBands, lines, selectedWords);
    }

    /// <summary>Returns only the live highlight geometry while a user drags across recognized words.</summary>
    public IReadOnlyList<OcrBounds> PreviewRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end)
    {
        var selection = SelectOcrGeometry(snapshot, start, end);
        return selection.HasSelection
            ? selection.SelectedLineBounds().Select(OcrSnapshot.FromRectangle).ToArray()
            : Array.Empty<OcrBounds>();
    }

    /// <summary>Previews each selected word without highlighting the gaps between words.</summary>
    public IReadOnlyList<OcrBounds> PreviewRecognizedWordSelection(OcrSnapshot snapshot, ImagePoint start, ImagePoint end)
    {
        var selection = SelectOcrGeometry(snapshot, start, end);
        return selection.HasSelection
            ? selection.SelectedWordBounds().Select(OcrSnapshot.FromRectangle).ToArray()
            : Array.Empty<OcrBounds>();
    }

    private global::SnipStudio.TextOverlaySelection SelectOcrGeometry(OcrSnapshot snapshot, ImagePoint start, ImagePoint end)
    {
        VerifyAccess();
        RequireCurrentOcr(snapshot);
        PointF first = ConvertPoint(start, nameof(start));
        PointF last = ConvertPoint(end, nameof(end));
        var selection = snapshot.SelectionGeometry;
        bool click = first == last;
        bool startsOnGraphic = snapshot.NativeResult.ProtectedGraphics.Any(graphic => graphic.Contains(first));
        int anchor = startsOnGraphic ? -1 : click ? selection.HitTest(first, 8) : selection.HitTestNear(first, 2.5f);
        // Dragging from a file icon is useful for selecting its neighboring
        // row. It never treats the icon itself as editable text on a click.
        if (!click && anchor < 0) selection.SelectArea(first, last);
        else selection.Select(anchor, click ? anchor : selection.Nearest(last));
        return selection;
    }

    public bool DeleteRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end) =>
        DeleteRecognizedSelection(snapshot, SelectRecognizedText(snapshot, start, end));

    /// <summary>Erases the selected source words as a single undoable change.</summary>
    public bool DeleteRecognizedSelection(OcrSnapshot snapshot, OcrSelectionSnapshot selection)
    {
        VerifyCanMutate();
        ArgumentNullException.ThrowIfNull(selection);
        RequireCurrentOcr(snapshot);
        if (!ReferenceEquals(selection.Source, snapshot))
            throw new InvalidOperationException("The text selection belongs to a different extraction. Select text again before deleting it.");
        if (selection.Bands.Count == 0) return false;
        // A tilted word's axis-aligned bounding box can include unrelated
        // pixels outside the actual word, even when no other word overlaps.
        if (HasAngledOcrGeometry(snapshot))
            throw new InvalidOperationException("Rotate the snip before deleting recognized text. The source pixels were kept.");
        if (selection.Bands.Count != selection.LineIndices.Count || selection.Bands.Count > 4096)
            throw new InvalidOperationException("The text selection no longer matches its source lines. Nothing was deleted.");
        var runs = new List<(int LineIndex, RectangleF Ink)>(selection.Bands.Count);
        for (int at = 0; at < selection.Bands.Count; at++)
        {
            int sourceIndex = selection.LineIndices[at];
            if (sourceIndex < 0 || sourceIndex >= snapshot.NativeResult.Lines.Count)
                throw new InvalidOperationException("The text selection has invalid source geometry. Nothing was deleted.");
            var words = snapshot.NativeResult.Lines[sourceIndex].Words;
            RectangleF run = RectangleF.Empty;
            int initialCount = runs.Count;
            for (int wordIndex = 0; wordIndex < words.Count; wordIndex++)
            {
                var word = words[wordIndex];
                if (string.IsNullOrWhiteSpace(word.Text) || !ValidOcrBounds(word.Bounds)) continue;
                if (!selection.IncludesWord(sourceIndex, wordIndex))
                {
                    FinishRun(sourceIndex, ref run);
                    continue;
                }
                RectangleF bounds = word.Bounds;
                // Preserve space that the two word masks (two pixels of padding
                // each) would leave untouched. It may contain unrecognized art.
                const float combinedPadding = 4;
                if (!run.IsEmpty && (bounds.Left > run.Right + combinedPadding || run.Left > bounds.Right + combinedPadding ||
                    bounds.Top > run.Bottom + combinedPadding || run.Top > bounds.Bottom + combinedPadding))
                    FinishRun(sourceIndex, ref run);
                run = run.IsEmpty ? bounds : RectangleF.Union(run, bounds);
            }
            FinishRun(sourceIndex, ref run);
            if (runs.Count == initialCount)
                throw new InvalidOperationException("The text selection has invalid source geometry. Nothing was deleted.");
        }

        var masks = new List<Rectangle>(runs.Count);
        var backgrounds = new List<Color>(runs.Count);
        foreach ((int lineIndex, RectangleF ink) in runs)
        {
            if (FindExistingOcrTextNote(ink) >= 0)
                throw new InvalidOperationException("This selection overlaps entered text. Select the original text note and edit it directly; no source pixels were deleted.");
            Rectangle mask = BuildSelectedOcrMask(snapshot, selection, lineIndex, ink);
            if (mask.Width < 1 || mask.Height < 1)
                throw new InvalidOperationException("This selection overlaps protected graphics or neighboring text. Nothing was deleted.");
            SampleOcrColors(mask, out Color background, out _);
            masks.Add(mask);
            backgrounds.Add(background);
        }
        RequireCurrentOcr(snapshot);
        return document.EraseRecognizedRegions(masks, backgrounds, snapshot.ImageIdentity, snapshot.Revision);

        void FinishRun(int sourceIndex, ref RectangleF ink)
        {
            if (ink.IsEmpty) return;
            if (runs.Count == 4096)
                throw new InvalidOperationException("Select a smaller area to delete recognized text. Nothing was deleted.");
            runs.Add((sourceIndex, ink));
            ink = RectangleF.Empty;
        }
    }

    /// <summary>
    /// Converts verified source regions in one Undo transaction. It prepares
    /// every font, mask and native layout before changing the document; a stale
    /// result, cancellation or unsafe fit leaves all original pixels intact.
    /// </summary>
    public Task<OcrConversionResult> ConvertRecognizedTextAsync(OcrSnapshot snapshot,
        IReadOnlyList<int>? lineIndices = null, CancellationToken cancellationToken = default) =>
        ConvertRecognizedTextCoreAsync(snapshot, lineIndices, null, cancellationToken);

    /// <summary>Prepares a draft without changing pixels or Undo history. An ambiguous note overlap returns null.</summary>
    public async Task<OcrLineEditSnapshot?> PrepareRecognizedLineEditAsync(OcrSnapshot source, int lineIndex,
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
        if (!TryFindEditableOcrNote(line, out int existing)) return null;
        if (existing >= 0 && document.GetTextAnnotation(existing) is { } note)
            return new OcrLineEditSnapshot(source, existing, note.Text, note.Rtf, note.FontFamily,
                (TextFontStyle)note.FontStyle, note.Location, note.Color, note.FontSize, note.LayoutWidth, note.MinimumHeight);
        if (HasAngledOcrGeometry(source))
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
            PointF location, anchor;
            if (fitted != null)
            {
                location = fitted.TextLocation;
                anchor = new PointF(location.X + fitted.MeasuredInkBounds.Left, location.Y + fitted.MeasuredInkBounds.Top);
            }
            else
            {
                // Even an overlong OCR misread can open so its text can be
                // corrected. Apply still validates the edited ink against the source.
                RectangleF ink = global::SnipStudio.RichTextLayout.Measure(rtf, width).InkBounds;
                if (ink.IsEmpty)
                    throw new InvalidOperationException("This line has no editable text that the renderer can display.");
                anchor = new PointF(RoundSourcePixel(line.Bounds.Left), RoundSourcePixel(line.Bounds.Top));
                location = new PointF(anchor.X - ink.Left, anchor.Y - ink.Top);
            }
            return new OcrLineEditSnapshot(source, -1, line.Text, rtf, style.FontFamily, (TextFontStyle)style.Style,
                location, foreground, fontSize, width, 0, new RectangleF(anchor, line.Bounds.Size), mask, layout, background);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Source-style matching reached its time limit. The original line was kept.");
        }
        finally { ocrWorkActive = false; }
    }

    /// <summary>Commits changed text as one Undo step, retaining the original ink position and protected source area.</summary>
    public int ApplyRecognizedLineEdit(OcrLineEditSnapshot draft, string text, string rtf, uint argb, float fontSize) =>
        ApplyRecognizedLineEdit(draft, text, rtf, argb, fontSize, null, null);

    /// <summary>Commits a source draft with an optional protected width and replacement fill.</summary>
    public int ApplyRecognizedLineEdit(OcrLineEditSnapshot draft, string text, string rtf, uint argb,
        float fontSize, float? layoutWidth, uint? replacementBackground)
    {
        VerifyCanMutate();
        ArgumentNullException.ThrowIfNull(draft);
        RequireCurrentOcr(draft.Source);
        float width = layoutWidth ?? draft.LayoutWidth;
        uint background = replacementBackground ?? draft.BackgroundArgb;
        ValidateText(text, fontSize, width, draft.MinimumHeight, rtf);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Enter text to keep this edit. To remove source words, cancel and use Delete selection.", nameof(text));
        if (replacementBackground.HasValue && (draft.ExistingAnnotationIndex >= 0 || (background >> 24) != 255))
            throw new ArgumentException("Choose an opaque replacement fill for a source image line.", nameof(replacementBackground));
        if (draft.ExistingAnnotationIndex < 0 && (width <= 0 || width > draft.LayoutWidth))
            throw new ArgumentOutOfRangeException(nameof(layoutWidth), "Keep the text box within the protected source line.");
        if (string.Equals(text, draft.Text, StringComparison.Ordinal) && string.Equals(rtf, draft.Rtf, StringComparison.Ordinal) &&
            argb == draft.Argb && fontSize == draft.FontSize && width == draft.LayoutWidth && background == draft.BackgroundArgb)
            return -1;
        if (draft.ExistingAnnotationIndex >= 0)
            return UpdateText(draft.ExistingAnnotationIndex, text, argb, fontSize, width, rtf, draft.MinimumHeight)
                ? draft.ExistingAnnotationIndex : -1;

        ArgumentException.ThrowIfNullOrWhiteSpace(rtf);
        RectangleF ink = global::SnipStudio.RichTextLayout.Measure(rtf, width).InkBounds;
        RectangleF layout = draft.ProtectedLayout;
        if (!TryAlignOcrInk(draft.SourceInk.Location, layout, ink, out PointF location))
            throw new InvalidOperationException("The edit needs more room than this source line has. Shorten the text or reduce its font size.");
        var replacement = new global::SnipStudio.RecognizedTextReplacement(draft.SourceMask, location, text, rtf,
            FromArgb(argb), FromArgb(background), fontSize, width, layout, ink);
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
            int sourceIndex = lineIndices[index];
            if ((uint)sourceIndex >= (uint)snapshot.Lines.Count)
                throw new ArgumentOutOfRangeException(nameof(lineIndices), "Choose lines from this recognition result.");
            string text = reviewedSegments[index];
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextCharacters)
                return Task.FromResult(ReviewedLineFailure(sourceIndex, "Enter between 1 and 16,000 characters or leave the line unchanged."));
            if (!reviewed.TryAdd(sourceIndex, text))
                throw new ArgumentException("A source line was selected more than once. Nothing was replaced.", nameof(lineIndices));
        }
        return ConvertRecognizedTextCoreAsync(snapshot, lineIndices, reviewed, cancellationToken);
    }

    private async Task<OcrConversionResult> ConvertRecognizedTextCoreAsync(OcrSnapshot snapshot,
        IReadOnlyList<int>? lineIndices, IReadOnlyDictionary<int, string>? reviewedTranslation, CancellationToken cancellationToken,
        OcrTextReview? review = null, bool isTranslation = true)
    {
        VerifyCanMutate();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireCurrentOcr(snapshot);
        RequireOcrContext();
        if (!snapshot.Available) return OcrFailure(snapshot.Message.Length == 0 ? "Windows text recognition is unavailable." : snapshot.Message);
        bool angled = HasAngledOcrGeometry(snapshot);
        if (reviewedTranslation == null && angled)
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
        if (requested.Count > 4096 || requested.Sum(index => (long)(reviewedTranslation?[index].Length ??
            review?.FindLine(index).Text.Length ?? snapshot.Lines[index].Text.Length)) > 512 * 1024)
            return OcrFailure("This selection is too large to convert in one edit. Select fewer lines; the complete extraction is kept.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken token = budget.Token;
        ocrWorkActive = true;
        try
        {
            var replacements = new List<global::SnipStudio.RecognizedTextReplacement>();
            var noteEdits = new Dictionary<int, global::SnipStudio.EditorDocument.TextAnnotation>();
            int keptNotes = 0, lastExistingNote = -1;
            NativeFamily? family = null;
            bool familyPrepared = false;
            foreach (int index in requested)
            {
                token.ThrowIfCancellationRequested();
                RequireCurrentOcr(snapshot);
                NativeLine line = snapshot.NativeResult.Lines[index];
                OcrReviewLine? segment = review?.FindLine(index);
                bool partialLine = segment is { IsWholeLine: false };
                string originalText = segment?.Text ?? line.Text;
                RectangleF inkBounds = partialLine ? segment!.SourceInk : line.Bounds;
                if (string.IsNullOrWhiteSpace(originalText))
                {
                    if (reviewedTranslation != null)
                        return ReviewedLineFailure(index, "This line has no original recognized text to replace.");
                    continue;
                }
                string replacementText = reviewedTranslation == null ? originalText : reviewedTranslation[index];
                if (reviewedTranslation != null && string.Equals(replacementText, originalText, StringComparison.Ordinal))
                    continue;
                if (originalText.Length > MaximumTextCharacters)
                    return reviewedTranslation == null
                        ? OcrFailure("One recognized line exceeds the 16,000-character text-note limit. No text was replaced.")
                        : ReviewedLineFailure(index, "The original text exceeds the text-note limit. Leave this line unchanged.");
                if (!ValidOcrBounds(inkBounds))
                    return reviewedTranslation == null ? OcrFailure("A recognized line has invalid positions. No text was replaced.")
                        : ReviewedLineFailure(index, "The original text position is invalid. Recognize the snip again or leave this line unchanged.");
                int existing = FindExistingOcrTextNote(inkBounds);
                if (existing >= 0)
                {
                    if (reviewedTranslation == null)
                    {
                        // A plain OCR conversion must leave editable notes alone.
                        keptNotes++;
                        lastExistingNote = existing;
                        continue;
                    }

                    if (partialLine)
                        return ReviewedLineFailure(index, "The selected words are part of an editable note. Edit the note directly or leave this row unchanged.");
                    var note = document.GetTextAnnotation(existing)!;
                    if (!IsWholeReviewedNote(snapshot, index, existing, note) || noteEdits.ContainsKey(existing))
                        return ReviewedLineFailure(index, "This row does not identify one complete editable note. Edit the note directly or leave this row unchanged.");
                    var edit = document.PrepareReviewedTextNote(existing, replacementText, out string failure);
                    if (edit == null)
                        return ReviewedLineFailure(index, failure);
                    if (ReviewedNoteCoversNeighbor(snapshot, index, existing, note.InkBounds, edit.InkBounds))
                        return ReviewedLineFailure(index, "The reviewed text would cover nearby content. Shorten it or leave this line unchanged.");
                    noteEdits.Add(existing, edit);
                    continue;
                }
                if (angled)
                    return ReviewedLineFailure(index, "Angled image text cannot be replaced safely. Leave this row unchanged or rotate and recognize the snip again.");
                Rectangle mask = partialLine
                    ? BuildSelectedOcrMask(snapshot, review!.Selection!, index, inkBounds)
                    : BuildOcrMask(snapshot, index);
                if (mask.Width < 1 || mask.Height < 1)
                    return reviewedTranslation == null
                        ? OcrFailure("A source text region overlaps protected graphics or neighboring text. No text was replaced.")
                        : ReviewedLineFailure(index, "The source crosses protected graphics or nearby text. Leave this line unchanged.");
                if (!familyPrepared)
                {
                    family = await EstimateOcrFamilyAsync(snapshot, token);
                    familyPrepared = true;
                    token.ThrowIfCancellationRequested();
                    RequireCurrentOcr(snapshot);
                }
                global::SnipStudio.RecognizedTextReplacement? replacement;
                try
                {
                    var (foreground, background, style, layout) = await MatchOcrLineStyleAsync(snapshot, index, mask, family, token,
                        partialLine ? segment : null, partialLine ? review!.Selection : null);
                    replacement = PrepareOcrReplacement(replacementText, inkBounds, mask, layout, foreground, background,
                        style, reviewedTranslation != null && isTranslation, token);
                }
                catch (InvalidOperationException exception) when (reviewedTranslation != null)
                {
                    return ReviewedLineFailure(index, exception.Message);
                }
                if (replacement == null)
                    return reviewedTranslation == null
                        ? OcrFailure("Recognized line " + (index + 1) + " does not fit its protected source region. No text was replaced; copy or edit the extraction separately.")
                        : ReviewedLineFailure(index, "The reviewed text does not fit the space around the original. Shorten it or leave this line unchanged.");
                replacements.Add(replacement);
            }
            token.ThrowIfCancellationRequested();
            RequireCurrentOcr(snapshot);
            int appliedCount = replacements.Count + noteEdits.Count;
            if (appliedCount == 0)
                return keptNotes > 0
                    ? new OcrConversionResult(true, lastExistingNote, 0, "Existing editable text was kept. Select the text note to edit it directly.")
                    : reviewedTranslation != null
                        ? OcrFailure("Nothing to apply. The reviewed text matches the original.")
                        : OcrFailure("No source text was available to convert.");
            int lastIndex;
            try
            {
                lastIndex = document.ReplaceRecognizedTextBlocks(replacements, snapshot.ImageIdentity, snapshot.Revision, noteEdits);
            }
            catch (ArgumentException exception)
            {
                return reviewedTranslation != null
                    ? OcrFailure(exception.Message.Split('\n')[0] + " Review the affected lines or leave them unchanged. The snip was kept.")
                    : OcrFailure("The converted text could not fit safely around nearby content. The original snip was kept. Select fewer lines or copy the recognized text.");
            }
            if (lastIndex < 0) return OcrFailure("The snip changed. No text was replaced; recognize the current image again.");
            return new OcrConversionResult(true, lastIndex, appliedCount,
                (reviewedTranslation == null ? "Converted " : isTranslation ? "Applied reviewed translation to " : "Updated ") + appliedCount +
                " line" + (appliedCount == 1 ? "" : "s") +
                (reviewedTranslation == null
                    ? " to editable text. Recognized strings were preserved verbatim; compare them with the image. Fonts are estimated. Undo restores the original pixels."
                    : ". Undo restores the original snip.") +
                (keptNotes == 0 ? "" : " Existing text notes were kept."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OcrFailure("Source-style matching reached its time limit. No text was replaced; select fewer lines and try again.");
        }
        finally { ocrWorkActive = false; }
    }

    private async Task<(Color Foreground, Color Background, NativeStyle Style, RectangleF Layout)> MatchOcrLineStyleAsync(
        OcrSnapshot snapshot, int index, Rectangle mask, NativeFamily? family, CancellationToken token,
        OcrReviewLine? segment = null, OcrSelectionSnapshot? selection = null)
    {
        token.ThrowIfCancellationRequested();
        RequireCurrentOcr(snapshot);
        NativeLine line = snapshot.NativeResult.Lines[index];
        SampleOcrColors(mask, out Color background, out Color foreground);
        string sourceText = segment?.Text ?? line.Text;
        Rectangle bounds = Rectangle.Intersect(new Rectangle(Point.Empty, document.ImageSize),
            EncloseSourcePixels(segment?.SourceInk ?? line.Bounds));
        using Bitmap source = document.CopyBaseRegion(bounds);
        Rectangle local = new(Point.Empty, source.Size);
        NativeStyle style = await Task.Run(() => global::SnipStudio.SourceTextMatcher.EstimateWithFamilyContext(
            source, local, sourceText, foreground, background, token, family), CancellationToken.None);
        token.ThrowIfCancellationRequested();
        RequireCurrentOcr(snapshot);
        // RichEdit refinement stays on the editor's STA after the background match.
        style = global::SnipStudio.SourceTextMatcher.RefineSizeOnUi(source, local, sourceText, foreground, background, style, token);
        RectangleF layout = VerifiedOcrLayout(snapshot, index, mask, background, token, selection);
        return (foreground, background, style, layout);
    }

    /// <summary>Highlights native word bands without changing their reading order or replacing any text.</summary>
    public bool HighlightRecognizedText(OcrSnapshot snapshot, ImagePoint start, ImagePoint end, uint argb,
        byte? highlighterAlpha = null)
    {
        VerifyCanMutate();
        RequireCurrentOcr(snapshot);
        var bands = global::SnipStudio.TextSnapSelection.BuildBands(snapshot.NativeResult.Lines,
            ConvertPoint(start, nameof(start)), ConvertPoint(end, nameof(end)), 8);
        if (bands.Count == 0) return false;
        document.AddHighlightBands(bands, Color.FromArgb(unchecked((int)argb)), highlighterAlpha);
        return true;
    }

    public IReadOnlyList<OcrBounds> PreviewSnappedHighlight(OcrSnapshot snapshot, ImagePoint start, ImagePoint end, double tolerance)
    {
        VerifyAccess();
        return GetSnappedHighlightBands(snapshot, start, end, tolerance).Select(OcrSnapshot.FromRectangle).ToArray();
    }

    public OcrSnapshot? ApplySnappedHighlight(OcrSnapshot snapshot, ImagePoint start, ImagePoint end, double tolerance,
        uint argb, byte? highlighterAlpha = null)
    {
        VerifyCanMutate();
        var bands = GetSnappedHighlightBands(snapshot, start, end, tolerance);
        if (bands.Count == 0) return null;
        document.AddHighlightBands(bands, Color.FromArgb(unchecked((int)argb)), highlighterAlpha);
        return new OcrSnapshot(this, document.BaseIdentity, document.Revision, snapshot.NativeResult);
    }

    private List<RectangleF> GetSnappedHighlightBands(OcrSnapshot snapshot, ImagePoint start, ImagePoint end, double tolerance)
    {
        RequireCurrentOcr(snapshot);
        return global::SnipStudio.TextSnapSelection.BuildStrokeBands(snapshot.NativeResult.Lines,
            ConvertPoint(start, nameof(start)), ConvertPoint(end, nameof(end)), (float)tolerance);
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

    private static OcrConversionResult ReviewedLineFailure(int index, string message) =>
        OcrFailure($"Line {index + 1}: {message} The snip was kept.");

    private bool IsWholeReviewedNote(OcrSnapshot snapshot, int index, int existing, global::SnipStudio.TextAnnotationInfo note)
    {
        NativeLine line = snapshot.NativeResult.Lines[index];
        RectangleF overlap = RectangleF.Intersect(line.Bounds, note.InkBounds);
        if (!string.Equals(note.Text, line.Text, StringComparison.Ordinal) ||
            overlap.Width * overlap.Height < line.Bounds.Width * line.Bounds.Height * .7f)
            return false;

        for (int candidate = 0; candidate < document.AnnotationCount; candidate++)
            if (candidate != existing && document.GetTextAnnotation(candidate) is { } other &&
                other.InkBounds.IntersectsWith(line.Bounds)) return false;
        for (int candidate = 0; candidate < snapshot.NativeResult.Lines.Count; candidate++)
            if (candidate != index && note.InkBounds.IntersectsWith(snapshot.NativeResult.Lines[candidate].Bounds))
                return false;
        return true;
    }

    private bool ReviewedNoteCoversNeighbor(OcrSnapshot snapshot, int index, int existing, RectangleF before, RectangleF after)
    {
        for (int candidate = 0; candidate < document.AnnotationCount; candidate++)
            if (candidate != existing && document.GetTextAnnotation(candidate) is { } other &&
                NewlyCovers(other.InkBounds)) return true;
        for (int candidate = 0; candidate < snapshot.NativeResult.Lines.Count; candidate++)
        {
            NativeLine line = snapshot.NativeResult.Lines[candidate];
            if ((candidate != index && NewlyCovers(line.Bounds)) || NewlyCovers(line.ExcludedGraphicBounds))
                return true;
        }
        return snapshot.NativeResult.ProtectedGraphics.Any(NewlyCovers);

        bool NewlyCovers(RectangleF area)
        {
            RectangleF overlap = RectangleF.Intersect(after, area);
            return !overlap.IsEmpty && !before.Contains(overlap);
        }
    }

    private int FindExistingOcrTextNote(RectangleF bounds)
    {
        for (int index = document.AnnotationCount - 1; index >= 0; index--)
        {
            var note = document.GetTextAnnotation(index);
            if (note != null && note.InkBounds.IntersectsWith(bounds)) return index;
        }
        return -1;
    }

    private bool TryFindEditableOcrNote(NativeLine line, out int existing)
    {
        existing = -1;
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
            {
                if (existing >= 0) return false;
                existing = index;
            }
        }
        return !overlapsNote || existing >= 0;
    }

    private Rectangle BuildOcrMask(OcrSnapshot snapshot, int sourceIndex)
    {
        NativeLine line = snapshot.NativeResult.Lines[sourceIndex];
        RectangleF text = line.Bounds;
        Rectangle mask = Rectangle.Intersect(new Rectangle(Point.Empty, document.ImageSize), Rectangle.FromLTRB(
            (int)Math.Floor(text.Left) - 2, (int)Math.Floor(text.Top) - 2,
            (int)Math.Ceiling(text.Right) + 2, (int)Math.Ceiling(text.Bottom) + 2));
        mask = ProtectOcrPadding(mask, text, line.ExcludedGraphicBounds);
        foreach (RectangleF graphic in snapshot.NativeResult.ProtectedGraphics)
            mask = ProtectOcrPadding(mask, text, graphic);
        for (int index = 0; index < document.AnnotationCount; index++)
            if (document.GetTextAnnotation(index) is { } note)
                mask = ProtectOcrPadding(mask, text, note.InkBounds);
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
        if (!ValidOcrBounds(obstacle)) return mask;
        Rectangle occupied = EncloseSourcePixels(obstacle);
        if (!mask.IntersectsWith(occupied)) return mask;
        if (obstacle.Right <= text.Left) return Rectangle.FromLTRB(Math.Max(mask.Left, occupied.Right), mask.Top, mask.Right, mask.Bottom);
        if (obstacle.Left >= text.Right) return Rectangle.FromLTRB(mask.Left, mask.Top, Math.Min(mask.Right, occupied.Left), mask.Bottom);
        if (obstacle.Bottom <= text.Top) return Rectangle.FromLTRB(mask.Left, Math.Max(mask.Top, occupied.Bottom), mask.Right, mask.Bottom);
        if (obstacle.Top >= text.Bottom) return Rectangle.FromLTRB(mask.Left, mask.Top, mask.Right, Math.Min(mask.Bottom, occupied.Top));
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
        {
            Rectangle bounds = EncloseSourcePixels(line.Bounds);
            union = union.IsEmpty ? bounds : Rectangle.Union(union, bounds);
        }
        if (union.IsEmpty || (long)union.Width * union.Height > 16L * 1024 * 1024) return null;
        var samples = new List<global::SnipStudio.SourceTextFamilySample>();
        foreach (NativeLine line in chosen)
        {
            token.ThrowIfCancellationRequested();
            Rectangle bounds = EncloseSourcePixels(line.Bounds);
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

    private RectangleF VerifiedOcrLayout(OcrSnapshot snapshot, int sourceIndex, Rectangle mask, Color background, CancellationToken token,
        OcrSelectionSnapshot? selection = null)
    {
        // Extra horizontal room is allowed only where the original pixels are
        // exactly the sampled background. This prevents a long filename from
        // painting over the neighboring date/type column or an annotation.
        int right = Math.Min(document.ImageSize.Width, mask.Right + (2 * 1024 * 1024) / mask.Height);
        NativeLine line = snapshot.NativeResult.Lines[sourceIndex];
        foreach (RectangleF graphic in snapshot.NativeResult.ProtectedGraphics)
            right = LimitOcrRight(graphic, mask, right);
        foreach (NativeLine other in snapshot.NativeResult.Lines)
        {
            if (ReferenceEquals(other, line)) continue;
            right = LimitOcrRight(other.Bounds, mask, right);
            right = LimitOcrRight(other.ExcludedGraphicBounds, mask, right);
        }
        if (selection != null)
            for (int wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
                if (!selection.IncludesWord(sourceIndex, wordIndex) && ValidOcrBounds(line.Words[wordIndex].Bounds))
                    right = LimitOcrRight(line.Words[wordIndex].Bounds, mask, right);
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
            if (!TryAlignOcrInk(sourceInk.Location, layout, ink, out PointF location)) return null;
            return new global::SnipStudio.RecognizedTextReplacement(mask, location, text, rtf,
                foreground, background, size, Math.Min(32768, layout.Width), layout, ink);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // Windows OCR can return fractional coordinates after an enlarged pass.
    // Measure from every touched pixel, including a partially covered first or
    // last row, so the native font matcher sees the original glyph edges.
    private static Rectangle EncloseSourcePixels(RectangleF bounds) => Rectangle.FromLTRB(
        (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
        (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));

    private static float RoundSourcePixel(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    private static bool TryAlignOcrInk(PointF sourceAnchor, RectangleF layout, RectangleF measuredInk, out PointF location)
    {
        location = default;
        if (measuredInk.IsEmpty || sourceAnchor.X < layout.Left || sourceAnchor.Y < layout.Top) return false;
        float minX = MathF.Ceiling(layout.Left), minY = MathF.Ceiling(layout.Top);
        float maxX = MathF.Floor(layout.Right - measuredInk.Width), maxY = MathF.Floor(layout.Bottom - measuredInk.Height);
        if (minX > maxX || minY > maxY) return false;
        float x = Math.Clamp(RoundSourcePixel(sourceAnchor.X), minX, maxX);
        float y = Math.Clamp(RoundSourcePixel(sourceAnchor.Y), minY, maxY);
        // Align the raster to the nearest safe source pixel instead of making
        // GDI+ blend an otherwise sharp glyph across two rows or columns.
        // Do not move the ink further than half a source pixel just to fit.
        if (Math.Abs(x - sourceAnchor.X) > .5f || Math.Abs(y - sourceAnchor.Y) > .5f) return false;
        location = new PointF(x - measuredInk.Left, y - measuredInk.Top);
        return true;
    }

    private static bool HasAngledOcrGeometry(OcrSnapshot snapshot) =>
        snapshot.TextAngle is double angle && (!double.IsFinite(angle) || Math.Abs(angle) > 2);

    private static bool ValidOcrBounds(RectangleF bounds) => float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom) && bounds.Width > 0 && bounds.Height > 0;
}
