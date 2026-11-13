using System;
using System.Drawing;

namespace SnipStudio
{
    public sealed partial class EditorDocument
    {
        // The selection frame follows the shape geometry, not the extra area
        // occupied by a wide outline. This also avoids float round trips through
        // inflated annotation bounds on a click without a resize.
        public RectangleF GetClosedShapeBounds(int index)
        {
            ThrowIfDisposed();
            Annotation annotation = HasImage && index >= 0 && index < AnnotationCount ? Current.Annotations[index] : null;
            RectangleAnnotation rectangle = annotation as RectangleAnnotation;
            if (rectangle != null) return rectangle.GeometryBounds;
            EllipseAnnotation ellipse = annotation as EllipseAnnotation;
            return ellipse == null ? RectangleF.Empty : ellipse.GeometryBounds;
        }

        internal Annotation CreateClosedShapeResizePreview(int index, RectangleF bounds)
        {
            RequireImage();
            ValidatePoint(bounds.Location);
            ValidatePoint(new PointF(bounds.Right, bounds.Bottom));
            Annotation annotation = index >= 0 && index < AnnotationCount ? Current.Annotations[index] : null;
            RectangleAnnotation rectangle = annotation as RectangleAnnotation;
            EllipseAnnotation ellipse = annotation as EllipseAnnotation;
            if (rectangle == null && ellipse == null) return null;
            RectangleF original = rectangle != null ? rectangle.GeometryBounds : ellipse.GeometryBounds;
            // A legacy or cropped shape may extend outside the current image,
            // or be smaller than the resize minimum. Merely grabbing a handle
            // must display that same geometry without changing its pixels.
            if (bounds != original) bounds = ConstrainClosedShapeBounds(bounds);
            if (rectangle != null) return new RectangleAnnotation(bounds, rectangle.Style);
            return new EllipseAnnotation(bounds, ellipse.Style);
        }

        public bool UpdateClosedShapeBounds(int index, RectangleF bounds)
        {
            Annotation replacement = CreateClosedShapeResizePreview(index, bounds);
            if (replacement == null) return false;
            RectangleAnnotation rectangle = replacement as RectangleAnnotation;
            RectangleF next = rectangle != null ? rectangle.GeometryBounds : ((EllipseAnnotation)replacement).GeometryBounds;
            return next != GetClosedShapeBounds(index) && ReplaceAnnotation(index, replacement);
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
    }
}
