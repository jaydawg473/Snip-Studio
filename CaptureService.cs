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
        FullScreen,
        // UI index 4 is reserved for the scrolling-capture workflow.
        AllMonitors = 5
    }

    internal enum CaptureOverlayPurpose { Screenshot, Recording, Scrolling }

    public static class CaptureService
    {
        public static bool UseHdrColorCorrection { get; set; }
        public static bool HideTaskbarsDuringCapture { get; set; }
        private static bool keepPrimaryTaskbarVisible = true;
        public static bool KeepPrimaryTaskbarVisible { get { return keepPrimaryTaskbarVisible; } set { keepPrimaryTaskbarVisible = value; } }

        // Scrolling capture owns taskbar/window visibility for its whole session.
        // Regions use physical desktop coordinates, including negative monitors.
        internal static Bitmap CaptureRegion(Rectangle bounds)
        {
            Rectangle desktop = SystemInformation.VirtualScreen;
            if (bounds.Width < 1 || bounds.Height < 1 || !desktop.Contains(bounds))
                throw new ArgumentOutOfRangeException("bounds", "Keep the capture region inside the connected desktop.");
            return CaptureDesktop(bounds);
        }
        // The caller hides its own window before calling, and owns the returned bitmap.
        // Selection is made against a frozen desktop so the overlay never appears in a snip.
        public static Bitmap Capture(CaptureMode mode)
        {
            string warning;
            return CaptureWithWarnings(mode, out warning);
        }

        internal static Bitmap CaptureWithWarnings(CaptureMode mode, out string warning)
        {
            warning = null;
            if (!Enum.IsDefined(typeof(CaptureMode), mode))
                throw new ArgumentOutOfRangeException("mode");

            Rectangle desktopBounds = SystemInformation.VirtualScreen;
            if (desktopBounds.Width < 1 || desktopBounds.Height < 1)
                throw new InvalidOperationException("No desktop is available to capture.");

            List<CaptureWindow> windows;
            Bitmap desktop = null;
            try
            {
                using (TaskbarVisibilityScope taskbars = HideTaskbarsDuringCapture ? TaskbarVisibilityScope.HideBestEffort(KeepPrimaryTaskbarVisible) : null)
                {
                    windows = mode == CaptureMode.Window ? FindWindows(desktopBounds) :
                        mode == CaptureMode.FullScreen ? FindMonitors(desktopBounds) : new List<CaptureWindow>();
                    desktop = CaptureDesktop(desktopBounds);
                    if (taskbars != null)
                    {
                        taskbars.VerifyHidden();
                        warning = taskbars.Warning;
                        string restoreWarning = taskbars.RestoreAfterCapture();
                        if (!String.IsNullOrEmpty(restoreWarning))
                            warning = String.IsNullOrEmpty(warning) ? restoreWarning : warning + " " + restoreWarning;
                    }
                }
            }
            catch
            {
                if (desktop != null) desktop.Dispose();
                throw;
            }
            // Taskbar restoration was attempted before selection, which uses
            // the frozen image and needs no ongoing desktop visibility changes.
            if (mode == CaptureMode.AllMonitors)
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

        private static List<CaptureWindow> FindMonitors(Rectangle desktopBounds)
        {
            Screen[] screens = Screen.AllScreens;
            Rectangle[] bounds = new Rectangle[screens.Length];
            int primaryIndex = -1;
            for (int index = 0; index < screens.Length; index++)
            {
                bounds[index] = screens[index].Bounds;
                if (screens[index].Primary) primaryIndex = index;
            }
            return CreateMonitorTargets(desktopBounds, bounds, primaryIndex);
        }

        // Coordinates stay in the frozen desktop's pixel space, including
        // displays left/above the primary monitor and gaps between displays.
        internal static List<CaptureWindow> CreateMonitorTargets(Rectangle desktopBounds, Rectangle[] monitorBounds, int primaryIndex)
        {
            if (monitorBounds == null) throw new ArgumentNullException("monitorBounds");
            List<CaptureWindow> monitors = new List<CaptureWindow>();
            for (int index = 0; index < monitorBounds.Length; index++)
            {
                Rectangle bounds = Rectangle.Intersect(monitorBounds[index], desktopBounds);
                if (bounds.Width < 1 || bounds.Height < 1) continue;
                bounds.Offset(-desktopBounds.Left, -desktopBounds.Top);
                CaptureWindow target = new CaptureWindow();
                target.Bounds = bounds;
                target.Title = index == primaryIndex ? "Primary monitor" : "Monitor " + (index + 1).ToString();
                monitors.Add(target);
            }
            return monitors;
        }

        public static Rectangle? SelectRecordingRegion()
        {
            return SelectRecordingRegion(HideTaskbarsDuringCapture, KeepPrimaryTaskbarVisible);
        }

        public static Rectangle? SelectRecordingRegion(bool hideTaskbars, bool keepPrimaryVisible)
        {
            return SelectRecordingRegion(CaptureMode.Rectangle, hideTaskbars, keepPrimaryVisible);
        }

        internal static CaptureMode RecordingModeFromIndex(int index)
        {
            if (index == 0) return CaptureMode.Rectangle;
            if (index == 1) return CaptureMode.Window;
            throw new ArgumentOutOfRangeException("index", "Choose Draw area or Window for recording.");
        }

        public static Rectangle? SelectRecordingRegion(CaptureMode selectionMode, bool hideTaskbars, bool keepPrimaryVisible)
        {
            // Video uses rectangular pixels, so photo-only freeform, scrolling,
            // and monitor modes must not silently change the recording selection.
            if (selectionMode != CaptureMode.Rectangle && selectionMode != CaptureMode.Window)
                throw new ArgumentOutOfRangeException("selectionMode", "Choose Draw area or Window for recording.");
            return SelectDesktopRegion(selectionMode, hideTaskbars, keepPrimaryVisible, CaptureOverlayPurpose.Recording);
        }

        internal static Rectangle? AdjustRecordingRegion(Rectangle screenRegion)
        {
            return SelectDesktopRegion(CaptureMode.Rectangle, false, true, CaptureOverlayPurpose.Recording, screenRegion);
        }

        internal static Rectangle? SelectScrollingRegion(bool hideTaskbars, bool keepPrimaryVisible)
        {
            return SelectDesktopRegion(CaptureMode.Rectangle, hideTaskbars, keepPrimaryVisible, CaptureOverlayPurpose.Scrolling);
        }

        private static Rectangle? SelectDesktopRegion(CaptureMode selectionMode, bool hideTaskbars, bool keepPrimaryVisible,
            CaptureOverlayPurpose purpose, Rectangle? existingRecordingRegion = null)
        {
            Rectangle desktopBounds = SystemInformation.VirtualScreen;
            if (desktopBounds.Width < 1 || desktopBounds.Height < 1)
                throw new InvalidOperationException(purpose == CaptureOverlayPurpose.Recording ?
                    "No desktop is available to record." : "No desktop is available to capture.");
            if (existingRecordingRegion.HasValue)
            {
                Rectangle area = Rectangle.Intersect(existingRecordingRegion.Value, desktopBounds);
                if (area.Width < 2 || area.Height < 2)
                    throw new InvalidOperationException("The previous recording area is no longer available. Choose a new area in Recording options.");
                area.Offset(-desktopBounds.Left, -desktopBounds.Top);
                existingRecordingRegion = area;
            }
            Bitmap desktop = null;
            List<CaptureWindow> windows = new List<CaptureWindow>();
            try
            {
                using (TaskbarVisibilityScope taskbars = hideTaskbars ? TaskbarVisibilityScope.Hide(keepPrimaryVisible) : null)
                {
                    if (selectionMode == CaptureMode.Window) windows = FindWindows(desktopBounds);
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
            using (CaptureOverlay overlay = new CaptureOverlay(desktop, desktopBounds, selectionMode, windows, purpose, existingRecordingRegion))
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
        private readonly string instructionTitle;
        private readonly string instructionDetail;
        private Size instructionTitleSize, instructionDetailSize;
        private Rectangle instructionMonitor, instructionBox;
        private float instructionDpiX, instructionDpiY;
        private string dimensionText = "";
        private Size dimensionTextSize;
        private Point start;
        private Point current;
        private bool dragging;
        private Rectangle selection = Rectangle.Empty;
        private GraphicsPath freeformPath;
        private CaptureWindow hoveredWindow;
        private readonly Button useRecordingArea;
        private Rectangle adjustmentOrigin;
        private AdjustmentEdge adjustmentEdge;
        private bool SelectsWholeTarget { get { return mode == CaptureMode.Window || mode == CaptureMode.FullScreen; } }

        [Flags]
        private enum AdjustmentEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

        internal CaptureOverlay(Bitmap desktop, Rectangle desktopBounds, CaptureMode mode, List<CaptureWindow> windows)
            : this(desktop, desktopBounds, mode, windows, false)
        {
        }

        internal CaptureOverlay(Bitmap desktop, Rectangle desktopBounds, CaptureMode mode, List<CaptureWindow> windows, bool forRecording)
            : this(desktop, desktopBounds, mode, windows,
                forRecording ? CaptureOverlayPurpose.Recording : CaptureOverlayPurpose.Screenshot)
        {
        }

        internal CaptureOverlay(Bitmap desktop, Rectangle desktopBounds, CaptureMode mode, List<CaptureWindow> windows,
            CaptureOverlayPurpose purpose, Rectangle? existingRecordingRegion = null)
        {
            this.desktop = desktop;
            this.desktopBounds = desktopBounds;
            this.mode = mode;
            this.windows = windows;
            instructionTitle = mode == CaptureMode.FullScreen ? "Click the monitor to capture" :
                mode == CaptureMode.Window ? "Click a window to capture" :
                mode == CaptureMode.Freeform ? "Draw around the area to capture" : "Drag to select an area";
            instructionDetail = mode == CaptureMode.FullScreen ? "One complete monitor    |    Esc or right-click to cancel" :
                mode == CaptureMode.Window ? "Visible window area    |    Esc or right-click to cancel" :
                "Release to capture    |    Esc or right-click to cancel";
            if (purpose == CaptureOverlayPurpose.Recording)
            {
                instructionTitle = mode == CaptureMode.Window ? "Click a window to record" : "Drag to select a recording area";
                instructionDetail = mode == CaptureMode.Window ? "Visible window area    |    Esc or right-click to cancel" :
                    "Release to choose the recording area    |    Esc or right-click to cancel";
            }
            else if (purpose == CaptureOverlayPurpose.Scrolling)
            {
                instructionTitle = "Select scrolling content";
                instructionDetail = "Drag around the content to scroll    |    Esc or right-click to cancel";
            }
            if (existingRecordingRegion.HasValue)
            {
                selection = existingRecordingRegion.Value;
                instructionTitle = "Adjust the recording area";
                instructionDetail = "Drag inside to move, or drag the edges to resize. Enter to confirm · Esc to cancel.";
                useRecordingArea = new Button
                {
                    Text = "Use area",
                    AccessibleName = "Use recording area",
                    Font = instructionFont,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(0, 88, 167),
                    ForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White,
                    UseVisualStyleBackColor = false
                };
                useRecordingArea.Click += delegate { if (!dragging) CompleteSelection(); };
                Controls.Add(useRecordingArea);
            }
            Text = purpose == CaptureOverlayPurpose.Recording ? "Choose a recording area - Snip Studio" :
                purpose == CaptureOverlayPurpose.Scrolling ? "Choose scrolling content - Snip Studio" : "Choose a snip - Snip Studio";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = desktopBounds;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = useRecordingArea != null ? Cursors.Default : SelectsWholeTarget ? Cursors.Hand : Cursors.Cross;
            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            current = ClampPoint(new Point(Cursor.Position.X - desktopBounds.Left, Cursor.Position.Y - desktopBounds.Top));
            instructionTitleSize = TextRenderer.MeasureText(instructionTitle, instructionFont);
            instructionDetailSize = TextRenderer.MeasureText(instructionDetail, detailFont);
            UpdateInstructionLocation();
            if (SelectsWholeTarget)
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
            // Mouse movement invalidates only changed strips/HUDs. Keep the
            // system's dirty clip and copy only its bounded desktop rectangle.
            Rectangle dirty = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, desktop.Size));
            if (dirty.Width <= 0 || dirty.Height <= 0) return;
            GraphicsState adjustmentState = null;
            if (useRecordingArea != null)
            {
                adjustmentState = graphics.Save();
                graphics.SetClip(dirty, CombineMode.Intersect);
            }
            PaintFrozenDesktop(graphics, desktop, dirty, selection, freeformPath);

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
            if (useRecordingArea != null) DrawAdjustmentHandles(graphics);

            DrawInstructions(graphics);
            if (!selection.IsEmpty)
                DrawDimensions(graphics);
            if (adjustmentState != null) graphics.Restore(adjustmentState);
            base.OnPaint(e);
        }

        internal static void PaintFrozenDesktop(Graphics graphics, Bitmap desktop, Rectangle dirty,
            Rectangle selection, GraphicsPath freeformPath)
        {
            GraphicsState desktopState = graphics.Save();
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(desktop, dirty, dirty.X, dirty.Y, dirty.Width, dirty.Height, GraphicsUnit.Pixel);
            graphics.Restore(desktopState);
            using (Region dimmed = new Region(dirty))
            using (Brush dimBrush = new SolidBrush(Color.FromArgb(112, 0, 0, 0)))
            {
                if (freeformPath != null)
                    dimmed.Exclude(freeformPath);
                else if (!selection.IsEmpty)
                    dimmed.Exclude(selection);
                graphics.FillRegion(dimBrush, dimmed);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // OnPaint replaces every dirty pixel with the frozen desktop.
        }

        private void DrawInstructions(Graphics graphics)
        {
            if (instructionDpiX != graphics.DpiX || instructionDpiY != graphics.DpiY)
            {
                Rectangle previous = instructionBox;
                instructionTitleSize = TextRenderer.MeasureText(graphics, instructionTitle, instructionFont);
                instructionDetailSize = TextRenderer.MeasureText(graphics, instructionDetail, detailFont);
                instructionDpiX = graphics.DpiX; instructionDpiY = graphics.DpiY;
                SetInstructionBox();
                if (previous != instructionBox) { InvalidateHud(previous); InvalidateHud(instructionBox); }
            }
            Rectangle box = instructionBox;
            if (!graphics.IsVisible(box)) return;
            DrawBox(graphics, box);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | HudClipping;
            TextRenderer.DrawText(graphics, instructionTitle, instructionFont,
                new Rectangle(box.X + 16, box.Y + 10, box.Width - 32, instructionTitleSize.Height), Color.White,
                flags | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(graphics, instructionDetail, detailFont,
                new Rectangle(box.X + 12, box.Y + instructionTitleSize.Height + 13, box.Width - 24, instructionDetailSize.Height),
                Color.FromArgb(198, 209, 224), flags);
        }

        private TextFormatFlags HudClipping { get { return useRecordingArea == null ? 0 : TextFormatFlags.PreserveGraphicsClipping; } }

        private void UpdateInstructionLocation()
        {
            if (instructionMonitor.Width > 0 && instructionMonitor.Contains(current)) return;
            Point screenPoint = new Point(current.X + desktopBounds.Left, current.Y + desktopBounds.Top);
            instructionMonitor = Screen.FromPoint(screenPoint).Bounds;
            instructionMonitor.Offset(-desktopBounds.Left, -desktopBounds.Top);
            SetInstructionBox();
        }

        private void SetInstructionBox()
        {
            int width = Math.Min(Math.Max(instructionTitleSize.Width, instructionDetailSize.Width) + 40, Math.Max(180, instructionMonitor.Width - 32));
            int height = instructionTitleSize.Height + instructionDetailSize.Height + 28;
            instructionBox = new Rectangle(instructionMonitor.Left + (instructionMonitor.Width - width) / 2, instructionMonitor.Top + 22, width, height);
            if (useRecordingArea == null) return;
            double scale = Math.Max(96, DeviceDpi) / 96d;
            Size preferred = useRecordingArea.GetPreferredSize(Size.Empty);
            int buttonWidth = Math.Max(preferred.Width + 12, (int)Math.Ceiling(112 * scale));
            int buttonHeight = Math.Max(preferred.Height, (int)Math.Ceiling(34 * scale));
            Rectangle next = new Rectangle(instructionBox.Left + (width - buttonWidth) / 2,
                instructionBox.Bottom + 6, buttonWidth, buttonHeight);
            Rectangle previous = useRecordingArea.Bounds;
            if (previous == next) return;
            useRecordingArea.Bounds = next;
            InvalidateHud(previous);
            InvalidateHud(next);
            useRecordingArea.Invalidate();
        }

        private void DrawDimensions(Graphics graphics)
        {
            Rectangle box = GetDimensionBox();
            if (!graphics.IsVisible(box)) return;
            DrawBox(graphics, box);
            TextRenderer.DrawText(graphics, dimensionText, detailFont, new Rectangle(box.X + 8, box.Y + 5, box.Width - 16, box.Height - 10),
                Color.White, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | HudClipping);
        }

        private static IEnumerable<Rectangle> AdjustmentHandles(Rectangle area)
        {
            int middleX = area.Left + area.Width / 2, middleY = area.Top + area.Height / 2;
            yield return AdjustmentHandle(area.Left, area.Top);
            yield return AdjustmentHandle(middleX, area.Top);
            yield return AdjustmentHandle(area.Right, area.Top);
            yield return AdjustmentHandle(area.Left, middleY);
            yield return AdjustmentHandle(area.Right, middleY);
            yield return AdjustmentHandle(area.Left, area.Bottom);
            yield return AdjustmentHandle(middleX, area.Bottom);
            yield return AdjustmentHandle(area.Right, area.Bottom);
        }

        private static Rectangle AdjustmentHandle(int x, int y) { return new Rectangle(x - 4, y - 4, 9, 9); }

        private void DrawAdjustmentHandles(Graphics graphics)
        {
            using (Brush fill = new SolidBrush(accent))
            using (Pen border = new Pen(Color.FromArgb(24, 32, 44)))
            {
                foreach (Rectangle handle in AdjustmentHandles(selection))
                {
                    graphics.FillRectangle(fill, handle);
                    graphics.DrawRectangle(border, handle.X, handle.Y, handle.Width - 1, handle.Height - 1);
                }
            }
        }

        private AdjustmentEdge GetAdjustmentEdge(Point point)
        {
            const int tolerance = 9;
            if (!Rectangle.Inflate(selection, tolerance, tolerance).Contains(point)) return AdjustmentEdge.None;
            int left = Math.Abs(point.X - selection.Left), right = Math.Abs(point.X - selection.Right);
            int top = Math.Abs(point.Y - selection.Top), bottom = Math.Abs(point.Y - selection.Bottom);
            AdjustmentEdge edges = AdjustmentEdge.None;
            if (Math.Min(left, right) <= tolerance) edges |= left <= right ? AdjustmentEdge.Left : AdjustmentEdge.Right;
            if (Math.Min(top, bottom) <= tolerance) edges |= top <= bottom ? AdjustmentEdge.Top : AdjustmentEdge.Bottom;
            return edges == AdjustmentEdge.None ? AdjustmentEdge.Move : edges;
        }

        private static Cursor AdjustmentCursor(AdjustmentEdge edge)
        {
            switch (edge)
            {
                case AdjustmentEdge.Left | AdjustmentEdge.Top:
                case AdjustmentEdge.Right | AdjustmentEdge.Bottom: return Cursors.SizeNWSE;
                case AdjustmentEdge.Right | AdjustmentEdge.Top:
                case AdjustmentEdge.Left | AdjustmentEdge.Bottom: return Cursors.SizeNESW;
                case AdjustmentEdge.Left:
                case AdjustmentEdge.Right: return Cursors.SizeWE;
                case AdjustmentEdge.Top:
                case AdjustmentEdge.Bottom: return Cursors.SizeNS;
                case AdjustmentEdge.Move: return Cursors.SizeAll;
                default: return Cursors.Default;
            }
        }

        private Rectangle MoveRecordingArea(Rectangle area, int dx, int dy)
        {
            return new Rectangle(ClampRecordingCoordinate(area.Left + dx, 0, desktop.Width - area.Width),
                ClampRecordingCoordinate(area.Top + dy, 0, desktop.Height - area.Height), area.Width, area.Height);
        }

        private static int ClampRecordingCoordinate(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }

        private void ApplyRecordingAdjustment()
        {
            int dx = current.X - start.X, dy = current.Y - start.Y;
            if (adjustmentEdge == AdjustmentEdge.Move)
            {
                selection = MoveRecordingArea(adjustmentOrigin, dx, dy);
                return;
            }

            int left = adjustmentOrigin.Left, top = adjustmentOrigin.Top, right = adjustmentOrigin.Right, bottom = adjustmentOrigin.Bottom;
            if ((adjustmentEdge & AdjustmentEdge.Left) != 0) left = ClampRecordingCoordinate(left + dx, 0, right - 2);
            if ((adjustmentEdge & AdjustmentEdge.Top) != 0) top = ClampRecordingCoordinate(top + dy, 0, bottom - 2);
            if ((adjustmentEdge & AdjustmentEdge.Right) != 0) right = ClampRecordingCoordinate(right + dx, left + 2, desktop.Width);
            if ((adjustmentEdge & AdjustmentEdge.Bottom) != 0) bottom = ClampRecordingCoordinate(bottom + dy, top + 2, desktop.Height);
            selection = Rectangle.FromLTRB(left, top, right, bottom);
        }

        private Rectangle GetDimensionBox()
        {
            if (selection.Width <= 0 || selection.Height <= 0) return Rectangle.Empty;
            string text = selection.Width.ToString() + " x " + selection.Height.ToString() + " px";
            if (SelectsWholeTarget && hoveredWindow != null)
                text = hoveredWindow.Title + "  |  " + text;
            if (!String.Equals(text, dimensionText, StringComparison.Ordinal))
            {
                dimensionText = text;
                dimensionTextSize = TextRenderer.MeasureText(text, detailFont);
            }
            int width = Math.Min(dimensionTextSize.Width + 20, Math.Max(80, Math.Min(500, ClientSize.Width - 16)));
            int height = dimensionTextSize.Height + 12;
            int x = Math.Max(8, Math.Min(selection.Left, ClientSize.Width - width - 8));
            int y = selection.Bottom + 8;
            if (y + height > ClientSize.Height - 8)
                y = Math.Max(8, selection.Top - height - 8);
            return new Rectangle(x, y, width, height);
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

            Rectangle previousSelection = selection;
            Rectangle previousFreeform = FreeformVisualBounds();
            Rectangle previousDimensions = GetDimensionBox();
            Rectangle previousInstructions = instructionBox;
            CaptureWindow previousWindow = hoveredWindow;
            current = ClampPoint(e.Location);
            UpdateInstructionLocation();
            if (useRecordingArea != null)
            {
                adjustmentEdge = GetAdjustmentEdge(current);
                if (adjustmentEdge == AdjustmentEdge.None) return;
                adjustmentOrigin = selection;
                start = current;
                dragging = true;
                Capture = true;
                return;
            }
            if (SelectsWholeTarget)
            {
                UpdateHoveredWindow(current);
                InvalidateOverlayChange(previousSelection, previousFreeform, previousDimensions, previousInstructions,
                    false, !Object.ReferenceEquals(previousWindow, hoveredWindow));
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
            InvalidateOverlayChange(previousSelection, previousFreeform, previousDimensions, previousInstructions,
                mode == CaptureMode.Freeform, false);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point next = ClampPoint(e.Location);
            if (next == current) return;
            Rectangle previousSelection = selection;
            Rectangle previousFreeform = FreeformVisualBounds();
            Rectangle previousDimensions = GetDimensionBox();
            Rectangle previousInstructions = instructionBox;
            CaptureWindow previousWindow = hoveredWindow;
            current = next;
            UpdateInstructionLocation();
            bool freeformChanged = false;
            if (useRecordingArea != null)
            {
                if (dragging) ApplyRecordingAdjustment();
                Cursor = AdjustmentCursor(dragging ? adjustmentEdge : GetAdjustmentEdge(current));
            }
            else if (SelectsWholeTarget)
                UpdateHoveredWindow(current);
            else if (dragging)
            {
                if (mode == CaptureMode.Freeform)
                {
                    Point last = freeformPoints[freeformPoints.Count - 1];
                    if (Math.Abs(last.X - current.X) + Math.Abs(last.Y - current.Y) >= 2)
                    {
                        freeformPoints.Add(current);
                        UpdateFreeformPath();
                        freeformChanged = true;
                    }
                }
                else
                    selection = CaptureService.NormalizeSelection(start, current, ClientRectangle);
            }
            InvalidateOverlayChange(previousSelection, previousFreeform, previousDimensions, previousInstructions,
                freeformChanged, !Object.ReferenceEquals(previousWindow, hoveredWindow));
        }

        private void InvalidateOverlayChange(Rectangle previousSelection, Rectangle previousFreeform,
            Rectangle previousDimensions, Rectangle previousInstructions, bool freeformChanged, bool windowChanged)
        {
            bool selectionChanged = previousSelection != selection;
            bool instructionsChanged = previousInstructions != instructionBox;
            if (!selectionChanged && !freeformChanged && !windowChanged && !instructionsChanged) return;

            using (Region dirty = mode == CaptureMode.Freeform
                ? new Region(Rectangle.Empty) : CreateSelectionRepaintRegion(previousSelection, selection))
            {
                if (useRecordingArea != null && selectionChanged)
                {
                    // The grips extend past the thin outline's normal dirty region.
                    foreach (Rectangle handle in AdjustmentHandles(previousSelection)) dirty.Union(handle);
                    foreach (Rectangle handle in AdjustmentHandles(selection)) dirty.Union(handle);
                }
                if (mode == CaptureMode.Freeform && freeformChanged)
                {
                    if (HasArea(previousFreeform)) dirty.Union(previousFreeform);
                    Rectangle nextFreeform = FreeformVisualBounds();
                    if (HasArea(nextFreeform)) dirty.Union(nextFreeform);
                }
                if (selectionChanged || freeformChanged || windowChanged)
                {
                    AddHud(dirty, previousDimensions);
                    AddHud(dirty, GetDimensionBox());
                }
                if (instructionsChanged)
                {
                    AddHud(dirty, previousInstructions);
                    AddHud(dirty, instructionBox);
                }
                dirty.Intersect(ClientRectangle);
                Invalidate(dirty, false);
            }
        }

        // Pure geometry: resizing a large rectangle changes its interior only
        // in the symmetric-difference strips. The shared bright interior is
        // left alone; both old/new anti-aliased border rings are refreshed.
        internal static Region CreateSelectionRepaintRegion(Rectangle previous, Rectangle next)
        {
            Region dirty = new Region(Rectangle.Empty);
            if (HasArea(previous)) dirty.Union(previous);
            if (HasArea(next)) dirty.Xor(next);
            AddSelectionEdge(dirty, previous);
            AddSelectionEdge(dirty, next);
            return dirty;
        }

        private static bool HasArea(Rectangle rectangle) { return rectangle.Width > 0 && rectangle.Height > 0; }

        private static void AddSelectionEdge(Region target, Rectangle rectangle)
        {
            if (!HasArea(rectangle)) return;
            Rectangle outside = rectangle; outside.Inflate(3, 3);
            Rectangle inside = rectangle; inside.Inflate(-3, -3);
            using (Region edge = new Region(outside))
            {
                if (HasArea(inside)) edge.Exclude(inside);
                target.Union(edge);
            }
        }

        private static void AddHud(Region target, Rectangle rectangle)
        {
            if (!HasArea(rectangle)) return;
            rectangle.Inflate(2, 2);
            target.Union(rectangle);
        }

        private void InvalidateHud(Rectangle rectangle)
        {
            if (!HasArea(rectangle)) return;
            rectangle.Inflate(2, 2);
            Invalidate(Rectangle.Intersect(rectangle, ClientRectangle), false);
        }

        private Rectangle FreeformVisualBounds()
        {
            if (mode != CaptureMode.Freeform || freeformPoints.Count < 2) return Rectangle.Empty;
            int left = freeformPoints[0].X, top = freeformPoints[0].Y, right = left, bottom = top;
            if (freeformPath != null)
            {
                RectangleF bounds = freeformPath.GetBounds();
                left = (int)Math.Floor(bounds.Left); top = (int)Math.Floor(bounds.Top);
                right = (int)Math.Ceiling(bounds.Right); bottom = (int)Math.Ceiling(bounds.Bottom);
            }
            else foreach (Point point in freeformPoints)
            {
                left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
            }
            return Rectangle.FromLTRB(left - 3, top - 3, right + 4, bottom + 4);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
                return;

            Rectangle previousSelection = selection;
            Rectangle previousFreeform = FreeformVisualBounds();
            Rectangle previousDimensions = GetDimensionBox();
            Rectangle previousInstructions = instructionBox;
            CaptureWindow previousWindow = hoveredWindow;
            current = ClampPoint(e.Location);
            UpdateInstructionLocation();
            if (useRecordingArea != null)
            {
                if (dragging) ApplyRecordingAdjustment();
                dragging = false;
                Capture = false;
                adjustmentEdge = AdjustmentEdge.None;
                Cursor = AdjustmentCursor(GetAdjustmentEdge(current));
                InvalidateOverlayChange(previousSelection, previousFreeform, previousDimensions, previousInstructions, false, false);
                return;
            }
            if (SelectsWholeTarget)
            {
                UpdateHoveredWindow(current);
                if (!selection.IsEmpty)
                    CompleteSelection();
                else InvalidateOverlayChange(previousSelection, previousFreeform, previousDimensions, previousInstructions,
                    false, !Object.ReferenceEquals(previousWindow, hoveredWindow));
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
                    ResetSelection(previousSelection, previousFreeform, previousDimensions, previousInstructions);
                    return;
                }
            }
            else
                selection = CaptureService.NormalizeSelection(start, current, ClientRectangle);

            if (selection.Width >= 2 && selection.Height >= 2)
                CompleteSelection();
            else
                ResetSelection(previousSelection, previousFreeform, previousDimensions, previousInstructions);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture)
            {
                dragging = false;
                if (useRecordingArea != null)
                {
                    Rectangle previous = selection;
                    Rectangle dimensions = GetDimensionBox();
                    selection = adjustmentOrigin;
                    adjustmentEdge = AdjustmentEdge.None;
                    InvalidateOverlayChange(previous, Rectangle.Empty, dimensions, instructionBox, false, false);
                }
                else ResetSelection();
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
            ResetSelection(selection, FreeformVisualBounds(), GetDimensionBox(), instructionBox);
        }

        private void ResetSelection(Rectangle previousSelection, Rectangle previousFreeform,
            Rectangle previousDimensions, Rectangle previousInstructions)
        {
            selection = Rectangle.Empty;
            freeformPoints.Clear();
            if (freeformPath != null)
            {
                freeformPath.Dispose();
                freeformPath = null;
            }
            InvalidateOverlayChange(previousSelection, previousFreeform, previousDimensions, previousInstructions,
                mode == CaptureMode.Freeform, false);
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
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Escape)
            {
                CancelSelection();
                return true;
            }
            if (key == Keys.Enter && useRecordingArea != null)
            {
                if (!dragging) CompleteSelection();
                return true;
            }
            if (useRecordingArea != null && !dragging &&
                (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down))
            {
                Rectangle previous = selection;
                Rectangle dimensions = GetDimensionBox();
                int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
                int dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
                int dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
                selection = MoveRecordingArea(selection, dx, dy);
                InvalidateOverlayChange(previous, Rectangle.Empty, dimensions, instructionBox, false, false);
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
