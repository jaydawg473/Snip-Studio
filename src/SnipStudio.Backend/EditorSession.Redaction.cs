#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

/// <summary>A flattened PNG containing only the pixels shown in a redaction review.</summary>
public sealed class RedactedImage
{
    private readonly byte[] pngBytes;

    internal RedactedImage(byte[] pngBytes) => this.pngBytes = pngBytes;

    public ReadOnlyMemory<byte> PngBytes => pngBytes;

    public Task SaveCopyAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a new .png filename for the redacted image.", nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        // A redacted export always creates a copy. Even different path spellings
        // or a destination that appears late must never replace the original.
        var target = new ImageSaveTarget(Path.GetFullPath(path), null);
        return Task.Run(() =>
        {
            if (File.Exists(target.Path)) throw new IOException("Choose a new filename for the redacted image.");
            AtomicImageWriter.WriteStaged(target, output => output.Write(pngBytes), cancellationToken);
        }, cancellationToken);
    }
}

public sealed partial class EditorSession
{
    public const int MaximumRedactionAreas = 128;

    /// <summary>Replaces every pixel touched by the chosen areas in a detached preview.</summary>
    public static Task<RedactedImage> RenderRedactedCopyAsync(PreviewSnapshot source,
        IReadOnlyList<ImageRectangle> areas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(areas);
        if (areas.Count is < 1 or > MaximumRedactionAreas)
            throw new ArgumentOutOfRangeException(nameof(areas), $"Choose between 1 and {MaximumRedactionAreas} redaction areas.");
        ImageRectangle[] selected = areas.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            using MemoryStream input = new(source.PngBytes, writable: false);
            using Bitmap original = new(input);
            ValidateImage(original);
            Rectangle[] covered = selected.Select(area => RedactionPixels(area, original.Width, original.Height)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();

            // A fresh pixel buffer has no source metadata, embedded preview,
            // original image, annotations or layers that could be recovered.
            using Bitmap pixels = global::SnipStudio.EditorDocument.CopyImage(original);
            using (Graphics graphics = Graphics.FromImage(pixels))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.FillRectangles(Brushes.Black, covered);
            }
            cancellationToken.ThrowIfCancellationRequested();
            using MemoryStream output = new();
            pixels.Save(output, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            return new RedactedImage(output.ToArray());
        }, cancellationToken);
    }

    private static Rectangle RedactionPixels(ImageRectangle area, int width, int height)
    {
        if (area.IsEmpty || !double.IsFinite(area.Left) || !double.IsFinite(area.Top) ||
            !double.IsFinite(area.Right) || !double.IsFinite(area.Bottom))
            throw new ArgumentException("Draw a bounded redaction area on the image.");

        double left = Math.Clamp(area.Left, 0, width), right = Math.Clamp(area.Right, 0, width);
        double top = Math.Clamp(area.Top, 0, height), bottom = Math.Clamp(area.Bottom, 0, height);
        if (right <= left || bottom <= top) throw new ArgumentException("Draw the redaction area on the image.");

        // Round outwards so a partial pixel at any edge is entirely replaced.
        return Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
            (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
    }
}
