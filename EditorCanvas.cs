using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas : ScrollableControl
    {
        private readonly EditorDocument document = new EditorDocument();
        private EditorTool tool = EditorTool.Pen;
        private Color inkColor = Color.FromArgb(226, 64, 70);
        private float strokeWidth = 3;
        private float zoom = 1;
        private bool fit = true;
        private bool drawing;
        private bool panning;
        private bool erasing;
        private bool moving;
        private int selectedIndex = -1;
        private PointF moveStart;
        private PointF moveDelta;
        private bool snapping;
        private bool snapHighlightToText = true;
        private TextRecognitionResult recognizedText;
        private TextOverlaySelection textSelection = new TextOverlaySelection(null);
        private bool textActionsMode;
        private bool textSelectionDragging;
        private int textSelectionAnchor = -1;
        private object recognizedBase;
        private CancellationTokenSource recognitionCancellation;
        private int recognitionGeneration;
        private bool disposingCanvas;
        private string recognitionStatus = "Capture an image to find text.";
        private PointF start;
        private PointF end;
        private Point lastPan;
        private readonly List<PointF> points = new List<PointF>();
        private EditorDocument.Annotation preview;
        private readonly ToolTip tooltip = new ToolTip();
        private const int CanvasMargin = 32;

        public event EventHandler DocumentChanged;
        public event EventHandler TextRecognitionStatusChanged;
        public event EventHandler TextSelectionChanged;
        public bool TextActionsMode
        {
            get { return textActionsMode; }
            set
            {
                if (textActionsMode == value) return;
                CancelGesture();
                textActionsMode = value;
                if (value) BeginTextActionFeedback();
                else { EndTextActionFeedback(); CancelSourceStyleMatching(); }
                selectedIndex = -1;
                if (!value) textSelection.Clear();
                UpdateCursor();
                RaiseTextSelectionChanged();
                RaiseTextStyleChanged();
                RefreshRecognitionIfNeeded();
                Invalidate();
            }
        }
        public string AllRecognizedText { get { return textSelection.AllText; } }
        public string SelectedRecognizedText { get { return textSelection.SelectedText; } }
        public bool HasSelectedRecognizedText { get { return textSelection.HasSelection; } }
        public long DocumentRevision { get { return document.Revision; } }
        public object ImageIdentity { get { return document.BaseIdentity; } }
        public bool SnapHighlightToText { get { return snapHighlightToText; } set { snapHighlightToText = value; RefreshRecognitionIfNeeded(); } }
        public string TextRecognitionStatus { get { return recognitionStatus; } }
        public bool TextSnapReady { get { return recognizedText != null && recognizedText.Available && recognizedText.Lines.Count > 0 && (!recognizedText.TextAngle.HasValue || Math.Abs(recognizedText.TextAngle.Value) <= 2); } }
        public bool HasImage { get { return document.HasImage; } }
        public bool CanUndo { get { return document.CanUndo; } }
        private bool requireCtrlForWheelZoom = true;
        public bool RequireCtrlForWheelZoom { get { return requireCtrlForWheelZoom; } set { requireCtrlForWheelZoom = value; } }
        public bool CanRedo { get { return document.CanRedo; } }
        public bool IsDirty { get { return document.IsDirty; } }
        public Size ImageSize { get { return document.ImageSize; } }
        public float Zoom { get { return zoom; } }
        public EditorTool Tool
        {
            get { return tool; }
            set
            {
                if (tool == value) return;
                bool keepRemoval = !textActionsMode && HasSelectedRemovalArea &&
                    ((tool == EditorTool.RemoveArea && value == EditorTool.Select) ||
                     (tool == EditorTool.Select && value == EditorTool.RemoveArea));
                CancelGesture(keepRemoval);
                selectedIndex = -1;
                tool = value;
                UpdateCursor();
                RaiseTextStyleChanged();
                RaiseToolChanged();
                RefreshRecognitionIfNeeded();
                Invalidate();
            }
        }
        public Color InkColor
        {
            get { return inkColor; }
            set
            {
                inkColor = value;
                TextAnnotationInfo selectedText = HasImage && !textActionsMode ? document.GetTextAnnotation(selectedIndex) : null;
                if (selectedText != null && document.UpdateTextAnnotationColor(selectedIndex, value)) RaiseChanged();
                else Invalidate();
            }
        }
        public float StrokeWidth
        {
            get { return strokeWidth; }
            set
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 1 || value > 100)
                    throw new ArgumentOutOfRangeException("value");
                strokeWidth = value;
            }
        }

        public EditorCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            BackColor = Color.FromArgb(20, 26, 36);
            TabStop = true;
            Font = new Font("Segoe UI", 10);
            AccessibleName = "Screenshot editing canvas";
            tooltip.SetToolTip(this, "Use the wheel to scroll, or Ctrl+wheel to zoom around the pointer (configurable in Settings). Move selects annotations and pending removal boxes. Middle button pans; Shift+wheel scrolls horizontally. Shift+highlight draws freehand. Escape cancels.");
            TooltipService.RegisterProvider(tooltip);
            InitializeToolPointers();
            UpdateCursor();
        }

        public void SetImage(Bitmap image)
        {
            ChangeViewIntent();
            StopViewAnimations();
            document.SetImage(image);
            CancelGesture();
            selectedIndex = -1;
            fit = true;
            firstImagePainted = false;
            zoomOriginOffset = PointF.Empty;
            RecalculateFit();
            AutoScrollPosition = Point.Empty;
            RaiseChanged();
        }

        public Bitmap RenderImage() { return document.RenderImage(); }
        public void MarkSaved() { document.MarkSaved(); RaiseChanged(); }
        public void Undo()
        {
            CancelGesture();
            if (!CanUndo) return;
            document.Undo();
            selectedIndex = -1;
            if (fit) RecalculateFit(); else UpdateCanvasSize();
            RaiseChanged();
        }
        public void Redo()
        {
            CancelGesture();
            if (!CanRedo) return;
            document.Redo();
            selectedIndex = -1;
            if (fit) RecalculateFit(); else UpdateCanvasSize();
            RaiseChanged();
        }

        public void FitToWindow()
        {
            ChangeViewIntent();
            CancelGesture();
            fit = true;
            zoomOriginOffset = PointF.Empty;
            RecalculateFit();
            AutoScrollPosition = Point.Empty;
            RaiseViewChanged();
        }
        public void ActualSize() { SetZoom(1f); }
        public void ZoomIn() { ZoomBy(1.25f, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); }
        public void ZoomOut() { ZoomBy(0.8f, new Point(ClientSize.Width / 2, ClientSize.Height / 2)); }

        private void SetZoom(float value)
        {
            if (!HasImage) return;
            ChangeViewIntent();
            Point center = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
            PointF imagePoint = ToImage(center, false);
            CancelGesture();
            fit = false;
            ApplyZoom(value, center, imagePoint);
        }

        private PointF ImageOrigin
        {
            get
            {
                if (zoomViewActive) return zoomViewOrigin;
                float width = ImageSize.Width * zoom, height = ImageSize.Height * zoom;
                float x = fit ? (ClientSize.Width - width) / 2f : CanvasMargin + AutoScrollPosition.X + zoomOriginOffset.X;
                float y = fit ? (ClientSize.Height - height) / 2f : CanvasMargin + AutoScrollPosition.Y + zoomOriginOffset.Y;
                return new PointF(x, y);
            }
        }

        private PointF ToImage(Point point, bool clamp)
        {
            PointF origin = ImageOrigin;
            float x = (point.X - origin.X) / zoom, y = (point.Y - origin.Y) / zoom;
            if (clamp)
            {
                // Crop coordinates are edges (right/bottom are exclusive); ink coordinates are pixels.
                x = Math.Max(0, Math.Min(tool == EditorTool.Crop ? ImageSize.Width : ImageSize.Width - 1, x));
                y = Math.Max(0, Math.Min(tool == EditorTool.Crop ? ImageSize.Height : ImageSize.Height - 1, y));
            }
            return new PointF(x, y);
        }

        private bool IsOnImage(Point point)
        {
            if (!HasImage) return false;
            PointF image = ToImage(point, false);
            return image.X >= 0 && image.Y >= 0 && image.X < ImageSize.Width && image.Y < ImageSize.Height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            if (!HasImage)
            {
                DrawEmpty(graphics);
                return;
            }
            firstImagePainted = true;
            PointF origin = ImageOrigin;
            float width = ImageSize.Width * zoom, height = ImageSize.Height * zoom;
            using (Brush shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                graphics.FillRectangle(shadow, origin.X + 5, origin.Y + 7, width, height);
            using (Brush white = new SolidBrush(Color.White))
                graphics.FillRectangle(white, origin.X, origin.Y, width, height);
            GraphicsState state = graphics.Save();
            try
            {
                graphics.TranslateTransform(origin.X, origin.Y);
                graphics.ScaleTransform(zoom, zoom);
                graphics.SetClip(new Rectangle(0, 0, ImageSize.Width, ImageSize.Height), CombineMode.Intersect);
                // A checker behind transparent areas is only a preview; exports preserve alpha.
                DrawChecker(graphics);
                // Whole-pixel enlargement stays crisp. Fractional zoom needs
                // interpolation so text strokes do not acquire uneven widths.
                bool wholePixelScale = zoom >= 1 && Math.Abs(zoom - (float)Math.Round(zoom)) < 0.001f;
                graphics.InterpolationMode = ViewAnimationActive || panning ? InterpolationMode.Bilinear : wholePixelScale ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                document.Draw(graphics, moving ? selectedIndex : -1, moveDelta, AnnotationPreviewIndex);
                if (preview != null) preview.Draw(graphics);
                if (drawing && tool == EditorTool.Crop) DrawCrop(graphics);
                if (selectedIndex >= 0 && selectedIndex < document.AnnotationCount)
                {
                    GraphicsState selectionState = graphics.Save();
                    graphics.ResetClip();
                    DrawSelection(graphics);
                    graphics.Restore(selectionState);
                }
                if (textActionsMode) DrawTextOverlay(graphics);
                DrawSourceRemoval(graphics);
            }
            finally { graphics.Restore(state); }
            using (Pen border = new Pen(Color.FromArgb(59, 73, 94)))
                graphics.DrawRectangle(border, origin.X - 1, origin.Y - 1, width + 1, height + 1);
            DrawTextScanFeedback(graphics, origin, width, height);
        }

        private void DrawChecker(Graphics graphics)
        {
            EnsureCheckerBrush();
            graphics.FillRectangle(checkerBrush, 0, 0, ImageSize.Width, ImageSize.Height);
        }

        public string EmptyMessage { get; set; }

        private void DrawEmpty(Graphics graphics)
        {
            using (Font title = new Font("Segoe UI", ClientSize.Height < 170 ? 11f : 15f, FontStyle.Regular))
            using (Brush ink = new SolidBrush(StudioTheme.Text))
            using (StringFormat centered = new StringFormat())
            {
                centered.Alignment = StringAlignment.Center;
                centered.LineAlignment = StringAlignment.Center;
                graphics.DrawString(EmptyMessage ?? "Choose New, open an image, or paste from the clipboard.", title, ink,
                    new RectangleF(12, 0, Math.Max(1, ClientSize.Width - 24), Math.Max(1, ClientSize.Height)), centered);
            }
        }

        private void DrawCrop(Graphics graphics)
        {
            RectangleF crop = BoundsBetween(start, end);
            using (Brush shade = new SolidBrush(Color.FromArgb(100, 15, 23, 42)))
            using (Region outside = new Region(new Rectangle(0, 0, ImageSize.Width, ImageSize.Height)))
            using (Pen line = new Pen(Color.FromArgb(0, 114, 206), 1.5f / zoom))
            {
                outside.Exclude(crop);
                graphics.FillRegion(shade, outside);
                graphics.DrawRectangle(line, crop.X, crop.Y, crop.Width, crop.Height);
            }
        }

        private void DrawTextOverlay(Graphics graphics)
        {
            if (!textSelection.HasSelection) return;
            // Preserve the screenshot's contrast and original type. Only selected
            // text receives an adorner; OCR boxes never outline or clip glyphs.
            // Join words by their OCR line so spaces do not become separate tiles.
            using (Pen underline = new Pen(Color.FromArgb(0, 102, 204), 2f / zoom))
            using (Brush highlight = new SolidBrush(Color.FromArgb(80, 0, 102, 204)))
            {
                foreach (RectangleF selectedLine in textSelection.SelectedLineBounds())
                {
                    RectangleF band = selectedLine;
                    band.Inflate(2 / zoom, 2 / zoom);
                    graphics.FillRectangle(highlight, band);
                    graphics.DrawLine(underline, band.Left, band.Bottom, band.Right, band.Bottom);
                }
            }
        }

        private void DrawSelection(Graphics graphics)
        {
            if (DrawSpecialAnnotationSelection(graphics)) return;
            RectangleF bounds = document.GetAnnotationBounds(selectedIndex);
            if (moving) bounds.Offset(moveDelta);
            bounds.Inflate(4 / zoom, 4 / zoom);
            using (Pen line = new Pen(Color.FromArgb(77, 163, 255), 1.3f / zoom))
            using (Brush handle = new SolidBrush(Color.FromArgb(77, 163, 255)))
            {
                line.DashStyle = DashStyle.Dash;
                graphics.DrawRectangle(line, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                float side = 5 / zoom;
                graphics.FillRectangle(handle, bounds.Left - side / 2, bounds.Top - side / 2, side, side);
                graphics.FillRectangle(handle, bounds.Right - side / 2, bounds.Top - side / 2, side, side);
                graphics.FillRectangle(handle, bounds.Left - side / 2, bounds.Bottom - side / 2, side, side);
                graphics.FillRectangle(handle, bounds.Right - side / 2, bounds.Bottom - side / 2, side, side);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            StopViewAnimations();
            Focus();
            if (!HasImage) return;
            if (e.Button == MouseButtons.Right) { HandleAnnotationRightClick(e); return; }
            if (BeginSourceRemoval(e)) return;
            if (TryBeginAnnotationHandleDrag(e)) return;
            if (textActionsMode && e.Button == MouseButtons.Left)
            {
                int hit = IsOnImage(e.Location) ? textSelection.HitTest(ToImage(e.Location, false), Math.Min(8, 3 / zoom)) : -1;
                if (hit >= 0)
                {
                    textSelectionAnchor = hit;
                    textSelection.Select(hit, hit);
                    textSelectionDragging = true;
                    Capture = true;
                    Cursor = Cursors.IBeam;
                }
                else { textSelection.Clear(); textSelectionAnchor = -1; }
                RaiseTextSelectionChanged();
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left && tool == EditorTool.Select)
            {
                selectedIndex = IsOnImage(e.Location) ? document.HitTestAnnotation(ToImage(e.Location, false), 5 / zoom) : -1;
                RaiseTextStyleChanged();
                if (selectedIndex >= 0)
                {
                    moving = true; moveStart = ToImage(e.Location, false); moveDelta = PointF.Empty;
                    Capture = true; Cursor = Cursors.SizeAll; Invalidate(); return;
                }
                // Pending removal boxes are handled above. The captured bitmap
                // remains background, not an object selected by a blank click.
                UpdateCursor();
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Middle)
            {
                BeginPanView();
                panning = true;
                lastPan = e.Location;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button != MouseButtons.Left || !IsOnImage(e.Location)) return;
            selectedIndex = -1;
            start = end = ToImage(e.Location, true);
            if (tool == EditorTool.Text)
            {
                int textHit = document.HitTestAnnotation(start, 3 / zoom);
                if (document.GetTextAnnotation(textHit) != null) { selectedIndex = textHit; EditTextAnnotation(textHit); }
                else AddTextNote(start);
                return;
            }
            if (tool == EditorTool.Eraser)
            {
                erasing = true;
                Capture = true;
                if (document.Erase(start, Math.Max(5 / zoom, strokeWidth))) RaiseChanged();
                return;
            }
            drawing = true;
            snapping = tool == EditorTool.Highlighter && snapHighlightToText && TextSnapReady &&
                (ModifierKeys & Keys.Shift) == 0 && TextSnapSelection.CanStart(recognizedText.Lines, start, Math.Min(20, 8 / zoom));
            Capture = true;
            points.Clear();
            points.Add(start);
            UpdatePreview();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (textActionsMode && !panning)
            {
                if (textSelectionDragging)
                {
                    int nearest = textSelection.Nearest(ToImage(e.Location, false));
                    if (nearest >= 0) textSelection.Select(textSelectionAnchor, nearest);
                    RaiseTextSelectionChanged();
                    Invalidate();
                }
                Cursor = IsOnImage(e.Location) && textSelection.HitTest(ToImage(e.Location, false), Math.Min(8, 3 / zoom)) >= 0
                    ? Cursors.IBeam : Cursors.Default;
                return;
            }
            if (UpdateAnnotationHandleDrag(e)) return;
            if (moving)
            {
                PointF position = ToImage(e.Location, false);
                moveDelta = document.ConstrainMove(selectedIndex, new PointF(position.X - moveStart.X, position.Y - moveStart.Y));
                Invalidate(); return;
            }
            if (panning)
            {
                PanView(e.X - lastPan.X, e.Y - lastPan.Y);
                lastPan = e.Location;
                Invalidate();
                return;
            }
            if (!HasImage) return;
            if (UpdateSourceRemoval(e)) return;
            if (erasing)
            {
                if (document.Erase(ToImage(e.Location, true), Math.Max(5 / zoom, strokeWidth))) RaiseChanged();
                return;
            }
            if (!drawing)
            {
                if (TrySetAnnotationHandleCursor(e.Location)) return;
                if (tool == EditorTool.Select) UpdateSelectPointer(e.Location);
                return;
            }
            end = ToImage(e.Location, true);
            if ((tool == EditorTool.Pen || tool == EditorTool.Highlighter) &&
                EditorDocument.DistanceSquared(points[points.Count - 1], end) >= 0.25f)
                points.Add(end);
            UpdatePreview();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (CompleteSourceRemoval(e)) return;
            if (textSelectionDragging && e.Button == MouseButtons.Left)
            {
                int nearest = textSelection.Nearest(ToImage(e.Location, false));
                if (nearest >= 0) textSelection.Select(textSelectionAnchor, nearest);
                textSelectionDragging = false;
                Capture = false;
                UpdateCursor();
                RaiseTextSelectionChanged();
                Invalidate();
                return;
            }
            if (CompleteAnnotationHandleDrag(e)) return;
            if (moving && e.Button == MouseButtons.Left)
            {
                PointF position = ToImage(e.Location, false);
                PointF delta = document.ConstrainMove(selectedIndex, new PointF(position.X - moveStart.X, position.Y - moveStart.Y));
                moving = false; moveDelta = PointF.Empty; Capture = false; UpdateCursor();
                if (document.MoveAnnotation(selectedIndex, delta)) RaiseChanged(); else Invalidate();
                return;
            }
            if (panning)
            {
                panning = false; Capture = false; UpdateCursor(); Invalidate(); return;
            }
            if (erasing)
            {
                erasing = false; Capture = false; return;
            }
            if (!drawing || e.Button != MouseButtons.Left) return;
            end = ToImage(e.Location, true);
            drawing = false;
            Capture = false;
            preview = null;
            try
            {
                if (tool == EditorTool.Pen || tool == EditorTool.Highlighter)
                {
                    if (EditorDocument.DistanceSquared(points[points.Count - 1], end) >= 0.25f) points.Add(end);
                    List<RectangleF> bands = GetSnapBands();
                    if (bands.Count > 0) document.AddHighlightBands(bands, inkColor);
                    else document.AddStroke(points, inkColor, EffectiveWidth, tool == EditorTool.Highlighter);
                }
                else if (tool == EditorTool.Arrow) document.AddArrow(start, end, inkColor, strokeWidth);
                else if (tool == EditorTool.Rectangle) document.AddRectangle(BoundsBetween(start, end), inkColor, strokeWidth);
                else if (tool == EditorTool.Crop)
                {
                    RectangleF bounds = BoundsBetween(start, end);
                    Rectangle crop = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                        Math.Min(ImageSize.Width, (int)Math.Ceiling(bounds.Right)),
                        Math.Min(ImageSize.Height, (int)Math.Ceiling(bounds.Bottom)));
                    if (crop.Width >= 2 && crop.Height >= 2)
                    {
                        ChangeViewIntent();
                        document.Crop(crop);
                        selectedIndex = -1;
                        fit = true;
                        RecalculateFit();
                        AutoScrollPosition = Point.Empty;
                    }
                }
                RaiseChanged();
            }
            finally { points.Clear(); Invalidate(); }
        }

        private float EffectiveWidth { get { return tool == EditorTool.Highlighter ? strokeWidth * 5 : strokeWidth; } }

        private void UpdatePreview()
        {
            List<RectangleF> bands = GetSnapBands();
            if (bands.Count > 0)
                preview = new EditorDocument.HighlightBandsAnnotation(bands.ToArray(), Color.FromArgb(80, inkColor.R, inkColor.G, inkColor.B));
            else if (tool == EditorTool.Pen || tool == EditorTool.Highlighter)
                preview = new EditorDocument.StrokeAnnotation(points.ToArray(), inkColor, EffectiveWidth, tool == EditorTool.Highlighter);
            else if (tool == EditorTool.Arrow)
                preview = new EditorDocument.ArrowAnnotation(start, end, inkColor, strokeWidth);
            else if (tool == EditorTool.Rectangle)
                preview = new EditorDocument.RectangleAnnotation(BoundsBetween(start, end), inkColor, strokeWidth);
            else preview = null;
        }

        private List<RectangleF> GetSnapBands()
        {
            return snapping && TextSnapReady ? TextSnapSelection.BuildBands(recognizedText.Lines, start, end, Math.Min(20, 8 / zoom)) : new List<RectangleF>();
        }

        private static RectangleF BoundsBetween(PointF a, PointF b)
        {
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && textSelectionDragging) { textSelectionDragging = false; UpdateCursor(); }
            if (!Capture && (drawing || panning || erasing || moving || removalDragging || removalMoving || AnnotationHandleDragging)) CancelGesture();
        }

        private void CancelGesture()
        {
            CancelGesture(false);
        }

        private void CancelGesture(bool preserveRemovalSelection)
        {
            StopViewAnimations();
            CancelAnnotationHandleDrag();
            CancelSourceRemoval(preserveRemovalSelection);
            drawing = false; panning = false; erasing = false; moving = false; snapping = false; moveDelta = PointF.Empty; preview = null;
            textSelectionDragging = false;
            points.Clear();
            if (Capture) Capture = false;
            UpdateCursor();
            Invalidate();
        }

        private void UpdateCursor()
        {
            UpdateToolPointer();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (HandleSourceRemovalKey(keyData)) return true;
            if (keyData == Keys.F2) { EditSelectedText(); return true; }
            if (textActionsMode && keyData == Keys.Delete) { DeleteRecognizedText(); return true; }
            if (textActionsMode && keyData == Keys.Escape)
            {
                if (textSelectionDragging || textSelection.HasSelection)
                {
                    CancelGesture(); textSelection.Clear(); RaiseTextSelectionChanged(); Invalidate();
                }
                else TextActionsMode = false;
                return true;
            }
            if (textActionsMode && keyData == (Keys.Control | Keys.A))
            {
                if (textSelection.Count > 0) textSelection.Select(0, textSelection.Count - 1);
                RaiseTextSelectionChanged(); Invalidate(); return true;
            }
            if (textActionsMode && (keyData & (Keys.Control | Keys.Alt)) == 0 && keyData != Keys.Tab)
                return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Escape && (drawing || panning || erasing || moving || AnnotationHandleDragging)) { CancelGesture(); return true; }
            if (keyData == Keys.Escape && selectedIndex >= 0) { selectedIndex = -1; RaiseTextStyleChanged(); Invalidate(); return true; }
            if (tool == EditorTool.Select && selectedIndex >= 0 && (keyData & (Keys.Control | Keys.Alt)) == 0)
            {
                Keys code = keyData & Keys.KeyCode;
                float amount = (keyData & Keys.Shift) != 0 ? 10 : 1;
                PointF delta = PointF.Empty;
                if (code == Keys.Left) delta.X = -amount; else if (code == Keys.Right) delta.X = amount;
                else if (code == Keys.Up) delta.Y = -amount; else if (code == Keys.Down) delta.Y = amount;
                else if (code == Keys.Delete)
                {
                    document.RemoveAnnotation(selectedIndex); selectedIndex = -1; RaiseChanged(); return true;
                }
                if (delta != PointF.Empty) { document.MoveAnnotation(selectedIndex, delta); RaiseChanged(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!HasImage) { base.OnMouseWheel(e); return; }
            if (RequireCtrlForWheelZoom && (ModifierKeys & Keys.Control) == 0)
                ScrollByWheel(e.Delta, (ModifierKeys & Keys.Shift) != 0);
            else ZoomBy((float)ViewMotion.WheelZoomFactor(e.Delta), e.Location);
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }

        protected override void WndProc(ref Message message)
        {
            const int MouseHorizontalWheel = 0x020E;
            if (message.Msg == MouseHorizontalWheel && HasImage)
            {
                int delta = unchecked((short)((message.WParam.ToInt64() >> 16) & 0xffff));
                ScrollByWheel(-delta, true);
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            if (!updatingView && preserveLayoutDepth == 0) ChangeViewIntent();
            if (!updatingView)
            {
                FollowNativeScrollDuringZoom();
                StopViewAnimations();
            }
            base.OnScroll(se);
            Invalidate();
            if (!updatingView) RaiseViewChanged();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (document == null || !HasImage) return;
            if (updatingView || preserveLayoutDepth > 0) return;
            StopViewAnimations();
            if (fit || fitOnWindowResize)
            {
                fit = true;
                fitOnWindowResize = false;
                RecalculateFit();
            }
            else UpdateCanvasSize();
            Invalidate();
            RaiseViewChanged();
        }

        private void RecalculateFit()
        {
            if (!HasImage) return;
            zoomOriginOffset = PointF.Empty;
            zoom = Math.Min(1f, Math.Min(Math.Max(1, ClientSize.Width - CanvasMargin * 2) / (float)ImageSize.Width,
                Math.Max(1, ClientSize.Height - CanvasMargin * 2) / (float)ImageSize.Height));
            zoom = Math.Max(0.01f, zoom);
            UpdateCanvasSize();
        }

        private void UpdateCanvasSize()
        {
            if (!HasImage) { AutoScrollMinSize = Size.Empty; return; }
            if (fit)
            {
                if (AutoScrollMinSize != Size.Empty) AutoScrollMinSize = Size.Empty;
            }
            else
            {
                bool wasUpdating = updatingView;
                updatingView = true;
                try { PositionViewOrigin(ImageOrigin); }
                finally { updatingView = wasUpdating; }
            }
            Invalidate();
        }

        private void RaiseChanged()
        {
            RefreshRecognitionIfNeeded();
            RaiseTextStyleChanged();
            Invalidate();
            EventHandler changed = DocumentChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private void RefreshRecognitionIfNeeded()
        {
            QueueRecognitionWhenNeeded();
        }

        private async void RecognizeCurrentImageAsync()
        {
            if (disposingCanvas || !HasImage || !RecognitionRequested) { SetTextRecognitionBusy(false); return; }
            if (recognizedText != null && Object.ReferenceEquals(recognizedBase, document.BaseIdentity) && recognizedRevision == document.Revision) { SetTextRecognitionBusy(false); return; }
            SetTextRecognitionBusy(true);
            if (recognitionCancellation != null) recognitionCancellation.Cancel();
            CancellationTokenSource cancellation = new CancellationTokenSource();
            recognitionCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            int generation = recognitionGeneration;
            activeRecognitionGeneration = generation;
            long revision = document.Revision;
            SetRecognitionStatus("Finding text for precise highlighting...");
            try
            {
                Task<TextRecognitionResult> pending;
                using (Bitmap original = document.RenderImage()) pending = TextRecognition.RecognizeAsync(original, token);
                TextRecognitionResult result = await pending;
                if (disposingCanvas || generation != recognitionGeneration || revision != document.Revision || token.IsCancellationRequested) return;
                recognizedText = result;
                ResetTextSelection(result);
                if (!result.Available) SetRecognitionStatus("Text snap unavailable. Freehand highlighting is ready.");
                else if (result.TextAngle.HasValue && Math.Abs(result.TextAngle.Value) > 2) SetRecognitionStatus("Rotated text detected. Use freehand highlighting.");
                else if (result.Lines.Count == 0) SetRecognitionStatus("No text detected. Freehand highlighting is ready.");
                else SetRecognitionStatus("Text snap ready. Drag across words; Shift draws freehand.");
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!disposingCanvas && generation == recognitionGeneration) SetRecognitionStatus("Text snap unavailable. Freehand highlighting is ready.");
            }
            finally
            {
                if (Object.ReferenceEquals(recognitionCancellation, cancellation)) recognitionCancellation = null;
                if (generation == recognitionGeneration) SetTextRecognitionBusy(false);
                cancellation.Dispose();
            }
        }

        private void SetRecognitionStatus(string value)
        {
            if (String.Equals(recognitionStatus, value, StringComparison.Ordinal)) return;
            recognitionStatus = value;
            EventHandler changed = TextRecognitionStatusChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private void ResetTextSelection(TextRecognitionResult result)
        {
            textSelectionDragging = false;
            textSelectionAnchor = -1;
            textSelection = new TextOverlaySelection(result != null && result.Available ? result.Lines : null);
            RaiseTextSelectionChanged();
            Invalidate();
        }

        private void RaiseTextSelectionChanged()
        {
            EventHandler changed = TextSelectionChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposingCanvas = true;
                DisposeViewResources();
                DisposeTextScanFeedback();
                CancelSourceStyleMatching();
                DisposeSourceRemoval();
                if (recognitionCancellation != null) recognitionCancellation.Cancel();
                Cursor = Cursors.Default;
                DisposeToolPointers();
                tooltip.Dispose();
                document.Dispose();
            }
            base.Dispose(disposing);
        }

    }
}
