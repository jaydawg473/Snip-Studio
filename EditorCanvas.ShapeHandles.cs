using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        [Flags]
        private enum ClosedShapeResizeHandle { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

        private static readonly ClosedShapeResizeHandle[] closedShapeHandles = new ClosedShapeResizeHandle[] {
            ClosedShapeResizeHandle.Left | ClosedShapeResizeHandle.Top,
            ClosedShapeResizeHandle.Top,
            ClosedShapeResizeHandle.Right | ClosedShapeResizeHandle.Top,
            ClosedShapeResizeHandle.Right,
            ClosedShapeResizeHandle.Right | ClosedShapeResizeHandle.Bottom,
            ClosedShapeResizeHandle.Bottom,
            ClosedShapeResizeHandle.Left | ClosedShapeResizeHandle.Bottom,
            ClosedShapeResizeHandle.Left
        };

        private ClosedShapeResizeHandle closedShapeHandle, closedShapePointerHandle;
        private int closedShapeIndex = -1;
        private long closedShapeRevision;
        private object closedShapeImage;
        private PointF closedShapeGrab;
        private RectangleF closedShapeOriginal, closedShapeDraft;
        private bool closedShapeGestureMoved;

        private bool ClosedShapeHandleDragging { get { return closedShapeHandle != ClosedShapeResizeHandle.None; } }
        private int ClosedShapePreviewIndex { get { return ClosedShapeHandleDragging ? closedShapeIndex : -1; } }
        private float ClosedShapeGripPixel { get { return Math.Max(96, DeviceDpi) / 96f / zoom; } }

        private bool CanUseClosedShapeHandles
        {
            get
            {
                return HasImage && !textActionsMode && !InlineTextEditing && selectedIndex >= 0 &&
                    (tool == EditorTool.Select || tool == EditorTool.Rectangle || tool == EditorTool.Ellipse) &&
                    !document.GetClosedShapeBounds(selectedIndex).IsEmpty;
            }
        }

        private bool TryBeginClosedShapeResize(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !CanUseClosedShapeHandles || drawing || moving || panning || erasing ||
                AnnotationHandleDragging || CropDragging || removalDragging || removalMoving) return false;
            ClosedShapeResizeHandle hit = HitClosedShapeHandle(e.Location);
            if (hit == ClosedShapeResizeHandle.None) return false;

            closedShapeIndex = selectedIndex;
            closedShapeRevision = document.Revision;
            closedShapeImage = document.BaseIdentity;
            closedShapeOriginal = closedShapeDraft = document.GetClosedShapeBounds(selectedIndex);
            closedShapeGrab = ToImage(e.Location, false);
            closedShapeHandle = closedShapePointerHandle = hit;
            closedShapeGestureMoved = false;
            preview = document.CreateClosedShapeResizePreview(closedShapeIndex, closedShapeDraft);
            if (preview == null) { CancelClosedShapeResize(); return false; }
            Capture = true;
            SetClosedShapeResizePointer(hit);
            Invalidate();
            return true;
        }

        private bool ClosedShapeResizeIsCurrent
        {
            get
            {
                return ClosedShapeHandleDragging && HasImage && selectedIndex == closedShapeIndex &&
                    document.Revision == closedShapeRevision && Object.ReferenceEquals(closedShapeImage, document.BaseIdentity) &&
                    CanUseClosedShapeHandles;
            }
        }

        private bool UpdateClosedShapeResize(MouseEventArgs e)
        {
            if (!ClosedShapeHandleDragging) return false;
            if (!ClosedShapeResizeIsCurrent) { CancelGesture(); return true; }
            PointF pointer = ToImage(e.Location, false);
            float dx = pointer.X - closedShapeGrab.X, dy = pointer.Y - closedShapeGrab.Y;
            if ((closedShapeHandle & (ClosedShapeResizeHandle.Left | ClosedShapeResizeHandle.Right)) == 0) dx = 0;
            if ((closedShapeHandle & (ClosedShapeResizeHandle.Top | ClosedShapeResizeHandle.Bottom)) == 0) dy = 0;
            if (dx == 0 && dy == 0)
            {
                closedShapeDraft = closedShapeOriginal;
                closedShapeGestureMoved = false;
                closedShapePointerHandle = closedShapeHandle;
            }
            else
            {
                float left = closedShapeOriginal.Left, right = closedShapeOriginal.Right;
                float top = closedShapeOriginal.Top, bottom = closedShapeOriginal.Bottom;
                ClosedShapeResizeHandle pointerHandle = ClosedShapeResizeHandle.None;
                bool low;
                if ((closedShapeHandle & (ClosedShapeResizeHandle.Left | ClosedShapeResizeHandle.Right)) != 0)
                {
                    bool first = (closedShapeHandle & ClosedShapeResizeHandle.Left) != 0;
                    ResizeClosedShapeAxis(first ? right : left, (first ? left : right) + dx, first, ImageSize.Width, out left, out right, out low);
                    pointerHandle |= low ? ClosedShapeResizeHandle.Left : ClosedShapeResizeHandle.Right;
                }
                if ((closedShapeHandle & (ClosedShapeResizeHandle.Top | ClosedShapeResizeHandle.Bottom)) != 0)
                {
                    bool first = (closedShapeHandle & ClosedShapeResizeHandle.Top) != 0;
                    ResizeClosedShapeAxis(first ? bottom : top, (first ? top : bottom) + dy, first, ImageSize.Height, out top, out bottom, out low);
                    pointerHandle |= low ? ClosedShapeResizeHandle.Top : ClosedShapeResizeHandle.Bottom;
                }
                closedShapeDraft = document.ConstrainClosedShapeBounds(RectangleF.FromLTRB(left, top, right, bottom));
                closedShapePointerHandle = pointerHandle;
                closedShapeGestureMoved = closedShapeDraft != closedShapeOriginal;
            }
            preview = document.CreateClosedShapeResizePreview(closedShapeIndex, closedShapeDraft);
            SetClosedShapeResizePointer(closedShapePointerHandle);
            Invalidate();
            return true;
        }

        // The opposite edge remains anchored until the pointer crosses it. The
        // moving edge then becomes the other side; the shape never inverts or
        // collapses while crossing its fixed corner.
        private static void ResizeClosedShapeAxis(float anchor, float moving, bool originallyLow, float limit,
            out float low, out float high, out bool movingIsLow)
        {
            float minimum = Math.Min(2, limit);
            anchor = Math.Max(0, Math.Min(limit, anchor));
            moving = Math.Max(0, Math.Min(limit, moving));
            movingIsLow = moving < anchor || (moving == anchor && originallyLow);
            if (movingIsLow)
            {
                high = anchor;
                low = Math.Max(0, Math.Min(anchor - minimum, moving));
                if (high - low < minimum) { low = 0; high = minimum; }
            }
            else
            {
                low = anchor;
                high = Math.Min(limit, Math.Max(anchor + minimum, moving));
                if (high - low < minimum) { high = limit; low = limit - minimum; }
            }
        }

        private bool CompleteClosedShapeResize(MouseEventArgs e)
        {
            if (!ClosedShapeHandleDragging || e.Button != MouseButtons.Left) return false;
            UpdateClosedShapeResize(e);
            if (!ClosedShapeHandleDragging) return true;
            int index = closedShapeIndex;
            RectangleF bounds = closedShapeDraft;
            bool current = ClosedShapeResizeIsCurrent && closedShapeGestureMoved;
            CancelClosedShapeResize();
            Capture = false;
            UpdateCursor();
            if (current && document.UpdateClosedShapeBounds(index, bounds)) RaiseChanged();
            else Invalidate();
            return true;
        }

        private void CancelClosedShapeResize()
        {
            if (ClosedShapeHandleDragging) preview = null;
            closedShapeHandle = closedShapePointerHandle = ClosedShapeResizeHandle.None;
            closedShapeIndex = -1;
            closedShapeImage = null;
            closedShapeGestureMoved = false;
        }

        private ClosedShapeResizeHandle HitClosedShapeHandle(Point clientCoordinates)
        {
            if (!CanUseClosedShapeHandles) return ClosedShapeResizeHandle.None;
            RectangleF bounds = document.GetClosedShapeBounds(selectedIndex);
            PointF point = ToImage(clientCoordinates, false);
            float radius = 8 * ClosedShapeGripPixel;
            float nearest = radius * radius;
            ClosedShapeResizeHandle result = ClosedShapeResizeHandle.None;
            foreach (ClosedShapeResizeHandle handle in closedShapeHandles)
            {
                PointF center = ClosedShapeHandleCenter(bounds, handle);
                float dx = point.X - center.X, dy = point.Y - center.Y, distance = dx * dx + dy * dy;
                // Strict comparison gives a corner priority when a tiny shape
                // puts a corner and an edge handle at the same distance.
                if (distance <= radius * radius && (result == ClosedShapeResizeHandle.None || distance < nearest))
                { nearest = distance; result = handle; }
            }
            return result;
        }

        private bool UpdateClosedShapePointer(Point clientCoordinates)
        {
            ClosedShapeResizeHandle hit = ClosedShapeHandleDragging ? closedShapePointerHandle : HitClosedShapeHandle(clientCoordinates);
            if (hit == ClosedShapeResizeHandle.None) return false;
            SetClosedShapeResizePointer(hit);
            return true;
        }

        private void SetClosedShapeResizePointer(ClosedShapeResizeHandle handle)
        {
            if (handle == ClosedShapeResizeHandle.Left || handle == ClosedShapeResizeHandle.Right) Cursor = Cursors.SizeWE;
            else if (handle == ClosedShapeResizeHandle.Top || handle == ClosedShapeResizeHandle.Bottom) Cursor = Cursors.SizeNS;
            else if (handle == (ClosedShapeResizeHandle.Left | ClosedShapeResizeHandle.Top) ||
                handle == (ClosedShapeResizeHandle.Right | ClosedShapeResizeHandle.Bottom)) Cursor = Cursors.SizeNWSE;
            else if (handle != ClosedShapeResizeHandle.None) Cursor = Cursors.SizeNESW;
            else Cursor = Cursors.Default;
        }

        private bool DrawClosedShapeSelection(Graphics sourceCoordinates)
        {
            if (!CanUseClosedShapeHandles) return false;
            RectangleF bounds = ClosedShapeHandleDragging && ClosedShapeResizeIsCurrent ? closedShapeDraft : document.GetClosedShapeBounds(selectedIndex);
            if (moving) bounds.Offset(moveDelta);
            float pixel = ClosedShapeGripPixel;
            using (Pen outline = new Pen(Color.FromArgb(77, 163, 255), 1.2f * pixel))
            {
                outline.DashStyle = DashStyle.Dash;
                sourceCoordinates.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            using (Brush fill = new SolidBrush(Color.White))
            using (Pen edge = new Pen(Color.FromArgb(0, 103, 192), 1.5f * pixel))
                foreach (ClosedShapeResizeHandle handle in closedShapeHandles)
                {
                    PointF center = ClosedShapeHandleCenter(bounds, handle);
                    RectangleF grip = new RectangleF(center.X - 4 * pixel, center.Y - 4 * pixel, 8 * pixel, 8 * pixel);
                    sourceCoordinates.FillRectangle(fill, grip);
                    sourceCoordinates.DrawRectangle(edge, grip.X, grip.Y, grip.Width, grip.Height);
                }
            return true;
        }

        private static PointF ClosedShapeHandleCenter(RectangleF bounds, ClosedShapeResizeHandle handle)
        {
            float x = (handle & ClosedShapeResizeHandle.Left) != 0 ? bounds.Left :
                (handle & ClosedShapeResizeHandle.Right) != 0 ? bounds.Right : bounds.Left + bounds.Width / 2;
            float y = (handle & ClosedShapeResizeHandle.Top) != 0 ? bounds.Top :
                (handle & ClosedShapeResizeHandle.Bottom) != 0 ? bounds.Bottom : bounds.Top + bounds.Height / 2;
            return new PointF(x, y);
        }
    }
}
