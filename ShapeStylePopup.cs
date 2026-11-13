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
        private readonly bool allowChannelChange;
        private readonly float scale;
        private readonly Panel content;
        private readonly ToolStripControlHost host;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly ColorPickerPanel picker;
        private readonly TrackBar opacity, size;
        private readonly Label opacityCaption, sizeCaption, opacityValue, sizeValue;
        private readonly StylePreview preview;
        private readonly StudioButton apply, cancel;
        private Size fullSize;
        private Rectangle workingArea;
        private bool arranging;
        private Color chosenColor;
        private bool applied;
        private float draftWidth;
        private bool embedded;

        internal Panel EmbeddedContent { get { return content; } }
        internal Size PreferredEmbeddedSize { get { return fullSize; } }
        internal event EventHandler DraftFinished;
        internal void PrepareEmbeddedLayout()
        {
            embedded = true;
            workingArea = Rectangle.Empty;
            content.AutoScrollMinSize = Size.Empty;
            content.AutoScroll = false;
            content.Size = fullSize;
        }

        internal ShapeStylePopup(ShapeStyle current, bool fillChannel, bool allowFill,
            Action<ShapeStyle> accepted, float scale, IWin32Window dialogOwner = null)
            : this(current, fillChannel, allowFill, accepted, scale, dialogOwner, null) { }

        internal ShapeStylePopup(ShapeStyle current, bool fillChannel, bool allowFill,
            Action<ShapeStyle> accepted, float scale, IWin32Window dialogOwner, IEnumerable<Color> recentColors)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (accepted == null) throw new ArgumentNullException("accepted");
            original = current; this.fillChannel = fillChannel; this.accepted = accepted;
            this.scale = Math.Max(.75f, Math.Min(4f, scale));
            allowChannelChange = !fillChannel || allowFill;
            chosenColor = fillChannel ? current.FillColor : current.OutlineColor;
            draftWidth = current.OutlineWidth;
            AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty;
            BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text;
            AccessibleName = fillChannel ? "Shape fill colors and opacity" : "Shape outline colors, opacity and size";
            content = new Panel { BackColor = StudioTheme.Surface, ForeColor = StudioTheme.Text,
                AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty };
            content.Font = OwnFont(9.5f, FontStyle.Regular);
            content.Size = new Size(S(320), S(480));
            int x = S(14), usable = S(292), y = S(12);
            Label title = LabelAt(fillChannel ? "Fill" : "Outline", x, y, usable, S(24));
            title.Font = OwnFont(10f, FontStyle.Bold); y += S(30);
            picker = new ColorPickerPanel(chosenColor, recentColors, true, this.scale)
            { Location = new Point(x, y), Width = usable, Enabled = allowChannelChange };
            picker.Height = picker.ContentHeight;
            picker.ColorChanged += delegate { chosenColor = picker.SelectedColor; UpdateDraft(); };
            picker.ContentHeightChanged += delegate { ArrangeDraft(); };
            content.Controls.Add(picker); y += picker.Height + S(10);
            opacityCaption = LabelAt("Opacity", x, y, S(180), S(22));
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
                sizeCaption = LabelAt("Size", x, y, S(180), S(22));
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
            cancel = ButtonAt("Cancel", x, y, S(104), S(34), false);
            cancel.Click += delegate { FinishDraft(ToolStripDropDownCloseReason.CloseCalled); };
            apply = ButtonAt("Apply", x + usable - S(112), y, S(112), S(34), true);
            apply.Enabled = allowChannelChange;
            apply.Click += delegate {
                ShapeStyle result;
                if (!TryAcceptDraft(out result)) return;
                FinishDraft(ToolStripDropDownCloseReason.ItemClicked); this.accepted(result);
            };
            tips.SetToolTip(apply, "Apply this style once. Undo restores the selected shape's previous style.");
            y += S(46);
            fullSize = new Size(S(320), y);
            content.Size = fullSize; content.AutoScrollMinSize = new Size(S(292), fullSize.Height);
            host = new ToolStripControlHost(content) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = fullSize };
            Items.Add(host); Size = fullSize;
            UpdateDraft(); ArrangeDraft();
        }

        internal void FitToWorkingArea(Rectangle work)
        {
            picker.SetDisplayScale(scale);
            workingArea = work;
            content.Size = host.Size = Size = new Size(
                Math.Min(fullSize.Width, Math.Max(1, work.Width - S(8))),
                Math.Min(fullSize.Height, Math.Max(1, work.Height - S(8))));
        }
        private void ArrangeDraft()
        {
            if (arranging || picker == null || opacity == null || preview == null || apply == null || host == null) return;
            arranging = true;
            try
            {
                Point scroll = content.AutoScrollPosition;
                content.AutoScrollPosition = Point.Empty;
                int x = S(14), usable = S(292), y = S(42);
                picker.SetBounds(x, y, usable, picker.ContentHeight);
                picker.Height = picker.ContentHeight;
                y += picker.Height + S(10);
                opacityCaption.SetBounds(x, y, usable - S(78), S(22));
                opacityValue.SetBounds(x + usable - S(74), y, S(74), S(22)); y += S(22);
                opacity.SetBounds(x - S(6), y, usable + S(10), S(28)); y += S(34);
                if (size != null)
                {
                    sizeCaption.SetBounds(x, y, usable - S(78), S(22));
                    sizeValue.SetBounds(x + usable - S(74), y, S(74), S(22)); y += S(22);
                    size.SetBounds(x - S(6), y, usable + S(10), S(28)); y += S(34);
                }
                preview.SetBounds(x, y, usable, S(48)); y += S(56);
                cancel.SetBounds(x, y, S(104), S(34));
                apply.SetBounds(x + usable - S(112), y, S(112), S(34)); y += S(46);
                fullSize = new Size(S(320), y);
                if (embedded)
                {
                    content.AutoScrollMinSize = Size.Empty;
                    content.AutoScroll = false;
                    content.Size = fullSize;
                }
                else
                {
                    content.AutoScrollMinSize = new Size(S(292), fullSize.Height);
                    if (workingArea.Width > 0 && workingArea.Height > 0) FitToWorkingArea(workingArea);
                    else content.Size = host.Size = Size = fullSize;
                    content.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
                }
                if (Visible && workingArea.Width > 0 && workingArea.Height > 0)
                    Location = new Point(Math.Max(workingArea.Left, Math.Min(Left, workingArea.Right - Width)),
                        Math.Max(workingArea.Top, Math.Min(Top, workingArea.Bottom - Height)));
            }
            finally { arranging = false; }
        }
        internal bool TryAcceptDraft(out ShapeStyle result)
        {
            result = null;
            if (applied || !allowChannelChange || !picker.IsValidColor) return false;
            result = DraftStyle;
            applied = true;
            return true;
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
            if (apply != null) apply.Enabled = allowChannelChange && picker.IsValidColor;
            if (preview != null) { preview.Style = DraftStyle; preview.Invalidate(); }
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
        private void FinishDraft(ToolStripDropDownCloseReason reason)
        {
            Close(reason);
            EventHandler handler = DraftFinished;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { FinishDraft(ToolStripDropDownCloseReason.Keyboard); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { tips.Dispose(); foreach (Font font in ownedFonts) font.Dispose(); ownedFonts.Clear(); }
        }

        private sealed class NoWheelTrackBar : TrackBar
        { protected override void OnMouseWheel(MouseEventArgs e) { } }
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
