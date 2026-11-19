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
    /// Shows Windows' printer/options dialog and sends one aspect-preserving
    /// page. False means canceled. A successful spool is not printer delivery.
    /// Keep the owner window open while the native driver is working.
    /// </summary>
    public static bool PrintImage(byte[] png, nint owner)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Open the Windows print dialog on the application's STA thread.");
        if (png.Length == 0 || png.LongLength > 512L * 1024 * 1024)
            throw new ArgumentException("The print image is empty or exceeds the supported size.", nameof(png));
        using MemoryStream input = new(png, false);
        using Bitmap image = new(input);
        if (image.Width > 32768 || image.Height > 32768 || (long)image.Width * image.Height > 64L * 1024 * 1024)
            throw new ArgumentException("The print image dimensions exceed the supported size.", nameof(png));
        using PrintDocument document = new()
        {
            DocumentName = "Snip Studio image",
            OriginAtMargins = true,
            PrintController = new StandardPrintController()
        };
        document.DefaultPageSettings.Landscape = image.Width > image.Height;
        document.DefaultPageSettings.Margins = new Margins(36, 36, 36, 36);
        document.PrintPage += (_, args) =>
        {
            if (args.Graphics is null) throw new InvalidOperationException("The printer did not provide a page surface.");
            RectangleF area = RectangleF.Intersect(args.MarginBounds, args.PageSettings.PrintableArea);
            if (area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("The printer returned an empty printable page. Change the page margins or printer.");
            // The native page's printable area uses physical page coordinates;
            // OriginAtMargins translates the Graphics origin to the user margins.
            area.Offset(-args.MarginBounds.Left, -args.MarginBounds.Top);
            float scale = Math.Min(area.Width / image.Width, area.Height / image.Height);
            float width = image.Width * scale, height = image.Height * scale;
            RectangleF destination = new(area.Left + (area.Width - width) / 2,
                area.Top + (area.Height - height) / 2, width, height);
            GraphicsState state = args.Graphics.Save();
            try
            {
                args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                args.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                args.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                args.Graphics.FillRectangle(Brushes.White, destination);
                args.Graphics.DrawImage(image, destination, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
            }
            finally { args.Graphics.Restore(state); }
            args.HasMorePages = false;
        };
        using PrintDialog dialog = new()
        {
            Document = document, UseEXDialog = true, AllowCurrentPage = false,
            AllowSelection = false, AllowSomePages = false, ShowNetwork = true
        };
        if (dialog.ShowDialog(new WindowOwner(owner)) != DialogResult.OK) return false;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("The selected printer is unavailable. Choose another printer or save as PDF.");
        document.Print();
        return true;
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
