using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private const double TextFeedbackMinimumMilliseconds = 1000;
        private const double TextFeedbackFadeMilliseconds = 280;
        private bool textRecognitionBusy;
        private bool textFeedbackVisible;
        private bool textFeedbackDisposed;
        private long textFeedbackStarted;
        private long textFeedbackFadeStarted;
        private float textFeedbackFadeFrom;
        private bool lastTextActionCommandsReady = true;
        private Timer textFeedbackTimer;
        private RectangleF lastTextFeedbackFrame;

        // Actual OCR work and the short activation cue are separate states.
        public bool IsRecognizingText { get { return textRecognitionBusy; } }
        public bool TextActionCommandsReady
        {
            get
            {
                return !textRecognitionBusy && (!textActionsMode || !textFeedbackVisible ||
                    ElapsedTextFeedbackMilliseconds(textFeedbackStarted, Stopwatch.GetTimestamp()) >= TextFeedbackMinimumMilliseconds);
            }
        }

        private void SetTextRecognitionBusy(bool value)
        {
            if (textRecognitionBusy == value) return;
            textRecognitionBusy = value;
            if (textFeedbackDisposed || disposingCanvas || IsDisposed) return;

            if (textActionsMode && HasImage)
            {
                if (value)
                {
                    if (!textFeedbackVisible) StartTextActionFeedback();
                    else textFeedbackFadeStarted = 0;
                }
                else AdvanceTextScanFeedback(false);
            }
            PublishTextScanState(true);
        }

        private void BeginTextActionFeedback()
        {
            StartTextActionFeedback();
            PublishTextScanState(false);
        }

        private void StartTextActionFeedback()
        {
            if (textFeedbackDisposed || disposingCanvas || IsDisposed || !HasImage || !textActionsMode) return;
            InvalidateTextFeedbackFrame(lastTextFeedbackFrame);
            textFeedbackVisible = true;
            textFeedbackStarted = Stopwatch.GetTimestamp();
            textFeedbackFadeStarted = 0;
            textFeedbackFadeFrom = 0;
            lastTextFeedbackFrame = CurrentTextFeedbackFrame();
            if (textFeedbackTimer == null)
            {
                textFeedbackTimer = new Timer { Interval = 40 };
                textFeedbackTimer.Tick += OnTextScanFeedbackTick;
            }
            textFeedbackTimer.Start();
            InvalidateTextFeedbackFrame(lastTextFeedbackFrame);
        }

        private void EndTextActionFeedback()
        {
            StopTextActionFeedback();
            PublishTextScanState(false);
        }

        private void StopTextActionFeedback()
        {
            if (textFeedbackTimer != null) textFeedbackTimer.Stop();
            RectangleF previous = lastTextFeedbackFrame;
            RectangleF current = CurrentTextFeedbackFrame();
            textFeedbackVisible = false;
            textFeedbackStarted = 0;
            textFeedbackFadeStarted = 0;
            lastTextFeedbackFrame = RectangleF.Empty;
            InvalidateTextFeedbackFrame(previous);
            InvalidateTextFeedbackFrame(current);
        }

        private void OnTextScanFeedbackTick(object sender, EventArgs e)
        {
            AdvanceTextScanFeedback(true);
        }

        private void AdvanceTextScanFeedback(bool publishReadiness)
        {
            if (!textFeedbackVisible) return;
            if (textFeedbackDisposed || disposingCanvas || IsDisposed || !HasImage || !textActionsMode)
            {
                StopTextActionFeedback();
                if (publishReadiness) PublishTextScanState(false);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = ElapsedTextFeedbackMilliseconds(textFeedbackStarted, now);
            if (!textRecognitionBusy && elapsed >= TextFeedbackMinimumMilliseconds)
            {
                if (textFeedbackFadeStarted == 0)
                {
                    textFeedbackFadeFrom = TextFeedbackPulse(elapsed);
                    textFeedbackFadeStarted = now;
                }
                else if (ElapsedTextFeedbackMilliseconds(textFeedbackFadeStarted, now) >= TextFeedbackFadeMilliseconds)
                {
                    StopTextActionFeedback();
                    if (publishReadiness) PublishTextScanState(false);
                    return;
                }
            }

            RectangleF current = CurrentTextFeedbackFrame();
            InvalidateTextFeedbackFrame(lastTextFeedbackFrame);
            InvalidateTextFeedbackFrame(current);
            lastTextFeedbackFrame = current;
            if (publishReadiness) PublishTextScanState(false);
        }

        private RectangleF CurrentTextFeedbackFrame()
        {
            if (!HasImage || IsDisposed || disposingCanvas) return RectangleF.Empty;
            PointF origin = ImageOrigin;
            return TextFeedbackFrame(origin, ImageSize.Width * zoom, ImageSize.Height * zoom);
        }

        private RectangleF TextFeedbackFrame(PointF origin, float width, float height)
        {
            // At high zoom, the actual image edges can be off-screen. Outline
            // the visible image area so activation is still apparent.
            float left = Math.Max(5, origin.X - 2);
            float top = Math.Max(5, origin.Y - 2);
            float right = Math.Min(ClientSize.Width - 5, origin.X + width + 2);
            float bottom = Math.Min(ClientSize.Height - 5, origin.Y + height + 2);
            return right > left && bottom > top ? RectangleF.FromLTRB(left, top, right, bottom) : RectangleF.Empty;
        }

        private void InvalidateTextFeedbackFrame(RectangleF frame)
        {
            if (textFeedbackDisposed || IsDisposed || !IsHandleCreated || frame.Width <= 0 || frame.Height <= 0) return;
            Rectangle outer = Rectangle.Ceiling(frame);
            outer.Inflate(10, 10);
            if (!outer.IntersectsWith(ClientRectangle)) return;
            using (Region edges = new Region(outer))
            {
                Rectangle inner = Rectangle.FromLTRB((int)Math.Ceiling(frame.Left + 10), (int)Math.Ceiling(frame.Top + 10),
                    (int)Math.Floor(frame.Right - 10), (int)Math.Floor(frame.Bottom - 10));
                if (inner.Width > 0 && inner.Height > 0) edges.Exclude(inner);
                edges.Intersect(ClientRectangle);
                Invalidate(edges, false);
            }
        }

        // Called after the image transform has been restored. All dimensions
        // here are viewport pixels, so the outline never grows with image zoom.
        private void DrawTextScanFeedback(Graphics graphics, PointF origin, float width, float height)
        {
            if (!textFeedbackVisible || !textActionsMode || textFeedbackDisposed || disposingCanvas ||
                width <= 0 || height <= 0) return;

            long now = Stopwatch.GetTimestamp();
            float intensity = TextFeedbackPulse(ElapsedTextFeedbackMilliseconds(textFeedbackStarted, now));
            if (textFeedbackFadeStarted != 0)
            {
                float remaining = 1 - (float)Math.Min(1,
                    ElapsedTextFeedbackMilliseconds(textFeedbackFadeStarted, now) / TextFeedbackFadeMilliseconds);
                intensity = textFeedbackFadeFrom * remaining;
            }
            if (intensity <= 0) return;

            RectangleF frame = TextFeedbackFrame(origin, width, height);
            if (frame.IsEmpty) return;
            lastTextFeedbackFrame = frame;
            Color accent = Color.FromArgb(0, 116, 232);
            int lineAlpha = Math.Max(0, Math.Min(255, (int)Math.Round(intensity * 255)));
            int glowAlpha = Math.Max(0, Math.Min(75, (int)Math.Round(intensity * 75)));
            GraphicsState saved = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen glow = new Pen(Color.FromArgb(glowAlpha, accent), 10))
                using (Pen contrast = new Pen(Color.FromArgb((int)Math.Round(intensity * 210), Color.White), 5))
                using (Pen line = new Pen(Color.FromArgb(lineAlpha, accent), 3))
                using (Pen traveling = new Pen(Color.FromArgb(lineAlpha, Color.FromArgb(86, 225, 255)), 4))
                {
                    glow.LineJoin = contrast.LineJoin = line.LineJoin = LineJoin.Round;
                    traveling.StartCap = traveling.EndCap = LineCap.Round;
                    graphics.DrawRectangle(glow, frame.X, frame.Y, frame.Width, frame.Height);
                    graphics.DrawRectangle(contrast, frame.X, frame.Y, frame.Width, frame.Height);
                    graphics.DrawRectangle(line, frame.X, frame.Y, frame.Width, frame.Height);
                    float perimeter = 2 * (frame.Width + frame.Height);
                    float distance = (float)((ElapsedTextFeedbackMilliseconds(textFeedbackStarted, now) % 1800) / 1800 * perimeter);
                    DrawTextFeedbackSegment(graphics, traveling, frame, distance, Math.Min(180, Math.Max(48, perimeter * 0.12f)));
                }
            }
            finally { graphics.Restore(saved); }
        }

        private static double ElapsedTextFeedbackMilliseconds(long start, long now)
        {
            return start == 0 ? 0 : Math.Max(0, (now - start) * 1000.0 / Stopwatch.Frequency);
        }

        private static float TextFeedbackPulse(double elapsed)
        {
            // Keep the outline clearly visible rather than fading almost to
            // nothing. Slow, small brightness changes avoid a strobe effect.
            double wave = 0.5 - 0.5 * Math.Cos(elapsed * 2 * Math.PI / 1400.0);
            double onset = Math.Min(1, elapsed / 100.0);
            return (float)((0.86 + 0.14 * wave) * onset);
        }

        private static void DrawTextFeedbackSegment(Graphics graphics, Pen pen, RectangleF frame, float distance, float length)
        {
            float perimeter = 2 * (frame.Width + frame.Height);
            if (perimeter <= 0) return;
            float remaining = Math.Min(length, perimeter);
            distance = Math.Max(0, distance % perimeter);
            // A short segment can cross a corner or wrap across its origin.
            for (int pieces = 0; pieces < 6 && remaining > 0.01f; pieces++)
            {
                float edgeEnd = distance < frame.Width ? frame.Width : distance < frame.Width + frame.Height
                    ? frame.Width + frame.Height : distance < 2 * frame.Width + frame.Height ? 2 * frame.Width + frame.Height : perimeter;
                float piece = Math.Min(remaining, Math.Max(0.01f, edgeEnd - distance));
                graphics.DrawLine(pen, TextFeedbackPerimeterPoint(frame, distance), TextFeedbackPerimeterPoint(frame, Math.Min(perimeter, distance + piece)));
                remaining -= piece;
                distance += piece;
                if (distance >= perimeter - 0.001f) distance = 0;
            }
        }

        private static PointF TextFeedbackPerimeterPoint(RectangleF frame, float distance)
        {
            if (distance <= frame.Width) return new PointF(frame.Left + distance, frame.Top);
            distance -= frame.Width;
            if (distance <= frame.Height) return new PointF(frame.Right, frame.Top + distance);
            distance -= frame.Height;
            if (distance <= frame.Width) return new PointF(frame.Right - distance, frame.Bottom);
            return new PointF(frame.Left, frame.Bottom - (distance - frame.Width));
        }

        private void PublishTextScanState(bool force)
        {
            if (textFeedbackDisposed || disposingCanvas || IsDisposed) return;
            bool ready = TextActionCommandsReady;
            if (!force && ready == lastTextActionCommandsReady) return;
            lastTextActionCommandsReady = ready;
            EventHandler changed = TextRecognitionStatusChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        private void DisposeTextScanFeedback()
        {
            if (textFeedbackDisposed) return;
            if (textFeedbackTimer != null)
            {
                textFeedbackTimer.Stop();
                textFeedbackTimer.Tick -= OnTextScanFeedbackTick;
                textFeedbackTimer.Dispose();
                textFeedbackTimer = null;
            }
            textFeedbackVisible = false;
            textRecognitionBusy = false;
            textFeedbackDisposed = true;
            lastTextFeedbackFrame = RectangleF.Empty;
        }
    }
}
