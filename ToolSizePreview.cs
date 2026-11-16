using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    // The drawing uses image pixels, independently of the editor's current zoom.
    // Oversize samples are fitted and their exact preview scale is always named.
    internal sealed class ToolSizePreview : Control
    {
        internal enum PreviewKind { Stroke, Highlight, Text }
        private PreviewKind kind;
        private float pixelSize = 4, settingSize = 4, previewScale = 1;
        private string family = "Segoe UI";
        private FontStyle fontStyle;
        private Color sampleColor = Color.Red;
        private bool compact;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private string tooltipText;

        internal PreviewKind Mode { get { return kind; } }
        internal float PixelSize { get { return pixelSize; } }
        internal float PreviewScale { get { return previewScale; } }
        internal bool Compact
        {
            get { return compact; }
            set
            {
                if (compact == value) return;
                compact = value;
                Invalidate();
                if (Parent != null) Parent.PerformLayout();
            }
        }

        internal ToolSizePreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            AccessibleName = "Size preview";
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            if (!compact) return base.GetPreferredSize(proposedSize);
            float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
            return new Size((int)Math.Round(120 * scale), (int)Math.Round(34 * scale));
        }

        internal void UpdateStroke(float setting, Color color, bool highlight)
        {
            settingSize = setting; pixelSize = setting * (highlight ? 5 : 1);
            kind = highlight ? PreviewKind.Highlight : PreviewKind.Stroke; sampleColor = color;
            AccessibleDescription = highlight ? Number(setting) + " pixel setting; " + Number(pixelSize) + " pixel highlighter stroke."
                : Number(pixelSize) + " pixel pen stroke.";
            Invalidate();
        }

        internal void UpdateText(float size, string fontFamily, FontStyle style, Color color)
        {
            pixelSize = settingSize = size; family = String.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily;
            fontStyle = style; sampleColor = color; kind = PreviewKind.Text;
            AccessibleDescription = Number(size) + " image pixel text in " + family + ".";
            Invalidate();
        }

        internal static float CalculateFitScale(SizeF natural, SizeF available)
        {
            if (natural.Width <= 0 || natural.Height <= 0 || available.Width <= 0 || available.Height <= 0) return 1;
            return Math.Min(1f, Math.Min(available.Width / natural.Width, available.Height / natural.Height));
        }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(ToolOptionsAppearance.Background); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float uiScale = graphics.DpiX / 96f;
            if (compact)
            {
                PaintCompact(graphics, uiScale);
                base.OnPaint(e);
                return;
            }
            int inset = Math.Max(6, (int)Math.Round(10 * uiScale));
            int captionHeight = Math.Max(Font.Height * 2 + 4, (int)Math.Round(40 * uiScale));
            RectangleF field = new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = StudioTheme.Round(field, 7 * uiScale))
            using (Brush background = new SolidBrush(ToolOptionsAppearance.Field))
            using (Pen border = new Pen(ToolOptionsAppearance.Line, Math.Max(1, uiScale)))
            { graphics.FillPath(background, path); graphics.DrawPath(border, path); }
            RectangleF sample = new RectangleF(inset, inset, Math.Max(1, Width - inset * 2), Math.Max(1, Height - inset * 2 - captionHeight));
            // A neutral surface keeps both white and black ink visible without
            // adding an outline that would exaggerate the selected size.
            using (Brush neutral = new SolidBrush(Color.FromArgb(144, 151, 160))) graphics.FillRectangle(neutral, sample);
            PaintSample(graphics, sample, false);
            string value = SizeDescription();
            string scale = ScaleDescription();
            string caption = value + Environment.NewLine + scale;
            TextRenderer.DrawText(graphics, caption, Font, new Rectangle(inset, Height - inset - captionHeight, Math.Max(1, Width - inset * 2), captionHeight),
                ToolOptionsAppearance.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            UpdateAccessiblePreview(value, scale);
            base.OnPaint(e);
        }

        private void PaintCompact(Graphics graphics, float uiScale)
        {
            int inset = Math.Max(2, (int)Math.Round(4 * uiScale));
            int scaleWidth = Math.Max(24, (int)Math.Round(34 * uiScale));
            RectangleF field = new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = StudioTheme.Round(field, 4 * uiScale))
            using (Brush background = new SolidBrush(ToolOptionsAppearance.Field))
            using (Pen border = new Pen(ToolOptionsAppearance.Line, Math.Max(1, uiScale)))
            { graphics.FillPath(background, path); graphics.DrawPath(border, path); }
            RectangleF sample = new RectangleF(inset, inset, Math.Max(1, Width - inset * 3 - scaleWidth), Math.Max(1, Height - inset * 2));
            using (Brush neutral = new SolidBrush(Color.FromArgb(144, 151, 160))) graphics.FillRectangle(neutral, sample);
            PaintSample(graphics, sample, true);
            string fit = previewScale >= .9995f ? "1:1" : (previewScale * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
            TextRenderer.DrawText(graphics, fit, Font,
                new Rectangle((int)Math.Ceiling(sample.Right) + inset, 0, Math.Max(1, Width - (int)Math.Ceiling(sample.Right) - inset * 2), Height),
                ToolOptionsAppearance.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            UpdateAccessiblePreview(SizeDescription(), ScaleDescription());
        }

        private void PaintSample(Graphics graphics, RectangleF sample, bool small)
        {
            if (kind == PreviewKind.Text)
            {
                using (Font actual = SampleFont(family, pixelSize, fontStyle))
                using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    string text = small ? "Aa" : "Aa Sample";
                    SizeF natural = graphics.MeasureString(text, actual, Int32.MaxValue, format);
                    previewScale = CalculateFitScale(natural, sample.Size);
                    GraphicsState state = graphics.Save();
                    graphics.SetClip(sample);
                    graphics.TranslateTransform(sample.X + (sample.Width - natural.Width * previewScale) / 2,
                        sample.Y + (sample.Height - natural.Height * previewScale) / 2);
                    graphics.ScaleTransform(previewScale, previewScale);
                    using (Brush brush = new SolidBrush(sampleColor)) graphics.DrawString(text, actual, brush, 0, 0, format);
                    graphics.Restore(state);
                }
            }
            else
            {
                float naturalLength = Math.Max(40, sample.Width - pixelSize);
                previewScale = CalculateFitScale(new SizeF(naturalLength + pixelSize, pixelSize + 2), sample.Size);
                float lineWidth = pixelSize * previewScale;
                float half = lineWidth / 2;
                Color ink = kind == PreviewKind.Highlight ? Color.FromArgb(80, sampleColor.R, sampleColor.G, sampleColor.B) : sampleColor;
                using (Pen pen = new Pen(ink, Math.Max(.1f, lineWidth)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    graphics.DrawLine(pen, sample.Left + half, sample.Top + sample.Height / 2, sample.Right - half, sample.Top + sample.Height / 2);
                }
            }
        }

        private string SizeDescription()
        {
            return kind == PreviewKind.Text ? Number(pixelSize) + " px text" : kind == PreviewKind.Highlight
                ? Number(settingSize) + " px setting · " + Number(pixelSize) + " px stroke" : Number(pixelSize) + " px stroke";
        }

        private string ScaleDescription()
        {
            return previewScale >= .9995f ? "100% image size" : (previewScale * 100).ToString("0.#", CultureInfo.CurrentCulture) + "% preview";
        }

        private void UpdateAccessiblePreview(string value, string scale)
        {
            string description = value + (kind == PreviewKind.Text ? " in " + family : "") + "; " + scale + ".";
            AccessibleDescription = description;
            if (String.Equals(tooltipText, description, StringComparison.Ordinal)) return;
            tooltipText = description;
            tips.SetToolTip(this, description);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        internal static Font SampleFont(string family, float size, FontStyle style)
        {
            try { return new Font(family, size, style, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        private static string Number(float value) { return value.ToString("0.#", CultureInfo.CurrentCulture); }
    }
}
