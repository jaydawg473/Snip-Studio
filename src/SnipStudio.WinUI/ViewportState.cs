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
    internal bool HasImage => ImageWidth > 0 && ImageHeight > 0;
    internal double Scale => ZoomPercent / 100 / RasterizationScale;
    internal double DisplayWidth => ImageWidth * Scale;
    internal double DisplayHeight => ImageHeight * Scale;
    internal CanvasPoint Center => new(Width / 2, Height / 2);

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
        CanvasPoint anchor = ImageAt(Center);
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
        CanvasPoint imageAnchor = ImageAt(anchor);
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
        return new((viewport.X - Origin.X) / Scale, (viewport.Y - Origin.Y) / Scale);
    }

    internal CanvasPoint ViewportAt(CanvasPoint image)
    {
        RequireFinite(image.X, image.Y);
        return new(Origin.X + image.X * Scale, Origin.Y + image.Y * Scale);
    }

    internal bool Contains(CanvasPoint viewport) => HasImage &&
        viewport.X >= Origin.X && viewport.X <= Origin.X + DisplayWidth &&
        viewport.Y >= Origin.Y && viewport.Y <= Origin.Y + DisplayHeight;

    internal CanvasPoint ClampedImageAt(CanvasPoint viewport)
    {
        CanvasPoint point = ImageAt(viewport);
        return new(Math.Clamp(point.X, 0, ImageWidth), Math.Clamp(point.Y, 0, ImageHeight));
    }

    private static void RequireFinite(params double[] values)
    {
        if (values.Any(value => !double.IsFinite(value)))
            throw new ArgumentOutOfRangeException(nameof(values), "View coordinates must be finite.");
    }
}
