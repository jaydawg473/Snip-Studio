using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class ScreenColorPickerUnavailableException : InvalidOperationException
    {
        internal ScreenColorPickerUnavailableException(string message) : base(message) { }
    }

    // A sampling session is separate from capture policies. Its desktop image
    // is temporary, never saved, and never becomes an editor document.
    internal static class ScreenColorPicker
    {
        private const long MaximumSnapshotPixels = 64000000;
        private static int active;
        internal static bool IsActive { get { return Interlocked.CompareExchange(ref active, 0, 0) != 0; } }

        internal static Color? Pick(Control requester)
        {
            if (requester == null || requester.IsDisposed || requester.Disposing || requester.InvokeRequired)
                throw new ScreenColorPickerUnavailableException("Open the color picker on its window's thread.");
            Form owner = requester.FindForm();
            if (!MainForm.CanBeginColorSampling(owner) || !requester.Visible || !requester.Enabled)
                throw new ScreenColorPickerUnavailableException("Finish the current capture or recording before sampling a color.");
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                throw new ScreenColorPickerUnavailableException("A color sample is already being selected.");
            try
            {
                Rectangle desktopBounds = SystemInformation.VirtualScreen;
                if (desktopBounds.Width < 1 || desktopBounds.Height < 1 ||
                    (long)desktopBounds.Width * desktopBounds.Height > MaximumSnapshotPixels)
                    throw new ScreenColorPickerUnavailableException("The connected desktop exceeds the color sampler's memory limit.");
                Screen[] screens = Screen.AllScreens;
                Rectangle[] monitors = new Rectangle[screens.Length];
                for (int index = 0; index < screens.Length; index++) monitors[index] = screens[index].Bounds;
                // Keep the snip visible. This entry point does not hide windows,
                // change taskbars, run OCR, save an image, or use the clipboard.
                using (Bitmap snapshot = CaptureService.CaptureRegion(desktopBounds))
                using (ScreenColorPickerForm picker = new ScreenColorPickerForm(snapshot, desktopBounds, monitors))
                {
                    EventHandler disposed = delegate { picker.CancelPick(); };
                    EventHandler visibility = delegate { if (!requester.Visible) picker.CancelPick(); };
                    requester.Disposed += disposed;
                    requester.VisibleChanged += visibility;
                    try
                    {
                        if (!MainForm.CanBeginColorSampling(owner) || requester.IsDisposed || !requester.Visible ||
                            !CurrentLayoutMatches(desktopBounds, monitors)) return null;
                        DialogResult result = picker.ShowDialog(owner);
                        if (result != DialogResult.OK || !picker.HasSample || requester.IsDisposed ||
                            requester.Disposing || owner.IsDisposed || owner.Disposing || !requester.Visible) return null;
                        return picker.SelectedColor;
                    }
                    finally
                    {
                        requester.Disposed -= disposed;
                        requester.VisibleChanged -= visibility;
                        // ShowDialog restores its owner. Do not activate a window
                        // that the user intentionally switched away from.
                        if (!picker.CancelledByDeactivation && !requester.IsDisposed && !owner.IsDisposed && owner.ContainsFocus)
                            requester.Focus();
                    }
                }
            }
            finally { Interlocked.Exchange(ref active, 0); }
        }

        internal static bool CurrentLayoutMatches(Rectangle desktopBounds, Rectangle[] monitors)
        {
            if (SystemInformation.VirtualScreen != desktopBounds) return false;
            Screen[] current = Screen.AllScreens;
            if (monitors == null || current.Length != monitors.Length) return false;
            foreach (Rectangle expected in monitors)
            {
                bool found = false;
                foreach (Screen screen in current) if (screen.Bounds == expected) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        internal static bool TrySample(Bitmap snapshot, Rectangle desktopBounds, Rectangle[] monitorBounds,
            Point screenPoint, out Color color)
        {
            color = Color.Empty;
            if (snapshot == null || desktopBounds.Width < 1 || desktopBounds.Height < 1 ||
                monitorBounds == null || !desktopBounds.Contains(screenPoint)) return false;
            bool onMonitor = false;
            foreach (Rectangle monitor in monitorBounds)
                if (monitor.Width > 0 && monitor.Height > 0 && monitor.Contains(screenPoint)) { onMonitor = true; break; }
            if (!onMonitor) return false;
            try
            {
                if (snapshot.Width != desktopBounds.Width || snapshot.Height != desktopBounds.Height) return false;
                Color sample = snapshot.GetPixel(screenPoint.X - desktopBounds.Left, screenPoint.Y - desktopBounds.Top);
                // Screen color is RGB; fill/outline opacity is a separate draft.
                color = Color.FromArgb(255, sample.R, sample.G, sample.B);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    internal sealed class ScreenColorPickerForm : Form
    {
        private readonly Bitmap snapshot;
        private readonly Rectangle desktopBounds;
        private readonly Rectangle[] monitors;
        private Font colorFont;
        private Font detailFont;
        private Point current;
        private Rectangle hudBounds;
        private Rectangle magnifierBounds;
        private readonly Rectangle[] hudTextBounds = new Rectangle[4];
        private float fontScale;
        private const TextFormatFlags HudTextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.PreserveGraphicsClipping;
        private bool shown, activated, finished, accepted;
        private float scale = 1f;
        private ScreenColorPickerCursor sampleCursor;

        internal Color SelectedColor { get; private set; }
        internal bool HasSample { get; private set; }
        internal bool CancelledByDeactivation { get; private set; }
        internal Rectangle HudBounds { get { return hudBounds; } }
        internal Rectangle HudMagnifierBounds { get { return magnifierBounds; } }
        internal bool HasHudMagnifier { get { return !magnifierBounds.IsEmpty; } }
        internal Font HudColorFont { get { return colorFont; } }
        internal Font HudDetailFont { get { return detailFont; } }
        internal Rectangle[] HudTextBounds { get { return (Rectangle[])hudTextBounds.Clone(); } }
        internal string[] HudTextLines
        {
            get
            {
                return new string[] { HasSample ? "#" + SelectedColor.R.ToString("X2", CultureInfo.InvariantCulture) +
                    SelectedColor.G.ToString("X2", CultureInfo.InvariantCulture) + SelectedColor.B.ToString("X2", CultureInfo.InvariantCulture) : "Move onto a monitor",
                    HasSample ? "RGB " + SelectedColor.R + ", " + SelectedColor.G + ", " + SelectedColor.B : "No screen pixel here",
                    "Click to choose", "Esc or right-click to cancel" };
            }
        }

        // The launcher owns the immutable image. Synthetic checks can construct
        // this form without reading the desktop or creating a native form HWND.
        internal ScreenColorPickerForm(Bitmap snapshot, Rectangle desktopBounds, Rectangle[] monitorBounds)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (desktopBounds.Width < 1 || desktopBounds.Height < 1 || snapshot.Width != desktopBounds.Width ||
                snapshot.Height != desktopBounds.Height) throw new ArgumentException("The desktop image dimensions do not match.", "desktopBounds");
            if (monitorBounds == null || monitorBounds.Length == 0) throw new ArgumentException("A connected monitor is required.", "monitorBounds");
            bool intersects = false;
            foreach (Rectangle monitor in monitorBounds) if (monitor.IntersectsWith(desktopBounds)) { intersects = true; break; }
            if (!intersects) throw new ArgumentException("The monitor layout does not intersect the desktop image.", "monitorBounds");
            this.snapshot = snapshot;
            this.desktopBounds = desktopBounds;
            monitors = (Rectangle[])monitorBounds.Clone();
            SetHudFonts(1f);
            Text = "Sample a screen color - Snip Studio";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = MaximumSize = Size.Empty;
            Bounds = desktopBounds;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SelectedColor = Color.Empty;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!ScreenColorPicker.CurrentLayoutMatches(desktopBounds, monitors))
                throw new ScreenColorPickerUnavailableException("The display layout changed. Try Eyedropper again.");
            // SetWindowPos preserves physical virtual-desktop coordinates,
            // including displays to the left or above the primary monitor.
            if (!CaptureNativeMethods.SetWindowPos(Handle, CaptureNativeMethods.HwndTopmost,
                desktopBounds.Left, desktopBounds.Top, desktopBounds.Width, desktopBounds.Height,
                CaptureNativeMethods.SwpShowWindow)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Bounds != desktopBounds || ClientSize != desktopBounds.Size)
                throw new ScreenColorPickerUnavailableException("The display layout changed. Try Eyedropper again.");
            SetDisplayScale(DeviceDpi / 96f);
            sampleCursor = ScreenColorPickerCursor.Create(DeviceDpi);
            Cursor = sampleCursor.Cursor;
            Activate(); Focus();
            shown = true;
            UpdateSample(Cursor.Position);
        }

        protected override void OnActivated(EventArgs e)
        {
            activated = true;
            base.OnActivated(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (shown && activated && !finished && !Disposing)
            {
                CancelledByDeactivation = true;
                CancelPick();
            }
        }

        internal bool UpdateSample(Point screenPoint)
        {
            if (finished) return false;
            Rectangle previousHud = hudBounds;
            current = screenPoint;
            Color color;
            HasSample = ScreenColorPicker.TrySample(snapshot, desktopBounds, monitors, screenPoint, out color);
            SelectedColor = HasSample ? color : Color.Empty;
            hudBounds = CalculateHudBounds(screenPoint);
            Invalidate(previousHud); Invalidate(hudBounds);
            return HasSample;
        }

        internal bool TryAccept(Point screenPoint)
        {
            if (finished || !UpdateSample(screenPoint)) return false;
            accepted = true;
            finished = true;
            DialogResult = DialogResult.OK;
            if (Visible && IsHandleCreated) Close();
            return true;
        }

        internal void CancelPick()
        {
            if (finished || IsDisposed || Disposing) return;
            finished = true;
            accepted = false;
            HasSample = false;
            SelectedColor = Color.Empty;
            DialogResult = DialogResult.Cancel;
            if (Visible && IsHandleCreated) Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateSample(new Point(e.X + desktopBounds.Left, e.Y + desktopBounds.Top));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) CancelPick();
            else if (e.Button == MouseButtons.Left) TryAccept(new Point(e.X + desktopBounds.Left, e.Y + desktopBounds.Top));
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { CancelPick(); return true; }
            if (keyData == Keys.Enter) { TryAccept(current); return true; }
            // While sampling, editor shortcuts must not start capture or change
            // the document that owns the draft palette.
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!accepted)
            {
                HasSample = false; SelectedColor = Color.Empty;
                DialogResult = DialogResult.Cancel;
            }
            finished = true;
            base.OnFormClosing(e);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            internal Point Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
        }

        protected override void WndProc(ref Message message)
        {
            // Never stretch a frozen desktop into a changed monitor layout.
            if ((message.Msg == 0x007E || message.Msg == 0x02E0) && shown && !finished)
            {
                CancelPick(); message.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref message);
            if (message.Msg == 0x0024 && message.LParam != IntPtr.Zero)
            {
                MinMaxInfo limits = (MinMaxInfo)Marshal.PtrToStructure(message.LParam, typeof(MinMaxInfo));
                limits.MaximumSize = new Point(desktopBounds.Width, desktopBounds.Height);
                limits.MaximumTrackSize = new Point(desktopBounds.Width, desktopBounds.Height);
                Marshal.StructureToPtr(limits, message.LParam, false);
            }
        }

        private Rectangle CalculateHudBounds(Point point)
        {
            Rectangle monitor = Rectangle.Intersect(monitors[0], desktopBounds);
            foreach (Rectangle candidate in monitors)
                if (candidate.Contains(point)) { monitor = Rectangle.Intersect(candidate, desktopBounds); break; }
            if (monitor.Width <= 0 || monitor.Height <= 0) return Rectangle.Empty;
            int margin = Math.Min(S(12), Math.Min(monitor.Width / 8, monitor.Height / 8));
            int maximumWidth = Math.Max(1, monitor.Width - margin * 2);
            int maximumHeight = Math.Max(1, monitor.Height - margin * 2);
            SetHudFonts(scale);
            int wantedTextWidth = Math.Max(MeasureHud("#FFFFFF", colorFont, Int32.MaxValue).Width,
                Math.Max(MeasureHud("RGB 255, 255, 255", detailFont, Int32.MaxValue).Width,
                MeasureHud("Esc or right-click to cancel", detailFont, Int32.MaxValue).Width));
            int width = Math.Min(maximumWidth, S(24 + 72 + 12) + wantedTextWidth);
            int height = 1;
            bool useMagnifier = true;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int inset = Math.Max(1, Math.Min(S(12), Math.Min(width / 16, maximumHeight / 16)));
                int gap = Math.Max(2, (int)Math.Round(4 * fontScale));
                int inner = Math.Max(1, width - inset * 2);
                int topTextWidth = Math.Max(MeasureHud("#FFFFFF", colorFont, Int32.MaxValue).Width,
                    MeasureHud("RGB 255, 255, 255", detailFont, Int32.MaxValue).Width);
                int side = useMagnifier ? Math.Min(S(72), inner - S(12) - topTextWidth) : 0;
                if (side < S(32)) side = 0;
                int textX = inset + (side > 0 ? side + S(12) : 0);
                int textWidth = Math.Max(1, width - inset - textX);
                string[] lines = HudTextLines;
                int hexHeight = MeasureHud(lines[0], colorFont, textWidth).Height + 2;
                int rgbHeight = Math.Max(MeasureHud("RGB 255, 255, 255", detailFont, textWidth).Height,
                    MeasureHud(lines[1], detailFont, textWidth).Height) + 2;
                int topHeight = Math.Max(side, hexHeight + gap + rgbHeight);
                int chooseHeight = MeasureHud(lines[2], detailFont, inner).Height + 2;
                int cancelHeight = MeasureHud(lines[3], detailFont, inner).Height + 2;
                int chooseTop = inset + topHeight + gap * 2;
                int cancelTop = chooseTop + chooseHeight + gap;
                height = cancelTop + cancelHeight + inset;
                magnifierBounds = side > 0 ? new Rectangle(inset, inset, side, side) : Rectangle.Empty;
                hudTextBounds[0] = new Rectangle(textX, inset, textWidth, hexHeight);
                hudTextBounds[1] = new Rectangle(textX, inset + hexHeight + gap, textWidth, rgbHeight);
                hudTextBounds[2] = new Rectangle(inset, chooseTop, inner, chooseHeight);
                hudTextBounds[3] = new Rectangle(inset, cancelTop, inner, cancelHeight);
                bool fitsWidth = MeasureHud(lines[0], colorFont, textWidth).Width <= textWidth &&
                    MeasureHud("RGB 255, 255, 255", detailFont, textWidth).Width <= textWidth;
                if (height <= maximumHeight && fitsWidth) break;
                if (side > 0) useMagnifier = false;
                else SetHudFonts(Math.Max(.5f, fontScale * .8f));
            }
            int left = point.X + S(24), top = point.Y + S(24);
            if (left + width > monitor.Right - margin) left = point.X - S(24) - width;
            if (top + height > monitor.Bottom - margin) top = point.Y - S(24) - height;
            left = Math.Max(monitor.Left + margin, Math.Min(left, monitor.Right - margin - width));
            top = Math.Max(monitor.Top + margin, Math.Min(top, monitor.Bottom - margin - height));
            Rectangle bounds = new Rectangle(left - desktopBounds.Left, top - desktopBounds.Top, width, height);
            if (!magnifierBounds.IsEmpty) magnifierBounds.Offset(bounds.Location);
            for (int index = 0; index < hudTextBounds.Length; index++) hudTextBounds[index].Offset(bounds.Location);
            return bounds;
        }

        private static Size MeasureHud(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), Int32.MaxValue), HudTextFlags);
        }

        private void SetHudFonts(float value)
        {
            if (colorFont != null && Math.Abs(fontScale - value) < .001f) return;
            if (colorFont != null) colorFont.Dispose();
            if (detailFont != null) detailFont.Dispose();
            fontScale = value;
            colorFont = new Font("Segoe UI", 14f * value, FontStyle.Bold, GraphicsUnit.Pixel);
            detailFont = new Font("Segoe UI", 12f * value, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        internal void SetDisplayScale(float value)
        {
            scale = Single.IsNaN(value) || Single.IsInfinity(value) ? 1f : Math.Max(.75f, Math.Min(4f, value));
            SetHudFonts(scale);
            UpdateSample(current);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Rectangle dirty = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, snapshot.Size));
            if (dirty.Width > 0 && dirty.Height > 0)
            {
                GraphicsState state = graphics.Save();
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(snapshot, dirty, dirty.X, dirty.Y, dirty.Width, dirty.Height, GraphicsUnit.Pixel);
                graphics.Restore(state);
            }
            if (hudBounds.IsEmpty || !hudBounds.IntersectsWith(e.ClipRectangle)) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF card = new RectangleF(hudBounds.X + 1, hudBounds.Y + 1, Math.Max(1, hudBounds.Width - 3), Math.Max(1, hudBounds.Height - 3));
            using (GraphicsPath path = StudioTheme.Round(card, S(8)))
            using (Brush fill = new SolidBrush(StudioTheme.Surface))
            using (Pen line = new Pen(StudioTheme.Accent, Math.Max(1, scale)))
            { graphics.FillPath(fill, path); graphics.DrawPath(line, path); }
            Rectangle magnifier = magnifierBounds;
            int side = magnifier.Width;
            if (!magnifier.IsEmpty)
                using (Brush fill = new SolidBrush(Color.FromArgb(30, 38, 48))) graphics.FillRectangle(fill, magnifier);
            if (HasSample && !magnifier.IsEmpty)
            {
                Point local = new Point(current.X - desktopBounds.Left, current.Y - desktopBounds.Top);
                Rectangle requested = new Rectangle(local.X - 4, local.Y - 4, 9, 9);
                Rectangle source = Rectangle.Intersect(requested, new Rectangle(Point.Empty, snapshot.Size));
                float cell = side / 9f;
                RectangleF destination = new RectangleF(magnifier.Left + (source.Left - requested.Left) * cell,
                    magnifier.Top + (source.Top - requested.Top) * cell, source.Width * cell, source.Height * cell);
                GraphicsState state = graphics.Save();
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(snapshot, destination, source, GraphicsUnit.Pixel);
                graphics.Restore(state);
                using (Pen light = new Pen(Color.White, 3 * scale))
                using (Pen dark = new Pen(Color.Black, Math.Max(1, scale)))
                {
                    RectangleF pixel = new RectangleF(magnifier.Left + cell * 4, magnifier.Top + cell * 4, cell, cell);
                    graphics.DrawRectangle(light, pixel.X, pixel.Y, pixel.Width, pixel.Height);
                    graphics.DrawRectangle(dark, pixel.X, pixel.Y, pixel.Width, pixel.Height);
                }
            }
            string[] text = HudTextLines;
            for (int index = 0; index < text.Length; index++)
                TextRenderer.DrawText(graphics, text[index], index == 0 ? colorFont : detailFont, hudTextBounds[index],
                    index == 0 ? StudioTheme.Text : StudioTheme.Muted, HudTextFlags);
        }

        private int S(int logical) { return Math.Max(1, (int)Math.Round(logical * scale)); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (sampleCursor != null) { Cursor = Cursors.Default; sampleCursor.Dispose(); sampleCursor = null; }
                colorFont.Dispose(); detailFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // The temporary sampler has its own eyedropper cursor. Existing tools keep
    // their normal pointer, and no generated icon files need to be replaced.
    internal sealed class ScreenColorPickerCursor : IDisposable
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
        internal Cursor Cursor { get; private set; }

        private ScreenColorPickerCursor(Cursor cursor, IntPtr handle) { Cursor = cursor; this.handle = handle; }

        internal static ScreenColorPickerCursor Create(int dpi)
        {
            float scale = Math.Max(1f, Math.Min(4f, dpi / 96f));
            int side = (int)Math.Ceiling(32 * scale);
            IntPtr icon = IntPtr.Zero;
            IconInfo info = new IconInfo();
            try
            {
                using (Bitmap image = new Bitmap(side, side, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(image))
                using (GraphicsPath path = new GraphicsPath())
                {
                    graphics.Clear(Color.Transparent); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.ScaleTransform(scale, scale);
                    path.AddPolygon(new PointF[] { new PointF(4, 27), new PointF(6, 22), new PointF(18, 10), new PointF(22, 14), new PointF(10, 26) });
                    path.StartFigure();
                    path.AddLines(new PointF[] { new PointF(16, 8), new PointF(20, 4), new PointF(28, 12), new PointF(24, 16), new PointF(16, 8) });
                    using (Pen halo = new Pen(Color.White, 4f))
                    using (Pen ink = new Pen(Color.FromArgb(18, 30, 46), 1.8f))
                    { halo.LineJoin = ink.LineJoin = LineJoin.Round; graphics.DrawPath(halo, path); graphics.DrawPath(ink, path); }
                    icon = image.GetHicon();
                }
                if (!GetIconInfo(icon, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                info.IsIcon = false; info.HotspotX = (uint)Math.Round(4 * scale); info.HotspotY = (uint)Math.Round(27 * scale);
                IntPtr native = CreateIconIndirect(ref info);
                if (native == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { return new ScreenColorPickerCursor(new Cursor(native), native); }
                catch { DestroyCursor(native); throw; }
            }
            catch (Exception) { return new ScreenColorPickerCursor(Cursors.Cross, IntPtr.Zero); }
            finally
            {
                if (icon != IntPtr.Zero) DestroyIcon(icon);
                if (info.MaskBitmap != IntPtr.Zero) DeleteObject(info.MaskBitmap);
                if (info.ColorBitmap != IntPtr.Zero) DeleteObject(info.ColorBitmap);
            }
        }

        public void Dispose()
        {
            IntPtr released = handle;
            handle = IntPtr.Zero;
            if (released == IntPtr.Zero) return;
            try { Cursor.Dispose(); }
            finally { DestroyCursor(released); }
        }
    }

    public sealed partial class MainForm
    {
        internal static bool CanBeginColorSampling(Form owner)
        {
            if (owner == null || owner.IsDisposed || owner.Disposing || !owner.Visible || !owner.Enabled || !owner.IsHandleCreated) return false;
            foreach (Form form in Application.OpenForms)
            {
                MainForm main = form as MainForm;
                if (main != null && !main.CanChangeAnnotationAppearance) return false;
            }
            return true;
        }
    }
}
