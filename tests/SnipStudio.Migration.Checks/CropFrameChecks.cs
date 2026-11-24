using System.Drawing;
using SnipStudio.WinUI;
using CropGeometry = global::SnipStudio.CropFrame;
using CropFrameHandle = global::SnipStudio.CropFrameHandle;

internal static partial class Program
{
    private static void CheckCropFrameGeometry()
    {
        var image = new Size(400, 240);
        var full = new Rectangle(Point.Empty, image);
        Check(CropGeometry.HitTest(full, new PointF(200, 0), 8) == CropFrameHandle.Top,
            "Full-snip crop initially has a top-edge target");
        Check(CropGeometry.HitTest(full, new PointF(401, 241), 8) == (CropFrameHandle.Right | CropFrameHandle.Bottom),
            "Full-snip bottom-right grip is reachable slightly outside the image");
        Check(CropGeometry.HitTest(new Rectangle(0, 0, 6, 4), new PointF(5.9f, 3.8f), 8) ==
            (CropFrameHandle.Right | CropFrameHandle.Bottom), "Overlapping crop grip hit radii favor the nearest edges");

        Rectangle trimmed = CropGeometry.Resize(full, CropFrameHandle.Left, new PointF(40, 50), image);
        Check(trimmed == new Rectangle(40, 0, 360, 240), "A crop edge moves independently of the perpendicular pointer motion");
        Rectangle clipped = CropGeometry.Resize(trimmed, CropFrameHandle.Right | CropFrameHandle.Bottom, new PointF(-700, -700), image);
        Check(clipped == new Rectangle(40, 0, 2, 2), "Cropping cannot invert even when dragged through the opposite edges");
        Rectangle moved = CropGeometry.Resize(new Rectangle(39, 10, 350, 220), CropFrameHandle.Move, new PointF(30, -40), image);
        Check(moved == new Rectangle(50, 0, 350, 220), "Moving a trimmed crop preserves its size and stops at the image edges");
        Rectangle drawn = CropGeometry.Create(new PointF(150, 120), new PointF(-10, 500), image);
        Check(drawn == new Rectangle(0, 120, 150, 120) &&
            CropGeometry.Create(new PointF(10, 10), new PointF(11, 11), image).IsEmpty,
            "Drawn crop rectangles clip to the image and ignore sub-minimum input");

        var view = new ViewportState();
        view.SetViewport(730, 490, 1.5);
        view.SetImage(image.Width, image.Height);
        foreach (double zoom in new[] { 25d, 100d, 400d })
        {
            view.ZoomTo(zoom, view.Center);
            var edge = view.ViewportAt(new CanvasPoint(full.Right, full.Height / 2));
            CanvasPoint point = view.ImageAt(new CanvasPoint(edge.X + 4, edge.Y));
            Check(CropGeometry.HitTest(full, new PointF((float)point.X, (float)point.Y), (float)(8 / view.Scale)) == CropFrameHandle.Right,
                $"Crop handle hit radius stays usable at {zoom:0}% on a 150% display");
        }
    }
}
