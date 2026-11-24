using SnipStudio.WinUI;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckObjectGestureGeometry()
    {
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .0001;
        static bool NearImagePoint(ImagePoint actual, ImagePoint expected) => Near(actual.X, expected.X) && Near(actual.Y, expected.Y);
        static bool NearCanvasPoint(CanvasPoint actual, CanvasPoint expected) => Near(actual.X, expected.X) && Near(actual.Y, expected.Y);

        Check(!ObjectTransformGeometry.StartedDrag(new(100, 200), new(101, 202)),
            "A small pointer wobble does not move a selection or nudge a handle");
        Check(ObjectTransformGeometry.StartedDrag(new(100, 200), new(100, 203)),
            "Move begins at the same three-DIP threshold regardless of the image zoom");
        Check(ObjectTransformGeometry.RotateFromPointer(0, new(0, -20), new(20, 0), default, snap: false) == 90,
            "A clockwise rotation gives a positive image angle");
        Check(ObjectTransformGeometry.NormalizeAngle(180) == -180 && ObjectTransformGeometry.NormalizeAngle(450) == 90,
            "Rotation remains continuous across repeated turns");
        Check(ObjectTransformGeometry.RotateFromPointer(7, new(0, -20), new(20, 0), default, snap: true) == 90,
            "Shift snaps the absolute angle to a 15-degree increment");

        var frame = new ImageRectangle(50, 20, 40, 24);
        ImagePoint localBottomRight = new(frame.Right, frame.Bottom);
        ImagePoint originalAnchor = ObjectTransformGeometry.Rotate(localBottomRight, ObjectTransformGeometry.Center(frame), 32);
        ImageRectangle resized = ObjectTransformGeometry.Resize(frame, ObjectGrip.TopLeft, new(-10, 8), 32);
        ImagePoint resizedAnchor = ObjectTransformGeometry.Rotate(new(resized.Right, resized.Bottom), ObjectTransformGeometry.Center(resized), 32);
        Check(NearImagePoint(originalAnchor, resizedAnchor), "Grabbing a rotated corner keeps its opposite corner fixed");
        ImageRectangle unchanged = ObjectTransformGeometry.Resize(frame, ObjectGrip.TopLeft, default, 32);
        Check(unchanged == frame, "The grab position does not jump to the center of the handle");
        ImagePoint topEdge = ObjectTransformGeometry.Rotate(new(70, 20), ObjectTransformGeometry.Center(frame), 32);
        Check(ObjectTransformGeometry.NearPerimeter(frame, topEdge, 32, 2) &&
            !ObjectTransformGeometry.NearPerimeter(frame, ObjectTransformGeometry.Center(frame), 32, 2),
            "A rotated selection perimeter uses the visible frame, with no selection through the middle padding");

        var head = ObjectTransformGeometry.ArrowHead(new(10, 20), new(110, 20), 4)!.Value;
        Check(head.Base == new CanvasPoint(90, 20) && head.Left == new CanvasPoint(90, 12) && head.Right == new CanvasPoint(90, 28),
            "Arrow preview uses a filled 4:5 pen-width cap and keeps its tip at the pointer");
        var shortHead = ObjectTransformGeometry.ArrowHead(new(0, 0), new(0, 3), 10)!.Value;
        Check(NearCanvasPoint(shortHead.Base, default) && Near(shortHead.Left.Y, 0) && Near(shortHead.Right.Y, 0),
            "A short arrow scales its head without crossing the tail");
        Check(ObjectTransformGeometry.ArrowHead(new(20, 20), new(20, 20), 3) is null,
            "An unmoved arrow pointer has no arrowhead");

        var view = new ViewportState();
        view.SetViewport(450, 300, 1);
        view.SetImage(1000, 600);
        Check(NearCanvasPoint(view.VisibleImageCenter(), new(500, 300)), "Fit places a clicked sticker at the image center");
        view.ZoomTo(100, view.ViewportAt(new(173, 210)));
        double visibleLeft = Math.Max(0, view.DisplayOrigin.X);
        double visibleTop = Math.Max(0, view.DisplayOrigin.Y);
        double visibleRight = Math.Min(view.Width, view.DisplayOrigin.X + view.DisplayWidth);
        double visibleBottom = Math.Min(view.Height, view.DisplayOrigin.Y + view.DisplayHeight);
        CanvasPoint visibleCenter = view.ViewportAt(view.VisibleImageCenter());
        Check(NearCanvasPoint(visibleCenter, new((visibleLeft + visibleRight) / 2, (visibleTop + visibleBottom) / 2)),
            "A zoomed and offset view places a clicked sticker at the center of the visible image intersection");
    }
}
