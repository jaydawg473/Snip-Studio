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
                TextAnnotationInfo info = HasImage ? document.GetTextAnnotation(selectedIndex) : null;
                return info == null ? textFontSize : info.FontSize;
            }
            set
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 1 || value > 1024) throw new ArgumentOutOfRangeException("value");
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
                int index = textSelection.SelectedLineIndex;
                return !matchingSourceText && textActionsMode && HasImage && recognizedText != null && recognizedText.Available &&
                    index >= 0 && index < recognizedText.Lines.Count &&
                    (!recognizedText.TextAngle.HasValue || Math.Abs(recognizedText.TextAngle.Value) <= 2);
            }
        }
        public bool CanDeleteRecognizedText { get { return CanEditRecognizedLine; } }

        public async void EditRecognizedLine()
        {
            if (!CanEditRecognizedLine) return;
            TextLineRegion line = recognizedText.Lines[textSelection.SelectedLineIndex];
            int owner = FindTextAnnotationForLine(line);
            if (owner == -2) { ExplainMixedTextLine(); return; }
            if (owner >= 0) { EditTextAnnotation(owner); return; }
            CancelGesture();
            Rectangle erase = PaddedTextBounds(line.Bounds);
            if (erase.Width < 12 || erase.Height < 2) return;
            Color background, foreground;
            SampleTextColors(erase, out background, out foreground);
            float width = Math.Min(ImageSize.Width - erase.Left, Math.Max(24, erase.Width));
            float size = EstimateLineFontSize(line.Text, line.Bounds, width);
            int selectionStart, selectionLength;
            bool hasRange = textSelection.TryGetSelectedCharacterRange(line.Text, out selectionStart, out selectionLength);
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
                Rectangle sourceBounds = Rectangle.Intersect(new Rectangle(Point.Empty, ImageSize), Rectangle.Ceiling(line.Bounds));
                using (Bitmap source = document.CopyBaseRegion(sourceBounds))
                {
                    SourceTextStyle style = await Task.Run(delegate { return SourceTextMatcher.Estimate(source,
                        new Rectangle(Point.Empty, source.Size), line.Text, foreground, background, matchingToken); });
                    if (disposingCanvas || matchingToken.IsCancellationRequested || !textActionsMode || !Object.ReferenceEquals(identity, ImageIdentity) || revision != DocumentRevision) return;
                    size = style.FontSizePixels;
                    rtf = RichTextLayout.CreatePlainRtf(line.Text, style.FontFamily, size, style.Style, foreground);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                if (disposingCanvas || matchingToken.IsCancellationRequested || !textActionsMode || !HasImage || !Object.ReferenceEquals(identity, ImageIdentity) || revision != DocumentRevision) return;
                rtf = RichTextLayout.CreatePlainRtf(line.Text, "Segoe UI", size, FontStyle.Regular, foreground);
            }
            finally
            {
                matchingSourceText = false;
                if (Object.ReferenceEquals(sourceStyleCancellation, matching)) sourceStyleCancellation = null;
                matching.Dispose();
                if (!disposingCanvas) RaiseTextSelectionChanged();
            }
            if (disposingCanvas || matchingToken.IsCancellationRequested || !textActionsMode || !HasImage || !Object.ReferenceEquals(identity, ImageIdentity) || revision != DocumentRevision) return;
            using (TextEditorDialog dialog = new TextEditorDialog("Edit recognized line", line.Text, size, width,
                ImageSize.Width - erase.Left, foreground, background, true, rtf))
            {
                if (hasRange) dialog.SelectTextRange(selectionStart, selectionLength);
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                selectedIndex = document.ReplaceImageRichText(erase, dialog.EnteredText, dialog.Rtf, dialog.TextColor, dialog.BackgroundColor,
                    dialog.TextSize, dialog.BoxWidth);
                textFontSize = dialog.TextSize;
                textActionsMode = false;
                tool = EditorTool.Select;
                RaiseTextSelectionChanged();
                UpdateCursor();
                RaiseChanged();
            }
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
            Rectangle erase = PaddedTextBounds(line.Bounds);
            Color background, foreground;
            SampleTextColors(erase, out background, out foreground);
            List<Rectangle> selectedRegions = new List<Rectangle>();
            foreach (RectangleF band in textSelection.SelectedLineBounds()) selectedRegions.Add(PaddedTextBounds(band));
            // Screenshot pixels have no editable font runs. Erase only the
            // selected words; all remaining source typography stays exact.
            if (document.EraseImageRegions(selectedRegions, background)) { selectedIndex = -1; RaiseChanged(); }
        }

        private void AddTextNote(PointF location)
        {
            int available = Math.Max(1, ImageSize.Width - (int)Math.Floor(location.X));
            if (available < 12) return;
            float width = Math.Min(available, Math.Max(24, defaultTextBoxWidth));
            using (TextEditorDialog dialog = new TextEditorDialog("Add text", "", textFontSize, width, available,
                inkColor, Color.White, false))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK || String.IsNullOrWhiteSpace(dialog.EnteredText)) return;
                document.AddRichText(dialog.EnteredText, dialog.Rtf, location, dialog.TextColor, dialog.TextSize, dialog.BoxWidth);
                selectedIndex = document.AnnotationCount - 1;
                textFontSize = dialog.TextSize;
                defaultTextBoxWidth = dialog.BoxWidth;
                RaiseChanged();
            }
        }

        public void EditSelectedText()
        {
            if (textActionsMode) EditRecognizedLine();
            else if (HasSelectedTextAnnotation) EditTextAnnotation(selectedIndex);
        }

        private void EditTextAnnotation(int index)
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
            if (!textActionsMode && (tool == EditorTool.RemoveArea ||
                (tool == EditorTool.Select && IsOnSourceRemovalSelection(e.Location)))) return;
            CancelGesture();
            if (textActionsMode) { EditRecognizedLine(); return; }
            if (tool != EditorTool.Select && tool != EditorTool.Text) return;
            int hit = document.HitTestAnnotation(ToImage(e.Location, false), 3 / zoom);
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
