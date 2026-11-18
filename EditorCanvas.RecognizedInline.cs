using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using CancellationToken = System.Threading.CancellationToken;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private bool inlineRecognizedSource, inlineRecognizedProxy;
        private Rectangle inlineRecognizedErase;
        private Color inlineRecognizedBackground, inlineRecognizedInitialBackground;
        private Region inlineRecognizedRegion;
        private Control inlineRecognizedHost;
        private int recognizedNavigationDepth, annotationNavigationDepth;
        private object annotationNavigationImage;
        private Timer inlineRecognizedCaretTimer;
        private bool inlineRecognizedCaretOn, inlineRecognizedSelecting;
        private string inlineRecognizedRtf;
        private RichTextCaretMap inlineOriginalCaretMap;
        private PointF[] inlineOriginalCaretPoints;
        private int inlineRecognizedSelectionAnchor;
        private Point textClickStart;
        private bool textClickMoved;
        private TextRecognitionResult recognizedSessionResult;
        private TextOverlaySelection recognizedSessionSelection;
        private object recognizedSessionImage;
        private long recognizedSessionRevision;
        private int recognizedSessionLine = -1;
        private RectangleF inlineRecognizedAnnotationInk;
        private readonly HashSet<int> recognizedSessionConsumed = new HashSet<int>();
        private bool recognizedTextToolHistory;
        private PointF? deferredRecognizedTextPoint;
        private object deferredRecognizedTextImage;
        private long deferredRecognizedTextRevision;
        private int deferredRecognizedTextGeneration;
        private SourceTextFamilyContext sourceFamilyContext;
        private object sourceFamilyImage;
        private long sourceFamilyRevision;
        public bool RecognizedTextEditing { get { return InlineTextEditing && inlineRecognizedProxy; } }
        internal bool RecognizedTextTransitionActive { get { return !disposingCanvas && HasImage && tool == EditorTool.Text &&
            ((annotationNavigationDepth > 0 && Object.ReferenceEquals(annotationNavigationImage, ImageIdentity)) ||
                (recognizedNavigationDepth > 0 && RecognizedTextSessionActive)); } }
        public bool RecognizedTextSessionActive
        {
            get { return recognizedSessionResult != null && HasImage &&
                Object.ReferenceEquals(recognizedSessionImage, ImageIdentity) && recognizedSessionRevision == DocumentRevision; }
        }

        private bool CurrentRecognizedTextResult
        {
            get { return HasImage && recognizedText != null && Object.ReferenceEquals(recognizedBase, ImageIdentity) &&
                recognizedRevision == DocumentRevision; }
        }

        public bool RecognizedTextRefreshPending
        {
            get { return tool == EditorTool.Text && recognizedTextToolHistory && !RecognizedTextSessionActive &&
                !CurrentRecognizedTextResult; }
        }

        private bool EnsureRecognizedTextToolSession()
        {
            if (tool != EditorTool.Text) return false;
            if (RecognizedTextSessionActive) return true;
            if (recognizedSessionResult != null) EndRecognizedTextSession();
            if (!CurrentRecognizedTextResult || !recognizedText.Available || recognizedText.Lines.Count == 0) return false;
            if (recognizedText.TextAngle.HasValue && (Double.IsNaN(recognizedText.TextAngle.Value) ||
                Double.IsInfinity(recognizedText.TextAngle.Value) || Math.Abs(recognizedText.TextAngle.Value) > 2)) return false;
            TextOverlaySelection selection = new TextOverlaySelection(recognizedText.Lines);
            if (selection.Count == 0) return false;
            recognizedTextToolHistory = true;
            StartRecognizedTextSession(recognizedText, selection);
            return true;
        }

        private bool HandleRecognizedTextRefreshClick(MouseEventArgs e)
        {
            if (!RecognizedTextRefreshPending || textActionsMode || InlineTextEditing || e.Button != MouseButtons.Left || !IsOnImage(e.Location)) return false;
            RefreshRecognitionIfNeeded();
            deferredRecognizedTextPoint = ToImage(e.Location, false);
            deferredRecognizedTextImage = ImageIdentity;
            deferredRecognizedTextRevision = DocumentRevision;
            deferredRecognizedTextGeneration = recognitionGeneration;
            SetRecognitionStatus("Updating recognized text. The selected line will open when recognition finishes.");
            return true;
        }

        private void ClearDeferredRecognizedTextClick()
        {
            deferredRecognizedTextPoint = null; deferredRecognizedTextImage = null;
        }

        private bool TryConsumeDeferredRecognizedTextClick(out PointF point)
        {
            point = deferredRecognizedTextPoint ?? PointF.Empty;
            bool valid = deferredRecognizedTextPoint.HasValue && !disposingCanvas && tool == EditorTool.Text && !textActionsMode &&
                Object.ReferenceEquals(deferredRecognizedTextImage, ImageIdentity) && deferredRecognizedTextRevision == DocumentRevision &&
                deferredRecognizedTextGeneration == recognitionGeneration && CurrentRecognizedTextResult;
            ClearDeferredRecognizedTextClick();
            return valid;
        }

        private void ActivateDeferredRecognizedTextClick()
        {
            PointF point;
            if (!TryConsumeDeferredRecognizedTextClick(out point) || !RecognizedTextSessionActive) return;
            int annotation = HitTestTextForEditing(point, 3 / zoom);
            if (document.GetTextAnnotation(annotation) != null || recognizedSessionSelection.HitTest(point, Math.Min(8, 3 / zoom)) >= 0)
                NavigateRecognizedSessionAt(point);
        }

        private TextRecognitionResult RecognizedEditResult { get { return textActionsMode ? recognizedText : recognizedSessionResult; } }
        private TextOverlaySelection RecognizedEditSelection { get { return textActionsMode ? textSelection : recognizedSessionSelection; } }

        private void StartRecognizedTextSession(TextRecognitionResult result, TextOverlaySelection selection)
        {
            if (!RecognizedTextSessionActive || !Object.ReferenceEquals(result, recognizedSessionResult))
            {
                recognizedSessionConsumed.Clear();
                recognizedSessionResult = result; recognizedSessionSelection = selection;
                recognizedSessionImage = ImageIdentity; recognizedSessionRevision = DocumentRevision;
            }
        }

        private void EndRecognizedTextSession()
        {
            recognizedSessionResult = null; recognizedSessionSelection = null;
            recognizedSessionImage = null; recognizedSessionLine = -1; recognizedSessionConsumed.Clear();
            ClearDeferredRecognizedTextClick();
            CancelSourceStyleMatching();
        }

        private bool SourceFamilyRecognitionCurrent(TextRecognitionResult result, object image, long revision)
        {
            return !disposingCanvas && HasImage && result != null && result.Available &&
                Object.ReferenceEquals(image, ImageIdentity) && revision == DocumentRevision &&
                ((Object.ReferenceEquals(result, recognizedText) && Object.ReferenceEquals(recognizedBase, image) && recognizedRevision == revision) ||
                (RecognizedTextSessionActive && Object.ReferenceEquals(result, recognizedSessionResult)));
        }

        private async Task<SourceTextFamilyContext> GetSourceFamilyContextAsync(TextRecognitionResult result,
            object image, long revision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!HasImage || !Object.ReferenceEquals(sourceFamilyImage, ImageIdentity) || sourceFamilyRevision != DocumentRevision)
            { sourceFamilyContext = null; sourceFamilyImage = null; }
            if (!SourceFamilyRecognitionCurrent(result, image, revision)) return null;
            if (Object.ReferenceEquals(sourceFamilyImage, image) && sourceFamilyRevision == revision)
                return sourceFamilyContext;
            sourceFamilyContext = null; sourceFamilyImage = null;
            List<TextLineRegion> eligible = new List<TextLineRegion>();
            Rectangle imageBounds = new Rectangle(Point.Empty, ImageSize);
            foreach (TextLineRegion line in result.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (line == null || String.IsNullOrWhiteSpace(line.Text) || line.Text.Length > 160) continue;
                int letters = 0;
                foreach (char character in line.Text) if (Char.IsLetterOrDigit(character)) letters++;
                if (letters < 6 || line.Bounds.Width < 12 || line.Bounds.Height < 5 || line.Bounds.Width > 2048 ||
                    line.Bounds.Height > 128 || !new RectangleF(PointF.Empty, ImageSize).Contains(line.Bounds)) continue;
                Rectangle bounds = Rectangle.Intersect(imageBounds, Rectangle.Ceiling(line.Bounds));
                bool annotation = false;
                for (int i = 0; i < document.AnnotationCount; i++)
                {
                    TextAnnotationInfo note = document.GetTextAnnotation(i);
                    if (note != null && note.InkBounds.IntersectsWith(bounds)) { annotation = true; break; }
                }
                // Previously generated text must never vote for its own guess.
                if (!annotation) eligible.Add(line);
            }
            if (eligible.Count == 0) return null;
            List<TextLineRegion> chosen = new List<TextLineRegion>();
            TextLineRegion longest = null, smallest = null, largest = null;
            foreach (TextLineRegion line in eligible)
            {
                if (longest == null || line.Text.Length > longest.Text.Length) longest = line;
                if (smallest == null || line.Bounds.Height < smallest.Bounds.Height ||
                    (line.Bounds.Height == smallest.Bounds.Height && line.Text.Length > smallest.Text.Length)) smallest = line;
                if (largest == null || line.Bounds.Height > largest.Bounds.Height ||
                    (line.Bounds.Height == largest.Bounds.Height && line.Text.Length > largest.Text.Length)) largest = line;
            }
            chosen.Add(longest);
            if (!chosen.Contains(smallest)) chosen.Add(smallest);
            if (!chosen.Contains(largest)) chosen.Add(largest);
            eligible.Sort(delegate(TextLineRegion a, TextLineRegion b) { return b.Text.Length.CompareTo(a.Text.Length); });
            foreach (TextLineRegion line in eligible)
            { if (chosen.Count >= 3) break; if (!chosen.Contains(line)) chosen.Add(line); }
            Rectangle union = Rectangle.Empty;
            foreach (TextLineRegion line in chosen)
            {
                Rectangle bounds = Rectangle.Intersect(imageBounds, Rectangle.Ceiling(line.Bounds));
                union = union.IsEmpty ? bounds : Rectangle.Union(union, bounds);
            }
            if (union.IsEmpty || (long)union.Width * union.Height > 16L * 1024 * 1024) return null;
            List<SourceTextFamilySample> samples = new List<SourceTextFamilySample>();
            foreach (TextLineRegion line in chosen)
            {
                Rectangle bounds = Rectangle.Intersect(imageBounds, Rectangle.Ceiling(line.Bounds));
                Color background, foreground;
                SampleTextColors(bounds, out background, out foreground);
                using (Bitmap colors = document.CopyBaseRegion(bounds)) foreground = SourceTextColor.Estimate(colors, background, foreground);
                bounds.Offset(-union.Left, -union.Top);
                samples.Add(new SourceTextFamilySample(bounds, line.Text, foreground, background));
            }
            using (Bitmap source = document.CopyBaseRegion(union))
            {
                SourceTextFamilyContext context = await Task.Run(delegate { return SourceTextMatcher.EstimateFamilyContext(source, samples, token); });
                token.ThrowIfCancellationRequested();
                if (!SourceFamilyRecognitionCurrent(result, image, revision)) return null;
                sourceFamilyContext = context; sourceFamilyImage = image; sourceFamilyRevision = revision;
                return context;
            }
        }

        private void AdvanceSourceFamilyContext(object previousImage, long previousRevision)
        {
            if (sourceFamilyContext == null || !Object.ReferenceEquals(sourceFamilyImage, previousImage) ||
                sourceFamilyRevision != previousRevision || !HasImage) return;
            sourceFamilyImage = ImageIdentity; sourceFamilyRevision = DocumentRevision;
        }

        private bool RecognizedEditStateCurrent(TextRecognitionResult result, TextOverlaySelection selection, object image, long revision)
        {
            return !disposingCanvas && HasImage && Object.ReferenceEquals(image, ImageIdentity) && revision == DocumentRevision &&
                ((textActionsMode && Object.ReferenceEquals(result, recognizedText) && Object.ReferenceEquals(selection, textSelection)) ||
                (tool == EditorTool.Text && RecognizedTextSessionActive && Object.ReferenceEquals(result, recognizedSessionResult) && Object.ReferenceEquals(selection, recognizedSessionSelection)));
        }

        private void AdvanceRecognizedTextSession(bool changed)
        {
            // Only transactions from this draft may advance the pinned OCR
            // geometry. Undo/crop/other edits invalidate it rather than guessing.
            if (recognizedSessionResult == null || !Object.ReferenceEquals(recognizedSessionImage, inlineTextImage) ||
                recognizedSessionRevision != inlineTextRevision) return;
            AdvanceSourceFamilyContext(inlineTextImage, inlineTextRevision);
            if (changed)
            {
                if (recognizedSessionLine >= 0) recognizedSessionConsumed.Add(recognizedSessionLine);
                // A single vector note may own several original OCR lines.
                // Editing/deleting it consumes all of those stale source words.
                if (inlineTextIndex >= 0)
                    for (int i = 0; i < recognizedSessionResult.Lines.Count; i++)
                    {
                        RectangleF line = recognizedSessionResult.Lines[i].Bounds;
                        RectangleF overlap = RectangleF.Intersect(line, inlineRecognizedAnnotationInk);
                        if (line.Width > 0 && line.Height > 0 && overlap.Width * overlap.Height >= line.Width * line.Height * .5f)
                            recognizedSessionConsumed.Add(i);
                    }
            }
            recognizedSessionImage = ImageIdentity; recognizedSessionRevision = DocumentRevision; recognizedSessionLine = -1;
        }

        private bool HandleRecognizedSessionMouseDown(MouseEventArgs e)
        {
            if (tool != EditorTool.Text || !RecognizedTextSessionActive || textActionsMode || e.Button != MouseButtons.Left || !IsOnImage(e.Location)) return false;
            PointF point = ToImage(e.Location, false);
            int annotation = HitTestTextForEditing(point, 3 / zoom);
            // Outside the cached words and editable notes, Text still draws a
            // new box. Consumed source words stay suppressed until refreshed.
            if (document.GetTextAnnotation(annotation) == null &&
                recognizedSessionSelection.HitTest(point, Math.Min(8, 3 / zoom)) < 0) return false;
            NavigateRecognizedSessionAt(point);
            return true;
        }

        private bool HandleTextAnnotationNavigation(MouseEventArgs e)
        {
            if (disposingCanvas || !HasImage || (tool != EditorTool.Text && !textActionsMode) ||
                e.Button != MouseButtons.Left || !IsOnImage(e.Location)) return false;
            PointF point = ToImage(e.Location, false);
            // Include the old selection-corner centers just outside its bounds:
            // Text edits at that edge, while Move still owns resize handles.
            int annotation = HitTestTextForEditing(point, 5 / zoom);
            if (document.GetTextAnnotation(annotation) == null) return false;
            if (InlineTextEditing && inlineRecognizedProxy && annotation == inlineTextIndex)
            {
                if (!InlineTextSnapshotCurrent) { CommitInlineText(); return true; }
                if (!HandleRecognizedInlineMouseDown(e))
                { PlaceInlineCaret(point); inlineTextEditor.Focus(); RecognizedInlineSelectionChanged(); }
                return true;
            }
            // Editable notes outlive OCR and its consumed source words. A text
            // click must reach their caret before refresh, move or resize logic.
            object previousNavigationImage = annotationNavigationImage;
            object image = ImageIdentity;
            long revision = DocumentRevision;
            EditorTool entryTool = tool;
            bool entryTextActions = textActionsMode, hadDraft = InlineTextEditing;
            annotationNavigationImage = image; annotationNavigationDepth++;
            try
            {
                if (!CommitInlineText()) return true;
                if (hadDraft) { image = inlineCommittedImage; revision = inlineCommittedRevision; }
                if (disposingCanvas || !HasImage || tool != entryTool || textActionsMode != entryTextActions ||
                    !Object.ReferenceEquals(image, ImageIdentity) || revision != DocumentRevision) return true;
                CancelSourceStyleMatching();
                // Deleting the previous note may have shifted annotation indices.
                annotation = HitTestTextForEditing(point, 5 / zoom);
                if (document.GetTextAnnotation(annotation) != null) BeginRecognizedAnnotationInline(annotation, point);
                return true;
            }
            finally
            {
                annotationNavigationDepth--; annotationNavigationImage = previousNavigationImage;
                if (!disposingCanvas) RaiseInlineTextStateChanged();
            }
        }

        private async void NavigateRecognizedSessionAt(PointF point)
        {
            // Keep the edit command row in place while the previous draft is
            // committed and the next line's font is matched asynchronously.
            recognizedNavigationDepth++;
            try
            {
                if (tool != EditorTool.Text || !CommitInlineText() || !RecognizedTextSessionActive) return;
                CancelSourceStyleMatching();
                int annotation = HitTestTextForEditing(point, 3 / zoom);
                if (document.GetTextAnnotation(annotation) != null)
                { recognizedSessionLine = -1; BeginRecognizedAnnotationInline(annotation, point); return; }
                int word = recognizedSessionSelection.HitTest(point, Math.Min(8, 3 / zoom));
                if (word < 0) return;
                recognizedSessionSelection.Select(word, word);
                int line = recognizedSessionSelection.SelectedLineIndex;
                if (!recognizedSessionConsumed.Contains(line)) await EditRecognizedLineAtAsync(point);
            }
            finally
            {
                recognizedNavigationDepth--;
                if (!disposingCanvas) RaiseInlineTextStateChanged();
            }
        }

        private bool ShouldNavigateRecognizedSession(MouseEventArgs e)
        {
            if (tool != EditorTool.Text || !RecognizedTextSessionActive || textActionsMode || e.Button != MouseButtons.Left || !IsOnImage(e.Location)) return false;
            PointF point = ToImage(e.Location, false);
            int annotation = HitTestTextForEditing(point, 3 / zoom);
            if (annotation != inlineTextIndex && document.GetTextAnnotation(annotation) != null) return true;
            int word = recognizedSessionSelection.HitTest(point, Math.Min(8, 3 / zoom));
            if (word < 0) return false;
            TextOverlaySelection probe = new TextOverlaySelection(recognizedSessionResult.Lines);
            probe.Select(word, word);
            return probe.SelectedLineIndex != recognizedSessionLine && !recognizedSessionConsumed.Contains(probe.SelectedLineIndex);
        }

        private struct RecognizedViewSnapshot
        {
            internal PointF Origin;
            internal float Zoom;
            internal bool FitRequested;
            internal object Image;
            internal long Intent;
        }

        // Move keeps the user's whole annotation frame selectable. Text editing
        // instead prefers real text ink, so a prior wide layout box cannot
        // cover an already edited cell or an untouched source column.
        private int HitTestTextForEditing(PointF point, float radius)
        {
            int frame = document.HitTestAnnotation(point, radius);
            if (document.GetTextAnnotation(frame) == null) return frame;
            for (int index = document.AnnotationCount - 1; index >= 0; index--)
            {
                TextAnnotationInfo text = document.GetTextAnnotation(index);
                if (text == null || text.InkBounds.Width <= 0 || text.InkBounds.Height <= 0) continue;
                RectangleF ink = text.InkBounds;
                ink.Inflate(Math.Min(2, radius), Math.Min(2, radius));
                if (ink.Contains(point)) return index;
            }
            TextRecognitionResult result = RecognizedEditResult;
            TextOverlaySelection selection = RecognizedEditSelection;
            if (result != null && selection != null && (textActionsMode || RecognizedTextSessionActive))
            {
                int word = selection.HitTest(point, Math.Min(8, radius));
                if (word >= 0)
                {
                    TextOverlaySelection probe = new TextOverlaySelection(result.Lines);
                    probe.Select(word, word);
                    int line = probe.SelectedLineIndex;
                    if (line >= 0 && (textActionsMode || !recognizedSessionConsumed.Contains(line))) return -1;
                }
                // After both cells have been edited, their source words are
                // consumed. Whitespace after the shorter adjacent note should
                // still place its end caret rather than select a newer broad
                // frame from another column. Keep original widths/RTF intact.
                int nearest = frame;
                float nearestDistance = Single.MaxValue;
                for (int index = document.AnnotationCount - 1; index >= 0; index--)
                {
                    TextAnnotationInfo text = document.GetTextAnnotation(index);
                    if (text == null || text.InkBounds.Width <= 0 || text.InkBounds.Height <= 0) continue;
                    RectangleF candidate = text.Bounds;
                    candidate.Inflate(radius, radius);
                    RectangleF ink = text.InkBounds;
                    if (!candidate.Contains(point) || point.Y < ink.Top - radius || point.Y > ink.Bottom + radius) continue;
                    float distance = Math.Max(ink.Left - point.X, Math.Max(0, point.X - ink.Right));
                    if (distance < nearestDistance) { nearestDistance = distance; nearest = index; }
                }
                return nearest;
            }
            return frame;
        }

        private PointF CanvasOffsetInWindow()
        {
            PointF offset = PointF.Empty;
            for (Control current = this; current != null; current = current.Parent)
            { offset.X += current.Left; offset.Y += current.Top; }
            return offset;
        }

        private RecognizedViewSnapshot CaptureRecognizedView()
        {
            PointF offset = CanvasOffsetInWindow(), origin = ImageOrigin;
            return new RecognizedViewSnapshot { Origin = new PointF(offset.X + origin.X, offset.Y + origin.Y),
                Zoom = zoom, FitRequested = fit || fitOnWindowResize, Image = ImageIdentity, Intent = viewIntentVersion };
        }

        private void RestoreRecognizedView(RecognizedViewSnapshot snapshot)
        {
            if (!HasImage || !Object.ReferenceEquals(snapshot.Image, ImageIdentity) || snapshot.Intent != viewIntentVersion) return;
            PointF offset = CanvasOffsetInWindow();
            PointF origin = ImageOrigin;
            if (Math.Abs(offset.X + origin.X - snapshot.Origin.X) < .01f &&
                Math.Abs(offset.Y + origin.Y - snapshot.Origin.Y) < .01f && Math.Abs(zoom - snapshot.Zoom) < .00001f)
            {
                // Keep an already stable fitted view fitted. Introducing a pan
                // extent here would add scrollbars even without a chrome change.
                UpdateInlineTextLayout();
                return;
            }
            bool updating = updatingView;
            updatingView = true;
            try
            {
                fit = false; fitOnWindowResize = snapshot.FitRequested; zoom = snapshot.Zoom;
                PositionViewOrigin(new PointF(snapshot.Origin.X - offset.X, snapshot.Origin.Y - offset.Y));
                UpdateInlineTextLayout();
            }
            finally { updatingView = updating; }
            RaiseViewChanged(); Invalidate();
        }

        protected override Point ScrollToControl(Control activeControl)
        {
            // Focusing the invisible keyboard proxy must not scroll the image.
            if (inlineRecognizedProxy && (Object.ReferenceEquals(activeControl, inlineTextEditor) ||
                Object.ReferenceEquals(activeControl, inlineRecognizedHost) ||
                (inlineRecognizedHost != null && inlineRecognizedHost.Contains(activeControl)))) return DisplayRectangle.Location;
            return base.ScrollToControl(activeControl);
        }

        // Native RichEdit supplies keyboard, clipboard and formatting behavior.
        // Its empty region never paints a field over the source screenshot.
        internal bool BeginRecognizedInlineDraft(Rectangle erase, string text, string rtf,
            Color background, float width, float height, int selectionStart, int selectionLength, PointF? caret)
        {
            return BeginRecognizedInlineDraftAt(erase, erase, text, rtf, background, width, height,
                selectionStart, selectionLength, caret);
        }

        private bool BeginRecognizedInlineDraftAt(Rectangle erase, RectangleF sourceInk, string text, string rtf,
            Color background, float width, float height, int selectionStart, int selectionLength, PointF? caret)
        {
            if (!HasImage || disposingCanvas || !CommitInlineText()) return false;
            erase = Rectangle.Intersect(new Rectangle(Point.Empty, ImageSize), erase);
            if (erase.Width < 1 || erase.Height < 1) return false;
            RichTextLayout.ValidateRtf(rtf);
            RichTextMetrics metrics = RichTextLayout.Measure(rtf, width);
            PointF location = new PointF(sourceInk.Left - metrics.InkBounds.Left, sourceInk.Top - metrics.InkBounds.Top);
            RecognizedViewSnapshot view = CaptureRecognizedView();
            inlineRecognizedSource = inlineRecognizedProxy = true;
            inlineRecognizedErase = erase;
            inlineRecognizedBackground = Color.FromArgb(255, background);
            inlineRecognizedInitialBackground = inlineRecognizedBackground;
            try
            {
                textActionsMode = false; EndTextActionFeedback(); tool = EditorTool.Text;
                if (!BeginInlineText(-1, location, width, height, rtf, true)) { ResetRecognizedInlineProxy(); return false; }
                inlineTextEditor.BackColor = inlineRecognizedBackground;
                SelectRecognizedInlineRange(selectionStart, selectionLength, caret);
                inlineTextStatus = "Edit the original line directly. Source font is estimated; Advanced text adjusts formatting. Ctrl+Enter applies; Escape cancels.";
                RaiseTextSelectionChanged(); RaiseInlineTextStateChanged(); UpdateCursor(); RestoreRecognizedView(view);
                return true;
            }
            catch { DisposeInlineText(); ResetRecognizedInlineProxy(); throw; }
        }

        private bool BeginRecognizedAnnotationInline(int index, PointF? caret)
        {
            if (!CommitInlineText()) return false;
            TextAnnotationInfo info = document.GetTextAnnotation(index);
            if (info == null) return false;
            RecognizedViewSnapshot view = CaptureRecognizedView();
            inlineRecognizedProxy = true; inlineRecognizedSource = false; inlineRecognizedErase = Rectangle.Empty;
            inlineRecognizedAnnotationInk = info.InkBounds;
            try
            {
                textActionsMode = false; EndTextActionFeedback(); tool = EditorTool.Text;
                if (!BeginInlineTextEdit(index)) { ResetRecognizedInlineProxy(); return false; }
                SelectRecognizedInlineRange(info.Text.Length, 0, caret);
                RaiseTextSelectionChanged(); UpdateCursor(); RestoreRecognizedView(view); return true;
            }
            catch { DisposeInlineText(); ResetRecognizedInlineProxy(); throw; }
        }

        private void SelectRecognizedInlineRange(int start, int length, PointF? caret)
        {
            if (caret.HasValue) PlaceInlineCaret(caret.Value);
            else
            {
                start = Math.Max(0, Math.Min(inlineTextEditor.TextLength, start));
                inlineTextEditor.Select(start, Math.Max(0, Math.Min(length, inlineTextEditor.TextLength - start)));
            }
            RecognizedInlineSelectionChanged();
        }

        private void InitializeRecognizedInlineProxy(InlineDraftBox draft)
        {
            if (!inlineRecognizedProxy) return;
            inlineRecognizedRegion = new Region(Rectangle.Empty);
            draft.Region = inlineRecognizedRegion;
            // Region.Empty prevents painting, but a direct child still expands
            // ScrollableControl's native extent. A fixed non-scrollable host
            // isolates the full-size RichEdit keyboard layout from image bars.
            // A host added after scrolling must start at the same content origin
            // as one that existed before the scroll. Otherwise native focus sees
            // a newly inserted control at the viewport's top-left.
            inlineRecognizedHost = new Control { Bounds = new Rectangle(AutoScrollPosition, new Size(1, 1)),
                TabStop = false, Region = new Region(Rectangle.Empty) };
            draft.ScrollBars = RichTextBoxScrollBars.None;
            draft.GotFocus += delegate { RecognizedInlineSelectionChanged(); };
            draft.LostFocus += delegate { Invalidate(); };
            inlineRecognizedCaretOn = true;
            int blink = SystemInformation.CaretBlinkTime;
            if (blink > 0)
            {
                inlineRecognizedCaretTimer = new Timer { Interval = Math.Max(100, Math.Min(2000, blink)) };
                inlineRecognizedCaretTimer.Tick += delegate
                {
                    if (!InlineTextEditing || !inlineRecognizedProxy || disposingCanvas) return;
                    inlineRecognizedCaretOn = !inlineRecognizedCaretOn; Invalidate();
                };
                inlineRecognizedCaretTimer.Start();
            }
        }

        private void ResetRecognizedInlineProxy()
        {
            bool release = inlineRecognizedSelecting && Capture;
            inlineRecognizedSelecting = false;
            if (release) Capture = false;
            if (inlineRecognizedCaretTimer != null)
            { inlineRecognizedCaretTimer.Stop(); inlineRecognizedCaretTimer.Dispose(); inlineRecognizedCaretTimer = null; }
            if (inlineRecognizedHost != null)
            {
                Control host = inlineRecognizedHost;
                inlineRecognizedHost = null;
                Region region = host.Region;
                host.Region = null;
                host.Dispose();
                if (region != null) region.Dispose();
            }
            if (inlineRecognizedRegion != null) { inlineRecognizedRegion.Dispose(); inlineRecognizedRegion = null; }
            inlineRecognizedSource = inlineRecognizedProxy = false; inlineRecognizedErase = Rectangle.Empty;
            inlineRecognizedAnnotationInk = RectangleF.Empty;
            inlineRecognizedRtf = null;
            inlineOriginalCaretMap = null; inlineOriginalCaretPoints = null;
        }

        private string RecognizedInlineRtf()
        {
            string current = inlineTextEditor.Rtf;
            // RichTextLayout keys immutable RTF by reference. Preserve that
            // reference between changes so caret paints and hover hit tests
            // reuse the rendered layout instead of rebuilding it every frame.
            if (!String.Equals(current, inlineRecognizedRtf, StringComparison.Ordinal)) inlineRecognizedRtf = current;
            return inlineRecognizedRtf;
        }

        private void RecognizedInlineSelectionChanged()
        {
            if (!inlineRecognizedProxy) return;
            inlineRecognizedCaretOn = true;
            if (inlineRecognizedCaretTimer != null) { inlineRecognizedCaretTimer.Stop(); inlineRecognizedCaretTimer.Start(); }
            Invalidate();
        }

        private void UpdateRecognizedInlineProxyBounds()
        {
            // The fixed host clips this keyboard proxy without contributing its
            // full text bounds to the image's native scrolling extent.
            int width = Math.Max(1, Math.Min(32768, (int)Math.Ceiling(inlineTextWidth * zoom)));
            float height = Math.Max(inlineTextMinimumHeight, ImageSize.Height - inlineTextLocation.Y);
            int pixels = Math.Max(1, Math.Min(32768, (int)Math.Ceiling(height * zoom) + 64));
            PointF origin = ImageOrigin;
            // Native scrolling may move the fixed host. Convert canvas-space
            // placement into its local coordinates so native caret/IME geometry
            // still agrees with the source-space caret drawn over the image.
            Point hostOffset = inlineRecognizedHost == null ? Point.Empty : inlineRecognizedHost.Location;
            inlineTextEditor.SetBounds((int)Math.Floor(origin.X + inlineTextLocation.X * zoom) - hostOffset.X,
                (int)Math.Floor(origin.Y + inlineTextLocation.Y * zoom) - hostOffset.Y, width, pixels);
        }

        private PointF RecognizedInlineCharacterPoint(int index)
        {
            RichTextCaretMap map = RichTextLayout.CaretMap(RecognizedInlineRtf(), inlineTextWidth);
            PointF[] original = OriginalSourceCaretPoints(map);
            int found = Array.BinarySearch(map.Boundaries, map.Normalize(index));
            if (original != null) return original[Math.Max(0, found)];
            PointF point = map.PointAt(index);
            return new PointF(inlineTextLocation.X + point.X, inlineTextLocation.Y + point.Y);
        }

        private PointF[] OriginalSourceCaretPoints(RichTextCaretMap map)
        {
            if (!inlineRecognizedSource || HasPendingInlineTextChanges || !RecognizedTextSessionActive || recognizedSessionLine < 0 ||
                recognizedSessionLine >= recognizedSessionResult.Lines.Count) return null;
            TextLineRegion line = recognizedSessionResult.Lines[recognizedSessionLine];
            if (!String.Equals(line.Text, map.Text, StringComparison.Ordinal) || line.Words.Count == 0) return null;
            if (Object.ReferenceEquals(inlineOriginalCaretMap, map)) return inlineOriginalCaretPoints;
            PointF[] points = new PointF[map.Boundaries.Length];
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(inlineTextLocation.X + map.Points[i].X, inlineTextLocation.Y + map.Points[i].Y);
            int cursor = 0;
            foreach (TextWordRegion word in line.Words)
            {
                if (word == null || String.IsNullOrEmpty(word.Text) || word.Bounds.Width <= 0 || word.Bounds.Height <= 0) continue;
                int first = line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
                if (first < 0) return null;
                int last = first + word.Text.Length;
                PointF a = map.PointAt(first), b = map.PointAt(last);
                float left = Math.Min(a.X, b.X), span = Math.Abs(b.X - a.X);
                for (int i = 0; i < points.Length; i++)
                    if (map.Boundaries[i] >= first && map.Boundaries[i] <= last)
                    {
                        float fraction = span > .01f && Math.Abs(a.Y - b.Y) < 1 ? (map.Points[i].X - left) / span :
                            (float)(map.Boundaries[i] - first) / Math.Max(1, last - first);
                        points[i].X = word.Bounds.Left + Math.Max(0, Math.Min(1, fraction)) * word.Bounds.Width;
                    }
                cursor = last;
            }
            inlineOriginalCaretMap = map; inlineOriginalCaretPoints = points;
            return points;
        }

        private int InlineCharacterAt(PointF imagePoint)
        {
            Point point;
            if (inlineRecognizedProxy)
            {
                RichTextCaretMap map = RichTextLayout.CaretMap(RecognizedInlineRtf(), inlineTextWidth);
                PointF[] original = OriginalSourceCaretPoints(map);
                if (original != null)
                {
                    int nearest = 0; double distance = Double.MaxValue;
                    for (int i = 0; i < original.Length; i++)
                    {
                        double x = imagePoint.X - original[i].X, y = imagePoint.Y - original[i].Y;
                        double current = x * x + y * y;
                        if (current <= distance) { distance = current; nearest = i; }
                    }
                    return map.Boundaries[nearest];
                }
                return map.HitTest(new PointF(imagePoint.X - inlineTextLocation.X, imagePoint.Y - inlineTextLocation.Y));
            }
            else
            {
                PointF origin = ImageOrigin;
                point = new Point((int)Math.Round(origin.X + imagePoint.X * zoom) - inlineTextEditor.Left,
                    (int)Math.Round(origin.Y + imagePoint.Y * zoom) - inlineTextEditor.Top);
            }
            int index = inlineTextEditor.GetCharIndexFromPosition(point);
            if (index < inlineTextEditor.TextLength && inlineTextEditor.Text[index] != '\n')
            {
                Point first = inlineTextEditor.GetPositionFromCharIndex(index);
                Point next = inlineTextEditor.GetPositionFromCharIndex(index + 1);
                if (next.Y == first.Y && point.X >= first.X + Math.Max(1, next.X - first.X) / 2f) index++;
            }
            return index;
        }

        private void PlaceInlineCaret(PointF imagePoint)
        {
            inlineTextEditor.Select(InlineCharacterAt(imagePoint), 0); RecognizedInlineSelectionChanged();
        }

        private RectangleF RecognizedInlineHitBounds()
        {
            RichTextMetrics metrics = RichTextLayout.Measure(RecognizedInlineRtf(), inlineTextWidth);
            // Ordinary hand-drawn notes retain their explicit editing frame.
            // Cached OCR edits use their ink/caret and original source band so
            // hidden layout height/whitespace cannot cover other source cells.
            if (!inlineRecognizedSource && !RecognizedTextSessionActive)
                return RectangleF.Intersect(new RectangleF(PointF.Empty, ImageSize), new RectangleF(inlineTextLocation,
                    new SizeF(inlineTextWidth, Math.Max(metrics.Size.Height, inlineTextMinimumHeight))));
            RectangleF bounds = metrics.InkBounds;
            bounds.Offset(inlineTextLocation);
            PointF end = RecognizedInlineCharacterPoint(inlineTextEditor.TextLength);
            RectangleF caret = new RectangleF(end.X - 2, end.Y, 4, Math.Max(1, InlineTextFontSize * 1.3f));
            bounds = bounds.Width > 0 && bounds.Height > 0 ? RectangleF.Union(bounds, caret) : caret;
            if (inlineRecognizedSource) bounds = RectangleF.Union(bounds, inlineRecognizedErase);
            return RectangleF.Intersect(new RectangleF(PointF.Empty, ImageSize), bounds);
        }

        private bool HandleRecognizedInlineMouseDown(MouseEventArgs e)
        {
            if (!InlineTextEditing || !inlineRecognizedProxy || !InlineTextSnapshotCurrent || !IsOnImage(e.Location)) return false;
            PointF point = ToImage(e.Location, false);
            if (!RecognizedInlineHitBounds().Contains(point)) return false;
            if (e.Button == MouseButtons.Right)
            { inlineTextEditor.Focus(); inlineTextEditor.ContextMenuStrip.Show(this, e.Location); return true; }
            if (e.Button != MouseButtons.Left) return false;
            inlineRecognizedSelectionAnchor = InlineCharacterAt(point);
            inlineTextEditor.Select(inlineRecognizedSelectionAnchor, 0);
            inlineRecognizedSelecting = true; Capture = true; Cursor = Cursors.IBeam;
            inlineTextEditor.Focus(); RecognizedInlineSelectionChanged(); return true;
        }

        private bool HandleRecognizedInlineMouseMove(MouseEventArgs e)
        {
            if (!InlineTextEditing || !inlineRecognizedProxy || !InlineTextSnapshotCurrent) return false;
            if (inlineRecognizedSelecting)
            {
                int end = InlineCharacterAt(ToImage(e.Location, false));
                inlineTextEditor.Select(Math.Min(inlineRecognizedSelectionAnchor, end), Math.Abs(end - inlineRecognizedSelectionAnchor));
                Cursor = Cursors.IBeam; RecognizedInlineSelectionChanged(); return true;
            }
            if (IsOnImage(e.Location) && RecognizedInlineHitBounds().Contains(ToImage(e.Location, false)))
            { Cursor = Cursors.IBeam; return true; }
            return false;
        }

        private bool HandleRecognizedInlineMouseUp(MouseEventArgs e)
        {
            if (!InlineTextEditing || !inlineRecognizedProxy || !inlineRecognizedSelecting || e.Button != MouseButtons.Left) return false;
            HandleRecognizedInlineMouseMove(e);
            inlineRecognizedSelecting = false;
            if (Capture) Capture = false;
            inlineTextEditor.Focus(); RecognizedInlineSelectionChanged(); return true;
        }

        private bool HandleRecognizedInlineDoubleClick(MouseEventArgs e)
        {
            if (!InlineTextEditing || !inlineRecognizedProxy || !InlineTextSnapshotCurrent || !IsOnImage(e.Location) ||
                !RecognizedInlineHitBounds().Contains(ToImage(e.Location, false))) return false;
            string value = inlineTextEditor.Text;
            int at = InlineCharacterAt(ToImage(e.Location, false)), first = at, last = at;
            while (first > 0 && !Char.IsWhiteSpace(value[first - 1])) first--;
            while (last < value.Length && !Char.IsWhiteSpace(value[last])) last++;
            inlineTextEditor.Select(first, last - first); inlineRecognizedSelecting = false;
            if (Capture) Capture = false;
            inlineTextEditor.Focus(); RecognizedInlineSelectionChanged(); return true;
        }

        private void DrawRecognizedInlineSelection(Graphics graphics)
        {
            int start = inlineTextEditor.SelectionStart, end = start + inlineTextEditor.SelectionLength;
            float height = Math.Max(1, InlineTextFontSize * 1.3f);
            using (Brush selection = new SolidBrush(Color.FromArgb(70, 0, 102, 204)))
                for (int index = start; index < end; index++)
                {
                    PointF first = RecognizedInlineCharacterPoint(index), next = RecognizedInlineCharacterPoint(index + 1);
                    float width = Math.Abs(next.Y - first.Y) < .5f && next.X > first.X ? next.X - first.X : Math.Max(2, height * .3f);
                    graphics.FillRectangle(selection, first.X, first.Y, width, height);
                }
            if (start == end && inlineRecognizedCaretOn && inlineTextEditor.Focused)
            {
                PointF point = RecognizedInlineCharacterPoint(start);
                using (Pen caret = new Pen(RichTextLayout.GetFirstColor(RecognizedInlineRtf()), 1f / zoom))
                    graphics.DrawLine(caret, point.X, point.Y, point.X, point.Y + height);
            }
        }

        private void DrawInlineTextFrame(Graphics graphics)
        {
            if (!InlineTextEditing || !InlineTextSnapshotCurrent) return;
            if (inlineRecognizedProxy)
            {
                if (HasPendingInlineTextChanges)
                {
                    if (inlineRecognizedSource)
                        using (Brush background = new SolidBrush(inlineRecognizedBackground)) graphics.FillRectangle(background, inlineRecognizedErase);
                    RichTextLayout.Draw(graphics, RecognizedInlineRtf(), inlineTextWidth, inlineTextLocation);
                }
                DrawRecognizedInlineSelection(graphics); return;
            }
            Rectangle frame = inlineTextEditor.Bounds;
            PointF origin = ImageOrigin;
            RectangleF sourceFrame = new RectangleF((frame.Left - origin.X) / zoom, (frame.Top - origin.Y) / zoom,
                frame.Width / zoom, frame.Height / zoom);
            using (Pen outline = new Pen(Color.FromArgb(0, 103, 192), 1f / zoom))
            {
                outline.DashStyle = DashStyle.Dot;
                graphics.DrawRectangle(outline, sourceFrame.X - 1 / zoom, sourceFrame.Y - 1 / zoom,
                    sourceFrame.Width + 2 / zoom, sourceFrame.Height + 2 / zoom);
            }
        }
    }
}
