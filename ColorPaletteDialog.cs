using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class ColorPaletteDialog : Form
    {
        private readonly List<ColorSwatch> swatches = new List<ColorSwatch>();
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly TextBox hexInput;
        private readonly Label validation;
        private readonly ColorPreview preview;
        private readonly StudioButton useColor;
        private bool settingHex;

        internal Color SelectedColor { get; private set; }

        internal ColorPaletteDialog(Color initialColor, IEnumerable<Color> recentColors)
        {
            SelectedColor = Opaque(initialColor);
            Text = "Choose color";
            Font = OwnFont(10f, FontStyle.Regular);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(468, 592);
            MainForm.TrySetAppIcon(this);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(24, 12, 24, 16), BackColor = StudioTheme.Surface };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            useColor = new StudioButton { Text = "Use color", Primary = true, Size = new Size(122, 38), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Use selected color" };
            useColor.Click += delegate
            {
                Color color;
                if (!TryParseHex(hexInput.Text, out color)) return;
                SelectedColor = color;
                DialogResult = DialogResult.OK;
                Close();
            };
            StudioButton cancel = new StudioButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 38), Margin = Padding.Empty };
            actions.Controls.Add(useColor);
            actions.Controls.Add(cancel);
            footer.Controls.Add(actions);
            Controls.Add(footer);
            AcceptButton = useColor;
            CancelButton = cancel;

            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 20, 24, 0), BackColor = StudioTheme.Surface };
            Controls.Add(scroll);
            scroll.BringToFront();
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(body);
            AddRow(body, TextLabel("Make it clear.", 17f, FontStyle.Bold, StudioTheme.Text, new Padding(0, 0, 0, 6)));
            AddRow(body, TextLabel("Choose a swatch or enter an exact color.", 9.5f, FontStyle.Regular, StudioTheme.Muted, new Padding(0, 0, 0, 20)));
            AddRow(body, TextLabel("Palette", 10f, FontStyle.Bold, StudioTheme.Text, new Padding(0, 0, 0, 6)));

            string[] colors =
            {
                "#FFFFFF", "#FEE2E2", "#FFEDD5", "#FEF9C3", "#DCFCE7", "#CFFAFE", "#DBEAFE", "#F3E8FF",
                "#CBD5E1", "#FCA5A5", "#FDBA74", "#FDE047", "#86EFAC", "#67E8F9", "#93C5FD", "#D8B4FE",
                "#64748B", "#DC2626", "#F97316", "#FACC15", "#22C55E", "#06B6D4", "#0078D4", "#9333EA",
                "#000000", "#991B1B", "#9A3412", "#854D0E", "#166534", "#155E75", "#1E40AF", "#6B21A8"
            };
            string[] names =
            {
                "White", "Pale red", "Pale orange", "Pale yellow", "Pale green", "Pale cyan", "Pale blue", "Pale purple",
                "Light gray", "Light red", "Light orange", "Light yellow", "Light green", "Light cyan", "Light blue", "Light purple",
                "Slate gray", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple",
                "Black", "Dark red", "Dark orange", "Dark yellow", "Dark green", "Dark cyan", "Dark blue", "Dark purple"
            };
            TableLayoutPanel palette = SwatchGrid(4);
            palette.Margin = new Padding(0, 0, 0, 18);
            for (int i = 0; i < colors.Length; i++)
            {
                Color color;
                TryParseHex(colors[i], out color);
                ColorSwatch swatch = MakeSwatch(color, names[i], palette, i, 8);
                palette.Controls.Add(swatch, i % 8, i / 8);
            }
            AddRow(body, palette);

            List<Color> recent = DistinctRecent(recentColors);
            AddRow(body, TextLabel("Recent colors", 10f, FontStyle.Bold, StudioTheme.Text, new Padding(0, 0, 0, 6)));
            if (recent.Count > 0)
            {
                TableLayoutPanel recentGrid = SwatchGrid(1);
                recentGrid.Margin = new Padding(0, 0, 0, 18);
                for (int i = 0; i < recent.Count; i++)
                    recentGrid.Controls.Add(MakeSwatch(recent[i], "Recent color", recentGrid, i, 8), i, 0);
                AddRow(body, recentGrid);
            }
            else
                AddRow(body, TextLabel("Colors you use will appear here.", 9.5f, FontStyle.Regular, StudioTheme.Muted, new Padding(0, 0, 0, 20)));

            AddRow(body, TextLabel("Exact color", 10f, FontStyle.Bold, StudioTheme.Text, new Padding(0, 0, 0, 8)));
            TableLayoutPanel exact = new TableLayoutPanel { Dock = DockStyle.Top, Height = 54, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            exact.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            exact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            preview = new ColorPreview { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Color = SelectedColor, AccessibleName = "Choose an exact color", AccessibleDescription = "Open the honeycomb color picker." };
            tips.SetToolTip(preview, "More colors — open the honeycomb color picker");
            preview.Click += delegate
            {
                using (HoneycombColorDialog picker = new HoneycombColorDialog(SelectedColor))
                    if (picker.ShowDialog(this) == DialogResult.OK) Choose(picker.SelectedColor);
            };
            exact.Controls.Add(preview, 0, 0);
            hexInput = new TextBox { Text = Hex(SelectedColor), Font = OwnFont(12f, FontStyle.Regular, "Consolas"), BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Top, MaxLength = 7, Margin = new Padding(0, 10, 0, 0), BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, AccessibleName = "Hex color", AccessibleDescription = "Enter a six digit RGB hex color, such as #0078D4. Three digit shorthand is also accepted." };
            exact.Controls.Add(hexInput, 1, 0);
            AddRow(body, exact);
            validation = TextLabel("Hex color, for example #0078D4", 9f, FontStyle.Regular, StudioTheme.Muted, new Padding(0, 7, 0, 8));
            validation.AccessibleRole = AccessibleRole.StaticText;
            AddRow(body, validation);
            hexInput.TextChanged += delegate { ReadHex(); };

            RefreshSwatches();
            Shown += delegate
            {
                StudioTheme.ApplyTo(this);
                FitToWorkingArea();
                ColorSwatch selected = swatches.Find(delegate(ColorSwatch swatch) { return swatch.Selected; });
                if (selected != null) selected.Focus(); else hexInput.Focus();
            };
        }

        private void FitToWorkingArea()
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int margin = Math.Max(12, (int)Math.Round(12 * DeviceScale()));
            int width = Math.Min(Width, Math.Max(280, work.Width - margin * 2));
            int height = Math.Min(Height, Math.Max(240, work.Height - margin * 2));
            Bounds = new Rectangle(Math.Max(work.Left + margin, Math.Min(Left, work.Right - width - margin)),
                Math.Max(work.Top + margin, Math.Min(Top, work.Bottom - height - margin)), width, height);
        }

        private float DeviceScale()
        {
            using (Graphics graphics = CreateGraphics()) return graphics.DpiX / 96f;
        }

        private Font OwnFont(float size, FontStyle style) { return OwnFont(size, style, "Segoe UI"); }
        private Font OwnFont(float size, FontStyle style, string family)
        {
            Font font = new Font(family, size, style);
            ownedFonts.Add(font);
            return font;
        }

        private Label TextLabel(string text, float size, FontStyle style, Color color, Padding margin)
        {
            return new Label { Text = text, AutoSize = true, Dock = DockStyle.Top, Font = OwnFont(size, style), ForeColor = color, Margin = margin, UseMnemonic = false };
        }

        private static void AddRow(TableLayoutPanel body, Control control)
        {
            int row = body.RowCount++;
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(control, 0, row);
        }

        private static TableLayoutPanel SwatchGrid(int rows)
        {
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 8, RowCount = rows, Height = rows * 44, Margin = Padding.Empty, Padding = Padding.Empty };
            for (int i = 0; i < 8; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));
            for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            return grid;
        }

        private ColorSwatch MakeSwatch(Color color, string name, TableLayoutPanel grid, int index, int columns)
        {
            ColorSwatch swatch = new ColorSwatch(color) { Dock = DockStyle.Fill, Margin = new Padding(2), AccessibleName = name + ", " + Hex(color) };
            tips.SetToolTip(swatch, name + "  " + Hex(color));
            swatch.Click += delegate { Choose(swatch.Color); };
            swatch.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                int target = index;
                if (e.KeyCode == Keys.Left) target--;
                else if (e.KeyCode == Keys.Right) target++;
                else if (e.KeyCode == Keys.Up) target -= columns;
                else if (e.KeyCode == Keys.Down) target += columns;
                else return;
                Control next = target < 0 || target >= grid.Controls.Count ? null : grid.GetControlFromPosition(target % columns, target / columns);
                if (next != null) next.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            swatches.Add(swatch);
            return swatch;
        }

        private void Choose(Color color)
        {
            settingHex = true;
            try { hexInput.Text = Hex(color); }
            finally { settingHex = false; }
            SelectedColor = Opaque(color);
            ApplyValidColor();
        }

        private void ReadHex()
        {
            if (settingHex) return;
            Color color;
            if (TryParseHex(hexInput.Text, out color))
            {
                SelectedColor = color;
                ApplyValidColor();
            }
            else
            {
                useColor.Enabled = false;
                validation.Text = "Use a hex color such as #0078D4.";
                validation.ForeColor = StudioTheme.IsDark ? Color.FromArgb(253, 162, 155) : Color.FromArgb(180, 35, 24);
            }
        }

        private void ApplyValidColor()
        {
            preview.Color = SelectedColor;
            preview.AccessibleDescription = Hex(SelectedColor) + ". Open the honeycomb color picker.";
            useColor.Enabled = true;
            validation.Text = "Hex color, for example #0078D4";
            validation.ForeColor = StudioTheme.Muted;
            RefreshSwatches();
        }

        private void RefreshSwatches()
        {
            foreach (ColorSwatch swatch in swatches)
            {
                swatch.Selected = swatch.Color.ToArgb() == SelectedColor.ToArgb();
                swatch.AccessibleDescription = swatch.Selected ? "Selected color" : "Choose this color";
                swatch.Invalidate();
            }
        }

        private static Color Opaque(Color color) { return Color.FromArgb(255, color.R, color.G, color.B); }
        private static string Hex(Color color) { return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture) + color.G.ToString("X2", CultureInfo.InvariantCulture) + color.B.ToString("X2", CultureInfo.InvariantCulture); }

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

        private static List<Color> DistinctRecent(IEnumerable<Color> recentColors)
        {
            List<Color> recent = new List<Color>();
            HashSet<int> seen = new HashSet<int>();
            if (recentColors != null)
                foreach (Color color in recentColors)
                {
                    if (color.IsEmpty) continue;
                    Color opaque = Opaque(color);
                    if (seen.Add(opaque.ToArgb())) recent.Add(opaque);
                    if (recent.Count == 8) break;
                }
            return recent;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
            if (disposing) foreach (Font font in ownedFonts) font.Dispose();
        }

        private sealed class ColorPreview : Button
        {
            private Color color;
            internal Color Color { get { return color; } set { color = value; Invalidate(); } }
            internal ColorPreview()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                Cursor = Cursors.Hand;
                TabStop = true;
                AccessibleRole = AccessibleRole.PushButton;
                Tag = "PreserveColor";
            }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(StudioTheme.Surface);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = e.Graphics.DpiX / 96f;
                RectangleF bounds = new RectangleF(2 * scale, 2 * scale, Math.Max(1, Width - 4 * scale - 1), Math.Max(1, Height - 4 * scale - 1));
                using (GraphicsPath shape = StudioTheme.Round(bounds, 8 * scale))
                using (Brush fill = new SolidBrush(color))
                using (Pen border = new Pen(Focused ? StudioTheme.Accent : StudioTheme.Border, (Focused ? 2 : 1) * scale))
                {
                    e.Graphics.FillPath(fill, shape);
                    e.Graphics.DrawPath(border, shape);
                }
                double light = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
                using (Brush ink = new SolidBrush(light > 150 ? System.Drawing.Color.Black : System.Drawing.Color.White))
                    for (int i = 0; i < 3; i++)
                        e.Graphics.FillEllipse(ink, bounds.Right - (15 - i * 4) * scale, bounds.Bottom - 8 * scale, 2 * scale, 2 * scale);
            }
        }

        private sealed class ColorSwatch : Button
        {
            private bool hovered;
            internal Color Color { get; private set; }
            internal bool Selected;
            internal ColorSwatch(Color color)
            {
                Color = color;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.PushButton;
                Tag = "PreserveColor";
                TabStop = true;
            }
            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(StudioTheme.Surface);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = graphics.DpiX / 96f;
                float inset = 4 * scale;
                RectangleF bounds = new RectangleF(inset, inset, Math.Max(1, Width - inset * 2 - 1), Math.Max(1, Height - inset * 2 - 1));
                using (GraphicsPath shape = StudioTheme.Round(bounds, 6 * scale))
                using (Brush fill = new SolidBrush(Color))
                using (Pen border = new Pen(StudioTheme.Border, scale))
                {
                    graphics.FillPath(fill, shape);
                    graphics.DrawPath(border, shape);
                }
                if (Selected || hovered || Focused)
                {
                    RectangleF outer = bounds;
                    outer.Inflate(2 * scale, 2 * scale);
                    using (GraphicsPath shape = StudioTheme.Round(outer, 8 * scale))
                    using (Pen ring = new Pen(Selected ? StudioTheme.Accent : StudioTheme.Muted, (Selected ? 2 : 1) * scale))
                        graphics.DrawPath(ring, shape);
                }
                if (Selected)
                {
                    Color check = Luminance(Color) > 0.179 ? System.Drawing.Color.Black : System.Drawing.Color.White;
                    float cx = bounds.X + bounds.Width / 2, cy = bounds.Y + bounds.Height / 2;
                    using (Pen pen = new Pen(check, 2.2f * scale))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        graphics.DrawLines(pen, new PointF[] { new PointF(cx - 5 * scale, cy), new PointF(cx - 1.5f * scale, cy + 3.5f * scale), new PointF(cx + 5 * scale, cy - 4 * scale) });
                    }
                }
            }
            private static double Luminance(Color color)
            {
                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }
            private static double Linear(byte channel)
            {
                double value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
        }
    }
}
