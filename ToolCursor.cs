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

        public static ToolCursor CreateEraser()
        {
            IntPtr original = IntPtr.Zero;
            IconInfo info = new IconInfo();
            try
            {
                using (Bitmap bitmap = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        PointF[] body = { new PointF(8, 20), new PointF(21, 5), new PointF(29, 12), new PointF(16, 27) };
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            path.AddPolygon(body);
                            using (Pen outline = new Pen(Color.FromArgb(18, 30, 46), 4f)) { outline.LineJoin = LineJoin.Round; g.DrawPath(outline, path); }
                            using (Brush fill = new SolidBrush(Color.FromArgb(97, 185, 255))) g.FillPath(fill, path);
                            using (Pen edge = new Pen(Color.White, 1.15f)) { edge.LineJoin = LineJoin.Round; g.DrawPath(edge, path); }
                        }
                        PointF[] rubber = { new PointF(8, 20), new PointF(13, 14), new PointF(21, 21), new PointF(16, 27) };
                        using (Brush fill = new SolidBrush(Color.FromArgb(246, 249, 253))) g.FillPolygon(fill, rubber);
                        using (Pen seam = new Pen(Color.FromArgb(30, 74, 113), 1.5f)) g.DrawLine(seam, 13, 14, 21, 21);
                        // The contact corner is the hotspot, marked with a tiny contrasting tip.
                        using (Pen edge = new Pen(Color.FromArgb(18, 30, 46), 1.7f)) g.DrawLine(edge, 7, 27, 16, 27);
                        using (Pen tip = new Pen(Color.White, 1f)) { g.DrawLine(tip, 5, 27, 9, 27); g.DrawLine(tip, 7, 25, 7, 29); }
                    }
                    original = bitmap.GetHicon();
                }
                if (!GetIconInfo(original, out info)) throw new InvalidOperationException("Cursor image unavailable.");
                info.IsIcon = false;
                info.HotspotX = 7;
                info.HotspotY = 27;
                IntPtr cursor = CreateIconIndirect(ref info);
                if (cursor == IntPtr.Zero) throw new InvalidOperationException("Cursor could not be created.");
                try { return new ToolCursor(new Cursor(cursor), cursor, true); }
                catch { DestroyCursor(cursor); throw; }
            }
            catch (Exception) { return new ToolCursor(Cursors.Cross, IntPtr.Zero, false); }
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
            Cursor.Dispose();
            if (handle != IntPtr.Zero) { DestroyCursor(handle); handle = IntPtr.Zero; }
        }
    }
}
