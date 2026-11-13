using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    // A draft-only picker shared by shape popups and the standalone color form.
    // Expanding the exact-color controls never creates another window.
    internal sealed class ColorPickerPanel : UserControl
    {
        private readonly bool allowNone;
        private readonly List<PaletteSwatch> swatches = new List<PaletteSwatch>();
        private readonly List<PaletteSwatch> recentSwatches = new List<PaletteSwatch>();
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly Font textFont, boldFont, hexFont;
        private readonly Label paletteLabel, recentLabel, exactLabel, validation, spectrumLabel, brightnessLabel;
        private readonly Label redLabel, greenLabel, blueLabel;
        private readonly ExactColorButton exactPreview;
        private readonly TextBox hexInput;
        private readonly StudioButton spectrumToggle;
        private readonly HoneycombColorDialog.HoneycombGrid honeycomb;
        private readonly HoneycombColorDialog.BrightnessSlider brightness;
        private readonly NumericUpDown red, green, blue;
        private float scale;
        private bool initialized, layingOut, scaling, updating, valid = true, spectrumExpanded;
        private int contentHeight, columns = 6;
        private Color selectedColor, lastColor = Color.Black;
        private double hue, saturation;

        internal event EventHandler ColorChanged;
        internal event EventHandler ContentHeightChanged;
        internal bool IsValidColor { get { return valid; } }
        internal int ContentHeight { get { return contentHeight; } }
        internal bool SpectrumExpanded { get { return spectrumExpanded; } }
        internal Color SelectedColor
        {
            get { return selectedColor; }
            set { SetColor(value, true, false, false); }
        }

        internal ColorPickerPanel(Color initialColor, IEnumerable<Color> recentColors, bool allowNone, float scale = 1f)
        {
            this.allowNone = allowNone;
            this.scale = NormalizeScale(scale);
            AutoScaleMode = AutoScaleMode.None;
            AutoScroll = false;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            textFont = new Font("Segoe UI", 9.5f);
            boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            hexFont = new Font("Consolas", 10.5f);
            Font = textFont;
            AccessibleName = "Color palette and exact color";

            paletteLabel = AddLabel("Palette", true);
            string[] names = { "White", "Pale red", "Pale orange", "Pale yellow", "Pale green", "Pale cyan", "Pale blue", "Pale purple",
                "Light gray", "Light red", "Light orange", "Light yellow", "Light green", "Light cyan", "Light blue", "Light purple",
                "Slate", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Violet",
                "Black", "Dark red", "Dark orange", "Brown", "Dark green", "Dark cyan", "Dark blue", "Dark purple" };
            string[] colors = { "#FFFFFF", "#FEE2E2", "#FFEDD5", "#FEF9C3", "#DCFCE7", "#CFFAFE", "#DBEAFE", "#F3E8FF",
                "#CBD5E1", "#FDA4AF", "#FDBA74", "#FDE047", "#86EFAC", "#67E8F9", "#93C5FD", "#D8B4FE",
                "#64748B", "#DC2626", "#F97316", "#FACC15", "#22C55E", "#06B6D4", "#0078D4", "#9333EA",
                "#000000", "#991B1B", "#9A3412", "#854D0E", "#166534", "#155E75", "#1E40AF", "#6B21A8" };
            if (allowNone) swatches.Add(AddSwatch(Color.Empty, "None", swatches));
            for (int index = 0; index < colors.Length; index++)
                swatches.Add(AddSwatch(ColorTranslator.FromHtml(colors[index]), names[index], swatches));
            recentLabel = AddLabel("Recent colors", true);
            HashSet<int> seen = new HashSet<int>();
            int inspectedRecent = 0;
            if (recentColors != null)
                foreach (Color color in recentColors)
                {
                    if (inspectedRecent++ == 64) break;
                    if (color.IsEmpty) continue;
                    Color opaque = Opaque(color);
                    if (!seen.Add(opaque.ToArgb())) continue;
                    recentSwatches.Add(AddSwatch(opaque, "Recent color", recentSwatches));
                    if (recentSwatches.Count == 8) break;
                }
            recentLabel.Visible = recentSwatches.Count != 0;

            exactLabel = AddLabel("Exact color", true);
            exactPreview = new ExactColorButton { AccessibleName = "Expand exact color spectrum", Tag = "PreserveColor" };
            exactPreview.Click += delegate { SetSpectrumExpanded(!spectrumExpanded); };
            Controls.Add(exactPreview);
            tips.SetToolTip(exactPreview, "Show or hide the color spectrum, brightness and RGB controls below.");
            hexInput = new TextBox { BorderStyle = BorderStyle.FixedSingle, MaxLength = 7, Font = hexFont,
                BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, AccessibleName = "Hex color",
                AccessibleDescription = allowNone ? "Enter #RRGGBB, #RGB, or None." : "Enter #RRGGBB or #RGB." };
            hexInput.TextChanged += delegate { ReadHex(); };
            Controls.Add(hexInput);
            tips.SetToolTip(hexInput, allowNone ? "Enter a hex color such as #0078D4, or choose None." : "Enter a hex color such as #0078D4. Three-digit shorthand is also accepted.");
            validation = AddLabel("", false);
            spectrumToggle = new StudioButton { Text = "More colors", AccessibleName = "Expand color spectrum" };
            spectrumToggle.Click += delegate { SetSpectrumExpanded(!spectrumExpanded); };
            Controls.Add(spectrumToggle);
            tips.SetToolTip(spectrumToggle, "Expand the exact-color controls in this same palette.");
            spectrumLabel = AddLabel("Color spectrum", true);
            honeycomb = new HoneycombColorDialog.HoneycombGrid { AccessibleName = "Color spectrum", Tag = "PreserveColor" };
            honeycomb.ColorChosen += delegate { if (!updating) SetColor(honeycomb.CurrentColor, true, false, false); };
            Controls.Add(honeycomb);
            tips.SetToolTip(honeycomb, "Choose a hexagon. Arrow keys browse; Enter or Space selects.");
            brightnessLabel = AddLabel("Brightness", true);
            brightness = new HoneycombColorDialog.BrightnessSlider { AccessibleName = "Color brightness", Tag = "PreserveColor" };
            brightness.ValueChanged += delegate
            {
                if (!updating) SetColor(HoneycombColorDialog.ColorMath.FromHsv(hue, saturation, brightness.Value), false, false, false);
            };
            Controls.Add(brightness);
            tips.SetToolTip(brightness, "Adjust brightness. Arrow keys change it by one percent.");
            redLabel = AddLabel("Red", false); greenLabel = AddLabel("Green", false); blueLabel = AddLabel("Blue", false);
            red = AddChannel("Red"); green = AddChannel("Green"); blue = AddChannel("Blue");
            red.TextChanged += ReadChannels; green.TextChanged += ReadChannels; blue.TextChanged += ReadChannels;
            initialized = true;
            Width = S(276);
            SetColor(initialColor, true, false, false);
            SetSpectrumExpanded(false);
            LayoutContent();
        }

        internal void SetDisplayScale(float value)
        {
            float next = NormalizeScale(value);
            if (Math.Abs(next - scale) < .001f) return;
            scale = next;
            LayoutContent();
        }

        internal void SetSpectrumExpanded(bool expanded)
        {
            spectrumExpanded = expanded;
            spectrumToggle.Text = expanded ? "Hide spectrum" : "More colors";
            spectrumToggle.AccessibleName = expanded ? "Collapse color spectrum" : "Expand color spectrum";
            exactPreview.AccessibleName = expanded ? "Collapse exact color spectrum" : "Expand exact color spectrum";
            foreach (Control control in new Control[] { spectrumLabel, honeycomb, brightnessLabel, brightness, redLabel, greenLabel, blueLabel, red, green, blue })
                control.Visible = expanded;
            LayoutContent();
        }

        internal void FocusColorInput()
        {
            hexInput.Focus();
            hexInput.SelectAll();
        }

        private Label AddLabel(string text, bool bold)
        {
            Label label = new Label { Text = text, AutoSize = false, Font = bold ? boldFont : textFont,
                BackColor = StudioTheme.Surface, ForeColor = bold ? StudioTheme.Text : StudioTheme.Muted,
                UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(label);
            return label;
        }

        private PaletteSwatch AddSwatch(Color color, string name, List<PaletteSwatch> group)
        {
            PaletteSwatch swatch = new PaletteSwatch(color) { AccessibleName = color.IsEmpty ? "None" : name + ", " + Hex(color), Tag = "PreserveColor" };
            swatch.Click += delegate { SelectedColor = swatch.Color; };
            swatch.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                int current = group.IndexOf(swatch), next = current;
                if (e.KeyCode == Keys.Left) next--;
                else if (e.KeyCode == Keys.Right) next++;
                else if (e.KeyCode == Keys.Up) next -= columns;
                else if (e.KeyCode == Keys.Down) next += columns;
                else if (e.KeyCode == Keys.Home) next = 0;
                else if (e.KeyCode == Keys.End) next = group.Count - 1;
                else return;
                if (next >= 0 && next < group.Count) group[next].Focus();
                e.Handled = true; e.SuppressKeyPress = true;
            };
            Controls.Add(swatch);
            tips.SetToolTip(swatch, color.IsEmpty ? "No color for this channel." : name + "  " + Hex(color));
            return swatch;
        }

        private NumericUpDown AddChannel(string name)
        {
            NumericUpDown channel = new NoWheelChannel { Minimum = 0, Maximum = 255, DecimalPlaces = 0,
                Font = textFont, BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text,
                AccessibleName = name + " channel", ThousandsSeparator = false };
            Controls.Add(channel);
            tips.SetToolTip(channel, name + " value from 0 to 255. Scrolling does not change this value.");
            return channel;
        }

        private void ReadHex()
        {
            if (updating) return;
            if (allowNone && String.Equals(hexInput.Text.Trim(), "None", StringComparison.OrdinalIgnoreCase))
            { SetColor(Color.Empty, false, true, false); return; }
            Color parsed;
            if (ColorPaletteDialog.TryParseHex(hexInput.Text, out parsed)) SetColor(parsed, true, true, false);
            else SetInvalid(allowNone ? "Use #RRGGBB, #RGB, or None." : "Use #RRGGBB or #RGB.");
        }

        private void ReadChannels(object sender, EventArgs e)
        {
            if (updating) return;
            int r, g, b;
            if (!ReadChannel(red.Text, out r) || !ReadChannel(green.Text, out g) || !ReadChannel(blue.Text, out b))
            { SetInvalid("RGB values must be whole numbers from 0 to 255."); return; }
            SetColor(Color.FromArgb(r, g, b), true, false, true);
        }

        private static bool ReadChannel(string value, out int result)
        {
            return Int32.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0 && result <= 255;
        }

        private void SetInvalid(string message)
        {
            bool changed = valid;
            valid = false;
            validation.Text = message;
            validation.ForeColor = StudioTheme.IsDark ? Color.FromArgb(253, 162, 155) : Color.FromArgb(180, 35, 24);
            if (changed) RaiseColorChanged();
        }

        private void SetColor(Color value, bool updateHue, bool keepHex, bool keepChannels)
        {
            Color next = value.IsEmpty && allowNone ? Color.Empty : Opaque(value);
            bool changed = !valid || next.IsEmpty != selectedColor.IsEmpty || next.ToArgb() != selectedColor.ToArgb();
            selectedColor = next;
            valid = true;
            if (!next.IsEmpty) lastColor = next;
            double colorHue, colorSaturation, brightnessValue;
            HoneycombColorDialog.ColorMath.ToHsv(lastColor, out colorHue, out colorSaturation, out brightnessValue);
            if (updateHue) { hue = colorHue; saturation = colorSaturation; }
            updating = true;
            try
            {
                if (!keepHex) hexInput.Text = next.IsEmpty ? "None" : Hex(next);
                if (!keepChannels) { red.Value = lastColor.R; green.Value = lastColor.G; blue.Value = lastColor.B; }
                honeycomb.CurrentColor = lastColor;
                brightness.FullColor = HoneycombColorDialog.ColorMath.FromHsv(hue, saturation, 1);
                brightness.Value = brightnessValue;
                brightnessLabel.Text = "Brightness  " + Math.Round(brightnessValue * 100).ToString(CultureInfo.CurrentCulture) + "%";
                exactPreview.Color = next;
                exactPreview.AccessibleDescription = next.IsEmpty ? "No color. Expand the spectrum to choose a color." : Hex(next) + ". Expand the spectrum to fine-tune this color.";
                validation.Text = next.IsEmpty ? "No color for this channel." : "Hex color, for example #0078D4";
                validation.ForeColor = StudioTheme.Muted;
                foreach (PaletteSwatch swatch in swatches) RefreshSwatch(swatch);
                foreach (PaletteSwatch swatch in recentSwatches) RefreshSwatch(swatch);
            }
            finally { updating = false; }
            if (changed) RaiseColorChanged();
        }

        private void RefreshSwatch(PaletteSwatch swatch)
        {
            swatch.Selected = swatch.Color.IsEmpty ? selectedColor.IsEmpty : !selectedColor.IsEmpty && swatch.Color.ToArgb() == selectedColor.ToArgb();
            swatch.AccessibleDescription = swatch.Selected ? "Selected color" : "Choose this color";
            swatch.Invalidate();
        }

        private void RaiseColorChanged()
        {
            EventHandler handler = ColorChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void LayoutContent()
        {
            if (!initialized || layingOut || scaling || IsDisposed) return;
            layingOut = true;
            int previous = contentHeight;
            try
            {
                int width = Math.Max(1, ClientSize.Width), y = 0;
                columns = width >= S(340) ? 8 : width >= S(235) ? 6 : 4;
                columns = Math.Min(columns, Math.Max(1, width / Math.Max(1, S(20))));
                paletteLabel.SetBounds(0, y, width, S(22)); y += S(26);
                y = LayoutSwatches(swatches, y, width); y += S(8);
                if (recentSwatches.Count != 0)
                {
                    recentLabel.SetBounds(0, y, width, S(22)); y += S(25);
                    y = LayoutSwatches(recentSwatches, y, width); y += S(8);
                }
                exactLabel.SetBounds(0, y, width, S(22)); y += S(25);
                if (width < S(160))
                {
                    exactPreview.SetBounds(0, y, width, S(38)); y += S(43);
                    hexInput.SetBounds(0, y, width, S(28)); y += S(32);
                }
                else
                {
                    int previewWidth = Math.Min(S(44), Math.Max(1, width / 4));
                    exactPreview.SetBounds(0, y, previewWidth, S(38));
                    int inputX = Math.Min(width - 1, previewWidth + S(10));
                    hexInput.SetBounds(inputX, y + S(5), Math.Max(1, width - inputX), S(28)); y += S(42);
                }
                validation.SetBounds(0, y, width, S(38)); y += S(41);
                spectrumToggle.SetBounds(0, y, width, S(30)); y += S(34);
                if (spectrumExpanded)
                {
                    spectrumLabel.SetBounds(0, y, width, S(22)); y += S(24);
                    int gridHeight = Math.Max(S(140), Math.Min(S(240), (int)Math.Round(width * .88)));
                    honeycomb.SetBounds(0, y, width, gridHeight); y += gridHeight + S(6);
                    brightnessLabel.SetBounds(0, y, width, S(22)); y += S(22);
                    brightness.SetBounds(0, y, width, S(32)); y += S(38);
                    if (width < S(180))
                    {
                        Label[] labels = { redLabel, greenLabel, blueLabel };
                        NumericUpDown[] channels = { red, green, blue };
                        for (int index = 0; index < channels.Length; index++)
                        {
                            labels[index].SetBounds(0, y, width, S(20)); y += S(20);
                            channels[index].SetBounds(0, y, width, S(28)); y += S(34);
                        }
                    }
                    else
                    {
                        int gap = S(10), channelWidth = Math.Max(1, (width - gap * 2) / 3);
                        redLabel.SetBounds(0, y, channelWidth, S(20));
                        greenLabel.SetBounds(channelWidth + gap, y, channelWidth, S(20));
                        blueLabel.SetBounds((channelWidth + gap) * 2, y, Math.Max(1, width - (channelWidth + gap) * 2), S(20)); y += S(20);
                        red.SetBounds(0, y, channelWidth, S(28));
                        green.SetBounds(channelWidth + gap, y, channelWidth, S(28));
                        blue.SetBounds((channelWidth + gap) * 2, y, Math.Max(1, width - (channelWidth + gap) * 2), S(28)); y += S(34);
                    }
                }
                contentHeight = Math.Max(1, y);
                if (Height != contentHeight) Height = contentHeight;
            }
            finally { layingOut = false; }
            if (previous != contentHeight)
            {
                EventHandler handler = ContentHeightChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        private int LayoutSwatches(List<PaletteSwatch> list, int y, int width)
        {
            int cellWidth = Math.Max(1, width / columns), rowHeight = S(34);
            for (int index = 0; index < list.Count; index++)
            {
                int column = index % columns;
                int left = column * cellWidth;
                int available = column == columns - 1 ? width - left : cellWidth;
                list[index].SetBounds(left, y + (index / columns) * rowHeight, Math.Max(1, available), rowHeight);
            }
            return y + ((list.Count + columns - 1) / columns) * rowHeight;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutContent();
        }

        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            // Let a parent Form scale existing child bounds once. Subsequent
            // expansion/resize uses the same updated metric scale.
            scaling = true;
            try
            {
                if (initialized)
                {
                    scale = NormalizeScale(scale * factor.Width);
                    contentHeight = Math.Max(1, (int)Math.Round(contentHeight * factor.Height));
                }
                base.ScaleControl(factor, specified);
            }
            finally { scaling = false; }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(proposedSize.Width > 0 ? proposedSize.Width : Width, Math.Max(1, contentHeight));
        }

        private static Color Opaque(Color color) { return Color.FromArgb(255, color.R, color.G, color.B); }
        private static string Hex(Color color) { return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture) + color.G.ToString("X2", CultureInfo.InvariantCulture) + color.B.ToString("X2", CultureInfo.InvariantCulture); }
        private static float NormalizeScale(float value) { return Single.IsNaN(value) || Single.IsInfinity(value) ? 1f : Math.Max(.5f, Math.Min(4f, value)); }
        private int S(int value) { return Math.Max(1, (int)Math.Round(value * scale)); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
            if (disposing) { textFont.Dispose(); boldFont.Dispose(); hexFont.Dispose(); }
        }

        private sealed class NoWheelChannel : NumericUpDown
        {
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x020A) return;
                base.WndProc(ref message);
            }
        }

        private sealed class PaletteSwatch : Button
        {
            internal Color Color { get; private set; }
            internal bool Selected;
            internal PaletteSwatch(Color color)
            {
                Color = color;
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                BackColor = StudioTheme.Surface; UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                AccessibleRole = AccessibleRole.PushButton;
            }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Enter || key == Keys.Space || key == Keys.Home || key == Keys.End ||
                    key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                { PerformClick(); e.Handled = true; e.SuppressKeyPress = true; }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(StudioTheme.Surface); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float side = Math.Max(1, Math.Min(Width, Height) * .72f);
                RectangleF bounds = new RectangleF((Width - side) / 2, (Height - side) / 2, side, side);
                using (Brush fill = new SolidBrush(Color.IsEmpty ? StudioTheme.Surface : Color)) graphics.FillEllipse(fill, bounds);
                using (Pen border = new Pen(Color.IsEmpty ? StudioTheme.Text : StudioTheme.Border, 1)) graphics.DrawEllipse(border, bounds);
                if (Color.IsEmpty)
                    using (Pen slash = new Pen(StudioTheme.Text, Math.Max(1, side / 14))) graphics.DrawLine(slash, bounds.Left + side * .22f, bounds.Bottom - side * .22f, bounds.Right - side * .22f, bounds.Top + side * .22f);
                if (Selected || Focused)
                {
                    bounds.Inflate(Math.Max(1, side * .1f), Math.Max(1, side * .1f));
                    using (Pen ring = new Pen(StudioTheme.Accent, Math.Max(1, side / 14))) graphics.DrawEllipse(ring, bounds);
                }
            }
        }

        private sealed class ExactColorButton : Button
        {
            private Color color;
            internal Color Color { get { return color; } set { color = value; Invalidate(); } }
            internal ExactColorButton()
            {
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                AccessibleRole = AccessibleRole.PushButton;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(StudioTheme.Surface); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF bounds = new RectangleF(2, 2, Math.Max(1, Width - 4), Math.Max(1, Height - 4));
                using (GraphicsPath path = StudioTheme.Round(bounds, Math.Min(7, Math.Min(Width, Height) / 5f)))
                using (Brush fill = new SolidBrush(color.IsEmpty ? StudioTheme.Surface : color))
                using (Pen border = new Pen(Focused ? StudioTheme.Accent : StudioTheme.Border, Focused ? 2 : 1))
                { graphics.FillPath(fill, path); graphics.DrawPath(border, path); }
                if (color.IsEmpty)
                    using (Pen slash = new Pen(StudioTheme.Text, 2)) graphics.DrawLine(slash, 5, Height - 5, Width - 5, 5);
            }
        }
    }
}
