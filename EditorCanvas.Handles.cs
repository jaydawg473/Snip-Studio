using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private enum AnnotationHandleKind { None, TextLeft, TextRight, ArrowTail, ArrowTip }

        private AnnotationHandleKind annotationHandle;
        private int handleAnnotationIndex = -1;
        private long handleDocumentRevision;
        private PointF handleGrabOffset;
        private TextAnnotationInfo handleText;
        private PointF handleTextLocation;
        private float handleTextWidth;
        private PointF handleArrowPoint;

        private bool AnnotationHandleDragging { get { return annotationHandle != AnnotationHandleKind.None; } }
        private int AnnotationPreviewIndex { get { return AnnotationHandleDragging ? handleAnnotationIndex : -1; } }

        private bool CanUseAnnotationHandles
        {
            get { return HasImage && !textActionsMode && selectedIndex >= 0 &&
                (tool == EditorTool.Select || tool == EditorTool.Text); }
        }

        private bool TryBeginAnnotationHandleDrag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !CanUseAnnotationHandles) return false;
            AnnotationHandleKind hit = HitAnnotationHandle(e.Location);
            if (hit == AnnotationHandleKind.None) return false;

            PointF pointer = ToImage(e.Location, false);
            annotationHandle = hit;
            handleAnnotationIndex = selectedIndex;
            handleDocumentRevision = document.Revision;
            handleText = document.GetTextAnnotation(selectedIndex);
            if (handleText != null)
            {
                handleTextLocation = handleText.Location;
                handleTextWidth = handleText.LayoutWidth > 0 ? handleText.LayoutWidth : handleText.Bounds.Width;
                float edge = hit == AnnotationHandleKind.TextLeft ? handleText.Bounds.Left : handleText.Bounds.Right;
                // Selection handles sit outside the text box. Retain the grab
                // offset so a click/release does not change its layout width.
                handleGrabOffset = new PointF(pointer.X - edge, 0);
                preview = document.CreateTextAnnotationPreview(selectedIndex, handleTextLocation, handleText.FontSize, handleTextWidth);
            }
            else
            {
                PointF[] ends = document.GetShapeEndpoints(selectedIndex);
                if (ends.Length != 2) { CancelAnnotationHandleDrag(); return false; }
                handleArrowPoint = hit == AnnotationHandleKind.ArrowTip ? ends[1] : ends[0];
                handleGrabOffset = new PointF(pointer.X - handleArrowPoint.X, pointer.Y - handleArrowPoint.Y);
                preview = document.CreateShapeEndpointPreview(selectedIndex, hit == AnnotationHandleKind.ArrowTip, handleArrowPoint);
                if (preview == null) { CancelAnnotationHandleDrag(); return false; }
            }
            Capture = true;
            Cursor = handleText != null ? Cursors.SizeWE : Cursors.Cross;
            Invalidate();
            return true;
        }

        private bool UpdateAnnotationHandleDrag(MouseEventArgs e)
        {
            if (!AnnotationHandleDragging) return false;
            if (!HasImage || document.Revision != handleDocumentRevision || selectedIndex != handleAnnotationIndex)
            {
                CancelGesture();
                return true;
            }
            PointF pointer = ToImage(e.Location, false);
            if (handleText != null)
            {
                float imageWidth = ImageSize.Width;
                float minimum = Math.Min(imageWidth, Math.Max(24, handleText.FontSize * 1.5f));
                float edge = pointer.X - handleGrabOffset.X;
                float left, right;
                if (annotationHandle == AnnotationHandleKind.TextLeft)
                {
                    right = Math.Max(minimum, Math.Min(imageWidth, handleText.Bounds.Right));
                    left = Math.Max(0, Math.Min(right - minimum, edge));
                }
                else
                {
                    left = Math.Max(0, Math.Min(imageWidth - minimum, handleText.Bounds.Left));
                    right = Math.Max(left + minimum, Math.Min(imageWidth, edge));
                }
                handleTextLocation = new PointF(left, handleText.Location.Y);
                handleTextWidth = Math.Max(1, right - left);
                preview = document.CreateTextAnnotationPreview(handleAnnotationIndex, handleTextLocation, handleText.FontSize, handleTextWidth);
                Cursor = Cursors.SizeWE;
            }
            else
            {
                PointF candidate = new PointF(
                    Math.Max(0, Math.Min(ImageSize.Width - 1, pointer.X - handleGrabOffset.X)),
                    Math.Max(0, Math.Min(ImageSize.Height - 1, pointer.Y - handleGrabOffset.Y)));
                EditorDocument.Annotation arrow = document.CreateShapeEndpointPreview(handleAnnotationIndex,
                    annotationHandle == AnnotationHandleKind.ArrowTip, candidate);
                // Crossing the fixed endpoint must not create a zero-length
                // arrow or commit invalid geometry. Keep the last valid preview.
                if (arrow != null) { handleArrowPoint = candidate; preview = arrow; }
                Cursor = Cursors.Cross;
            }
            Invalidate();
            return true;
        }

        private bool CompleteAnnotationHandleDrag(MouseEventArgs e)
        {
            if (!AnnotationHandleDragging || e.Button != MouseButtons.Left) return false;
            UpdateAnnotationHandleDrag(e);
            if (!AnnotationHandleDragging) return true;

            AnnotationHandleKind kind = annotationHandle;
            int index = handleAnnotationIndex;
            TextAnnotationInfo text = handleText;
            PointF location = handleTextLocation;
            float width = handleTextWidth;
            PointF endpoint = handleArrowPoint;
            bool current = HasImage && document.Revision == handleDocumentRevision && selectedIndex == index;
            CancelAnnotationHandleDrag();
            Capture = false;
            UpdateCursor();
            bool changed = current && (text != null
                ? document.UpdateTextAnnotationBounds(index, location, text.FontSize, width)
                : document.UpdateShapeEndpoint(index, kind == AnnotationHandleKind.ArrowTip, endpoint));
            if (changed) RaiseChanged(); else Invalidate();
            return true;
        }

        private void CancelAnnotationHandleDrag()
        {
            if (AnnotationHandleDragging) preview = null;
            annotationHandle = AnnotationHandleKind.None;
            handleAnnotationIndex = -1;
            handleText = null;
            handleGrabOffset = PointF.Empty;
        }

        private AnnotationHandleKind HitAnnotationHandle(Point point)
        {
            if (!CanUseAnnotationHandles) return AnnotationHandleKind.None;
            PointF location = ToImage(point, false);
            float radius = 8 / zoom;
            TextAnnotationInfo text = document.GetTextAnnotation(selectedIndex);
            if (text != null)
            {
                RectangleF frame = TextSelectionFrame(text.Bounds);
                float middle = frame.Top + frame.Height / 2;
                if (NearHandle(location, new PointF(frame.Left, middle), radius)) return AnnotationHandleKind.TextLeft;
                if (NearHandle(location, new PointF(frame.Right, middle), radius)) return AnnotationHandleKind.TextRight;
            }
            else
            {
                PointF[] ends = document.GetShapeEndpoints(selectedIndex);
                if (ends.Length == 2)
                {
                    if (NearHandle(location, ends[1], radius)) return AnnotationHandleKind.ArrowTip;
                    if (NearHandle(location, ends[0], radius)) return AnnotationHandleKind.ArrowTail;
                }
            }
            return AnnotationHandleKind.None;
        }

        private bool TrySetAnnotationHandleCursor(Point point)
        {
            AnnotationHandleKind hit = HitAnnotationHandle(point);
            if (hit == AnnotationHandleKind.None) return false;
            Cursor = hit == AnnotationHandleKind.TextLeft || hit == AnnotationHandleKind.TextRight ? Cursors.SizeWE : Cursors.Cross;
            return true;
        }

        private bool DrawSpecialAnnotationSelection(Graphics graphics)
        {
            TextAnnotationInfo text = document.GetTextAnnotation(selectedIndex);
            if (text != null)
            {
                RectangleF bounds = handleText != null && AnnotationHandleDragging && preview != null ? preview.Bounds : text.Bounds;
                if (moving) bounds.Offset(moveDelta);
                RectangleF frame = TextSelectionFrame(bounds);
                using (Pen outline = new Pen(Color.FromArgb(77, 163, 255), 1.2f / zoom))
                {
                    outline.DashStyle = DashStyle.Dash;
                    graphics.DrawRectangle(outline, frame.X, frame.Y, frame.Width, frame.Height);
                }
                float middle = frame.Top + frame.Height / 2;
                DrawAnnotationHandle(graphics, new PointF(frame.Left, middle), false);
                DrawAnnotationHandle(graphics, new PointF(frame.Right, middle), false);
                return true;
            }

            PointF[] ends = document.GetShapeEndpoints(selectedIndex);
            if (ends.Length != 2) return false;
            if (AnnotationHandleDragging && handleText == null)
                ends[annotationHandle == AnnotationHandleKind.ArrowTip ? 1 : 0] = handleArrowPoint;
            if (moving) { ends[0] = AddHandleOffset(ends[0], moveDelta); ends[1] = AddHandleOffset(ends[1], moveDelta); }
            DrawAnnotationHandle(graphics, ends[0], true);
            DrawAnnotationHandle(graphics, ends[1], true);
            return true;
        }

        private RectangleF TextSelectionFrame(RectangleF bounds)
        {
            bounds.Inflate(4 / zoom, 4 / zoom);
            return bounds;
        }

        private void DrawAnnotationHandle(Graphics graphics, PointF center, bool round)
        {
            float side = 8 / zoom;
            RectangleF bounds = new RectangleF(center.X - side / 2, center.Y - side / 2, side, side);
            using (Brush fill = new SolidBrush(Color.White))
            using (Pen outline = new Pen(Color.FromArgb(0, 103, 192), 1.5f / zoom))
            {
                if (round) { graphics.FillEllipse(fill, bounds); graphics.DrawEllipse(outline, bounds); }
                else { graphics.FillRectangle(fill, bounds); graphics.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height); }
            }
        }

        private static bool NearHandle(PointF point, PointF center, float radius)
        {
            float dx = point.X - center.X, dy = point.Y - center.Y;
            return dx * dx + dy * dy <= radius * radius;
        }

        private static PointF AddHandleOffset(PointF point, PointF offset)
        {
            return new PointF(point.X + offset.X, point.Y + offset.Y);
        }
    }
}
