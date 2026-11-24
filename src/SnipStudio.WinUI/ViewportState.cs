namespace SnipStudio.WinUI;

/// <summary>All view coordinates are logical DIPs; image coordinates are source pixels.</summary>
internal readonly record struct CanvasPoint(double X, double Y);

internal sealed class ViewportState
{
    internal const double MinimumZoom = 1;
    internal const double MaximumZoom = 800;
    internal const double Margin = 24;

    internal int ImageWidth { get; private set; }
    internal int ImageHeight { get; private set; }
    internal double Width { get; private set; }
    internal double Height { get; private set; }
    internal double RasterizationScale { get; private set; } = 1;
    internal double ZoomPercent { get; private set; } = 100;
    internal bool IsFit { get; private set; } = true;
    internal CanvasPoint Origin { get; private set; }
    private CanvasPoint rasterRootOffset;
    internal bool HasImage => ImageWidth > 0 && ImageHeight > 0;
    internal bool UsesNativePixels => Math.Abs(ZoomPercent - 100) < 1e-9;
    internal double Scale => (UsesNativePixels ? 1 : ZoomPercent / 100) / RasterizationScale;
    internal double DisplayWidth => ImageWidth * Scale;
    internal double DisplayHeight => ImageHeight * Scale;
    internal double FrameThickness => UsesNativePixels ? 1 / RasterizationScale : 1;
    internal CanvasPoint DisplayOrigin => UsesNativePixels ? new(
        AlignToRaster(Origin.X, rasterRootOffset.X), AlignToRaster(Origin.Y, rasterRootOffset.Y)) : Origin;
    internal CanvasPoint Center => new(Width / 2, Height / 2);

    // Snap in root coordinates because the viewport itself may sit on a
    // fractional DIP. Keep the mathematical origin for reversible zoom anchors.
    private double AlignToRaster(double position, double offset) =>
        Math.Round((position + offset) * RasterizationScale) / RasterizationScale - offset;

    internal bool SetRasterRootOffset(CanvasPoint offset)
    {
        RequireFinite(offset.X, offset.Y);
        if (rasterRootOffset == offset) return false;
        CanvasPoint previous = DisplayOrigin;
        rasterRootOffset = offset;
        return previous != DisplayOrigin;
    }

    internal void SetImage(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        ImageWidth = width;
        ImageHeight = height;
        Fit();
    }

    internal void SetViewport(double width, double height, double rasterizationScale)
    {
        RequireFinite(width, height, rasterizationScale);
        if (width < 0 || height < 0 || rasterizationScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (Width == width && Height == height && RasterizationScale == rasterizationScale) return;
        CanvasPoint anchor = ImageAtOrigin(Center, Origin);
        Width = width;
        Height = height;
        RasterizationScale = rasterizationScale;
        if (IsFit) Fit();
        else Origin = new(Center.X - anchor.X * Scale, Center.Y - anchor.Y * Scale);
    }

    internal void Fit()
    {
        IsFit = true;
        if (!HasImage) { ZoomPercent = 100; Origin = default; return; }
        double scale = Math.Min(1 / RasterizationScale,
            Math.Min(Math.Max(1, Width - Margin * 2) / ImageWidth,
                     Math.Max(1, Height - Margin * 2) / ImageHeight));
        // Fit may need less than manual 1% for a large image in a compact window.
        ZoomPercent = Math.Max(double.Epsilon, scale * RasterizationScale * 100);
        Origin = new((Width - DisplayWidth) / 2, (Height - DisplayHeight) / 2);
    }

    internal void ZoomTo(double percent, CanvasPoint anchor)
    {
        RequireFinite(percent, anchor.X, anchor.Y);
        CanvasPoint imageAnchor = ImageAtOrigin(anchor, Origin);
        ZoomPercent = Math.Clamp(percent, MinimumZoom, MaximumZoom);
        IsFit = false;
        Origin = new(anchor.X - imageAnchor.X * Scale, anchor.Y - imageAnchor.Y * Scale);
    }

    internal void Pan(double deltaX, double deltaY)
    {
        RequireFinite(deltaX, deltaY);
        // Fit already establishes the canonical centered origin and has no
        // pannable overflow. Do not re-clamp that origin: converting its scale
        // through physical-percent units can put an edge a floating-point ULP
        // beyond the padding boundary, which must not become manual-view intent.
        // Manual zoom still enables panning through the bounded path below.
        if (!HasImage || IsFit) return;
        double x = Origin.X + deltaX, y = Origin.Y + deltaY;
        x = DisplayWidth <= Width - Margin * 2 ? (Width - DisplayWidth) / 2 :
            Math.Clamp(x, Math.Min(Margin, Width - DisplayWidth - Margin), Margin);
        y = DisplayHeight <= Height - Margin * 2 ? (Height - DisplayHeight) / 2 :
            Math.Clamp(y, Math.Min(Margin, Height - DisplayHeight - Margin), Margin);
        if (x != Origin.X || y != Origin.Y) IsFit = false;
        Origin = new(x, y);
    }

    internal CanvasPoint ImageAt(CanvasPoint viewport)
    {
        RequireFinite(viewport.X, viewport.Y);
        return ImageAtOrigin(viewport, DisplayOrigin);
    }

    private CanvasPoint ImageAtOrigin(CanvasPoint viewport, CanvasPoint origin) =>
        new((viewport.X - origin.X) / Scale, (viewport.Y - origin.Y) / Scale);

    internal CanvasPoint ViewportAt(CanvasPoint image)
    {
        RequireFinite(image.X, image.Y);
        CanvasPoint origin = DisplayOrigin;
        return new(origin.X + image.X * Scale, origin.Y + image.Y * Scale);
    }

    internal bool Contains(CanvasPoint viewport)
    {
        CanvasPoint origin = DisplayOrigin;
        return HasImage && viewport.X >= origin.X && viewport.X <= origin.X + DisplayWidth &&
            viewport.Y >= origin.Y && viewport.Y <= origin.Y + DisplayHeight;
    }

    internal CanvasPoint ClampedImageAt(CanvasPoint viewport)
    {
        CanvasPoint point = ImageAt(viewport);
        return new(Math.Clamp(point.X, 0, ImageWidth), Math.Clamp(point.Y, 0, ImageHeight));
    }

    internal CanvasPoint VisibleImageCenter()
    {
        CanvasPoint origin = DisplayOrigin;
        double left = Math.Max(0, origin.X), right = Math.Min(Width, origin.X + DisplayWidth);
        double top = Math.Max(0, origin.Y), bottom = Math.Min(Height, origin.Y + DisplayHeight);
        return HasImage && right > left && bottom > top
            ? ClampedImageAt(new CanvasPoint((left + right) / 2, (top + bottom) / 2))
            : new(ImageWidth / 2.0, ImageHeight / 2.0);
    }

    internal (double X, double Y, double Width, double Height)? TextActionsGlowBounds(double spread)
    {
        if (!double.IsFinite(spread) || spread <= 0)
            throw new ArgumentOutOfRangeException(nameof(spread));
        if (!HasImage || Width <= 2 * spread || Height <= 2 * spread) return null;

        CanvasPoint origin = DisplayOrigin;
        double imageRight = origin.X + DisplayWidth;
        double imageBottom = origin.Y + DisplayHeight;
        if (Math.Min(Width, imageRight) <= Math.Max(0, origin.X) ||
            Math.Min(Height, imageBottom) <= Math.Max(0, origin.Y)) return null;

        // A fitted image keeps the halo outside its frame. When zoom or pan
        // cuts off an edge, anchor that side of the halo to the visible canvas.
        double padding = FrameThickness + spread;
        double left = Math.Max(0, origin.X - padding);
        double top = Math.Max(0, origin.Y - padding);
        double width = Math.Min(Width, imageRight + padding) - left;
        double height = Math.Min(Height, imageBottom + padding) - top;
        return width > 2 * spread && height > 2 * spread ? (left, top, width, height) : null;
    }

    private static void RequireFinite(params double[] values)
    {
        if (values.Any(value => !double.IsFinite(value)))
            throw new ArgumentOutOfRangeException(nameof(values), "View coordinates must be finite.");
    }
}
