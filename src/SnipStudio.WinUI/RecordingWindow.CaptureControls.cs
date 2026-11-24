using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;
using Windows.Graphics;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnipStudio.WinUI;

public sealed partial class RecordingWindow
{
    private const uint ExcludeFromCapture = 0x11;
    private static Task<int>? recordingCompositionFlush;
    private static readonly object recordingCompositionGate = new();
    private RecordingBoundaryOverlay? recordingPreview;
    private bool selectingRecordingRegion;
    private (OverlappedPresenter Presenter, bool WasAlwaysOnTop)? recordingPresenter;
    private (nint Window, uint PreviousAffinity)? recordingAffinity;

    internal void ActivateRecordingCapture()
    {
        if (closed || selectingRecordingRegion) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void RefreshRecordingPreview()
    {
        if (!captureWorkflow || closed || publishedRecordingState || recorder.IsActive || videoPath is not null ||
            candidateCopyPath is not null || RegionBox.SelectedItem is not RecordingDisplay area) return;

        ReleaseRecordingOutline(ref recordingPreview);
        bool outlined = TryShowRecordingOutline(area.Region, ref recordingPreview);
        bool positioned = TryPlaceRecordingControls(recordingPreview?.Region ?? area.Region, anchorAbove: true);
        if (!outlined) SetStatus("Recording outline unavailable. You can still record the selected area.");
        else if (!positioned) SetStatus("The bar could not be positioned automatically. You can still record the selected area.");
    }

    private bool TryShowRecordingOutline(RecordingRegion region, ref RecordingBoundaryOverlay? outline)
    {
        try
        {
            outline = new RecordingBoundaryOverlay(region);
            outline.Show();
            return outline.VisibleSegmentCount > 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Trace.WriteLine(exception, "Recording outline");
            ReleaseRecordingOutline(ref outline);
            return false;
        }
    }

    private async Task<RecordingRegion> PrepareRecordingControlsAsync(RecordingRegion requestedRegion, CancellationToken token)
    {
        RecordingRegion region = RecordingBoundaryOverlay.ClipToDesktop(requestedRegion);
        _ = TryShowRecordingOutline(region, ref recordingBoundary);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            recordingPresenter = (presenter, presenter.IsAlwaysOnTop);
            presenter.IsAlwaysOnTop = true;
        }

        bool excluded = TryExcludeRecordingControls();
        bool outlined = recordingBoundary?.HasCompleteOutline == true;
        if (!outlined || !excluded)
        {
            VideoNotice.Title = "Recording area";
            VideoNotice.Severity = InfoBarSeverity.Warning;
            string outlineWarning = outlined ? "" : "The outline may be incomplete. Recording still uses the selected area.";
            string controlsWarning = excluded ? "" : "Recording controls may appear in the video if they overlap the selected area.";
            VideoNotice.Message = string.Join(" ", new[] { outlineWarning, controlsWarning }.Where(message => message.Length != 0));
            VideoNotice.IsOpen = true;
        }

        _ = TryPlaceRecordingControls(region, anchorAbove: false);

        // Position and affinity can both change just before the first GDI frame.
        // Wait for composition without sleeping on every Start or blocking UI input.
        Task<int> pending;
        lock (recordingCompositionGate)
        {
            if (recordingCompositionFlush is null || recordingCompositionFlush.IsCompleted)
                recordingCompositionFlush = Task.Run(FlushRecordingComposition);
            pending = recordingCompositionFlush;
        }
        try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(2), token); }
        catch (TimeoutException) { throw new TimeoutException("The Windows compositor did not settle. Start the recording again."); }
        return region;
    }

    private bool TryExcludeRecordingControls()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetWindowDisplayAffinity(window, out uint previousAffinity)) previousAffinity = 0;
        if (!SetWindowDisplayAffinity(window, ExcludeFromCapture)) return false;
        recordingAffinity = (window, previousAffinity);
        if (GetWindowDisplayAffinity(window, out uint actualAffinity) && actualAffinity == ExcludeFromCapture) return true;
        RestoreRecordingAffinity();
        return false;
    }

    private void RestoreRecordingControls()
    {
        RestoreRecordingAffinity();
        var presenterState = recordingPresenter;
        recordingPresenter = null;
        if (!closed && presenterState is { } previous)
            previous.Presenter.IsAlwaysOnTop = previous.WasAlwaysOnTop;
    }

    private void RestoreRecordingAffinity()
    {
        var affinityState = recordingAffinity;
        if (affinityState is { } previous && SetWindowDisplayAffinity(previous.Window, previous.PreviousAffinity))
            recordingAffinity = null;
    }

    private bool TryPlaceRecordingControls(RecordingRegion region, bool anchorAbove)
    {
        try
        {
            IReadOnlyList<RecordingRegion> workAreas = RecordingBoundaryOverlay.GetMonitorWorkAreas(region);
            if (workAreas.Count == 0) return false;

            nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SizeInt32 originalSize = AppWindow.Size;
            if (!PositionRecordingControls(window, region, workAreas, anchorAbove)) return false;
            // Moving across monitors can change the native frame size. Recompute once with the new size.
            SizeInt32 movedSize = AppWindow.Size;
            return (movedSize.Width == originalSize.Width && movedSize.Height == originalSize.Height)
                || PositionRecordingControls(window, region, workAreas, anchorAbove);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            System.Diagnostics.Trace.WriteLine(exception, "Recording control placement");
            return false;
        }
    }

    private bool PositionRecordingControls(nint window, RecordingRegion region, IReadOnlyList<RecordingRegion> workAreas, bool anchorAbove)
    {
        PointInt32 current = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            size = new(Math.Max(size.Width, presenter.PreferredMinimumWidth.GetValueOrDefault()),
                Math.Max(size.Height, presenter.PreferredMinimumHeight.GetValueOrDefault()));

        DrawingRectangle capture = new(region.X, region.Y, region.Width, region.Height);
        int margin = Math.Max(8, (int)Math.Round(10 * RecordingWindowDpi(window) / 96d));
        capture.Inflate(margin, margin);
        if (!anchorAbove && !capture.IntersectsWith(new DrawingRectangle(current.X, current.Y, size.Width, size.Height))) return true;

        RecordingRegion selectedMonitor = workAreas[0];
        var preferred = new DrawingRectangle(selectedMonitor.X, selectedMonitor.Y, selectedMonitor.Width, selectedMonitor.Height);
        int idealX = region.X + (region.Width - size.Width) / 2;
        int idealY = capture.Top - size.Height;

        if (preferred.Width >= size.Width && idealY >= preferred.Top && capture.Top <= preferred.Bottom)
        {
            int x = Math.Clamp(idealX, preferred.Left, preferred.Right - size.Width);
            return MoveRecordingControls(window, current, new PointInt32(x, idealY));
        }

        PointInt32? nearest = null;
        long nearestDistance = long.MaxValue;
        foreach (RecordingRegion workArea in workAreas)
        {
            var work = new DrawingRectangle(workArea.X, workArea.Y, workArea.Width, workArea.Height);
            DrawingRectangle[] candidates =
            [
                DrawingRectangle.FromLTRB(work.Left, work.Top, work.Right, Math.Min(work.Bottom, capture.Top)),
                DrawingRectangle.FromLTRB(work.Left, work.Top, Math.Min(work.Right, capture.Left), work.Bottom),
                DrawingRectangle.FromLTRB(Math.Max(work.Left, capture.Right), work.Top, work.Right, work.Bottom),
                DrawingRectangle.FromLTRB(work.Left, Math.Max(work.Top, capture.Bottom), work.Right, work.Bottom)
            ];
            foreach (DrawingRectangle candidate in candidates)
            {
                if (size.Width > candidate.Width || size.Height > candidate.Height) continue;
                int x = Math.Clamp(idealX, candidate.Left, candidate.Right - size.Width);
                int y = Math.Clamp(idealY, candidate.Top, candidate.Bottom - size.Height);
                long dx = (long)x - idealX, dy = (long)y - idealY;
                long distance = dx * dx + dy * dy;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = new PointInt32(x, y);
            }
        }

        if (nearest is { } position) return MoveRecordingControls(window, current, position);
        // Full-display capture has no exterior work area. Keep the control bar on the selected display.
        if (!anchorAbove || preferred.Width < size.Width || preferred.Height < size.Height) return true;
        return MoveRecordingControls(window, current, new PointInt32(
            Math.Clamp(idealX, preferred.Left, preferred.Right - size.Width), preferred.Top));
    }

    private static bool MoveRecordingControls(nint window, PointInt32 current, PointInt32 position) =>
        current.X == position.X && current.Y == position.Y ||
        SetRecordingControlWindowPosition(window, 0, position.X, position.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);

    private static int FlushRecordingComposition()
    {
        try { return DwmFlush(); }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRecordingControlWindowPosition(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);
}
