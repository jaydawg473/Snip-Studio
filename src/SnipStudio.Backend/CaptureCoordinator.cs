#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

/// <summary>
/// Owns one capture at a time, including selection, hidden-window lifetime and
/// bitmap disposal. The legacy pixel overlay and stitcher run on an isolated
/// STA with a real WinForms message pump, never on the WinUI dispatcher.
/// No executable, global input hook or security setting is created/changed.
/// </summary>
public static class CaptureCoordinator
{
    private static readonly SemaphoreSlim captureGate = new(1, 1);
    private static readonly object flushGate = new();
    private static Task<int>? compositorFlush;

    public static Task<CapturedImage?> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, recording: false);
        return RunOnCaptureThreadAsync(async token =>
        {
            using var owner = await HiddenCaptureOwner.HideAsync(request.OwnerWindow, token);
            if (!await WaitForDelayAsync(request.DelaySeconds, token)) return null;
            await SettleCompositorAsync(token);
            using var options = new CaptureOptionScope(request, token);
            ValidateDesktopSize();
            token.ThrowIfCancellationRequested();
            using Bitmap? image = global::SnipStudio.CaptureService.CaptureWithWarnings(ToLegacyMode(request.Kind), out string warning);
            token.ThrowIfCancellationRequested();
            return image is null ? null : await EncodeAsync(image, 1, warning, false, token);
        }, cancellationToken);
    }

    public static Task<CaptureRectangle?> SelectRecordingRegionAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
        SelectRecordingRegionAsync(request, null, cancellationToken);

    public static Task<CaptureRectangle?> AdjustRecordingRegionAsync(nint ownerWindow, RecordingRegion initialRegion,
        CancellationToken cancellationToken = default)
    {
        if (initialRegion.Width < 2 || initialRegion.Height < 2)
            throw new ArgumentException("Select a recording area at least 2 × 2 pixels.", nameof(initialRegion));
        return SelectRecordingRegionAsync(new CaptureRequest { OwnerWindow = ownerWindow }, initialRegion, cancellationToken);
    }

    private static Task<CaptureRectangle?> SelectRecordingRegionAsync(CaptureRequest request, RecordingRegion? initialRegion,
        CancellationToken cancellationToken)
    {
        Validate(request, recording: true);
        return RunOnCaptureThreadAsync<CaptureRectangle?>(async token =>
        {
            using var owner = await HiddenCaptureOwner.HideAsync(request.OwnerWindow, token);
            if (!await WaitForDelayAsync(request.DelaySeconds, token)) return null;
            await SettleCompositorAsync(token);
            using var options = new CaptureOptionScope(request, token);
            ValidateDesktopSize();
            Rectangle? bounds = initialRegion is { } selected
                ? global::SnipStudio.CaptureService.AdjustRecordingRegion(selected.Rectangle)
                : global::SnipStudio.CaptureService.SelectRecordingRegion(ToLegacyMode(request.Kind),
                    request.HideTaskbars, request.KeepPrimaryTaskbarVisible);
            token.ThrowIfCancellationRequested();
            return bounds is { } region ? new CaptureRectangle(region.X, region.Y, region.Width, region.Height) : null;
        }, cancellationToken);
    }

    public static Task<CapturedImage?> CaptureScrollingAsync(CaptureRequest request,
        IProgress<CaptureProgress>? progress = null, Func<bool>? finishRequested = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request, recording: false);
        return RunOnCaptureThreadAsync(async token =>
        {
            using var owner = await HiddenCaptureOwner.HideAsync(request.OwnerWindow, token);
            if (!await WaitForDelayAsync(request.DelaySeconds, token)) return null;
            await SettleCompositorAsync(token);
            using var options = new CaptureOptionScope(request, token);
            ValidateDesktopSize();
            Rectangle? chosen = global::SnipStudio.CaptureService.SelectScrollingRegion(request.HideTaskbars, request.KeepPrimaryTaskbarVisible);
            token.ThrowIfCancellationRequested();
            if (chosen is not { } region) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var controller = new CaptureSessionHud(region, linked);
            controller.Show();
            using var taskbars = request.HideTaskbars
                ? global::SnipStudio.TaskbarVisibilityScope.Hide(request.KeepPrimaryTaskbarVisible) : null;
            var service = new global::SnipStudio.ScrollingCaptureService(bounds =>
            {
                linked.Token.ThrowIfCancellationRequested();
                // Older Windows builds may not support capture exclusion.
                // Always hide an intersecting HUD for the actual pixel read.
                bool hideHud = controller.Visible && !controller.IsOutsideCapture;
                if (hideHud) controller.Hide();
                try
                {
                    if (hideHud) FlushCompositionBounded();
                    taskbars?.VerifyHidden();
                    return global::SnipStudio.CaptureService.CaptureRegion(bounds);
                }
                finally { if (hideHud && !controller.IsDisposed) controller.Show(); }
            }, controller.Handle);
            using var result = await service.CaptureAsync(region, update =>
            {
                controller.UpdateProgress(update.FrameCount, update.Height, update.Status);
                progress?.Report(new CaptureProgress(update.FrameCount, update.Height, update.Status));
            }, () => controller.FinishRequested || (finishRequested?.Invoke() ?? false), linked.Token);
            if (result.EndReason == global::SnipStudio.ScrollingCaptureEndReason.EscapePressed)
                throw new OperationCanceledException("Scrolling capture canceled with Escape.", token);
            controller.FinishAndClose();
            token.ThrowIfCancellationRequested();
            return await EncodeAsync(result.Image, result.FrameCount, result.Detail, result.StoppedEarly, token);
        }, cancellationToken);
    }

    private static async Task<CapturedImage> EncodeAsync(Bitmap image, int frameCount, string? warning,
        bool stoppedEarly, CancellationToken token)
    {
        // The capture thread owns the bitmap until this awaited encoder ends.
        int width = image.Width, height = image.Height;
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var bytes = new MemoryStream();
            image.Save(bytes, ImageFormat.Png);
            token.ThrowIfCancellationRequested();
            return new CapturedImage(bytes.ToArray(), width, height, frameCount,
                string.IsNullOrWhiteSpace(warning) ? null : warning, stoppedEarly);
        }, CancellationToken.None);
    }

    private static Task<T> RunOnCaptureThreadAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!captureGate.Wait(0)) throw new InvalidOperationException("Another capture is already in progress. Finish or cancel it first.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            T result = default!;
            Exception? error = null;
            try
            {
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                using var context = new ApplicationContext();
                using var synchronization = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(synchronization);
                using var registration = token.Register(() =>
                {
                    try
                    {
                        if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated)
                            dispatcher.BeginInvoke((Action)CloseSelectionOverlays);
                    }
                    catch (InvalidOperationException) { /* The private dispatcher already exited. */ }
                });
                dispatcher.BeginInvoke((Action)(async () =>
                {
                    try { token.ThrowIfCancellationRequested(); result = await action(token); }
                    catch (Exception exception) { error = exception; }
                    finally { context.ExitThread(); }
                }));
                Application.Run(context);
            }
            catch (Exception exception) { error ??= exception; }
            finally
            {
                captureGate.Release();
                if (error is OperationCanceledException cancellation)
                    completion.TrySetCanceled(cancellation.CancellationToken.IsCancellationRequested ? cancellation.CancellationToken : new CancellationToken(true));
                else if (error is not null) completion.TrySetException(error);
                else completion.TrySetResult(result);
            }
        }) { IsBackground = true, Name = "Snip Studio capture STA" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { captureGate.Release(); throw; }
        return completion.Task;
    }

    private static void CloseSelectionOverlays()
    {
        // Application.OpenForms can contain windows on other application threads.
        // Close only these capture overlay types on this exact private STA.
        var forms = new List<Form>();
        foreach (Form form in Application.OpenForms) forms.Add(form);
        foreach (Form? form in forms)
            if (form is not null && !form.IsDisposed && !form.InvokeRequired &&
                (form is global::SnipStudio.CaptureOverlay || form is CaptureDelayHud || form is CaptureSessionHud))
                form.Close();
    }

    private static async Task<bool> WaitForDelayAsync(int seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (seconds == 0) return true;
        using var hud = new CaptureDelayHud(seconds);
        hud.Show();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();
            if (hud.IsDisposed || hud.CancelRequested) return false;
            hud.SetSeconds(Math.Max(1, (int)Math.Ceiling(seconds - clock.Elapsed.TotalSeconds)));
            await Task.Delay(70, token);
        }
        hud.FinishAndClose();
        await SettleCompositorAsync(token);
        return true;
    }

    private static void Validate(CaptureRequest request, bool recording)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentOutOfRangeException(nameof(request), "Choose a supported capture mode.");
        if (request.DelaySeconds is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(request), "Capture delay must be between zero and ten seconds.");
        if (recording && request.Kind is not CaptureKind.Rectangle and not CaptureKind.Window)
            throw new ArgumentException("Choose Rectangle or Window for a recording region.", nameof(request));
        HiddenCaptureOwner.ValidateWindow(request.OwnerWindow);
    }

    private static void ValidateDesktopSize()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        if (bounds.Width < 1 || bounds.Height < 1) throw new InvalidOperationException("No desktop is available to capture.");
        if (bounds.Width > 32768 || bounds.Height > 32768 || (long)bounds.Width * bounds.Height > 64L * 1024 * 1024)
            throw new InvalidOperationException("The connected desktop exceeds the 64-megapixel capture limit. Reduce the display layout or resolution before capturing.");
    }

    private static global::SnipStudio.CaptureMode ToLegacyMode(CaptureKind kind) => kind switch
    {
        CaptureKind.Rectangle => global::SnipStudio.CaptureMode.Rectangle,
        CaptureKind.Freeform => global::SnipStudio.CaptureMode.Freeform,
        CaptureKind.Window => global::SnipStudio.CaptureMode.Window,
        CaptureKind.Monitor => global::SnipStudio.CaptureMode.FullScreen,
        CaptureKind.AllMonitors => global::SnipStudio.CaptureMode.AllMonitors,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task SettleCompositorAsync(CancellationToken token)
    {
        // Hidden popup shadows/animations must settle before the frozen read.
        await Task.Delay(220, token);
        Task<int> pending;
        lock (flushGate)
        {
            if (compositorFlush is null || compositorFlush.IsCompleted)
                compositorFlush = Task.Run(FlushComposition);
            pending = compositorFlush;
        }
        try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(2), token); }
        catch (TimeoutException) { throw new TimeoutException("The Windows compositor did not settle within two seconds. Your previous image is kept."); }
    }

    private static int FlushComposition()
    {
        try { return DwmFlush(); }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private static void FlushCompositionBounded()
    {
        Task<int> pending;
        lock (flushGate)
        {
            if (compositorFlush is null || compositorFlush.IsCompleted)
                compositorFlush = Task.Run(FlushComposition);
            pending = compositorFlush;
        }
        if (!pending.Wait(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The Windows compositor did not settle before reading the selected region. The accepted portion is preserved for review.");
        Marshal.ThrowExceptionForHR(pending.GetAwaiter().GetResult());
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private sealed class CaptureOptionScope : IDisposable
    {
        private readonly bool hdr = global::SnipStudio.CaptureService.UseHdrColorCorrection;
        private readonly bool hide = global::SnipStudio.CaptureService.HideTaskbarsDuringCapture;
        private readonly bool primary = global::SnipStudio.CaptureService.KeepPrimaryTaskbarVisible;
        private readonly CancellationToken previousCancellation = global::SnipStudio.HdrCaptureService.CaptureCancellation.Value;
        internal CaptureOptionScope(CaptureRequest request, CancellationToken token)
        {
            global::SnipStudio.HdrCaptureService.CaptureCancellation.Value = token;
            global::SnipStudio.CaptureService.UseHdrColorCorrection = request.UseHdrColorCorrection;
            global::SnipStudio.CaptureService.HideTaskbarsDuringCapture = request.HideTaskbars;
            global::SnipStudio.CaptureService.KeepPrimaryTaskbarVisible = request.KeepPrimaryTaskbarVisible;
        }
        public void Dispose()
        {
            global::SnipStudio.HdrCaptureService.CaptureCancellation.Value = previousCancellation;
            global::SnipStudio.CaptureService.UseHdrColorCorrection = hdr;
            global::SnipStudio.CaptureService.HideTaskbarsDuringCapture = hide;
            global::SnipStudio.CaptureService.KeepPrimaryTaskbarVisible = primary;
        }
    }

    private sealed class HiddenCaptureOwner : IDisposable
    {
        private readonly nint window;
        private readonly uint thread;
        private readonly uint process;
        private readonly bool wasVisible;
        private readonly bool changedTransitions;
        private readonly int transitions;
        private HiddenCaptureOwner(nint handle)
        {
            window = handle;
            if (handle == 0) return;
            thread = GetWindowThreadProcessId(handle, out process);
            wasVisible = IsWindowVisible(handle);
            if (DwmGetWindowAttribute(handle, 3, out transitions, sizeof(int)) == 0)
            {
                int disabled = 1;
                changedTransitions = DwmSetWindowAttribute(handle, 3, ref disabled, sizeof(int)) == 0;
            }
            if (wasVisible) _ = ShowWindowAsync(window, 0);
        }
        internal static void ValidateWindow(nint handle)
        {
            if (handle == 0) return;
            if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out uint process) == 0 || process != Environment.ProcessId)
                throw new ArgumentException("The capture owner must be an existing Snip Studio window.", nameof(handle));
        }
        internal static async Task<HiddenCaptureOwner> HideAsync(nint handle, CancellationToken token)
        {
            ValidateWindow(handle);
            var scope = new HiddenCaptureOwner(handle);
            try
            {
                var timer = Stopwatch.StartNew();
                while (handle != 0 && scope.IsSameWindow && IsWindowVisible(handle))
                {
                    token.ThrowIfCancellationRequested();
                    if (timer.ElapsedMilliseconds >= 750)
                        throw new InvalidOperationException("Snip Studio did not finish hiding. Your previous image is kept; try capture again.");
                    await Task.Delay(10, token);
                }
                token.ThrowIfCancellationRequested();
                return scope;
            }
            catch { scope.Dispose(); throw; }
        }
        private bool IsSameWindow => window != 0 && IsWindow(window) && GetWindowThreadProcessId(window, out uint actual) == thread && actual == process;
        public void Dispose()
        {
            if (!IsSameWindow) return;
            if (changedTransitions)
            {
                int previous = transitions;
                _ = DwmSetWindowAttribute(window, 3, ref previous, sizeof(int));
            }
            if (wasVisible) _ = ShowWindowAsync(window, 8); // SW_SHOWNA preserves the current placement without taking focus.
        }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    }
}
