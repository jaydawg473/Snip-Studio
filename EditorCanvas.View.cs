using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    // Coordinate helpers are independent of the window and screen DPI. Both
    // points and origins are in physical client pixels; image points are pixels
    // in the captured bitmap.
    public static class ViewportTransform
    {
        public static PointF ImagePointAt(PointF viewportPoint, PointF origin, float scale)
        {
            Validate(viewportPoint, origin, scale);
            return new PointF((viewportPoint.X - origin.X) / scale, (viewportPoint.Y - origin.Y) / scale);
        }

        public static PointF OriginForAnchor(PointF imagePoint, PointF viewportPoint, float scale)
        {
            Validate(imagePoint, viewportPoint, scale);
            return new PointF(viewportPoint.X - imagePoint.X * scale, viewportPoint.Y - imagePoint.Y * scale);
        }

        private static void Validate(PointF first, PointF second, float scale)
        {
            if (Single.IsNaN(scale) || Single.IsInfinity(scale) || scale <= 0 ||
                Single.IsNaN(first.X) || Single.IsInfinity(first.X) || Single.IsNaN(first.Y) || Single.IsInfinity(first.Y) ||
                Single.IsNaN(second.X) || Single.IsInfinity(second.X) || Single.IsNaN(second.Y) || Single.IsInfinity(second.Y))
                throw new ArgumentOutOfRangeException("scale", "View coordinates must be finite and scale must be positive.");
        }
    }

    public struct ViewMotionState
    {
        public double Position { get; private set; }
        public double Velocity { get; private set; }
        public ViewMotionState(double position, double velocity) : this() { Position = position; Velocity = velocity; }
    }

    // Exact critically damped motion is independent of timer cadence. Retargeting
    // preserves the current velocity instead of restarting a fresh ease-out for
    // every wheel message. It is pure so elapsed-time/input behavior is testable.
    public static class ViewMotion
    {
        public static ViewMotionState Advance(double position, double velocity, double target, double elapsedSeconds)
        {
            if (!Finite(position) || !Finite(velocity) || !Finite(target) || !Finite(elapsedSeconds) || elapsedSeconds < 0)
                throw new ArgumentOutOfRangeException("elapsedSeconds");
            const double frequency = 48;
            double offset = position - target;
            double time = Math.Min(1, elapsedSeconds);
            double term = velocity + frequency * offset;
            double decay = Math.Exp(-frequency * time);
            double next = target + (offset + term * time) * decay;
            double nextVelocity = (velocity - frequency * term * time) * decay;
            if (offset != 0 && (next - target) * offset < 0) return new ViewMotionState(target, 0);
            return new ViewMotionState(next, nextVelocity);
        }

        public static double WheelDistance(int delta, int scrollLines, double lineHeight, int viewportLength)
        {
            if (!Finite(lineHeight) || lineHeight <= 0 || viewportLength < 0 || scrollLines < -1)
                throw new ArgumentOutOfRangeException("lineHeight");
            double unit = scrollLines == -1 ? viewportLength : scrollLines * lineHeight;
            return delta / 120.0 * unit;
        }

        public static double WheelZoomFactor(int delta)
        {
            // A notch changes scale by three percent, not percentage points.
            // Small/high-resolution messages contribute their full fraction.
            // Limit only extreme input to the canvas's complete 1%-800% range.
            double logarithm = delta / 120.0 * Math.Log(1.03);
            double limit = Math.Log(800);
            return Math.Exp(Math.Max(-limit, Math.Min(limit, logarithm)));
        }

        public static double AccumulateScroll(double target, double distance, double minimum, double maximum)
        {
            if (!Finite(target) || !Finite(distance) || !Finite(minimum) || !Finite(maximum) || minimum > maximum)
                throw new ArgumentOutOfRangeException("minimum");
            return Math.Max(minimum, Math.Min(maximum, target + distance));
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }

    public sealed partial class EditorCanvas
    {
        private Timer zoomTimer;
        private Timer scrollTimer;
        private Timer recognitionTimer;
        private PointF zoomOriginOffset;
        private bool zoomViewActive;
        private PointF zoomViewOrigin;
        private Point zoomNativeScrollPosition;
        private PointF zoomAnchorViewport, zoomAnchorImage;
        private float zoomTarget;
        private double zoomVelocity;
        private long zoomFrameTimestamp;
        private PointF scrollOrigin, scrollTarget, scrollBaseOrigin, scrollMinimum, scrollMaximum;
        private double scrollVelocityX, scrollVelocityY;
        private long scrollFrameTimestamp;
        private bool updatingView;
        private bool fitOnWindowResize;
        private int preserveLayoutDepth;
        private long viewIntentVersion;
        private LayoutViewSnapshot layoutViewSnapshot;
        private bool firstImagePainted;
        private int activeRecognitionGeneration = -1;
        private Bitmap checkerPattern;
        private TextureBrush checkerBrush;

        public event EventHandler ViewChanged;
        private bool ZoomAnimationActive { get { return zoomTimer != null && zoomTimer.Enabled; } }
        private bool ScrollAnimationActive { get { return scrollTimer != null && scrollTimer.Enabled; } }
        private bool ViewAnimationActive { get { return ZoomAnimationActive || ScrollAnimationActive; } }
        private bool RecognitionRequested { get { return textActionsMode || (tool == EditorTool.Highlighter && snapHighlightToText); } }

        private sealed class LayoutViewSnapshot
        {
            internal object ImageIdentity;
            internal Size Viewport;
            internal float Zoom;
            internal PointF ImageCenter;
            internal bool FitRequested;
            internal long IntentVersion;
        }

        private sealed class ViewLayoutScope : IDisposable
        {
            private EditorCanvas owner;
            internal ViewLayoutScope(EditorCanvas owner) { this.owner = owner; }
            public void Dispose()
            {
                EditorCanvas current = owner;
                owner = null;
                if (current != null) current.EndPreserveViewForLayout();
            }
        }

        // The chrome owner uses this only for tool/context-row changes, not a
        // genuine window resize. No view or document change occurs if bounds do
        // not change. Nested layouts retain the original outer viewport anchor.
        internal IDisposable PreserveViewForLayout()
        {
            if (disposingCanvas || !HasImage) return new ViewLayoutScope(null);
            if (preserveLayoutDepth == 0)
            {
                layoutViewSnapshot = new LayoutViewSnapshot
                {
                    ImageIdentity = ImageIdentity,
                    Viewport = ClientSize,
                    Zoom = zoom,
                    ImageCenter = ViewportTransform.ImagePointAt(new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f), ImageOrigin, zoom),
                    FitRequested = fit || fitOnWindowResize,
                    IntentVersion = viewIntentVersion
                };
            }
            preserveLayoutDepth++;
            return new ViewLayoutScope(this);
        }

        private void EndPreserveViewForLayout()
        {
            if (preserveLayoutDepth <= 0) return;
            if (--preserveLayoutDepth != 0) return;
            LayoutViewSnapshot snapshot = layoutViewSnapshot;
            layoutViewSnapshot = null;
            if (snapshot == null || disposingCanvas || !HasImage || snapshot.Viewport == ClientSize ||
                !Object.ReferenceEquals(snapshot.ImageIdentity, ImageIdentity) || snapshot.IntentVersion != viewIntentVersion) return;
            StopViewAnimations();
            bool wasUpdating = updatingView;
            updatingView = true;
            try
            {
                fit = false;
                fitOnWindowResize = snapshot.FitRequested;
                zoom = snapshot.Zoom;
                // New native bars can change the client area once more. Reapply
                // the center after that bounded layout has settled, at one scale.
                for (int pass = 0; pass < 3; pass++)
                {
                    Size viewport = ClientSize;
                    PointF center = new PointF(viewport.Width / 2f, viewport.Height / 2f);
                    PositionViewOrigin(ViewportTransform.OriginForAnchor(snapshot.ImageCenter, center, zoom));
                    if (ClientSize == viewport) break;
                }
            }
            finally { updatingView = wasUpdating; }
            Invalidate();
            RaiseViewChanged();
        }

        private void ChangeViewIntent()
        {
            viewIntentVersion++;
            fitOnWindowResize = false;
        }

        // Menu and toolbar zoom use the viewport center. Wheel zoom uses the
        // actual pointer position, including the transition out of a centered fit.
        public void ZoomAt(float value, Point viewportPoint)
        {
            if (!HasImage) return;
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value <= 0) throw new ArgumentOutOfRangeException("value");
            ChangeViewIntent();
            PointF imagePoint = ToImage(viewportPoint, false);
            CancelGesture();
            fit = false;
            ApplyZoom(value, viewportPoint, imagePoint);
        }

        private void ZoomBy(float factor, Point viewportPoint)
        {
            ZoomByAt(factor, viewportPoint, Stopwatch.GetTimestamp());
        }

        // The clock boundary is separate so the production retarget/frame path
        // can be checked deterministically without pumping a native timer loop.
        private void ZoomByAt(float factor, Point viewportPoint, long now)
        {
            if (!HasImage || factor <= 0 || Single.IsNaN(factor) || Single.IsInfinity(factor)) return;
            ChangeViewIntent();
            if (ScrollAnimationActive) AdvanceScroll(now);
            bool continuing = ZoomAnimationActive;
            if (continuing)
            {
                AdvanceZoom(now);
                continuing = ZoomAnimationActive;
            }
            float pending = continuing ? zoomTarget : zoom;
            // An opposite wheel direction should respond now, without first
            // finishing the destination queued by the previous direction.
            if ((factor > 1 && pending < zoom) || (factor < 1 && pending > zoom))
            { pending = zoom; zoomVelocity = 0; }
            float target = Math.Max(0.01f, Math.Min(8f, pending * factor));
            PointF currentOrigin = ImageOrigin;
            PointF imagePoint = ToImage(viewportPoint, false);
            if (!continuing)
            {
                CancelGesture();
                fit = false;
                // Native scrollbars move in whole pixels and can change the
                // viewport when their range changes. Keep rendering in one
                // floating coordinate space for this animation instead.
                zoomViewOrigin = currentOrigin;
                zoomNativeScrollPosition = AutoScrollPosition;
                zoomViewActive = true;
                zoomVelocity = 0;
            }
            zoomTarget = target;
            zoomAnchorViewport = viewportPoint;
            zoomAnchorImage = imagePoint;
            zoomFrameTimestamp = now;
            if (Math.Abs(zoomTarget - zoom) < 0.00001f) { ApplyZoom(target, viewportPoint, imagePoint); StopZoomAnimation(); return; }
            if (zoomTimer == null)
            {
                // Request frequent UI frames. Progress follows Stopwatch time,
                // so a delayed/coalesced timer does not replay a backlog.
                zoomTimer = new Timer { Interval = 10 };
                zoomTimer.Tick += OnZoomFrame;
            }
            zoomTimer.Start();
            Invalidate();
        }

        private void OnZoomFrame(object sender, EventArgs e)
        {
            if (disposingCanvas || !HasImage) { StopZoomAnimation(); return; }
            AdvanceZoom(Stopwatch.GetTimestamp());
        }

        private void AdvanceZoom(long now)
        {
            double elapsed = Math.Max(0, (now - zoomFrameTimestamp) / (double)Stopwatch.Frequency);
            zoomFrameTimestamp = now;
            ViewMotionState motion = ViewMotion.Advance(Math.Log(zoom), zoomVelocity, Math.Log(zoomTarget), elapsed);
            zoomVelocity = motion.Velocity;
            float next = (float)Math.Exp(motion.Position);
            double extent = Math.Max(ImageSize.Width, ImageSize.Height);
            bool finished = Math.Abs(next - zoomTarget) * extent < 0.1 && Math.Abs(zoomVelocity * next) * extent < 2;
            if (finished)
            {
                next = zoomTarget;
                zoomVelocity = 0;
            }
            ApplyZoom(next, zoomAnchorViewport, zoomAnchorImage);
            if (finished) StopZoomAnimation();
        }

        private void ScrollByWheel(int delta, bool horizontal)
        {
            if (!HasImage || delta == 0) return;
            long now = Stopwatch.GetTimestamp();
            if (ZoomAnimationActive) { AdvanceZoom(now); StopZoomAnimation(); }
            if (ScrollAnimationActive) AdvanceScroll(now);
            int maxX = HorizontalScroll.Visible ? Math.Max(0, HorizontalScroll.Maximum - HorizontalScroll.LargeChange + 1) : 0;
            int maxY = VerticalScroll.Visible ? Math.Max(0, VerticalScroll.Maximum - VerticalScroll.LargeChange + 1) : 0;
            if (!horizontal && maxY == 0 && maxX > 0) horizontal = true;
            double distance = ViewMotion.WheelDistance(delta, SystemInformation.MouseWheelScrollLines,
                Math.Max(1, Font.Height * 1.25), horizontal ? ClientSize.Width : ClientSize.Height);
            if (distance == 0 || (horizontal ? maxX == 0 : maxY == 0)) return;
            ChangeViewIntent();
            bool continuing = ScrollAnimationActive;
            if (!continuing)
            {
                CancelGesture();
                scrollOrigin = scrollTarget = ImageOrigin;
                Point native = AutoScrollPosition;
                scrollBaseOrigin = new PointF(scrollOrigin.X - native.X, scrollOrigin.Y - native.Y);
                scrollMaximum = scrollBaseOrigin;
                scrollMinimum = new PointF(scrollBaseOrigin.X - maxX, scrollBaseOrigin.Y - maxY);
                scrollVelocityX = scrollVelocityY = 0;
            }
            if (horizontal)
            {
                if ((distance > 0 && scrollTarget.X < scrollOrigin.X) || (distance < 0 && scrollTarget.X > scrollOrigin.X))
                { scrollTarget.X = scrollOrigin.X; scrollVelocityX = 0; }
                scrollTarget.X = (float)ViewMotion.AccumulateScroll(scrollTarget.X, distance, scrollMinimum.X, scrollMaximum.X);
            }
            else
            {
                if ((distance > 0 && scrollTarget.Y < scrollOrigin.Y) || (distance < 0 && scrollTarget.Y > scrollOrigin.Y))
                { scrollTarget.Y = scrollOrigin.Y; scrollVelocityY = 0; }
                scrollTarget.Y = (float)ViewMotion.AccumulateScroll(scrollTarget.Y, distance, scrollMinimum.Y, scrollMaximum.Y);
            }
            scrollFrameTimestamp = now;
            if (scrollTimer == null)
            {
                scrollTimer = new Timer { Interval = 10 };
                scrollTimer.Tick += delegate
                {
                    if (disposingCanvas || !HasImage) { StopScrollAnimation(); return; }
                    AdvanceScroll(Stopwatch.GetTimestamp());
                };
            }
            scrollTimer.Start();
        }

        private void AdvanceScroll(long now)
        {
            double elapsed = Math.Max(0, (now - scrollFrameTimestamp) / (double)Stopwatch.Frequency);
            scrollFrameTimestamp = now;
            ViewMotionState x = ViewMotion.Advance(scrollOrigin.X, scrollVelocityX, scrollTarget.X, elapsed);
            ViewMotionState y = ViewMotion.Advance(scrollOrigin.Y, scrollVelocityY, scrollTarget.Y, elapsed);
            scrollVelocityX = x.Velocity; scrollVelocityY = y.Velocity;
            scrollOrigin = new PointF((float)x.Position, (float)y.Position);
            if (Math.Abs(scrollOrigin.X - scrollTarget.X) < 0.05 && Math.Abs(scrollOrigin.Y - scrollTarget.Y) < 0.05 &&
                Math.Abs(scrollVelocityX) < 1 && Math.Abs(scrollVelocityY) < 1)
            {
                scrollTimer.Stop();
                scrollOrigin = scrollTarget;
                scrollVelocityX = scrollVelocityY = 0;
            }
            updatingView = true;
            try
            {
                // Keep the existing native scrollbar range fixed throughout the
                // burst. Only the view origin and fractional remainder change.
                AutoScrollPosition = new Point((int)Math.Round(scrollBaseOrigin.X - scrollOrigin.X),
                    (int)Math.Round(scrollBaseOrigin.Y - scrollOrigin.Y));
                Point actual = AutoScrollPosition;
                zoomOriginOffset = new PointF(scrollOrigin.X - CanvasMargin - actual.X,
                    scrollOrigin.Y - CanvasMargin - actual.Y);
            }
            finally { updatingView = false; }
            Invalidate();
            RaiseViewChanged();
        }

        private void ApplyZoom(float value, PointF viewportAnchor, PointF imageAnchor)
        {
            if (!HasImage || Single.IsNaN(value) || Single.IsInfinity(value)) return;
            updatingView = true;
            try
            {
                zoom = Math.Max(0.01f, Math.Min(8f, value));
                PointF origin = ViewportTransform.OriginForAnchor(imageAnchor, viewportAnchor, zoom);
                if (zoomViewActive) zoomViewOrigin = origin;
                else PositionViewOrigin(origin);
            }
            finally { updatingView = false; }
            Invalidate();
            RaiseViewChanged();
        }

        private void PositionViewOrigin(PointF desiredOrigin)
        {
            // Positive baseline padding and sufficient trailing space let the
            // native scrollbar reach every image edge even when pointer zoom
            // intentionally leaves blank space beyond an edge. The retained
            // offset contains only that padding and subpixel rounding, never
            // an unreachable negative scroll remainder.
            float baselineX = Math.Max(CanvasMargin, desiredOrigin.X);
            float baselineY = Math.Max(CanvasMargin, desiredOrigin.Y);
            int horizontal = (int)Math.Ceiling(Math.Max(0, baselineX - desiredOrigin.X));
            int vertical = (int)Math.Ceiling(Math.Max(0, baselineY - desiredOrigin.Y));
            for (int pass = 0; pass < 3; pass++)
            {
                Size viewport = ClientSize;
                int width = (int)Math.Ceiling(Math.Max(baselineX + ImageSize.Width * zoom + CanvasMargin, viewport.Width + horizontal));
                int height = (int)Math.Ceiling(Math.Max(baselineY + ImageSize.Height * zoom + CanvasMargin, viewport.Height + vertical));
                Size extent = new Size(width, height);
                if (AutoScrollMinSize != extent) AutoScrollMinSize = extent;
                if (ClientSize == viewport) break;
            }
            AutoScrollPosition = new Point(horizontal, vertical);
            Point actual = AutoScrollPosition;
            zoomOriginOffset = new PointF(desiredOrigin.X - CanvasMargin - actual.X, desiredOrigin.Y - CanvasMargin - actual.Y);
        }

        private void BeginPanView()
        {
            ChangeViewIntent();
            PointF origin = ImageOrigin;
            fit = false;
            updatingView = true;
            try { PositionViewOrigin(origin); }
            finally { updatingView = false; }
        }

        private void PanView(float x, float y)
        {
            PointF origin = ImageOrigin;
            updatingView = true;
            try { PositionViewOrigin(new PointF(origin.X + x, origin.Y + y)); }
            finally { updatingView = false; }
            RaiseViewChanged();
        }

        private void StopZoomAnimation()
        {
            bool wasActive = ZoomAnimationActive || zoomViewActive;
            if (zoomTimer != null) zoomTimer.Stop();
            zoomVelocity = 0;
            if (zoomViewActive)
            {
                PointF origin = zoomViewOrigin;
                zoomViewActive = false;
                if (HasImage && !disposingCanvas)
                {
                    bool wasUpdating = updatingView;
                    updatingView = true;
                    try { PositionViewOrigin(origin); }
                    finally { updatingView = wasUpdating; }
                }
            }
            if (wasActive) Invalidate();
        }

        private void FollowNativeScrollDuringZoom()
        {
            if (!zoomViewActive) return;
            Point current = AutoScrollPosition;
            zoomViewOrigin = new PointF(zoomViewOrigin.X + current.X - zoomNativeScrollPosition.X,
                zoomViewOrigin.Y + current.Y - zoomNativeScrollPosition.Y);
            zoomNativeScrollPosition = current;
        }

        private void StopScrollAnimation()
        {
            if (scrollTimer == null || !scrollTimer.Enabled) return;
            scrollTimer.Stop();
            scrollVelocityX = scrollVelocityY = 0;
            Invalidate();
        }

        private void StopViewAnimations()
        {
            StopZoomAnimation();
            StopScrollAnimation();
        }

        private void RaiseViewChanged()
        {
            EventHandler handler = ViewChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void EnsureCheckerBrush()
        {
            if (checkerBrush != null) return;
            const int tile = 16;
            checkerPattern = new Bitmap(tile * 2, tile * 2);
            using (Graphics graphics = Graphics.FromImage(checkerPattern))
            using (Brush dark = new SolidBrush(Color.FromArgb(64, 71, 83)))
            {
                graphics.Clear(Color.FromArgb(78, 86, 98));
                graphics.FillRectangle(dark, tile, 0, tile, tile);
                graphics.FillRectangle(dark, 0, tile, tile, tile);
            }
            checkerBrush = new TextureBrush(checkerPattern, WrapMode.Tile);
        }

        private void QueueRecognitionWhenNeeded()
        {
            if (disposingCanvas || !HasImage) return;
            bool stale = !Object.ReferenceEquals(recognizedBase, document.BaseIdentity) || recognizedRevision != document.Revision;
            if (stale)
            {
                recognizedBase = document.BaseIdentity;
                recognizedRevision = document.Revision;
                recognitionGeneration++;
                if (recognitionCancellation != null) recognitionCancellation.Cancel();
                recognizedText = null;
                ResetTextSelection(null);
            }
            if (!RecognitionRequested)
            {
                SetTextRecognitionBusy(false);
                if (recognitionTimer != null) recognitionTimer.Stop();
                if (recognitionCancellation != null && !recognitionCancellation.IsCancellationRequested)
                { recognitionCancellation.Cancel(); recognitionGeneration++; }
                if (recognizedText == null) SetRecognitionStatus("Open Text actions or Highlight to recognize text.");
                return;
            }
            if (recognizedText != null) { SetTextRecognitionBusy(false); return; }
            SetTextRecognitionBusy(true);
            if (recognitionCancellation != null && !recognitionCancellation.IsCancellationRequested && activeRecognitionGeneration == recognitionGeneration) return;
            if (recognitionTimer == null)
            {
                recognitionTimer = new Timer { Interval = 180 };
                recognitionTimer.Tick += delegate
                {
                    if (disposingCanvas || recognitionTimer == null) return;
                    recognitionTimer.Stop();
                    if (disposingCanvas || !HasImage || !RecognitionRequested) return;
                    if (!firstImagePainted || ViewAnimationActive || drawing || moving || panning || erasing || AnnotationHandleDragging)
                    { recognitionTimer.Interval = 100; recognitionTimer.Start(); return; }
                    RecognizeCurrentImageAsync();
                };
            }
            recognitionTimer.Stop();
            recognitionTimer.Interval = 180;
            recognitionTimer.Start();
            SetRecognitionStatus("Finding text for precise highlighting...");
        }

        private void DisposeViewResources()
        {
            if (zoomTimer != null) { zoomTimer.Stop(); zoomTimer.Dispose(); zoomTimer = null; }
            if (scrollTimer != null) { scrollTimer.Stop(); scrollTimer.Dispose(); scrollTimer = null; }
            if (recognitionTimer != null) { recognitionTimer.Stop(); recognitionTimer.Dispose(); recognitionTimer = null; }
            if (checkerBrush != null) { checkerBrush.Dispose(); checkerBrush = null; }
            if (checkerPattern != null) { checkerPattern.Dispose(); checkerPattern = null; }
        }
    }
}
