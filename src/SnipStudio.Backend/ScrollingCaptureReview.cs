#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

// Review the capture STA's existing bitmap in place. A tall result can be
// inspected at 100% without encoding it or allocating another full image.
internal sealed class ScrollingCaptureReview : Form
{
    private readonly Font summaryFont;
    private readonly Icon? reviewIcon;

    internal ScrollingCaptureReview(Bitmap image, int[] joinRows, int count, string detail, bool partial,
        ScrollingReviewAppearance appearance)
    {
        bool highContrast = SystemInformation.HighContrast;
        bool dark = !highContrast && appearance == ScrollingReviewAppearance.Dark;
        Color surface = highContrast ? SystemColors.Control : dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(248, 250, 253);
        Color text = highContrast ? SystemColors.ControlText : dark ? Color.WhiteSmoke : Color.FromArgb(24, 38, 57);
        Color muted = highContrast ? text : dark ? Color.FromArgb(190, 199, 211) : Color.FromArgb(70, 85, 106);
        Color edge = highContrast ? SystemColors.WindowText : dark ? Color.FromArgb(68, 77, 91) : Color.FromArgb(205, 216, 229);
        Color card = dark ? Color.FromArgb(39, 43, 50) : Color.White;
        Color selected = dark ? Color.FromArgb(36, 74, 112) : Color.FromArgb(221, 237, 253);
        Color accent = dark ? Color.FromArgb(122, 191, 255) : Color.FromArgb(0, 103, 192);
        Color accentText = dark ? Color.FromArgb(20, 35, 53) : Color.White;
        Text = partial ? "Review partial scrolling capture — Snip Studio" : "Review scrolling capture — Snip Studio";
        ShowInTaskbar = false;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(560, 420);
        Font = SystemFonts.MessageBoxFont;
        BackColor = surface;
        ForeColor = text;
        summaryFont = new Font(Font, FontStyle.Bold);
        reviewIcon = TryLoadReviewIcon();
        if (reviewIcon is not null) Icon = reviewIcon;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var summary = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Font = summaryFont,
            Text = $"{image.Width:N0} × {image.Height:N0} px   ·   {count:N0} captured {(count == 1 ? "view" : "views")}",
            Margin = new Padding(0, 0, 0, 4)
        };
        var status = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Text = detail,
            ForeColor = muted, Visible = !string.IsNullOrWhiteSpace(detail),
            Margin = new Padding(0, 0, 0, 12)
        };
        var canvas = new ScrollingPreviewCanvas(image, joinRows)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = highContrast ? SystemColors.Window : dark ? Color.FromArgb(24, 25, 29) : Color.FromArgb(234, 239, 246),
            AccessibleName = "Scrolling capture preview. Use the arrow or Page Up and Page Down keys to scroll."
        };
        var canvasFrame = new Panel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(1), BackColor = edge
        };
        canvasFrame.Controls.Add(canvas);

        var toolbar = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8)
        };
        AddZoom("Fit width", ScrollPreviewZoom.Width);
        AddZoom("Fit image", ScrollPreviewZoom.Image);
        AddZoom("100%", ScrollPreviewZoom.Actual);
        MarkZoom(ScrollPreviewZoom.Width);
        var joins = new CheckBox
        {
            Text = "Show stitch joins", AutoSize = true, Enabled = joinRows.Length > 0,
            Margin = new Padding(8, 9, 0, 0)
        };
        joins.CheckedChanged += (_, _) => canvas.SetJoinVisibility(joins.Checked);
        toolbar.Controls.Add(joins);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        var use = MakeButton("Use capture", 108, primary: true);
        use.DialogResult = DialogResult.OK;
        var discard = MakeButton("Discard", 88);
        discard.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(use);
        actions.Controls.Add(discard);
        AcceptButton = use;
        CancelButton = discard;
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(toolbar, 0, 2);
        layout.Controls.Add(canvasFrame, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);

        Resize += (_, _) => status.MaximumSize = new Size(Math.Max(1, ClientSize.Width - layout.Padding.Horizontal), 0);
        Shown += (_, _) =>
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width),
                Math.Clamp(Top, area.Top, area.Bottom - Height));
            status.MaximumSize = new Size(Math.Max(1, ClientSize.Width - layout.Padding.Horizontal), 0);
            canvas.Select();
        };

        void AddZoom(string label, ScrollPreviewZoom mode)
        {
            var button = MakeButton(label, 70);
            button.Tag = mode;
            button.Click += (_, _) => { canvas.SetView(mode); MarkZoom(mode); canvas.Select(); };
            toolbar.Controls.Add(button);
        }

        void MarkZoom(ScrollPreviewZoom current)
        {
            foreach (Control control in toolbar.Controls)
            {
                if (control is not Button { Tag: ScrollPreviewZoom mode } button) continue;
                bool active = current == mode;
                button.AccessibleDescription = active ? "Current zoom" : null;
                if (highContrast) continue;
                button.BackColor = active ? selected : card;
                button.FlatAppearance.BorderColor = active ? accent : edge;
            }
        }

        Button MakeButton(string label, int width, bool primary = false)
        {
            var button = new Button
            {
                Text = label, AutoSize = true, MinimumSize = new Size(width, 32),
                Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 6, 0)
            };
            if (highContrast) return button;
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = primary ? accent : card;
            button.ForeColor = primary ? accentText : text;
            button.FlatAppearance.BorderColor = primary ? accent : edge;
            button.FlatAppearance.MouseOverBackColor = primary
                ? (dark ? Color.FromArgb(159, 211, 255) : Color.FromArgb(0, 89, 168))
                : selected;
            return button;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            reviewIcon?.Dispose();
            summaryFont?.Dispose();
        }
    }

    private static Icon? TryLoadReviewIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
        if (!File.Exists(path)) return null;
        try { return new Icon(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("Scrolling capture icon unavailable: " + error.Message);
            return null;
        }
    }

    private enum ScrollPreviewZoom { Width, Image, Actual }

    private sealed class ScrollingPreviewCanvas : ScrollableControl
    {
        private readonly Bitmap image;
        private readonly int[] joinRows;
        private ScrollPreviewZoom view;
        private bool showJoins;
        private bool arranging;
        private float zoom = 1;
        private int ViewMargin => LogicalToDeviceUnits(10);

        internal ScrollingPreviewCanvas(Bitmap image, int[] joinRows)
        {
            this.image = image;
            this.joinRows = joinRows;
            AutoScroll = true;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        internal void SetView(ScrollPreviewZoom mode)
        {
            view = mode;
            AutoScrollPosition = Point.Empty;
            ArrangeImage();
        }

        internal void SetJoinVisibility(bool enabled)
        {
            showJoins = enabled;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ArrangeImage();
        }

        private void ArrangeImage()
        {
            if (arranging || image is null) return;
            arranging = true;
            try
            {
                int padding = ViewMargin * 2;
                int width = Math.Max(1, Width - padding - SystemInformation.VerticalScrollBarWidth);
                int height = Math.Max(1, Height - padding - SystemInformation.HorizontalScrollBarHeight);
                zoom = view switch
                {
                    ScrollPreviewZoom.Actual => 1,
                    ScrollPreviewZoom.Image => Math.Min(1, Math.Min((float)width / image.Width, (float)height / image.Height)),
                    _ => Math.Min(1, (float)width / image.Width)
                };
                AutoScrollMinSize = new Size((int)Math.Ceiling(image.Width * zoom) + padding,
                    (int)Math.Ceiling(image.Height * zoom) + padding);
                Invalidate();
            }
            finally { arranging = false; }
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown
            || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = LogicalToDeviceUnits(30);
            Point offset = AutoScrollPosition;
            int x = -offset.X, y = -offset.Y;
            switch (e.KeyCode)
            {
                case Keys.Up: y -= step; break;
                case Keys.Down: y += step; break;
                case Keys.Left: x -= step; break;
                case Keys.Right: x += step; break;
                case Keys.PageUp: y -= ClientSize.Height; break;
                case Keys.PageDown: y += ClientSize.Height; break;
                default: base.OnKeyDown(e); return;
            }
            AutoScrollPosition = new Point(Math.Max(0, x), Math.Max(0, y));
            e.Handled = true;
            Invalidate();
        }

        protected override void OnScroll(ScrollEventArgs e)
        {
            base.OnScroll(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (zoom <= 0) return;
            Point offset = AutoScrollPosition;
            float imageWidth = image.Width * zoom;
            float left = Math.Max(ViewMargin, (ClientSize.Width - imageWidth) / 2) + offset.X;
            float top = ViewMargin + offset.Y;
            RectangleF visible = RectangleF.Intersect(new RectangleF(left, top, imageWidth, image.Height * zoom),
                ClientRectangle);
            if (visible.Width <= 0 || visible.Height <= 0) return;
            RectangleF source = new((visible.Left - left) / zoom, (visible.Top - top) / zoom,
                visible.Width / zoom, visible.Height / zoom);
            e.Graphics.InterpolationMode = zoom == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(image, visible, source, GraphicsUnit.Pixel);
            if (!showJoins || joinRows.Length == 0) return;
            using var outline = new Pen(Color.FromArgb(46, 46, 46), 3);
            using var marker = new Pen(Color.FromArgb(60, 203, 255), 2) { DashStyle = DashStyle.Dash };
            foreach (int row in joinRows)
            {
                float y = top + row * zoom;
                if (y < visible.Top || y > visible.Bottom) continue;
                e.Graphics.DrawLine(outline, visible.Left, y, visible.Right, y);
                e.Graphics.DrawLine(marker, visible.Left, y, visible.Right, y);
            }
        }
    }
}
