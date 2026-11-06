using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public enum ScrollingCaptureEndReason
    {
        NoFurtherMovement,
        UserStopped,
        EscapePressed,
        TargetChanged,
        ContentDidNotSettle,
        NoReliableOverlap,
        LimitReached,
        CaptureFailed,
        ScrollingStalled
    }

    public sealed class ScrollingCaptureProgress
    {
        public int FrameCount { get; private set; }
        public int Height { get; private set; }
        public string Status { get; private set; }

        internal ScrollingCaptureProgress(int frameCount, int height, string status)
        {
            FrameCount = frameCount;
            Height = height;
            Status = status;
        }
    }

    // The caller owns this result and its Image. Cancel throws and disposes all
    // frames; Finish returns the accepted portion for the caller's preview.
    public sealed class ScrollingCaptureResult : IDisposable
    {
        public Bitmap Image { get; private set; }
        public int FrameCount { get; private set; }
        public int Height { get { return Image == null ? 0 : Image.Height; } }
        public ScrollingCaptureEndReason EndReason { get; private set; }
        public string Detail { get; private set; }
        public bool StoppedEarly { get { return EndReason != ScrollingCaptureEndReason.NoFurtherMovement && EndReason != ScrollingCaptureEndReason.UserStopped; } }

        internal ScrollingCaptureResult(Bitmap image, int frameCount, ScrollingCaptureEndReason reason, string detail)
        {
            Image = image;
            FrameCount = frameCount;
            EndReason = reason;
            Detail = detail;
        }

        public void Dispose()
        {
            Bitmap image = Image;
            Image = null;
            if (image != null) image.Dispose();
        }
    }

    // The same session loop is used by native capture and deterministic checks.
    // Each ReadFrame result is newly owned by the session. Tests supply only
    // in-memory frames, a virtual clock and completed delays; no input is sent.
    internal sealed class ScrollingCaptureDriver
    {
        internal Func<Bitmap> ReadFrame;
        internal Func<int, bool> ScrollWheel;
        internal Func<ScrollingCaptureInterruption> CheckInterruption;
        internal Func<int, CancellationToken, Task> DelayAsync;
        internal Func<long> ElapsedMilliseconds;
    }

    internal sealed class ScrollingCaptureInterruption
    {
        internal readonly ScrollingCaptureEndReason Reason;
        internal readonly string Detail;
        internal ScrollingCaptureInterruption(ScrollingCaptureEndReason reason, string detail) { Reason = reason; Detail = detail; }
    }

    public sealed class ScrollingCaptureService
    {
        public const int MaximumDurationSeconds = 180;
        public const int MaximumScrollActions = 300;
        private const int SettleDelayMilliseconds = 180;
        private const int SettleSampleMilliseconds = 120;
        private const int MinimumUnchangedMilliseconds = 1200;
        private const int MaximumSettleMilliseconds = 4000;
        private const int MaximumRecoveryAttempts = 2;
        private readonly Func<Rectangle, Bitmap> captureFrame;
        private readonly IntPtr knownCaptureController;

        public ScrollingCaptureService() : this(CaptureService.CaptureRegion) { }

        // The delegate makes capture ownership explicit: it returns a new bitmap
        // owned by this service. Native input is used only inside CaptureAsync.
        internal ScrollingCaptureService(Func<Rectangle, Bitmap> captureFrame) : this(captureFrame, IntPtr.Zero) { }

        // The caller must hide this exact controller and settle the compositor
        // inside captureFrame, then show it without activation after reading.
        internal ScrollingCaptureService(Func<Rectangle, Bitmap> captureFrame, IntPtr knownCaptureController)
        {
            if (captureFrame == null) throw new ArgumentNullException("captureFrame");
            this.captureFrame = captureFrame;
            this.knownCaptureController = knownCaptureController;
        }

        public async Task<ScrollingCaptureResult> CaptureAsync(Rectangle physicalRegion,
            Action<ScrollingCaptureProgress> report, Func<bool> stopRequested,
            CancellationToken cancellationToken)
        {
            ValidateRegion(physicalRegion);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Application.MessageLoop)
                throw new InvalidOperationException("Start scrolling capture on the application window's UI thread.");
            TargetWindow target = TargetWindow.Bind(physicalRegion, knownCaptureController);
            string initialProblem = target.GetProblem(false);
            if (initialProblem != null) throw new InvalidOperationException(initialProblem + " Start scrolling capture again.");

            // The user's explicit selection authorizes this single activation.
            // Never reactivate later: a foreground change ends the capture.
            if (!target.IsForeground)
            {
                if (!ScrollNative.SetForegroundWindow(target.Root))
                    throw new InvalidOperationException("The selected window could not be activated. Activate that window and start scrolling capture again.");
                await Task.Delay(150, cancellationToken);
            }
            initialProblem = target.GetProblem();
            if (initialProblem != null)
                throw new InvalidOperationException(initialProblem + " Activate the selected window and start again.");

            Stopwatch duration = Stopwatch.StartNew();
            ScrollingCaptureDriver driver = new ScrollingCaptureDriver();
            driver.ReadFrame = delegate { return ReadFrame(target); };
            driver.ScrollWheel = delegate(int delta) { return target.ScrollWheel(delta); };
            driver.CheckInterruption = delegate
            {
                ScrollingCaptureEndReason reason;
                string problem = GetStopReason(stopRequested, target, out reason);
                return problem == null ? null : new ScrollingCaptureInterruption(reason, problem);
            };
            driver.DelayAsync = delegate(int milliseconds, CancellationToken token) { return Task.Delay(Math.Max(1, milliseconds), token); };
            driver.ElapsedMilliseconds = delegate { return duration.ElapsedMilliseconds; };
            return await CaptureSessionAsync(physicalRegion, driver, report, cancellationToken);
        }

        internal async Task<ScrollingCaptureResult> CaptureSessionAsync(Rectangle region,
            ScrollingCaptureDriver driver, Action<ScrollingCaptureProgress> report, CancellationToken cancellationToken)
        {
            if (driver == null || driver.ReadFrame == null || driver.ScrollWheel == null || driver.CheckInterruption == null ||
                driver.DelayAsync == null || driver.ElapsedMilliseconds == null) throw new ArgumentException("A complete scrolling capture driver is required.", "driver");
            cancellationToken.ThrowIfCancellationRequested();
            Bitmap acceptedFrame = ReadDriverFrame(driver, region);
            try
            {
                using (ScrollingStitchSession session = new ScrollingStitchSession(acceptedFrame))
                {
                    Report(report, session, "Captured the first view. Scrolling down...");
                    int wheelDelta = -120, scrollActions = 0, unchangedCount = 0, unchangedWheel = 0;
                    while (scrollActions < MaximumScrollActions)
                    {
                        // Native delays yield the UI; virtual completed delays keep
                        // deterministic checks on their owning runspace/thread.
                        await driver.DelayAsync(0, cancellationToken);
                        int recoveryAttempts = 0;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ScrollingCaptureInterruption interruption = CheckDriver(driver, scrollActions);
                            if (interruption != null) return Complete(session, interruption.Reason, interruption.Detail, report);
                            // Only the native driver's still-pinned target receives
                            // this bounded delta. No global input/cursor fallback.
                            scrollActions++;
                            if (!driver.ScrollWheel(wheelDelta))
                            {
                                interruption = CheckDriver(driver, scrollActions);
                                return Complete(session, interruption == null ? ScrollingCaptureEndReason.CaptureFailed : interruption.Reason,
                                    interruption == null ? "The selected application did not respond to the scroll request. Review the captured portion." : interruption.Detail, report);
                            }
                            Report(report, session, "Waiting for the scrolling view to settle...");
                            using (SettledFrame settled = await WaitForSettledFrameAsync(driver, region, acceptedFrame, false, cancellationToken))
                            {
                                if (settled.Image == null) return Complete(session, settled.Reason, settled.Detail, report);
                                ScrollingAppendResult appended = session.Append(settled.Image);
                                if (appended.Status == ScrollingAppendStatus.Accepted)
                                {
                                    acceptedFrame.Dispose();
                                    acceptedFrame = settled.Image;
                                    settled.Image = null;
                                    unchangedCount = unchangedWheel = 0;
                                    wheelDelta = -ChooseWheelDelta(Math.Abs(wheelDelta), appended.Offset, session.ScrollableHeight);
                                    Report(report, session, "Captured more content. Scrolling down...");
                                    break;
                                }
                                if (appended.Status == ScrollingAppendStatus.Unchanged)
                                {
                                    unchangedCount++;
                                    unchangedWheel += Math.Abs(wheelDelta);
                                    if (unchangedCount >= 4 && unchangedWheel >= 480)
                                        return Complete(session, ScrollingCaptureEndReason.ScrollingStalled,
                                            "Automatic scrolling stopped after repeated requests produced no new content. The bottom is not confirmed; this may be the end, a loading pause, or an application that ignores automatic scrolling. Review the captured portion.", report);
                                    // Partial wheel deltas can legitimately be
                                    // accumulated by the destination until 120.
                                    wheelDelta = -Math.Max(120, Math.Abs(wheelDelta));
                                    Report(report, session, "No movement yet. Waiting and retrying before deciding capture has stalled...");
                                    break;
                                }
                                if (appended.Status == ScrollingAppendStatus.Limit)
                                    return Complete(session, ScrollingCaptureEndReason.LimitReached, appended.Message + " Review the captured portion.", report);
                            }

                            if (recoveryAttempts >= MaximumRecoveryAttempts || Math.Abs(wheelDelta) <= 30)
                                return Complete(session, ScrollingCaptureEndReason.NoReliableOverlap,
                                    "Capture stopped because the next view could not be aligned reliably after smaller-step retries. Content may have changed or the selected area may include fixed sidebars. The accepted portion is preserved for review.", report);
                            cancellationToken.ThrowIfCancellationRequested();
                            interruption = CheckDriver(driver, scrollActions);
                            if (interruption != null) return Complete(session, interruption.Reason, interruption.Detail, report);
                            Report(report, session, "Checking the previous view before retrying a smaller scroll step...");
                            cancellationToken.ThrowIfCancellationRequested();
                            interruption = CheckDriver(driver, scrollActions);
                            if (interruption != null) return Complete(session, interruption.Reason, interruption.Detail, report);
                            scrollActions++;
                            if (!driver.ScrollWheel(-wheelDelta))
                            {
                                interruption = CheckDriver(driver, scrollActions);
                                return Complete(session, interruption == null ? ScrollingCaptureEndReason.NoReliableOverlap : interruption.Reason,
                                    interruption == null ? "The previous view could not be restored for a smaller-step retry. The accepted portion is preserved for review." : interruption.Detail, report);
                            }
                            using (SettledFrame recovered = await WaitForSettledFrameAsync(driver, region, acceptedFrame, true, cancellationToken))
                            {
                                if (recovered.Image == null) return Complete(session, recovered.Reason, recovered.Detail, report);
                                // This must not call Append: a plausible forward
                                // overlap could mutate accepted history. Require
                                // every source RGB pixel to match the owned base.
                                if (!FramesIdentical(acceptedFrame, recovered.Image))
                                    return Complete(session, ScrollingCaptureEndReason.NoReliableOverlap,
                                        "The page changed or did not return to the previous view, so capture stopped instead of guessing a join. The accepted portion is preserved for review.", report);
                            }
                            recoveryAttempts++;
                            wheelDelta = -Math.Max(30, Math.Abs(wheelDelta) / 2);
                        }
                    }
                    return Complete(session, ScrollingCaptureEndReason.LimitReached,
                        "The 300-scroll safety limit was reached. Review the captured portion.", report);
                }
            }
            finally { if (acceptedFrame != null) acceptedFrame.Dispose(); }
        }

        private static ScrollingCaptureInterruption CheckDriver(ScrollingCaptureDriver driver, int scrollActions)
        {
            ScrollingCaptureInterruption interruption = driver.CheckInterruption();
            if (interruption != null) return interruption;
            if (driver.ElapsedMilliseconds() >= MaximumDurationSeconds * 1000L)
                return new ScrollingCaptureInterruption(ScrollingCaptureEndReason.LimitReached, "The three-minute capture limit was reached. Review the captured portion.");
            if (scrollActions >= MaximumScrollActions)
                return new ScrollingCaptureInterruption(ScrollingCaptureEndReason.LimitReached, "The 300-scroll safety limit was reached. Review the captured portion.");
            return null;
        }

        private static Bitmap ReadDriverFrame(ScrollingCaptureDriver driver, Rectangle region)
        {
            Bitmap frame = driver.ReadFrame();
            if (frame == null || frame.Size != region.Size)
            {
                if (frame != null) frame.Dispose();
                throw new InvalidOperationException("The selected view could not be read at its original size.");
            }
            return frame;
        }

        private async Task<SettledFrame> WaitForSettledFrameAsync(ScrollingCaptureDriver driver, Rectangle region,
            Bitmap reference, bool restoringReference, CancellationToken cancellationToken)
        {
            Bitmap previous = null;
            long started = driver.ElapsedMilliseconds();
            int stableSamples = 0;
            bool localizedStreak = false;
            try
            {
                await driver.DelayAsync(SettleDelayMilliseconds, cancellationToken);
                while (driver.ElapsedMilliseconds() - started < MaximumSettleMilliseconds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScrollingCaptureInterruption interruption = CheckDriver(driver, 0);
                    if (interruption != null) return new SettledFrame(interruption.Reason, interruption.Detail);

                    Bitmap current;
                    try { current = ReadDriverFrame(driver, region); }
                    catch (TargetChangedException ex)
                    {
                        return new SettledFrame(ScrollingCaptureEndReason.TargetChanged,
                            ex.Message + " The accepted portion is preserved for review.");
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new SettledFrame(ScrollingCaptureEndReason.CaptureFailed,
                            "Capture stopped: " + ex.Message + " The accepted portion is preserved for review.");
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return new SettledFrame(ScrollingCaptureEndReason.CaptureFailed,
                            "Windows could not read the selected view. The accepted portion is preserved for review.");
                    }
                    bool equivalent, strictlyEquivalent;
                    try
                    {
                        strictlyEquivalent = previous != null && FramesEquivalent(previous, current);
                        equivalent = strictlyEquivalent || (previous != null && FramesStableEnough(previous, current));
                    }
                    catch { current.Dispose(); throw; }
                    if (previous != null) previous.Dispose();
                    previous = current;
                    stableSamples = equivalent ? stableSamples + 1 : 0;
                    localizedStreak = equivalent && (localizedStreak || !strictlyEquivalent);
                    // Minor animation is not evidence that scrolling responded.
                    // Recovery still requires the separate exact pixel check.
                    bool responseObserved = restoringReference ? FramesIdentical(reference, current) : !FramesStableEnough(reference, current);
                    long elapsed = driver.ElapsedMilliseconds() - started;
                    if (stableSamples >= 2 && (!localizedStreak || elapsed >= 900) &&
                        (responseObserved || elapsed >= MinimumUnchangedMilliseconds))
                    {
                        Bitmap accepted = previous;
                        previous = null;
                        return new SettledFrame(accepted);
                    }
                    await driver.DelayAsync(SettleSampleMilliseconds, cancellationToken);
                }
                return new SettledFrame(ScrollingCaptureEndReason.ContentDidNotSettle,
                    "The selected view kept changing, so capture stopped. The accepted portion is preserved for review.");
            }
            finally { if (previous != null) previous.Dispose(); }
        }

        private Bitmap ReadFrame(TargetWindow target)
        {
            string problem = target.GetProblem();
            if (problem != null) throw new TargetChangedException(problem);
            Bitmap frame = captureFrame(target.Region);
            try
            {
                if (frame == null || frame.Width != target.Region.Width || frame.Height != target.Region.Height)
                    throw new InvalidOperationException("The selected view could not be read at its original size.");
                if (IsBlackFrame(frame))
                    throw new InvalidOperationException("The selected view is entirely black or cannot be captured.");
                // Do not accept a frame if the target changed while pixels were
                // read. This remains best effort against external HWND races.
                problem = target.GetProblem();
                if (problem != null) throw new TargetChangedException(problem);
                return frame;
            }
            catch { if (frame != null) frame.Dispose(); throw; }
        }

        private static string GetStopReason(Func<bool> stopRequested, TargetWindow target,
            out ScrollingCaptureEndReason reason)
        {
            reason = ScrollingCaptureEndReason.UserStopped;
            if (stopRequested != null && stopRequested()) return "Capture finished at your request. Review the captured portion.";
            if (ScrollNative.IsKeyDown(0x1B))
            {
                reason = ScrollingCaptureEndReason.EscapePressed;
                return "Capture stopped with Esc. Review the captured portion.";
            }
            if (ScrollNative.IsKeyDown(0x10) || ScrollNative.IsKeyDown(0x11) || ScrollNative.IsKeyDown(0x12))
            {
                reason = ScrollingCaptureEndReason.TargetChanged;
                return "Capture stopped because a modifier key is held. The accepted portion is preserved for review.";
            }
            if ((ScrollNative.IsKeyDown(0x01) || ScrollNative.IsKeyDown(0x02) || ScrollNative.IsKeyDown(0x04)) && !target.PointerIsOnController())
            {
                reason = ScrollingCaptureEndReason.TargetChanged;
                return "Capture stopped because the mouse is being used. The accepted portion is preserved for review.";
            }
            string problem = target.GetProblem();
            if (problem != null)
            {
                reason = ScrollingCaptureEndReason.TargetChanged;
                return problem + " The accepted portion is preserved for review.";
            }
            return null;
        }

        private static ScrollingCaptureResult Complete(ScrollingStitchSession session,
            ScrollingCaptureEndReason reason, string detail, Action<ScrollingCaptureProgress> report)
        {
            Bitmap image = session.BuildImage();
            try
            {
                Report(report, session, detail);
                return new ScrollingCaptureResult(image, session.FrameCount, reason, detail);
            }
            catch { image.Dispose(); throw; }
        }

        private static void Report(Action<ScrollingCaptureProgress> report, ScrollingStitchSession session, string status)
        {
            if (report != null) report(new ScrollingCaptureProgress(session.FrameCount, session.Height, status));
        }

        internal static int ChooseWheelNotches(int currentNotches, int actualOffset, int viewportHeight)
        {
            if (actualOffset <= 0 || viewportHeight < 1) return 1;
            double requested = Math.Floor((viewportHeight * 0.25) * Math.Max(1, currentNotches) / actualOffset);
            return Math.Max(1, Math.Min(2, (int)Math.Min(2.0, requested)));
        }

        internal static int ChooseWheelDelta(int currentDelta, int actualOffset, int scrollableHeight)
        {
            if (actualOffset <= 0 || scrollableHeight < 1) return 120;
            double requested = Math.Floor((scrollableHeight * 0.25) * Math.Max(30, currentDelta) / actualOffset);
            return Math.Max(30, Math.Min(240, (int)Math.Min(240.0, requested)));
        }

        // Recovery requires every source RGB pixel, not the sparse settling or
        // overlap sample, to match. Alpha is not meaningful in desktop captures.
        internal static bool FramesIdentical(Bitmap first, Bitmap second)
        {
            if (first == null || second == null || first.Size != second.Size) return false;
            if (Object.ReferenceEquals(first, second)) return true;
            BitmapData left = null, right = null;
            try
            {
                Rectangle bounds = new Rectangle(Point.Empty, first.Size);
                left = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                right = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                byte[] a = new byte[first.Width * 4], b = new byte[first.Width * 4];
                for (int y = 0; y < first.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(left.Scan0, y * left.Stride), a, 0, a.Length);
                    Marshal.Copy(IntPtr.Add(right.Scan0, y * right.Stride), b, 0, b.Length);
                    for (int x = 0; x < a.Length; x += 4)
                        if (a[x] != b[x] || a[x + 1] != b[x + 1] || a[x + 2] != b[x + 2]) return false;
                }
                return true;
            }
            finally
            {
                if (right != null) second.UnlockBits(right);
                if (left != null) first.UnlockBits(left);
            }
        }

        // A caret, spinner or small changing badge must not restart settling
        // forever. Require 82 of 96 independent tiles to remain stable. This
        // only admits a frame for the stitcher's separate strict join check;
        // it never relaxes overlap validation or a recovery comparison.
        internal static bool FramesStableEnough(Bitmap first, Bitmap second)
        {
            if (FramesEquivalent(first, second)) return true;
            if (first == null || second == null || first.Size != second.Size) return false;
            BitmapData left = null, right = null;
            try
            {
                Rectangle bounds = new Rectangle(Point.Empty, first.Size);
                left = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                right = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                int unstable = 0;
                for (int tileY = 0; tileY < 8; tileY++)
                    for (int tileX = 0; tileX < 12; tileX++)
                    {
                        long difference = 0;
                        int changed = 0;
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                            {
                                int row = Math.Min(first.Height - 1, ((tileY * 8 + y * 2 + 1) * first.Height) / 64);
                                int column = Math.Min(first.Width - 1, ((tileX * 8 + x * 2 + 1) * first.Width) / 96);
                                int a = Marshal.ReadInt32(IntPtr.Add(left.Scan0, row * left.Stride + column * 4));
                                int b = Marshal.ReadInt32(IntPtr.Add(right.Scan0, row * right.Stride + column * 4));
                                int delta = Math.Abs((a & 255) - (b & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255));
                                difference += delta;
                                if (delta > 30) changed++;
                            }
                        if ((difference > 16 * 3 || changed > 0) && ++unstable > 14) return false;
                    }
                return true;
            }
            finally
            {
                if (right != null) second.UnlockBits(right);
                if (left != null) first.UnlockBits(left);
            }
        }

        private static void ValidateRegion(Rectangle region)
        {
            if (region.Width < 80 || region.Height < 100)
                throw new ArgumentException("Select a scrolling content area at least 80 pixels wide and 100 pixels high.", "physicalRegion");
            if (region.Width > ScrollingStitchSession.MaximumWidth || region.Height > ScrollingStitchSession.MaximumHeight ||
                (long)region.Width * region.Height > ScrollingStitchSession.MaximumPixels)
                throw new ArgumentException("The selected area exceeds the scrolling image size limit.", "physicalRegion");
            Rectangle desktop = SystemInformation.VirtualScreen;
            if (!desktop.Contains(region))
                throw new ArgumentException("Keep the scrolling selection inside the available desktop.", "physicalRegion");
            long centerX = (long)region.Left + region.Width / 2;
            long centerY = (long)region.Top + region.Height / 2;
            if (centerX < short.MinValue || centerX > short.MaxValue || centerY < short.MinValue || centerY > short.MaxValue)
                throw new ArgumentException("The selected area is outside the screen coordinate range supported by automatic scrolling.", "physicalRegion");
        }

        // Sampling is only for settling. The stitcher independently validates
        // overlap using the original pixels, without resizing the output.
        internal static bool FramesEquivalent(Bitmap first, Bitmap second)
        {
            if (first == null || second == null || first.Size != second.Size) return false;
            if (Object.ReferenceEquals(first, second)) return true;
            BitmapData left = null, right = null;
            try
            {
                Rectangle bounds = new Rectangle(Point.Empty, first.Size);
                left = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                right = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                long difference = 0;
                int changed = 0, samples = 0;
                for (int y = 0; y < 48; y++)
                {
                    int row = Math.Min(first.Height - 1, ((2 * y + 1) * first.Height) / 96);
                    for (int x = 0; x < 64; x++)
                    {
                        int column = Math.Min(first.Width - 1, ((2 * x + 1) * first.Width) / 128);
                        int a = Marshal.ReadInt32(IntPtr.Add(left.Scan0, row * left.Stride + column * 4));
                        int b = Marshal.ReadInt32(IntPtr.Add(right.Scan0, row * right.Stride + column * 4));
                        int delta = Math.Abs((a & 255) - (b & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255));
                        difference += delta;
                        if (delta > 30) changed++;
                        samples++;
                    }
                }
                return difference <= (long)samples * 3 && changed <= samples / 200;
            }
            finally
            {
                if (right != null) second.UnlockBits(right);
                if (left != null) first.UnlockBits(left);
            }
        }

        internal static bool IsBlackFrame(Bitmap frame)
        {
            BitmapData data = null;
            try
            {
                data = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                byte[] row = new byte[frame.Width * 4];
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x += 4)
                        if (row[x] != 0 || row[x + 1] != 0 || row[x + 2] != 0) return false;
                }
                return true;
            }
            finally { if (data != null) frame.UnlockBits(data); }
        }

        private sealed class SettledFrame : IDisposable
        {
            internal Bitmap Image;
            internal ScrollingCaptureEndReason Reason;
            internal string Detail;
            internal SettledFrame(Bitmap image) { Image = image; }
            internal SettledFrame(ScrollingCaptureEndReason reason, string detail) { Reason = reason; Detail = detail; }
            public void Dispose() { if (Image != null) Image.Dispose(); Image = null; }
        }

        private sealed class TargetChangedException : InvalidOperationException
        {
            internal TargetChangedException(string message) : base(message) { }
        }

        private sealed class TargetWindow
        {
            internal Rectangle Region;
            internal IntPtr Root;
            private IntPtr recipient;
            private uint rootProcess, rootThread, recipientProcess, recipientThread;
            private Rectangle rootBounds, recipientBounds, clientBounds;
            private ScrollNative.NativePoint center;
            private IntPtr knownController;
            private uint controllerThread, controllerProcess;

            internal bool IsForeground { get { return ScrollNative.GetForegroundWindow() == Root; } }

            internal static TargetWindow Bind(Rectangle region, IntPtr knownController)
            {
                TargetWindow target = new TargetWindow();
                target.Region = region;
                if (knownController != IntPtr.Zero)
                {
                    target.knownController = knownController;
                    target.controllerThread = ScrollNative.GetWindowThreadProcessId(knownController, out target.controllerProcess);
                    if (target.controllerThread == 0 || target.controllerProcess != (uint)Process.GetCurrentProcess().Id ||
                        ScrollNative.GetAncestor(knownController, 2) != knownController)
                        throw new ArgumentException("The capture controller must be an existing window owned by this application.", "knownController");
                }
                target.center = new ScrollNative.NativePoint(region.Left + region.Width / 2, region.Top + region.Height / 2);
                target.recipient = target.WindowAtPoint(target.center);
                target.Root = ScrollNative.GetAncestor(target.recipient, 2);
                target.rootThread = ScrollNative.GetWindowThreadProcessId(target.Root, out target.rootProcess);
                target.recipientThread = ScrollNative.GetWindowThreadProcessId(target.recipient, out target.recipientProcess);
                if (target.Root == IntPtr.Zero || target.recipient == IntPtr.Zero || target.rootThread == 0 ||
                    target.rootProcess == (uint)Process.GetCurrentProcess().Id || target.IsDesktopOrTaskbar())
                    throw new InvalidOperationException("Select scrolling content inside another application window.");
                if (!ScrollNative.TryGetBounds(target.Root, out target.rootBounds) ||
                    !ScrollNative.TryGetBounds(target.recipient, out target.recipientBounds) ||
                    !ScrollNative.TryGetClientBounds(target.Root, out target.clientBounds) || !target.clientBounds.Contains(region))
                    throw new InvalidOperationException("Select only the scrolling content inside one application window.");
                return target;
            }

            internal string GetProblem(bool requireForeground = true)
            {
                if (!MatchesIdentity(Root, rootThread, rootProcess) || !MatchesIdentity(recipient, recipientThread, recipientProcess))
                    return "The selected window is no longer available.";
                if (!ScrollNative.IsWindowVisible(Root) || ScrollNative.IsIconic(Root) || ScrollNative.IsCloaked(Root))
                    return "The selected window is no longer visible.";
                if (requireForeground && !IsForeground) return "The active window changed.";
                Rectangle currentRoot, currentRecipient, currentClient;
                if (!ScrollNative.TryGetBounds(Root, out currentRoot) || currentRoot != rootBounds ||
                    !ScrollNative.TryGetBounds(recipient, out currentRecipient) || currentRecipient != recipientBounds ||
                    !ScrollNative.TryGetClientBounds(Root, out currentClient) || currentClient != clientBounds)
                    return "The selected window moved or changed size.";
                IntPtr centerWindow = WindowAtPoint(center);
                if (centerWindow != recipient)
                    return "The selected scrolling area changed or is covered by another window.";
                // Corner and center checks supplement the top-level overlap scan.
                for (int y = 0; y < 3; y++)
                    for (int x = 0; x < 3; x++)
                    {
                        ScrollNative.NativePoint point = new ScrollNative.NativePoint(
                            x == 0 ? Region.Left + 2 : x == 1 ? center.X : Region.Right - 3,
                            y == 0 ? Region.Top + 2 : y == 1 ? center.Y : Region.Bottom - 3);
                        if (ScrollNative.GetAncestor(WindowAtPoint(point), 2) != Root)
                            return "The selected area is covered by another window.";
                    }
                IntPtr above = ScrollNative.GetWindow(Root, 3); // GW_HWNDPREV
                for (int count = 0; above != IntPtr.Zero && count < 512; count++)
                {
                    Rectangle bounds;
                    if (!IsKnownController(above) && ScrollNative.IsWindowVisible(above) && !ScrollNative.IsIconic(above) && !ScrollNative.IsCloaked(above) &&
                        ScrollNative.TryGetBounds(above, out bounds) && bounds.IntersectsWith(Region))
                        return "Another window is covering the selected area.";
                    above = ScrollNative.GetWindow(above, 3);
                }
                if (above != IntPtr.Zero) return "The selected area could not be verified as visible.";
                return null;
            }

            private bool IsKnownController(IntPtr window)
            {
                return knownController != IntPtr.Zero && ScrollNative.GetAncestor(window, 2) == knownController &&
                    MatchesIdentity(knownController, controllerThread, controllerProcess);
            }

            internal bool PointerIsOnController()
            {
                ScrollNative.NativePoint point;
                return knownController != IntPtr.Zero && ScrollNative.GetCursorPos(out point) &&
                    IsKnownController(ScrollNative.WindowFromPoint(point));
            }

            private IntPtr WindowAtPoint(ScrollNative.NativePoint point)
            {
                IntPtr actual = ScrollNative.WindowFromPoint(point);
                if (!IsKnownController(actual))
                    return DescendantAtPoint(ScrollNative.GetAncestor(actual, 2), point);
                // Only the exact pinned same-process controller may be skipped.
                // No general own-process or layered-window exemptions are used.
                IntPtr candidate = ScrollNative.GetWindow(knownController, 2); // GW_HWNDNEXT
                for (int count = 0; candidate != IntPtr.Zero && count < 512; count++)
                {
                    Rectangle bounds;
                    if (!IsKnownController(candidate) && ScrollNative.IsWindowVisible(candidate) && !ScrollNative.IsIconic(candidate) &&
                        !ScrollNative.IsCloaked(candidate) && ScrollNative.TryGetBounds(candidate, out bounds) && bounds.Contains(point.X, point.Y))
                        return DescendantAtPoint(candidate, point);
                    candidate = ScrollNative.GetWindow(candidate, 2);
                }
                return IntPtr.Zero;
            }

            private static IntPtr DescendantAtPoint(IntPtr candidate, ScrollNative.NativePoint point)
            {
                // Use the same child resolution with and without the HUD.
                // WindowFromPoint specially skips static labels; a canonical
                // child walk avoids weakening identity checks for that case.
                for (int depth = 0; candidate != IntPtr.Zero && depth < 32; depth++)
                {
                    ScrollNative.NativePoint local = point;
                    if (!ScrollNative.ScreenToClient(candidate, ref local)) return IntPtr.Zero;
                    IntPtr child = ScrollNative.ChildWindowFromPointEx(candidate, local, 7);
                    if (child == IntPtr.Zero || child == candidate) return candidate;
                    candidate = child;
                }
                return IntPtr.Zero;
            }

            internal bool ScrollWheel(int delta)
            {
                // Recheck immediately before each bounded dispatch, including
                // recovery, without retargeting or moving the pointer.
                if (GetProblem() != null) return false;
                if (delta == 0 || delta < -240 || delta > 240) throw new ArgumentOutOfRangeException("delta");
                int wheel = unchecked((int)((uint)(ushort)(short)delta << 16));
                int coordinates = unchecked((int)((uint)(ushort)center.X | ((uint)(ushort)center.Y << 16)));
                UIntPtr ignored;
                return ScrollNative.SendMessageTimeout(recipient, 0x020A, new IntPtr(wheel), new IntPtr(coordinates),
                    0x0001 | 0x0002 | 0x0020, 250, out ignored) != IntPtr.Zero;
            }

            private bool IsDesktopOrTaskbar()
            {
                if (Root == ScrollNative.GetShellWindow()) return true;
                StringBuilder name = new StringBuilder(128);
                ScrollNative.GetClassName(Root, name, name.Capacity);
                string value = name.ToString();
                return value == "Progman" || value == "WorkerW" || value == "Shell_TrayWnd" || value == "Shell_SecondaryTrayWnd";
            }

            private static bool MatchesIdentity(IntPtr window, uint expectedThread, uint expectedProcess)
            {
                uint process;
                return ScrollNative.IsWindow(window) && ScrollNative.GetWindowThreadProcessId(window, out process) == expectedThread && process == expectedProcess;
            }
        }

        private static class ScrollNative
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct NativePoint
            {
                internal int X, Y;
                internal NativePoint(int x, int y) { X = x; Y = y; }
            }
            [StructLayout(LayoutKind.Sequential)]
            private struct NativeRect
            {
                internal int Left, Top, Right, Bottom;
                internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
            }
            [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(NativePoint point);
            [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
            [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
            [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
            [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
            [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out NativePoint point);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
            [DllImport("user32.dll")] internal static extern IntPtr ChildWindowFromPointEx(IntPtr window, NativePoint point, uint flags);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
            [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
            private static extern int GetDwmInt(IntPtr window, int attribute, out int value, int size);

            internal static bool IsKeyDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
            internal static bool TryGetBounds(IntPtr window, out Rectangle bounds)
            {
                NativeRect native;
                bool success = GetWindowRect(window, out native);
                bounds = success ? native.ToRectangle() : Rectangle.Empty;
                return success;
            }
            internal static bool TryGetClientBounds(IntPtr window, out Rectangle bounds)
            {
                NativeRect native;
                NativePoint origin = new NativePoint(0, 0);
                bool success = GetClientRect(window, out native) && ClientToScreen(window, ref origin);
                bounds = success ? new Rectangle(origin.X + native.Left, origin.Y + native.Top, native.Right - native.Left, native.Bottom - native.Top) : Rectangle.Empty;
                return success;
            }
            internal static bool IsCloaked(IntPtr window)
            {
                try
                {
                    int cloaked;
                    return GetDwmInt(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }
    }
}
