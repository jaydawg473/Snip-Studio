using System;
using System.Drawing;

namespace SnipStudio
{
    [Flags]
    internal enum CropFrameHandle { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16, Create = 32 }

    // Image pixel edges: right/bottom can reach Width/Height. The preview never
    // resamples or changes the document; only Apply commits its rectangle.
    internal static class CropFrame
    {
        internal static CropFrameHandle HitTest(Rectangle frame, PointF point, float radius)
        {
            if (frame.Width <= 0 || frame.Height <= 0 || !Finite(radius) || radius <= 0 || !Finite(point.X) || !Finite(point.Y)) return CropFrameHandle.None;
            bool left = Math.Abs(point.X - frame.Left) <= radius, right = Math.Abs(point.X - frame.Right) <= radius;
            bool top = Math.Abs(point.Y - frame.Top) <= radius, bottom = Math.Abs(point.Y - frame.Bottom) <= radius;
            if (left && top) return CropFrameHandle.Left | CropFrameHandle.Top;
            if (right && top) return CropFrameHandle.Right | CropFrameHandle.Top;
            if (left && bottom) return CropFrameHandle.Left | CropFrameHandle.Bottom;
            if (right && bottom) return CropFrameHandle.Right | CropFrameHandle.Bottom;
            if (point.Y >= frame.Top - radius && point.Y <= frame.Bottom + radius)
            {
                if (left) return CropFrameHandle.Left;
                if (right) return CropFrameHandle.Right;
            }
            if (point.X >= frame.Left - radius && point.X <= frame.Right + radius)
            {
                if (top) return CropFrameHandle.Top;
                if (bottom) return CropFrameHandle.Bottom;
            }
            return point.X >= frame.Left && point.X <= frame.Right && point.Y >= frame.Top && point.Y <= frame.Bottom ? CropFrameHandle.Move : CropFrameHandle.None;
        }

        internal static Rectangle Resize(Rectangle frame, CropFrameHandle handle, PointF delta, Size image)
        {
            if (image.Width <= 0 || image.Height <= 0 || !Finite(delta.X) || !Finite(delta.Y)) return frame;
            int dx = Pixel(delta.X, -image.Width, image.Width), dy = Pixel(delta.Y, -image.Height, image.Height);
            int minWidth = Math.Min(2, image.Width), minHeight = Math.Min(2, image.Height);
            int left = Limit(frame.Left, 0, image.Width - minWidth), top = Limit(frame.Top, 0, image.Height - minHeight);
            int right = Limit(frame.Right, left + minWidth, image.Width), bottom = Limit(frame.Bottom, top + minHeight, image.Height);
            if (handle == CropFrameHandle.Move)
            {
                dx = Limit(dx, -left, image.Width - right); dy = Limit(dy, -top, image.Height - bottom);
                return Rectangle.FromLTRB(left + dx, top + dy, right + dx, bottom + dy);
            }
            if ((handle & CropFrameHandle.Left) != 0) left = Limit(left + dx, 0, right - minWidth);
            if ((handle & CropFrameHandle.Right) != 0) right = Limit(right + dx, left + minWidth, image.Width);
            if ((handle & CropFrameHandle.Top) != 0) top = Limit(top + dy, 0, bottom - minHeight);
            if ((handle & CropFrameHandle.Bottom) != 0) bottom = Limit(bottom + dy, top + minHeight, image.Height);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        internal static Rectangle Create(PointF first, PointF second, Size image)
        {
            if (image.Width <= 0 || image.Height <= 0 || !Finite(first.X) || !Finite(first.Y) || !Finite(second.X) || !Finite(second.Y)) return Rectangle.Empty;
            int x1 = Pixel(first.X, 0, image.Width), x2 = Pixel(second.X, 0, image.Width);
            int y1 = Pixel(first.Y, 0, image.Height), y2 = Pixel(second.Y, 0, image.Height);
            Rectangle frame = Rectangle.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            return frame.Width >= 2 && frame.Height >= 2 ? frame : Rectangle.Empty;
        }

        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static int Pixel(float value, int minimum, int maximum) { return (int)Math.Round(Math.Max(minimum, Math.Min(maximum, value)), MidpointRounding.AwayFromZero); }
        private static int Limit(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
    }
}
