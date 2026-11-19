#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;

namespace SnipStudio.WinUI.Backend
{
    public sealed partial class EditorSession
    {
        private const int MaximumTextCharacters = 16000;
        private const int MaximumRtfCharacters = 1024 * 1024;
        private static readonly IReadOnlyList<EmojiStickerSnapshot> emojiCatalog = CreateEmojiCatalog();

        public int AnnotationCount { get { VerifyAccess(); return document.AnnotationCount; } }
        public IReadOnlyList<EmojiStickerSnapshot> EmojiCatalog { get { VerifyAccess(); return emojiCatalog; } }

        public int HitTest(ImagePoint point, float radius = 4)
        {
            VerifyAccess();
            RequireImage();
            ValidateRadius(radius);
            return document.HitTestAnnotation(ConvertPoint(point, nameof(point)), radius);
        }

        public AnnotationSnapshot? GetAnnotation(int index)
        {
            VerifyAccess();
            if (!document.HasImage || index < 0 || index >= document.AnnotationCount) return null;
            ImageRectangle bounds = ToImageRectangle(document.GetAnnotationBounds(index));
            global::SnipStudio.TextAnnotationInfo? text = document.GetTextAnnotation(index);
            if (text != null)
                return new AnnotationSnapshot(index, document.Revision, AnnotationKind.Text, bounds,
                    new TextAnnotationSnapshot(text.Text, text.Rtf, text.FontFamily, (TextFontStyle)text.FontStyle,
                        ToImagePoint(text.Location), ToArgb(text.Color), text.FontSize, text.LayoutWidth,
                        text.MinimumHeight, ToImageRectangle(text.InkBounds)), null, null) { Owner = this };
            global::SnipStudio.EmojiAnnotationInfo? emoji = document.GetEmojiAnnotation(index);
            if (emoji != null)
                return new AnnotationSnapshot(index, document.Revision, AnnotationKind.Emoji, bounds, null, null,
                    new EmojiAnnotationSnapshot(emoji.Key, emoji.Glyph, emoji.DisplayName,
                        ToImageRectangle(emoji.Frame), emoji.RotationDegrees)) { Owner = this };
            global::SnipStudio.EditorTool? shapeTool = document.GetShapeAnnotationTool(index);
            global::SnipStudio.ShapeStyle? style = document.GetShapeAnnotationStyle(index);
            if (shapeTool.HasValue && style != null)
            {
                PointF[] endpoints = document.GetShapeEndpoints(index);
                ImagePoint[] copiedEndpoints = new ImagePoint[endpoints.Length];
                for (int at = 0; at < endpoints.Length; at++) copiedEndpoints[at] = ToImagePoint(endpoints[at]);
                RectangleF geometry = document.GetClosedShapeBounds(index);
                if (endpoints.Length == 2)
                    geometry = RectangleF.FromLTRB(Math.Min(endpoints[0].X, endpoints[1].X), Math.Min(endpoints[0].Y, endpoints[1].Y),
                        Math.Max(endpoints[0].X, endpoints[1].X), Math.Max(endpoints[0].Y, endpoints[1].Y));
                return new AnnotationSnapshot(index, document.Revision, AnnotationKind.Shape, bounds, null,
                    new ShapeAnnotationSnapshot(FromLegacyShape(shapeTool.Value), style.FillEnabled ? ToArgb(style.FillColor) : null,
                        style.OutlineEnabled ? ToArgb(style.OutlineColor) : null, style.OutlineWidth,
                        style.FillOpacity, style.OutlineOpacity, ToImageRectangle(geometry), Array.AsReadOnly(copiedEndpoints)), null) { Owner = this };
            }
            return new AnnotationSnapshot(index, document.Revision, AnnotationKind.Ink, bounds, null, null, null) { Owner = this };
        }

        public IReadOnlyList<AnnotationSnapshot> GetAnnotations()
        {
            VerifyAccess();
            AnnotationSnapshot[] snapshots = new AnnotationSnapshot[document.AnnotationCount];
            for (int index = 0; index < snapshots.Length; index++) snapshots[index] = GetAnnotation(index)!;
            return Array.AsReadOnly(snapshots);
        }

        public bool IsAnnotationCurrent(AnnotationSnapshot snapshot)
        {
            VerifyAccess();
            ArgumentNullException.ThrowIfNull(snapshot);
            return ReferenceEquals(snapshot.Owner, this) && document.HasImage && snapshot.Revision == document.Revision && snapshot.Index >= 0 &&
                snapshot.Index < document.AnnotationCount;
        }

        public bool AddShape(AnnotationShape shape, ImagePoint start, ImagePoint end,
            uint outlineArgb, float width, uint? fillArgb = null)
        {
            VerifyCanMutate();
            RequireImage();
            ValidateWidth(width);
            return document.AddShape(ToLegacyShape(shape), ConvertPoint(start, nameof(start)), ConvertPoint(end, nameof(end)),
                new global::SnipStudio.ShapeStyle(fillArgb.HasValue ? FromArgb(fillArgb.Value) : Color.Empty, FromArgb(outlineArgb), width));
        }

        public bool UpdateShapeStyle(int index, uint? fillArgb, uint? outlineArgb, float outlineWidth,
            int fillOpacity = 100, int outlineOpacity = 100)
        {
            VerifyCanMutate();
            RequireImage();
            ValidateWidth(outlineWidth);
            return document.UpdateShapeAnnotationStyle(index, new global::SnipStudio.ShapeStyle(
                fillArgb.HasValue ? FromArgb(fillArgb.Value) : Color.Empty,
                outlineArgb.HasValue ? FromArgb(outlineArgb.Value) : Color.Empty, outlineWidth, fillOpacity, outlineOpacity));
        }

        public bool UpdateShapeEndpoints(int index, ImagePoint start, ImagePoint end)
        {
            VerifyCanMutate();
            RequireImage();
            return document.ReplaceShapeEndpoints(index, ConvertPoint(start, nameof(start)), ConvertPoint(end, nameof(end)));
        }

        /// <summary>Edits line/arrow endpoints and styling in one Undo step.</summary>
        public bool UpdateShapeEndpoints(AnnotationSnapshot selection, ImagePoint start, ImagePoint end,
            ShapeStyleEdit? style = null)
        {
            RequireCurrentSelection(selection);
            if (selection.Shape is null || selection.Shape.Endpoints.Count != 2)
                throw new ArgumentException("Select a line or arrow before editing its endpoints.", nameof(selection));
            PointF first = start == selection.Shape.Endpoints[0] ? ToFinitePoint(start, nameof(start)) : ConvertPoint(start, nameof(start));
            PointF last = end == selection.Shape.Endpoints[1] ? ToFinitePoint(end, nameof(end)) : ConvertPoint(end, nameof(end));
            return document.ReplaceShapeEndpoints(selection.Index, first, last, style is null ? null : ConvertShapeStyle(style));
        }

        public bool MoveAnnotation(int index, ImagePoint delta)
        {
            VerifyCanMutate();
            RequireImage();
            return document.MoveAnnotation(index, ToFinitePoint(delta, nameof(delta)));
        }

        public bool MoveAnnotation(AnnotationSnapshot snapshot, ImagePoint delta)
        {
            RequireCurrentSelection(snapshot);
            return MoveAnnotation(snapshot.Index, delta);
        }

        public bool RemoveAnnotation(int index)
        {
            VerifyCanMutate();
            RequireImage();
            return document.RemoveAnnotation(index);
        }

        public bool RemoveAnnotation(AnnotationSnapshot snapshot)
        {
            RequireCurrentSelection(snapshot);
            return RemoveAnnotation(snapshot.Index);
        }

        public bool EraseAt(ImagePoint point, float radius)
        {
            VerifyCanMutate();
            RequireImage();
            ValidateRadius(radius);
            return document.Erase(ConvertPoint(point, nameof(point)), radius);
        }

        /// <summary>Erases every touched overlay as a single Undo transaction. Source pixels are kept.</summary>
        public bool ErasePath(IReadOnlyList<ImagePoint> points, float radius)
        {
            VerifyCanMutate();
            RequireImage();
            ArgumentNullException.ThrowIfNull(points);
            ValidateRadius(radius);
            if (points.Count > MaximumStrokePoints)
                throw new ArgumentException("Finish this eraser gesture and start another to continue.", nameof(points));
            PointF[] copy = new PointF[points.Count];
            for (int index = 0; index < copy.Length; index++) copy[index] = ConvertPoint(points[index], nameof(points));
            return document.EraseAnnotationsAlongPath(copy, radius);
        }

        public bool EraseAllAnnotations()
        {
            VerifyCanMutate();
            return document.EraseAllAnnotations();
        }

        public bool Crop(ImagePoint start, ImagePoint end)
        {
            VerifyCanMutate();
            RequireImage();
            Rectangle pixels = PixelRectangle(start, end);
            if (pixels.Width < 2 || pixels.Height < 2) throw new ArgumentException("Select an area at least 2 by 2 pixels.");
            long previous = document.Revision;
            document.Crop(pixels);
            return document.Revision != previous;
        }

        /// <summary>Fills only the reviewed source-pixel area; existing annotation objects remain editable.</summary>
        public bool RemoveArea(ImagePoint start, ImagePoint end, uint backgroundArgb)
        {
            VerifyCanMutate();
            RequireImage();
            Rectangle pixels = PixelRectangle(start, end);
            if (pixels.IsEmpty) return false;
            return document.EraseImageRegions(new[] { pixels }, FromArgb(backgroundArgb));
        }

        public bool AddEmoji(string key, ImagePoint start, ImagePoint end, float rotationDegrees = 0)
        {
            VerifyCanMutate();
            RequireImage();
            ArgumentNullException.ThrowIfNull(key);
            PointF first = ConvertPoint(start, nameof(start)), last = ConvertPoint(end, nameof(end));
            return document.AddEmoji(key, Between(first, last), rotationDegrees);
        }

        public bool ResizeAnnotation(int index, ImageRectangle bounds)
        {
            VerifyCanMutate();
            RequireImage();
            AnnotationSnapshot? original = GetAnnotation(index);
            if (original is null) return false;
            if (bounds == ObjectFrame(original)) return false;
            RectangleF frame = ConvertFrame(bounds, nameof(bounds), original);
            global::SnipStudio.TextAnnotationInfo? text = document.GetTextAnnotation(index);
            if (text != null)
                return document.UpdateTextAnnotationBounds(index, frame.Location, text.FontSize, frame.Width, frame.Height);
            global::SnipStudio.EmojiAnnotationInfo? emoji = document.GetEmojiAnnotation(index);
            if (emoji != null) return document.UpdateEmojiAnnotationBounds(index, frame, emoji.RotationDegrees);
            global::SnipStudio.EditorTool? shape = document.GetShapeAnnotationTool(index);
            if (shape == global::SnipStudio.EditorTool.Rectangle || shape == global::SnipStudio.EditorTool.Ellipse)
                return document.UpdateClosedShapeBounds(index, frame);
            PointF[] endpoints = document.GetShapeEndpoints(index);
            if (endpoints.Length != 2) return false;
            float minimumX = Math.Min(endpoints[0].X, endpoints[1].X), minimumY = Math.Min(endpoints[0].Y, endpoints[1].Y);
            float spanX = Math.Abs(endpoints[0].X - endpoints[1].X), spanY = Math.Abs(endpoints[0].Y - endpoints[1].Y);
            PointF Scale(PointF point) => new(frame.Left + (spanX == 0 ? .5f : (point.X - minimumX) / spanX) * frame.Width,
                frame.Top + (spanY == 0 ? .5f : (point.Y - minimumY) / spanY) * frame.Height);
            return document.ReplaceShapeEndpoints(index, Scale(endpoints[0]), Scale(endpoints[1]));
        }

        /// <summary>Applies an object-properties dialog in one revision and one Undo step.</summary>
        public bool UpdateAnnotationProperties(AnnotationSnapshot selection, ImageRectangle bounds,
            ShapeStyleEdit? style = null, float? rotationDegrees = null)
        {
            RequireCurrentSelection(selection);
            RectangleF frame = ConvertFrame(bounds, nameof(bounds), selection);
            global::SnipStudio.ShapeStyle? convertedStyle = null;
            if (style != null)
            {
                if (selection.Kind != AnnotationKind.Shape)
                    throw new ArgumentException("Shape styling applies to shape objects only.", nameof(style));
                convertedStyle = ConvertShapeStyle(style);
            }
            if (rotationDegrees.HasValue && (!float.IsFinite(rotationDegrees.Value) || selection.Kind != AnnotationKind.Emoji))
                throw new ArgumentException("Only emoji stickers support a finite rotation angle.", nameof(rotationDegrees));
            if (bounds == ObjectFrame(selection) && style is null && rotationDegrees is null) return false;
            return document.UpdateAdapterAnnotationProperties(selection.Index, frame, convertedStyle, rotationDegrees);
        }

        /// <summary>The legacy document supports rotation for emoji stickers.</summary>
        public bool RotateAnnotation(int index, float rotationDegrees)
        {
            VerifyCanMutate();
            RequireImage();
            if (!float.IsFinite(rotationDegrees)) throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
            global::SnipStudio.EmojiAnnotationInfo? emoji = document.GetEmojiAnnotation(index);
            return emoji != null && document.UpdateEmojiAnnotationBounds(index, emoji.Frame, rotationDegrees);
        }

        public int AddText(string text, ImagePoint location, uint argb, float fontSize,
            float layoutWidth = 0, string? rtf = null, float minimumHeight = 0)
        {
            VerifyCanMutate();
            RequireImage();
            ValidateText(text, fontSize, layoutWidth, minimumHeight, rtf);
            PointF point = ConvertPoint(location, nameof(location));
            int previousCount = document.AnnotationCount;
            if (rtf is null) document.AddText(text, point, FromArgb(argb), fontSize, layoutWidth, minimumHeight);
            else document.AddRichText(text, rtf, point, FromArgb(argb), fontSize, layoutWidth, minimumHeight);
            return document.AnnotationCount > previousCount ? previousCount : -1;
        }

        /// <summary>RTF, when supplied, is authoritative for character styling and must match text exactly.</summary>
        public bool UpdateText(int index, string text, uint argb, float fontSize, float layoutWidth,
            string? rtf = null, float minimumHeight = 0)
        {
            VerifyCanMutate();
            RequireImage();
            ValidateText(text, fontSize, layoutWidth, minimumHeight, rtf);
            if (rtf is not null)
                return document.UpdateRichTextAnnotation(index, text, rtf, FromArgb(argb), fontSize, layoutWidth, minimumHeight);
            // Retain original RTF runs when content is unchanged and keep its
            // first source family/style when plain content is edited. Frame
            // height participates in this same commit rather than being lost.
            return document.UpdateAdapterPlainText(index, text, FromArgb(argb), fontSize, layoutWidth, minimumHeight);
        }

        public bool UpdateTextFontSize(int index, float fontSize)
        {
            VerifyCanMutate();
            RequireImage();
            ValidateFontSize(fontSize);
            return document.UpdateTextAnnotationFontSize(index, fontSize);
        }

        public bool UpdateTextColor(int index, uint argb)
        {
            VerifyCanMutate();
            RequireImage();
            return document.UpdateTextAnnotationColor(index, FromArgb(argb));
        }

        public bool RemoveTextRange(int index, int start, int length)
        {
            VerifyCanMutate();
            RequireImage();
            return document.RemoveTextAnnotationRange(index, start, length);
        }

        public string CreateTextRtf(string text, string family, float fontSize, TextFontStyle style, uint argb)
        {
            VerifyAccess();
            ValidateText(text, fontSize, 0, 0, null);
            if (string.IsNullOrWhiteSpace(family) || family.Length > 256)
                throw new ArgumentException("Choose a font family of at most 256 characters.", nameof(family));
            if ((style & ~(TextFontStyle.Bold | TextFontStyle.Italic | TextFontStyle.Underline | TextFontStyle.Strikeout)) != 0)
                throw new ArgumentOutOfRangeException(nameof(style));
            return global::SnipStudio.RichTextLayout.CreatePlainRtf(text, family, fontSize, (FontStyle)style, FromArgb(argb));
        }

        private void RequireCurrentSelection(AnnotationSnapshot snapshot)
        {
            VerifyCanMutate();
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!IsAnnotationCurrent(snapshot))
                throw new InvalidOperationException("This selection belongs to an earlier image revision. Select the object again.");
        }

        private Rectangle PixelRectangle(ImagePoint start, ImagePoint end)
        {
            PointF first = ConvertPoint(start, nameof(start)), last = ConvertPoint(end, nameof(end));
            return Rectangle.FromLTRB((int)Math.Floor(Math.Min(first.X, last.X)), (int)Math.Floor(Math.Min(first.Y, last.Y)),
                (int)Math.Ceiling(Math.Max(first.X, last.X)), (int)Math.Ceiling(Math.Max(first.Y, last.Y)));
        }

        private RectangleF ConvertFrame(ImageRectangle bounds, string name, AnnotationSnapshot? original = null)
        {
            bool line = original?.Shape?.Shape is AnnotationShape.Line or AnnotationShape.Arrow;
            if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                (line ? bounds.Width < 0 || bounds.Height < 0 || Math.Max(bounds.Width, bounds.Height) < 1 : bounds.Width < 1 || bounds.Height < 1))
                throw new ArgumentOutOfRangeException(name, line ? "A line must span at least one image pixel." : "Choose a frame at least one pixel wide and high.");
            // Cropping keeps immutable objects, including geometry extending
            // outside the new frame. A no-op properties change or style-only
            // edit must retain those exact coordinates rather than clamp them.
            bool unchanged = original != null && bounds == ObjectFrame(original);
            PointF first = unchanged ? ToFinitePoint(new ImagePoint(bounds.Left, bounds.Top), name) : ConvertPoint(new ImagePoint(bounds.Left, bounds.Top), name);
            PointF last = unchanged ? ToFinitePoint(new ImagePoint(bounds.Right, bounds.Bottom), name) : ConvertPoint(new ImagePoint(bounds.Right, bounds.Bottom), name);
            return RectangleF.FromLTRB(first.X, first.Y, last.X, last.Y);
        }

        private static ImageRectangle ObjectFrame(AnnotationSnapshot snapshot)
        {
            if (snapshot.Shape != null) return snapshot.Shape.GeometryBounds;
            if (snapshot.Emoji != null) return snapshot.Emoji.Frame;
            if (snapshot.Text != null)
                return new ImageRectangle(snapshot.Text.Location.X, snapshot.Text.Location.Y,
                    snapshot.Text.LayoutWidth > 0 ? snapshot.Text.LayoutWidth : snapshot.Bounds.Width,
                    snapshot.Text.MinimumHeight > 0 ? snapshot.Text.MinimumHeight : snapshot.Bounds.Height);
            return snapshot.Bounds;
        }

        private static void ValidateText(string text, float fontSize, float width, float height, string? rtf)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length > MaximumTextCharacters)
                throw new ArgumentException("Use at most 16,000 characters in one text note.", nameof(text));
            if (rtf != null && rtf.Length > MaximumRtfCharacters)
                throw new ArgumentException("This formatted text is too large for one text note.", nameof(rtf));
            ValidateFontSize(fontSize);
            if (!float.IsFinite(width) || width < 0 || width > MaximumImageDimension)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (!float.IsFinite(height) || height < 0 || height > MaximumImageDimension)
                throw new ArgumentOutOfRangeException(nameof(height));
        }

        private static void ValidateFontSize(float fontSize)
        {
            if (!float.IsFinite(fontSize) || fontSize < 1 || fontSize > 1024)
                throw new ArgumentOutOfRangeException(nameof(fontSize), "Choose a font size from 1 to 1,024 image pixels.");
        }

        private static void ValidateRadius(float radius)
        {
            if (!float.IsFinite(radius) || radius <= 0 || radius > 1024)
                throw new ArgumentOutOfRangeException(nameof(radius), "Choose a radius greater than zero and at most 1,024 image pixels.");
        }

        private static global::SnipStudio.ShapeStyle ConvertShapeStyle(ShapeStyleEdit style)
        {
            ValidateWidth(style.OutlineWidth);
            return new global::SnipStudio.ShapeStyle(style.FillArgb.HasValue ? FromArgb(style.FillArgb.Value) : Color.Empty,
                style.OutlineArgb.HasValue ? FromArgb(style.OutlineArgb.Value) : Color.Empty,
                style.OutlineWidth, style.FillOpacity, style.OutlineOpacity);
        }

        private static PointF ToFinitePoint(ImagePoint value, string name)
        {
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || value.X < -float.MaxValue || value.X > float.MaxValue ||
                value.Y < -float.MaxValue || value.Y > float.MaxValue)
                throw new ArgumentOutOfRangeException(name);
            return new PointF((float)value.X, (float)value.Y);
        }

        private static RectangleF Between(PointF first, PointF last) => RectangleF.FromLTRB(
            Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
        private static Color FromArgb(uint argb) => Color.FromArgb(unchecked((int)argb));
        private static uint ToArgb(Color color) => unchecked((uint)color.ToArgb());
        private static ImagePoint ToImagePoint(PointF point) => new(point.X, point.Y);
        private static ImageRectangle ToImageRectangle(RectangleF bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        private static global::SnipStudio.EditorTool ToLegacyShape(AnnotationShape shape) => shape switch
        {
            AnnotationShape.Line => global::SnipStudio.EditorTool.Line,
            AnnotationShape.Arrow => global::SnipStudio.EditorTool.Arrow,
            AnnotationShape.Rectangle => global::SnipStudio.EditorTool.Rectangle,
            AnnotationShape.Ellipse => global::SnipStudio.EditorTool.Ellipse,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        private static AnnotationShape FromLegacyShape(global::SnipStudio.EditorTool shape) => shape switch
        {
            global::SnipStudio.EditorTool.Line => AnnotationShape.Line,
            global::SnipStudio.EditorTool.Arrow => AnnotationShape.Arrow,
            global::SnipStudio.EditorTool.Rectangle => AnnotationShape.Rectangle,
            global::SnipStudio.EditorTool.Ellipse => AnnotationShape.Ellipse,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        private static IReadOnlyList<EmojiStickerSnapshot> CreateEmojiCatalog()
        {
            var definitions = global::SnipStudio.EmojiArtwork.Catalog;
            EmojiStickerSnapshot[] snapshots = new EmojiStickerSnapshot[definitions.Count];
            for (int index = 0; index < snapshots.Length; index++)
            {
                var definition = definitions[index];
                snapshots[index] = new EmojiStickerSnapshot(definition.Key, definition.Glyph, definition.DisplayName);
            }
            return Array.AsReadOnly(snapshots);
        }
    }
}

namespace SnipStudio
{
    // These document transactions live with the adapter. The inherited released
    // renderer remains byte-for-byte intact and history owns the same immutable
    // objects/frames as the existing editing operations.
    public sealed partial class EditorDocument
    {
        internal bool UpdateAdapterPlainText(int index, string text, Color color, float fontSize, float width, float minimumHeight)
        {
            RequireImage();
            TextAnnotation original = GetTextAnnotationObject(index);
            if (original == null) return false;
            if (string.IsNullOrEmpty(text)) return RemoveAnnotation(index);
            string rtf;
            if (text == original.Text)
            {
                rtf = original.Rtf;
                if (fontSize != original.FontSize) rtf = RichTextLayout.SetFontSize(rtf, fontSize);
                if (color.ToArgb() != original.Color.ToArgb()) rtf = RichTextLayout.SetColor(rtf, color);
            }
            else rtf = RichTextLayout.CreatePlainRtf(text, original.FontFamily, fontSize, original.FontStyle, color);
            return UpdateRichTextAnnotation(index, text, rtf, color, fontSize, width, minimumHeight);
        }

        internal bool UpdateAdapterAnnotationProperties(int index, RectangleF frame, ShapeStyle? style, float? rotationDegrees)
        {
            RequireImage();
            Annotation? replacement;
            TextAnnotation text = GetTextAnnotationObject(index);
            if (text != null)
            {
                float width = NormalizeTextWidth(frame.Width), height = NormalizeTextWidth(frame.Height);
                if (text.Location == frame.Location && text.LayoutWidth == width && text.MinimumHeight == height) return false;
                replacement = text.WithGeometry(frame.Location, text.FontSize, width, height);
            }
            else
            {
                EmojiAnnotation emoji = GetEmojiAnnotationObject(index);
                if (emoji != null)
                {
                    float rotation = rotationDegrees.HasValue ? NormalizeEmojiRotation(rotationDegrees.Value) : emoji.RotationDegrees;
                    if (emoji.Frame == frame && emoji.RotationDegrees == rotation) return false;
                    replacement = CreateEmojiAnnotationPreview(index, frame, rotation);
                }
                else
                {
                    EditorTool? tool = GetShapeAnnotationTool(index);
                    if (!tool.HasValue) return false;
                    ShapeStyle originalStyle = GetShapeAnnotationStyle(index);
                    ShapeStyle nextStyle = style ?? originalStyle;
                    if (tool == EditorTool.Rectangle || tool == EditorTool.Ellipse)
                    {
                        Annotation geometry = CreateClosedShapeResizePreview(index, frame);
                        RectangleF next = geometry is RectangleAnnotation ? ((RectangleAnnotation)geometry).GeometryBounds : ((EllipseAnnotation)geometry).GeometryBounds;
                        if (next == GetClosedShapeBounds(index) && originalStyle.SameAs(nextStyle)) return false;
                        replacement = ((IStyledShape)geometry).WithStyle(nextStyle);
                    }
                    else
                    {
                        PointF[] old = GetShapeEndpoints(index);
                        if (old.Length != 2) return false;
                        float left = Math.Min(old[0].X, old[1].X), top = Math.Min(old[0].Y, old[1].Y);
                        float spanX = Math.Abs(old[0].X - old[1].X), spanY = Math.Abs(old[0].Y - old[1].Y);
                        PointF Scale(PointF point) => new PointF(frame.Left + (spanX == 0 ? .5f : (point.X - left) / spanX) * frame.Width,
                            frame.Top + (spanY == 0 ? .5f : (point.Y - top) / spanY) * frame.Height);
                        PointF first = Scale(old[0]), last = Scale(old[1]);
                        if (first == old[0] && last == old[1] && originalStyle.SameAs(nextStyle)) return false;
                        replacement = CreateAdapterEndpointAnnotation(tool.Value, first, last, nextStyle);
                    }
                }
            }
            return replacement != null && ReplaceAnnotation(index, replacement);
        }

        internal bool ReplaceShapeEndpoints(int index, PointF start, PointF end, ShapeStyle? style = null)
        {
            RequireImage();
            ValidatePoint(start); ValidatePoint(end);
            PointF[] old = GetShapeEndpoints(index);
            EditorTool? tool = GetShapeAnnotationTool(index);
            if (old.Length != 2 || !tool.HasValue) return false;
            ShapeStyle oldStyle = GetShapeAnnotationStyle(index), nextStyle = style ?? oldStyle;
            if (old[0] == start && old[1] == end && oldStyle.SameAs(nextStyle)) return false;
            Annotation? replacement = CreateAdapterEndpointAnnotation(tool.Value, start, end, nextStyle);
            return replacement != null && ReplaceAnnotation(index, replacement);
        }

        private static Annotation? CreateAdapterEndpointAnnotation(EditorTool tool, PointF start, PointF end, ShapeStyle style)
        {
            if (DistanceSquared(start, end) < 1) return null;
            // Editing an existing object's explicit None/transparent styling
            // keeps the same object/history semantics as UpdateShapeStyle.
            return tool == EditorTool.Arrow ? new ArrowAnnotation(start, end, style) :
                tool == EditorTool.Line ? new LineAnnotation(start, end, style) : null;
        }

        internal bool EraseAnnotationsAlongPath(PointF[] points, float radius)
        {
            RequireImage();
            if (points.Length == 0 || AnnotationCount == 0) return false;
            // A continuous pointer segment needs sampling even when native
            // pointer events are sparse. Validate the entire work budget before
            // changing any history, so an oversized gesture remains reversible.
            const int maximumSamples = 200000;
            double spacing = Math.Max(.5, Math.Min(4, radius * .5));
            int[] steps = new int[points.Length];
            long sampleCount = 1;
            for (int index = 1; index < points.Length; index++)
            {
                double dx = points[index].X - points[index - 1].X, dy = points[index].Y - points[index - 1].Y;
                double needed = Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / spacing);
                if (needed > maximumSamples || (sampleCount += Math.Max(1, (long)needed)) > maximumSamples)
                    throw new ArgumentException("This eraser gesture is too long. Finish it and use shorter strokes.", nameof(points));
                steps[index] = Math.Max(1, (int)needed);
            }
            List<Annotation> remaining = new List<Annotation>(Current.Annotations.Count);
            foreach (Annotation annotation in Current.Annotations)
            {
                bool touched = annotation.HitTest(points[0], radius);
                for (int index = 1; index < points.Length && !touched; index++)
                {
                    PointF first = points[index - 1], last = points[index];
                    RectangleF swept = RectangleF.FromLTRB(Math.Min(first.X, last.X) - radius, Math.Min(first.Y, last.Y) - radius,
                        Math.Max(first.X, last.X) + radius, Math.Max(first.Y, last.Y) + radius);
                    if (!swept.IntersectsWith(annotation.Bounds)) continue;
                    for (int step = 1; step <= steps[index] && !touched; step++)
                    {
                        float amount = step / (float)steps[index];
                        touched = annotation.HitTest(new PointF(first.X + (last.X - first.X) * amount,
                            first.Y + (last.Y - first.Y) * amount), radius);
                    }
                }
                if (!touched) remaining.Add(annotation);
            }
            if (remaining.Count == AnnotationCount) return false;
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, remaining, ++nextRevision));
            return true;
        }
    }
}
