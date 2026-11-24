#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

public static class DesktopPrintingService
{
    /// <summary>
    /// Previews one aspect-preserving page, then opens Windows' print options.
    /// Returns true only after Windows accepts the print job.
    /// </summary>
    public static bool PrintImage(byte[] png, nint owner)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Open the Windows print dialog on the application's STA thread.");
        if (png.Length == 0 || png.LongLength > 512L * 1024 * 1024)
            throw new ArgumentException("The print image is empty or exceeds the supported size.", nameof(png));
        var parent = new WindowOwner(owner);
        Bitmap thumbnail;
        Size imageSize;
        using (MemoryStream input = new(png, false))
        using (Bitmap image = new(input))
        {
            if (image.Width > 32768 || image.Height > 32768 || (long)image.Width * image.Height > 64L * 1024 * 1024)
                throw new ArgumentException("The print image dimensions exceed the supported size.", nameof(png));
            imageSize = image.Size;
            thumbnail = CreateThumbnail(image);
        }
        using (thumbnail)
        using (var preview = new DesktopPrintPreview(png, imageSize, thumbnail))
            return preview.ShowDialog(parent) == DialogResult.OK;
    }

    internal static PrintDocument PrepareDocument(PrinterSettings printer, PageSettings page)
    {
        var settings = (PrinterSettings)printer.Clone();
        var defaults = (PageSettings)page.Clone();
        defaults.PrinterSettings = settings;
        return new PrintDocument
        {
            DocumentName = "Snip Studio image",
            OriginAtMargins = true,
            PrintController = new StandardPrintController(),
            PrinterSettings = settings,
            DefaultPageSettings = defaults
        };
    }

    internal static bool Submit(PrintDocument document, byte[] png)
    {
        if (!document.PrinterSettings.IsValid) throw new InvalidPrinterException(document.PrinterSettings);
        using MemoryStream input = new(png, false);
        using Bitmap image = new(input);
        void RenderPage(object? sender, PrintPageEventArgs args)
        {
            if (args.Graphics is null) throw new InvalidOperationException("The printer did not provide a page surface.");
            RectangleF destination = CalculateDestination(image.Size, args.MarginBounds, args.PageSettings.PrintableArea);
            // OriginAtMargins places the graphics origin at the user margins.
            destination.Offset(-args.MarginBounds.Left, -args.MarginBounds.Top);
            DrawImage(args.Graphics, image, destination);
            args.HasMorePages = false;
        }
        document.PrintPage += RenderPage;
        try { document.Print(); }
        finally { document.PrintPage -= RenderPage; }
        return true;
    }

    public static RectangleF CalculateDestination(Size image, RectangleF margins, RectangleF printable)
    {
        RectangleF area = RectangleF.Intersect(margins, printable);
        if (area.Width <= 0 || area.Height <= 0)
            throw new InvalidOperationException("The margins leave no printable space. Change Page setup or choose another printer.");
        return FitInside(image, area);
    }

    internal static RectangleF FitInside(Size image, RectangleF area)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentOutOfRangeException(nameof(image));
        if (area.Width <= 0 || area.Height <= 0 || !float.IsFinite(area.Width) || !float.IsFinite(area.Height) ||
            !float.IsFinite(area.Left) || !float.IsFinite(area.Top))
            throw new ArgumentOutOfRangeException(nameof(area));
        double scale = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        float width = (float)(image.Width * scale), height = (float)(image.Height * scale);
        return new RectangleF(area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2, width, height);
    }

    internal static void DrawImage(Graphics graphics, Image image, RectangleF destination)
    {
        GraphicsState state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.FillRectangle(Brushes.White, destination);
            graphics.DrawImage(image, destination, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
        }
        finally { graphics.Restore(state); }
    }

    private static Bitmap CreateThumbnail(Bitmap original)
    {
        RectangleF fitted = FitInside(original.Size, new RectangleF(0, 0, 1600, 1600));
        var size = new Size(Math.Max(1, Math.Min(original.Width, (int)MathF.Round(fitted.Width))),
            Math.Max(1, Math.Min(original.Height, (int)MathF.Round(fitted.Height))));
        var result = new Bitmap(size.Width, size.Height);
        try
        {
            using Graphics graphics = Graphics.FromImage(result);
            DrawImage(graphics, original, new Rectangle(Point.Empty, size));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private sealed class WindowOwner : IWin32Window
    {
        public nint Handle { get; }
        public WindowOwner(nint handle)
        {
            if (handle == 0) throw new ArgumentException("The print dialog needs an owner window.", nameof(handle));
            Handle = handle;
        }
    }
}
