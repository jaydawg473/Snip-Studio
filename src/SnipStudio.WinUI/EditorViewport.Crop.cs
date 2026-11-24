using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SnipStudio.WinUI.Backend;
using Windows.System;
using Windows.UI;
using CropGeometry = global::SnipStudio.CropFrame;
using CropFrameHandle = global::SnipStudio.CropFrameHandle;
using PixelPoint = System.Drawing.PointF;
using PixelRectangle = System.Drawing.Rectangle;
using PixelSize = System.Drawing.Size;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    private const double CropHandleSize = 8;
    private const CropFrameHandle CropTopLeft = CropFrameHandle.Left | CropFrameHandle.Top;
    private const CropFrameHandle CropTopRight = CropFrameHandle.Right | CropFrameHandle.Top;
    private const CropFrameHandle CropBottomLeft = CropFrameHandle.Left | CropFrameHandle.Bottom;
    private const CropFrameHandle CropBottomRight = CropFrameHandle.Right | CropFrameHandle.Bottom;
    private PixelRectangle? cropDraft;
    private long? cropImageRevision;
    private CropDrag? cropDrag;
    private CropAdornments? cropAdornments;

    internal event EventHandler? CropFrameChanged;
    internal event EventHandler? CropFrameCanceled;
    internal event EventHandler? CropFrameApplyRequested;

    internal ImageRectangle? CropFrame => tool == PreviewTool.Crop && cropDraft is { } frame
        ? new(frame.X, frame.Y, frame.Width, frame.Height)
        : null;

    internal bool CanApplyCropFrame => tool == PreviewTool.Crop && cropDrag is null &&
        Picture.Source is not null && cropDraft is { Width: >= 2, Height: >= 2 } frame && frame != FullCropFrame;

    private PixelRectangle FullCropFrame => new(0, 0, view.ImageWidth, view.ImageHeight);

    internal void BeginCropFrame()
    {
        if (tool != PreviewTool.Crop || !view.HasImage) return;
        CancelGesture();
        ResetCropFrameForImage();
        if (IsEnabled) Focus(FocusState.Programmatic);
    }

    internal void ClearCropFrame()
    {
        if (cropDraft is null) return;
        cropDrag = null;
        cropDraft = null;
        CancelGesture();
        SetObjectCursor(null);
        DrawCropFrame();
        CropFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetCropFrameForImage()
    {
        cropDraft = FullCropFrame;
        DrawCropFrame();
        CropFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private CropFrameHandle CropGripAt(CanvasPoint position)
    {
        if (!view.HasImage || cropDraft is not { } frame || Picture.Source is null) return CropFrameHandle.None;
        CanvasPoint point = view.ImageAt(position);
        CropFrameHandle grip = CropGeometry.HitTest(frame, new PixelPoint((float)point.X, (float)point.Y),
            (float)(CropHandleSize / view.Scale));
        // Retain drawing a new crop from the image interior while making the
        // eight handles on the initial full frame immediately usable.
        if (view.Contains(position) &&
            (grip == CropFrameHandle.None || grip == CropFrameHandle.Move && frame == FullCropFrame))
            return CropFrameHandle.Create;
        return grip;
    }

    private void BeginCropDrag(CanvasPoint position, CropFrameHandle handle)
    {
        if (cropDraft is not { } frame) return;
        cropDrag = new CropDrag(frame, position, view.ImageAt(position), handle);
        UpdateCropCursor(position);
        DrawCropFrame();
        CropFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AdvanceCropDrag(CanvasPoint position)
    {
        if (cropDrag is not { } drag) return;
        double dx = position.X - drag.ViewAnchor.X, dy = position.Y - drag.ViewAnchor.Y;
        double minimumMovement = .5 / view.RasterizationScale;
        if (!drag.Started && Math.Abs(dx) < minimumMovement && Math.Abs(dy) < minimumMovement) return;
        drag.Started = true;
        CanvasPoint target = view.ImageAt(position);
        var imageSize = new PixelSize(view.ImageWidth, view.ImageHeight);
        PixelRectangle next = drag.Handle == CropFrameHandle.Create
            ? CropGeometry.Create(new PixelPoint((float)drag.Anchor.X, (float)drag.Anchor.Y),
                new PixelPoint((float)target.X, (float)target.Y), imageSize)
            : CropGeometry.Resize(drag.Start, drag.Handle,
                new PixelPoint((float)(target.X - drag.Anchor.X), (float)(target.Y - drag.Anchor.Y)), imageSize);
        if (next.IsEmpty)
        {
            if (cropDraft == drag.Start) return;
            cropDraft = drag.Start;
        }
        else
        {
            if (cropDraft == next) return;
            cropDraft = next;
        }
        DrawCropFrame();
    }

    private void FinishCropDrag(CanvasPoint position)
    {
        AdvanceCropDrag(position);
        cropDrag = null;
        CancelGesture();
        UpdateCropCursor(position);
        DrawCropFrame();
        CropFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool RevertCropDrag()
    {
        if (cropDrag is not { } drag) return false;
        cropDraft = drag.Start;
        cropDrag = null;
        SetObjectCursor(null);
        return true;
    }

    private void UpdateCropCursor(CanvasPoint position)
    {
        CropFrameHandle handle = IsEnabled && !panning
            ? cropDrag?.Handle ?? CropGripAt(position)
            : CropFrameHandle.None;
        SetObjectCursor(handle switch
        {
            CropFrameHandle.Left or CropFrameHandle.Right => InputSystemCursorShape.SizeWestEast,
            CropFrameHandle.Top or CropFrameHandle.Bottom => InputSystemCursorShape.SizeNorthSouth,
            CropTopLeft or CropBottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
            CropTopRight or CropBottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
            CropFrameHandle.Move => InputSystemCursorShape.SizeAll,
            CropFrameHandle.Create => InputSystemCursorShape.Cross,
            _ => null
        });
    }

    private bool TryHandleCropKey(VirtualKey key)
    {
        if (tool != PreviewTool.Crop || cropDraft is null || !IsEnabled) return false;
        if (key == VirtualKey.Enter)
        {
            if (CanApplyCropFrame) CropFrameApplyRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (key != VirtualKey.Escape) return false;
        ClearCropFrame();
        CropFrameCanceled?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void DrawCropFrame()
    {
        if (CropOverlay is null) return;
        if (tool != PreviewTool.Crop || cropDraft is not { } frame || !view.HasImage || Picture.Source is null)
        {
            CropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        CropOverlay.Visibility = Visibility.Visible;
        var visuals = cropAdornments ??= new CropAdornments(CropOverlay);
        CanvasPoint origin = view.DisplayOrigin;
        double imageRight = origin.X + view.DisplayWidth, imageBottom = origin.Y + view.DisplayHeight;
        CanvasPoint topLeft = view.ViewportAt(new(frame.Left, frame.Top));
        CanvasPoint bottomRight = view.ViewportAt(new(frame.Right, frame.Bottom));
        double left = topLeft.X, top = topLeft.Y, right = bottomRight.X, bottom = bottomRight.Y;
        double middleX = (left + right) * .5, middleY = (top + bottom) * .5;

        SetCropRectangle(visuals.Shade[0], origin.X, origin.Y, imageRight, top);
        SetCropRectangle(visuals.Shade[1], origin.X, bottom, imageRight, imageBottom);
        SetCropRectangle(visuals.Shade[2], origin.X, top, left, bottom);
        SetCropRectangle(visuals.Shade[3], right, top, imageRight, bottom);
        SetCropRectangle(visuals.OutlineUnderlay, left, top, right, bottom);
        SetCropRectangle(visuals.Outline, left, top, right, bottom);

        double grip = CropHandleSize / 2;
        DrawGrip(0, left, top);
        DrawGrip(1, middleX, top);
        DrawGrip(2, right, top);
        DrawGrip(3, right, middleY);
        DrawGrip(4, right, bottom);
        DrawGrip(5, middleX, bottom);
        DrawGrip(6, left, bottom);
        DrawGrip(7, left, middleY);

        bool showGuides = cropDrag is { Started: true };
        for (int i = 0; i < visuals.Guides.Length; i++)
        {
            Line guide = visuals.Guides[i];
            guide.Visibility = showGuides ? Visibility.Visible : Visibility.Collapsed;
            if (!showGuides) continue;
            if (i < 2)
            {
                guide.X1 = guide.X2 = left + (right - left) * (i + 1) / 3;
                guide.Y1 = top;
                guide.Y2 = bottom;
            }
            else
            {
                guide.Y1 = guide.Y2 = top + (bottom - top) * (i - 1) / 3;
                guide.X1 = left;
                guide.X2 = right;
            }
        }

        void DrawGrip(int index, double centerX, double centerY) =>
            SetCropRectangle(visuals.Handles[index], centerX - grip, centerY - grip, centerX + grip, centerY + grip);
    }

    private static void SetCropRectangle(Rectangle shape, double left, double top, double right, double bottom)
    {
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        shape.Width = Math.Max(0, right - left);
        shape.Height = Math.Max(0, bottom - top);
    }

    private sealed class CropAdornments
    {
        internal Rectangle[] Shade { get; } = new Rectangle[4];
        internal Rectangle OutlineUnderlay { get; }
        internal Rectangle Outline { get; }
        internal Line[] Guides { get; } = new Line[4];
        internal Rectangle[] Handles { get; } = new Rectangle[8];

        internal CropAdornments(Canvas layer)
        {
            var shade = new SolidColorBrush(Color.FromArgb(112, 15, 23, 42));
            var blue = new SolidColorBrush(Color.FromArgb(255, 0, 95, 175));
            var white = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            var guideBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
            for (int i = 0; i < Shade.Length; i++)
                layer.Children.Add(Shade[i] = new Rectangle { Fill = shade });
            layer.Children.Add(OutlineUnderlay = new Rectangle { Stroke = blue, StrokeThickness = 3 });
            layer.Children.Add(Outline = new Rectangle { Stroke = white, StrokeThickness = 1 });
            for (int i = 0; i < Guides.Length; i++)
                layer.Children.Add(Guides[i] = new Line { Stroke = guideBrush, StrokeThickness = 1 });
            for (int i = 0; i < Handles.Length; i++)
                layer.Children.Add(Handles[i] = new Rectangle { Fill = white, Stroke = blue, StrokeThickness = 1 });
        }
    }

    private sealed record CropDrag(PixelRectangle Start, CanvasPoint ViewAnchor, CanvasPoint Anchor, CropFrameHandle Handle)
    {
        internal bool Started { get; set; }
    }
}
