using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SnipStudio.WinUI;

internal static class Program
{
    private static int assertions;
    private static readonly List<object> cases = new();

    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.Error.WriteLine("Usage: SnipStudio.Viewport.Checks --output <empty evidence directory>");
            return 2;
        }
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use an empty evidence directory; existing evidence is never replaced.");
            return 2;
        }
        Directory.CreateDirectory(output);
        Stopwatch clock = Stopwatch.StartNew();
        bool passed = false;
        string? failure = null;
        try
        {
            Run("visible viewport reclaims closed and shorter tool rows", VisibleViewportExamples);
            Run("known physical-pixel and DIP mappings", KnownMappings);
            Run("actual-size image, frame and input share the physical raster", NativeRasterAlignment);
            Run("Fit centers without upscaling across DPI and aspect ratios", FitMatrix);
            Run("large-image Fit can be below the manual zoom minimum", LargeImageFit);
            Run("zoom keeps the source pixel under its viewport anchor", AnchoredZoom);
            Run("manual resize and DPI change preserve center source anchor", ManualResizeAndDpi);
            Run("panning stays bounded and centers an axis that fits", PanLimits);
            Run("image hit testing and clipping follow displayed edges", EdgesAndClipping);
            Run("Text actions glow stays visible around a cropped snip", TextActionsGlowClipping);
            Run("empty and tiny viewports remain finite and recover", EmptyAndDegenerate);
            Run("invalid inputs reject without changing view intent", InvalidInputs);
            passed = true;
            return 0;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            clock.Stop();
            string json = JsonSerializer.Serialize(new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                Passed = passed,
                Assertions = assertions,
                Cases = cases,
                ElapsedMilliseconds = clock.ElapsedMilliseconds,
                Scope = "Linked production ViewportState geometry only; no WinUI controls, native pointer capture, rendered pixels, actual DPI/monitors, clipboard, file pickers, or installation.",
                Failure = failure
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "viewport-checks.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
        }
    }

    private static void VisibleViewportExamples()
    {
        ViewportState view = Create(759, 301, 855, 439, 1);
        Near(100, view.ZoomPercent, "Snip fits at native physical scale");
        Point(new(48, 69), view.Origin, "Closed-tool viewport centers the snip");
        Near(69, view.Height - (view.Origin.Y + view.DisplayHeight), "Closed-tool bottom gap equals the top");

        view.SetViewport(855, 399, 1);
        Point(new(48, 49), view.Origin, "Visible 40-DIP row takes its height from the viewport");
        Near(49, view.Height - (view.Origin.Y + view.DisplayHeight), "Short-tool bottom gap equals the top");
        Check(view.IsFit, "Changing tool-row space retains Fit intent");

        view.SetViewport(855, 439, 1);
        Point(new(48, 69), view.Origin, "Closing a visible row returns its full space");
        CheckCentered(view, "Returned viewport");
    }

    private static void TextActionsGlowClipping()
    {
        const double spread = 12;
        var empty = new ViewportState();
        empty.SetViewport(548, 348, 1);
        Check(empty.TextActionsGlowBounds(spread) is null, "No halo before opening a snip");

        ViewportState view = Create(1000, 500, 548, 348, 1);
        CheckBounds(view, 11, 36, 526, 276, "Fit keeps all halo bands outside the image frame");

        view.ZoomTo(100, view.Center);
        CheckBounds(view, 0, 0, 548, 348, "A large snip at 100% retains the halo around the visible viewport");

        view.Pan(10000, 10000);
        CheckBounds(view, 11, 11, 537, 337, "Panning reveals the real top and left edges");
        view.Pan(-10000, -10000);
        CheckBounds(view, 0, 0, 537, 337, "Panning back reveals the real bottom and right edges");
        view.Fit();
        CheckBounds(view, 11, 36, 526, 276, "Fit restores the real image outline after panning");

        ViewportState highDpi = Create(300, 200, 400, 300, 2);
        highDpi.SetRasterRootOffset(new(.3, .1));
        CheckBounds(highDpi, 112.7, 87.4, 175, 125, "Physical-pixel alignment and the half-DIP frame are honored");

        view.SetViewport(24, 348, 1);
        Check(view.TextActionsGlowBounds(spread) is null, "Halo leaves a clear center in narrow layouts");
        view.SetViewport(548, 24, 1);
        Check(view.TextActionsGlowBounds(spread) is null, "Halo leaves a clear center in short layouts");
        view.SetViewport(0, 0, 1);
        Check(view.TextActionsGlowBounds(spread) is null, "Collapsed viewport keeps the halo hidden");

        ViewportState offscreen = Create(10, 10, 100, 100, 1);
        offscreen.ZoomTo(800, new(0, 0));
        Check(offscreen.TextActionsGlowBounds(spread) is null, "No phantom halo when no image pixels are visible");

        void CheckBounds(ViewportState state, double x, double y, double width, double height, string label)
        {
            var bounds = state.TextActionsGlowBounds(spread);
            Check(bounds.HasValue, label + " has bounds");
            var actual = bounds.GetValueOrDefault();
            Point(new(x, y), new(actual.X, actual.Y), label + " origin");
            Near(width, actual.Width, label + " width");
            Near(height, actual.Height, label + " height");
        }
    }

    private static void KnownMappings()
    {
        // A 1,000x500 image fits a 500x300 usable area at 50%: 500x250,
        // with a 24-DIP side gap and a 49-DIP top/bottom gap.
        ViewportState view = Create(1000, 500, 548, 348, 1);
        Near(50, view.ZoomPercent, "Known Fit physical zoom");
        Near(500, view.DisplayWidth, "Known Fit displayed width");
        Near(250, view.DisplayHeight, "Known Fit displayed height");
        Point(new(24, 49), view.ViewportAt(new(0, 0)), "Known top-left image edge");
        Point(new(274, 174), view.ViewportAt(new(500, 250)), "Known center mapping");
        Point(new(524, 299), view.ViewportAt(new(1000, 500)), "Known bottom-right image edge");
        Point(new(250, 100), view.ImageAt(new(149, 99)), "Known inverse mapping");

        ViewportState highDpi = Create(1000, 500, 548, 348, 2);
        Near(100, highDpi.ZoomPercent, "Same DIP geometry is physical 100% at 200% DPI");
        Point(new(24, 49), highDpi.Origin, "Known high-DPI origin");
        Point(new(274, 174), highDpi.ViewportAt(new(500, 250)), "Known high-DPI center");

        foreach (double raster in new[] { 0.5, 1, 1.25, 1.5, 2, 3, 4 })
        {
            ViewportState native = Create(800, 600, 1500, 1100, raster);
            native.ZoomTo(100, native.Center);
            CanvasPoint first = native.ViewportAt(new(73, 91));
            CanvasPoint second = native.ViewportAt(new(173, 241));
            Near(100, (second.X - first.X) * raster, "100% maps 100 source pixels to 100 physical pixels");
            Near(150, (second.Y - first.Y) * raster, "100% vertical physical pixel mapping");
            Point(new(73, 91), native.ImageAt(first), "Native-scale inverse mapping");
            Check(!native.IsFit, "Explicit 100% selects manual view intent");
        }
    }

    private static void FitMatrix()
    {
        (int Width, int Height)[] images = { (144, 121), (759, 301), (4000, 1800), (600, 6000) };
        (double Width, double Height)[] viewports = { (320, 180), (855, 439), (1920, 1080) };
        foreach (var image in images)
        foreach (var viewport in viewports)
        foreach (double raster in new[] { 1, 1.25, 1.5, 2, 3 })
        {
            ViewportState view = Create(image.Width, image.Height, viewport.Width, viewport.Height, raster);
            string label = $"{image.Width}x{image.Height} in {viewport.Width}x{viewport.Height} at {raster}";
            Check(view.IsFit, label + " selects Fit");
            Check(view.ZoomPercent > 0 && view.ZoomPercent <= 100 + 1e-9, label + " never enlarges beyond native physical size");
            Check(view.DisplayWidth <= viewport.Width - 48 + 1e-8, label + " fits available horizontal space");
            Check(view.DisplayHeight <= viewport.Height - 48 + 1e-8, label + " fits available vertical space");
            CheckCentered(view, label);
            Near((double)image.Width / image.Height, view.DisplayWidth / view.DisplayHeight, label + " preserves aspect ratio");
            CanvasPoint source = new(image.Width * 0.37, image.Height * 0.63);
            Point(source, view.ImageAt(view.ViewportAt(source)), label + " round trip");

            // A fitted image either reaches physical 100% or touches one of
            // the two independently specified 24-DIP padding boundaries.
            bool reachesLimit = Math.Abs(view.ZoomPercent - 100) < 1e-8 ||
                Math.Abs(view.DisplayWidth - (viewport.Width - 48)) < 1e-8 ||
                Math.Abs(view.DisplayHeight - (viewport.Height - 48)) < 1e-8;
            Check(reachesLimit, label + " uses available space instead of retaining a hidden row");

            CanvasPoint fittedOrigin = view.Origin;
            view.Pan(10000, -10000);
            Check(view.IsFit, label + " cannot leave Fit when no image overflow can pan");
            Point(fittedOrigin, view.Origin, label + " fitted image does not move under an ineffective pan");
        }
    }

    private static void NativeRasterAlignment()
    {
        foreach (double raster in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.25, 3 })
        {
            CanvasPoint root = new(19.31, 87.17);
            ViewportState view = Create(301, 169, 663.5, 414.25, raster);
            view.SetRasterRootOffset(root);
            Near(100, view.ZoomPercent, "Small image fits at actual size");
            Check(view.IsFit, "Pixel alignment preserves Fit intent");
            VerifyGrid(view, root);

            // Changing a toolbar row can move the canvas within the XAML root
            // without changing its width or height.
            CanvasPoint previousOrigin = view.Origin;
            CanvasPoint previousDisplayOrigin = view.DisplayOrigin;
            root = new(19.27, 106.83);
            Check(view.SetRasterRootOffset(root) && view.DisplayOrigin != previousDisplayOrigin,
                "A moved canvas gets a new aligned local origin");
            Point(previousOrigin, view.Origin, "Raster movement does not change the logical view");
            Check(!view.SetRasterRootOffset(root), "An unchanged layout does not require repainting");
            VerifyGrid(view, root);

            view.ZoomTo(87, view.Center);
            Check(!view.UsesNativePixels, "A different zoom keeps normal subpixel placement");
            Point(view.Origin, view.DisplayOrigin, "Other zoom levels are not snapped");
            Near(1, view.FrameThickness, "Other zoom levels keep the one-DIP border");
            view.ZoomTo(100, view.Center);
            Check(!view.IsFit, "Explicit actual size remains a manual view");
            VerifyGrid(view, root);
        }

        static void VerifyGrid(ViewportState view, CanvasPoint root)
        {
            double raster = view.RasterizationScale;
            CanvasPoint first = view.ViewportAt(new(0, 0));
            CanvasPoint last = view.ViewportAt(new(view.ImageWidth, view.ImageHeight));
            double x = (first.X + root.X) * raster;
            double y = (first.Y + root.Y) * raster;
            Near(Math.Round(x), x, "The image starts on a horizontal device pixel");
            Near(Math.Round(y), y, "The image starts on a vertical device pixel");
            Near(view.ImageWidth, (last.X - first.X) * raster, "The image width is an exact device-pixel count");
            Near(view.ImageHeight, (last.Y - first.Y) * raster, "The image height is an exact device-pixel count");
            Near(1, view.FrameThickness * raster, "The frame inset is exactly one device pixel");
            Check(Math.Abs((first.X - view.Origin.X) * raster) <= .5 + 1e-9 &&
                Math.Abs((first.Y - view.Origin.Y) * raster) <= .5 + 1e-9,
                "The actual image stays within half a device pixel of the logical origin");
            Check(view.Contains(first) && !view.Contains(new(first.X - .01, first.Y)),
                "Pointer hit testing starts at the aligned image edge");
            Point(new(17, 9), view.ImageAt(view.ViewportAt(new(17, 9))), "Overlays and pointer input agree on the same source pixel");
        }
    }

    private static void LargeImageFit()
    {
        ViewportState view = Create(32768, 2048, 256, 160, 1);
        Near(208, view.DisplayWidth, "Large Fit spans the usable width");
        Near(13, view.DisplayHeight, "Large Fit keeps the exact aspect ratio");
        Point(new(24, 73.5), view.Origin, "Large Fit remains centered");
        Check(view.ZoomPercent < 1 && view.ZoomPercent > 0, "Fit can be below manual 1%");
        Point(new(16384, 1024), view.ImageAt(view.Center), "Large image center remains addressable");

        view.ZoomTo(0.5, view.Center);
        Near(1, view.ZoomPercent, "Manual zoom clamps to its 1% floor");
        Point(new(16384, 1024), view.ImageAt(view.Center), "Manual floor preserves the source anchor");
        view.Fit();
        Near(208, view.DisplayWidth, "Fit restores a value below the manual floor");
        CheckCentered(view, "Restored large Fit");
    }

    private static void AnchoredZoom()
    {
        foreach (double raster in new[] { 1, 1.25, 1.5, 2, 3 })
        {
            ViewportState view = Create(1600, 900, 800, 520, raster);
            CanvasPoint anchor = new(173.25, 217.75);
            CanvasPoint original = view.ImageAt(anchor);
            foreach (double zoom in new[] { 100, 175, 42.5, 800, 1, 125 })
            {
                view.ZoomTo(zoom, anchor);
                Near(zoom, view.ZoomPercent, "Requested manual zoom is retained");
                if (view.UsesNativePixels)
                {
                    CanvasPoint atCursor = view.ImageAt(anchor);
                    CanvasPoint drawn = view.ViewportAt(original);
                    Check(Math.Abs(atCursor.X - original.X) <= .5 + 1e-9 && Math.Abs(atCursor.Y - original.Y) <= .5 + 1e-9,
                        "Actual-size pointer anchor stays within half a source pixel");
                    Check(Math.Abs((drawn.X - anchor.X) * raster) <= .5 + 1e-9 &&
                        Math.Abs((drawn.Y - anchor.Y) * raster) <= .5 + 1e-9,
                        "Actual-size visual anchor stays within half a device pixel");
                }
                else
                {
                    Point(original, view.ImageAt(anchor), "Repeated zoom keeps the same source point under the cursor");
                    Point(anchor, view.ViewportAt(original), "Anchored source point renders at the cursor");
                }
                Check(!view.IsFit, "Manual zoom does not silently refit");
            }
            view.ZoomTo(-100, anchor);
            Near(1, view.ZoomPercent, "Finite negative manual zoom clamps to 1%");
            view.ZoomTo(900, anchor);
            Near(800, view.ZoomPercent, "Manual zoom clamps to 800%");
            Point(original, view.ImageAt(anchor), "Clamped zoom preserves the cursor anchor");
        }
    }

    private static void ManualResizeAndDpi()
    {
        ViewportState view = Create(2400, 1600, 800, 500, 1);
        view.ZoomTo(125, view.Center);
        view.Pan(-70, 53);
        CanvasPoint centerSource = view.ImageAt(view.Center);
        double physicalWidth = view.DisplayWidth * view.RasterizationScale;
        foreach (var viewport in new[] { (653d, 417d, 1d), (1001d, 709d, 1.25d),
                     (653d, 417d, 2d), (1200d, 800d, 3d), (800d, 500d, 1d) })
        {
            view.SetViewport(viewport.Item1, viewport.Item2, viewport.Item3);
            Near(125, view.ZoomPercent, "Resize and DPI changes retain manual physical zoom");
            Check(!view.IsFit, "Resize and DPI changes retain manual intent");
            Point(centerSource, view.ImageAt(view.Center), "Resize and DPI change retain the center source anchor");
            Near(physicalWidth, view.DisplayWidth * view.RasterizationScale, "Manual image physical width survives DPI change");
        }
        State before = Capture(view);
        view.SetViewport(view.Width, view.Height, view.RasterizationScale);
        Same(before, Capture(view), "Repeated layout with identical bounds is idempotent");
        view.Fit();
        Check(view.IsFit, "Explicit Fit restores Fit intent");
        CheckCentered(view, "Fit after manual pan/resize/DPI");
    }

    private static void PanLimits()
    {
        ViewportState view = Create(1000, 500, 400, 300, 1);
        view.ZoomTo(100, view.Center);
        view.Pan(10000, 10000);
        Point(new(24, 24), view.Origin, "Pan toward the beginning stops at its 24-DIP padding");
        view.Pan(-10000, -10000);
        Point(new(-624, -224), view.Origin, "Pan toward the end exposes the ending edges with padding");
        Point(new(376, 276), view.ViewportAt(new(1000, 500)), "Ending edges remain reachable");
        view.Pan(73, 29);
        Point(new(-551, -195), view.Origin, "Ordinary pan applies the requested DIP delta");
        Near(100, view.ZoomPercent, "Pan does not change physical zoom");

        ViewportState shortImage = Create(1000, 100, 400, 300, 1);
        shortImage.ZoomTo(100, shortImage.Center);
        shortImage.Pan(-50, 10000);
        Near(100, shortImage.Origin.Y, "Short image is centered on its fitting vertical axis");
        shortImage.Pan(10000, -10000);
        Near(100, shortImage.Origin.Y, "Panning cannot strand a fitting axis outside the viewport");
        Near(24, shortImage.Origin.X, "Non-fitting horizontal axis can still pan");

        ViewportState small = Create(100, 80, 400, 300, 2);
        small.ZoomTo(100, small.Center);
        small.Pan(10000, -10000);
        Point(new(175, 130), small.Origin, "A small image remains centered on both axes");

        ViewportState fittedSmall = Create(100, 80, 400, 300, 2);
        fittedSmall.Pan(10000, -10000);
        Check(fittedSmall.IsFit, "An ineffective pan over a fully fitting image retains Fit intent");
        fittedSmall.SetViewport(500, 400, 2);
        Point(new(225, 180), fittedSmall.Origin, "Later resize still refits after an ineffective pan");
    }

    private static void EdgesAndClipping()
    {
        ViewportState view = Create(1000, 500, 548, 348, 1);
        Check(view.Contains(new(24, 49)), "Image includes its top-left geometric edge");
        Check(view.Contains(new(524, 299)), "Image includes its bottom-right geometric edge");
        Check(view.Contains(new(274, 174)), "Image includes its center");
        Check(!view.Contains(new(23.999, 174)), "Left margin does not start an image gesture");
        Check(!view.Contains(new(524.001, 174)), "Right margin does not start an image gesture");
        Check(!view.Contains(new(274, 48.999)), "Top margin does not start an image gesture");
        Check(!view.Contains(new(274, 299.001)), "Bottom margin does not start an image gesture");
        Point(new(0, 0), view.ClampedImageAt(new(-500, -500)), "Out-of-image pointer clips to the starting edge");
        Point(new(1000, 500), view.ClampedImageAt(new(2000, 2000)), "Out-of-image pointer clips to the ending edge");
        Point(new(250, 100), view.ClampedImageAt(new(149, 99)), "Clipping does not move a valid interior point");
    }

    private static void EmptyAndDegenerate()
    {
        ViewportState empty = new();
        Check(!empty.HasImage && !empty.Contains(new(0, 0)), "Empty viewport has no image hit target");
        empty.Pan(5, -5);
        Point(new(0, 0), empty.Origin, "Panning an empty viewport changes no origin");
        empty.Fit();
        Near(100, empty.ZoomPercent, "Empty Fit keeps a finite native zoom");
        empty.SetViewport(0, 0, 2);
        Check(!empty.HasImage && empty.IsFit, "Zero-sized empty viewport is valid during layout");

        ViewportState delayedLayout = new();
        delayedLayout.SetImage(32768, 2048);
        foreach (var size in new[] { (0d, 0d), (0d, 400d), (400d, 0d), (1d, 1d), (24d, 47d), (48d, 48d) })
        {
            delayedLayout.SetViewport(size.Item1, size.Item2, 2);
            CheckFinite(delayedLayout, "Image before usable layout");
            CheckCentered(delayedLayout, "Tiny/zero layout center");
        }
        delayedLayout.SetViewport(1000, 700, 2);
        Check(delayedLayout.DisplayWidth <= 952 && delayedLayout.DisplayHeight <= 652, "A later real layout recovers normally");
        CheckCentered(delayedLayout, "Recovered viewport");
    }

    private static void InvalidInputs()
    {
        ViewportState view = Create(1000, 500, 548, 348, 1.5);
        view.ZoomTo(125, new(137, 91));
        State before = Capture(view);
        Action[] invalid =
        {
            () => view.SetImage(0, 10), () => view.SetImage(10, 0),
            () => view.SetImage(-1, 10), () => view.SetViewport(-1, 300, 1),
            () => view.SetViewport(400, -1, 1), () => view.SetViewport(400, 300, 0),
            () => view.SetViewport(400, 300, -1), () => view.SetViewport(double.NaN, 300, 1),
            () => view.SetViewport(400, double.PositiveInfinity, 1),
            () => view.SetViewport(400, 300, double.NaN),
            () => view.SetViewport(400, 300, double.PositiveInfinity),
            () => view.ZoomTo(double.NaN, view.Center),
            () => view.ZoomTo(double.PositiveInfinity, view.Center),
            () => view.ZoomTo(100, new(double.NaN, 10)),
            () => view.Pan(double.NegativeInfinity, 0), () => view.Pan(0, double.NaN),
            () => view.ImageAt(new(double.NaN, 0)),
            () => view.ViewportAt(new(0, double.PositiveInfinity))
        };
        foreach (Action action in invalid)
        {
            Expect<ArgumentOutOfRangeException>(action, "Non-finite or invalid input is rejected");
            Same(before, Capture(view), "Rejected input preserves all view state");
        }
    }

    private static ViewportState Create(int imageWidth, int imageHeight, double width, double height, double raster)
    {
        ViewportState view = new();
        view.SetViewport(width, height, raster);
        view.SetImage(imageWidth, imageHeight);
        return view;
    }

    private static void CheckCentered(ViewportState view, string label)
    {
        Near(view.Origin.X, view.Width - view.Origin.X - view.DisplayWidth, label + " equal left/right gaps");
        Near(view.Origin.Y, view.Height - view.Origin.Y - view.DisplayHeight, label + " equal top/bottom gaps");
        CanvasPoint drawnCenter = view.ViewportAt(new(view.ImageWidth / 2d, view.ImageHeight / 2d));
        if (view.UsesNativePixels)
            Check(Math.Abs((drawnCenter.X - view.Center.X) * view.RasterizationScale) <= .5 + 1e-9 &&
                Math.Abs((drawnCenter.Y - view.Center.Y) * view.RasterizationScale) <= .5 + 1e-9,
                label + " actual-size image center stays within half a device pixel");
        else
            Point(view.Center, drawnCenter, label + " image center at viewport center");
    }

    private static void CheckFinite(ViewportState view, string label)
    {
        Check(double.IsFinite(view.ZoomPercent) && view.ZoomPercent > 0, label + " finite positive zoom");
        Check(double.IsFinite(view.Scale) && view.Scale > 0, label + " finite positive scale");
        Check(double.IsFinite(view.Origin.X) && double.IsFinite(view.Origin.Y), label + " finite origin");
        Check(double.IsFinite(view.DisplayWidth) && double.IsFinite(view.DisplayHeight), label + " finite displayed dimensions");
    }

    private static State Capture(ViewportState view) => new(view.ImageWidth, view.ImageHeight,
        view.Width, view.Height, view.RasterizationScale, view.ZoomPercent, view.IsFit, view.Origin);

    private static void Same(State expected, State actual, string label)
    {
        Check(expected == actual, label);
    }

    private static void Run(string name, Action action)
    {
        int startingCount = assertions;
        try
        {
            action();
            cases.Add(new { Name = name, Passed = true, Assertions = assertions - startingCount });
        }
        catch
        {
            cases.Add(new { Name = name, Passed = false, Assertions = assertions - startingCount });
            throw;
        }
    }

    private static void Point(CanvasPoint expected, CanvasPoint actual, string label)
    {
        Near(expected.X, actual.X, label + " X");
        Near(expected.Y, actual.Y, label + " Y");
    }

    private static void Near(double expected, double actual, string label)
    {
        // Source coordinates can be large after manual zoom. Relative tolerance
        // stays sub-millionth of a source pixel for this fixture's 32K limit.
        double tolerance = 1e-8 + Math.Abs(expected) * 1e-10;
        Check(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance,
            $"{label}: expected {expected:R}, actual {actual:R}, tolerance {tolerance:R}");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action, string label) where T : Exception
    {
        assertions++;
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(label + ": expected " + typeof(T).Name);
    }

    private readonly record struct State(int ImageWidth, int ImageHeight, double Width, double Height,
        double RasterizationScale, double ZoomPercent, bool IsFit, CanvasPoint Origin);
}
