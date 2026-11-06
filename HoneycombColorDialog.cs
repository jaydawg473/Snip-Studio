using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class HoneycombColorDialog : Form
    {
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly HoneycombGrid honeycomb;
        private readonly BrightnessSlider brightness;
        private readonly Label brightnessLabel, validation;
        private readonly NumericUpDown red, green, blue;
        private readonly TextBox hex;
        private readonly ColorSample currentSample, newSample;
        private readonly StudioButton apply;
        private bool updating;
        private double hue, saturation;

        internal Color SelectedColor { get; private set; }

        internal HoneycombColorDialog(Color initialColor)
        {
            SelectedColor = Color.FromArgb(255, initialColor.R, initialColor.G, initialColor.B);
            Text = "More colors";
            Font = OwnFont("Segoe UI", 9.5f, FontStyle.Regular);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            ClientSize = new Size(456, 648);
            MainForm.TrySetAppIcon(this);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(24, 12, 24, 16), BackColor = StudioTheme.Surface };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            apply = new StudioButton { Text = "Use color", Primary = true, Size = new Size(122, 38), Margin = new Padding(8, 0, 0, 0), AccessibleName = "Use this exact color" };
            apply.Click += delegate { if (apply.Enabled) { DialogResult = DialogResult.OK; Close(); } };
            StudioButton cancel = new StudioButton { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 38), Margin = Padding.Empty };
            actions.Controls.Add(apply);
            actions.Controls.Add(cancel);
            footer.Controls.Add(actions);
            Controls.Add(footer);
            AcceptButton = apply;
            CancelButton = cancel;

            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 18, 24, 0), BackColor = StudioTheme.Surface };
            Controls.Add(scroll);
            scroll.BringToFront();
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(body);
            AddRow(body, LabelFor("Find your color", 17f, FontStyle.Bold, StudioTheme.Text, new Padding(0, 0, 0, 5)));
            AddRow(body, LabelFor("Pick a hexagon, then fine-tune the color below.", 9.5f, FontStyle.Regular, StudioTheme.Muted, new Padding(0, 0, 0, 10)));

            honeycomb = new HoneycombGrid { Dock = DockStyle.Top, Height = 260, Margin = new Padding(0, 0, 0, 10), CurrentColor = SelectedColor, AccessibleName = "Color honeycomb", AccessibleDescription = "Use arrow keys to choose a color. Home selects the center. Enter or Space chooses the focused hexagon." };
            honeycomb.ColorChosen += delegate { SetColor(honeycomb.CurrentColor, true, false); };
            tips.SetToolTip(honeycomb, "Choose a hexagon. Use arrow keys, Enter, or Space with the keyboard.");
            AddRow(body, honeycomb);

            brightnessLabel = LabelFor("Brightness", 9.5f, FontStyle.Bold, StudioTheme.Text, new Padding(0, 0, 0, 2));
            AddRow(body, brightnessLabel);
            brightness = new BrightnessSlider { Dock = DockStyle.Top, Height = 32, Margin = new Padding(0, 0, 0, 12), AccessibleName = "Color brightness", AccessibleDescription = "Arrow keys adjust brightness. Home is black; End is full brightness." };
            brightness.ValueChanged += delegate
            {
                if (!updating) SetColor(ColorMath.FromHsv(hue, saturation, brightness.Value), false, false);
            };
            tips.SetToolTip(brightness, "Adjust brightness. Arrow keys adjust by one percent.");
            AddRow(body, brightness);

            TableLayoutPanel channels = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 2, Height = 58, Padding = Padding.Empty, Margin = new Padding(0, 0, 0, 10) };
            for (int i = 0; i < 3; i++) channels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            channels.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            channels.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            red = MakeChannel("Red", channels, 0);
            green = MakeChannel("Green", channels, 1);
            blue = MakeChannel("Blue", channels, 2);
            AddRow(body, channels);

            TableLayoutPanel exact = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, Height = 60, Margin = Padding.Empty, Padding = Padding.Empty };
            exact.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            exact.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            exact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            currentSample = new ColorSample { Dock = DockStyle.Fill, Color = SelectedColor, Caption = "Current", Margin = new Padding(0, 0, 8, 0), AccessibleName = "Current color " + Hex(SelectedColor) };
            newSample = new ColorSample { Dock = DockStyle.Fill, Color = SelectedColor, Caption = "New", Margin = new Padding(0, 0, 18, 0), AccessibleName = "New color " + Hex(SelectedColor) };
            exact.Controls.Add(currentSample, 0, 0);
            exact.Controls.Add(newSample, 1, 0);
            TableLayoutPanel hexArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            hexArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            hexArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            hexArea.Controls.Add(new Label { Text = "Hex color", AutoSize = true, ForeColor = StudioTheme.Text, Margin = Padding.Empty }, 0, 0);
            hex = new TextBox { Text = Hex(SelectedColor), Font = OwnFont("Consolas", 12f, FontStyle.Regular), BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Top, Margin = Padding.Empty, MaxLength = 7, BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, AccessibleName = "Exact hex color", AccessibleDescription = "Enter #RRGGBB, or three digit #RGB shorthand." };
            hexArea.Controls.Add(hex, 0, 1);
            exact.Controls.Add(hexArea, 2, 0);
            AddRow(body, exact);
            validation = LabelFor("RGB values are 0–255. Hex accepts #RRGGBB.", 9f, FontStyle.Regular, StudioTheme.Muted, new Padding(0, 7, 0, 5));
            AddRow(body, validation);

            red.TextChanged += ReadChannels;
            green.TextChanged += ReadChannels;
            blue.TextChanged += ReadChannels;
            hex.TextChanged += delegate
            {
                if (updating) return;
                Color color;
                if (ColorPaletteDialog.TryParseHex(hex.Text, out color)) SetColor(color, true, true);
                else ShowInvalid("Use a hex color such as #0078D4.");
            };
            SetColor(SelectedColor, true, false);
            Shown += delegate
            {
                StudioTheme.ApplyTo(this);
                FitToWorkingArea();
                honeycomb.Focus();
            };
        }

        private NumericUpDown MakeChannel(string caption, TableLayoutPanel row, int column)
        {
            row.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = Padding.Empty, ForeColor = StudioTheme.Text }, column, 0);
            NumericUpDown channel = new ColorChannel { Minimum = 0, Maximum = 255, DecimalPlaces = 0, Dock = DockStyle.Top, Margin = new Padding(0, 0, column == 2 ? 0 : 12, 0), BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, AccessibleName = caption + " channel", ThousandsSeparator = false };
            row.Controls.Add(channel, column, 1);
            return channel;
        }

        private void ReadChannels(object sender, EventArgs e)
        {
            if (updating) return;
            int r, g, b;
            if (!Channel(red.Text, out r) || !Channel(green.Text, out g) || !Channel(blue.Text, out b))
            {
                ShowInvalid("Enter a whole number from 0 to 255 for each RGB value.");
                return;
            }
            SetColor(Color.FromArgb(r, g, b), true, false, true);
        }

        private static bool Channel(string text, out int channel)
        {
            return Int32.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out channel) && channel >= 0 && channel <= 255;
        }

        private void SetColor(Color color, bool updateHue, bool keepHex)
        {
            SetColor(color, updateHue, keepHex, false);
        }

        private void SetColor(Color color, bool updateHue, bool keepHex, bool keepChannels)
        {
            SelectedColor = Color.FromArgb(255, color.R, color.G, color.B);
            double value;
            double colorHue, colorSaturation;
            ColorMath.ToHsv(SelectedColor, out colorHue, out colorSaturation, out value);
            if (updateHue) { hue = colorHue; saturation = colorSaturation; }
            updating = true;
            try
            {
                honeycomb.CurrentColor = SelectedColor;
                brightness.FullColor = ColorMath.FromHsv(hue, saturation, 1);
                brightness.Value = value;
                brightnessLabel.Text = "Brightness  " + Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + "%";
                if (!keepChannels) { red.Value = SelectedColor.R; green.Value = SelectedColor.G; blue.Value = SelectedColor.B; }
                if (!keepHex) hex.Text = Hex(SelectedColor);
                newSample.Color = SelectedColor;
                newSample.AccessibleName = "New color " + Hex(SelectedColor);
                validation.Text = "RGB values are 0–255. Hex accepts #RRGGBB.";
                validation.ForeColor = StudioTheme.Muted;
                apply.Enabled = true;
            }
            finally { updating = false; }
        }

        private void ShowInvalid(string message)
        {
            apply.Enabled = false;
            validation.Text = message;
            validation.ForeColor = StudioTheme.IsDark ? Color.FromArgb(253, 162, 155) : Color.FromArgb(180, 35, 24);
        }

        private void FitToWorkingArea()
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            float scale;
            using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
            int margin = Math.Max(12, (int)Math.Round(12 * scale));
            int width = Math.Min(Width, Math.Max(280, work.Width - margin * 2));
            int height = Math.Min(Height, Math.Max(240, work.Height - margin * 2));
            Bounds = new Rectangle(Math.Max(work.Left + margin, Math.Min(Left, work.Right - width - margin)),
                Math.Max(work.Top + margin, Math.Min(Top, work.Bottom - height - margin)), width, height);
        }

        private Font OwnFont(string family, float size, FontStyle style)
        {
            Font font = new Font(family, size, style);
            ownedFonts.Add(font);
            return font;
        }
        private Label LabelFor(string text, float size, FontStyle style, Color ink, Padding margin)
        {
            return new Label { Text = text, Font = OwnFont("Segoe UI", size, style), AutoSize = true, Dock = DockStyle.Top, ForeColor = ink, Margin = margin, UseMnemonic = false };
        }
        private static void AddRow(TableLayoutPanel body, Control control)
        {
            int row = body.RowCount++;
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(control, 0, row);
        }
        private static string Hex(Color color)
        {
            return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture) + color.G.ToString("X2", CultureInfo.InvariantCulture) + color.B.ToString("X2", CultureInfo.InvariantCulture);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
            if (disposing) foreach (Font font in ownedFonts) font.Dispose();
        }

        private sealed class ColorChannel : NumericUpDown
        {
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x020A) return;
                base.WndProc(ref message);
            }
        }

        private sealed class ColorSample : Control
        {
            private Color color;
            internal Color Color { get { return color; } set { color = value; Invalidate(); } }
            internal string Caption;
            internal ColorSample()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Tag = "PreserveColor";
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(StudioTheme.Surface);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = graphics.DpiX / 96f;
                int labelHeight = Math.Max(Font.Height + 3, (int)Math.Round(20 * scale));
                RectangleF bounds = new RectangleF(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - labelHeight - 3));
                using (GraphicsPath path = StudioTheme.Round(bounds, 5 * scale))
                using (Brush fill = new SolidBrush(color))
                using (Pen border = new Pen(StudioTheme.Border, scale))
                {
                    graphics.FillPath(fill, path);
                    graphics.DrawPath(border, path);
                }
                TextRenderer.DrawText(graphics, Caption ?? "", Font, new Rectangle(0, Height - labelHeight, Width, labelHeight), StudioTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        private sealed class HoneycombGrid : Control
        {
            private sealed class Cell
            {
                internal int Q, R;
                internal Color Color;
                internal PointF Center;
                internal PointF[] Polygon;
            }
            private readonly List<Cell> cells = new List<Cell>();
            private int hovered = -1, focusedCell = -1, selectedCell = -1;
            private Color currentColor;
            private float radius;
            internal event EventHandler ColorChosen;
            internal Color CurrentColor
            {
                get { return currentColor; }
                set
                {
                    currentColor = value;
                    selectedCell = -1;
                    for (int i = 0; i < cells.Count; i++) if (cells[i].Color.ToArgb() == value.ToArgb()) { selectedCell = i; break; }
                    if (focusedCell < 0 || !Focused) focusedCell = selectedCell >= 0 ? selectedCell : ClosestColor(value);
                    AccessibleDescription = "Current color " + Hex(value) + ". Arrow keys browse; Enter or Space chooses a hexagon.";
                    Invalidate();
                }
            }
            internal HoneycombGrid()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.List;
                Tag = "PreserveColor";
                for (int r = -6; r <= 6; r++)
                    for (int q = Math.Max(-6, -r - 6); q <= Math.Min(6, -r + 6); q++)
                    {
                        double x = Math.Sqrt(3) * (q + r / 2.0), y = 1.5 * r;
                        double distance = Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(q + r))) / 6.0;
                        double angle = (Math.Atan2(y, x) * 180 / Math.PI + 300 + 360) % 360;
                        Color color = distance == 0 ? Color.White : ColorMath.FromHsl(angle, Math.Min(1, distance * 1.25), 1 - distance * 0.73);
                        cells.Add(new Cell { Q = q, R = r, Color = color });
                    }
                CurrentColor = Color.White;
            }
            private void LayoutCells()
            {
                float padding = Math.Max(6, Math.Min(Width, Height) * 0.025f);
                radius = Math.Max(1, Math.Min((Width - padding * 2) / (13 * (float)Math.Sqrt(3)), (Height - padding * 2) / 20f));
                float cx = Width / 2f, cy = Height / 2f;
                foreach (Cell cell in cells)
                {
                    cell.Center = new PointF(cx + (float)(Math.Sqrt(3) * (cell.Q + cell.R / 2.0)) * radius, cy + 1.5f * cell.R * radius);
                    cell.Polygon = Polygon(cell.Center, radius - Math.Max(0.3f, radius * 0.025f));
                }
            }
            protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutCells(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(StudioTheme.Surface);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (cells.Count == 0) return;
                if (cells[0].Polygon == null) LayoutCells();
                foreach (Cell cell in cells)
                    using (Brush fill = new SolidBrush(cell.Color)) graphics.FillPolygon(fill, cell.Polygon);
                if (hovered >= 0 && hovered != selectedCell) DrawRing(graphics, hovered, false);
                if (selectedCell >= 0) DrawRing(graphics, selectedCell, true);
                if (Focused && focusedCell >= 0 && focusedCell != selectedCell) DrawRing(graphics, focusedCell, false);
            }
            private void DrawRing(Graphics graphics, int index, bool selected)
            {
                float scale = graphics.DpiX / 96f;
                PointF[] polygon = Polygon(cells[index].Center, radius - Math.Max(0.6f, scale));
                using (Pen halo = new Pen(Color.White, 3.4f * scale)) graphics.DrawPolygon(halo, polygon);
                using (Pen outline = new Pen(Color.FromArgb(20, 28, 40), (selected ? 1.7f : 1.1f) * scale))
                {
                    if (!selected) outline.DashStyle = DashStyle.Dot;
                    graphics.DrawPolygon(outline, polygon);
                }
            }
            private static PointF[] Polygon(PointF center, float side)
            {
                PointF[] points = new PointF[6];
                for (int i = 0; i < 6; i++)
                {
                    double angle = (-90 + i * 60) * Math.PI / 180;
                    points[i] = new PointF(center.X + (float)Math.Cos(angle) * side, center.Y + (float)Math.Sin(angle) * side);
                }
                return points;
            }
            private int Hit(Point point)
            {
                if (cells.Count == 0) return -1;
                if (cells[0].Polygon == null) LayoutCells();
                for (int i = cells.Count - 1; i >= 0; i--)
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddPolygon(cells[i].Polygon);
                        if (path.IsVisible(point)) return i;
                    }
                return -1;
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int hit = Hit(e.Location);
                if (hit == hovered) return;
                hovered = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                Focus();
                int hit = Hit(e.Location);
                if (hit >= 0) Choose(hit);
            }
            protected override void OnGotFocus(EventArgs e) { if (focusedCell < 0) focusedCell = ClosestColor(currentColor); Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.Enter || key == Keys.Space || base.IsInputKey(keyData);
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                int key = (int)e.KeyCode;
                if (key == (int)Keys.Home) { focusedCell = Find(0, 0); Choose(focusedCell); }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Choose(focusedCell);
                else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    if (cells[0].Polygon == null) LayoutCells();
                    float dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                    float dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
                    int next = Neighbor(focusedCell, dx, dy);
                    if (next >= 0) Choose(next);
                }
                else return;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            private int Neighbor(int index, float dx, float dy)
            {
                if (index < 0) return ClosestColor(currentColor);
                PointF start = cells[index].Center;
                int next = -1;
                double best = Double.MaxValue;
                for (int i = 0; i < cells.Count; i++)
                {
                    float x = cells[i].Center.X - start.X, y = cells[i].Center.Y - start.Y;
                    float forward = x * dx + y * dy;
                    if (forward < radius * 0.5f) continue;
                    float side = Math.Abs(x * dy - y * dx);
                    double score = forward + side * 3;
                    if (score < best) { best = score; next = i; }
                }
                return next;
            }
            private int Find(int q, int r)
            {
                for (int i = 0; i < cells.Count; i++) if (cells[i].Q == q && cells[i].R == r) return i;
                return -1;
            }
            private int ClosestColor(Color color)
            {
                int nearest = 0;
                long best = Int64.MaxValue;
                for (int i = 0; i < cells.Count; i++)
                {
                    long r = color.R - cells[i].Color.R, g = color.G - cells[i].Color.G, b = color.B - cells[i].Color.B;
                    long distance = r * r + g * g + b * b;
                    if (distance < best) { best = distance; nearest = i; }
                }
                return nearest;
            }
            private void Choose(int index)
            {
                if (index < 0 || index >= cells.Count) return;
                focusedCell = index;
                CurrentColor = cells[index].Color;
                EventHandler changed = ColorChosen;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }

        private sealed class BrightnessSlider : Control
        {
            private double value = 1;
            private Color fullColor = Color.White;
            private bool dragging;
            internal event EventHandler ValueChanged;
            internal Color FullColor { get { return fullColor; } set { fullColor = value; Invalidate(); } }
            internal double Value
            {
                get { return value; }
                set { this.value = Math.Max(0, Math.Min(1, value)); AccessibleDescription = Math.Round(this.value * 100) + " percent brightness"; Invalidate(); }
            }
            internal BrightnessSlider()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.Slider;
                Tag = "PreserveColor";
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(StudioTheme.Surface);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = graphics.DpiX / 96f, inset = 9 * scale;
                RectangleF track = new RectangleF(inset, Height / 2f - 4 * scale, Math.Max(1, Width - inset * 2), 8 * scale);
                using (GraphicsPath path = StudioTheme.Round(track, 4 * scale))
                using (LinearGradientBrush fill = new LinearGradientBrush(track, Color.Black, fullColor, LinearGradientMode.Horizontal))
                using (Pen outline = new Pen(StudioTheme.Border, scale))
                {
                    graphics.FillPath(fill, path);
                    graphics.DrawPath(outline, path);
                }
                float x = track.Left + (float)value * track.Width;
                RectangleF thumb = new RectangleF(x - 7 * scale, Height / 2f - 7 * scale, 14 * scale, 14 * scale);
                using (Brush fill = new SolidBrush(StudioTheme.Surface)) graphics.FillEllipse(fill, thumb);
                using (Pen outline = new Pen(Focused ? StudioTheme.Accent : StudioTheme.Text, 2 * scale)) graphics.DrawEllipse(outline, thumb);
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                Focus(); dragging = true; Capture = true; SetFromMouse(e.X);
            }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromMouse(e.X); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { dragging = false; Capture = false; } }
            protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) dragging = false; base.OnMouseCaptureChanged(e); }
            private void SetFromMouse(int x)
            {
                float scale;
                using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
                SetValue((x - 9 * scale) / Math.Max(1, Width - 18 * scale));
            }
            private void SetValue(double next)
            {
                double old = value;
                Value = next;
                if (Math.Abs(old - value) < 0.0001) return;
                EventHandler changed = ValueChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Home) SetValue(0);
                else if (e.KeyCode == Keys.End) SetValue(1);
                else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) SetValue(value - 0.01);
                else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) SetValue(value + 0.01);
                else return;
                e.Handled = true; e.SuppressKeyPress = true;
            }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        }

        private static class ColorMath
        {
            internal static Color FromHsl(double hue, double saturation, double lightness)
            {
                double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
                return FromChroma(hue, chroma, lightness - chroma / 2);
            }
            internal static Color FromHsv(double hue, double saturation, double value)
            {
                return FromChroma(hue, value * saturation, value * (1 - saturation));
            }
            private static Color FromChroma(double hue, double chroma, double offset)
            {
                hue = (hue % 360 + 360) % 360;
                double x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
                double r = 0, g = 0, b = 0;
                if (hue < 60) { r = chroma; g = x; }
                else if (hue < 120) { r = x; g = chroma; }
                else if (hue < 180) { g = chroma; b = x; }
                else if (hue < 240) { g = x; b = chroma; }
                else if (hue < 300) { r = x; b = chroma; }
                else { r = chroma; b = x; }
                return Color.FromArgb(Byte(r + offset), Byte(g + offset), Byte(b + offset));
            }
            private static int Byte(double value) { return Math.Max(0, Math.Min(255, (int)Math.Round(value * 255))); }
            internal static void ToHsv(Color color, out double hue, out double saturation, out double value)
            {
                double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
                double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
                double delta = maximum - minimum;
                hue = delta == 0 ? 0 : maximum == r ? 60 * (((g - b) / delta) % 6) : maximum == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
                saturation = maximum == 0 ? 0 : delta / maximum;
                value = maximum;
            }
        }
    }
}
