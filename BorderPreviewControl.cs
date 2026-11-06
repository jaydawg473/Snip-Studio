using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    // A generated sample keeps the border preview local and independent of a
    // user's captures. Its outline is measured in the sample's image pixels.
    internal sealed class BorderPreviewControl : Control
    {
        private Bitmap sample;
        private Color outlineColor = Color.Red;
        private int outlineWidth = 2;

        internal Color OutlineColor
        {
            get { return outlineColor; }
            set { outlineColor = Color.FromArgb(255, value); Invalidate(); }
        }
        internal int OutlineWidth
        {
            get { return outlineWidth; }
            set { outlineWidth = Math.Max(1, Math.Min(20, value)); Invalidate(); }
        }

        internal BorderPreviewControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 214;
            MinimumSize = new Size(180, 130);
            AccessibleName = "Screenshot border preview";
            AccessibleDescription = "Shows the selected border color and thickness on a sample image, including while automatic borders are off.";
            AccessibleRole = AccessibleRole.Graphic;
            Tag = "PreserveColor";
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.Clear(StudioTheme.Input);
            if (sample == null) sample = CreateSample();
            float scale = graphics.DpiX / 96f;
            float availableWidth = Math.Max(1, ClientSize.Width - 48 * scale);
            float availableHeight = Math.Max(1, ClientSize.Height - 32 * scale);
            float factor = Math.Min(availableWidth / sample.Width, availableHeight / sample.Height);
            factor = Math.Min(factor, 1.1f * scale);
            RectangleF frame = new RectangleF((ClientSize.Width - sample.Width * factor) / 2,
                (ClientSize.Height - sample.Height * factor) / 2, sample.Width * factor, sample.Height * factor);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(sample, frame);
            using (Pen outline = new Pen(outlineColor, Math.Max(1, outlineWidth * factor)))
            {
                outline.Alignment = PenAlignment.Inset;
                graphics.DrawRectangle(outline, frame.X, frame.Y, frame.Width, frame.Height);
            }
        }

        private static Bitmap CreateSample()
        {
            Bitmap image = new Bitmap(320, 180);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush sky = new LinearGradientBrush(new Rectangle(0, 0, 320, 180),
                    Color.FromArgb(172, 183, 215), Color.FromArgb(255, 209, 164), LinearGradientMode.Vertical))
                    graphics.FillRectangle(sky, 0, 0, 320, 180);
                using (LinearGradientBrush distant = new LinearGradientBrush(new Rectangle(0, 72, 320, 108),
                    Color.FromArgb(205, 160, 136), Color.FromArgb(157, 118, 117), LinearGradientMode.Vertical))
                    graphics.FillPolygon(distant, new Point[] { new Point(0, 128), new Point(64, 86), new Point(112, 113),
                        new Point(179, 94), new Point(247, 112), new Point(320, 103), new Point(320, 180), new Point(0, 180) });
                using (LinearGradientBrush ridge = new LinearGradientBrush(new Rectangle(0, 95, 320, 85),
                    Color.FromArgb(222, 140, 93), Color.FromArgb(117, 66, 60), LinearGradientMode.Vertical))
                    graphics.FillPolygon(ridge, new Point[] { new Point(0, 142), new Point(61, 94), new Point(104, 114),
                        new Point(142, 150), new Point(217, 160), new Point(320, 142), new Point(320, 180), new Point(0, 180) });
                using (Brush light = new SolidBrush(Color.FromArgb(70, 255, 223, 179)))
                    graphics.FillPolygon(light, new Point[] { new Point(3, 139), new Point(61, 94), new Point(104, 114),
                        new Point(142, 150), new Point(87, 128), new Point(53, 115) });
                using (Brush foreground = new SolidBrush(Color.FromArgb(102, 71, 72)))
                    graphics.FillPolygon(foreground, new Point[] { new Point(0, 164), new Point(68, 146), new Point(143, 158),
                        new Point(220, 147), new Point(320, 165), new Point(320, 180), new Point(0, 180) });
            }
            return image;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && sample != null) { sample.Dispose(); sample = null; }
            base.Dispose(disposing);
        }
    }
}
