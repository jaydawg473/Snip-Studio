#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend
{
    /// <summary>A detached, display-only preview of an annotation and its neighbors in stacking order.</summary>
    public sealed record AnnotationDragPreview(byte[] BelowPng, byte[] ObjectPng, byte[]? AbovePng,
        ImageRectangle ObjectFrame);

    public sealed partial class EditorSession
    {
        public Task<AnnotationDragPreview?> RenderAnnotationDragPreviewAsync(AnnotationSnapshot selection,
            CancellationToken cancellationToken)
        {
            VerifyAccess();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAnnotationCurrent(selection)) return Task.FromResult<AnnotationDragPreview?>(null);

            // Native rich text reuses state on the editor STA. Detach the
            // bounded rasters here and move only their PNG encodes off-thread.
            var layers = document.RenderAnnotationDragLayers(selection.Index, cancellationToken);
            if (layers is null) return Task.FromResult<AnnotationDragPreview?>(null);
            try
            {
                return Task.Run<AnnotationDragPreview?>(() =>
                {
                    using (layers)
                    {
                        byte[] Encode(Bitmap bitmap)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            using var stream = new MemoryStream();
                            bitmap.Save(stream, ImageFormat.Png);
                            cancellationToken.ThrowIfCancellationRequested();
                            return stream.ToArray();
                        }
                        return new AnnotationDragPreview(Encode(layers.Below), Encode(layers.Selected),
                            layers.Above is null ? null : Encode(layers.Above), layers.Frame);
                    }
                });
            }
            catch
            {
                layers.Dispose();
                throw;
            }
        }
    }
}

namespace SnipStudio
{
    public sealed partial class EditorDocument
    {
        private const int DragLayerPixelLimit = 1920 * 1080;
        private const int DragLayerDimensionLimit = 4096;

        internal sealed record AnnotationDragLayers(Bitmap Below, Bitmap Selected, Bitmap? Above,
            WinUI.Backend.ImageRectangle Frame) : IDisposable
        {
            public void Dispose()
            {
                Below.Dispose();
                Selected.Dispose();
                Above?.Dispose();
            }
        }

        internal AnnotationDragLayers? RenderAnnotationDragLayers(int index, CancellationToken token)
        {
            RequireImage();
            if (index < 0 || index >= AnnotationCount) return null;
            RectangleF image = new(PointF.Empty, ImageSize);
            RectangleF ink = Current.Annotations[index].Bounds;
            if (!float.IsFinite(ink.X) || !float.IsFinite(ink.Y) || !float.IsFinite(ink.Width) ||
                !float.IsFinite(ink.Height) || ink.Width <= 0 || ink.Height <= 0) return null;
            // A partly cropped object can be moved back onto the image.
            // Capture its entire sprite, then let the viewport clip it.
            RectangleF sprite = RectangleF.FromLTRB(MathF.Floor(ink.Left) - 2, MathF.Floor(ink.Top) - 2,
                MathF.Ceiling(ink.Right) + 2, MathF.Ceiling(ink.Bottom) + 2);

            Bitmap? below = null, selected = null, above = null;
            try
            {
                below = RenderDragLayer(image, from: 0, to: index, drawBase: true, token);
                selected = RenderDragLayer(sprite, from: index, to: index + 1, drawBase: false, token);
                if (index + 1 < AnnotationCount)
                    above = RenderDragLayer(image, from: index + 1, to: AnnotationCount, drawBase: false, token);
                return new AnnotationDragLayers(below, selected, above,
                    new(sprite.X, sprite.Y, sprite.Width, sprite.Height));
            }
            catch
            {
                below?.Dispose();
                selected?.Dispose();
                above?.Dispose();
                throw;
            }
        }

        private Bitmap RenderDragLayer(RectangleF area, int from, int to, bool drawBase, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            double scale = Math.Min(1, Math.Min(DragLayerDimensionLimit / Math.Max(area.Width, area.Height),
                Math.Sqrt(DragLayerPixelLimit / ((double)area.Width * area.Height))));
            int width = Math.Max(1, (int)Math.Floor(area.Width * scale));
            int height = Math.Max(1, (int)Math.Floor(area.Height * scale));
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                if (drawBase)
                {
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.DrawImage(Current.Frame.Bitmap, new Rectangle(0, 0, width, height),
                        0, 0, ImageSize.Width, ImageSize.Height, GraphicsUnit.Pixel);
                }
                float x = width / area.Width, y = height / area.Height;
                using var coordinates = new Matrix(x, 0, 0, y, -area.X * x, -area.Y * y);
                graphics.Transform = coordinates;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                for (int index = from; index < to; index++)
                {
                    token.ThrowIfCancellationRequested();
                    Current.Annotations[index].Draw(graphics);
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }
}
