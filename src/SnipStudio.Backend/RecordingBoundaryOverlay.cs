#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

/// <summary>
/// Owns the proven native recording strips on the calling WinUI STA. Exterior
/// strips stay outside the recording; inset strips are shown only after the
/// original implementation verifies capture exclusion on each hidden HWND.
/// </summary>
public sealed class RecordingBoundaryOverlay : IDisposable
{
    private readonly int ownerThread;
    private readonly global::SnipStudio.RecordingBoundary boundary;
    private bool disposed;

    public RecordingBoundaryOverlay(RecordingRegion requestedRegion)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Create and show the recording outline on the application's STA UI thread.");
        ownerThread = Environment.CurrentManagedThreadId;
        Region = ClipToDesktop(requestedRegion);
        boundary = new global::SnipStudio.RecordingBoundary(Region.Rectangle);
    }

    public static RecordingRegion ClipToDesktop(RecordingRegion requestedRegion)
    {
        // Match ScreenRecorder's physical rectangle before creating any optional outline HWNDs.
        Rectangle clipped = Rectangle.Intersect(requestedRegion.Rectangle, SystemInformation.VirtualScreen);
        if (clipped.Width < 2 || clipped.Height < 2)
            throw new ArgumentException("The selected recording area is no longer available on the desktop.", nameof(requestedRegion));
        return AsRegion(clipped);
    }

    public static IReadOnlyList<RecordingRegion> GetMonitorWorkAreas(RecordingRegion region)
    {
        // Screen.WorkingArea is already in desktop coordinates, including on monitors to the left or above primary.
        Screen preferred = Screen.FromRectangle(region.Rectangle);
        var workAreas = new List<RecordingRegion> { AsRegion(preferred.WorkingArea) };
        foreach (Screen screen in Screen.AllScreens)
            if (!screen.Equals(preferred)) workAreas.Add(AsRegion(screen.WorkingArea));
        return workAreas;
    }

    private static RecordingRegion AsRegion(Rectangle bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    public RecordingRegion Region { get; }
    public bool HasCompleteOutline { get { VerifyAccess(); return boundary.HasCompleteOutline; } }
    public bool CaptureExclusionSupported { get { VerifyAccess(); return boundary.CaptureExclusionSupported; } }
    public int VisibleSegmentCount { get { VerifyAccess(); return boundary.VisibleSegmentCount; } }

    public void Show()
    {
        VerifyAccess();
        boundary.Show();
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("The recording outline must be used and closed on its owning UI thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Close the recording outline on its owning UI thread.");
        if (disposed) return;
        disposed = true;
        boundary.Dispose();
    }
}
