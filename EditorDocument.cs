using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipStudio
{
    public enum EditorTool { Select, Pen, Highlighter, Arrow, Rectangle, Text, Eraser, Crop, RemoveArea, Ellipse, Line, Emoji }

    // Detached immutable data for an editor UI; history-owned annotations stay private.
    public sealed class TextAnnotationInfo
    {
        public string Text { get; private set; }
        public string Rtf { get; private set; }
        public string FontFamily { get; private set; }
        public FontStyle FontStyle { get; private set; }
        public PointF Location { get; private set; }
        public Color Color { get; private set; }
        public float FontSize { get; private set; }
        public float LayoutWidth { get; private set; }
        public float MinimumHeight { get; private set; }
        public RectangleF Bounds { get; private set; }
        public RectangleF InkBounds { get; private set; }
        internal TextAnnotationInfo(string text, string rtf, string fontFamily, FontStyle fontStyle, PointF location, Color color, float fontSize, float layoutWidth, RectangleF bounds, RectangleF inkBounds)
            : this(text, rtf, fontFamily, fontStyle, location, color, fontSize, layoutWidth, 0, bounds, inkBounds) { }
        internal TextAnnotationInfo(string text, string rtf, string fontFamily, FontStyle fontStyle, PointF location, Color color, float fontSize, float layoutWidth, float minimumHeight, RectangleF bounds, RectangleF inkBounds)
        { Text = text; Rtf = rtf; FontFamily = fontFamily; FontStyle = fontStyle; Location = location; Color = color; FontSize = fontSize; LayoutWidth = layoutWidth; MinimumHeight = minimumHeight; Bounds = bounds; InkBounds = inkBounds; }
    }

    // Immutable annotations make undo inexpensive: ordinary edits share the captured bitmap.
    public sealed partial class EditorDocument : IDisposable
    {
        private const int MaximumStates = 26;
        private const long MaximumHistoryBytes = 256L * 1024L * 1024L;
        private readonly List<DocumentState> states = new List<DocumentState>();
        private int position = -1;
        private long nextRevision;
        private long savedRevision = -1;
        private bool disposed;

        public bool HasImage { get { return position >= 0; } }
        public bool CanUndo { get { return position > 0; } }
        public bool CanRedo { get { return position >= 0 && position < states.Count - 1; } }
        public bool IsDirty { get { return HasImage && Current.Revision != savedRevision; } }
        public Size ImageSize { get { return HasImage ? Current.Frame.Bitmap.Size : Size.Empty; } }
        public long Revision { get { return HasImage ? Current.Revision : 0; } }
        public int AnnotationCount { get { return HasImage ? Current.Annotations.Count : 0; } }
        internal object BaseIdentity { get { return HasImage ? Current.Frame : null; } }
        private DocumentState Current { get { return states[position]; } }

        public void SetImage(Bitmap image)
        {
            ThrowIfDisposed();
            if (image == null) throw new ArgumentNullException("image");
            Bitmap copy = CopyImage(image);
            try { SetOwnedImage(copy); }
            catch { copy.Dispose(); throw; }
        }

        // Takes ownership on success after the caller has fully decoded and
        // detached the image from its source stream.
        internal void SetOwnedImage(Bitmap image, List<Annotation> annotations = null)
        {
            ThrowIfDisposed();
            var next = new DocumentState(new ImageFrame(image), annotations ?? new List<Annotation>(), nextRevision + 1);
            if (states.Capacity == 0) states.Capacity = 1;
            ClearStates();
            states.Add(next);
            nextRevision = next.Revision;
            position = 0;
            savedRevision = -1;
        }

        public void MarkSaved()
        {
            ThrowIfDisposed();
            if (HasImage) savedRevision = Current.Revision;
        }

        public void Undo()
        {
            ThrowIfDisposed();
            if (CanUndo) position--;
        }

        public void Redo()
        {
            ThrowIfDisposed();
            if (CanRedo) position++;
        }

        public Bitmap RenderImage()
        {
            ThrowIfDisposed();
            if (!HasImage) return null;
            Bitmap bitmap = new Bitmap(ImageSize.Width, ImageSize.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap)) Draw(graphics);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        internal void Draw(Graphics graphics)
        {
            Draw(graphics, -1, PointF.Empty);
        }

        internal void Draw(Graphics graphics, int movingIndex, PointF delta)
        {
            Draw(graphics, movingIndex, delta, -1);
        }

        internal void Draw(Graphics graphics, int movingIndex, PointF delta, int hiddenIndex)
        {
            if (!HasImage) return;
            graphics.DrawImage(Current.Frame.Bitmap, new Rectangle(Point.Empty, ImageSize),
                0, 0, ImageSize.Width, ImageSize.Height, GraphicsUnit.Pixel);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < Current.Annotations.Count; i++)
                if (i != hiddenIndex)
                {
                    if (i != movingIndex) Current.Annotations[i].Draw(graphics);
                    else
                    {
                        GraphicsState state = graphics.Save();
                        try
                        {
                            graphics.TranslateTransform(delta.X, delta.Y);
                            Current.Annotations[i].Draw(graphics);
                        }
                        finally { graphics.Restore(state); }
                    }
                }
        }

        internal Bitmap CopyBaseImage() { RequireImage(); return CopyImage(Current.Frame.Bitmap); }

        internal Bitmap CopyBaseRegion(Rectangle requested)
        {
            RequireImage();
            Rectangle clipped = ClipPixelRectangle(requested);
            if (clipped.IsEmpty) throw new ArgumentException("The requested image region must overlap the screenshot.", "requested");
            return Current.Frame.Bitmap.Clone(clipped, PixelFormat.Format32bppArgb);
        }

        public int HitTestAnnotation(PointF point, float radius)
        {
            RequireImage(); ValidatePoint(point); ValidatePositive(radius, "radius");
            if (point.X < 0 || point.Y < 0 || point.X >= ImageSize.Width || point.Y >= ImageSize.Height) return -1;
            for (int i = Current.Annotations.Count - 1; i >= 0; i--)
                if (Current.Annotations[i].HitTest(point, radius)) return i;
            return -1;
        }

        public RectangleF GetAnnotationBounds(int index)
        {
            RequireImage();
            return index >= 0 && index < AnnotationCount ? Current.Annotations[index].Bounds : RectangleF.Empty;
        }

        public TextAnnotationInfo GetTextAnnotation(int index)
        {
            ThrowIfDisposed();
            TextAnnotation text = HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] as TextAnnotation : null;
            return text == null ? null : new TextAnnotationInfo(text.Text, text.Rtf, text.FontFamily, text.FontStyle, text.Location, text.Color, text.FontSize, text.LayoutWidth, text.MinimumHeight, text.Bounds, text.InkBounds);
        }

        public bool IsArrowAnnotation(int index)
        {
            ThrowIfDisposed();
            return HasImage && index >= 0 && index < AnnotationCount && Current.Annotations[index] is ArrowAnnotation;
        }

        public ShapeStyle GetShapeAnnotationStyle(int index)
        {
            ThrowIfDisposed();
            IStyledShape shape = HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] as IStyledShape : null;
            return shape == null ? null : shape.Style;
        }

        public EditorTool? GetShapeAnnotationTool(int index)
        {
            ThrowIfDisposed();
            IStyledShape shape = HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] as IStyledShape : null;
            return shape == null ? (EditorTool?)null : shape.ShapeTool;
        }

        public bool UpdateShapeAnnotationStyle(int index, ShapeStyle style)
        {
            RequireImage();
            if (style == null) throw new ArgumentNullException("style");
            IStyledShape shape = index >= 0 && index < AnnotationCount ? Current.Annotations[index] as IStyledShape : null;
            return shape != null && !shape.Style.SameAs(style) && ReplaceAnnotation(index, shape.WithStyle(style));
        }

        public PointF[] GetShapeEndpoints(int index)
        {
            ThrowIfDisposed();
            Annotation annotation = HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] : null;
            ArrowAnnotation arrow = annotation as ArrowAnnotation;
            if (arrow != null) return new PointF[] { arrow.Start, arrow.End };
            LineAnnotation line = annotation as LineAnnotation;
            return line == null ? new PointF[0] : new PointF[] { line.Start, line.End };
        }

        internal Annotation CreateShapeEndpointPreview(int index, bool tip, PointF point)
        {
            RequireImage(); ValidatePoint(point);
            PointF[] ends = GetShapeEndpoints(index);
            if (ends.Length != 2) return null;
            point = new PointF(Math.Max(0, Math.Min(ImageSize.Width - 1, point.X)), Math.Max(0, Math.Min(ImageSize.Height - 1, point.Y)));
            PointF first = tip ? ends[0] : point, second = tip ? point : ends[1];
            if (DistanceSquared(first, second) < 4) return null;
            ShapeStyle style = GetShapeAnnotationStyle(index);
            return GetShapeAnnotationTool(index) == EditorTool.Arrow ? (Annotation)new ArrowAnnotation(first, second, style) : new LineAnnotation(first, second, style);
        }

        public bool UpdateShapeEndpoint(int index, bool tip, PointF point)
        {
            Annotation replacement = CreateShapeEndpointPreview(index, tip, point);
            if (replacement == null) return false;
            PointF[] old = GetShapeEndpoints(index), current = replacement is ArrowAnnotation ?
                new PointF[] { ((ArrowAnnotation)replacement).Start, ((ArrowAnnotation)replacement).End } :
                new PointF[] { ((LineAnnotation)replacement).Start, ((LineAnnotation)replacement).End };
            return (old[0] != current[0] || old[1] != current[1]) && ReplaceAnnotation(index, replacement);
        }

        public PointF[] GetArrowEndpoints(int index)
        {
            if (!IsArrowAnnotation(index)) return new PointF[0];
            ArrowAnnotation arrow = (ArrowAnnotation)Current.Annotations[index];
            return new PointF[] { arrow.Start, arrow.End };
        }

        internal ArrowAnnotation CreateArrowPreview(int index, bool tip, PointF point)
        {
            RequireImage(); ValidatePoint(point);
            if (!IsArrowAnnotation(index)) return null;
            ArrowAnnotation arrow = (ArrowAnnotation)Current.Annotations[index];
            point = new PointF(Math.Max(0, Math.Min(ImageSize.Width - 1, point.X)), Math.Max(0, Math.Min(ImageSize.Height - 1, point.Y)));
            PointF start = tip ? arrow.Start : point, end = tip ? point : arrow.End;
            if (DistanceSquared(start, end) < 4) return null;
            return new ArrowAnnotation(start, end, arrow.Style);
        }

        public bool UpdateArrowEndpoint(int index, bool tip, PointF point)
        {
            ArrowAnnotation replacement = CreateArrowPreview(index, tip, point);
            if (replacement == null) return false;
            ArrowAnnotation original = (ArrowAnnotation)Current.Annotations[index];
            if (replacement.Start == original.Start && replacement.End == original.End) return false;
            return ReplaceAnnotation(index, replacement);
        }

        internal TextAnnotation CreateTextAnnotationPreview(int index, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            return original == null ? null : original.WithGeometry(original.Location, fontSize, layoutWidth);
        }

        internal TextAnnotation CreateTextAnnotationPreview(int index, PointF location, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            return original == null ? null : original.WithGeometry(location, fontSize, layoutWidth);
        }

        internal TextAnnotation CreateTextAnnotationPreview(int index, PointF location, float fontSize, float layoutWidth, float minimumHeight)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth); ValidateTextHeight(minimumHeight);
            TextAnnotation original = GetTextAnnotationObject(index);
            return original == null ? null : original.WithGeometry(location, fontSize, layoutWidth, minimumHeight);
        }

        public bool UpdateTextAnnotationBounds(int index, PointF location, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            return original != null && UpdateTextAnnotationBounds(index, location, fontSize, layoutWidth, original.MinimumHeight);
        }

        public bool UpdateTextAnnotationBounds(int index, PointF location, float fontSize, float layoutWidth, float minimumHeight)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth); ValidateTextHeight(minimumHeight);
            layoutWidth = NormalizeTextWidth(layoutWidth); minimumHeight = NormalizeTextWidth(minimumHeight);
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            if (location == original.Location && fontSize == original.FontSize && layoutWidth == original.LayoutWidth && minimumHeight == original.MinimumHeight) return false;
            return ReplaceAnnotation(index, original.WithGeometry(location, fontSize, layoutWidth, minimumHeight));
        }

        public bool UpdateTextAnnotation(int index, string text, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            return original != null && UpdateTextAnnotation(index, text, original.Color, fontSize, layoutWidth);
        }

        public bool UpdateTextAnnotation(int index, string text, Color color, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            layoutWidth = NormalizeTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            if (String.IsNullOrEmpty(text)) return RemoveAnnotation(index);
            if (text == original.Text && color.ToArgb() == original.Color.ToArgb() && fontSize == original.FontSize && layoutWidth == original.LayoutWidth) return false;
            return ReplaceAnnotation(index, original.WithTextAndFormatting(text, color, fontSize, layoutWidth));
        }

        public bool UpdateRichTextAnnotation(int index, string plain, string rtf, Color color, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            return original != null && UpdateRichTextAnnotation(index, plain, rtf, color, fontSize, layoutWidth, original.MinimumHeight);
        }

        public bool UpdateRichTextAnnotation(int index, string plain, string rtf, Color color, float fontSize, float layoutWidth, float minimumHeight)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth); ValidateTextHeight(minimumHeight);
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            if (String.IsNullOrEmpty(plain)) return RemoveAnnotation(index);
            layoutWidth = NormalizeTextWidth(layoutWidth); minimumHeight = NormalizeTextWidth(minimumHeight);
            TextAnnotation replacement = new TextAnnotation(plain, rtf, original.Location, color, fontSize, layoutWidth, minimumHeight, original.FontFamily, original.FontStyle);
            if (replacement.Text == original.Text && replacement.Rtf == original.Rtf && replacement.LayoutWidth == original.LayoutWidth && replacement.MinimumHeight == original.MinimumHeight) return false;
            return ReplaceAnnotation(index, replacement);
        }

        public bool UpdateTextAnnotationFontSize(int index, float fontSize)
        {
            RequireImage();
            TextAnnotation original = GetTextAnnotationObject(index);
            return original != null && UpdateTextAnnotationFontSize(index, fontSize, original.LayoutWidth);
        }

        public bool UpdateTextAnnotationFontSize(int index, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            string rtf = RichTextLayout.SetFontSize(original.Rtf, fontSize);
            return UpdateRichTextAnnotation(index, original.Text, rtf, original.Color, fontSize, layoutWidth);
        }

        public bool UpdateTextAnnotationColor(int index, Color color)
        {
            RequireImage();
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            string rtf = RichTextLayout.SetColor(original.Rtf, color);
            return UpdateRichTextAnnotation(index, original.Text, rtf, color, original.FontSize, original.LayoutWidth);
        }

        public bool RemoveTextAnnotationRange(int index, int start, int length)
        {
            RequireImage();
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            if (start < 0 || length < 0 || start > original.Text.Length - length) throw new ArgumentOutOfRangeException("start");
            if (length == 0) return false;
            if (SplitsTextSequence(original.Text, start) || SplitsTextSequence(original.Text, start + length))
                throw new ArgumentException("The selected range must not split a line ending or a Unicode character.", "length");
            string plain = original.Text.Remove(start, length);
            if (plain.Length == 0) return RemoveAnnotation(index);
            int nativeStart = NormalizeRichPlain(original.Text.Substring(0, start)).Length;
            int nativeLength = NormalizeRichPlain(original.Text.Substring(start, length)).Length;
            string rtf = RichTextLayout.RemoveTextRange(original.Rtf, nativeStart, nativeLength);
            return UpdateRichTextAnnotation(index, plain, rtf, original.Color, original.FontSize, original.LayoutWidth);
        }

        private TextAnnotation GetTextAnnotationObject(int index)
        {
            return HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] as TextAnnotation : null;
        }

        internal PointF ConstrainMove(int index, PointF delta)
        {
            RectangleF bounds = GetAnnotationBounds(index);
            if (bounds.IsEmpty) return PointF.Empty;
            float visibleX = Math.Min(ImageSize.Width, Math.Min(16, Math.Max(1, bounds.Width)));
            float visibleY = Math.Min(ImageSize.Height, Math.Min(16, Math.Max(1, bounds.Height)));
            // A crop or rotation can leave less than the normal visible margin.
            // Keep zero in each range so moving along one axis never jumps along the other.
            return new PointF(Math.Max(Math.Min(0, visibleX - bounds.Right), Math.Min(Math.Max(0, ImageSize.Width - visibleX - bounds.Left), delta.X)),
                Math.Max(Math.Min(0, visibleY - bounds.Bottom), Math.Min(Math.Max(0, ImageSize.Height - visibleY - bounds.Top), delta.Y)));
        }

        public bool MoveAnnotation(int index, PointF delta)
        {
            RequireImage(); ValidatePoint(delta);
            if (index < 0 || index >= AnnotationCount || (delta.X == 0 && delta.Y == 0)) return false;
            delta = ConstrainMove(index, delta);
            if (Math.Abs(delta.X) < 0.0001f && Math.Abs(delta.Y) < 0.0001f) return false;
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            annotations[index] = annotations[index].Translate(delta);
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
            return true;
        }

        public bool RemoveAnnotation(int index)
        {
            RequireImage();
            if (index < 0 || index >= AnnotationCount) return false;
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            annotations.RemoveAt(index);
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
            return true;
        }

        private const byte DefaultHighlighterAlpha = 80;

        public void AddHighlightBands(IEnumerable<RectangleF> bands, Color color, byte? highlighterAlpha = null)
        {
            RequireImage();
            if (bands == null) throw new ArgumentNullException("bands");
            List<RectangleF> valid = new List<RectangleF>();
            foreach (RectangleF bounds in bands)
            {
                ValidatePoint(bounds.Location); ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
                if (bounds.Width > 0 && bounds.Height > 0) valid.Add(bounds);
            }
            if (valid.Count > 0)
                Add(new HighlightBandsAnnotation(valid.ToArray(), Color.FromArgb(highlighterAlpha ?? DefaultHighlighterAlpha, color)));
        }

        public void AddStroke(IEnumerable<PointF> points, Color color, float width, bool highlighter,
            byte? highlighterAlpha = null)
        {
            RequireImage();
            if (points == null) throw new ArgumentNullException("points");
            ValidatePositive(width, "width");
            List<PointF> copy = new List<PointF>();
            foreach (PointF point in points)
            {
                ValidatePoint(point);
                copy.Add(point);
            }
            if (copy.Count == 0) return;
            if (highlighter) color = Color.FromArgb(highlighterAlpha ?? DefaultHighlighterAlpha, color);
            Add(new StrokeAnnotation(copy.ToArray(), color, width, highlighter));
        }

        public void AddArrow(PointF start, PointF end, Color color, float width)
        {
            RequireImage(); ValidatePoint(start); ValidatePoint(end); ValidatePositive(width, "width");
            if (DistanceSquared(start, end) < 1) return;
            Add(new ArrowAnnotation(start, end, color, width));
        }

        public void AddRectangle(RectangleF rectangle, Color color, float width)
        {
            RequireImage(); ValidatePositive(width, "width");
            ValidatePoint(new PointF(rectangle.Left, rectangle.Top));
            ValidatePoint(new PointF(rectangle.Right, rectangle.Bottom));
            if (rectangle.Width < 1 || rectangle.Height < 1) return;
            Add(new RectangleAnnotation(rectangle, color, width));
        }

        public bool AddShape(EditorTool shapeTool, PointF first, PointF second, ShapeStyle style)
        {
            RequireImage(); ValidatePoint(first); ValidatePoint(second);
            if (style == null) throw new ArgumentNullException("style");
            Annotation annotation = CreateShapeAnnotation(shapeTool, first, second, style);
            if (annotation == null) return false;
            Add(annotation); return true;
        }

        internal static Annotation CreateShapeAnnotation(EditorTool shapeTool, PointF first, PointF second, ShapeStyle style)
        {
            ValidatePoint(first); ValidatePoint(second);
            if (style == null) throw new ArgumentNullException("style");
            if (shapeTool != EditorTool.Rectangle && shapeTool != EditorTool.Ellipse && shapeTool != EditorTool.Line && shapeTool != EditorTool.Arrow)
                throw new ArgumentOutOfRangeException("shapeTool");
            if (!style.IsVisibleFor(shapeTool)) return null;
            if (shapeTool == EditorTool.Arrow || shapeTool == EditorTool.Line)
            {
                if (DistanceSquared(first, second) < 1) return null;
                return shapeTool == EditorTool.Arrow ? (Annotation)new ArrowAnnotation(first, second, style) : new LineAnnotation(first, second, style);
            }
            RectangleF bounds = RectangleF.FromLTRB(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
            ValidatePoint(bounds.Location); ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
            if (bounds.Width < 1 || bounds.Height < 1) return null;
            return shapeTool == EditorTool.Rectangle ? (Annotation)new RectangleAnnotation(bounds, style) : new EllipseAnnotation(bounds, style);
        }

        public void AddText(string text, PointF location, Color color, float fontSize)
        {
            AddText(text, location, color, fontSize, 0);
        }

        public void AddText(string text, PointF location, Color color, float fontSize, float layoutWidth)
        {
            AddText(text, location, color, fontSize, layoutWidth, 0);
        }

        public void AddText(string text, PointF location, Color color, float fontSize, float layoutWidth, float minimumHeight)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth); ValidateTextHeight(minimumHeight);
            if (String.IsNullOrWhiteSpace(text)) return;
            Add(new TextAnnotation(text, null, location, color, fontSize, layoutWidth, minimumHeight, "Segoe UI", FontStyle.Regular));
        }

        public void AddRichText(string plain, string rtf, PointF location, Color color, float fontSize, float layoutWidth)
        {
            AddRichText(plain, rtf, location, color, fontSize, layoutWidth, 0);
        }

        public void AddRichText(string plain, string rtf, PointF location, Color color, float fontSize, float layoutWidth, float minimumHeight)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth); ValidateTextHeight(minimumHeight);
            if (String.IsNullOrWhiteSpace(plain)) return;
            Add(new TextAnnotation(plain, rtf, location, color, fontSize, layoutWidth, minimumHeight, "Segoe UI", FontStyle.Regular));
        }

        public int ReplaceImageText(Rectangle eraseBounds, string text, Color foreground, Color background, float fontSize, float layoutWidth)
        {
            return ReplaceImageRichText(eraseBounds, text, null, foreground, background, fontSize, layoutWidth);
        }

        public int ReplaceImageRichText(Rectangle eraseBounds, string plain, string rtf, Color foreground, Color background, float fontSize, float layoutWidth)
        {
            RequireImage();
            Rectangle clipped = ClipPixelRectangle(eraseBounds);
            return ReplaceImageRichTextAt(eraseBounds, plain, rtf, foreground, background, fontSize, layoutWidth, clipped.Location);
        }

        internal int ReplaceImageRichTextAt(Rectangle eraseBounds, string plain, string rtf, Color foreground, Color background,
            float fontSize, float layoutWidth, PointF textLocation)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            ValidatePoint(textLocation);
            Rectangle clipped = ClipPixelRectangle(eraseBounds);
            if (clipped.IsEmpty) throw new ArgumentException("The text region must overlap the image.", "eraseBounds");
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            int addedIndex = -1;
            if (!String.IsNullOrWhiteSpace(plain))
            {
                addedIndex = annotations.Count;
                annotations.Add(new TextAnnotation(plain, rtf, textLocation, foreground, fontSize, layoutWidth, "Segoe UI", FontStyle.Regular));
            }
            Bitmap patched = CopyImage(Current.Frame.Bitmap);
            try
            {
                using (Graphics graphics = Graphics.FromImage(patched))
                using (Brush fill = new SolidBrush(background))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.FillRectangle(fill, clipped);
                }
                Commit(new DocumentState(new ImageFrame(patched), annotations, ++nextRevision));
                return addedIndex;
            }
            catch { patched.Dispose(); throw; }
        }

        internal TextAnnotation PrepareReviewedTextNote(int index, string translated, out string failure)
        {
            TextAnnotation original = GetTextAnnotationObject(index);
            TextAnnotation replacement;
            try
            {
                if (original == null || !RichTextLayout.TryReplaceUniformSingleLine(original.Rtf, translated, out string rtf))
                {
                    failure = "This note contains formatting or line breaks that need to be edited directly.";
                    return null;
                }
                replacement = new TextAnnotation(translated, rtf, original.Location, original.Color,
                    original.FontSize, original.LayoutWidth, original.MinimumHeight, original.FontFamily, original.FontStyle);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
            {
                failure = "The reviewed text could not be laid out in the existing note. Shorten it and try again.";
                return null;
            }
            RectangleF ink = replacement.InkBounds;
            if (ink.IsEmpty || !original.Bounds.Contains(ink) ||
                !new RectangleF(Point.Empty, ImageSize).Contains(ink))
            {
                failure = "The reviewed text does not fit the existing note. Shorten it or resize the note and recognize the snip again.";
                return null;
            }
            failure = null;
            return replacement;
        }

        internal int ReplaceRecognizedTextBlocks(IList<RecognizedTextReplacement> replacements,
            object expectedImageIdentity, long expectedRevision, IReadOnlyDictionary<int, TextAnnotation> noteEdits = null)
        {
            RequireImage();
            if (replacements == null) throw new ArgumentNullException("replacements");
            if (replacements.Count == 0 && (noteEdits == null || noteEdits.Count == 0)) return -1;
            if (!Object.ReferenceEquals(expectedImageIdentity, BaseIdentity) || expectedRevision != Revision) return -1;

            List<Rectangle> masks = new List<Rectangle>();
            List<Color> backgrounds = new List<Color>();
            List<Annotation> text = new List<Annotation>();
            foreach (RecognizedTextReplacement replacement in replacements)
            {
                if (replacement == null) throw new ArgumentException("Every source region needs replacement text.", "replacements");
                ValidatePoint(replacement.TextLocation);
                ValidatePositive(replacement.FontSize, "fontSize");
                ValidateTextWidth(replacement.LayoutWidth);
                Rectangle clipped = ClipPixelRectangle(replacement.Bounds);
                if (clipped.IsEmpty || clipped != replacement.Bounds || String.IsNullOrWhiteSpace(replacement.PlainText))
                    throw new ArgumentException("Every replacement must contain text and stay inside its source image.", "replacements");
                RectangleF layout = replacement.LayoutBounds;
                ValidatePoint(layout.Location);
                ValidatePoint(new PointF(layout.Right, layout.Bottom));
                ValidateTextWidth(layout.Width);
                ValidateTextHeight(layout.Height);
                if (layout.Width <= 0 || layout.Height <= 0 || layout.Left < clipped.Left || layout.Top < clipped.Top ||
                    layout.Bottom > clipped.Bottom || layout.Right > ImageSize.Width)
                    throw new ArgumentException("Translation layout may only extend right within its source image and line band.", "replacements");
                // Construct and validate every immutable annotation first. RTF
                // failures cannot leave a half-translated document or extra Undo.
                TextAnnotation annotation = new TextAnnotation(replacement.PlainText, replacement.Rtf, replacement.TextLocation,
                    replacement.Foreground, replacement.FontSize, replacement.LayoutWidth, "Segoe UI", FontStyle.Regular);
                RectangleF ink = annotation.InkBounds;
                if (ink.IsEmpty || ink.Left < layout.Left || ink.Top < layout.Top || ink.Right > layout.Right || ink.Bottom > layout.Bottom)
                    throw new ArgumentException("The complete translated text must fit inside its verified layout region.", "replacements");
                ValidateTranslationExtension(replacement, clipped, ink, replacements);
                text.Add(annotation);
                masks.Add(clipped);
                backgrounds.Add(Color.FromArgb(255, replacement.Background));
            }
            for (int first = 0; first < text.Count; first++)
                for (int second = first + 1; second < text.Count; second++)
                {
                    RectangleF a = ((TextAnnotation)text[first]).InkBounds;
                    RectangleF b = ((TextAnnotation)text[second]).InkBounds;
                    if ((a.Right > masks[first].Right || b.Right > masks[second].Right) && a.IntersectsWith(b))
                        throw new ArgumentException("Expanded translated lines would overlap. Nothing was replaced.", "replacements");
                }
            if (!Object.ReferenceEquals(expectedImageIdentity, BaseIdentity) || expectedRevision != Revision) return -1;

            // Sampled flat-color masks cover only source raster text. The
            // original capture is shared, untouched, across this single history
            // state. Existing highlights/shapes/notes retain their draw order.
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            int lastEditedNote = -1;
            if (noteEdits != null)
                foreach (KeyValuePair<int, TextAnnotation> edit in noteEdits)
                {
                    TextAnnotation original = GetTextAnnotationObject(edit.Key);
                    if (original == null || edit.Value == null)
                        throw new ArgumentException("Every reviewed note must have an existing annotation.", "noteEdits");
                    foreach (KeyValuePair<int, TextAnnotation> other in noteEdits)
                    {
                        if (other.Key >= edit.Key) continue;
                        RectangleF overlap = RectangleF.Intersect(edit.Value.InkBounds, other.Value.InkBounds);
                        TextAnnotation previous = GetTextAnnotationObject(other.Key);
                        if (!overlap.IsEmpty && !RectangleF.Intersect(original.InkBounds, previous.InkBounds).Contains(overlap))
                            throw new ArgumentException("The reviewed notes would newly overlap. Shorten their text.", "noteEdits");
                    }
                    foreach (TextAnnotation raster in text)
                        if (edit.Value.InkBounds.IntersectsWith(raster.InkBounds))
                            throw new ArgumentException("A reviewed note and an image line would overlap. Shorten their text.", "noteEdits");
                    annotations[edit.Key] = edit.Value;
                    lastEditedNote = Math.Max(lastEditedNote, edit.Key);
                }
            if (text.Count != 0)
            {
                annotations.Insert(0, new SourceTextMasksAnnotation(masks.ToArray(), backgrounds.ToArray()));
                annotations.AddRange(text);
            }
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
            return text.Count != 0 ? annotations.Count - 1 : lastEditedNote;
        }

        private void ValidateTranslationExtension(RecognizedTextReplacement replacement, Rectangle mask,
            RectangleF ink, IList<RecognizedTextReplacement> replacements)
        {
            if (ink.Right <= mask.Right) return;
            // Validate visible extension independently of the canvas planner.
            // One extra blank column protects fractional raster placement.
            int right = Math.Min(ImageSize.Width, (int)Math.Ceiling(ink.Right) + 1);
            Rectangle extension = Rectangle.FromLTRB(mask.Right, mask.Top, right, mask.Bottom);
            if ((long)extension.Width * extension.Height > 2 * 1024 * 1024)
                throw new ArgumentException("Translation extension exceeds the checked pixel budget.", "replacements");
            foreach (Annotation existing in Current.Annotations)
                if (existing.Bounds.IntersectsWith(extension))
                    throw new ArgumentException("Translation extension would overlap an existing annotation.", "replacements");
            // Another erase mask may meet the padding above or below this
            // text. Only the visible extension can cover that source line.
            Rectangle inkExtension = Rectangle.FromLTRB(extension.Left, (int)Math.Floor(ink.Top),
                extension.Right, (int)Math.Ceiling(ink.Bottom));
            foreach (RecognizedTextReplacement other in replacements)
                if (other != null && !Object.ReferenceEquals(other, replacement) && other.Bounds.IntersectsWith(inkExtension))
                    throw new ArgumentException("Translation extension would overlap another source line.", "replacements");
            Bitmap source = Current.Frame.Bitmap;
            Color background = replacement.Background;
            BitmapData pixels = source.LockBits(extension, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[checked(extension.Width * 4)];
                for (int y = 0; y < extension.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(pixels.Scan0, checked(y * pixels.Stride)), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x += 4)
                        if (row[x] != background.B || row[x + 1] != background.G || row[x + 2] != background.R || row[x + 3] != 255)
                            throw new ArgumentException("Translation extension contains source pixels that must be preserved.", "replacements");
                }
            }
            finally { source.UnlockBits(pixels); }
        }

        public bool EraseImageRegions(IEnumerable<Rectangle> regions, Color background)
        {
            RequireImage();
            if (regions == null) throw new ArgumentNullException("regions");
            List<Rectangle> clipped = new List<Rectangle>();
            foreach (Rectangle region in regions)
            {
                Rectangle area = ClipPixelRectangle(region);
                if (!area.IsEmpty) clipped.Add(area);
            }
            if (clipped.Count == 0) return false;
            Bitmap patched = CopyImage(Current.Frame.Bitmap);
            try
            {
                using (Graphics graphics = Graphics.FromImage(patched))
                using (Brush fill = new SolidBrush(background))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    foreach (Rectangle area in clipped) graphics.FillRectangle(fill, area);
                }
                Commit(new DocumentState(new ImageFrame(patched), new List<Annotation>(Current.Annotations), ++nextRevision));
                return true;
            }
            catch { patched.Dispose(); throw; }
        }

        public bool Erase(PointF location, float radius)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(radius, "radius");
            List<Annotation> remaining = new List<Annotation>(Current.Annotations);
            bool removed = false;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                if (remaining[i].HitTest(location, radius))
                {
                    remaining.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
            if (!removed) return false;
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, remaining, ++nextRevision));
            return true;
        }

        public void Crop(Rectangle rectangle)
        {
            RequireImage();
            Rectangle clipped = Rectangle.Intersect(rectangle, new Rectangle(Point.Empty, ImageSize));
            if (clipped.Width < 2 || clipped.Height < 2)
                throw new ArgumentException("Select an area at least 2 by 2 pixels.", "rectangle");
            if (clipped == new Rectangle(Point.Empty, ImageSize)) return;
            Bitmap cropped = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(cropped))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImage(Current.Frame.Bitmap, new Rectangle(0, 0, clipped.Width, clipped.Height),
                        clipped, GraphicsUnit.Pixel);
                }
                List<Annotation> annotations = new List<Annotation>();
                foreach (Annotation annotation in Current.Annotations)
                    annotations.Add(annotation.Translate(new PointF(-clipped.X, -clipped.Y)));
                Commit(new DocumentState(new ImageFrame(cropped), annotations, ++nextRevision));
            }
            catch
            {
                cropped.Dispose();
                throw;
            }
        }

        private void Add(Annotation annotation)
        {
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            annotations.Add(annotation);
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
        }

        private bool ReplaceAnnotation(int index, Annotation replacement)
        {
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            annotations[index] = replacement;
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
            return true;
        }

        private Rectangle ClipPixelRectangle(Rectangle rectangle)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return Rectangle.Empty;
            long left = Math.Max(0L, rectangle.X), top = Math.Max(0L, rectangle.Y);
            long right = Math.Min((long)ImageSize.Width, (long)rectangle.X + rectangle.Width);
            long bottom = Math.Min((long)ImageSize.Height, (long)rectangle.Y + rectangle.Height);
            return right <= left || bottom <= top ? Rectangle.Empty : Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
        }

        private void Commit(DocumentState state)
        {
            for (int i = states.Count - 1; i > position; i--)
            {
                states[i].Dispose();
                states.RemoveAt(i);
            }
            states.Add(state);
            position = states.Count - 1;
            while (states.Count > MaximumStates)
            {
                states[0].Dispose();
                states.RemoveAt(0);
                position--;
            }
            // Crops introduce new bitmap bases; cap their retained memory as well as edit count.
            // A single current capture is always kept, even if it exceeds the budget.
            while (states.Count > 1 && RetainedImageBytes() > MaximumHistoryBytes)
            {
                states[0].Dispose();
                states.RemoveAt(0);
                position--;
            }
        }

        private long RetainedImageBytes()
        {
            HashSet<ImageFrame> frames = new HashSet<ImageFrame>();
            long bytes = 0;
            foreach (DocumentState state in states)
                if (frames.Add(state.Frame)) bytes += (long)state.Frame.Bitmap.Width * state.Frame.Bitmap.Height * 4;
            return bytes;
        }

        internal static Bitmap CopyImage(Bitmap image)
        {
            Bitmap copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(copy))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height),
                        0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }

        private void RequireImage()
        {
            ThrowIfDisposed();
            if (!HasImage) throw new InvalidOperationException("Capture or open an image first.");
        }

        private static void ValidatePositive(float value, string name)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value <= 0 || value > 1024)
                throw new ArgumentOutOfRangeException(name);
        }

        private static void ValidateTextWidth(float value)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 0 || value > 32768)
                throw new ArgumentOutOfRangeException("layoutWidth");
        }

        private static void ValidateTextHeight(float value)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 0 || value > 32768)
                throw new ArgumentOutOfRangeException("minimumHeight");
        }

        private static float NormalizeTextWidth(float value)
        {
            return value > 0 ? Math.Max(1, (float)Math.Round(value)) : 0;
        }

        private static string NormalizeRichPlain(string value)
        {
            return (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static bool SplitsTextSequence(string text, int at)
        {
            if (at <= 0 || at >= text.Length) return false;
            return (text[at - 1] == '\r' && text[at] == '\n') || (Char.IsHighSurrogate(text[at - 1]) && Char.IsLowSurrogate(text[at]));
        }

        private static void ValidatePoint(PointF point)
        {
            if (Single.IsNaN(point.X) || Single.IsNaN(point.Y) || Single.IsInfinity(point.X) || Single.IsInfinity(point.Y))
                throw new ArgumentException("Image coordinates must be finite.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("EditorDocument");
        }

        private void ClearStates()
        {
            foreach (DocumentState state in states) state.Dispose();
            states.Clear();
            position = -1;
        }

        public void Dispose()
        {
            if (disposed) return;
            ClearStates();
            disposed = true;
        }

        internal static float DistanceSquared(PointF a, PointF b)
        {
            float x = a.X - b.X, y = a.Y - b.Y;
            return x * x + y * y;
        }

        internal static float DistanceToSegment(PointF point, PointF start, PointF end)
        {
            float length = DistanceSquared(start, end);
            if (length == 0) return (float)Math.Sqrt(DistanceSquared(point, start));
            float t = ((point.X - start.X) * (end.X - start.X) + (point.Y - start.Y) * (end.Y - start.Y)) / length;
            t = Math.Max(0, Math.Min(1, t));
            return (float)Math.Sqrt(DistanceSquared(point, new PointF(start.X + t * (end.X - start.X), start.Y + t * (end.Y - start.Y))));
        }

        private sealed class ImageFrame
        {
            public readonly Bitmap Bitmap;
            private int references = 1;
            public ImageFrame(Bitmap bitmap) { Bitmap = bitmap; }
            public void AddReference() { references++; }
            public void Release() { if (--references == 0) Bitmap.Dispose(); }
        }

        private sealed class DocumentState : IDisposable
        {
            public readonly ImageFrame Frame;
            public readonly List<Annotation> Annotations;
            public readonly long Revision;
            public DocumentState(ImageFrame frame, List<Annotation> annotations, long revision)
            { Frame = frame; Annotations = annotations; Revision = revision; }
            public void Dispose() { Frame.Release(); }
        }

        internal abstract class Annotation
        {
            public abstract void Draw(Graphics graphics);
            public abstract bool HitTest(PointF point, float radius);
            public abstract RectangleF Bounds { get; }
            public abstract Annotation Translate(PointF delta);
        }

        internal interface IStyledShape
        {
            ShapeStyle Style { get; }
            EditorTool ShapeTool { get; }
            Annotation WithStyle(ShapeStyle style);
        }

        private sealed class SourceTextMasksAnnotation : Annotation
        {
            private readonly RectangleF[] regions;
            private readonly Color[] backgrounds;
            private readonly RectangleF bounds;

            internal SourceTextMasksAnnotation(Rectangle[] regions, Color[] backgrounds)
            {
                this.regions = new RectangleF[regions.Length];
                this.backgrounds = (Color[])backgrounds.Clone();
                RectangleF union = RectangleF.Empty;
                for (int i = 0; i < regions.Length; i++)
                {
                    this.regions[i] = regions[i];
                    union = union.IsEmpty ? regions[i] : RectangleF.Union(union, regions[i]);
                }
                bounds = union;
            }

            internal SourceTextMasksAnnotation(RectangleF[] regions, Color[] backgrounds, RectangleF bounds)
            { this.regions = regions; this.backgrounds = backgrounds; this.bounds = bounds; }

            internal int RegionCount { get { return regions.Length; } }
            internal RectangleF GetRegion(int index) { return regions[index]; }
            internal Color GetBackground(int index) { return backgrounds[index]; }

            public override RectangleF Bounds { get { return bounds; } }
            // A mask belongs to its source pixels, so moving/erasing the
            // translated text cannot accidentally move the erased source words.
            public override bool HitTest(PointF point, float radius) { return false; }
            public override void Draw(Graphics graphics)
            {
                for (int i = 0; i < regions.Length; i++)
                    using (Brush fill = new SolidBrush(backgrounds[i])) graphics.FillRectangle(fill, regions[i]);
            }
            public override Annotation Translate(PointF delta)
            {
                RectangleF[] moved = new RectangleF[regions.Length];
                for (int i = 0; i < moved.Length; i++) { moved[i] = regions[i]; moved[i].Offset(delta); }
                RectangleF movedBounds = bounds;
                movedBounds.Offset(delta);
                return new SourceTextMasksAnnotation(moved, backgrounds, movedBounds);
            }
        }

        internal sealed class StrokeAnnotation : Annotation
        {
            private readonly PointF[] points;
            private readonly Color color;
            private readonly float width;
            private readonly bool highlighter;
            public StrokeAnnotation(PointF[] points, Color color, float width, bool highlighter)
            {
                this.points = (PointF[])points.Clone();
                this.color = color;
                this.width = width;
                this.highlighter = highlighter;
            }
            internal int PointCount { get { return points.Length; } }
            internal PointF GetPoint(int index) { return points[index]; }
            internal Color Ink { get { return color; } }
            internal float Width { get { return width; } }
            internal bool IsHighlighter { get { return highlighter; } }
            public override RectangleF Bounds
            {
                get
                {
                    float left = points[0].X, top = points[0].Y, right = left, bottom = top;
                    foreach (PointF point in points) { left = Math.Min(left, point.X); top = Math.Min(top, point.Y); right = Math.Max(right, point.X); bottom = Math.Max(bottom, point.Y); }
                    return RectangleF.FromLTRB(left - width / 2, top - width / 2, right + width / 2, bottom + width / 2);
                }
            }
            public override Annotation Translate(PointF delta)
            {
                PointF[] moved = new PointF[points.Length];
                for (int i = 0; i < moved.Length; i++) moved[i] = new PointF(points[i].X + delta.X, points[i].Y + delta.Y);
                return new StrokeAnnotation(moved, color, width, highlighter);
            }
            public override void Draw(Graphics graphics)
            {
                if (points.Length == 1)
                {
                    using (Brush brush = new SolidBrush(color))
                        graphics.FillEllipse(brush, points[0].X - width / 2, points[0].Y - width / 2, width, width);
                    return;
                }
                using (Pen pen = new Pen(color, width))
                using (GraphicsPath path = new GraphicsPath())
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    path.AddLines(points);
                    graphics.DrawPath(pen, path);
                }
            }
            public override bool HitTest(PointF point, float radius)
            {
                if (points.Length == 1) return Math.Sqrt(DistanceSquared(point, points[0])) <= radius + width / 2;
                for (int i = 1; i < points.Length; i++)
                    if (DistanceToSegment(point, points[i - 1], points[i]) <= radius + width / 2) return true;
                return false;
            }
        }

        internal sealed class ArrowAnnotation : Annotation, IStyledShape
        {
            private readonly PointF start, end;
            public ArrowAnnotation(PointF start, PointF end, Color color, float width)
                : this(start, end, new ShapeStyle(System.Drawing.Color.Empty, color, width)) { }
            internal ArrowAnnotation(PointF start, PointF end, ShapeStyle style)
            { this.start = start; this.end = end; Style = style; }
            public ShapeStyle Style { get; private set; }
            public EditorTool ShapeTool { get { return EditorTool.Arrow; } }
            public Annotation WithStyle(ShapeStyle style) { return new ArrowAnnotation(start, end, style); }
            internal PointF Start { get { return start; } }
            internal PointF End { get { return end; } }
            internal Color Color { get { return Style.EffectiveOutlineColor; } }
            internal float Width { get { return Style.OutlineWidth; } }
            public override RectangleF Bounds { get { RectangleF result = RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)); result.Inflate(Width * 5, Width * 5); return result; } }
            public override Annotation Translate(PointF delta) { return new ArrowAnnotation(new PointF(start.X + delta.X, start.Y + delta.Y), new PointF(end.X + delta.X, end.Y + delta.Y), Style); }
            public override void Draw(Graphics graphics)
            {
                if (!Style.VisibleOutline) return;
                using (Pen pen = Style.CreateOutlinePen())
                using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 5, true))
                {
                    pen.StartCap = LineCap.Round;
                    pen.CustomEndCap = cap;
                    graphics.DrawLine(pen, start, end);
                }
            }
            public override bool HitTest(PointF point, float radius)
            { return Style.VisibleOutline && DistanceToSegment(point, start, end) <= radius + Math.Max(Width / 2, Width * 4); }
            internal bool HitTestOutline(PointF point, float radius)
            {
                if (!Style.VisibleOutline) return false;
                if (DistanceToSegment(point, start, end) <= radius + Width / 2) return true;
                double dx = end.X - start.X, dy = end.Y - start.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0) return false;
                double fromTipX = end.X - point.X, fromTipY = end.Y - point.Y;
                double depth = (fromTipX * dx + fromTipY * dy) / length;
                double headDepth = Math.Min(length, Width * 5);
                if (depth < -radius || depth > headDepth + radius) return false;
                double sideways = Math.Abs(fromTipY * dx - fromTipX * dy) / length;
                return sideways <= radius + Width * 2 * Math.Max(0, Math.Min(1, depth / headDepth));
            }
        }

        internal sealed class RectangleAnnotation : ClosedShapeAnnotation
        {
            public RectangleAnnotation(RectangleF bounds, Color color, float width)
                : this(bounds, new ShapeStyle(Color.Empty, color, width)) { }
            internal RectangleAnnotation(RectangleF bounds, ShapeStyle style, float rotationDegrees = 0)
                : base(bounds, style, rotationDegrees, false) { }
            public override EditorTool ShapeTool { get { return EditorTool.Rectangle; } }
            internal override ClosedShapeAnnotation WithGeometry(RectangleF frame, ShapeStyle style, float rotationDegrees)
            { return new RectangleAnnotation(frame, style, rotationDegrees); }
            protected override void DrawGeometry(Graphics graphics, RectangleF bounds)
            {
                if (Style.VisibleFill)
                    using (Brush fill = new SolidBrush(Style.EffectiveFillColor)) graphics.FillRectangle(fill, bounds);
                if (Style.VisibleOutline)
                    using (Pen pen = Style.CreateOutlinePen())
                        graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            protected override bool ContainsGeometry(PointF point) { return GeometryBounds.Contains(point); }
            protected override bool HitTestGeometry(PointF point, float radius, bool includeFill)
            {
                RectangleF bounds = GeometryBounds;
                if (includeFill && Style.VisibleFill && bounds.Contains(point)) return true;
                if (!Style.VisibleOutline) return false;
                float tolerance = radius + Style.OutlineWidth / 2;
                return DistanceToSegment(point, new PointF(bounds.Left, bounds.Top), new PointF(bounds.Right, bounds.Top)) <= tolerance ||
                    DistanceToSegment(point, new PointF(bounds.Right, bounds.Top), new PointF(bounds.Right, bounds.Bottom)) <= tolerance ||
                    DistanceToSegment(point, new PointF(bounds.Right, bounds.Bottom), new PointF(bounds.Left, bounds.Bottom)) <= tolerance ||
                    DistanceToSegment(point, new PointF(bounds.Left, bounds.Bottom), new PointF(bounds.Left, bounds.Top)) <= tolerance;
            }
        }

        internal sealed class EllipseAnnotation : ClosedShapeAnnotation
        {
            internal EllipseAnnotation(RectangleF bounds, ShapeStyle style, float rotationDegrees = 0)
                : base(bounds, style, rotationDegrees, true) { }
            public override EditorTool ShapeTool { get { return EditorTool.Ellipse; } }
            internal override ClosedShapeAnnotation WithGeometry(RectangleF frame, ShapeStyle style, float rotationDegrees)
            { return new EllipseAnnotation(frame, style, rotationDegrees); }
            protected override void DrawGeometry(Graphics graphics, RectangleF bounds)
            {
                if (Style.VisibleFill)
                    using (Brush fill = new SolidBrush(Style.EffectiveFillColor)) graphics.FillEllipse(fill, bounds);
                if (Style.VisibleOutline)
                    using (Pen pen = Style.CreateOutlinePen()) graphics.DrawEllipse(pen, bounds);
            }
            protected override bool ContainsGeometry(PointF point)
            {
                RectangleF frame = GeometryBounds;
                double x = (point.X - frame.X) / frame.Width * 2 - 1;
                double y = (point.Y - frame.Y) / frame.Height * 2 - 1;
                return x * x + y * y <= 1;
            }
            protected override bool HitTestGeometry(PointF point, float radius, bool includeFill)
            {
                if (!Style.VisibleFill && !Style.VisibleOutline) return false;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddEllipse(GeometryBounds);
                    if (includeFill && Style.VisibleFill && path.IsVisible(point)) return true;
                    if (!Style.VisibleOutline) return false;
                    using (Pen hit = new Pen(Color.Black, Style.OutlineWidth + radius * 2)) return path.IsOutlineVisible(point, hit);
                }
            }
        }

        internal sealed class LineAnnotation : Annotation, IStyledShape
        {
            private readonly PointF start, end;
            internal LineAnnotation(PointF start, PointF end, ShapeStyle style) { this.start = start; this.end = end; Style = style; }
            internal PointF Start { get { return start; } }
            internal PointF End { get { return end; } }
            public ShapeStyle Style { get; private set; }
            public EditorTool ShapeTool { get { return EditorTool.Line; } }
            public Annotation WithStyle(ShapeStyle style) { return new LineAnnotation(start, end, style); }
            public override RectangleF Bounds { get { RectangleF result = RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)); result.Inflate(Style.OutlineWidth / 2, Style.OutlineWidth / 2); return result; } }
            public override Annotation Translate(PointF delta) { return new LineAnnotation(new PointF(start.X + delta.X, start.Y + delta.Y), new PointF(end.X + delta.X, end.Y + delta.Y), Style); }
            public override void Draw(Graphics graphics)
            {
                if (!Style.VisibleOutline) return;
                using (Pen pen = Style.CreateOutlinePen())
                { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; graphics.DrawLine(pen, start, end); }
            }
            public override bool HitTest(PointF point, float radius)
            { return Style.VisibleOutline && DistanceToSegment(point, start, end) <= radius + Style.OutlineWidth / 2; }
        }

        internal sealed class TextAnnotation : Annotation
        {
            // Content and source-pixel layout are immutable and shared by all
            // translated previews/history entries. Moving must not invoke native
            // rich-text layout, regenerate RTF, or allocate a font on each paint.
            private sealed class TextContent
            {
                internal readonly string Text, Rtf, FontFamily;
                internal readonly FontStyle Style;
                internal readonly Color Color;
                internal readonly float FontSize, LayoutWidth;
                internal readonly SizeF Size;
                internal readonly RectangleF InkBounds;

                internal TextContent(string plain, string rtf, Color color, float fontSize, float layoutWidth, string family, FontStyle style)
                {
                    Text = plain ?? "";
                    LayoutWidth = NormalizeTextWidth(layoutWidth);
                    Rtf = String.IsNullOrWhiteSpace(rtf) ? RichTextLayout.CreatePlainRtf(Text, family, fontSize, style, color) : rtf;
                    if (NormalizeRichPlain(RichTextLayout.GetPlainText(Rtf)) != NormalizeRichPlain(Text))
                        throw new ArgumentException("The formatted text does not match its plain-text content.", "rtf");
                    FontFamily = RichTextLayout.GetFirstFontFamily(Rtf);
                    Style = RichTextLayout.GetFirstFontStyle(Rtf);
                    FontSize = RichTextLayout.GetFirstFontSize(Rtf);
                    Color = RichTextLayout.GetFirstColor(Rtf);
                    RichTextMetrics metrics = RichTextLayout.Measure(Rtf, LayoutWidth);
                    Size = metrics.Size;
                    InkBounds = metrics.InkBounds;
                }
            }

            private readonly TextContent content;
            private readonly PointF location;
            private readonly float minimumHeight;
            private readonly RectangleF bounds;
            private readonly RectangleF inkBounds;
            public TextAnnotation(string text, PointF location, Color color, float fontSize)
                : this(text, location, color, fontSize, 0) { }
            public TextAnnotation(string text, PointF location, Color color, float fontSize, float layoutWidth)
                : this(text, null, location, color, fontSize, layoutWidth, "Segoe UI", System.Drawing.FontStyle.Regular) { }

            internal TextAnnotation(string plain, string rtf, PointF location, Color color, float fontSize, float layoutWidth, string family, FontStyle style)
                : this(plain, rtf, location, color, fontSize, layoutWidth, 0, family, style) { }

            internal TextAnnotation(string plain, string rtf, PointF location, Color color, float fontSize, float layoutWidth, float minimumHeight, string family, FontStyle style)
            {
                ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth); ValidateTextHeight(minimumHeight);
                content = new TextContent(plain, rtf, color, fontSize, layoutWidth, family, style);
                this.location = location;
                this.minimumHeight = NormalizeTextWidth(minimumHeight);
                bounds = new RectangleF(location, new SizeF(content.Size.Width, Math.Max(content.Size.Height, this.minimumHeight)));
                inkBounds = content.InkBounds;
                inkBounds.Offset(location);
            }

            private TextAnnotation(TextContent content, PointF location, float minimumHeight)
            {
                ValidatePoint(location); ValidateTextHeight(minimumHeight);
                this.content = content;
                this.location = location;
                this.minimumHeight = NormalizeTextWidth(minimumHeight);
                bounds = new RectangleF(location, new SizeF(content.Size.Width, Math.Max(content.Size.Height, this.minimumHeight)));
                inkBounds = content.InkBounds;
                inkBounds.Offset(location);
            }

            internal string Text { get { return content.Text; } }
            internal string Rtf { get { return content.Rtf; } }
            internal string FontFamily { get { return content.FontFamily; } }
            internal FontStyle FontStyle { get { return content.Style; } }
            internal PointF Location { get { return location; } }
            internal Color Color { get { return content.Color; } }
            internal float FontSize { get { return content.FontSize; } }
            internal float LayoutWidth { get { return content.LayoutWidth; } }
            internal float MinimumHeight { get { return minimumHeight; } }
            internal RectangleF InkBounds { get { return inkBounds; } }

            internal TextAnnotation WithGeometry(PointF newLocation, float newFontSize, float newWidth)
            {
                return WithGeometry(newLocation, newFontSize, newWidth, MinimumHeight);
            }

            internal TextAnnotation WithGeometry(PointF newLocation, float newFontSize, float newWidth, float newHeight)
            {
                newWidth = NormalizeTextWidth(newWidth);
                ValidateTextHeight(newHeight);
                if (newFontSize == FontSize && newWidth == LayoutWidth) return new TextAnnotation(content, newLocation, newHeight);
                string rtf = newFontSize == FontSize ? Rtf : RichTextLayout.SetFontSize(Rtf, newFontSize);
                return new TextAnnotation(Text, rtf, newLocation, Color, newFontSize, newWidth, newHeight, FontFamily, FontStyle);
            }

            internal TextAnnotation WithTextAndFormatting(string text, Color color, float fontSize, float layoutWidth)
            {
                string rtf;
                if (text == Text)
                {
                    rtf = Rtf;
                    if (fontSize != FontSize) rtf = RichTextLayout.SetFontSize(rtf, fontSize);
                    if (color.ToArgb() != Color.ToArgb()) rtf = RichTextLayout.SetColor(rtf, color);
                }
                else rtf = RichTextLayout.CreatePlainRtf(text, FontFamily, fontSize, FontStyle, color);
                if (rtf == Rtf && NormalizeTextWidth(layoutWidth) == LayoutWidth && text == Text) return new TextAnnotation(content, location, MinimumHeight);
                return new TextAnnotation(text, rtf, location, color, fontSize, layoutWidth, MinimumHeight, FontFamily, FontStyle);
            }

            public override void Draw(Graphics graphics)
            {
                RichTextLayout.Draw(graphics, Rtf, LayoutWidth, location);
            }
            public override bool HitTest(PointF point, float radius)
            {
                RectangleF expanded = bounds;
                expanded.Inflate(radius, radius);
                return expanded.Contains(point);
            }
            public override RectangleF Bounds { get { return bounds; } }
            public override Annotation Translate(PointF delta) { return new TextAnnotation(content, new PointF(location.X + delta.X, location.Y + delta.Y), MinimumHeight); }
        }

        internal sealed class HighlightBandsAnnotation : Annotation
        {
            private readonly RectangleF[] bands;
            private readonly Color color;
            public HighlightBandsAnnotation(RectangleF[] bands, Color color) { this.bands = (RectangleF[])bands.Clone(); this.color = color; }
            internal int BandCount { get { return bands.Length; } }
            internal RectangleF GetBand(int index) { return bands[index]; }
            internal Color Ink { get { return color; } }
            public override void Draw(Graphics graphics)
            {
                using (Brush brush = new SolidBrush(color)) foreach (RectangleF band in bands) graphics.FillRectangle(brush, band);
            }
            public override bool HitTest(PointF point, float radius)
            {
                foreach (RectangleF band in bands) { RectangleF hit = band; hit.Inflate(radius, radius); if (hit.Contains(point)) return true; }
                return false;
            }
            public override RectangleF Bounds { get { RectangleF result = bands[0]; for (int i = 1; i < bands.Length; i++) result = RectangleF.Union(result, bands[i]); return result; } }
            public override Annotation Translate(PointF delta)
            {
                RectangleF[] moved = (RectangleF[])bands.Clone();
                for (int i = 0; i < moved.Length; i++) moved[i].Offset(delta);
                return new HighlightBandsAnnotation(moved, color);
            }
        }
    }
}
