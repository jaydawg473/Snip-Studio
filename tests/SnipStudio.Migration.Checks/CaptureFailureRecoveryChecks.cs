using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckCaptureFailureRecoveryAsync()
    {
        await CheckCaptureCountdownCancellationAsync();
        completedGroups.Add("Capture countdown honors HUD and operation cancellation on the final tick");
        await CheckScrollingFrameTimeoutAsync();
        CheckScrollingPreviewJoinRows();
        CheckTaskbarRestorationWarning(rejectRestoreRequest: true, observeRestoredTaskbar: false);
        CheckTaskbarRestorationWarning(rejectRestoreRequest: false, observeRestoredTaskbar: false);
        CheckTaskbarRestorationWarning(rejectRestoreRequest: false, observeRestoredTaskbar: true);
        completedGroups.Add("Synthetic scrolling preserves accepted pixels after frame timeout and reports taskbar restoration failures");
    }

    private static async Task CheckCaptureCountdownCancellationAsync()
    {
        (double Elapsed, bool CancelHud, bool CancelOperation, string Outcome)[] scenarios =
        [
            (.999, true, false, "HUD cancel before expiration prevents capture"),
            (1.005, true, false, "HUD cancel during the final wait wins over expiration"),
            (1.005, false, true, "Operation cancel is observed when the final wait completes"),
            (1.005, false, false, "An uncanceled countdown completes at its deadline")
        ];
        foreach (var scenario in scenarios)
        {
            using var cancellation = new CancellationTokenSource();
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            double elapsed = .980;
            bool canceledByUser = false;
            int updates = 0;
            Task<bool> result = CaptureCoordinator.RunCaptureCountdownAsync(1, () => elapsed, () => canceledByUser,
                (remaining, token) =>
                {
                    if (++updates != 1 || remaining != 1 || token != cancellation.Token)
                        throw new InvalidOperationException("Unexpected capture countdown update.");
                    return tick.Task;
                }, cancellation.Token);
            Check(updates == 1 && !result.IsCompleted, "The capture countdown waits with one second displayed");

            elapsed = scenario.Elapsed;
            canceledByUser = scenario.CancelHud;
            if (scenario.CancelOperation) cancellation.Cancel();
            tick.SetResult();

            if (scenario.CancelOperation)
                await ExpectAsync<OperationCanceledException>(() => result, scenario.Outcome);
            else
                Check(await result == !scenario.CancelHud, scenario.Outcome);
        }
    }

    private static void CheckScrollingPreviewJoinRows()
    {
        const int width = 160, height = 240, footer = 24, firstOffset = 80, secondOffset = 140;
        using var body = new Bitmap(width, height + secondOffset, PixelFormat.Format32bppArgb);
        var random = new Random(473);
        for (int y = 0; y < body.Height; y++)
            for (int x = 0; x < body.Width; x++)
                body.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        using Bitmap first = BuildFrame(0);
        using Bitmap next = BuildFrame(firstOffset);
        using Bitmap last = BuildFrame(secondOffset);
        using var session = new ScrollingStitchSession(first);
        Check(session.GetJoinRows().Length == 0, "A single captured view has no stitch marker");
        Check(session.Append(next).Status == ScrollingAppendStatus.Accepted &&
              session.Append(last).Status == ScrollingAppendStatus.Accepted,
            "Synthetic scrolling preview accepts both unique overlaps");
        int[] markerRows = [height - footer, height - footer + firstOffset];
        Check(session.GetJoinRows().SequenceEqual(markerRows),
            "Preview markers follow the actual strip boundaries above a sticky footer");
        Check(session.Append(last).Status == ScrollingAppendStatus.Unchanged &&
              session.GetJoinRows().SequenceEqual(markerRows),
            "Unchanged views leave the accepted stitch markers intact");
        using Bitmap stitched = session.BuildImage();
        Check(markerRows.All(y => stitched.GetPixel(53, y).ToArgb() == body.GetPixel(53, y).ToArgb()) &&
              stitched.Height == height + secondOffset,
            "The exported pixels underneath the displayed stitch markers are untouched");

        Bitmap BuildFrame(int offset)
        {
            var frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(frame);
            graphics.Clear(Color.DarkGreen);
            var content = new Rectangle(0, 0, width, height - footer);
            graphics.DrawImage(body, content, new Rectangle(0, offset, content.Width, content.Height), GraphicsUnit.Pixel);
            return frame;
        }
    }

    private static async Task CheckScrollingFrameTimeoutAsync()
    {
        var region = new Rectangle(0, 0, 160, 240);
        using var first = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(first)) graphics.Clear(Color.CornflowerBlue);
        first.SetPixel(5, 13, Color.DarkOrange);
        const string timeoutMessage = "The next display frame took too long.";
        int reads = 0;
        int scrolls = 0;
        long clock = 0;
        var driver = new ScrollingCaptureDriver
        {
            ReadFrame = () => ++reads == 1 ? new Bitmap(first) : throw new TimeoutException(timeoutMessage),
            ScrollWheel = _ => { scrolls++; return true; },
            CheckInterruption = () => null!,
            DelayAsync = (milliseconds, token) =>
            {
                token.ThrowIfCancellationRequested();
                clock += Math.Max(1, milliseconds);
                return Task.CompletedTask;
            },
            ElapsedMilliseconds = () => clock
        };
        var service = new ScrollingCaptureService();
        using ScrollingCaptureResult partial = await service.CaptureSessionAsync(region, driver, _ => { }, CancellationToken.None);
        Check(reads == 2 && scrolls == 1, "The timeout happened after an accepted first view and one scroll request");
        Check(partial.EndReason == ScrollingCaptureEndReason.CaptureFailed && partial.StoppedEarly && partial.FrameCount == 1,
            "A later frame timeout returns an explicitly partial scrolling capture");
        Check(partial.Detail.Contains(timeoutMessage, StringComparison.Ordinal) && partial.Image.Size == region.Size,
            "The scrolling partial result reports the timeout and preserves image dimensions");
        Check(partial.Image.GetPixel(5, 13).ToArgb() == first.GetPixel(5, 13).ToArgb() &&
              partial.Image.GetPixel(100, 200).ToArgb() == first.GetPixel(100, 200).ToArgb(),
            "Accepted pixels remain available for review after a later frame timeout");

        await ExpectAsync<TimeoutException>(() => service.CaptureSessionAsync(region, driver, _ => { }, CancellationToken.None),
            "A first-frame timeout cannot return an uninitialized partial capture");

        reads = 0;
        using var cancellation = new CancellationTokenSource();
        driver.ReadFrame = () =>
        {
            if (++reads == 1) return new Bitmap(first);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The fixture failed to cancel.");
        };
        await ExpectAsync<OperationCanceledException>(() => service.CaptureSessionAsync(region, driver, _ => { }, cancellation.Token),
            "Cancellation discards accepted scrolling pixels instead of returning a partial result");
    }

    private static void CheckTaskbarRestorationWarning(bool rejectRestoreRequest, bool observeRestoredTaskbar)
    {
        var handle = new IntPtr(202);
        bool visible = true;
        long clock = 0;
        int restorationAttempts = 0;
        var operations = new TaskbarVisibilityScope.Operations
        {
            Enumerate = () => [handle],
            Visible = window => window == handle && visible,
            Exists = window => window == handle,
            Primary = _ => false,
            Process = _ => 123u,
            ClassName = _ => "Shell_SecondaryTrayWnd",
            Show = (_, command) =>
            {
                if (command == 0) { visible = false; return true; }
                restorationAttempts++;
                if (rejectRestoreRequest) return false;
                visible = observeRestoredTaskbar;
                return true;
            },
            EnumerationError = () => 0,
            Clock = () => clock,
            Delay = milliseconds => clock += milliseconds,
            Flush = () => { }
        };
        using var scope = TaskbarVisibilityScope.HideWithOperations(keepPrimaryVisible: true, bestEffort: false, operations);
        Check(!visible && scope.HideConfirmed, "The taskbar fixture entered capture with its secondary taskbar hidden");
        string warning = scope.RestoreAfterCapture();
        if (observeRestoredTaskbar)
            Check(visible && warning.Length == 0 && restorationAttempts == 1,
                "Successful taskbar restoration finishes without a warning");
        else
            Check(!visible && warning.Contains(rejectRestoreRequest ? "queue restoration" : "were restored", StringComparison.Ordinal) &&
                  restorationAttempts == 2,
                "An unsuccessful taskbar restoration returns its specific warning after the bounded retry");
        int attempts = restorationAttempts;
        scope.Dispose();
        Check(restorationAttempts == attempts, "Disposing a completed taskbar restoration does not retry or override its capture result");
    }
}
