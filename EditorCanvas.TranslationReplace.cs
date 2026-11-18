using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio
{
    // Geometry and source text are captured together, before any asynchronous
    // translation work. Translated blocks retain this exact order.
    public sealed class TranslationSourceBlock
    {
        public int LineIndex { get; private set; }
        public string SourceText { get; private set; }
        public RectangleF Bounds { get; private set; }

        internal TranslationSourceBlock(int lineIndex, string sourceText, RectangleF bounds)
        { LineIndex = lineIndex; SourceText = sourceText; Bounds = bounds; }
    }

    public sealed class TranslationSourceSnapshot
    {
        internal readonly EditorCanvas Owner;
        public object ImageIdentity { get; private set; }
        public long Revision { get; private set; }
        public bool SelectedOnly { get; private set; }
        public string Text { get; private set; }
        public ReadOnlyCollection<TranslationSourceBlock> Blocks { get; private set; }

        internal TranslationSourceSnapshot(EditorCanvas owner, object identity, long revision,
            bool selectedOnly, List<TranslationSourceBlock> blocks)
        {
            Owner = owner; ImageIdentity = identity; Revision = revision; SelectedOnly = selectedOnly;
            Blocks = new ReadOnlyCollection<TranslationSourceBlock>(new List<TranslationSourceBlock>(blocks));
            List<string> lines = new List<string>();
            foreach (TranslationSourceBlock block in blocks) lines.Add(block.SourceText);
            Text = String.Join(Environment.NewLine, lines.ToArray());
        }
    }

    public sealed class TranslationReplaceResult
    {
        public bool Applied { get; private set; }
        public string Message { get; private set; }
        internal TranslationReplaceResult(bool applied, string message) { Applied = applied; Message = message; }
    }

    // Prepared, detached values. The document validates and commits the entire
    // batch once; no source pixels or history entries change during preparation.
    internal sealed class RecognizedTextReplacement
    {
        internal readonly Rectangle Bounds;
        internal readonly RectangleF LayoutBounds;
        internal readonly RectangleF MeasuredInkBounds;
        internal readonly PointF TextLocation;
        internal readonly string PlainText, Rtf;
        internal readonly Color Foreground, Background;
        internal readonly float FontSize, LayoutWidth;

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth)
            : this(bounds, textLocation, plainText, rtf, foreground, background, fontSize, layoutWidth, bounds) { }

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth, RectangleF layoutBounds)
            : this(bounds, textLocation, plainText, rtf, foreground, background, fontSize, layoutWidth, layoutBounds, RectangleF.Empty) { }

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth, RectangleF layoutBounds, RectangleF measuredInkBounds)
        {
            Bounds = bounds; LayoutBounds = layoutBounds; MeasuredInkBounds = measuredInkBounds;
            TextLocation = textLocation; PlainText = plainText; Rtf = rtf;
            Foreground = foreground; Background = background; FontSize = fontSize; LayoutWidth = layoutWidth;
        }
    }

    public sealed partial class EditorCanvas
    {
        public TranslationSourceSnapshot CaptureTranslationSource(bool selectedOnly)
        {
            if (disposingCanvas || IsDisposed || !HasImage || recognizedText == null || !recognizedText.Available ||
                !Object.ReferenceEquals(recognizedBase, ImageIdentity) || recognizedRevision != DocumentRevision)
                throw new InvalidOperationException("Recognized text is no longer current. Reopen Text actions for this snip, then translate again.");
            if (recognizedText.TextAngle.HasValue && Math.Abs(recognizedText.TextAngle.Value) > 2)
                throw new InvalidOperationException("In-place translation currently needs horizontal text. Straighten the snip, then recognize its text again.");
            if (selectedOnly && !textSelection.HasSelection)
                throw new InvalidOperationException("Select the words to replace, or clear the selection to translate the whole snip.");

            List<TranslationSourceBlock> blocks = new List<TranslationSourceBlock>();
            int wordIndex = 0;
            for (int lineIndex = 0; lineIndex < recognizedText.Lines.Count; lineIndex++)
            {
                TextLineRegion line = recognizedText.Lines[lineIndex];
                if (line == null) continue;
                RectangleF selectedBounds = RectangleF.Empty, allWordBounds = RectangleF.Empty;
                List<string> selectedWords = new List<string>(), allWords = new List<string>();
                foreach (TextWordRegion word in line.Words)
                {
                    // Match TextOverlaySelection's flattened valid-word order.
                    if (word == null || String.IsNullOrWhiteSpace(word.Text) || !ValidTranslationBounds(word.Bounds)) continue;
                    allWordBounds = allWordBounds.IsEmpty ? word.Bounds : RectangleF.Union(allWordBounds, word.Bounds);
                    allWords.Add(word.Text);
                    if (selectedOnly && textSelection.IsSelected(wordIndex))
                    {
                        selectedBounds = selectedBounds.IsEmpty ? word.Bounds : RectangleF.Union(selectedBounds, word.Bounds);
                        selectedWords.Add(word.Text);
                    }
                    wordIndex++;
                }
                string source = selectedOnly ? String.Join(" ", selectedWords.ToArray()) :
                    (!String.IsNullOrWhiteSpace(line.Text) ? line.Text : String.Join(" ", allWords.ToArray()));
                RectangleF bounds = selectedOnly ? selectedBounds :
                    (ValidTranslationBounds(line.Bounds) ? line.Bounds : allWordBounds);
                if (String.IsNullOrWhiteSpace(source) || !ValidTranslationBounds(bounds)) continue;
                bounds = RectangleF.Intersect(bounds, new RectangleF(PointF.Empty, ImageSize));
                if (!ValidTranslationBounds(bounds)) continue;
                // Painting raster words cannot remove vector note glyphs.
                // Keep such annotations intact rather than hiding/editing a
                // guessed text owner or silently leaving duplicate words.
                if (FindTextAnnotationForLine(new TextLineRegion { Text = source, Bounds = bounds }) != -1)
                    throw new InvalidOperationException("Some recognized text overlaps an editable text box. Move or edit that box first, or select only screenshot text to translate. Nothing was replaced.");
                blocks.Add(new TranslationSourceBlock(lineIndex, source, bounds));
            }
            if (blocks.Count == 0)
                throw new InvalidOperationException("No recognized text positions are available to replace. Try Text actions on a clearer snip.");
            return new TranslationSourceSnapshot(this, ImageIdentity, DocumentRevision, selectedOnly, blocks);
        }

        public async Task<TranslationReplaceResult> ReplaceTranslatedTextAsync(TranslationSourceSnapshot snapshot,
            IList<string> translatedBlocks, CancellationToken cancellationToken)
        {
            if (snapshot == null || !Object.ReferenceEquals(snapshot.Owner, this))
                return TranslationReplacementFailure("The translation has no matching source positions. Reopen translation for this snip.");
            if (translatedBlocks == null || translatedBlocks.Count != snapshot.Blocks.Count)
                return TranslationReplacementFailure("The translated lines do not match their source positions. Translate again before replacing the snip text.");
            if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
            if (matchingSourceText)
                return TranslationReplacementFailure("Source text matching is already running. Wait for it to finish, then try replacing the text again.");

            // Detach from a mutable caller-owned list before the first await.
            List<string> translated = new List<string>();
            foreach (string value in translatedBlocks)
            {
                if (String.IsNullOrWhiteSpace(value))
                    return TranslationReplacementFailure("Every source line needs a translation. No screenshot text was replaced.");
                if (value.Length > 16000)
                    return TranslationReplacementFailure("One translated line exceeds 16,000 characters. Shorten that line before replacing the snip text.");
                translated.Add(value);
            }

            CancellationTokenSource matching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = matching.Token;
            matchingSourceText = true;
            sourceStyleCancellation = matching;
            RaiseTextSelectionChanged();
            try
            {
                List<RecognizedTextReplacement> replacements = new List<RecognizedTextReplacement>();
                SourceTextFamilyContext familyContext = null;
                bool familyContextPrepared = false;
                for (int i = 0; i < snapshot.Blocks.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                    TranslationSourceBlock block = snapshot.Blocks[i];
                    // Protected identifiers and other unchanged lines already
                    // have the exact original appearance. Keep their pixels
                    // instead of reconstructing an approximate font needlessly.
                    if (String.Equals(translated[i], block.SourceText, StringComparison.Ordinal)) continue;
                    // Build one original-source family estimate for the batch.
                    // Per-line size, weight and slant still use that line's pixels.
                    // An entirely unchanged translation needs no font work.
                    if (!familyContextPrepared)
                    {
                        familyContext = await GetSourceFamilyContextAsync(recognizedText,
                            snapshot.ImageIdentity, snapshot.Revision, token);
                        token.ThrowIfCancellationRequested();
                        if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                        familyContextPrepared = true;
                    }
                    Rectangle maskBounds = PaddedTextBounds(block.Bounds);
                    // A changed line's erasure padding must not touch a line
                    // retained as original pixels. Reject the entire batch so
                    // the translated draft remains available without damage.
                    for (int other = 0; other < snapshot.Blocks.Count; other++)
                    {
                        TranslationSourceBlock retained = snapshot.Blocks[other];
                        if (other != i && String.Equals(translated[other], retained.SourceText, StringComparison.Ordinal) &&
                            maskBounds.IntersectsWith(Rectangle.Ceiling(retained.Bounds)))
                            return TranslationReplacementFailure("A translated line is too close to unchanged text to replace safely. Nothing was replaced; copy or adjust the translation and try again.");
                    }
                    Rectangle sourceBounds = Rectangle.Intersect(new Rectangle(Point.Empty, ImageSize), Rectangle.Ceiling(block.Bounds));
                    if (maskBounds.Width < 1 || maskBounds.Height < 1 || sourceBounds.Width < 1 || sourceBounds.Height < 1)
                        return TranslationReplacementFailure("A recognized text region no longer fits inside the snip. Recognize the current image again.");
                    Color background, foreground;
                    SampleTranslationTextColors(maskBounds, out background, out foreground);
                    string family = "Segoe UI";
                    FontStyle fontStyle = FontStyle.Regular;
                    float fontSize = EstimateLineFontSize(block.SourceText, block.Bounds, sourceBounds.Width);
                    using (Bitmap source = document.CopyBaseRegion(sourceBounds))
                    {
                        try
                        {
                            SourceTextStyle style = await Task.Run(delegate
                            {
                                return SourceTextMatcher.EstimateWithFamilyContext(source, new Rectangle(Point.Empty, source.Size),
                                    block.SourceText, foreground, background, token, familyContext);
                            }, token);
                            token.ThrowIfCancellationRequested();
                            if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                            // Compare the original line with the same native
                            // renderer used for editing before fitting its
                            // translation. Translation must share the source
                            // appearance validation used by inline text edits.
                            style = SourceTextMatcher.RefineSizeOnUi(source, new Rectangle(Point.Empty, source.Size),
                                block.SourceText, foreground, background, style, token);
                            family = style.FontFamily; fontStyle = style.Style; fontSize = style.FontSizePixels;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                    RecognizedTextReplacement replacement;
                    if (!TryFitTranslationWithSpace(snapshot, i, translated[i], maskBounds, foreground, background,
                        family, fontStyle, fontSize, token, out replacement))
                        return TranslationReplacementFailure("Translation for source line " + (block.LineIndex + 1) +
                            " does not fit its source line or verified blank space to its right, even at 6 pixels. Shorten that translated line and try again. Nothing was replaced; the complete translation remains in this window.");
                    replacements.Add(replacement);
                }

                token.ThrowIfCancellationRequested();
                if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                if (replacements.Count == 0)
                    return new TranslationReplaceResult(true, "The translated text is unchanged. Original screenshot pixels were kept.");
                CancelGesture();
                int lastTextIndex = document.ReplaceRecognizedTextBlocks(replacements, snapshot.ImageIdentity, snapshot.Revision);
                if (lastTextIndex < 0) return TranslationReplacementStale();
                AdvanceSourceFamilyContext(snapshot.ImageIdentity, snapshot.Revision);
                // Restore tool state before selecting the inserted text, so a
                // highlighter's ink restoration cannot recolor the replacement.
                TextActionsMode = false;
                Tool = EditorTool.Select;
                selectedIndex = lastTextIndex;
                textFontSize = replacements[replacements.Count - 1].FontSize;
                RaiseTextStyleChanged();
                RaiseChanged();
                return new TranslationReplaceResult(true, "Translated text replaced the recognized screenshot text in its original positions. Use Undo to restore the original.");
            }
            catch (OperationCanceledException)
            {
                return TranslationReplacementFailure("Replacing the snip text was canceled. Nothing was replaced; the translation remains available.");
            }
            finally
            {
                if (Object.ReferenceEquals(sourceStyleCancellation, matching))
                {
                    matchingSourceText = false;
                    sourceStyleCancellation = null;
                }
                matching.Dispose();
                if (!disposingCanvas && !IsDisposed) RaiseTextSelectionChanged();
            }
        }

        private void SampleTranslationTextColors(Rectangle bounds, out Color background, out Color foreground)
        {
            SampleTextColors(bounds, out background, out foreground);
            using (Bitmap source = document.CopyBaseRegion(bounds))
                foreground = SourceTextColor.Estimate(source, background, foreground);
        }

        private bool TranslationSnapshotCurrent(TranslationSourceSnapshot snapshot)
        {
            return !disposingCanvas && !IsDisposed && HasImage && Object.ReferenceEquals(snapshot.ImageIdentity, ImageIdentity) &&
                snapshot.Revision == DocumentRevision;
        }

        private static TranslationReplaceResult TranslationReplacementFailure(string message)
        { return new TranslationReplaceResult(false, message); }

        private static TranslationReplaceResult TranslationReplacementStale()
        {
            return TranslationReplacementFailure("The snip changed while translation was open. Nothing was replaced. Copy the translation if needed, then reopen translation for the current snip.");
        }

        private bool TryFitTranslationWithSpace(TranslationSourceSnapshot snapshot, int sourceIndex, string text,
            Rectangle maskBounds, Color foreground, Color background, string family, FontStyle style,
            float sourceSize, CancellationToken token, out RecognizedTextReplacement replacement)
        {
            RectangleF original = snapshot.Blocks[sourceIndex].Bounds;
            float initialSize = Math.Max(6, Math.Min(160, sourceSize));
            // Keep the source size before considering any shrink. Expansion adds
            // layout room only; the original source-erasure mask never grows.
            if (TryCreateFittedTranslation(text, original, maskBounds, foreground, background,
                family, style, initialSize, Math.Min(32768, (float)Math.Floor(original.Width)), out replacement)) return true;
            RectangleF available = TranslationLayoutWithBlankRight(snapshot, sourceIndex, maskBounds, background, token);
            if (!TryFitTranslationBlock(text, available, maskBounds, foreground, background,
                family, style, sourceSize, out replacement)) return false;
            // Maximum verified blank room is a fitting budget, not the note's
            // eventual editor width. Tighten with one extra native measurement;
            // keep the already verified wider result if the threshold reflows.
            float compactWidth = Math.Min(replacement.LayoutWidth,
                Math.Max(original.Width, (float)Math.Ceiling(replacement.MeasuredInkBounds.Right) + 2));
            if (compactWidth < replacement.LayoutWidth)
            {
                RectangleF compactBounds = new RectangleF(original.Left, original.Top, compactWidth, original.Height);
                RecognizedTextReplacement compact;
                if (TryCreateFittedTranslation(text, compactBounds, maskBounds, foreground, background,
                    family, style, replacement.FontSize, (float)Math.Floor(compactWidth), out compact)) replacement = compact;
            }
            return true;
        }

        private RectangleF TranslationLayoutWithBlankRight(TranslationSourceSnapshot snapshot, int sourceIndex,
            Rectangle maskBounds, Color background, CancellationToken token)
        {
            TranslationSourceBlock block = snapshot.Blocks[sourceIndex];
            RectangleF original = block.Bounds;
            const int maximumScanPixels = 2 * 1024 * 1024;
            if (maskBounds.Height < 1 || maskBounds.Height > 512 || maskBounds.Right >= ImageSize.Width) return original;
            int scanLeft = maskBounds.Right;
            int right = Math.Min(ImageSize.Width, Math.Min((int)Math.Floor(original.Left + 32768),
                scanLeft + maximumScanPixels / maskBounds.Height));
            foreach (TranslationSourceBlock other in snapshot.Blocks)
                if (!Object.ReferenceEquals(other, block)) right = LimitTranslationRight(other.Bounds, maskBounds, scanLeft, right);
            // Unselected words/lines must also be protected when translating
            // only part of a screenshot. Raster sampling alone cannot see a
            // transparent or not-yet-rendered native annotation.
            if (recognizedText != null)
                for (int lineIndex = 0; lineIndex < recognizedText.Lines.Count; lineIndex++)
                {
                    TextLineRegion line = recognizedText.Lines[lineIndex];
                    if (line == null) continue;
                    if (lineIndex != block.LineIndex) right = LimitTranslationRight(line.Bounds, maskBounds, scanLeft, right);
                    else foreach (TextWordRegion word in line.Words)
                    {
                        if (word == null || original.Contains(word.Bounds)) continue;
                        right = LimitTranslationRight(word.Bounds, maskBounds, scanLeft, right);
                    }
                }
            for (int index = 0; index < document.AnnotationCount; index++)
                right = LimitTranslationRight(document.GetAnnotationBounds(index), maskBounds, scanLeft, right);
            if (right <= scanLeft) return original;
            Rectangle scan = Rectangle.FromLTRB(scanLeft, maskBounds.Top, right, maskBounds.Bottom);
            using (Bitmap pixels = document.CopyBaseRegion(scan))
            {
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
                            // Exact ARGB comparison deliberately protects pale
                            // glyphs, dividers, gradients and transparency.
                            if (row[at] != background.B || row[at + 1] != background.G ||
                                row[at + 2] != background.R || row[at + 3] != 255)
                                return RectangleF.FromLTRB(original.Left, original.Top,
                                    Math.Max(original.Right, scanLeft + x - 1), original.Bottom);
                    }
                }
                finally { pixels.UnlockBits(data); }
            }
            // Keep a scanned blank guard column when stopping at a bounded
            // search/neighbor edge; the physical image edge needs no extra one.
            return RectangleF.FromLTRB(original.Left, original.Top,
                Math.Max(original.Right, right < ImageSize.Width ? right - 1 : right), original.Bottom);
        }

        private static int LimitTranslationRight(RectangleF obstacle, Rectangle band, int scanLeft, int right)
        {
            if (obstacle.IsEmpty || obstacle.Bottom <= band.Top || obstacle.Top >= band.Bottom ||
                obstacle.Right <= scanLeft || obstacle.Left >= right) return right;
            return Math.Min(right, Math.Max(scanLeft, (int)Math.Floor(obstacle.Left) - 2));
        }

        private static bool TryFitTranslationBlock(string text, RectangleF sourceBounds, Rectangle maskBounds,
            Color foreground, Color background, string family, FontStyle style, float sourceSize,
            out RecognizedTextReplacement replacement)
        {
            replacement = null;
            float width = Math.Min(32768, (float)Math.Floor(sourceBounds.Width));
            if (width < 1 || sourceBounds.Height < 1) return false;
            float initialSize = Math.Max(6, Math.Min(160, sourceSize));
            // Wrapping is confined to the source rectangle. All glyphs must fit;
            // a fit failure leaves the whole document and translated draft intact.
            if (TryCreateFittedTranslation(text, sourceBounds, maskBounds, foreground, background,
                family, style, initialSize, width, out replacement)) return true;
            if (initialSize <= 6 || !TryCreateFittedTranslation(text, sourceBounds, maskBounds, foreground, background,
                family, style, 6, width, out replacement)) return false;
            // Native RichEdit measurement renders a layout. Bound the number of
            // attempts instead of measuring every intervening pixel size.
            float lower = 6, upper = initialSize;
            for (int attempt = 0; attempt < 8 && upper - lower >= 0.5f; attempt++)
            {
                float candidate = (float)Math.Floor((lower + upper) * 2) / 4;
                RecognizedTextReplacement fitted;
                if (TryCreateFittedTranslation(text, sourceBounds, maskBounds, foreground, background,
                    family, style, candidate, width, out fitted))
                {
                    replacement = fitted;
                    lower = candidate;
                }
                else upper = candidate;
            }
            return true;
        }

        private static bool TryCreateFittedTranslation(string text, RectangleF sourceBounds, Rectangle maskBounds,
            Color foreground, Color background, string family, FontStyle style, float size, float width,
            out RecognizedTextReplacement replacement)
        {
            replacement = null;
            try
            {
                string rtf = RichTextLayout.CreatePlainRtf(text, family, size, style, foreground);
                RichTextMetrics metrics = RichTextLayout.Measure(rtf, width);
                RectangleF ink = metrics.InkBounds;
                if (ink.IsEmpty || ink.Width > sourceBounds.Width || ink.Height > sourceBounds.Height) return false;
                PointF location = new PointF(sourceBounds.Left - ink.Left, sourceBounds.Top - ink.Top);
                replacement = new RecognizedTextReplacement(maskBounds, location, text, rtf,
                    foreground, background, size, width, sourceBounds, ink);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool ValidTranslationBounds(RectangleF bounds)
        {
            return FiniteTranslationCoordinate(bounds.Left) && FiniteTranslationCoordinate(bounds.Top) &&
                FiniteTranslationCoordinate(bounds.Right) && FiniteTranslationCoordinate(bounds.Bottom) &&
                bounds.Width > 0 && bounds.Height > 0;
        }

        private static bool FiniteTranslationCoordinate(float value)
        { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }
}
