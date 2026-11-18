using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private float textFontSize = 20;
        private float defaultTextBoxWidth = 320;
        private long recognizedRevision = Int64.MinValue;
        private bool matchingSourceText;
        private CancellationTokenSource sourceStyleCancellation;
        public bool IsMatchingSourceText { get { return matchingSourceText; } }
        public event EventHandler TextStyleChanged;

        public bool HasSelectedTextAnnotation { get { return HasImage && document.GetTextAnnotation(selectedIndex) != null; } }
        public float TextFontSize
        {
            get
            {
                if (InlineTextEditing) return InlineTextFontSize;
                TextAnnotationInfo info = HasImage ? document.GetTextAnnotation(selectedIndex) : null;
                return info == null ? textFontSize : info.FontSize;
            }
            set
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 1 || value > 1024) throw new ArgumentOutOfRangeException("value");
                if (InlineTextEditing)
                {
                    ApplyInlineTextFontSize(value); textFontSize = value; return;
                }
                TextAnnotationInfo info = HasImage && !textActionsMode ? document.GetTextAnnotation(selectedIndex) : null;
                if (info != null)
                {
                    CancelGesture();
                    float available = Math.Max(12, ImageSize.Width - info.Location.X);
                    value = Math.Min(value, Math.Max(8, available / 1.5f));
                    float width = info.LayoutWidth <= 0 ? Math.Max(info.Bounds.Width, value * 1.5f) : Math.Max(info.LayoutWidth, value * 1.5f);
                    width = Math.Min(available, width);
                    if (document.UpdateTextAnnotationFontSize(selectedIndex, value, width)) RaiseChanged();
                }
                textFontSize = value;
                RaiseTextStyleChanged();
            }
        }

        public bool CanEditRecognizedLine
        {
            get
            {
                TextRecognitionResult result = RecognizedEditResult;
                TextOverlaySelection selection = RecognizedEditSelection;
                int index = selection == null ? -1 : selection.SelectedLineIndex;
                return !matchingSourceText && (textActionsMode || (tool == EditorTool.Text && RecognizedTextSessionActive)) && HasImage && result != null && result.Available &&
                    index >= 0 && index < result.Lines.Count &&
                    (!result.TextAngle.HasValue || Math.Abs(result.TextAngle.Value) <= 2);
            }
        }
        public bool CanDeleteRecognizedText { get { return textActionsMode && HasSelectedRecognizedText && CanEditRecognizedLine; } }

        public void EditRecognizedLine() { EditRecognizedLineAt(null); }

        private async void EditRecognizedLineAt(PointF? caret)
        {
            await EditRecognizedLineAtAsync(caret);
        }

        private async Task EditRecognizedLineAtAsync(PointF? caret)
        {
            // A newer line click may supersede a canceled match before its
            // continuation returns to the UI thread. Only that canceled request
            // may be bypassed; an active match still owns the busy state.
            if (matchingSourceText && sourceStyleCancellation != null && sourceStyleCancellation.IsCancellationRequested)
                matchingSourceText = false;
            if (!CanEditRecognizedLine) return;
            TextRecognitionResult sourceResult = RecognizedEditResult;
            TextOverlaySelection sourceWords = RecognizedEditSelection;
            int sourceLineIndex = sourceWords.SelectedLineIndex;
            string sourceSelection = sourceWords.SelectedText;
            TextLineRegion line = sourceResult.Lines[sourceLineIndex];
            StartRecognizedTextSession(sourceResult, sourceWords);
            recognizedSessionLine = sourceLineIndex;
            int owner = FindTextAnnotationForLine(line);
            if (owner == -2) { ExplainMixedTextLine(); return; }
            if (owner >= 0)
            {
                BeginRecognizedAnnotationInline(owner, caret); return;
            }
            CancelGesture();
            Rectangle erase = PaddedTextBounds(line.Bounds, line.ExcludedGraphicBounds, line.RecognitionClipBounds);
            if (erase.Width < 12 || erase.Height < 2) return;
            Color background, foreground;
            SampleTextColors(erase, out background, out foreground);
            using (Bitmap sourceColors = document.CopyBaseRegion(erase))
                foreground = SourceTextColor.Estimate(sourceColors, background, foreground);
            float width = Math.Min(ImageSize.Width - erase.Left, Math.Max(24, erase.Width));
            float size = EstimateLineFontSize(line.Text, line.Bounds, width);
            int selectionStart, selectionLength;
            bool hasRange = sourceWords.TryGetSelectedCharacterRange(line.Text, out selectionStart, out selectionLength);
            object identity = ImageIdentity;
            long revision = DocumentRevision;
            string rtf = null;
            matchingSourceText = true;
            CancellationTokenSource matching = new CancellationTokenSource();
            CancellationToken matchingToken = matching.Token;
            sourceStyleCancellation = matching;
            RaiseTextSelectionChanged();
            try
            {
                SourceTextFamilyContext familyContext = await GetSourceFamilyContextAsync(sourceResult, identity, revision, matchingToken);
                if (matchingToken.IsCancellationRequested || !RecognizedEditStateCurrent(sourceResult, sourceWords, identity, revision)) return;
                Rectangle sourceBounds = Rectangle.Intersect(new Rectangle(Point.Empty, ImageSize), Rectangle.Ceiling(line.Bounds));
                using (Bitmap source = document.CopyBaseRegion(sourceBounds))
                {
                    SourceTextStyle style = await Task.Run(delegate { return SourceTextMatcher.EstimateWithFamilyContext(source,
                        new Rectangle(Point.Empty, source.Size), line.Text, foreground, background, matchingToken, familyContext); });
                    if (matchingToken.IsCancellationRequested || !RecognizedEditStateCurrent(sourceResult, sourceWords, identity, revision)) return;
                    style = SourceTextMatcher.RefineSizeOnUi(source, new Rectangle(Point.Empty, source.Size),
                        line.Text, foreground, background, style, matchingToken);
                    if (matchingToken.IsCancellationRequested || !RecognizedEditStateCurrent(sourceResult, sourceWords, identity, revision)) return;
                    size = style.FontSizePixels;
                    rtf = RichTextLayout.CreatePlainRtf(line.Text, style.FontFamily, size, style.Style, foreground);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                if (matchingToken.IsCancellationRequested || !RecognizedEditStateCurrent(sourceResult, sourceWords, identity, revision)) return;
                rtf = RichTextLayout.CreatePlainRtf(line.Text, "Segoe UI", size, FontStyle.Regular, foreground);
            }
            finally
            {
                if (Object.ReferenceEquals(sourceStyleCancellation, matching))
                { matchingSourceText = false; sourceStyleCancellation = null; }
                matching.Dispose();
                if (!disposingCanvas) RaiseTextSelectionChanged();
            }
            if (matchingToken.IsCancellationRequested || !RecognizedEditStateCurrent(sourceResult, sourceWords, identity, revision) ||
                sourceWords.SelectedLineIndex != sourceLineIndex || !String.Equals(sourceWords.SelectedText, sourceSelection, StringComparison.Ordinal)) return;
            // The keyboard proxy is invisible, but its wrapping width is also
            // the committed annotation's layout box. Keep room to type only
            // inside this column rather than claiming neighboring source cells.
            float right = RecognizedColumnRight(line.Bounds, sourceResult.Lines, ImageSize.Width);
            RichTextMetrics measured = RichTextLayout.Measure(rtf, Math.Max(1, right - line.Bounds.Left));
            float layoutLeft = line.Bounds.Left - measured.InkBounds.Left;
            width = Math.Max(1, right - layoutLeft);
            float height = Math.Max(erase.Height, size * 1.55f + 8);
            BeginRecognizedInlineDraftAt(erase, line.Bounds, line.Text, rtf, background, width, height,
                hasRange ? selectionStart : line.Text.Length, hasRange ? selectionLength : 0, caret);
        }

        public void DeleteRecognizedText()
        {
            if (!CanDeleteRecognizedText) return;
            TextLineRegion line = recognizedText.Lines[textSelection.SelectedLineIndex];
            int owner = FindTextAnnotationForLine(line);
            if (owner == -2) { ExplainMixedTextLine(); return; }
            if (owner >= 0)
            {
                TextAnnotationInfo info = document.GetTextAnnotation(owner);
                string needle = textSelection.SelectedText;
                int at = info.Text.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0 || info.Text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal) >= 0)
                {
                    // OCR is an estimate. Show the original editable note rather
                    // than silently deleting a guessed occurrence.
                    EditTextAnnotation(owner);
                    return;
                }
                if (document.RemoveTextAnnotationRange(owner, at, needle.Length))
                { selectedIndex = -1; RaiseChanged(); }
                return;
            }
            Rectangle erase = PaddedTextBounds(line.Bounds, line.ExcludedGraphicBounds, line.RecognitionClipBounds);
            Color background, foreground;
            SampleTextColors(erase, out background, out foreground);
            List<Rectangle> selectedRegions = new List<Rectangle>();
            foreach (RectangleF band in textSelection.SelectedLineBounds())
            {
                Rectangle padded = PaddedTextBounds(band);
                foreach (TextLineRegion sourceLine in recognizedText.Lines)
                {
                    padded = ProtectGraphicPadding(padded, band, sourceLine.ExcludedGraphicBounds);
                    if (sourceLine.Bounds.IntersectsWith(band) && !sourceLine.RecognitionClipBounds.IsEmpty)
                        padded = Rectangle.Intersect(padded, Rectangle.Ceiling(sourceLine.RecognitionClipBounds));
                }
                selectedRegions.Add(padded);
            }
            // Screenshot pixels have no editable font runs. Erase only the
            // selected words; all remaining source typography stays exact.
            if (document.EraseImageRegions(selectedRegions, background)) { selectedIndex = -1; RaiseChanged(); }
        }

        private void AddTextNote(PointF location)
        {
            BeginInlineTextNote(location);
        }

        public void EditSelectedText()
        {
            if (InlineTextEditing) { inlineTextEditor.Focus(); return; }
            if (textActionsMode) EditRecognizedLine();
            else if (HasSelectedTextAnnotation) EditTextAnnotation(selectedIndex);
        }

        private void EditTextAnnotation(int index)
        {
            if (textActionsMode) EditTextAnnotationAdvanced(index);
            else BeginInlineTextEdit(index);
        }

        private void EditTextAnnotationAdvanced(int index)
        {
            TextAnnotationInfo info = document.GetTextAnnotation(index);
            if (info == null) return;
            CancelGesture();
            int available = Math.Max(12, ImageSize.Width - (int)Math.Floor(info.Location.X));
            float width = info.LayoutWidth > 0 ? info.LayoutWidth : Math.Min(available, Math.Max(24, info.Bounds.Width));
            using (TextEditorDialog dialog = new TextEditorDialog("Edit text", info.Text, info.FontSize, width,
                available, info.Color, Color.White, false, info.Rtf))
            {
                if (textActionsMode)
                {
                    int at = info.Text.IndexOf(textSelection.SelectedText, StringComparison.Ordinal);
                    if (at >= 0) dialog.SelectTextRange(at, textSelection.SelectedText.Length);
                }
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (document.UpdateRichTextAnnotation(index, dialog.EnteredText, dialog.Rtf, dialog.TextColor, dialog.TextSize, dialog.BoxWidth))
                {
                    selectedIndex = String.IsNullOrWhiteSpace(dialog.EnteredText) ? -1 : index;
                    textFontSize = dialog.TextSize;
                    defaultTextBoxWidth = dialog.BoxWidth;
                    textActionsMode = false;
                    tool = EditorTool.Select;
                    RaiseTextSelectionChanged(); UpdateCursor(); RaiseChanged();
                }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || !IsOnImage(e.Location)) return;
            if (HandleRecognizedInlineDoubleClick(e)) return;
            if (!textActionsMode && (tool == EditorTool.RemoveArea ||
                (tool == EditorTool.Select && IsOnSourceRemovalSelection(e.Location)))) return;
            CancelGesture();
            if (textActionsMode) { EditRecognizedLine(); return; }
            if (tool != EditorTool.Select && tool != EditorTool.Text) return;
            int hit = HitTestTextForEditing(ToImage(e.Location, false), 3 / zoom);
            if (document.GetTextAnnotation(hit) != null) { selectedIndex = hit; EditTextAnnotation(hit); }
        }

        private int FindTextAnnotationForLine(TextLineRegion line)
        {
            // A rendered text note is still a vector. Its words must be edited
            // through that note, never by painting the bitmap underneath it.
            bool overlapFound = false;
            string recognized = WithoutWhitespace(line.Text);
            for (int i = document.AnnotationCount - 1; i >= 0; i--)
            {
                TextAnnotationInfo info = document.GetTextAnnotation(i);
                if (info == null) continue;
                RectangleF overlap = RectangleF.Intersect(line.Bounds, info.InkBounds);
                if (overlap.Width <= 0 || overlap.Height <= 0) continue;
                overlapFound = true;
                if (recognized.Length > 0 && WithoutWhitespace(info.Text).IndexOf(recognized, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    overlap.Width * overlap.Height >= line.Bounds.Width * line.Bounds.Height * 0.7f) return i;
            }
            return overlapFound ? -2 : -1;
        }

        private static string WithoutWhitespace(string text)
        {
            return String.Concat((text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }
        private void ExplainMixedTextLine()
        {
            MessageBox.Show(FindForm(), "This recognized line overlaps an editable text note. Close Text actions, choose Move, then double-click the note to edit its original text. No changes were made.",
                "Edit overlapping text", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private Rectangle PaddedTextBounds(RectangleF bounds)
        {
            return Rectangle.Intersect(new Rectangle(Point.Empty, ImageSize), Rectangle.FromLTRB(
                (int)Math.Floor(bounds.Left) - 2, (int)Math.Floor(bounds.Top) - 2,
                (int)Math.Ceiling(bounds.Right) + 2, (int)Math.Ceiling(bounds.Bottom) + 2));
        }

        private Rectangle PaddedTextBounds(RectangleF bounds, RectangleF excludedGraphic)
        {
            return ProtectGraphicPadding(PaddedTextBounds(bounds), bounds, excludedGraphic);
        }

        private Rectangle PaddedTextBounds(RectangleF bounds, RectangleF excludedGraphic, RectangleF recognitionClip)
        {
            Rectangle padded = PaddedTextBounds(bounds, excludedGraphic);
            return recognitionClip.IsEmpty ? padded : Rectangle.Intersect(padded, Rectangle.Ceiling(recognitionClip));
        }

        private static Rectangle ProtectGraphicPadding(Rectangle padded, RectangleF text, RectangleF graphic)
        {
            if (graphic.Width > 0 && graphic.Height > 0 && graphic.Right <= text.Left &&
                graphic.Bottom > padded.Top && graphic.Top < padded.Bottom && graphic.Right > padded.Left)
                return Rectangle.FromLTRB(Math.Min(padded.Right, (int)Math.Ceiling(graphic.Right)),
                    padded.Top, padded.Right, padded.Bottom);
            return padded;
        }

        private float EstimateLineFontSize(string text, RectangleF bounds, float width)
        {
            float size = Math.Max(8, Math.Min(160, bounds.Height * 1.25f));
            using (Bitmap scratch = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(scratch))
            using (Font font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat format = new StringFormat())
            {
                format.FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip;
                format.Trimming = StringTrimming.None;
                float measured = graphics.MeasureString(text ?? "", font, Int32.MaxValue, format).Width;
                if (measured > width && measured > 0) size *= width / measured;
            }
            return Math.Max(8, Math.Min(160, (float)Math.Round(size)));
        }

        // OCR cells are source-space regions, not the broad native RichEdit
        // viewport. Only a distinct cell on the same row limits this column;
        // ordinary words within one cell and vertically adjacent lines do not.
        internal static float RecognizedColumnRight(RectangleF sourceInk, IList<TextLineRegion> lines, float imageWidth)
        {
            if (Single.IsNaN(imageWidth) || Single.IsInfinity(imageWidth) || imageWidth < 1)
                throw new ArgumentOutOfRangeException("imageWidth");
            float right = imageWidth;
            if (lines == null || sourceInk.Width <= 0 || sourceInk.Height <= 0 ||
                !FiniteTextBoxPoint(sourceInk.Location) || !FiniteTextBoxPoint(new PointF(sourceInk.Right, sourceInk.Bottom))) return right;
            foreach (TextLineRegion line in lines)
            {
                if (line == null || line.Bounds.Width <= 0 || line.Bounds.Height <= 0) continue;
                RectangleF other = line.Bounds;
                if (Single.IsNaN(other.Left) || Single.IsInfinity(other.Left) ||
                    Single.IsNaN(other.Top) || Single.IsInfinity(other.Top) ||
                    Single.IsNaN(other.Right) || Single.IsInfinity(other.Right) ||
                    Single.IsNaN(other.Bottom) || Single.IsInfinity(other.Bottom)) continue;
                float overlap = Math.Min(sourceInk.Bottom, other.Bottom) - Math.Max(sourceInk.Top, other.Top);
                if (overlap < Math.Min(sourceInk.Height, other.Height) * .5f || other.Left <= sourceInk.Right) continue;
                float gutter = Math.Min(4, (other.Left - sourceInk.Right) * .25f);
                right = Math.Min(right, other.Left - gutter);
            }
            return Math.Max(Math.Min(imageWidth, sourceInk.Right), right);
        }

        private void SampleTextColors(Rectangle bounds, out Color background, out Color foreground)
        {
            Rectangle region = bounds;
            region.Inflate(2, 2);
            region.Intersect(new Rectangle(Point.Empty, ImageSize));
            using (Bitmap image = document.CopyBaseRegion(region))
            {
                bounds.Offset(-region.Left, -region.Top);
                Dictionary<int, int> edge = new Dictionary<int, int>();
                int strideX = Math.Max(1, bounds.Width / 100), strideY = Math.Max(1, bounds.Height / 30);
                for (int x = bounds.Left; x < bounds.Right; x += strideX)
                { CountColor(edge, image.GetPixel(x, Math.Max(0, bounds.Top - 1))); CountColor(edge, image.GetPixel(x, Math.Min(image.Height - 1, bounds.Bottom))); }
                for (int y = bounds.Top; y < bounds.Bottom; y += strideY)
                { CountColor(edge, image.GetPixel(Math.Max(0, bounds.Left - 1), y)); CountColor(edge, image.GetPixel(Math.Min(image.Width - 1, bounds.Right), y)); }
                background = MostCommon(edge, Color.White);
                Dictionary<int, int> ink = new Dictionary<int, int>();
                for (int y = bounds.Top; y < bounds.Bottom; y += Math.Max(1, bounds.Height / 40))
                    for (int x = bounds.Left; x < bounds.Right; x += Math.Max(1, bounds.Width / 300))
                    {
                        Color sample = image.GetPixel(x, y);
                        if (Math.Abs(sample.R - background.R) + Math.Abs(sample.G - background.G) + Math.Abs(sample.B - background.B) > 150) CountColor(ink, sample);
                    }
                foreground = MostCommon(ink, background.GetBrightness() > 0.5f ? Color.Black : Color.White);
            }
        }

        private static void CountColor(Dictionary<int, int> counts, Color color)
        {
            int value = Color.FromArgb(255, color).ToArgb(), count;
            counts.TryGetValue(value, out count); counts[value] = count + 1;
        }
        private static Color MostCommon(Dictionary<int, int> counts, Color fallback)
        {
            int best = 0;
            foreach (KeyValuePair<int, int> entry in counts) if (entry.Value > best) { best = entry.Value; fallback = Color.FromArgb(entry.Key); }
            return fallback;
        }
        private void RaiseTextStyleChanged()
        {
            EventHandler handler = TextStyleChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void CancelSourceStyleMatching()
        {
            if (sourceStyleCancellation != null) sourceStyleCancellation.Cancel();
        }
    }
}
