using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class ToolCursor : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
            internal uint HotspotX, HotspotY;
            internal IntPtr MaskBitmap, ColorBitmap;
        }
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        private IntPtr handle;
        private bool owns;
        public Cursor Cursor { get; private set; }

        private ToolCursor(Cursor cursor, IntPtr handle, bool owns)
        {
            Cursor = cursor;
            this.handle = handle;
            this.owns = owns;
        }

        public static ToolCursor CreateEraser() { return Create(EditorTool.Eraser, 96); }

        public static ToolCursor Create(EditorTool tool, int dpi)
        {
            IntPtr original = IntPtr.Zero;
            IconInfo info = new IconInfo();
            try
            {
                string glyph;
                PointF contact;
                switch (tool)
                {
                    case EditorTool.Pen: glyph = "pen"; contact = new PointF(4, 20); break;
                    case EditorTool.Highlighter: glyph = "highlighter"; contact = new PointF(4.8f, 18); break;
                    case EditorTool.Arrow: glyph = "arrow"; contact = new PointF(19, 5); break;
                    case EditorTool.Rectangle: glyph = "box"; contact = new PointF(4, 5.5f); break;
                    case EditorTool.Text: glyph = "text"; contact = new PointF(12, 20); break;
                    case EditorTool.Eraser: glyph = "eraser"; contact = new PointF(7.2f, 19.5f); break;
                    case EditorTool.RemoveArea: glyph = "remove-area"; contact = new PointF(4, 4); break;
                    case EditorTool.Crop: glyph = "crop"; contact = new PointF(7, 7); break;
                    default: return new ToolCursor(Cursors.SizeAll, IntPtr.Zero, false);
                }
                float scale = Math.Max(1f, Math.Min(3f, dpi / 96f));
                int size = (int)Math.Round(32 * scale);
                int glyphSize = (int)Math.Round(26 * scale);
                int inset = (size - glyphSize) / 2;
                int halo = Math.Max(1, (int)Math.Round(scale));
                int hotspotX = Math.Max(0, Math.Min(size - 1, (int)Math.Round(inset + contact.X * glyphSize / 24f)));
                int hotspotY = Math.Max(0, Math.Min(size - 1, (int)Math.Round(inset + contact.Y * glyphSize / 24f)));
                using (Bitmap bitmap = new Bitmap(size, size))
                using (Bitmap light = IconArtwork.CreateToolIcon(glyph, glyphSize, Color.White))
                using (Bitmap dark = IconArtwork.CreateToolIcon(glyph, glyphSize, Color.FromArgb(18, 30, 46)))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        // Reuse the selected button's actual geometry. A white
                        // halo and dark stroke remain visible on arbitrary snips.
                        for (int dy = -halo; dy <= halo; dy += halo)
                            for (int dx = -halo; dx <= halo; dx += halo)
                                g.DrawImage(light, new Rectangle(inset + (glyphSize - light.Width) / 2 + dx, inset + (glyphSize - light.Height) / 2 + dy,
                                    light.Width, light.Height), 0, 0, light.Width, light.Height, GraphicsUnit.Pixel);
                        g.DrawImage(dark, new Rectangle(inset + (glyphSize - dark.Width) / 2, inset + (glyphSize - dark.Height) / 2,
                            dark.Width, dark.Height), 0, 0, dark.Width, dark.Height, GraphicsUnit.Pixel);
                        // The small contact dot is the precise image coordinate;
                        // it does not add a generic cross over the tool artwork.
                        float outer = 1.8f * scale, inner = .8f * scale;
                        using (Brush white = new SolidBrush(Color.White))
                        using (Brush ink = new SolidBrush(Color.FromArgb(18, 30, 46)))
                        {
                            g.FillEllipse(white, hotspotX - outer, hotspotY - outer, outer * 2, outer * 2);
                            g.FillEllipse(ink, hotspotX - inner, hotspotY - inner, inner * 2, inner * 2);
                        }
                    }
                    original = bitmap.GetHicon();
                    info.HotspotX = (uint)hotspotX;
                    info.HotspotY = (uint)hotspotY;
                }
                uint cursorX = info.HotspotX, cursorY = info.HotspotY;
                if (!GetIconInfo(original, out info)) throw new InvalidOperationException("Cursor image unavailable.");
                info.IsIcon = false;
                info.HotspotX = cursorX;
                info.HotspotY = cursorY;
                IntPtr cursor = CreateIconIndirect(ref info);
                if (cursor == IntPtr.Zero) throw new InvalidOperationException("Cursor could not be created.");
                try { return new ToolCursor(new Cursor(cursor), cursor, true); }
                catch { DestroyCursor(cursor); throw; }
            }
            catch (Exception) { return new ToolCursor(tool == EditorTool.Text ? Cursors.IBeam : Cursors.Default, IntPtr.Zero, false); }
            finally
            {
                if (original != IntPtr.Zero) DestroyIcon(original);
                if (info.MaskBitmap != IntPtr.Zero) DeleteObject(info.MaskBitmap);
                if (info.ColorBitmap != IntPtr.Zero) DeleteObject(info.ColorBitmap);
            }
        }

        public void Dispose()
        {
            if (!owns) return;
            owns = false;
            // Cursor(IntPtr) wraps a caller-owned native handle. Dispose its
            // managed wrapper, then release that handle exactly once ourselves.
            IntPtr released = handle;
            handle = IntPtr.Zero;
            try { if (Cursor != null) Cursor.Dispose(); }
            finally { if (released != IntPtr.Zero) DestroyCursor(released); }
        }
    }
}
