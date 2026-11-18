using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    // App-owned timeline UI. Frames are decoded by the existing Windows media
    // adapter; painting and pointer movement never open or decode a media file.
    public sealed class VideoFilmstripControl : Control
    {
        private Bitmap[] frames = new Bitmap[0];
        private double duration, selectionStart, selectionEnd, position;
        private int dragPart;
        private double dragStart, dragEnd, dragPosition;
        private string emptyMessage = "Reading video frames…";

        public event EventHandler SelectionChanged;
        public event EventHandler PositionChanged;

        public VideoFilmstripControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            MinimumSize = new Size(220, 108);
            Height = 112;
            TabStop = true;
            AccessibleName = "Video filmstrip. Drag the blue start and end handles to trim; click a frame to preview.";
            AccessibleRole = AccessibleRole.Slider;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
        }

        public double DurationSeconds { get { return duration; } }
        public double SelectionStartSeconds { get { return selectionStart; } }
        public double SelectionEndSeconds { get { return selectionEnd; } }
        public double PositionSeconds
        {
            get { return position; }
            set { SetPosition(value, false); }
        }
        public double FrameStepSeconds { get; set; }
        public string EmptyMessage
        {
            get { return emptyMessage; }
            set { emptyMessage = value ?? ""; Invalidate(); }
        }

        public void SetDuration(TimeSpan value)
        {
            double seconds = value.TotalSeconds;
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException("value");
            duration = seconds;
            selectionStart = position = 0;
            selectionEnd = duration;
            dragPart = 0;
            Capture = false;
            Invalidate();
        }

        public void SetSelection(double start, double end)
        {
            if (!Finite(start) || !Finite(end)) throw new ArgumentOutOfRangeException("start");
            selectionStart = Math.Max(0, Math.Min(duration, start));
            selectionEnd = Math.Max(selectionStart, Math.Min(duration, end));
            Invalidate();
        }

        // Ownership transfers to this control. Replacing/disposing the strip
        // disposes its previous images; the producer must not keep using them.
        public void SetFrames(Bitmap[] value)
        {
            if (value == null) value = new Bitmap[0];
            if (value.Length > 24) throw new ArgumentOutOfRangeException("value");
            if (Object.ReferenceEquals(frames, value)) return;
            Bitmap[] previous = frames;
            frames = value;
            foreach (Bitmap frame in previous) if (frame != null) frame.Dispose();
            Invalidate();
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private int Ui(int value) { return Math.Max(1, (int)Math.Round(value * DeviceDpi / 96.0)); }
        private Rectangle Track
        {
            get { return new Rectangle(Ui(14), Ui(25), Math.Max(1, ClientSize.Width - Ui(28)), Math.Max(Ui(28), ClientSize.Height - Ui(48))); }
        }
        private int ToX(double seconds)
        {
            Rectangle track = Track;
            return track.Left + (int)Math.Round((duration <= 0 ? 0 : Math.Max(0, Math.Min(1, seconds / duration))) * track.Width);
        }
        private double ToSeconds(int x)
        {
            Rectangle track = Track;
            return duration * Math.Max(0, Math.Min(1, (x - track.Left) / (double)Math.Max(1, track.Width)));
        }
        private void SetPosition(double seconds, bool notify)
        {
            if (!Finite(seconds)) return;
            double next = Math.Max(0, Math.Min(duration, seconds));
            if (Math.Abs(next - position) < .0000001) return;
            position = next;
            Invalidate();
            if (notify && PositionChanged != null) PositionChanged(this, EventArgs.Empty);
        }
        private string Stamp(double seconds)
        {
            if (duration < 10) return Math.Max(0, seconds).ToString("0.0") + "s";
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return time.TotalHours >= 1 ? ((int)time.TotalHours).ToString() + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00")
                : ((int)time.TotalMinutes).ToString() + ":" + time.Seconds.ToString("00");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.Clear(StudioTheme.Surface);
            Rectangle track = Track;
            using (Brush fill = new SolidBrush(StudioTheme.Raised)) graphics.FillRectangle(fill, track);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            for (int i = 0; i < frames.Length; i++)
            {
                Bitmap frame = frames[i];
                if (frame == null) continue;
                Rectangle cell = new Rectangle(track.Left + (int)((long)i * track.Width / frames.Length), track.Top,
                    Math.Max(1, (int)((long)(i + 1) * track.Width / frames.Length) - (int)((long)i * track.Width / frames.Length)), track.Height);
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.SetClip(cell);
                    double fit = Math.Min(cell.Width / (double)frame.Width, cell.Height / (double)frame.Height);
                    int width = Math.Max(1, (int)Math.Round(frame.Width * fit));
                    int height = Math.Max(1, (int)Math.Round(frame.Height * fit));
                    graphics.DrawImage(frame, new Rectangle(cell.Left + (cell.Width - width) / 2, cell.Top + (cell.Height - height) / 2, width, height));
                }
                finally { graphics.Restore(state); }
                using (Pen divider = new Pen(StudioTheme.Surface)) graphics.DrawLine(divider, cell.Right, track.Top, cell.Right, track.Bottom);
            }
            if (frames.Length == 0)
                TextRenderer.DrawText(graphics, emptyMessage, Font, track, StudioTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int labelWidth = Math.Max(Ui(56), TextRenderer.MeasureText(Stamp(duration), Font).Width + Ui(12));
            int intervals = Math.Max(1, Math.Min(4, track.Width / labelWidth));
            for (int i = 0; i <= intervals; i++)
            {
                int x = track.Left + track.Width * i / intervals;
                Rectangle label = new Rectangle(x - labelWidth / 2, 0, labelWidth, Ui(22));
                if (i == 0) label.X = track.Left;
                if (i == intervals) label.X = track.Right - label.Width;
                TextRenderer.DrawText(graphics, Stamp(duration * i / intervals), Font, label, StudioTheme.Muted,
                    (i == 0 ? TextFormatFlags.Left : i == intervals ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (duration > 0)
            {
                int startX = ToX(selectionStart), endX = ToX(selectionEnd);
                using (Brush mask = new SolidBrush(Color.FromArgb(165, StudioTheme.Background)))
                {
                    graphics.FillRectangle(mask, track.Left, track.Top, Math.Max(0, startX - track.Left), track.Height);
                    graphics.FillRectangle(mask, endX, track.Top, Math.Max(0, track.Right - endX), track.Height);
                }
                using (Pen outline = new Pen(Enabled ? StudioTheme.Accent : StudioTheme.Disabled, Ui(2)))
                    graphics.DrawRectangle(outline, startX, track.Top, Math.Max(1, endX - startX), track.Height);
                DrawHandle(graphics, startX, track);
                DrawHandle(graphics, endX, track);
                int head = ToX(position);
                using (Pen line = new Pen(StudioTheme.Text, Ui(2))) graphics.DrawLine(line, head, track.Top - Ui(5), head, track.Bottom + Ui(8));
                using (Brush ink = new SolidBrush(StudioTheme.Text))
                    graphics.FillPolygon(ink, new Point[] { new Point(head - Ui(5), track.Top - Ui(9)), new Point(head + Ui(5), track.Top - Ui(9)), new Point(head, track.Top - Ui(3)) });
            }
            if (Focused) ControlPaint.DrawFocusRectangle(graphics, new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), StudioTheme.Text, StudioTheme.Surface);
        }

        private void DrawHandle(Graphics graphics, int x, Rectangle track)
        {
            Rectangle handle = new Rectangle(x - Ui(5), track.Top - Ui(2), Ui(10), track.Height + Ui(4));
            using (Brush fill = new SolidBrush(Enabled ? StudioTheme.Accent : StudioTheme.Disabled)) graphics.FillRectangle(fill, handle);
            using (Pen ink = new Pen(StudioTheme.PrimaryText))
            {
                graphics.DrawLine(ink, x - Ui(1), track.Top + track.Height / 2 - Ui(7), x - Ui(1), track.Top + track.Height / 2 + Ui(7));
                graphics.DrawLine(ink, x + Ui(2), track.Top + track.Height / 2 - Ui(7), x + Ui(2), track.Top + track.Height / 2 + Ui(7));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || duration <= 0 || e.Button != MouseButtons.Left) return;
            Focus();
            dragStart = selectionStart; dragEnd = selectionEnd; dragPosition = position;
            int leftDistance = Math.Abs(e.X - ToX(selectionStart)), rightDistance = Math.Abs(e.X - ToX(selectionEnd));
            Rectangle track = Track;
            bool onHandles = e.Y >= track.Top - Ui(5) && e.Y <= track.Bottom + Ui(5);
            dragPart = onHandles && Math.Min(leftDistance, rightDistance) <= Ui(10) ? (leftDistance <= rightDistance ? 1 : 2) : 3;
            Capture = true;
            ApplyPointer(e.X);
        }
        private void ApplyPointer(int x)
        {
            double value = ToSeconds(x);
            double minimum = Math.Min(duration, Math.Max(.001, FrameStepSeconds > 0 ? FrameStepSeconds : .001));
            if (dragPart == 1) selectionStart = Math.Max(0, Math.Min(selectionEnd - minimum, value));
            else if (dragPart == 2) selectionEnd = Math.Min(duration, Math.Max(selectionStart + minimum, value));
            if (dragPart == 1 || dragPart == 2)
            {
                SetPosition(dragPart == 1 ? selectionStart : selectionEnd, true);
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                Invalidate();
            }
            else SetPosition(value, true);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragPart != 0 && Capture) ApplyPointer(e.X);
            else
            {
                Rectangle track = Track;
                Cursor = duration > 0 && e.Y >= track.Top - Ui(5) && e.Y <= track.Bottom + Ui(5) &&
                    Math.Min(Math.Abs(e.X - ToX(selectionStart)), Math.Abs(e.X - ToX(selectionEnd))) <= Ui(10)
                    ? Cursors.SizeWE : Cursors.Default;
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (dragPart != 0) ApplyPointer(e.X);
            dragPart = 0; Capture = false;
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End ||
                (key == Keys.Escape && dragPart != 0) || base.IsInputKey(keyData);
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) dragPart = 0;
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) { dragPart = 0; Capture = false; }
            Invalidate();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (duration <= 0 || !Enabled) return;
            double step = FrameStepSeconds > 0 ? FrameStepSeconds : .1;
            if (e.KeyCode == Keys.Left) SetPosition(position - step, true);
            else if (e.KeyCode == Keys.Right) SetPosition(position + step, true);
            else if (e.KeyCode == Keys.Home) SetPosition(0, true);
            else if (e.KeyCode == Keys.End) SetPosition(duration, true);
            else if (e.KeyCode == Keys.Escape && dragPart != 0)
            {
                selectionStart = dragStart; selectionEnd = dragEnd;
                dragPart = 0; Capture = false;
                SetPosition(dragPosition, true);
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                Invalidate();
            }
            else return;
            e.Handled = true; e.SuppressKeyPress = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Bitmap[] previous = frames; frames = new Bitmap[0];
                foreach (Bitmap frame in previous) if (frame != null) frame.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
