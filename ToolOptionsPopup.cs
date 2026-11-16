using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    // The options card deliberately has its own palette: it is a separate,
    // lightly tinted surface below the main toolbar, with clean field controls.
    internal static class ToolOptionsAppearance
    {
        internal static Color Background { get { return StudioTheme.IsDark ? Color.FromArgb(37, 51, 71) : Color.FromArgb(237, 243, 252); } }
        internal static Color Field { get { return StudioTheme.IsDark ? Color.FromArgb(24, 36, 53) : Color.White; } }
        internal static Color Border { get { return StudioTheme.IsDark ? Color.FromArgb(66, 93, 129) : Color.FromArgb(204, 217, 237); } }
        internal static Color Line { get { return StudioTheme.IsDark ? Color.FromArgb(59, 72, 90) : Color.FromArgb(217, 225, 235); } }
        internal static Color Ink { get { return StudioTheme.IsDark ? Color.FromArgb(236, 242, 250) : Color.FromArgb(32, 43, 59); } }
        internal static Color Muted { get { return StudioTheme.IsDark ? Color.FromArgb(169, 186, 208) : Color.FromArgb(82, 100, 123); } }
        internal static Color Accent { get { return StudioTheme.IsDark ? Color.FromArgb(89, 168, 255) : Color.FromArgb(0, 103, 192); } }

        internal static bool IsOptionsSurface(Control control)
        {
            for (Control current = control; current != null; current = current.Parent)
                if (current is ToolOptionsContentPanel) return true;
            return false;
        }
    }

    // A type marker keeps the options-only field appearance scoped to this
    // surface without changing the borrowed controls' Tag or event bindings.
    internal sealed class ToolOptionsContentPanel : Panel { }

    // Formatting controls keep their existing bindings and belong to the main
    // window. The attached card borrows them until it is closed. The historical
    // class name and opening signature stay compatible with its callers.
    internal sealed class ToolOptionsPopup : Panel
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
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly Font bodyFont, titleFont, kickerFont;
        private readonly float scale;
        private Size preferredBody, scrollContentSize;
        private Rectangle workingArea;
        private bool restoring, resourcesDisposed;
        private bool compactMode = true, showCompactCaption;

        internal Panel Content { get { return content; } }
        // A single row may reserve viewport space for the native horizontal
        // scrollbar without making that space part of the scrollable row.
        // Empty retains the legacy SetContentSize scrolling contract.
        internal Size ScrollContentSize
        {
            get { return scrollContentSize; }
            set
            {
                scrollContentSize = value.IsEmpty ? Size.Empty : new Size(Math.Max(1, value.Width), Math.Max(1, value.Height));
                ArrangeFrame();
            }
        }
        internal bool CompactMode
        {
            get { return compactMode; }
            set { if (compactMode != value) { compactMode = value; ArrangeFrame(); } }
        }
        internal bool ShowCompactCaption
        {
            get { return showCompactCaption; }
            set { if (showCompactCaption != value) { showCompactCaption = value; ArrangeFrame(); } }
        }
        // Device-pixel reservations include their outside inset and the gap to
        // the body. Callers subtract these from the available strip width.
        internal int HeaderWidth { get { return compactMode ? S(showCompactCaption ? 88 : 8) : S(28); } }
        internal int ReservedCloseWidth { get { return compactMode ? S(32) : 0; } }
        internal event EventHandler CardClosed;
        // Retained for existing combo-box handlers. An attached card remains
        // available while a nested field opens; it has no native auto-close.
        internal bool AutoClose { get; set; }

        internal ToolOptionsPopup(string title, float scale)
        {
            this.scale = Math.Max(.75f, Math.Min(4f, scale));
            AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty;
            Dock = DockStyle.None; Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Visible = false; TabStop = false; AutoClose = true;
            BackColor = ToolOptionsAppearance.Background; ForeColor = ToolOptionsAppearance.Ink;
            AccessibleName = String.IsNullOrWhiteSpace(title) ? "Tool options" : title;
            try
            {
                bodyFont = new Font("Segoe UI", 9.5f);
                titleFont = new Font("Segoe UI", 9.75f, FontStyle.Bold);
                kickerFont = new Font("Segoe UI", 8.25f);
                frame = new FramePanel(this.scale) { Margin = Padding.Empty, Padding = Padding.Empty, BackColor = ToolOptionsAppearance.Background };
                content = new ToolOptionsContentPanel { BackColor = ToolOptionsAppearance.Background, ForeColor = ToolOptionsAppearance.Ink,
                    Font = bodyFont, AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty };
                header = new HeaderPanel(AccessibleName, this.scale, kickerFont) { Font = titleFont, BackColor = ToolOptionsAppearance.Background,
                    AccessibleName = AccessibleName, TabStop = false };
                closeButton = new StudioButton { Text = "×", Font = bodyFont,
                    AccessibleName = "Close tool options", TabStop = true };
                closeButton.Click += delegate { Close(ToolStripDropDownCloseReason.CloseCalled); };
                tips.SetToolTip(closeButton, "Close tool options (Escape)");
                frame.Controls.Add(header); frame.Controls.Add(content); header.Controls.Add(closeButton);
                Controls.Add(frame);
                SetContentSize(new Size(S(304), S(32)));
                StudioTheme.ThemeChanged += ThemeChanged;
            }
            catch { Dispose(); throw; }
        }

        // This is a child surface of the editor, never a separate popup window.
        // The caller supplies a position beside its toolbar in owner coordinates.
        internal void Show(Control owner, Point location, ToolStripDropDownDirection direction)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (owner.IsDisposed) throw new ObjectDisposedException("owner");
            if (IsDisposed) throw new ObjectDisposedException("ToolOptionsPopup");
            if (Object.ReferenceEquals(owner, this) || Contains(owner))
                throw new ArgumentException("An options card cannot own itself.", "owner");
            if (!Object.ReferenceEquals(Parent, owner)) owner.Controls.Add(this);
            Dock = DockStyle.None;
            // Preserve the caller's smaller available area below the toolbar.
            // Only constrain again if the full owner is smaller than that area.
            if (Width > owner.ClientSize.Width || Height > owner.ClientSize.Height)
                FitToWorkingArea(owner.ClientRectangle);
            Location = new Point(Math.Max(0, Math.Min(location.X, owner.ClientSize.Width - Width)),
                Math.Max(0, Math.Min(location.Y, owner.ClientSize.Height - Height)));
            BringToFront();
            Visible = true;
        }

        internal void Close(ToolStripDropDownCloseReason reason)
        {
            if (IsDisposed) return;
            try { Visible = false; }
            finally
            {
                RestoreHostedControls();
                EventHandler handler = CardClosed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        // The context remains available to accessibility even when the compact
        // strip hides its caption. Expanded chrome shows both group and tool.
        internal void SetContext(string kicker, string currentTool)
        {
            header.SetContext(kicker, currentTool);
            AccessibleName = header.AccessibleName;
        }

        private void ThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed || resourcesDisposed) return;
            BackColor = frame.BackColor = header.BackColor = content.BackColor = ToolOptionsAppearance.Background;
            ForeColor = content.ForeColor = ToolOptionsAppearance.Ink;
            frame.Invalidate(true);
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
        // frame. Compact chrome shares the first body row and adds only eight
        // logical pixels vertically. It never covers a borrowed field.
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
            if (content == null || frame == null) return;
            int inset = compactMode ? S(4) : S(14), heading = S(compactMode ? 24 : 28), gap = S(6);
            Size desired = compactMode
                ? new Size(preferredBody.Width + HeaderWidth + ReservedCloseWidth, Math.Max(preferredBody.Height, heading) + inset * 2)
                : new Size(preferredBody.Width + inset * 2, preferredBody.Height + heading + gap + inset * 2);
            if (!workingArea.IsEmpty)
            {
                int maximumWidth = Math.Max(1, workingArea.Width - (compactMode ? 0 : S(8)));
                int maximumHeight = Math.Max(1, workingArea.Height - (compactMode ? 0 : S(8)));
                // Reserve room for a vertical scrollbar when the monitor forces
                // a shorter body, so it does not create an unnecessary second bar.
                if (desired.Height > maximumHeight)
                    desired.Width += Math.Max(SystemInformation.VerticalScrollBarWidth, S(17));
                desired = new Size(Math.Min(desired.Width, maximumWidth), Math.Min(desired.Height, maximumHeight));
            }
            Size = frame.Size = desired;
            frame.Location = Point.Empty;
            header.Compact = frame.Compact = compactMode;
            header.Visible = !compactMode || showCompactCaption;
            if (compactMode)
            {
                header.SetBounds(S(8), inset, Math.Max(1, HeaderWidth - S(16)), Math.Min(heading, Math.Max(1, desired.Height - inset * 2)));
                content.SetBounds(HeaderWidth, inset, Math.Max(1, desired.Width - HeaderWidth - ReservedCloseWidth), Math.Max(1, desired.Height - inset * 2));
                if (!Object.ReferenceEquals(closeButton.Parent, frame)) frame.Controls.Add(closeButton);
                closeButton.SetBounds(Math.Max(0, desired.Width - S(28)), inset, S(24), Math.Min(heading, Math.Max(1, desired.Height - inset * 2)));
            }
            else
            {
                header.SetBounds(inset, inset, Math.Max(1, desired.Width - inset * 2), heading);
                content.SetBounds(inset, inset + heading + gap, Math.Max(1, desired.Width - inset * 2), Math.Max(1, desired.Height - heading - gap - inset * 2));
                if (!Object.ReferenceEquals(closeButton.Parent, header)) header.Controls.Add(closeButton);
                closeButton.SetBounds(Math.Max(0, header.Width - S(28)), 0, S(28), heading);
            }
            content.AutoScrollMinSize = scrollContentSize.IsEmpty ? preferredBody : scrollContentSize;
            closeButton.BringToFront();
            // Clip the editor child to the same rounded card outline.
            Region previous = Region;
            using (GraphicsPath outline = StudioTheme.Round(new RectangleF(0, 0, Math.Max(1, Width), Math.Max(1, Height)), (compactMode ? 6 : 12) * scale))
                Region = new Region(outline);
            if (previous != null) previous.Dispose();
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

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(ToolStripDropDownCloseReason.Keyboard); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private int S(int value) { return Math.Max(1, (int)Math.Round(value * scale)); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RestoreHostedControls();
                if (Parent != null) Parent.Controls.Remove(this);
                Region outline = Region;
                Region = null;
                if (outline != null) outline.Dispose();
            }
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing && !resourcesDisposed)
                {
                    resourcesDisposed = true;
                    StudioTheme.ThemeChanged -= ThemeChanged;
                    tips.Dispose();
                    if (bodyFont != null) bodyFont.Dispose();
                    if (titleFont != null) titleFont.Dispose();
                    if (kickerFont != null) kickerFont.Dispose();
                }
            }
        }

        private sealed class FramePanel : Panel
        {
            private readonly float scale;
            internal bool Compact { get; set; }
            internal FramePanel(float scale)
            {
                this.scale = scale;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics; graphics.Clear(ToolOptionsAppearance.Background);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float line = Math.Max(1f, scale);
                using (GraphicsPath path = StudioTheme.Round(new RectangleF(line / 2, line / 2, Math.Max(1, Width - line), Math.Max(1, Height - line)), (Compact ? 6 : 12) * scale))
                using (Brush fill = new SolidBrush(ToolOptionsAppearance.Background))
                using (Pen border = new Pen(ToolOptionsAppearance.Border, line))
                {
                    graphics.FillPath(fill, path); graphics.DrawPath(border, path);
                    GraphicsState state = graphics.Save();
                    try
                    {
                        graphics.SetClip(path);
                        using (Brush accent = new SolidBrush(ToolOptionsAppearance.Accent))
                            graphics.FillRectangle(accent, 0, 0, Math.Max(3f, 3 * scale), Height);
                    }
                    finally { graphics.Restore(state); }
                }
            }
        }

        private sealed class HeaderPanel : Control
        {
            private string kicker, currentTool;
            private readonly float scale;
            private readonly Font kickerFont;
            internal bool Compact { get; set; }
            internal HeaderPanel(string title, float scale, Font kickerFont)
            {
                this.scale = scale; this.kickerFont = kickerFont;
                SetContext(title, "");
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            internal void SetContext(string group, string current)
            {
                kicker = String.IsNullOrWhiteSpace(group) ? "TOOL OPTIONS" : group.Trim().ToUpperInvariant();
                currentTool = current == null ? "" : current.Trim();
                AccessibleName = kicker + (currentTool.Length == 0 ? "" : ": " + currentTool);
                Invalidate();
            }
            protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(ToolOptionsAppearance.Background); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (Compact)
                {
                    string caption = currentTool.Length > 0 ? currentTool : kicker;
                    if (caption.EndsWith(" OPTIONS", StringComparison.Ordinal)) caption = caption.Substring(0, caption.Length - 8);
                    TextRenderer.DrawText(graphics, caption, kickerFont, ClientRectangle, ToolOptionsAppearance.Muted,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    return;
                }
                int available = Math.Max(0, Width - (int)Math.Ceiling(38 * scale));
                int measured = TextRenderer.MeasureText(graphics, kicker, kickerFont, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                int titleWidth = Math.Min(available, measured);
                Rectangle text = new Rectangle(0, 0, titleWidth, Height);
                TextRenderer.DrawText(graphics, kicker, kickerFont, text, ToolOptionsAppearance.Muted,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (currentTool.Length == 0) return;
                int separatorX = titleWidth + (int)Math.Ceiling(10 * scale);
                int currentX = separatorX + (int)Math.Ceiling(10 * scale);
                if (available <= currentX) return;
                using (Pen divider = new Pen(ToolOptionsAppearance.Border, Math.Max(1f, scale)))
                    graphics.DrawLine(divider, separatorX, 7 * scale, separatorX, Height - 7 * scale);
                TextRenderer.DrawText(graphics, currentTool, Font, new Rectangle(currentX, 0, available - currentX, Height), ToolOptionsAppearance.Ink,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }
    }
}
