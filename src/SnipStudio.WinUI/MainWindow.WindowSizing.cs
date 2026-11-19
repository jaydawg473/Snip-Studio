using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private const nuint WindowSizingSubclassId = 0x53535753;
    private const int SnipImagePaddingPixels = 48;
    private WindowSizingProcedure? windowSizingProcedure;
    private bool windowSizingHookInstalled, applyingSnipWindowSize, nativeSizeMoveActive, userSizedSnipWindow;
    private nint windowSizingWindow;
    private SizeInt32 nativeSizeMoveStart, observedWindowSizingSize;
    private uint nativeSizeMoveStartDpi;
    private int systemWindowSizingDepth;
    private long windowSizingIntentVersion, snipWindowSizingSerial;
    private SnipWindowResizeRequest? pendingSnipWindowResize;

    private sealed record SnipWindowResizeRequest(long Serial, long IntentVersion, int PreviewGeneration,
        long Revision, int ImageWidth, int ImageHeight, int Pass);

    private long WindowSizingIntentVersion => windowSizingIntentVersion;

    private void InitializeWindowSizing()
    {
        if (windowSizingHookInstalled) return;
        windowSizingWindow = WindowHandle;
        windowSizingProcedure = SnipWindowSizingProcedure;
        windowSizingHookInstalled = WindowSizingSetWindowSubclass(windowSizingWindow, windowSizingProcedure,
            WindowSizingSubclassId, 0);
        if (!windowSizingHookInstalled)
        {
            PreviewNotice.Title = "Automatic window sizing unavailable";
            PreviewNotice.Message = "Windows could not enable automatic image-window sizing. You can still open images and resize this window manually.";
            PreviewNotice.IsOpen = true;
            SetStatus(PreviewNotice.Message);
        }
        Root.Loaded += OnWindowSizingRootLoaded;
        Closed += OnWindowSizingClosed;
        // Use the legacy launcher's 540x260 design size. Native XAML chrome is
        // measured separately when a fresh image's preview becomes ready.
        if (!session.HasImage)
        {
            double raster = WindowSizingRasterScale();
            if (TryGetWindowSizingWorkArea(out RectInt32 work))
            {
                applyingSnipWindowSize = true;
                try { AppWindow.Resize(new SizeInt32(Math.Min(work.Width, (int)Math.Ceiling(540 * raster)),
                    Math.Min(work.Height, (int)Math.Ceiling(260 * raster)))); }
                finally { applyingSnipWindowSize = false; }
            }
        }
        observedWindowSizingSize = AppWindow.Size;
    }

    private void CancelPendingSnipResize()
    {
        unchecked { windowSizingIntentVersion++; snipWindowSizingSerial++; }
        pendingSnipWindowResize = null;
    }

    private void ScheduleResizeForSnip(long expectedIntentVersion)
    {
        if (closed || applicationExitPending || userSizedSnipWindow || !windowSizingHookInstalled || !preferences.ResizeToSnip ||
            expectedIntentVersion != windowSizingIntentVersion || !session.HasImage || !PreviewIsCurrent ||
            Viewport.IsTextEditing || !Viewport.IsFit) return;
        long serial = ++snipWindowSizingSerial;
        pendingSnipWindowResize = new(serial, expectedIntentVersion, previewGeneration, session.Revision,
            session.ImageWidth, session.ImageHeight, 0);
        ResumePendingSnipResize();
    }

    private void ResumePendingSnipResize()
    {
        // Capture may still be saving an original or publishing the clipboard
        // after its preview is ready. Its action-finally hook resumes this work
        // once busy is cleared and the final commands have been updated.
        if (!busy && !closed && pendingSnipWindowResize is { } request)
            QueueSnipWindowResize(request.Serial);
    }

    private void OnWindowSizingRootLoaded(object sender, RoutedEventArgs args)
    {
        ResumePendingSnipResize();
    }

    private void QueueSnipWindowResize(long serial)
    {
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyPendingSnipWindowResize(serial)))
            pendingSnipWindowResize = null;
    }

    private void ApplyPendingSnipWindowResize(long serial)
    {
        SnipWindowResizeRequest? request = pendingSnipWindowResize;
        if (request is null || request.Serial != serial) return;
        if (closed || applicationExitPending || userSizedSnipWindow || request.IntentVersion != windowSizingIntentVersion ||
            !preferences.ResizeToSnip || request.PreviewGeneration != previewGeneration ||
            request.Revision != session.Revision || !PreviewIsCurrent ||
            request.ImageWidth != session.ImageWidth || request.ImageHeight != session.ImageHeight ||
            Viewport.IsTextEditing || !Viewport.IsFit ||
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
        { pendingSnipWindowResize = null; return; }
        // Enqueued work runs after the successful render action unwinds. Never
        // measure a disabled/loading toolbar as the final editor chrome.
        if (busy || nativeSizeMoveActive || Root.XamlRoot is null || Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return;
        try
        {
            Root.UpdateLayout();
            if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0 ||
                !TryGetWindowSizingWorkArea(out RectInt32 work))
            { pendingSnipWindowResize = null; return; }
            double raster = WindowSizingRasterScale();
            SizeInt32 current = AppWindow.Size;
            int chromeWidth = Math.Max(0, current.Width - (int)Math.Round(Viewport.ActualWidth * raster));
            int chromeHeight = Math.Max(0, current.Height - (int)Math.Round(Viewport.ActualHeight * raster));
            // Leave room for the compact options strip so choosing Text or a
            // drawing tool does not immediately reduce a newly fitted image.
            if (ContextRow.Visibility == Visibility.Collapsed)
                chromeHeight += (int)Math.Ceiling(40 * raster);
            // Prefer native image pixels with the current viewport's DPI-scaled
            // fit padding (48 physical pixels at 100% DPI). The legacy 780 Ui
            // width floor is a preferred 780-DIP toolbar shell here.
            // The preferred shell is not a native minimum tracking constraint.
            int minimumWidth = Math.Min(work.Width, (int)Math.Ceiling(780 * raster));
            int fitPadding = (int)Math.Ceiling(Math.Max(SnipImagePaddingPixels, ViewportState.Margin * 2 * raster));
            double scale = Math.Min(1d, Math.Min(
                Math.Max(1, work.Width - chromeWidth - fitPadding) / (double)request.ImageWidth,
                Math.Max(1, work.Height - chromeHeight - fitPadding) / (double)request.ImageHeight));
            int desiredWidth = (int)Math.Min(work.Width, Math.Max(minimumWidth,
                Math.Ceiling(request.ImageWidth * scale) + fitPadding + chromeWidth));
            int desiredHeight = (int)Math.Min(work.Height,
                Math.Ceiling(request.ImageHeight * scale) + fitPadding + chromeHeight);
            PointInt32 position = AppWindow.Position;
            int x = Math.Clamp(position.X, work.X, work.X + work.Width - desiredWidth);
            int y = Math.Clamp(position.Y, work.Y, work.Y + work.Height - desiredHeight);
            bool changed = current.Width != desiredWidth || current.Height != desiredHeight ||
                position.X != x || position.Y != y;
            if (changed)
            {
                applyingSnipWindowSize = true;
                try { AppWindow.MoveAndResize(new RectInt32(x, y, desiredWidth, desiredHeight)); }
                finally { applyingSnipWindowSize = false; }
            }
            if (changed && request.Pass < 2)
            {
                pendingSnipWindowResize = request with { Pass = request.Pass + 1 };
                QueueSnipWindowResize(serial);
            }
            else
            {
                pendingSnipWindowResize = null;
                Viewport.Fit();
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            pendingSnipWindowResize = null;
            // Sizing is presentation only. An unavailable monitor/window must
            // not discard a successfully opened document or its view.
            SetStatus("The image is open. Its window size could not be adjusted automatically: " + failure.Message);
        }
    }

    private double WindowSizingRasterScale()
    {
        double raster = Root.XamlRoot?.RasterizationScale ?? 0;
        if (double.IsFinite(raster) && raster > 0) return raster;
        uint dpi = WindowSizingGetDpiForWindow(WindowHandle);
        return dpi > 0 ? dpi / 96d : 1;
    }

    private bool TryGetWindowSizingWorkArea(out RectInt32 work)
    {
        nint monitor = WindowSizingMonitorFromWindow(WindowHandle, 2); // nearest
        var info = new WindowSizingMonitorInfo { Size = Marshal.SizeOf<WindowSizingMonitorInfo>() };
        if (monitor != 0 && WindowSizingGetMonitorInfo(monitor, ref info))
        {
            work = new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top);
            return work.Width > 0 && work.Height > 0;
        }
        work = default;
        return false;
    }

    private nint SnipWindowSizingProcedure(nint window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint reference)
    {
        bool forwarded = false;
        nint forwardedResult = 0;
        try
        {
            if (message == 0x02E0) // WM_DPICHANGED may synchronously emit WM_SIZE.
            {
                systemWindowSizingDepth++;
                try
                {
                    forwarded = true;
                    forwardedResult = WindowSizingDefSubclassProc(window, message, wParam, lParam);
                }
                finally
                {
                    systemWindowSizingDepth--;
                    observedWindowSizingSize = AppWindow.Size;
                }
                return forwardedResult;
            }
            if (message == 0x0231) // WM_ENTERSIZEMOVE: can be a move alone.
            {
                nativeSizeMoveActive = true;
                nativeSizeMoveStart = AppWindow.Size;
                nativeSizeMoveStartDpi = WindowSizingGetDpiForWindow(window);
            }
            else if (message == 0x0214 && !applyingSnipWindowSize && systemWindowSizingDepth == 0 && lParam != 0) // WM_SIZING
            {
                WindowSizingNativeRect proposed = Marshal.PtrToStructure<WindowSizingNativeRect>(lParam);
                if (proposed.Right - proposed.Left != nativeSizeMoveStart.Width ||
                    proposed.Bottom - proposed.Top != nativeSizeMoveStart.Height)
                    MarkSnipWindowSizedByUser();
            }
            else if (message == 0x0005) // WM_SIZE, including snap/maximize
            {
                // Minimize is not a new preferred editor size. Keep the last
                // restored size so an ordinary restore does not appear manual.
                if (wParam != 1)
                {
                    SizeInt32 current = AppWindow.Size;
                    bool changed = current.Width != observedWindowSizingSize.Width ||
                        current.Height != observedWindowSizingSize.Height;
                    observedWindowSizingSize = current;
                    if (changed && !applyingSnipWindowSize && systemWindowSizingDepth == 0 && !nativeSizeMoveActive)
                        MarkSnipWindowSizedByUser();
                }
            }
            else if (message == 0x0232) // WM_EXITSIZEMOVE
            {
                SizeInt32 current = AppWindow.Size;
                uint dpi = WindowSizingGetDpiForWindow(window);
                if (!applyingSnipWindowSize && systemWindowSizingDepth == 0 && nativeSizeMoveActive &&
                    LogicalWindowSizeChanged(nativeSizeMoveStart, nativeSizeMoveStartDpi, current, dpi))
                    MarkSnipWindowSizedByUser();
                observedWindowSizingSize = current;
                nativeSizeMoveActive = false;
                ResumePendingSnipResize();
            }
            else if (message == 0x0082) // WM_NCDESTROY
            {
                pendingSnipWindowResize = null;
                WindowSizingRemoveWindowSubclass(window, windowSizingProcedure!, WindowSizingSubclassId);
                windowSizingHookInstalled = false;
            }
        }
        catch (Exception)
        {
            // A managed exception cannot unwind through a native callback.
            CancelPendingSnipResize();
        }
        if (forwarded) return forwardedResult;
        try { return WindowSizingDefSubclassProc(window, message, wParam, lParam); }
        catch (Exception)
        {
            CancelPendingSnipResize();
            return 0;
        }
    }

    private void MarkSnipWindowSizedByUser()
    {
        userSizedSnipWindow = true;
        CancelPendingSnipResize();
    }

    private static bool LogicalWindowSizeChanged(SizeInt32 before, uint beforeDpi, SizeInt32 after, uint afterDpi)
    {
        if (beforeDpi == 0 || afterDpi == 0) return false;
        // A pure cross-monitor move can scale and round the physical frame.
        // Compare logical dimensions; explicit WM_SIZING still captures even
        // a one-pixel resize independently of this snap/exit fallback.
        double roundingTolerance = Math.Max(1d, 96d / beforeDpi + 96d / afterDpi);
        return Math.Abs(before.Width * 96d / beforeDpi - after.Width * 96d / afterDpi) > roundingTolerance ||
            Math.Abs(before.Height * 96d / beforeDpi - after.Height * 96d / afterDpi) > roundingTolerance;
    }

    private void OnWindowSizingClosed(object sender, WindowEventArgs args)
    {
        CancelPendingSnipResize();
        Root.Loaded -= OnWindowSizingRootLoaded;
        Closed -= OnWindowSizingClosed;
        if (windowSizingHookInstalled && windowSizingProcedure is not null)
        {
            WindowSizingRemoveWindowSubclass(windowSizingWindow, windowSizingProcedure, WindowSizingSubclassId);
            windowSizingHookInstalled = false;
        }
    }

    private delegate nint WindowSizingProcedure(nint window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint reference);
    [StructLayout(LayoutKind.Sequential)] private struct WindowSizingNativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowSizingMonitorInfo
    { public int Size; public WindowSizingNativeRect Monitor, Work; public uint Flags; }
    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowSizingSetWindowSubclass(nint window,
        WindowSizingProcedure callback, nuint id, nuint reference);
    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowSizingRemoveWindowSubclass(nint window,
        WindowSizingProcedure callback, nuint id);
    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern nint WindowSizingDefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint WindowSizingMonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowSizingGetMonitorInfo(nint monitor,
        ref WindowSizingMonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint WindowSizingGetDpiForWindow(nint window);
}
