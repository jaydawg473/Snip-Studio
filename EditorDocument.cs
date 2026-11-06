using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnipStudio
{
    public enum EditorTool { Select, Pen, Highlighter, Arrow, Rectangle, Text, Eraser, Crop, RemoveArea }

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
        public RectangleF Bounds { get; private set; }
        public RectangleF InkBounds { get; private set; }
        internal TextAnnotationInfo(string text, string rtf, string fontFamily, FontStyle fontStyle, PointF location, Color color, float fontSize, float layoutWidth, RectangleF bounds, RectangleF inkBounds)
        { Text = text; Rtf = rtf; FontFamily = fontFamily; FontStyle = fontStyle; Location = location; Color = color; FontSize = fontSize; LayoutWidth = layoutWidth; Bounds = bounds; InkBounds = inkBounds; }
    }

    // Immutable annotations make undo inexpensive: ordinary edits share the captured bitmap.
    public sealed class EditorDocument : IDisposable
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
            ClearStates();
            states.Add(new DocumentState(new ImageFrame(copy), new List<Annotation>(), ++nextRevision));
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
            return text == null ? null : new TextAnnotationInfo(text.Text, text.Rtf, text.FontFamily, text.FontStyle, text.Location, text.Color, text.FontSize, text.LayoutWidth, text.Bounds, text.InkBounds);
        }

        public bool IsArrowAnnotation(int index)
        {
            ThrowIfDisposed();
            return HasImage && index >= 0 && index < AnnotationCount && Current.Annotations[index] is ArrowAnnotation;
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
            return new ArrowAnnotation(start, end, arrow.Color, arrow.Width);
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

        public bool UpdateTextAnnotationBounds(int index, PointF location, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            layoutWidth = NormalizeTextWidth(layoutWidth);
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            if (location == original.Location && fontSize == original.FontSize && layoutWidth == original.LayoutWidth) return false;
            return ReplaceAnnotation(index, original.WithGeometry(location, fontSize, layoutWidth));
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
            if (original == null) return false;
            if (String.IsNullOrEmpty(plain)) return RemoveAnnotation(index);
            layoutWidth = NormalizeTextWidth(layoutWidth);
            TextAnnotation replacement = new TextAnnotation(plain, rtf, original.Location, color, fontSize, layoutWidth, original.FontFamily, original.FontStyle);
            if (replacement.Text == original.Text && replacement.Rtf == original.Rtf && replacement.LayoutWidth == original.LayoutWidth) return false;
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
            float visibleX = Math.Min(16, Math.Max(1, bounds.Width));
            float visibleY = Math.Min(16, Math.Max(1, bounds.Height));
            return new PointF(Math.Max(visibleX - bounds.Right, Math.Min(ImageSize.Width - visibleX - bounds.Left, delta.X)),
                Math.Max(visibleY - bounds.Bottom, Math.Min(ImageSize.Height - visibleY - bounds.Top, delta.Y)));
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

        public void AddHighlightBands(IEnumerable<RectangleF> bands, Color color)
        {
            RequireImage();
            if (bands == null) throw new ArgumentNullException("bands");
            List<RectangleF> valid = new List<RectangleF>();
            foreach (RectangleF bounds in bands)
            {
                ValidatePoint(bounds.Location); ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
                if (bounds.Width > 0 && bounds.Height > 0) valid.Add(bounds);
            }
            if (valid.Count > 0) Add(new HighlightBandsAnnotation(valid.ToArray(), Color.FromArgb(80, color.R, color.G, color.B)));
        }

        public void AddStroke(IEnumerable<PointF> points, Color color, float width, bool highlighter)
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

        public void AddText(string text, PointF location, Color color, float fontSize)
        {
            AddText(text, location, color, fontSize, 0);
        }

        public void AddText(string text, PointF location, Color color, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            if (String.IsNullOrWhiteSpace(text)) return;
            Add(new TextAnnotation(text, location, color, fontSize, layoutWidth));
        }

        public void AddRichText(string plain, string rtf, PointF location, Color color, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            if (String.IsNullOrWhiteSpace(plain)) return;
            Add(new TextAnnotation(plain, rtf, location, color, fontSize, layoutWidth, "Segoe UI", FontStyle.Regular));
        }

        public int ReplaceImageText(Rectangle eraseBounds, string text, Color foreground, Color background, float fontSize, float layoutWidth)
        {
            return ReplaceImageRichText(eraseBounds, text, null, foreground, background, fontSize, layoutWidth);
        }

        public int ReplaceImageRichText(Rectangle eraseBounds, string plain, string rtf, Color foreground, Color background, float fontSize, float layoutWidth)
        {
            RequireImage(); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
            Rectangle clipped = ClipPixelRectangle(eraseBounds);
            if (clipped.IsEmpty) throw new ArgumentException("The text region must overlap the image.", "eraseBounds");
            List<Annotation> annotations = new List<Annotation>(Current.Annotations);
            int addedIndex = -1;
            if (!String.IsNullOrWhiteSpace(plain))
            {
                addedIndex = annotations.Count;
                annotations.Add(new TextAnnotation(plain, rtf, clipped.Location, foreground, fontSize, layoutWidth, "Segoe UI", FontStyle.Regular));
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

        internal int ReplaceRecognizedTextBlocks(IList<RecognizedTextReplacement> replacements,
            object expectedImageIdentity, long expectedRevision)
        {
            RequireImage();
            if (replacements == null) throw new ArgumentNullException("replacements");
            if (replacements.Count == 0) return -1;
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
                // Construct and validate every immutable annotation first. RTF
                // failures cannot leave a half-translated document or extra Undo.
                TextAnnotation annotation = new TextAnnotation(replacement.PlainText, replacement.Rtf, replacement.TextLocation,
                    replacement.Foreground, replacement.FontSize, replacement.LayoutWidth, "Segoe UI", FontStyle.Regular);
                RectangleF ink = annotation.InkBounds;
                if (ink.IsEmpty || ink.Left < clipped.Left || ink.Top < clipped.Top || ink.Right > clipped.Right || ink.Bottom > clipped.Bottom)
                    throw new ArgumentException("The complete translated text must fit inside its original source region.", "replacements");
                text.Add(annotation);
                masks.Add(clipped);
                backgrounds.Add(Color.FromArgb(255, replacement.Background));
            }
            if (!Object.ReferenceEquals(expectedImageIdentity, BaseIdentity) || expectedRevision != Revision) return -1;

            // Sampled flat-color masks cover only source raster text. The
            // original capture is shared, untouched, across this single history
            // state. Existing highlights/shapes/notes retain their draw order.
            List<Annotation> annotations = new List<Annotation>();
            annotations.Add(new SourceTextMasksAnnotation(masks.ToArray(), backgrounds.ToArray()));
            annotations.AddRange(Current.Annotations);
            annotations.AddRange(text);
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, annotations, ++nextRevision));
            return annotations.Count - 1;
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

        private static Bitmap CopyImage(Bitmap image)
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

            private SourceTextMasksAnnotation(RectangleF[] regions, Color[] backgrounds, RectangleF bounds)
            { this.regions = regions; this.backgrounds = backgrounds; this.bounds = bounds; }

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
                this.color = highlighter ? Color.FromArgb(80, color.R, color.G, color.B) : color;
                this.width = width;
                this.highlighter = highlighter;
            }
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

        internal sealed class ArrowAnnotation : Annotation
        {
            private readonly PointF start, end;
            private readonly Color color;
            private readonly float width;
            public ArrowAnnotation(PointF start, PointF end, Color color, float width)
            { this.start = start; this.end = end; this.color = color; this.width = width; }
            internal PointF Start { get { return start; } }
            internal PointF End { get { return end; } }
            internal Color Color { get { return color; } }
            internal float Width { get { return width; } }
            public override RectangleF Bounds { get { RectangleF result = RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)); result.Inflate(width * 5, width * 5); return result; } }
            public override Annotation Translate(PointF delta) { return new ArrowAnnotation(new PointF(start.X + delta.X, start.Y + delta.Y), new PointF(end.X + delta.X, end.Y + delta.Y), color, width); }
            public override void Draw(Graphics graphics)
            {
                using (Pen pen = new Pen(color, width))
                using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 5, true))
                {
                    pen.StartCap = LineCap.Round;
                    pen.CustomEndCap = cap;
                    graphics.DrawLine(pen, start, end);
                }
            }
            public override bool HitTest(PointF point, float radius)
            { return DistanceToSegment(point, start, end) <= radius + Math.Max(width / 2, width * 4); }
        }

        internal sealed class RectangleAnnotation : Annotation
        {
            private readonly RectangleF bounds;
            private readonly Color color;
            private readonly float width;
            public RectangleAnnotation(RectangleF bounds, Color color, float width)
            { this.bounds = bounds; this.color = color; this.width = width; }
            public override RectangleF Bounds { get { RectangleF result = bounds; result.Inflate(width / 2, width / 2); return result; } }
            public override Annotation Translate(PointF delta) { RectangleF result = bounds; result.Offset(delta); return new RectangleAnnotation(result, color, width); }
            public override void Draw(Graphics graphics)
            {
                using (Pen pen = new Pen(color, width))
                    graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            public override bool HitTest(PointF point, float radius)
            {
                float tolerance = radius + width / 2;
                return DistanceToSegment(point, new PointF(bounds.Left, bounds.Top), new PointF(bounds.Right, bounds.Top)) <= tolerance ||
                    DistanceToSegment(point, new PointF(bounds.Right, bounds.Top), new PointF(bounds.Right, bounds.Bottom)) <= tolerance ||
                    DistanceToSegment(point, new PointF(bounds.Right, bounds.Bottom), new PointF(bounds.Left, bounds.Bottom)) <= tolerance ||
                    DistanceToSegment(point, new PointF(bounds.Left, bounds.Bottom), new PointF(bounds.Left, bounds.Top)) <= tolerance;
            }
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
            private readonly RectangleF bounds;
            private readonly RectangleF inkBounds;
            public TextAnnotation(string text, PointF location, Color color, float fontSize)
                : this(text, location, color, fontSize, 0) { }
            public TextAnnotation(string text, PointF location, Color color, float fontSize, float layoutWidth)
                : this(text, null, location, color, fontSize, layoutWidth, "Segoe UI", System.Drawing.FontStyle.Regular) { }

            internal TextAnnotation(string plain, string rtf, PointF location, Color color, float fontSize, float layoutWidth, string family, FontStyle style)
            {
                ValidatePoint(location); ValidatePositive(fontSize, "fontSize"); ValidateTextWidth(layoutWidth);
                content = new TextContent(plain, rtf, color, fontSize, layoutWidth, family, style);
                this.location = location;
                bounds = new RectangleF(location, content.Size);
                inkBounds = content.InkBounds;
                inkBounds.Offset(location);
            }

            private TextAnnotation(TextContent content, PointF location)
            {
                ValidatePoint(location);
                this.content = content;
                this.location = location;
                bounds = new RectangleF(location, content.Size);
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
            internal RectangleF InkBounds { get { return inkBounds; } }

            internal TextAnnotation WithGeometry(PointF newLocation, float newFontSize, float newWidth)
            {
                newWidth = NormalizeTextWidth(newWidth);
                if (newFontSize == FontSize && newWidth == LayoutWidth) return new TextAnnotation(content, newLocation);
                string rtf = newFontSize == FontSize ? Rtf : RichTextLayout.SetFontSize(Rtf, newFontSize);
                return new TextAnnotation(Text, rtf, newLocation, Color, newFontSize, newWidth, FontFamily, FontStyle);
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
                if (rtf == Rtf && NormalizeTextWidth(layoutWidth) == LayoutWidth && text == Text) return new TextAnnotation(content, location);
                return new TextAnnotation(text, rtf, location, color, fontSize, layoutWidth, FontFamily, FontStyle);
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
            public override Annotation Translate(PointF delta) { return new TextAnnotation(content, new PointF(location.X + delta.X, location.Y + delta.Y)); }
        }

        internal sealed class HighlightBandsAnnotation : Annotation
        {
            private readonly RectangleF[] bands;
            private readonly Color color;
            public HighlightBandsAnnotation(RectangleF[] bands, Color color) { this.bands = (RectangleF[])bands.Clone(); this.color = color; }
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
