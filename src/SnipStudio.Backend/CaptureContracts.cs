#nullable enable
using System;
using System.Drawing;

namespace SnipStudio.WinUI.Backend;

public enum CaptureKind { Rectangle, Freeform, Window, Monitor, AllMonitors }

/// <summary>One explicit user capture request. Coordinates in results are physical desktop pixels.</summary>
public sealed class CaptureRequest
{
    public nint OwnerWindow { get; init; }
    public CaptureKind Kind { get; init; } = CaptureKind.Rectangle;
    public int DelaySeconds { get; init; }
    public bool UseHdrColorCorrection { get; init; }
    public bool HideTaskbars { get; init; }
    public bool KeepPrimaryTaskbarVisible { get; init; } = true;
    public uint? ScreenshotBorderArgb { get; init; }
    public int ScreenshotBorderWidth { get; init; } = 2;
}

public sealed record CapturedImage(byte[] PngBytes, int Width, int Height,
    int FrameCount = 1, string? Warning = null, bool StoppedEarly = false);

public sealed record CaptureProgress(int FrameCount, int Height, string Status);

public readonly record struct CaptureRectangle(int X, int Y, int Width, int Height)
{
    public Rectangle ToDrawingRectangle() => new(X, Y, Width, Height);
}
