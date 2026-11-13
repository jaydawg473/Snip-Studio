using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private enum AnnotationHandleKind { None, TextLeft, TextRight, ArrowTail, ArrowTip,
            TextTop, TextBottom, TextTopLeft, TextTopRight, TextBottomLeft, TextBottomRight }

        private AnnotationHandleKind annotationHandle;
        private int handleAnnotationIndex = -1;
        private long handleDocumentRevision;
        private object handleImageIdentity;
        private PointF handleGrabOffset;
        private TextAnnotationInfo handleText;
        private PointF handleTextLocation;
        private float handleTextWidth, handleTextMinimumHeight;
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
            handleImageIdentity = ImageIdentity;
            handleText = document.GetTextAnnotation(selectedIndex);
            if (handleText != null)
            {
                handleTextLocation = handleText.Location;
                handleTextWidth = handleText.LayoutWidth;
                handleTextMinimumHeight = handleText.MinimumHeight;
                float edgeX = TextHandleLeft(hit) ? handleText.Bounds.Left : handleText.Bounds.Right;
                float edgeY = TextHandleTop(hit) ? handleText.Bounds.Top : handleText.Bounds.Bottom;
                // Selection handles sit outside the text box. Retain the grab
                // offset so a click/release does not change its layout width.
                handleGrabOffset = new PointF(pointer.X - edgeX, pointer.Y - edgeY);
                preview = document.CreateTextAnnotationPreview(selectedIndex, handleTextLocation, handleText.FontSize, handleTextWidth, handleTextMinimumHeight);
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
            Cursor = handleText != null ? TextHandleCursor(hit) : Cursors.Cross;
            Invalidate();
            return true;
        }

        private bool UpdateAnnotationHandleDrag(MouseEventArgs e)
        {
            if (!AnnotationHandleDragging) return false;
            if (!HasImage || !Object.ReferenceEquals(handleImageIdentity, ImageIdentity) || document.Revision != handleDocumentRevision || selectedIndex != handleAnnotationIndex)
            {
                CancelGesture();
                return true;
            }
            PointF pointer = ToImage(e.Location, false);
            if (handleText != null)
            {
                RectangleF original = handleText.Bounds;
                float left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
                float edgeX = pointer.X - handleGrabOffset.X, edgeY = pointer.Y - handleGrabOffset.Y;
                bool horizontal = (TextHandleLeft(annotationHandle) || TextHandleRight(annotationHandle)) &&
                    Math.Abs(edgeX - (TextHandleLeft(annotationHandle) ? original.Left : original.Right)) > .0001f;
                bool vertical = (TextHandleTop(annotationHandle) || TextHandleBottom(annotationHandle)) &&
                    Math.Abs(edgeY - (TextHandleTop(annotationHandle) ? original.Top : original.Bottom)) > .0001f;
                if (horizontal)
                {
                    float minimum = Math.Min(12, ImageSize.Width);
                    if (TextHandleLeft(annotationHandle))
                    {
                        right = Math.Max(minimum, Math.Min(ImageSize.Width, right));
                        left = Math.Max(0, Math.Min(right - minimum, edgeX));
                    }
                    else
                    {
                        left = Math.Max(0, Math.Min(ImageSize.Width - minimum, left));
                        right = Math.Max(left + minimum, Math.Min(ImageSize.Width, edgeX));
                    }
                }
                if (vertical)
                {
                    float minimum = Math.Min(12, ImageSize.Height);
                    if (TextHandleTop(annotationHandle))
                    {
                        bottom = Math.Max(minimum, Math.Min(ImageSize.Height, bottom));
                        top = Math.Max(0, Math.Min(bottom - minimum, edgeY));
                    }
                    else
                    {
                        top = Math.Max(0, Math.Min(ImageSize.Height - minimum, top));
                        bottom = Math.Max(top + minimum, Math.Min(ImageSize.Height, edgeY));
                    }
                }
                handleTextLocation = new PointF(left, top);
                // Only the requested frame dimension changes. In particular, a
                // no-op gesture retains legacy auto-height/width values of zero.
                handleTextWidth = horizontal ? Math.Min(32768, Math.Max(1, (float)Math.Round(right - left))) : handleText.LayoutWidth;
                handleTextMinimumHeight = vertical ? Math.Min(32768, Math.Max(1, (float)Math.Round(bottom - top))) : handleText.MinimumHeight;
                preview = document.CreateTextAnnotationPreview(handleAnnotationIndex, handleTextLocation, handleText.FontSize, handleTextWidth, handleTextMinimumHeight);
                Cursor = TextHandleCursor(annotationHandle);
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
            float minimumHeight = handleTextMinimumHeight;
            PointF endpoint = handleArrowPoint;
            bool current = HasImage && Object.ReferenceEquals(handleImageIdentity, ImageIdentity) && document.Revision == handleDocumentRevision && selectedIndex == index;
            CancelAnnotationHandleDrag();
            Capture = false;
            UpdateCursor();
            bool changed = current && (text != null
                ? document.UpdateTextAnnotationBounds(index, location, text.FontSize, width, minimumHeight)
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
            handleImageIdentity = null;
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
                float middleX = frame.Left + frame.Width / 2, middleY = frame.Top + frame.Height / 2;
                if (NearHandle(location, new PointF(frame.Left, frame.Top), radius)) return AnnotationHandleKind.TextTopLeft;
                if (NearHandle(location, new PointF(frame.Right, frame.Top), radius)) return AnnotationHandleKind.TextTopRight;
                if (NearHandle(location, new PointF(frame.Right, frame.Bottom), radius)) return AnnotationHandleKind.TextBottomRight;
                if (NearHandle(location, new PointF(frame.Left, frame.Bottom), radius)) return AnnotationHandleKind.TextBottomLeft;
                if (NearHandle(location, new PointF(frame.Left, middleY), radius)) return AnnotationHandleKind.TextLeft;
                if (NearHandle(location, new PointF(frame.Right, middleY), radius)) return AnnotationHandleKind.TextRight;
                if (NearHandle(location, new PointF(middleX, frame.Top), radius)) return AnnotationHandleKind.TextTop;
                if (NearHandle(location, new PointF(middleX, frame.Bottom), radius)) return AnnotationHandleKind.TextBottom;
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
            Cursor = TextHandleLeft(hit) || TextHandleRight(hit) || TextHandleTop(hit) || TextHandleBottom(hit) ? TextHandleCursor(hit) : Cursors.Cross;
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
                float middleX = frame.Left + frame.Width / 2, middleY = frame.Top + frame.Height / 2;
                DrawAnnotationHandle(graphics, new PointF(frame.Left, frame.Top), false);
                DrawAnnotationHandle(graphics, new PointF(frame.Right, frame.Top), false);
                DrawAnnotationHandle(graphics, new PointF(frame.Right, frame.Bottom), false);
                DrawAnnotationHandle(graphics, new PointF(frame.Left, frame.Bottom), false);
                DrawAnnotationHandle(graphics, new PointF(frame.Left, middleY), false);
                DrawAnnotationHandle(graphics, new PointF(frame.Right, middleY), false);
                DrawAnnotationHandle(graphics, new PointF(middleX, frame.Top), false);
                DrawAnnotationHandle(graphics, new PointF(middleX, frame.Bottom), false);
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

        private static bool TextHandleLeft(AnnotationHandleKind kind)
        { return kind == AnnotationHandleKind.TextLeft || kind == AnnotationHandleKind.TextTopLeft || kind == AnnotationHandleKind.TextBottomLeft; }
        private static bool TextHandleRight(AnnotationHandleKind kind)
        { return kind == AnnotationHandleKind.TextRight || kind == AnnotationHandleKind.TextTopRight || kind == AnnotationHandleKind.TextBottomRight; }
        private static bool TextHandleTop(AnnotationHandleKind kind)
        { return kind == AnnotationHandleKind.TextTop || kind == AnnotationHandleKind.TextTopLeft || kind == AnnotationHandleKind.TextTopRight; }
        private static bool TextHandleBottom(AnnotationHandleKind kind)
        { return kind == AnnotationHandleKind.TextBottom || kind == AnnotationHandleKind.TextBottomLeft || kind == AnnotationHandleKind.TextBottomRight; }
        private static Cursor TextHandleCursor(AnnotationHandleKind kind)
        {
            if (kind == AnnotationHandleKind.TextTopLeft || kind == AnnotationHandleKind.TextBottomRight) return Cursors.SizeNWSE;
            if (kind == AnnotationHandleKind.TextTopRight || kind == AnnotationHandleKind.TextBottomLeft) return Cursors.SizeNESW;
            return TextHandleLeft(kind) || TextHandleRight(kind) ? Cursors.SizeWE : Cursors.SizeNS;
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
