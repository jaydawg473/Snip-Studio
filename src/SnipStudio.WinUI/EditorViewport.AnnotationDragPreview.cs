using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SnipStudio.WinUI.Backend;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    internal Func<AnnotationSnapshot, CancellationToken, Task<AnnotationDragPreview?>>? PrepareAnnotationDragPreview { get; set; }

    private Task? annotationDragLoad;
    private CancellationTokenSource? annotationDragCancellation;
    private Canvas? annotationDragLayer;
    private Image? annotationDragBelow, annotationDragItem, annotationDragAbove;
    private RectangleGeometry? annotationDragClip;
    private RotateTransform? annotationDragRotation;
    private ImageRectangle annotationDragFrame;
    private ImagePoint annotationDragOffset, annotationDragPivot;
    private double annotationDragAngle;
    private bool HasAnnotationDragPreview => annotationDragLayer is { Visibility: Visibility.Visible };

    private void BeginAnnotationDragPreview(AnnotationSnapshot selection)
    {
        ClearAnnotationDragPreview();
        if (annotationDragLoad is { IsCompleted: false } || PrepareAnnotationDragPreview is not { } prepare) return;
        var lifetime = new CancellationTokenSource();
        annotationDragCancellation = lifetime;
        annotationDragLoad = LoadAnnotationDragPreviewAsync(selection, prepare, lifetime);
    }

    private async Task LoadAnnotationDragPreviewAsync(AnnotationSnapshot selection,
        Func<AnnotationSnapshot, CancellationToken, Task<AnnotationDragPreview?>> prepare, CancellationTokenSource lifetime)
    {
        CancellationToken token = lifetime.Token;
        try
        {
            // Let the first pointer update and selection outline finish before
            // taking the single bounded raster snapshot for this gesture.
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            AnnotationDragPreview? preview = await prepare(selection, token);
            if (preview is null) return;
            BitmapImage below = await DecodeAnnotationDragLayerAsync(preview.BelowPng, token);
            BitmapImage item = await DecodeAnnotationDragLayerAsync(preview.ObjectPng, token);
            BitmapImage? above = preview.AbovePng is { } png ? await DecodeAnnotationDragLayerAsync(png, token) : null;
            token.ThrowIfCancellationRequested();
            EnsureAnnotationDragLayer();
            annotationDragBelow!.Source = below;
            annotationDragItem!.Source = item;
            annotationDragAbove!.Source = above;
            annotationDragFrame = preview.ObjectFrame;
            Picture.Opacity = 0;
            annotationDragLayer!.Visibility = Visibility.Visible;
            UpdateScene();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            // The existing outline can complete the same drag if an optional
            // preview cannot decode or its selection was replaced.
            System.Diagnostics.Debug.WriteLine(error);
            if (ReferenceEquals(annotationDragCancellation, lifetime)) ClearAnnotationDragPreview();
        }
        finally
        {
            if (ReferenceEquals(annotationDragCancellation, lifetime)) annotationDragCancellation = null;
            lifetime.Dispose();
        }
    }

    private static async Task<BitmapImage> DecodeAnnotationDragLayerAsync(byte[] png, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        token.ThrowIfCancellationRequested();
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        token.ThrowIfCancellationRequested();
        return image;
    }

    private void EnsureAnnotationDragLayer()
    {
        if (annotationDragLayer is not null) return;
        annotationDragBelow = new Image { Stretch = Stretch.Fill };
        annotationDragRotation = new RotateTransform();
        annotationDragItem = new Image { Stretch = Stretch.Fill, RenderTransform = annotationDragRotation };
        annotationDragAbove = new Image { Stretch = Stretch.Fill };
        annotationDragClip = new RectangleGeometry();
        annotationDragLayer = new Canvas
        {
            IsHitTestVisible = false, Clip = annotationDragClip, Visibility = Visibility.Collapsed
        };
        annotationDragLayer.Children.Add(annotationDragBelow);
        annotationDragLayer.Children.Add(annotationDragItem);
        annotationDragLayer.Children.Add(annotationDragAbove);
        // The original Picture keeps its layout, including the border, while
        // the detached layers sit under all annotation grips and guides.
        Scene.Children.Insert(Scene.Children.IndexOf(ImageFrame) + 1, annotationDragLayer);
    }

    private void UpdateAnnotationDragPreview(ImagePoint translation) => annotationDragOffset = translation;

    private void UpdateAnnotationDragRotation(ImagePoint center, double clockwiseDegrees)
    {
        annotationDragPivot = center;
        annotationDragAngle = clockwiseDegrees;
    }

    private void PositionAnnotationDragPreview()
    {
        if (annotationDragLayer is not { Visibility: Visibility.Visible } layer) return;
        CanvasPoint origin = view.DisplayOrigin;
        Canvas.SetLeft(layer, origin.X);
        Canvas.SetTop(layer, origin.Y);
        layer.Width = annotationDragBelow!.Width = annotationDragAbove!.Width = view.DisplayWidth;
        layer.Height = annotationDragBelow.Height = annotationDragAbove.Height = view.DisplayHeight;
        annotationDragClip!.Rect = new Rect(0, 0, layer.Width, layer.Height);
        Canvas.SetLeft(annotationDragItem!, (annotationDragFrame.Left + annotationDragOffset.X) * view.Scale);
        Canvas.SetTop(annotationDragItem!, (annotationDragFrame.Top + annotationDragOffset.Y) * view.Scale);
        annotationDragItem!.Width = annotationDragFrame.Width * view.Scale;
        annotationDragItem.Height = annotationDragFrame.Height * view.Scale;
        annotationDragRotation!.CenterX = (annotationDragPivot.X - annotationDragFrame.Left) * view.Scale;
        annotationDragRotation.CenterY = (annotationDragPivot.Y - annotationDragFrame.Top) * view.Scale;
        annotationDragRotation.Angle = annotationDragAngle;
    }

    private void ClearAnnotationDragPreview()
    {
        CancellationTokenSource? pending = annotationDragCancellation;
        annotationDragCancellation = null;
        pending?.Cancel();
        annotationDragOffset = annotationDragPivot = default;
        annotationDragAngle = 0;
        if (annotationDragLayer is null) return;
        annotationDragLayer.Visibility = Visibility.Collapsed;
        annotationDragBelow!.Source = annotationDragItem!.Source = annotationDragAbove!.Source = null;
        Picture.Opacity = 1;
    }
}
