using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    public enum EmojiResizeCorner { TopLeft, TopRight, BottomRight, BottomLeft }

    // All values are source pixels. Resize fixes the opposite rotated corner.
    public static class EmojiStickerGeometry
    {
        public static PointF[] Corners(RectangleF frame, float rotation)
        {
            Validate(frame, rotation);
            PointF center = Center(frame);
            return new PointF[] {
                Transform(center, -frame.Width / 2, -frame.Height / 2, rotation),
                Transform(center, frame.Width / 2, -frame.Height / 2, rotation),
                Transform(center, frame.Width / 2, frame.Height / 2, rotation),
                Transform(center, -frame.Width / 2, frame.Height / 2, rotation) };
        }

        public static PointF RotationGrip(RectangleF frame, float rotation, float gap)
        {
            Validate(frame, rotation);
            if (!Finite(gap) || gap < 0) throw new ArgumentOutOfRangeException("gap");
            return Transform(Center(frame), 0, -frame.Height / 2 - gap, rotation);
        }

        public static RectangleF ResizeFromCorner(RectangleF frame, float rotation, EmojiResizeCorner corner, PointF target, float minimumSide)
        {
            Validate(frame, rotation); ValidatePoint(target);
            int index = (int)corner;
            if (index < 0 || index > 3) throw new ArgumentOutOfRangeException("corner");
            if (!Finite(minimumSide) || minimumSide < 1 || minimumSide > 32768) throw new ArgumentOutOfRangeException("minimumSide");
            PointF[] corners = Corners(frame, rotation);
            PointF original = corners[index];
            if (Math.Abs(target.X - original.X) < .0001f && Math.Abs(target.Y - original.Y) < .0001f) return frame;
            PointF anchor = corners[(index + 2) % 4];
            double radians = NormalizeRotation(rotation) * Math.PI / 180;
            double cosine = Math.Cos(radians), sine = Math.Sin(radians);
            double dx = (double)target.X - anchor.X, dy = (double)target.Y - anchor.Y;
            double localX = dx * cosine + dy * sine, localY = -dx * sine + dy * cosine;
            int signX = index == 1 || index == 2 ? 1 : -1;
            int signY = index == 2 || index == 3 ? 1 : -1;
            double width = frame.Width, height = frame.Height;
            double scale = (localX * signX * width + localY * signY * height) / (width * width + height * height);
            // Preserve a legacy larger frame, but never grow beyond its long
            // side or the normal 32768px cap. Extreme aspect ratios can make
            // the requested minimum unattainable without violating that cap.
            double longest = Math.Max(width, height);
            double maximum = Math.Max(32768, longest) / longest;
            double minimum = Math.Min(maximum, Math.Max(minimumSide / width, minimumSide / height));
            scale = Math.Max(minimum, Math.Min(maximum, scale));
            float nextWidth = (float)(width * scale), nextHeight = (float)(height * scale);
            PointF center = Transform(anchor, signX * nextWidth / 2, signY * nextHeight / 2, rotation);
            RectangleF resized = new RectangleF(center.X - nextWidth / 2, center.Y - nextHeight / 2, nextWidth, nextHeight);
            Validate(resized, rotation); return resized;
        }

        public static float RotateFromPointer(RectangleF frame, float originalRotation, PointF startPointer, PointF pointer)
        {
            Validate(frame, originalRotation); ValidatePoint(startPointer); ValidatePoint(pointer);
            PointF center = Center(frame);
            double sx = (double)startPointer.X - center.X, sy = (double)startPointer.Y - center.Y;
            double x = (double)pointer.X - center.X, y = (double)pointer.Y - center.Y;
            if (sx * sx + sy * sy < .00000001 || x * x + y * y < .00000001) return NormalizeRotation(originalRotation);
            double change = (Math.Atan2(y, x) - Math.Atan2(sy, sx)) * 180 / Math.PI;
            return NormalizeRotation((float)(NormalizeRotation(originalRotation) + change));
        }

        public static float NormalizeRotation(float rotation)
        {
            if (!Finite(rotation)) throw new ArgumentOutOfRangeException("rotation");
            float normalized = rotation % 360;
            if (normalized >= 180) normalized -= 360;
            if (normalized < -180) normalized += 360;
            return normalized == 0 ? 0 : normalized;
        }

        private static PointF Center(RectangleF frame)
        { return new PointF(frame.X + frame.Width / 2, frame.Y + frame.Height / 2); }

        private static PointF Transform(PointF center, float x, float y, float rotation)
        {
            double radians = NormalizeRotation(rotation) * Math.PI / 180;
            double cosine = Math.Cos(radians), sine = Math.Sin(radians);
            PointF point = new PointF((float)(center.X + x * cosine - y * sine), (float)(center.Y + x * sine + y * cosine));
            ValidatePoint(point); return point;
        }

        private static void Validate(RectangleF frame, float rotation)
        {
            ValidatePoint(frame.Location); ValidatePoint(new PointF(frame.Right, frame.Bottom));
            if (!Finite(frame.Width) || !Finite(frame.Height) || frame.Width < 1 || frame.Height < 1)
                throw new ArgumentOutOfRangeException("frame");
            NormalizeRotation(rotation);
        }
        private static void ValidatePoint(PointF point)
        { if (!Finite(point.X) || !Finite(point.Y)) throw new ArgumentOutOfRangeException("point"); }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }

    public sealed partial class EditorCanvas
    {
        private enum EmojiHandleKind { None, TopLeft, TopRight, BottomRight, BottomLeft, Rotate }
        private EmojiHandleKind emojiHandle;
        private int emojiHandleIndex = -1;
        private object emojiHandleImage;
        private long emojiHandleRevision;
        private RectangleF emojiOriginalFrame, emojiPreviewFrame;
        private float emojiOriginalRotation, emojiPreviewRotation;
        private PointF emojiGrabPointer, emojiGrabCorner;
        private EditorDocument.Annotation emojiHandlePreview;
        private bool emojiMoveActive;
        private int emojiMoveIndex = -1;
        private object emojiMoveImage;
        private long emojiMoveRevision;

        internal bool EmojiHandleDragging { get { return emojiHandle != EmojiHandleKind.None; } }
        internal int EmojiPreviewIndex
        {
            get
            {
                if (!EmojiHandleDragging) return -1;
                if (EmojiHandleSnapshotCurrent) return emojiHandleIndex;
                if (Object.ReferenceEquals(preview, emojiHandlePreview)) preview = null;
                return -1;
            }
        }
        private bool CanUseEmojiHandles
        { get { return HasImage && !textActionsMode && !InlineTextEditing && (tool == EditorTool.Emoji || tool == EditorTool.Select) && HasSelectedEmoji; } }
        private bool EmojiHandleSnapshotCurrent
        { get { return HasImage && Object.ReferenceEquals(emojiHandleImage, ImageIdentity) && document.Revision == emojiHandleRevision && selectedIndex == emojiHandleIndex && document.GetEmojiAnnotation(emojiHandleIndex) != null; } }
        private bool EmojiMoveSnapshotCurrent
        { get { return HasImage && Object.ReferenceEquals(emojiMoveImage, ImageIdentity) && document.Revision == emojiMoveRevision && selectedIndex == emojiMoveIndex && document.GetEmojiAnnotation(emojiMoveIndex) != null; } }
        private float EmojiScreenPixel { get { return Math.Max(96, DeviceDpi) / 96f / zoom; } }
        private float EmojiHandleRadius { get { return 8 * EmojiScreenPixel; } }

        private void BeginEmojiBodyMove(PointF point)
        {
            emojiMoveActive = true; emojiMoveIndex = selectedIndex;
            emojiMoveImage = ImageIdentity; emojiMoveRevision = document.Revision;
            moving = true; moveStart = point; moveDelta = PointF.Empty;
            Capture = true; Cursor = Cursors.SizeAll;
        }

        internal bool TryBeginEmojiHandleDrag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !CanUseEmojiHandles) return false;
            EmojiAnnotationInfo info = SelectedEmojiInfo;
            EmojiHandleKind hit = HitEmojiHandle(ToImage(e.Location, false), info);
            if (hit == EmojiHandleKind.None) return false;
            CancelGesture();
            emojiHandle = hit; emojiHandleIndex = selectedIndex;
            emojiHandleImage = ImageIdentity; emojiHandleRevision = document.Revision;
            emojiOriginalFrame = emojiPreviewFrame = info.Frame;
            emojiOriginalRotation = emojiPreviewRotation = info.RotationDegrees;
            emojiGrabPointer = ToImage(e.Location, false);
            if (hit != EmojiHandleKind.Rotate) emojiGrabCorner = EmojiStickerGeometry.Corners(info.Frame, info.RotationDegrees)[(int)hit - 1];
            emojiHandlePreview = document.CreateEmojiAnnotationPreview(emojiHandleIndex, info.Frame, info.RotationDegrees);
            preview = emojiHandlePreview;
            if (preview == null) { CancelEmojiHandleDrag(); return false; }
            Capture = true; SetEmojiHandleCursor(hit, info.Frame, info.RotationDegrees); Invalidate(); return true;
        }

        internal bool UpdateEmojiHandleDrag(MouseEventArgs e)
        {
            if (emojiMoveActive)
            {
                if (!EmojiMoveSnapshotCurrent) { CancelGesture(); Invalidate(); return true; }
                return false;
            }
            if (!EmojiHandleDragging) return false;
            if (!EmojiHandleSnapshotCurrent) { CancelGesture(); Invalidate(); return true; }
            PointF pointer = ToImage(e.Location, false);
            RectangleF frame = emojiOriginalFrame;
            float rotation = emojiOriginalRotation;
            try
            {
                if (emojiHandle == EmojiHandleKind.Rotate)
                {
                    PointF center = new PointF(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
                    if (EditorDocument.DistanceSquared(pointer, center) < .0001f) return true;
                    rotation = EmojiStickerGeometry.RotateFromPointer(frame, rotation, emojiGrabPointer, pointer);
                }
                else
                {
                    PointF target = new PointF(emojiGrabCorner.X + pointer.X - emojiGrabPointer.X, emojiGrabCorner.Y + pointer.Y - emojiGrabPointer.Y);
                    // A sticker fitted to a tiny snip may already be narrower
                    // than the normal grip minimum. Do not enlarge it on grab.
                    float minimum = Math.Min(12, Math.Min(emojiOriginalFrame.Width, emojiOriginalFrame.Height));
                    frame = EmojiStickerGeometry.ResizeFromCorner(frame, rotation, (EmojiResizeCorner)((int)emojiHandle - 1), target, Math.Max(1, minimum));
                }
                EditorDocument.Annotation candidate = document.CreateEmojiAnnotationPreview(emojiHandleIndex, frame, rotation);
                if (candidate != null)
                {
                    emojiPreviewFrame = frame; emojiPreviewRotation = rotation;
                    emojiHandlePreview = candidate; preview = candidate;
                }
            }
            catch (ArgumentException) { /* Keep the last finite, valid preview. */ }
            SetEmojiHandleCursor(emojiHandle, emojiPreviewFrame, emojiPreviewRotation); Invalidate(); return true;
        }

        internal bool CompleteEmojiHandleDrag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;
            if (emojiMoveActive)
            {
                if (!EmojiMoveSnapshotCurrent) { CancelGesture(); Invalidate(); return true; }
                // The existing generic mouse-up code commits movement once.
                emojiMoveActive = false; emojiMoveIndex = -1; emojiMoveImage = null;
                return false;
            }
            if (!EmojiHandleDragging) return false;
            UpdateEmojiHandleDrag(e);
            if (!EmojiHandleDragging) return true;
            int index = emojiHandleIndex;
            RectangleF frame = emojiPreviewFrame;
            float rotation = emojiPreviewRotation;
            bool current = EmojiHandleSnapshotCurrent;
            CancelEmojiHandleDrag(); UpdateCursor();
            if (current && document.UpdateEmojiAnnotationBounds(index, frame, rotation)) RaiseChanged();
            else Invalidate();
            return true;
        }

        internal void CancelEmojiHandleDrag()
        {
            bool ownedCapture = EmojiHandleDragging || emojiMoveActive;
            if (Object.ReferenceEquals(preview, emojiHandlePreview)) preview = null;
            if (emojiMoveActive) { moving = false; moveDelta = PointF.Empty; }
            emojiHandle = EmojiHandleKind.None; emojiHandleIndex = -1; emojiHandleImage = null; emojiHandlePreview = null;
            emojiMoveActive = false; emojiMoveIndex = -1; emojiMoveImage = null;
            if (ownedCapture && Capture) Capture = false;
        }

        private EmojiHandleKind HitEmojiHandle(PointF pointer, EmojiAnnotationInfo info)
        {
            float radius = EmojiHandleRadius;
            PointF grip = EmojiStickerGeometry.RotationGrip(info.Frame, info.RotationDegrees, 18 * EmojiScreenPixel);
            if (EditorDocument.DistanceSquared(pointer, grip) <= radius * radius) return EmojiHandleKind.Rotate;
            PointF[] corners = EmojiStickerGeometry.Corners(info.Frame, info.RotationDegrees);
            for (int i = 0; i < corners.Length; i++)
                if (EditorDocument.DistanceSquared(pointer, corners[i]) <= radius * radius) return (EmojiHandleKind)(i + 1);
            return EmojiHandleKind.None;
        }

        internal bool UpdateEmojiPointer(Point point)
        {
            if (EmojiHandleDragging) { SetEmojiHandleCursor(emojiHandle, emojiPreviewFrame, emojiPreviewRotation); return true; }
            if (!CanUseEmojiHandles) return false;
            EmojiAnnotationInfo info = SelectedEmojiInfo;
            SetEmojiHandleCursor(HitEmojiHandle(ToImage(point, false), info), info.Frame, info.RotationDegrees); return true;
        }

        private void SetEmojiHandleCursor(EmojiHandleKind kind, RectangleF frame, float rotation)
        {
            if (kind == EmojiHandleKind.Rotate) { Cursor = Cursors.Hand; return; }
            if (kind == EmojiHandleKind.None) { Cursor = moving ? Cursors.SizeAll : Cursors.Default; return; }
            PointF[] corners = EmojiStickerGeometry.Corners(frame, rotation);
            int index = (int)kind - 1;
            PointF first = corners[index], second = corners[(index + 2) % 4];
            double direction = Math.Atan2(first.Y - second.Y, first.X - second.X) * 180 / Math.PI;
            int octant = ((int)Math.Round(direction / 45) % 4 + 4) % 4;
            Cursor = octant == 0 ? Cursors.SizeWE : octant == 1 ? Cursors.SizeNWSE : octant == 2 ? Cursors.SizeNS : Cursors.SizeNESW;
        }

        internal bool DrawEmojiSelection(Graphics graphics)
        {
            EmojiAnnotationInfo info = SelectedEmojiInfo;
            if (info == null) return false;
            RectangleF frame = EmojiHandleDragging && EmojiHandleSnapshotCurrent ? emojiPreviewFrame : info.Frame;
            float rotation = EmojiHandleDragging && EmojiHandleSnapshotCurrent ? emojiPreviewRotation : info.RotationDegrees;
            if (moving) frame.Offset(moveDelta);
            PointF[] corners = EmojiStickerGeometry.Corners(frame, rotation);
            PointF top = new PointF((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
            PointF grip = EmojiStickerGeometry.RotationGrip(frame, rotation, 18 * EmojiScreenPixel);
            GraphicsState state = graphics.Save();
            try
            {
                graphics.ResetClip();
                using (Pen outline = new Pen(Color.FromArgb(0, 103, 192), 1.3f * EmojiScreenPixel))
                using (Brush fill = new SolidBrush(Color.White))
                {
                    outline.DashStyle = DashStyle.Dash; graphics.DrawPolygon(outline, corners);
                    outline.DashStyle = DashStyle.Solid; graphics.DrawLine(outline, top, grip);
                    float side = 8 * EmojiScreenPixel;
                    foreach (PointF corner in corners)
                    {
                        RectangleF handle = new RectangleF(corner.X - side / 2, corner.Y - side / 2, side, side);
                        graphics.FillRectangle(fill, handle); graphics.DrawRectangle(outline, handle.X, handle.Y, handle.Width, handle.Height);
                    }
                    float diameter = 10 * EmojiScreenPixel;
                    RectangleF rotate = new RectangleF(grip.X - diameter / 2, grip.Y - diameter / 2, diameter, diameter);
                    graphics.FillEllipse(fill, rotate); graphics.DrawEllipse(outline, rotate);
                }
            }
            finally { graphics.Restore(state); }
            return true;
        }
    }
}
