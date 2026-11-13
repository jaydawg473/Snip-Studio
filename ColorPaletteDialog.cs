using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // Standalone tools and the shape style popup share the same draft-only
    // picker. The exact square expands its spectrum here, never another dialog.
    internal sealed class ColorPaletteDialog : Form
    {
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly ColorPickerPanel picker;
        private readonly StudioButton useColor;
        private readonly Panel scroll, footer;
        private readonly TableLayoutPanel body;
        private bool arranging, presenting;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        internal Color SelectedColor { get; private set; }

        internal ColorPaletteDialog(Color initialColor, IEnumerable<Color> recentColors)
        {
            SelectedColor = Opaque(initialColor);
            Text = "Choose color";
            Font = OwnFont(9.5f, FontStyle.Regular);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(468, 550);
            MainForm.TrySetAppIcon(this);

            footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(24, 12, 24, 16), BackColor = StudioTheme.Surface };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            useColor = new StudioButton { Text = "Use color", Primary = true, Size = new Size(122, 38), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Use selected color" };
            useColor.Click += delegate
            {
                if (!picker.IsValidColor) return;
                SelectedColor = picker.SelectedColor;
                DialogResult = DialogResult.OK;
                Close();
            };
            StudioButton cancel = new StudioButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 38), Margin = Padding.Empty };
            tips.SetToolTip(useColor, "Use this color. Cancel keeps the previous color.");
            tips.SetToolTip(cancel, "Keep the previous color.");
            actions.Controls.Add(useColor); actions.Controls.Add(cancel);
            footer.Controls.Add(actions); Controls.Add(footer);
            AcceptButton = useColor; CancelButton = cancel;

            scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 20, 24, 8), BackColor = StudioTheme.Surface };
            Controls.Add(scroll); scroll.BringToFront();
            body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(body);
            AddRow(new Label { Text = "Choose your color", Font = OwnFont(17f, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top, ForeColor = StudioTheme.Text, Margin = new Padding(0, 0, 0, 6) });
            AddRow(new Label { Text = "Pick a swatch or expand the exact color square to fine-tune it.", AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(420, 0), ForeColor = StudioTheme.Muted, Margin = new Padding(0, 0, 0, 14) });
            picker = new ColorPickerPanel(SelectedColor, recentColors, false, 1f)
            { Dock = DockStyle.Top, Width = 420, Margin = Padding.Empty };
            picker.Height = picker.ContentHeight;
            picker.ColorChanged += delegate
            {
                useColor.Enabled = picker.IsValidColor;
                if (picker.IsValidColor) SelectedColor = picker.SelectedColor;
            };
            picker.ContentHeightChanged += delegate { UpdatePickerLayout(); };
            AddRow(picker);
            Shown += delegate
            {
                StudioTheme.ApplyTo(this);
                FitToWorkingArea();
                Present();
                picker.FocusColorInput();
            };
            Activated += delegate { if (!presenting && TopMost) TopMost = false; };
        }

        private void AddRow(Control control)
        {
            int row = body.RowCount++;
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(control, 0, row);
        }

        private void UpdatePickerLayout()
        {
            if (arranging || picker == null || body == null) return;
            arranging = true;
            try { picker.Height = picker.ContentHeight; body.PerformLayout(); }
            finally { arranging = false; }
            if (Visible && IsHandleCreated) FitToWorkingArea();
        }

        internal static Rectangle FitDialogBounds(Rectangle desired, Rectangle work)
        {
            if (work.Width < 1 || work.Height < 1) throw new ArgumentOutOfRangeException("work");
            int marginX = Math.Min(12, work.Width / 8), marginY = Math.Min(12, work.Height / 8);
            int width = Math.Min(Math.Max(1, desired.Width), Math.Max(1, work.Width - marginX * 2));
            int height = Math.Min(Math.Max(1, desired.Height), Math.Max(1, work.Height - marginY * 2));
            int x = Math.Max(work.Left + marginX, Math.Min(desired.Left, work.Right - width - marginX));
            int y = Math.Max(work.Top + marginY, Math.Min(desired.Top, work.Bottom - height - marginY));
            return new Rectangle(x, y, width, height);
        }

        private void FitToWorkingArea()
        {
            if (arranging || !IsHandleCreated) return;
            arranging = true;
            try
            {
                Form owner = Owner;
                Rectangle work = Screen.FromControl(owner != null && !owner.IsDisposed ? owner : this).WorkingArea;
                using (Graphics graphics = CreateGraphics()) picker.SetDisplayScale(graphics.DpiX / 96f);
                // One outer scrolling surface keeps the footer reachable even
                // when the inline spectrum is taller than the monitor work area.
                for (int pass = 0; pass < 2; pass++)
                {
                    picker.Height = picker.ContentHeight;
                    body.PerformLayout();
                    int frame = Height - ClientSize.Height;
                    int wanted = body.PreferredSize.Height + scroll.Padding.Vertical + footer.Height + frame;
                    Bounds = FitDialogBounds(new Rectangle(Left, Top, Width, wanted), work);
                }
            }
            finally { arranging = false; }
        }

        private void Present()
        {
            if (!Visible || !IsHandleCreated || IsDisposed || Disposing || presenting) return;
            presenting = true;
            try
            {
                TopMost = true;
                BringToFront(); Activate();
                if (SetForegroundWindow(Handle)) TopMost = false;
            }
            finally { presenting = false; }
        }

        private Font OwnFont(float size, FontStyle style)
        {
            Font font = new Font("Segoe UI", size, style);
            ownedFonts.Add(font);
            return font;
        }

        private static Color Opaque(Color color) { return Color.FromArgb(255, color.R, color.G, color.B); }

        internal static bool TryParseHex(string value, out Color color)
        {
            color = Color.Black;
            string hex = (value ?? "").Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal)) hex = hex.Substring(1);
            if (hex.Length == 3) hex = new string(new char[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            int rgb;
            if (hex.Length != 6 || !Int32.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb)) return false;
            color = Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (TopMost) TopMost = false; tips.Dispose(); }
            base.Dispose(disposing);
            if (disposing) { foreach (Font font in ownedFonts) font.Dispose(); ownedFonts.Clear(); }
        }
    }
}
