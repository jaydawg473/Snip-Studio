using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

internal enum ObjectGrip
{
    None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Tail, Head, Rotate, Move
}

internal static class ObjectTransformGeometry
{
    internal const double DragThreshold = 3;

    internal static bool StartedDrag(CanvasPoint anchor, CanvasPoint pointer)
    {
        double x = pointer.X - anchor.X, y = pointer.Y - anchor.Y;
        return x * x + y * y >= DragThreshold * DragThreshold;
    }

    internal static ImagePoint Center(ImageRectangle frame) => new(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);

    internal static ImagePoint Rotate(ImagePoint point, ImagePoint center, double degrees)
    {
        if (degrees == 0) return point;
        double radians = degrees * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        double x = point.X - center.X, y = point.Y - center.Y;
        return new(center.X + x * cosine - y * sine, center.Y + x * sine + y * cosine);
    }

    internal static float NormalizeAngle(double degrees)
    {
        double angle = (degrees + 180) % 360;
        float normalized = (float)(angle < 0 ? angle + 180 : angle - 180);
        return normalized >= 180 ? -180 : normalized;
    }

    internal static float RotateFromPointer(float original, CanvasPoint start, CanvasPoint current,
        CanvasPoint center, bool snap)
    {
        double angle = Math.Atan2(current.Y - center.Y, current.X - center.X);
        double initial = Math.Atan2(start.Y - center.Y, start.X - center.X);
        double degrees = original + (angle - initial) * 180 / Math.PI;
        if (snap) degrees = Math.Round(degrees / 15, MidpointRounding.AwayFromZero) * 15;
        return NormalizeAngle(degrees);
    }

    internal static ImageRectangle Translate(ImageRectangle frame, ImagePoint delta) =>
        new(frame.X + delta.X, frame.Y + delta.Y, frame.Width, frame.Height);

    internal static ImageRectangle Resize(ImageRectangle frame, ObjectGrip grip, ImagePoint delta, double degrees,
        double minWidth = 1, double minHeight = 1)
    {
        bool left = grip is ObjectGrip.TopLeft or ObjectGrip.BottomLeft or ObjectGrip.Left;
        bool right = grip is ObjectGrip.TopRight or ObjectGrip.BottomRight or ObjectGrip.Right;
        bool top = grip is ObjectGrip.TopLeft or ObjectGrip.Top or ObjectGrip.TopRight;
        bool bottom = grip is ObjectGrip.BottomLeft or ObjectGrip.Bottom or ObjectGrip.BottomRight;
        ImagePoint local = Rotate(delta, default, -degrees);
        double x = frame.Width / 2, y = frame.Height / 2;
        double leftEdge = left ? Math.Min(-x + local.X, x - minWidth) : -x;
        double rightEdge = right ? Math.Max(x + local.X, -x + minWidth) : x;
        double topEdge = top ? Math.Min(-y + local.Y, y - minHeight) : -y;
        double bottomEdge = bottom ? Math.Max(y + local.Y, -y + minHeight) : y;
        ImagePoint center = Center(frame);
        ImagePoint change = Rotate(new((leftEdge + rightEdge) / 2, (topEdge + bottomEdge) / 2), default, degrees);
        double width = rightEdge - leftEdge, height = bottomEdge - topEdge;
        return new(center.X + change.X - width / 2, center.Y + change.Y - height / 2, width, height);
    }

    internal static bool NearPerimeter(ImageRectangle frame, ImagePoint point, double degrees, double radius)
    {
        ImagePoint local = Rotate(point, Center(frame), -degrees);
        if (local.X < frame.Left - radius || local.X > frame.Right + radius ||
            local.Y < frame.Top - radius || local.Y > frame.Bottom + radius) return false;
        return Math.Min(Math.Abs(local.X - frame.Left), Math.Abs(local.X - frame.Right)) <= radius ||
            Math.Min(Math.Abs(local.Y - frame.Top), Math.Abs(local.Y - frame.Bottom)) <= radius;
    }

    internal static (CanvasPoint Base, CanvasPoint Left, CanvasPoint Right)? ArrowHead(
        CanvasPoint tail, CanvasPoint tip, double stroke)
    {
        double x = tip.X - tail.X, y = tip.Y - tail.Y, length = Math.Sqrt(x * x + y * y);
        if (length <= 0 || stroke <= 0) return null;
        double unitX = x / length, unitY = y / length;
        // Match AdjustableArrowCap(4, 5, true) in the committed renderer.
        double capStroke = Math.Min(stroke, length / 5);
        var center = new CanvasPoint(tip.X - unitX * (5 * capStroke), tip.Y - unitY * (5 * capStroke));
        double halfWidth = 2 * capStroke;
        return (center, new(center.X + unitY * halfWidth, center.Y - unitX * halfWidth),
            new(center.X - unitY * halfWidth, center.Y + unitX * halfWidth));
    }
}
