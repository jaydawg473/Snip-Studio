#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace SnipStudio.WinUI.Backend;

internal static class AtomicImageWriter
{
    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose an image filename.", nameof(path));
        string fullPath = System.IO.Path.GetFullPath(path);
        _ = FormatFor(fullPath);
        return fullPath;
    }

    private static ImageFormat? FormatFor(string path)
    {
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            ".pdf" => null,
            _ => throw new ArgumentException("Choose a PNG, JPEG, Bitmap or PDF filename.", nameof(path))
        };
    }

    internal static void Save(Bitmap image, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageFormat? format = FormatFor(path);
        // PDF retains the existing encoder and gains a cancellation boundary
        // before its completed staging file is published to the destination.
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            ".snip-winui-" + Guid.NewGuid().ToString("N") + (format == null ? ".pdf" : ".tmp"));
        try
        {
            if (format == null)
            {
                global::SnipStudio.PdfExportService.Save(image, temporary);
            }
            else
            {
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (format.Guid == ImageFormat.Jpeg.Guid)
                    {
                        using Bitmap flattened = new(image.Width, image.Height, PixelFormat.Format24bppRgb);
                        using (Graphics graphics = Graphics.FromImage(flattened))
                        {
                            graphics.Clear(Color.White);
                            graphics.DrawImageUnscaled(image, 0, 0);
                        }
                        flattened.Save(output, format);
                    }
                    else
                    {
                        image.Save(output, format);
                    }
                    output.Flush(true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            temporary = "";
            // Once published, report success even if cancellation arrives now.
        }
        finally
        {
            if (temporary.Length != 0)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
