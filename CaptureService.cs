using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SnipStudio
{
    public enum CaptureMode
    {
        Rectangle,
        Freeform,
        Window,
        FullScreen
    }

    public static class CaptureService
    {
        public static bool UseHdrColorCorrection { get; set; }
        public static bool HideTaskbarsDuringCapture { get; set; }
        private static bool keepPrimaryTaskbarVisible = true;
        public static bool KeepPrimaryTaskbarVisible { get { return keepPrimaryTaskbarVisible; } set { keepPrimaryTaskbarVisible = value; } }
        // The caller hides its own window before calling, and owns the returned bitmap.
        // Selection is made against a frozen desktop so the overlay never appears in a snip.
        public static Bitmap Capture(CaptureMode mode)
        {
            if (!Enum.IsDefined(typeof(CaptureMode), mode))
                throw new ArgumentOutOfRangeException("mode");

            Rectangle desktopBounds = SystemInformation.VirtualScreen;
            if (desktopBounds.Width < 1 || desktopBounds.Height < 1)
                throw new InvalidOperationException("No desktop is available to capture.");

            List<CaptureWindow> windows;
            Bitmap desktop = null;
            try
            {
                using (TaskbarVisibilityScope taskbars = HideTaskbarsDuringCapture ? TaskbarVisibilityScope.Hide(KeepPrimaryTaskbarVisible) : null)
                {
                    windows = mode == CaptureMode.Window ? FindWindows(desktopBounds) : new List<CaptureWindow>();
                    desktop = CaptureDesktop(desktopBounds);
                    if (taskbars != null) taskbars.VerifyHidden();
                }
            }
            catch
            {
                if (desktop != null) desktop.Dispose();
                throw;
            }
            // The live taskbars are restored here. Selection uses the frozen
            // image, so canceling or spending time selecting cannot leave them hidden.
            if (mode == CaptureMode.FullScreen)
                return desktop;

            try
            {
                using (CaptureOverlay overlay = new CaptureOverlay(desktop, desktopBounds, mode, windows))
                {
                    if (overlay.ShowDialog() != DialogResult.OK)
                        return null;
                    return overlay.CopySelection();
                }
            }
            finally
            {
                desktop.Dispose();
            }
        }

        public static Rectangle NormalizeSelection(Point start, Point end, Rectangle limits)
        {
            if (limits.Width <= 0 || limits.Height <= 0)
                return Rectangle.Empty;

            int left = Math.Max(limits.Left, Math.Min(start.X, end.X));
            int top = Math.Max(limits.Top, Math.Min(start.Y, end.Y));
            int right = Math.Min(limits.Right, Math.Max(start.X, end.X));
            int bottom = Math.Min(limits.Bottom, Math.Max(start.Y, end.Y));
            if (right <= left || bottom <= top)
                return Rectangle.Empty;
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        public static Rectangle? SelectRecordingRegion()
        {
            return SelectRecordingRegion(HideTaskbarsDuringCapture, KeepPrimaryTaskbarVisible);
        }

        public static Rectangle? SelectRecordingRegion(bool hideTaskbars, bool keepPrimaryVisible)
        {
            Rectangle desktopBounds = SystemInformation.VirtualScreen;
            if (desktopBounds.Width < 1 || desktopBounds.Height < 1)
                throw new InvalidOperationException("No desktop is available to record.");
            Bitmap desktop = null;
            try
            {
                using (TaskbarVisibilityScope taskbars = hideTaskbars ? TaskbarVisibilityScope.Hide(keepPrimaryVisible) : null)
                {
                    desktop = CaptureDesktop(desktopBounds);
                    if (taskbars != null) taskbars.VerifyHidden();
                }
            }
            catch
            {
                if (desktop != null) desktop.Dispose();
                throw;
            }
            using (desktop)
            using (CaptureOverlay overlay = new CaptureOverlay(desktop, desktopBounds, CaptureMode.Rectangle, new List<CaptureWindow>()))
                return overlay.ShowDialog() == DialogResult.OK ? (Rectangle?)overlay.SelectedScreenBounds : null;
        }

        private static Bitmap CaptureDesktop(Rectangle bounds)
        {
            if (UseHdrColorCorrection) return HdrCaptureService.CaptureDesktop(bounds);
            Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Black);
                    IntPtr screenDc = CaptureNativeMethods.GetDC(IntPtr.Zero);
                    if (screenDc == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "The desktop could not be read.");
                    try
                    {
                        IntPtr targetDc = graphics.GetHdc();
                        try
                        {
                            if (!CaptureNativeMethods.BitBlt(targetDc, 0, 0, bounds.Width, bounds.Height,
                                screenDc, bounds.Left, bounds.Top,
                                CaptureNativeMethods.SourceCopy | CaptureNativeMethods.CaptureBlt))
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "The screenshot could not be captured.");
                        }
                        finally
                        {
                            graphics.ReleaseHdc(targetDc);
                        }
                    }
                    finally
                    {
                        CaptureNativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                    }
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static List<CaptureWindow> FindWindows(Rectangle desktopBounds)
        {
            List<CaptureWindow> windows = new List<CaptureWindow>();
            IntPtr shell = CaptureNativeMethods.GetShellWindow();
            CaptureNativeMethods.EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                if (handle == shell || !CaptureNativeMethods.IsWindowVisible(handle) || CaptureNativeMethods.IsIconic(handle))
                    return true;
                if ((CaptureNativeMethods.GetWindowLong(handle, CaptureNativeMethods.ExtendedStyle) &
                    CaptureNativeMethods.TransparentStyle) != 0)
                    return true;

                StringBuilder className = new StringBuilder(256);
                CaptureNativeMethods.GetClassName(handle, className, className.Capacity);
                if (className.ToString() == "Progman" || className.ToString() == "WorkerW")
                    return true;

                CaptureNativeMethods.NativeRect nativeBounds;
                bool haveDwmBounds = false;
                try
                {
                    int cloaked;
                    if (CaptureNativeMethods.GetDwmInt(handle, CaptureNativeMethods.DwmCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                        return true;
                    haveDwmBounds = CaptureNativeMethods.GetDwmRect(handle, CaptureNativeMethods.DwmExtendedFrameBounds,
                        out nativeBounds, Marshal.SizeOf(typeof(CaptureNativeMethods.NativeRect))) == 0;
                }
                catch (DllNotFoundException)
                {
                    nativeBounds = new CaptureNativeMethods.NativeRect();
                }
                catch (EntryPointNotFoundException)
                {
                    nativeBounds = new CaptureNativeMethods.NativeRect();
                }

                if (!haveDwmBounds && !CaptureNativeMethods.GetWindowRect(handle, out nativeBounds))
                    return true;
                Rectangle bounds = Rectangle.Intersect(nativeBounds.ToRectangle(), desktopBounds);
                if (bounds.Width < 8 || bounds.Height < 8)
                    return true;

                StringBuilder title = new StringBuilder(512);
                CaptureNativeMethods.GetWindowText(handle, title, title.Capacity);
                bounds.Offset(-desktopBounds.Left, -desktopBounds.Top);
                CaptureWindow window = new CaptureWindow();
                window.Bounds = bounds;
                window.Title = title.Length == 0 ? "Window" : title.ToString();
                windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows;
        }
    }

    internal sealed class CaptureWindow
    {
        internal Rectangle Bounds;
        internal string Title;
    }

    internal sealed class CaptureOverlay : Form
    {
        private readonly Bitmap desktop;
        private readonly Rectangle desktopBounds;
        private readonly CaptureMode mode;
        private readonly List<CaptureWindow> windows;
        private readonly List<Point> freeformPoints = new List<Point>();
        private readonly Font instructionFont = new Font("Segoe UI", 11f, FontStyle.Regular);
        private readonly Font detailFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        private readonly Color accent = Color.FromArgb(89, 184, 255);
        private Point start;
        private Point current;
        private bool dragging;
        private Rectangle selection = Rectangle.Empty;
        private GraphicsPath freeformPath;
        private CaptureWindow hoveredWindow;

        internal CaptureOverlay(Bitmap desktop, Rectangle desktopBounds, CaptureMode mode, List<CaptureWindow> windows)
        {
            this.desktop = desktop;
            this.desktopBounds = desktopBounds;
            this.mode = mode;
            this.windows = windows;
            Text = "Choose a snip - Snip Studio";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = desktopBounds;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = mode == CaptureMode.Window ? Cursors.Hand : Cursors.Cross;
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            current = ClampPoint(new Point(Cursor.Position.X - desktopBounds.Left, Cursor.Position.Y - desktopBounds.Top));
            if (mode == CaptureMode.Window)
                UpdateHoveredWindow(current);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Explicit physical bounds also retain monitors left or above the primary screen.
            CaptureNativeMethods.SetWindowPos(Handle, CaptureNativeMethods.HwndTopmost,
                desktopBounds.X, desktopBounds.Y, desktopBounds.Width, desktopBounds.Height,
                CaptureNativeMethods.SwpShowWindow);
            Activate();
            Focus();
        }

        internal Bitmap CopySelection()
        {
            Rectangle crop = Rectangle.Intersect(selection, new Rectangle(Point.Empty, desktop.Size));
            if (crop.Width < 1 || crop.Height < 1)
                return null;

            if (mode != CaptureMode.Freeform || freeformPath == null)
                return desktop.Clone(crop, PixelFormat.Format32bppRgb);

            Bitmap result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                using (GraphicsPath clip = (GraphicsPath)freeformPath.Clone())
                using (Matrix translation = new Matrix())
                {
                    graphics.Clear(Color.Transparent);
                    translation.Translate(-crop.X, -crop.Y);
                    clip.Transform(translation);
                    graphics.SetClip(clip);
                    graphics.DrawImage(desktop, new Rectangle(Point.Empty, crop.Size), crop, GraphicsUnit.Pixel);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        internal Rectangle SelectedScreenBounds
        {
            get
            {
                Rectangle result = Rectangle.Intersect(selection, new Rectangle(Point.Empty, desktop.Size));
                result.Offset(desktopBounds.Left, desktopBounds.Top);
                return result;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.DrawImageUnscaled(desktop, 0, 0);
            using (Region dimmed = new Region(ClientRectangle))
            using (Brush dimBrush = new SolidBrush(Color.FromArgb(112, 0, 0, 0)))
            {
                if (mode == CaptureMode.Freeform && freeformPath != null)
                    dimmed.Exclude(freeformPath);
                else if (!selection.IsEmpty)
                    dimmed.Exclude(selection);
                graphics.FillRegion(dimBrush, dimmed);
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(accent, 2f))
            {
                if (mode == CaptureMode.Freeform && freeformPath != null)
                    graphics.DrawPath(border, freeformPath);
                else if (!selection.IsEmpty)
                    graphics.DrawRectangle(border, selection.X, selection.Y,
                        Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));
                else if (mode == CaptureMode.Freeform && freeformPoints.Count > 1)
                    graphics.DrawLines(border, freeformPoints.ToArray());
            }

            DrawInstructions(graphics);
            if (!selection.IsEmpty)
                DrawDimensions(graphics);
            base.OnPaint(e);
        }

        private void DrawInstructions(Graphics graphics)
        {
            Point screenPoint = new Point(current.X + desktopBounds.Left, current.Y + desktopBounds.Top);
            Rectangle monitor = Screen.FromPoint(screenPoint).Bounds;
            monitor.Offset(-desktopBounds.Left, -desktopBounds.Top);
            string title = mode == CaptureMode.Window ? "Click a window to capture" :
                mode == CaptureMode.Freeform ? "Draw around the area to capture" : "Drag to select an area";
            string detail = "Release to capture    |    Esc or right-click to cancel";
            if (mode == CaptureMode.Window)
                detail = "Visible window area    |    Esc or right-click to cancel";

            Size titleSize = TextRenderer.MeasureText(graphics, title, instructionFont);
            Size detailSize = TextRenderer.MeasureText(graphics, detail, detailFont);
            int width = Math.Min(Math.Max(titleSize.Width, detailSize.Width) + 40, Math.Max(180, monitor.Width - 32));
            int height = titleSize.Height + detailSize.Height + 28;
            Rectangle box = new Rectangle(monitor.Left + (monitor.Width - width) / 2, monitor.Top + 22, width, height);
            DrawBox(graphics, box);
            TextRenderer.DrawText(graphics, title, instructionFont,
                new Rectangle(box.X + 16, box.Y + 10, box.Width - 32, titleSize.Height), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(graphics, detail, detailFont,
                new Rectangle(box.X + 12, box.Y + titleSize.Height + 13, box.Width - 24, detailSize.Height),
                Color.FromArgb(198, 209, 224), TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        private void DrawDimensions(Graphics graphics)
        {
            string text = selection.Width.ToString() + " x " + selection.Height.ToString() + " px";
            if (mode == CaptureMode.Window && hoveredWindow != null)
                text = hoveredWindow.Title + "  |  " + text;
            Size textSize = TextRenderer.MeasureText(graphics, text, detailFont);
            int width = Math.Min(textSize.Width + 20, Math.Max(80, Math.Min(500, ClientSize.Width - 16)));
            int height = textSize.Height + 12;
            int x = Math.Max(8, Math.Min(selection.Left, ClientSize.Width - width - 8));
            int y = selection.Bottom + 8;
            if (y + height > ClientSize.Height - 8)
                y = Math.Max(8, selection.Top - height - 8);
            Rectangle box = new Rectangle(x, y, width, height);
            DrawBox(graphics, box);
            TextRenderer.DrawText(graphics, text, detailFont, new Rectangle(x + 8, y + 5, width - 16, height - 10),
                Color.White, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private void DrawBox(Graphics graphics, Rectangle bounds)
        {
            using (Brush background = new SolidBrush(Color.FromArgb(245, 27, 32, 40)))
            using (Pen edge = new Pen(Color.FromArgb(88, 105, 127)))
            {
                graphics.FillRectangle(background, bounds);
                graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                CancelSelection();
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;

            current = ClampPoint(e.Location);
            if (mode == CaptureMode.Window)
            {
                UpdateHoveredWindow(current);
                return;
            }

            start = current;
            dragging = true;
            Capture = true;
            selection = Rectangle.Empty;
            freeformPoints.Clear();
            freeformPoints.Add(start);
            if (freeformPath != null)
            {
                freeformPath.Dispose();
                freeformPath = null;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            current = ClampPoint(e.Location);
            if (mode == CaptureMode.Window)
                UpdateHoveredWindow(current);
            else if (dragging)
            {
                if (mode == CaptureMode.Freeform)
                {
                    Point last = freeformPoints[freeformPoints.Count - 1];
                    if (Math.Abs(last.X - current.X) + Math.Abs(last.Y - current.Y) >= 2)
                        freeformPoints.Add(current);
                    UpdateFreeformPath();
                }
                else
                    selection = CaptureService.NormalizeSelection(start, current, ClientRectangle);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
                return;

            current = ClampPoint(e.Location);
            if (mode == CaptureMode.Window)
            {
                UpdateHoveredWindow(current);
                if (!selection.IsEmpty)
                    CompleteSelection();
                return;
            }
            if (!dragging)
                return;

            dragging = false;
            Capture = false;
            if (mode == CaptureMode.Freeform)
            {
                freeformPoints.Add(current);
                UpdateFreeformPath();
                if (freeformPath == null || !HasPolygonArea())
                {
                    ResetSelection();
                    return;
                }
            }
            else
                selection = CaptureService.NormalizeSelection(start, current, ClientRectangle);

            if (selection.Width >= 2 && selection.Height >= 2)
                CompleteSelection();
            else
                ResetSelection();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture)
            {
                dragging = false;
                ResetSelection();
            }
        }

        private void UpdateHoveredWindow(Point point)
        {
            hoveredWindow = null;
            foreach (CaptureWindow window in windows)
            {
                if (window.Bounds.Contains(point))
                {
                    hoveredWindow = window;
                    break;
                }
            }
            selection = hoveredWindow == null ? Rectangle.Empty : hoveredWindow.Bounds;
        }

        private void UpdateFreeformPath()
        {
            if (freeformPath != null)
            {
                freeformPath.Dispose();
                freeformPath = null;
            }
            if (freeformPoints.Count < 3)
            {
                selection = Rectangle.Empty;
                return;
            }
            freeformPath = new GraphicsPath(FillMode.Alternate);
            freeformPath.AddPolygon(freeformPoints.ToArray());
            RectangleF bounds = freeformPath.GetBounds();
            selection = Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom)), ClientRectangle);
        }

        private bool HasPolygonArea()
        {
            // Inspect the filled region, rather than signed polygon area: a self-crossing
            // outline can enclose real pixels even when its signed areas cancel out.
            if (freeformPath == null)
                return false;
            using (Region filled = new Region(freeformPath))
            using (Matrix identity = new Matrix())
            {
                double area = 0;
                foreach (RectangleF scan in filled.GetRegionScans(identity))
                {
                    area += (double)scan.Width * scan.Height;
                    if (area >= 4)
                        return true;
                }
            }
            return false;
        }

        private Point ClampPoint(Point point)
        {
            return new Point(Math.Max(0, Math.Min(desktop.Width, point.X)),
                Math.Max(0, Math.Min(desktop.Height, point.Y)));
        }

        private void ResetSelection()
        {
            selection = Rectangle.Empty;
            freeformPoints.Clear();
            if (freeformPath != null)
            {
                freeformPath.Dispose();
                freeformPath = null;
            }
            Invalidate();
        }

        private void CompleteSelection()
        {
            dragging = false;
            Capture = false;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CancelSelection()
        {
            dragging = false;
            Capture = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape)
            {
                CancelSelection();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (freeformPath != null)
                    freeformPath.Dispose();
                instructionFont.Dispose();
                detailFont.Dispose();
            }
            // The service owns the frozen desktop and disposes it after this form.
            base.Dispose(disposing);
        }
    }
}
