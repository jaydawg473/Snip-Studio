using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnipStudio
{
    public sealed partial class EditorDocument
    {
        // The selection frame follows the shape geometry, not the extra area
        // occupied by a wide outline. This also avoids float round trips through
        // inflated annotation bounds on a click without a resize.
        public RectangleF GetClosedShapeBounds(int index)
        {
            ClosedShapeAnnotation shape = GetClosedShape(index);
            return shape == null ? RectangleF.Empty : shape.GeometryBounds;
        }

        private ClosedShapeAnnotation GetClosedShape(int index)
        {
            ThrowIfDisposed();
            return HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] as ClosedShapeAnnotation : null;
        }

        internal float GetShapeRotation(int index)
        {
            ClosedShapeAnnotation closed = GetClosedShape(index);
            if (closed != null) return closed.RotationDegrees;
            PointF[] ends = GetShapeEndpoints(index);
            return ends.Length == 2 ? GetEndpointRotation(ends[0], ends[1]) : 0;
        }

        internal int HitTestMovableAnnotation(PointF point, float radius)
        {
            int hit = HitTestAnnotation(point, radius);
            if (hit >= 0) return hit;
            // An outline-only object should be draggable by its interior. Give
            // real ink from overlapping annotations priority over empty space.
            for (int index = AnnotationCount - 1; index >= 0; index--)
            {
                ClosedShapeAnnotation shape = GetClosedShape(index);
                if (shape != null && shape.Style.VisibleOutline && shape.ContainsInterior(point)) return index;
            }
            return -1;
        }

        // Drawing tools reserve the surrounding canvas and shape interiors
        // for creating new marks. Only placed outline or stroke ink moves.
        internal int HitTestAnnotationOutline(PointF point, float radius)
        {
            for (int index = AnnotationCount - 1; index >= 0; index--)
            {
                Annotation item = Current.Annotations[index];
                if (item is ClosedShapeAnnotation closed)
                {
                    RectangleF bounds = closed.Bounds;
                    bounds.Inflate(radius, radius);
                    if (!bounds.Contains(point)) continue;
                    if (closed.HitTestOutline(point, radius)) return index;
                    if (closed.Style.EffectiveFillColor.A == 255 && closed.ContainsInterior(point)) return -1;
                }
                else if (item is ArrowAnnotation arrow)
                {
                    if (arrow.HitTestOutline(point, radius)) return index;
                }
                else if ((item is LineAnnotation ||
                    item is StrokeAnnotation stroke && stroke.Ink.A != 0 ||
                    item is HighlightBandsAnnotation highlight && highlight.Ink.A != 0) &&
                    item.HitTest(point, radius)) return index;
            }
            return -1;
        }

        internal ClosedShapeAnnotation CreateClosedShapeResizePreview(int index, RectangleF bounds,
            ShapeStyle style = null, float? rotationDegrees = null)
        {
            RequireImage();
            ValidatePoint(bounds.Location);
            ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
            ClosedShapeAnnotation shape = GetClosedShape(index);
            if (shape == null) return null;
            float angle = rotationDegrees.HasValue ? NormalizeEmojiRotation(rotationDegrees.Value) : shape.RotationDegrees;
            if (SameRotation(angle, shape.RotationDegrees)) angle = shape.RotationDegrees;
            RectangleF original = shape.GeometryBounds;
            // A legacy or cropped shape may extend outside the current image,
            // or be smaller than the resize minimum. Merely grabbing a handle
            // must display that same geometry without changing its pixels.
            if (bounds != original && angle == 0 && shape.RotationDegrees == 0)
                bounds = ConstrainClosedShapeBounds(bounds);
            ClosedShapeAnnotation result = shape.WithGeometry(bounds, style ?? shape.Style, angle);
            return bounds != original && !result.Bounds.IntersectsWith(new RectangleF(PointF.Empty, ImageSize)) ? null : result;
        }

        public bool UpdateClosedShapeBounds(int index, RectangleF bounds)
        {
            return UpdateClosedShape(index, bounds, null, null);
        }

        internal bool UpdateClosedShape(int index, RectangleF frame, ShapeStyle style, float? rotationDegrees)
        {
            ClosedShapeAnnotation old = GetClosedShape(index);
            if (old == null) return false;
            ClosedShapeAnnotation next = CreateClosedShapeResizePreview(index, frame, style, rotationDegrees);
            return next != null && (old.GeometryBounds != next.GeometryBounds ||
                old.RotationDegrees != next.RotationDegrees || !old.Style.SameAs(next.Style)) && ReplaceAnnotation(index, next);
        }

        private static float GetEndpointRotation(PointF start, PointF end)
        {
            return NormalizeEmojiRotation((float)(Math.Atan2((double)end.Y - start.Y, (double)end.X - start.X) * 180 / Math.PI));
        }

        private static bool SameRotation(float first, float second)
        {
            return Math.Abs(NormalizeEmojiRotation(first - second)) < .0001f;
        }

        private static void RotationComponents(float angle, out double cosine, out double sine)
        {
            // Cardinal angles should produce exact geometry, especially when a
            // user rotates four times and expects to return to the same points.
            if (angle == 0) { cosine = 1; sine = 0; return; }
            if (angle == -180) { cosine = -1; sine = 0; return; }
            if (angle == 90 || angle == -90) { cosine = 0; sine = angle > 0 ? 1 : -1; return; }
            double radians = angle * Math.PI / 180;
            cosine = Math.Cos(radians);
            sine = Math.Sin(radians);
        }

        internal RectangleF ConstrainClosedShapeBounds(RectangleF bounds)
        {
            RequireImage();
            ValidatePoint(bounds.Location);
            ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
            float left = Math.Max(0, Math.Min(ImageSize.Width, Math.Min(bounds.Left, bounds.Right)));
            float right = Math.Max(0, Math.Min(ImageSize.Width, Math.Max(bounds.Left, bounds.Right)));
            float top = Math.Max(0, Math.Min(ImageSize.Height, Math.Min(bounds.Top, bounds.Bottom)));
            float bottom = Math.Max(0, Math.Min(ImageSize.Height, Math.Max(bounds.Top, bounds.Bottom)));
            float minimumWidth = Math.Min(2, ImageSize.Width), minimumHeight = Math.Min(2, ImageSize.Height);
            if (right - left < minimumWidth)
            {
                left = Math.Min(left, ImageSize.Width - minimumWidth);
                right = left + minimumWidth;
            }
            if (bottom - top < minimumHeight)
            {
                top = Math.Min(top, ImageSize.Height - minimumHeight);
                bottom = top + minimumHeight;
            }
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        internal abstract class ClosedShapeAnnotation : Annotation, IStyledShape
        {
            private readonly RectangleF footprint;
            private readonly double cosine, sine, centerX, centerY;

            protected ClosedShapeAnnotation(RectangleF frame, ShapeStyle style, float rotationDegrees, bool ellipse)
            {
                GeometryBounds = frame;
                Style = style;
                RotationDegrees = NormalizeEmojiRotation(rotationDegrees);
                RotationComponents(RotationDegrees, out cosine, out sine);
                centerX = (double)frame.Left + frame.Width / 2.0;
                centerY = (double)frame.Top + frame.Height / 2.0;
                float outline = Style.VisibleOutline ? Style.OutlineWidth / 2 : 0;
                if (RotationDegrees == 0)
                {
                    footprint = frame;
                    footprint.Inflate(outline, outline);
                    return;
                }
                double halfWidth = frame.Width / 2.0, halfHeight = frame.Height / 2.0;
                double horizontal, vertical;
                if (ellipse)
                {
                    double widthAlongX = halfWidth * cosine, heightAlongX = halfHeight * sine;
                    double widthAlongY = halfWidth * sine, heightAlongY = halfHeight * cosine;
                    horizontal = Math.Sqrt(widthAlongX * widthAlongX + heightAlongX * heightAlongX) + outline;
                    vertical = Math.Sqrt(widthAlongY * widthAlongY + heightAlongY * heightAlongY) + outline;
                }
                else
                {
                    horizontal = (halfWidth + outline) * Math.Abs(cosine) + (halfHeight + outline) * Math.Abs(sine);
                    vertical = (halfWidth + outline) * Math.Abs(sine) + (halfHeight + outline) * Math.Abs(cosine);
                }
                footprint = RectangleF.FromLTRB((float)(centerX - horizontal), (float)(centerY - vertical),
                    (float)(centerX + horizontal), (float)(centerY + vertical));
            }

            internal RectangleF GeometryBounds { get; private set; }
            internal float RotationDegrees { get; private set; }
            public ShapeStyle Style { get; private set; }
            public abstract EditorTool ShapeTool { get; }
            public override RectangleF Bounds { get { return footprint; } }

            internal abstract ClosedShapeAnnotation WithGeometry(RectangleF frame, ShapeStyle style, float rotationDegrees);
            protected abstract void DrawGeometry(Graphics graphics, RectangleF frame);
            protected abstract bool ContainsGeometry(PointF point);
            protected abstract bool HitTestGeometry(PointF point, float radius, bool includeFill);

            public Annotation WithStyle(ShapeStyle style) { return WithGeometry(GeometryBounds, style, RotationDegrees); }
            public override Annotation Translate(PointF delta)
            {
                RectangleF frame = GeometryBounds;
                frame.Offset(delta);
                return WithGeometry(frame, Style, RotationDegrees);
            }

            public override void Draw(Graphics graphics)
            {
                if (RotationDegrees == 0) { DrawGeometry(graphics, GeometryBounds); return; }
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.TranslateTransform((float)centerX, (float)centerY);
                    graphics.RotateTransform(RotationDegrees);
                    DrawGeometry(graphics, new RectangleF(-GeometryBounds.Width / 2, -GeometryBounds.Height / 2,
                        GeometryBounds.Width, GeometryBounds.Height));
                }
                finally { graphics.Restore(state); }
            }

            internal bool ContainsInterior(PointF point) { return ContainsGeometry(Unrotate(point)); }
            public override bool HitTest(PointF point, float radius) { return HitTestGeometry(Unrotate(point), radius, true); }
            internal bool HitTestOutline(PointF point, float radius)
            { return Style.VisibleOutline && HitTestGeometry(Unrotate(point), radius, false); }

            private PointF Unrotate(PointF point)
            {
                if (RotationDegrees == 0) return point;
                double x = point.X - centerX, y = point.Y - centerY;
                return new PointF((float)(centerX + x * cosine + y * sine), (float)(centerY - x * sine + y * cosine));
            }
        }
    }
}
