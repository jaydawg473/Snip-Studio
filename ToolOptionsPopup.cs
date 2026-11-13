using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    // Formatting controls keep their existing bindings and belong to the main
    // window. The popup borrows them only until its complete close sequence ends.
    internal sealed class ToolOptionsPopup : ToolStripDropDown
    {
        private sealed class HostedControl
        {
            internal Control Control, Parent;
            internal Rectangle Bounds;
            internal bool Visible;
            internal int TabIndex;
        }

        private readonly List<HostedControl> hosted = new List<HostedControl>();
        private readonly FramePanel frame;
        private readonly HeaderPanel header;
        private readonly Panel content;
        private readonly StudioButton closeButton;
        private readonly ToolStripControlHost host;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly Font bodyFont, titleFont;
        private readonly float scale;
        private Size preferredBody;
        private Rectangle workingArea;
        private bool restoring, resourcesDisposed;

        internal Panel Content { get { return content; } }

        internal ToolOptionsPopup(string title, float scale)
        {
            this.scale = Math.Max(.75f, Math.Min(4f, scale));
            AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty;
            BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text;
            AccessibleName = String.IsNullOrWhiteSpace(title) ? "Tool options" : title;
            try
            {
                bodyFont = new Font("Segoe UI", 9.5f);
                titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
                frame = new FramePanel(this.scale) { Margin = Padding.Empty, Padding = Padding.Empty, BackColor = StudioTheme.Raised };
                content = new Panel { BackColor = StudioTheme.Raised, ForeColor = StudioTheme.Text,
                    Font = bodyFont, AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty };
                header = new HeaderPanel(AccessibleName, this.scale) { Font = titleFont, BackColor = StudioTheme.Selection,
                    AccessibleName = AccessibleName, TabStop = false };
                closeButton = new StudioButton { Text = "×", Font = bodyFont,
                    AccessibleName = "Close tool options", TabStop = true };
                closeButton.Click += delegate { Close(ToolStripDropDownCloseReason.CloseCalled); };
                tips.SetToolTip(closeButton, "Close tool options (Escape)");
                frame.Controls.Add(header); frame.Controls.Add(content); header.Controls.Add(closeButton);
                host = new ToolStripControlHost(frame) { AutoSize = false,
                    Margin = Padding.Empty, Padding = Padding.Empty };
                Items.Add(host);
                SetContentSize(new Size(S(304), S(130)));
            }
            catch { Dispose(); throw; }
        }

        internal void AttachControl(Control control)
        {
            if (control == null) throw new ArgumentNullException("control");
            if (control.IsDisposed) throw new ObjectDisposedException("control");
            if (Object.ReferenceEquals(control, this) || Object.ReferenceEquals(control, frame) || Object.ReferenceEquals(control, content))
                throw new ArgumentException("A popup cannot host itself.", "control");
            foreach (HostedControl item in hosted) if (Object.ReferenceEquals(item.Control, control)) return;
            hosted.Add(new HostedControl { Control = control, Parent = control.Parent,
                Bounds = control.Bounds, Visible = control.Visible, TabIndex = control.TabIndex });
            content.Controls.Add(control);
            control.Visible = true;
        }

        // The size is for the scrolling body in device pixels, excluding the
        // header and frame. Larger bodies remain reachable on smaller monitors.
        internal void SetContentSize(Size size)
        {
            preferredBody = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
            ArrangeFrame();
        }

        internal void FitToWorkingArea(Rectangle work)
        {
            workingArea = work;
            ArrangeFrame();
        }

        private void ArrangeFrame()
        {
            if (content == null || frame == null || host == null) return;
            int inset = S(8), heading = S(40);
            Size desired = new Size(preferredBody.Width + inset * 2, preferredBody.Height + heading + inset * 2);
            if (!workingArea.IsEmpty)
            {
                int maximumWidth = Math.Max(1, workingArea.Width - S(8));
                int maximumHeight = Math.Max(1, workingArea.Height - S(8));
                // Reserve room for a vertical scrollbar when the monitor forces
                // a shorter body, so it does not create an unnecessary second bar.
                if (desired.Height > maximumHeight)
                    desired.Width += Math.Max(SystemInformation.VerticalScrollBarWidth, S(17));
                desired = new Size(Math.Min(desired.Width, maximumWidth), Math.Min(desired.Height, maximumHeight));
            }
            Size = host.Size = frame.Size = desired;
            header.SetBounds(inset, inset, Math.Max(1, desired.Width - inset * 2), heading);
            content.SetBounds(inset, inset + heading, Math.Max(1, desired.Width - inset * 2), Math.Max(1, desired.Height - heading - inset * 2));
            content.AutoScrollMinSize = preferredBody;
            closeButton.SetBounds(Math.Max(0, header.Width - S(32)), S(6), S(28), S(28));
            closeButton.BringToFront();
            frame.Invalidate(); header.Invalidate();
        }

        internal void RestoreHostedControls()
        {
            if (restoring || hosted.Count == 0) return;
            restoring = true;
            HostedControl[] restore = hosted.ToArray();
            hosted.Clear();
            try
            {
                foreach (HostedControl item in restore)
                {
                    Control control = item.Control;
                    if (control == null || control.IsDisposed) continue;
                    control.Visible = false;
                    if (content != null) content.Controls.Remove(control);
                    if (item.Parent != null && !item.Parent.IsDisposed) item.Parent.Controls.Add(control);
                    control.Bounds = item.Bounds;
                    control.TabIndex = item.TabIndex;
                    control.Visible = item.Visible;
                }
            }
            finally { restoring = false; }
        }

        protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
        {
            // Never dispose a popup in Closed: WinForms may still inspect its
            // owner during the remainder of that close/click message sequence.
            try { base.OnClosed(e); }
            finally { RestoreHostedControls(); }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(ToolStripDropDownCloseReason.Keyboard); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private int S(int value) { return Math.Max(1, (int)Math.Round(value * scale)); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) RestoreHostedControls();
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing && !resourcesDisposed)
                {
                    resourcesDisposed = true;
                    tips.Dispose();
                    if (bodyFont != null) bodyFont.Dispose();
                    if (titleFont != null) titleFont.Dispose();
                }
            }
        }

        private sealed class FramePanel : Panel
        {
            private readonly float scale;
            internal FramePanel(float scale)
            {
                this.scale = scale;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics; graphics.Clear(StudioTheme.Surface);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float line = Math.Max(1f, scale);
                using (GraphicsPath path = StudioTheme.Round(new RectangleF(line / 2, line / 2, Math.Max(1, Width - line), Math.Max(1, Height - line)), 9 * scale))
                using (Brush fill = new SolidBrush(StudioTheme.Raised))
                using (Pen border = new Pen(StudioTheme.Border, line))
                { graphics.FillPath(fill, path); graphics.DrawPath(border, path); }
            }
        }

        private sealed class HeaderPanel : Control
        {
            private readonly string title;
            private readonly float scale;
            internal HeaderPanel(string title, float scale)
            {
                this.title = title; this.scale = scale;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(StudioTheme.Raised); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = StudioTheme.Round(new RectangleF(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 5 * scale))
                using (Brush fill = new SolidBrush(StudioTheme.Selection)) graphics.FillPath(fill, path);
                using (Pen accent = new Pen(StudioTheme.Accent, Math.Max(2f, 3 * scale)))
                    graphics.DrawLine(accent, 5 * scale, 10 * scale, 5 * scale, Height - 10 * scale);
                Rectangle text = new Rectangle((int)(15 * scale), 0, Math.Max(1, Width - (int)(50 * scale)), Height);
                TextRenderer.DrawText(graphics, title, Font, text, StudioTheme.Text,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }
}
