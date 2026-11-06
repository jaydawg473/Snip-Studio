using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
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
        internal readonly PointF TextLocation;
        internal readonly string PlainText, Rtf;
        internal readonly Color Foreground, Background;
        internal readonly float FontSize, LayoutWidth;

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth)
        {
            Bounds = bounds; TextLocation = textLocation; PlainText = plainText; Rtf = rtf;
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
                for (int i = 0; i < snapshot.Blocks.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                    TranslationSourceBlock block = snapshot.Blocks[i];
                    Rectangle maskBounds = PaddedTextBounds(block.Bounds);
                    Rectangle sourceBounds = Rectangle.Intersect(new Rectangle(Point.Empty, ImageSize), Rectangle.Ceiling(block.Bounds));
                    if (maskBounds.Width < 1 || maskBounds.Height < 1 || sourceBounds.Width < 1 || sourceBounds.Height < 1)
                        return TranslationReplacementFailure("A recognized text region no longer fits inside the snip. Recognize the current image again.");
                    Color background, foreground;
                    SampleTextColors(maskBounds, out background, out foreground);
                    string family = "Segoe UI";
                    FontStyle fontStyle = FontStyle.Regular;
                    float fontSize = EstimateLineFontSize(block.SourceText, block.Bounds, sourceBounds.Width);
                    using (Bitmap source = document.CopyBaseRegion(sourceBounds))
                    {
                        try
                        {
                            SourceTextStyle style = await Task.Run(delegate
                            {
                                return SourceTextMatcher.Estimate(source, new Rectangle(Point.Empty, source.Size),
                                    block.SourceText, foreground, background, token);
                            }, token);
                            family = style.FontFamily; fontStyle = style.Style; fontSize = style.FontSizePixels;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                    RecognizedTextReplacement replacement;
                    if (!TryFitTranslationBlock(translated[i], block.Bounds, maskBounds, foreground, background,
                        family, fontStyle, fontSize, out replacement))
                        return TranslationReplacementFailure("Translation for source line " + (block.LineIndex + 1) +
                            " does not fit its original text area, even at 6 pixels. Shorten that translated line and try again. Nothing was replaced; the complete translation remains in this window.");
                    replacements.Add(replacement);
                }

                token.ThrowIfCancellationRequested();
                if (!TranslationSnapshotCurrent(snapshot)) return TranslationReplacementStale();
                CancelGesture();
                int lastTextIndex = document.ReplaceRecognizedTextBlocks(replacements, snapshot.ImageIdentity, snapshot.Revision);
                if (lastTextIndex < 0) return TranslationReplacementStale();
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
                matchingSourceText = false;
                if (Object.ReferenceEquals(sourceStyleCancellation, matching)) sourceStyleCancellation = null;
                matching.Dispose();
                if (!disposingCanvas && !IsDisposed) RaiseTextSelectionChanged();
            }
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
                    foreground, background, size, width);
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
