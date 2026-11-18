using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // The recorder owns this short-lived outline. Normal strips are entirely
    // outside the captured pixels. At a monitor edge, an inset strip is allowed
    // only after Windows confirms WDA_EXCLUDEFROMCAPTURE on its hidden HWND.
    internal sealed class RecordingBoundary : IDisposable
    {
        internal const int Thickness = 3;
        private readonly Rectangle captureBounds;
        private readonly List<BoundaryStrip> strips = new List<BoundaryStrip>();
        private bool shown, disposed;

        internal Rectangle CaptureBounds { get { return captureBounds; } }
        internal bool IsDisposed { get { return disposed; } }
        internal int VisibleSegmentCount { get; private set; }
        internal bool HasCompleteOutline { get; private set; }
        internal bool CaptureExclusionSupported { get; private set; }

        internal RecordingBoundary(Rectangle bounds)
        {
            ValidateGeometry(bounds, new Rectangle[0], Thickness);
            captureBounds = bounds;
        }

        internal void Show()
        {
            if (disposed) throw new ObjectDisposedException("RecordingBoundary");
            if (shown) return;
            List<Rectangle> monitors = new List<Rectangle>();
            foreach (Screen screen in Screen.AllScreens) monitors.Add(screen.Bounds);
            Rectangle[] outside = CalculateSegments(captureBounds, monitors.ToArray(), Thickness);
            Rectangle[] inside = CalculateInsetSegments(captureBounds, monitors.ToArray(), Thickness);
            bool supportsExclusion = SupportsCaptureExclusion();
            bool complete = true, allExcluded = true;
            try
            {
                foreach (Rectangle bounds in outside)
                    ShowStrip(bounds, false, supportsExclusion, ref allExcluded);
                foreach (Rectangle bounds in inside)
                    if (!ShowStrip(bounds, true, supportsExclusion, ref allExcluded)) complete = false;
                HasCompleteOutline = complete && VisibleSegmentCount > 0;
                CaptureExclusionSupported = allExcluded && VisibleSegmentCount > 0;
                shown = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private bool ShowStrip(Rectangle bounds, bool requiresExclusion, bool supportsExclusion, ref bool allExcluded)
        {
            BoundaryStrip strip = new BoundaryStrip(bounds, requiresExclusion, supportsExclusion);
            strips.Add(strip);
            bool visible = strip.ShowBoundary();
            if (visible)
            {
                VisibleSegmentCount++;
                allExcluded = allExcluded && strip.ExclusionVerified;
            }
            return visible;
        }

        // Pure physical-pixel geometry; do not replace off-screen exterior
        // strips by clamping them inward into the video.
        internal static Rectangle[] CalculateSegments(Rectangle capture, Rectangle[] monitorBounds, int thickness)
        {
            ValidateGeometry(capture, monitorBounds, thickness);
            List<Rectangle> result = new List<Rectangle>();
            foreach (Rectangle side in ExteriorSides(capture, thickness))
                AddMonitorClips(result, side, monitorBounds);
            return result.ToArray();
        }

        // Returns only portions of edges without a visible exterior strip.
        // These rectangles overlap captured pixels and MUST remain hidden
        // unless exclusion has been verified for that individual native HWND.
        internal static Rectangle[] CalculateInsetSegments(Rectangle capture, Rectangle[] monitorBounds, int thickness)
        {
            ValidateGeometry(capture, monitorBounds, thickness);
            Rectangle[] sides = ExteriorSides(capture, thickness);
            List<Rectangle> result = new List<Rectangle>();
            for (int side = 0; side < sides.Length; side++)
            {
                bool horizontal = side < 2;
                int first = horizontal ? capture.Left : capture.Top;
                int last = horizontal ? capture.Right : capture.Bottom;
                List<Interval> visible = new List<Interval>();
                foreach (Rectangle monitor in monitorBounds)
                {
                    Rectangle clipped = Rectangle.Intersect(sides[side], monitor);
                    if (clipped.Width < 1 || clipped.Height < 1) continue;
                    int start = Math.Max(first, horizontal ? clipped.Left : clipped.Top);
                    int end = Math.Min(last, horizontal ? clipped.Right : clipped.Bottom);
                    if (end > start) visible.Add(new Interval(start, end));
                }
                visible.Sort(delegate(Interval a, Interval b) { return a.Start.CompareTo(b.Start); });
                int next = first;
                foreach (Interval interval in visible)
                {
                    if (interval.Start > next)
                        AddInset(result, capture, monitorBounds, thickness, side, next, interval.Start);
                    next = Math.Max(next, interval.End);
                }
                if (next < last) AddInset(result, capture, monitorBounds, thickness, side, next, last);
            }
            return result.ToArray();
        }

        private static Rectangle[] ExteriorSides(Rectangle bounds, int thickness)
        {
            return new Rectangle[] {
                new Rectangle(bounds.Left - thickness, bounds.Top - thickness, bounds.Width + thickness * 2, thickness),
                new Rectangle(bounds.Left - thickness, bounds.Bottom, bounds.Width + thickness * 2, thickness),
                new Rectangle(bounds.Left - thickness, bounds.Top, thickness, bounds.Height),
                new Rectangle(bounds.Right, bounds.Top, thickness, bounds.Height)
            };
        }

        private static void AddInset(List<Rectangle> result, Rectangle capture, Rectangle[] monitors,
            int thickness, int side, int first, int last)
        {
            int width = Math.Min(thickness, capture.Width), height = Math.Min(thickness, capture.Height);
            Rectangle bounds = side == 0 ? new Rectangle(first, capture.Top, last - first, height) :
                side == 1 ? new Rectangle(first, capture.Bottom - height, last - first, height) :
                side == 2 ? new Rectangle(capture.Left, first, width, last - first) :
                new Rectangle(capture.Right - width, first, width, last - first);
            AddMonitorClips(result, bounds, monitors);
        }

        private static void AddMonitorClips(List<Rectangle> result, Rectangle bounds, Rectangle[] monitors)
        {
            foreach (Rectangle monitor in monitors)
            {
                Rectangle clipped = Rectangle.Intersect(bounds, monitor);
                if (clipped.Width > 0 && clipped.Height > 0 && !result.Contains(clipped)) result.Add(clipped);
            }
        }

        private static void ValidateGeometry(Rectangle capture, Rectangle[] monitors, int thickness)
        {
            if (monitors == null) throw new ArgumentNullException("monitorBounds");
            if (thickness < 1 || thickness > 64) throw new ArgumentOutOfRangeException("thickness");
            if (capture.Width < 1 || capture.Height < 1 ||
                (long)capture.Width + thickness * 2 > Int32.MaxValue ||
                (long)capture.Left - thickness < Int32.MinValue ||
                (long)capture.Top - thickness < Int32.MinValue ||
                (long)capture.Left + capture.Width + thickness > Int32.MaxValue ||
                (long)capture.Top + capture.Height + thickness > Int32.MaxValue)
                throw new ArgumentOutOfRangeException("capture");
            foreach (Rectangle monitor in monitors)
                if (monitor.Width < 1 || monitor.Height < 1 ||
                    (long)monitor.Left + monitor.Width > Int32.MaxValue ||
                    (long)monitor.Top + monitor.Height > Int32.MaxValue)
                    throw new ArgumentOutOfRangeException("monitorBounds");
        }

        private struct Interval
        {
            internal int Start, End;
            internal Interval(int start, int end) { Start = start; End = end; }
        }

        private static bool SupportsCaptureExclusion()
        {
            try
            {
                WindowsVersion version = new WindowsVersion();
                return RtlGetVersion(version) == 0 &&
                    (version.Major > 10 || (version.Major == 10 && version.Build >= 19041));
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Exception failure = null;
            foreach (BoundaryStrip strip in strips)
            {
                try { strip.Dispose(); }
                catch (Exception error) { if (failure == null) failure = error; }
            }
            strips.Clear();
            VisibleSegmentCount = 0;
            HasCompleteOutline = false;
            CaptureExclusionSupported = false;
            if (failure != null) throw new InvalidOperationException("The recording outline could not be closed completely.", failure);
        }

        private sealed class BoundaryStrip : Form
        {
            private readonly Rectangle physicalBounds;
            private readonly bool requiresExclusion, supportsExclusion;
            internal bool ExclusionVerified { get; private set; }

            internal BoundaryStrip(Rectangle bounds, bool mustExclude, bool canExclude)
            {
                physicalBounds = bounds;
                requiresExclusion = mustExclude;
                supportsExclusion = canExclude;
                Text = "Recording boundary";
                AccessibleName = "Recording area outline";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                MinimumSize = MaximumSize = Size.Empty;
                Bounds = bounds;
                ShowInTaskbar = false;
                TopMost = true;
                // Layered + transparent is intentional: clicks pass through
                // even when the target application belongs to another thread.
                Opacity = 0.99;
                Enabled = false;
                BackColor = Color.FromArgb(227, 45, 61);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer, true);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams value = base.CreateParams;
                    value.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000;
                    value.ClassStyle &= ~0x00020000; // no drop shadow
                    value.X = physicalBounds.X;
                    value.Y = physicalBounds.Y;
                    value.Width = physicalBounds.Width;
                    value.Height = physicalBounds.Height;
                    return value;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                ExclusionVerified = false;
                try
                {
                    uint affinity;
                    ExclusionVerified = supportsExclusion && SetWindowDisplayAffinity(Handle, 0x11) &&
                        GetWindowDisplayAffinity(Handle, out affinity) && affinity == 0x11;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                ExclusionVerified = false;
                base.OnHandleDestroyed(e);
            }

            internal bool ShowBoundary()
            {
                // Create while hidden, then inspect affinity. Never display an
                // inset even briefly when exclusion is unavailable.
                IntPtr native = Handle;
                if (requiresExclusion && !ExclusionVerified) return false;
                Show();
                return Visible;
            }

            protected override void SetVisibleCore(bool value)
            {
                if (value)
                {
                    IntPtr native = Handle;
                    if (requiresExclusion && !ExclusionVerified) return;
                    PlaceAtPhysicalBounds(false);
                }
                base.SetVisibleCore(value);
                if (value) PlaceAtPhysicalBounds(true);
            }

            private void PlaceAtPhysicalBounds(bool show)
            {
                if (!CaptureNativeMethods.SetWindowPos(Handle, CaptureNativeMethods.HwndTopmost,
                    physicalBounds.Left, physicalBounds.Top, physicalBounds.Width, physicalBounds.Height,
                    0x0010 | (show ? CaptureNativeMethods.SwpShowWindow : 0)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The recording outline could not be placed.");
                CaptureNativeMethods.NativeRect actual;
                if (!CaptureNativeMethods.GetWindowRect(Handle, out actual) || actual.ToRectangle() != physicalBounds)
                    throw new InvalidOperationException("The recording outline did not stay at the selected screen boundary.");
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                bool horizontal = physicalBounds.Width >= physicalBounds.Height;
                int extent = horizontal ? ClientSize.Width : ClientSize.Height;
                using (Brush brush = new SolidBrush(Color.White))
                    for (int offset = 0; offset < extent; offset += 12)
                    {
                        int length = Math.Min(6, extent - offset);
                        e.Graphics.FillRectangle(brush, horizontal ? new Rectangle(offset, 0, length, ClientSize.Height) :
                            new Rectangle(0, offset, ClientSize.Width, length));
                    }
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
                if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
                // No text or scaled controls exist here. Suggested DPI bounds
                // could put an exterior strip inside the recording, so retain
                // the immutable physical-pixel rectangle across monitors.
                if (message.Msg == 0x02E0) { message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WindowsVersion
        {
            internal int Size = Marshal.SizeOf(typeof(WindowsVersion));
            internal int Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion([In, Out] WindowsVersion version);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    }
}
