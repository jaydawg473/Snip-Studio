using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // The capture owner handles cancellation, stitching and image ownership.
    internal sealed class ScrollingCaptureControlForm : Form
    {
        private readonly Rectangle captureRegion;
        private readonly Label progress = new Label();
        private readonly Label status = new Label();
        private readonly StudioButton finish = new StudioButton();
        private readonly StudioButton cancel = new StudioButton();
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private bool positioning;
        private bool closingAllowed;
        private bool escapeRegistered;
        private const int EscapeHotkeyId = 0x5343;

        internal event EventHandler FinishRequested;
        internal event EventHandler CancelRequested;
        internal bool FinishIsRequested { get; private set; }
        internal bool CancelIsRequested { get; private set; }
        internal bool CaptureExclusionSupported { get; private set; }
        internal bool IsOutsideCapture
        {
            get
            {
                Rectangle occupied = Bounds;
                occupied.Inflate(12, 12); // Leave room for the native window shadow.
                return !occupied.IntersectsWith(captureRegion);
            }
        }
        internal bool IsSafeForCapture { get { return IsOutsideCapture || CaptureExclusionSupported; } }
        internal bool AllowClose { get { return closingAllowed; } set { closingAllowed = value; } }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WindowsVersion
        {
            internal int Size = Marshal.SizeOf(typeof(WindowsVersion));
            internal int Major;
            internal int Minor;
            internal int Build;
            internal int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion([In, Out] WindowsVersion version);

        internal ScrollingCaptureControlForm(Rectangle region)
        {
            captureRegion = region;
            Text = "Scrolling capture — Snip Studio";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(442, 183);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            MainForm.TrySetAppIcon(this);
            CaptureVisibilityScope.DisableTransientAnimations(this);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(17, 12, 17, 12);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            progress.Text = "Preparing scrolling capture…";
            progress.Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
            progress.AutoSize = true;
            progress.Margin = new Padding(0, 0, 0, 5);
            progress.AccessibleName = "Captured views and image height";
            layout.Controls.Add(progress, 0, 0);
            status.Text = "Keep the selected window in front while it scrolls.";
            status.Dock = DockStyle.Fill;
            status.ForeColor = StudioTheme.Muted;
            status.Margin = new Padding(0, 0, 0, 8);
            status.AccessibleName = "Scrolling capture status";
            layout.Controls.Add(status, 0, 1);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            actions.Margin = Padding.Empty;
            finish.Text = "Finish";
            finish.Glyph = "stop";
            finish.Primary = true;
            finish.Size = new Size(124, 38);
            finish.Margin = new Padding(0, 0, 9, 0);
            finish.Click += delegate { RequestFinish(); };
            finish.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RequestFinish(); };
            finish.AccessibleName = "Finish and review captured content";
            actions.Controls.Add(finish);
            cancel.Text = "Cancel (Esc)";
            cancel.Size = new Size(132, 38);
            cancel.Margin = Padding.Empty;
            cancel.Click += delegate { RequestCancel(); };
            cancel.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RequestCancel(); };
            cancel.AccessibleName = "Cancel and discard scrolling capture";
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 2);

            Label help = new Label();
            help.Text = "Finish opens a preview. Cancel discards this capture.";
            help.AutoSize = true;
            help.ForeColor = StudioTheme.Muted;
            help.Margin = new Padding(0, 8, 0, 0);
            layout.Controls.Add(help, 0, 3);
            tips.SetToolTip(finish, "Stop scrolling and review the content captured so far.");
            tips.SetToolTip(cancel, "Discard this capture and keep your current image.");
            // Floating native tooltips could extend into the selected region even
            // when the controller itself sits outside it. Its help is inline.
            tips.Popup += delegate(object sender, PopupEventArgs e) { e.Cancel = true; };
            TooltipService.Apply(this);
            Shown += delegate { PlaceOutsideCapture(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return value;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MainForm.ApplyDarkCaption(this);
            // A short-lived hotkey queues even a quick Escape tap while the
            // target application has focus. No keyboard hook is installed.
            escapeRegistered = RegisterHotKey(Handle, EscapeHotkeyId, 0x4000, (uint)Keys.Escape);
            if (!escapeRegistered) cancel.Text = "Cancel";
            CaptureExclusionSupported = false;
            try
            {
                WindowsVersion version = new WindowsVersion();
                // Older releases render an excluded window as a black rectangle.
                if (RtlGetVersion(version) == 0 && (version.Major > 10 ||
                    (version.Major == 10 && version.Build >= 19041)))
                    CaptureExclusionSupported = SetWindowDisplayAffinity(Handle, 0x11);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            PlaceOutsideCapture();
        }

        // Uses actual screen work areas, avoiding gaps in a multi-monitor desktop.
        // The owner must check IsSafeForCapture before starting screen samples.
        internal void PlaceOutsideCapture()
        {
            if (positioning || IsDisposed) return;
            positioning = true;
            try
            {
                const int gap = 12;
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle area = screen.WorkingArea;
                    if (Width > area.Width || Height > area.Height) continue;
                    int left = Math.Max(area.Left, Math.Min(area.Right - Width, captureRegion.Left));
                    int top = Math.Max(area.Top, Math.Min(area.Bottom - Height, captureRegion.Top));
                    Point[] points = new Point[] {
                        new Point(left, captureRegion.Bottom + gap),
                        new Point(left, captureRegion.Top - Height - gap),
                        new Point(captureRegion.Right + gap, top),
                        new Point(captureRegion.Left - Width - gap, top),
                        new Point(area.Right - Width - gap, area.Top + gap),
                        new Point(area.Left + gap, area.Top + gap),
                        new Point(area.Left + gap, area.Bottom - Height - gap),
                        new Point(area.Right - Width - gap, area.Bottom - Height - gap)
                    };
                    foreach (Point point in points)
                    {
                        Rectangle proposed = new Rectangle(point, Size);
                        Rectangle occupied = proposed;
                        occupied.Inflate(gap, gap);
                        if (area.Contains(proposed) && !occupied.IntersectsWith(captureRegion))
                        {
                            Location = point;
                            return;
                        }
                    }
                }
                Rectangle fallback = Screen.FromRectangle(captureRegion).WorkingArea;
                Location = new Point(Math.Max(fallback.Left, fallback.Right - Width - gap),
                    Math.Max(fallback.Top, fallback.Top + gap));
            }
            finally { positioning = false; }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (IsHandleCreated && Visible && !positioning && !CaptureExclusionSupported && !IsOutsideCapture)
                PlaceOutsideCapture();
        }

        internal void UpdateProgress(int frameCount, int imageHeight, string message)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { UpdateProgress(frameCount, imageHeight, message); }); }
                catch (InvalidOperationException) { }
                return;
            }
            progress.Text = Math.Max(0, frameCount).ToString("N0") +
                (frameCount == 1 ? " view" : " views") + " · " + Math.Max(0, imageHeight).ToString("N0") + " px high";
            if (!FinishIsRequested && !CancelIsRequested && !String.IsNullOrEmpty(message)) status.Text = message;
        }

        internal void SetFinishing(string message)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { SetFinishing(message); }); }
                catch (InvalidOperationException) { }
                return;
            }
            FinishIsRequested = true;
            finish.Enabled = false;
            finish.Text = "Finishing…";
            finish.Glyph = "";
            status.Text = String.IsNullOrEmpty(message) ? "Preparing your capture preview…" : message;
        }

        private void RequestFinish()
        {
            if (FinishIsRequested || CancelIsRequested || closingAllowed) return;
            SetFinishing(null);
            EventHandler handler = FinishRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RequestCancel()
        {
            if (CancelIsRequested || closingAllowed) return;
            CancelIsRequested = true;
            finish.Enabled = false;
            cancel.Enabled = false;
            status.Text = "Cancelling and discarding this capture…";
            EventHandler handler = CancelRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == EscapeHotkeyId)
            {
                RequestCancel();
                return;
            }
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE: let buttons click without stealing target focus.
            {
                message.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingAllowed)
            {
                e.Cancel = true;
                RequestCancel();
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape) { RequestCancel(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (escapeRegistered) { UnregisterHotKey(Handle, EscapeHotkeyId); escapeRegistered = false; }
            base.OnHandleDestroyed(e);
        }
    }

    // This dialog borrows the image. OK lets the owner transfer it to the editor.
    internal sealed class ScrollingCapturePreviewForm : Form
    {
        private readonly CapturePreviewCanvas canvas;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());

        internal ScrollingCapturePreviewForm(Bitmap image, int frameCount, string detail)
            : this(image, frameCount, detail, false)
        {
        }

        internal ScrollingCapturePreviewForm(Bitmap image, int frameCount, string detail, bool partial)
        {
            if (image == null) throw new ArgumentNullException("image");
            Text = "Review scrolling capture — Snip Studio";
            Font = new Font("Segoe UI", 9.5f);
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(920, 740);
            MinimumSize = new Size(620, 460);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MainForm.TrySetAppIcon(this);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20, 16, 20, 16);
            layout.ColumnCount = 1;
            layout.RowCount = 6;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            Label heading = new Label();
            heading.Text = partial ? "Review your partial capture" : "Review your scrolling capture";
            heading.Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 0, 0, 7);
            layout.Controls.Add(heading, 0, 0);
            Label summary = new Label();
            summary.AutoSize = true;
            summary.Dock = DockStyle.Fill;
            summary.ForeColor = StudioTheme.Muted;
            summary.Margin = new Padding(0, 0, 0, 10);
            summary.Text = image.Width.ToString("N0") + " × " + image.Height.ToString("N0") + " px · " +
                frameCount.ToString("N0") + (frameCount == 1 ? " view" : " views") +
                (String.IsNullOrWhiteSpace(detail) ? "" : "\r\n" + detail);
            summary.AccessibleName = "Capture dimensions and stop reason";
            layout.Controls.Add(summary, 0, 1);

            FlowLayoutPanel zoom = new FlowLayoutPanel();
            zoom.AutoSize = true;
            zoom.WrapContents = false;
            zoom.Margin = new Padding(0, 0, 0, 10);
            canvas = new CapturePreviewCanvas(image);
            string[] labels = new string[] { "Fit width", "Fit image", "100%" };
            for (int i = 0; i < labels.Length; i++)
            {
                int mode = i;
                StudioButton button = new StudioButton();
                button.Text = labels[i];
                button.Size = new Size(108, 34);
                button.Margin = new Padding(0, 0, 8, 0);
                button.Click += delegate { canvas.SetView(mode); };
                zoom.Controls.Add(button);
            }
            layout.Controls.Add(zoom, 0, 2);
            canvas.Dock = DockStyle.Fill;
            canvas.Margin = Padding.Empty;
            canvas.AccessibleName = "Scrolling capture preview; scroll to inspect the full image";
            layout.Controls.Add(canvas, 0, 3);
            Label guidance = new Label();
            guidance.Text = partial
                ? "More content may remain. Check the joins and bottom before using this partial capture. Select only the scrolling content when trying again."
                : "Check the joins and content. Use capture to crop or annotate in the editor.";
            guidance.AutoSize = true;
            guidance.Dock = DockStyle.Fill;
            guidance.ForeColor = StudioTheme.Muted;
            guidance.Margin = new Padding(0, 10, 0, 12);
            layout.Controls.Add(guidance, 0, 4);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Margin = Padding.Empty;
            StudioButton use = new StudioButton();
            use.Text = "Use capture";
            use.Primary = true;
            use.Size = new Size(140, 40);
            use.Margin = new Padding(10, 0, 0, 0);
            use.DialogResult = DialogResult.OK;
            StudioButton cancel = new StudioButton();
            cancel.Text = "Cancel";
            cancel.Size = new Size(108, 40);
            cancel.Margin = Padding.Empty;
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(use);
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 5);
            AcceptButton = use;
            CancelButton = cancel;
            tips.SetToolTip(use, "Open this capture in the editor. Your automatic save and clipboard settings apply when you choose Use capture.");
            tips.SetToolTip(cancel, "Discard this capture and keep your previous image.");
            TooltipService.Apply(this);
            Shown += delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Size limit = new Size(Math.Max(320, area.Width - 24), Math.Max(300, area.Height - 24));
                MinimumSize = new Size(Math.Min(MinimumSize.Width, limit.Width), Math.Min(MinimumSize.Height, limit.Height));
                Size = new Size(Math.Min(Width, limit.Width), Math.Min(Height, limit.Height));
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
                canvas.Select();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MainForm.ApplyDarkCaption(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        private sealed class CapturePreviewCanvas : ScrollableControl
        {
            private readonly Bitmap image;
            private int viewMode;
            private float zoom = 1;
            private bool arranging;

            internal CapturePreviewCanvas(Bitmap borrowedImage)
            {
                image = borrowedImage;
                AutoScroll = true;
                TabStop = true;
                BackColor = StudioTheme.Raised;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            internal void SetView(int mode)
            {
                viewMode = mode;
                AutoScrollPosition = Point.Empty;
                ArrangeImage();
                Focus();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                ArrangeImage();
            }

            private void ArrangeImage()
            {
                if (arranging || image == null) return;
                arranging = true;
                try
                {
                    int width = Math.Max(1, ClientSize.Width - 20 - SystemInformation.VerticalScrollBarWidth);
                    int height = Math.Max(1, ClientSize.Height - 20 - SystemInformation.HorizontalScrollBarHeight);
                    zoom = viewMode == 2 ? 1f : viewMode == 1
                        ? Math.Min(1f, Math.Min((float)width / image.Width, (float)height / image.Height))
                        : Math.Min(1f, (float)width / image.Width);
                    AutoScrollMinSize = new Size((int)Math.Ceiling(image.Width * zoom) + 20,
                        (int)Math.Ceiling(image.Height * zoom) + 20);
                    Invalidate();
                }
                finally { arranging = false; }
            }

            protected override void OnScroll(ScrollEventArgs e)
            {
                base.OnScroll(e);
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                base.OnMouseDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (image == null || zoom <= 0) return;
                float left = 10 + AutoScrollPosition.X;
                float top = 10 + AutoScrollPosition.Y;
                RectangleF destination = new RectangleF(left, top, image.Width * zoom, image.Height * zoom);
                RectangleF visible = RectangleF.Intersect(destination, ClientRectangle);
                if (visible.Width <= 0 || visible.Height <= 0) return;
                RectangleF source = new RectangleF((visible.Left - left) / zoom, (visible.Top - top) / zoom,
                    visible.Width / zoom, visible.Height / zoom);
                e.Graphics.InterpolationMode = zoom == 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.DrawImage(image, visible, source, GraphicsUnit.Pixel);
            }
        }
    }
}
