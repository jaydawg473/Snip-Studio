using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SnipStudio.WinUI.Backend;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace SnipStudio.WinUI;

internal enum PreviewTool { Pan, Pen, Highlighter, Rectangle }

internal sealed class DrawGestureEventArgs(PreviewTool tool, IReadOnlyList<ImagePoint> points,
    uint argb, float width, bool reachedPointLimit = false) : EventArgs
{
    internal PreviewTool Tool { get; } = tool;
    internal IReadOnlyList<ImagePoint> Points { get; } = points;
    internal uint Argb { get; } = argb;
    internal float Width { get; } = width;
    internal bool ReachedPointLimit { get; } = reachedPointLimit;
}

public sealed partial class EditorViewport : UserControl
{
    private const int MaximumPreviewPoints = 16_384;
    private readonly ViewportState view = new();
    private readonly List<ImagePoint> gesturePoints = [];
    private PreviewTool tool = PreviewTool.Pen;
    private bool gestureActive, panning;
    private uint pointerId;
    private CanvasPoint previousPan;
    private uint gestureArgb;
    private float gestureWidth;
    private XamlRoot? observedRoot;

    internal event EventHandler<DrawGestureEventArgs>? GestureCommitted;
    internal event EventHandler? ViewChanged;
    internal bool HasImage => view.HasImage;
    internal bool IsFit => view.IsFit;
    internal double ZoomPercent => view.ZoomPercent;
    internal uint InkArgb { get; set; } = 0xFFE24046;
    internal float InkWidth { get; set; } = 3;
    internal PreviewTool Tool
    {
        get => tool;
        set { CancelGesture(); tool = value; }
    }

    public EditorViewport() => InitializeComponent();

    internal void SetImage(BitmapImage bitmap, int width, int height, bool resetView)
    {
        CancelGesture();
        Picture.Source = bitmap;
        if (resetView || view.ImageWidth != width || view.ImageHeight != height) view.SetImage(width, height);
        EmptyState.Visibility = Visibility.Collapsed;
        ImageFrame.Visibility = Visibility.Visible;
        UpdateViewport();
        UpdateScene();
    }

    internal void ShowUnavailable()
    {
        CancelGesture();
        Picture.Source = null;
        ImageFrame.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = "Preview unavailable";
        EmptyHint.Text = "Your document is kept. Use Retry preview, Undo, or Save as.";
        EmptyState.Visibility = Visibility.Visible;
    }

    internal void Fit()
    {
        CancelGesture();
        view.Fit();
        UpdateScene();
    }

    internal void ActualPixels()
    {
        CancelGesture();
        view.ZoomTo(100, view.Center);
        UpdateScene();
    }

    internal void ZoomBy(double factor)
    {
        if (!view.HasImage || !double.IsFinite(factor) || factor <= 0) return;
        CancelGesture();
        view.ZoomTo(view.ZoomPercent * factor, view.Center);
        UpdateScene();
    }

    internal void CancelGesture()
    {
        gestureActive = false;
        panning = false;
        gesturePoints.Clear();
        StrokePreview.Points.Clear();
        StrokePreview.Visibility = Visibility.Collapsed;
        RectanglePreview.Visibility = Visibility.Collapsed;
        Host.ReleasePointerCaptures();
    }

    private void OnViewportLoaded(object sender, RoutedEventArgs args)
    {
        if (observedRoot != XamlRoot)
        {
            if (observedRoot is not null) observedRoot.Changed -= OnXamlRootChanged;
            observedRoot = XamlRoot;
            if (observedRoot is not null) observedRoot.Changed += OnXamlRootChanged;
        }
        UpdateViewport();
    }

    private void OnViewportUnloaded(object sender, RoutedEventArgs args)
    {
        CancelGesture();
        if (observedRoot is not null) observedRoot.Changed -= OnXamlRootChanged;
        observedRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateViewport();
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateViewport();

    private void UpdateViewport()
    {
        CancelGesture();
        view.SetViewport(Math.Max(0, Host.ActualWidth), Math.Max(0, Host.ActualHeight),
            Math.Max(0.01, XamlRoot?.RasterizationScale ?? 1));
        Host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, view.Width, view.Height) };
        UpdateScene();
    }

    private void UpdateScene()
    {
        if (view.HasImage)
        {
            // The one-DIP frame sits outside the source image; pointer coordinates
            // and the image itself share precisely the same origin.
            Canvas.SetLeft(ImageFrame, view.Origin.X - 1);
            Canvas.SetTop(ImageFrame, view.Origin.Y - 1);
            ImageFrame.Width = view.DisplayWidth + 2;
            ImageFrame.Height = view.DisplayHeight + 2;
            Picture.Width = view.DisplayWidth;
            Picture.Height = view.DisplayHeight;
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!view.HasImage || gestureActive || !IsEnabled) return;
        var current = args.GetCurrentPoint(Host);
        bool middle = current.Properties.IsMiddleButtonPressed;
        if (!middle && !current.Properties.IsLeftButtonPressed) return;
        CanvasPoint position = new(current.Position.X, current.Position.Y);
        bool pan = middle || tool == PreviewTool.Pan;
        if (!pan && !view.Contains(position)) return;
        if (!Host.CapturePointer(args.Pointer)) return;
        Focus(FocusState.Pointer);
        pointerId = args.Pointer.PointerId;
        gestureActive = true;
        panning = pan;
        previousPan = position;
        gestureArgb = InkArgb;
        gestureWidth = Math.Clamp(InkWidth, 1, 100);
        if (!pan)
        {
            CanvasPoint image = view.ClampedImageAt(position);
            gesturePoints.Add(new ImagePoint(image.X, image.Y));
            var color = Color.FromArgb((byte)(gestureArgb >> 24), (byte)(gestureArgb >> 16),
                (byte)(gestureArgb >> 8), (byte)gestureArgb);
            double width = gestureWidth * view.Scale;
            if (tool == PreviewTool.Rectangle)
            {
                RectanglePreview.Stroke = new SolidColorBrush(color);
                RectanglePreview.StrokeThickness = width;
                RectanglePreview.Visibility = Visibility.Visible;
                UpdateRectanglePreview(image);
            }
            else
            {
                StrokePreview.Stroke = new SolidColorBrush(color);
                StrokePreview.StrokeThickness = width;
                StrokePreview.Opacity = tool == PreviewTool.Highlighter ? 80 / 255.0 : 1;
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Visibility = Visibility.Visible;
            }
        }
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!gestureActive || args.Pointer.PointerId != pointerId) return;
        var current = args.GetCurrentPoint(Host);
        CanvasPoint position = new(current.Position.X, current.Position.Y);
        if (panning)
        {
            view.Pan(position.X - previousPan.X, position.Y - previousPan.Y);
            previousPan = position;
            UpdateScene();
        }
        else AddGesturePoint(position);
        args.Handled = true;
    }

    private void AddGesturePoint(CanvasPoint position)
    {
        CanvasPoint image = view.ClampedImageAt(position);
        ImagePoint next = new(image.X, image.Y);
        if (tool == PreviewTool.Rectangle)
        {
            if (gesturePoints.Count == 1) gesturePoints.Add(next);
            else gesturePoints[1] = next;
            UpdateRectanglePreview(image);
            return;
        }
        ImagePoint last = gesturePoints[^1];
        double dx = next.X - last.X, dy = next.Y - last.Y;
        if (dx * dx + dy * dy < 0.0625) return;
        CanvasPoint display = view.ViewportAt(image);
        if (gesturePoints.Count < MaximumPreviewPoints)
        {
            gesturePoints.Add(next);
            StrokePreview.Points.Add(new Point(display.X, display.Y));
        }
        else
        {
            // Finish the complete bounded path instead of replacing its tail with
            // a false straight chord. The editor reports that another stroke can
            // continue after the user lifts the pointer.
            CommitCurrentGesture(reachedPointLimit: true);
        }
    }

    private void UpdateRectanglePreview(CanvasPoint end)
    {
        ImagePoint start = gesturePoints[0];
        CanvasPoint first = view.ViewportAt(new CanvasPoint(start.X, start.Y));
        CanvasPoint last = view.ViewportAt(end);
        Canvas.SetLeft(RectanglePreview, Math.Min(first.X, last.X));
        Canvas.SetTop(RectanglePreview, Math.Min(first.Y, last.Y));
        RectanglePreview.Width = Math.Abs(last.X - first.X);
        RectanglePreview.Height = Math.Abs(last.Y - first.Y);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!gestureActive || args.Pointer.PointerId != pointerId) return;
        if (!panning)
        {
            var current = args.GetCurrentPoint(Host);
            AddGesturePoint(new CanvasPoint(current.Position.X, current.Position.Y));
        }
        if (gestureActive) CommitCurrentGesture(reachedPointLimit: false);
        args.Handled = true;
    }

    private void CommitCurrentGesture(bool reachedPointLimit)
    {
        bool commit = !panning;
        ImagePoint[] points = gesturePoints.ToArray();
        PreviewTool completedTool = tool;
        uint argb = gestureArgb;
        float width = gestureWidth;
        CancelGesture();
        if (commit && points.Length > 0 && (completedTool != PreviewTool.Rectangle ||
            (points.Length > 1 && Math.Abs(points[0].X - points[1].X) >= 0.5 && Math.Abs(points[0].Y - points[1].Y) >= 0.5)))
            GestureCommitted?.Invoke(this, new DrawGestureEventArgs(completedTool, points, argb, width, reachedPointLimit));
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (gestureActive && args.Pointer.PointerId == pointerId) CancelGesture();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (gestureActive && args.Pointer.PointerId == pointerId) CancelGesture();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (!view.HasImage || gestureActive) return;
        var current = args.GetCurrentPoint(Host);
        int delta = current.Properties.MouseWheelDelta;
        if ((args.KeyModifiers & VirtualKeyModifiers.Control) != 0)
        {
            double factor = Math.Pow(1.1, delta / 120.0);
            view.ZoomTo(view.ZoomPercent * factor, new CanvasPoint(current.Position.X, current.Position.Y));
        }
        else
        {
            double movement = delta / 120.0 * 48;
            if (current.Properties.IsHorizontalMouseWheel || (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0)
                view.Pan(movement, 0);
            else view.Pan(0, movement);
        }
        UpdateScene();
        args.Handled = true;
    }

    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { CancelGesture(); args.Handled = true; }
    }
}
