using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    // The sliders update a local preview; only Apply commits a single Undo step.
    internal sealed class ShapeStylePopup : ToolStripDropDown
    {
        private readonly ShapeStyle original;
        private readonly bool fillChannel;
        private readonly Action<ShapeStyle> accepted;
        private readonly IWin32Window dialogOwner;
        private readonly float scale;
        private readonly Panel content;
        private readonly ToolStripControlHost host;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly List<ColorSwatch> swatches = new List<ColorSwatch>();
        private readonly TrackBar opacity, size;
        private readonly Label opacityValue, sizeValue;
        private readonly StylePreview preview;
        private readonly Size fullSize;
        private Color chosenColor;
        private bool applied;
        private float draftWidth;

        internal ShapeStylePopup(ShapeStyle current, bool fillChannel, bool allowFill,
            Action<ShapeStyle> accepted, float scale, IWin32Window dialogOwner = null)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (accepted == null) throw new ArgumentNullException("accepted");
            original = current; this.fillChannel = fillChannel; this.accepted = accepted;
            this.dialogOwner = dialogOwner; this.scale = Math.Max(.75f, Math.Min(4f, scale));
            chosenColor = fillChannel ? current.FillColor : current.OutlineColor;
            draftWidth = current.OutlineWidth;
            AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty;
            BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text;
            AccessibleName = fillChannel ? "Shape fill colors and opacity" : "Shape outline colors, opacity and size";
            content = new Panel { BackColor = StudioTheme.Surface, ForeColor = StudioTheme.Text,
                AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty };
            content.Font = OwnFont(9.5f, FontStyle.Regular);
            int x = S(14), usable = S(276), y = S(12);
            Label title = LabelAt(fillChannel ? "Fill" : "Outline", x, y, usable, S(24));
            title.Font = OwnFont(10f, FontStyle.Bold); y += S(30);
            string[] names = { "None", "Black", "White", "Light gray", "Gray", "Slate",
                "Magenta", "Red", "Orange", "Amber", "Yellow", "Golden yellow",
                "Lime", "Green", "Teal", "Sky blue", "Blue", "Violet",
                "Purple", "Dark purple", "Peach", "Tan", "Brown", "Dark brown",
                "Pink", "Apricot", "Lemon", "Mint", "Light blue", "Lavender" };
            string[] colors = { "", "#000000", "#FFFFFF", "#D4D4D4", "#A3A3A3", "#737373",
                "#BE185D", "#F01522", "#FF6A00", "#FFAF00", "#FFD400", "#FFF000",
                "#A3E610", "#16D410", "#00B78A", "#00A8D9", "#0755E9", "#3A0BCE",
                "#7300D9", "#7B008E", "#F6D4BF", "#BF9668", "#8B5631", "#654235",
                "#F67BEE", "#FFC77E", "#FEFB7E", "#7AEFB5", "#7BCCF5", "#B7A9FF" };
            for (int i = 0; i < colors.Length; i++)
            {
                Color color = i == 0 ? Color.Empty : ColorTranslator.FromHtml(colors[i]);
                ColorSwatch swatch = new ColorSwatch(color, this.scale) {
                    Bounds = new Rectangle(x + (i % 6) * S(45), y + (i / 6) * S(36), S(34), S(32)),
                    AccessibleName = names[i] + (fillChannel ? " fill" : " outline"),
                    Enabled = !fillChannel || allowFill };
                swatch.Click += delegate(object sender, EventArgs e) { chosenColor = ((ColorSwatch)sender).Color; UpdateDraft(); };
                tips.SetToolTip(swatch, swatch.AccessibleName);
                swatches.Add(swatch); content.Controls.Add(swatch);
            }
            y += S(184);
            StudioButton exact = ButtonAt("More colors…", x, y, usable, S(30), false);
            exact.Enabled = !fillChannel || allowFill;
            exact.Click += delegate { ChooseExactColor(); };
            tips.SetToolTip(exact, "Choose an exact color with the palette, RGB or hex value.");
            y += S(38);
            LabelAt("Opacity", x, y, S(180), S(22));
            opacityValue = LabelAt("", x + usable - S(74), y, S(74), S(22));
            opacityValue.TextAlign = ContentAlignment.MiddleRight; y += S(22);
            opacity = new NoWheelTrackBar { Minimum = 0, Maximum = 100,
                Value = fillChannel ? current.FillOpacity : current.OutlineOpacity,
                TickStyle = TickStyle.None, AutoSize = false,
                Bounds = new Rectangle(x - S(6), y, usable + S(10), S(28)),
                AccessibleName = fillChannel ? "Fill opacity" : "Outline opacity" };
            opacity.ValueChanged += delegate { UpdateDraft(); };
            content.Controls.Add(opacity);
            tips.SetToolTip(opacity, "Opacity from transparent (0%) to solid (100%).");
            y += S(34);
            if (!fillChannel)
            {
                LabelAt("Size", x, y, S(180), S(22));
                sizeValue = LabelAt("", x + usable - S(74), y, S(74), S(22));
                sizeValue.TextAlign = ContentAlignment.MiddleRight; y += S(22);
                size = new NoWheelTrackBar { Minimum = 1, Maximum = Math.Max(40, (int)Math.Ceiling(current.OutlineWidth)),
                    Value = Math.Max(1, (int)Math.Round(current.OutlineWidth)),
                    TickStyle = TickStyle.None, AutoSize = false,
                    Bounds = new Rectangle(x - S(6), y, usable + S(10), S(28)),
                    AccessibleName = "Outline width in image pixels" };
                size.ValueChanged += delegate { draftWidth = size.Value; UpdateDraft(); };
                content.Controls.Add(size);
                tips.SetToolTip(size, "Outline width in image pixels. Exports retain this width regardless of zoom.");
                y += S(34);
            }
            preview = new StylePreview(this.scale) { Bounds = new Rectangle(x, y, usable, S(48)) };
            content.Controls.Add(preview); y += S(56);
            StudioButton cancel = ButtonAt("Cancel", x, y, S(104), S(34), false);
            cancel.Click += delegate { Close(ToolStripDropDownCloseReason.CloseCalled); };
            StudioButton apply = ButtonAt("Apply", x + usable - S(112), y, S(112), S(34), true);
            apply.Enabled = !fillChannel || allowFill;
            apply.Click += delegate {
                if (applied) return;
                ShapeStyle result = DraftStyle; applied = true;
                Close(ToolStripDropDownCloseReason.ItemClicked); this.accepted(result);
            };
            tips.SetToolTip(apply, "Apply this style once. Undo restores the selected shape's previous style.");
            y += S(46);
            fullSize = new Size(S(320), y);
            content.Size = fullSize; content.AutoScrollMinSize = new Size(S(292), fullSize.Height);
            host = new ToolStripControlHost(content) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = fullSize };
            Items.Add(host); Size = fullSize;
            UpdateDraft();
        }

        internal void FitToWorkingArea(Rectangle work)
        {
            content.Size = host.Size = Size = new Size(
                Math.Min(fullSize.Width, Math.Max(1, work.Width - S(8))),
                Math.Min(fullSize.Height, Math.Max(1, work.Height - S(8))));
        }
        private ShapeStyle DraftStyle
        {
            get { return new ShapeStyle(fillChannel ? chosenColor : original.FillColor,
                fillChannel ? original.OutlineColor : chosenColor,
                draftWidth,
                fillChannel ? opacity.Value : original.FillOpacity,
                fillChannel ? original.OutlineOpacity : opacity.Value); }
        }
        private void UpdateDraft()
        {
            if (opacity == null) return;
            opacityValue.Text = opacity.Value.ToString(CultureInfo.CurrentCulture) + "%";
            opacity.Enabled = !chosenColor.IsEmpty;
            if (sizeValue != null) sizeValue.Text = draftWidth.ToString("0.##", CultureInfo.CurrentCulture) + " px";
            foreach (ColorSwatch swatch in swatches)
            {
                swatch.Selected = swatch.Color.IsEmpty ? chosenColor.IsEmpty :
                    !chosenColor.IsEmpty && swatch.Color.ToArgb() == chosenColor.ToArgb();
                swatch.Invalidate();
            }
            if (preview != null) { preview.Style = DraftStyle; preview.Invalidate(); }
        }
        private void ChooseExactColor()
        {
            AutoClose = false;
            try {
                using (ColorPaletteDialog picker = new ColorPaletteDialog(chosenColor.IsEmpty ? Color.Black : chosenColor, new Color[0]))
                    if (picker.ShowDialog(dialogOwner) == DialogResult.OK) { chosenColor = picker.SelectedColor; UpdateDraft(); }
            }
            finally { if (!IsDisposed) { AutoClose = true; content.Focus(); } }
        }
        private Label LabelAt(string text, int x, int y, int width, int height)
        {
            Label label = new Label { Text = text, ForeColor = StudioTheme.Text, BackColor = StudioTheme.Surface,
                AutoSize = false, Bounds = new Rectangle(x, y, width, height), TextAlign = ContentAlignment.MiddleLeft };
            content.Controls.Add(label); return label;
        }
        private StudioButton ButtonAt(string text, int x, int y, int width, int height, bool primary)
        {
            StudioButton button = new StudioButton { Text = text, Primary = primary,
                Bounds = new Rectangle(x, y, width, height), AccessibleName = text };
            content.Controls.Add(button); return button;
        }
        private Font OwnFont(float size, FontStyle style) { Font font = new Font("Segoe UI", size, style); ownedFonts.Add(font); return font; }
        private int S(int value) { return Math.Max(1, (int)Math.Round(value * scale)); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(ToolStripDropDownCloseReason.Keyboard); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { tips.Dispose(); foreach (Font font in ownedFonts) font.Dispose(); ownedFonts.Clear(); }
        }

        private sealed class NoWheelTrackBar : TrackBar
        { protected override void OnMouseWheel(MouseEventArgs e) { } }
        private sealed class ColorSwatch : Button
        {
            internal Color Color { get; private set; }
            internal bool Selected;
            private readonly float scale;
            internal ColorSwatch(Color color, float scale)
            {
                Color = color; this.scale = scale;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                BackColor = StudioTheme.Surface; UseVisualStyleBackColor = false;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(StudioTheme.Surface);
                float side = Math.Min(Width, Height) - 8 * scale;
                RectangleF circle = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
                using (Brush fill = new SolidBrush(Color.IsEmpty ? StudioTheme.Surface : Color)) g.FillEllipse(fill, circle);
                using (Pen outline = new Pen(Color.IsEmpty ? StudioTheme.Text : StudioTheme.Border, scale)) g.DrawEllipse(outline, circle);
                if (Color.IsEmpty)
                    using (Pen slash = new Pen(StudioTheme.Text, 1.6f * scale))
                        g.DrawLine(slash, circle.Left + side * .2f, circle.Bottom - side * .2f, circle.Right - side * .2f, circle.Top + side * .2f);
                if (Selected || Focused)
                {
                    circle.Inflate(3 * scale, 3 * scale);
                    using (Pen ring = new Pen(StudioTheme.Accent, 1.8f * scale)) g.DrawEllipse(ring, circle);
                }
            }
        }
        private sealed class StylePreview : Control
        {
            internal ShapeStyle Style;
            private readonly float scale;
            internal StylePreview(float scale)
            { this.scale = scale; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); AccessibleName = "Shape style preview"; }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics; g.Clear(StudioTheme.Input); g.SmoothingMode = SmoothingMode.AntiAlias;
                int tile = Math.Max(4, (int)Math.Round(6 * scale));
                using (Brush checker = new SolidBrush(StudioTheme.Raised))
                    for (int y = 0; y < Height; y += tile)
                        for (int x = 0; x < Width; x += tile)
                            if ((x / tile + y / tile) % 2 == 0) g.FillRectangle(checker, x, y, tile, tile);
                if (Style == null) return;
                RectangleF box = new RectangleF(14 * scale, 10 * scale, Math.Max(1, Width - 28 * scale), Math.Max(1, Height - 20 * scale));
                if (Style.FillEnabled)
                    using (Brush fill = new SolidBrush(Style.EffectiveFillColor)) g.FillRectangle(fill, box);
                if (Style.OutlineEnabled)
                    using (Pen outline = new Pen(Style.EffectiveOutlineColor, Math.Max(1, Math.Min(14 * scale, Style.OutlineWidth * scale))))
                        g.DrawRectangle(outline, box.X, box.Y, box.Width, box.Height);
            }
        }
    }
}
