using System.Drawing;
using System.Drawing.Drawing2D;
using SnipStudio;

internal static partial class Program
{
    private static void CheckRecordingAdjustmentRepaint()
    {
        using Bitmap desktop = new(64, 48);
        Rectangle viewport = new(Point.Empty, desktop.Size);
        for (int y = 0; y < desktop.Height; y++)
            for (int x = 0; x < desktop.Width; x++)
                desktop.SetPixel(x, y, Color.FromArgb(36 + x * 3, 40 + y * 4, (x * 37 + y * 13) % 256));

        using Bitmap incremental = new(desktop.Width, desktop.Height);
        Rectangle previous = new(8, 10, 29, 20);
        using (Graphics graphics = Graphics.FromImage(incremental))
            CaptureOverlay.PaintFrozenDesktop(graphics, desktop, viewport, previous, null);

        Rectangle[] adjustedAreas = [new(17, 13, 29, 20), new(17, 13, 12, 16), new(0, 0, 12, 16), previous];
        foreach (Rectangle adjusted in adjustedAreas)
        {
            using (Graphics graphics = Graphics.FromImage(incremental))
            using (Region dirty = CaptureOverlay.CreateSelectionRepaintRegion(previous, adjusted))
            {
                dirty.Intersect(viewport);
                Rectangle bounds = Rectangle.Ceiling(dirty.GetBounds(graphics));
                graphics.SetClip(dirty, CombineMode.Intersect);
                CaptureOverlay.PaintFrozenDesktop(graphics, desktop, bounds, adjusted, null);
            }

            using Bitmap expected = new(desktop.Width, desktop.Height);
            using (Graphics graphics = Graphics.FromImage(expected))
                CaptureOverlay.PaintFrozenDesktop(graphics, desktop, viewport, adjusted, null);

            for (int y = 0; y < desktop.Height; y++)
                for (int x = 0; x < desktop.Width; x++)
                    if (incremental.GetPixel(x, y) != expected.GetPixel(x, y))
                        Check(false, $"Recording adjustment to {adjusted} left stale desktop pixels at ({x}, {y}).");

            Check(true, $"Recording adjustment to {adjusted} matches a clean render.");
            previous = adjusted;
        }
    }
}
