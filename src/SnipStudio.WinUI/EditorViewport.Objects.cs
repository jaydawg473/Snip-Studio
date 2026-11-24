using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SnipStudio.WinUI.Backend;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using ShapeOutlinePattern = global::SnipStudio.ShapeOutlinePattern;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    private const double SelectionHitRadius = 5;
    private const double RotationHandleGap = 23;
    private readonly SolidColorBrush objectAccent = new(Color.FromArgb(255, 0, 120, 215));
    private readonly SolidColorBrush gripBackground = new(Color.FromArgb(255, 255, 255, 255));
    private readonly Dictionary<InputSystemCursorShape, InputSystemCursor> objectCursors = [];
    private InputSystemCursorShape? objectCursor;
    private ObjectDrag? objectDrag;
    private ObjectDrag? pendingObjectTransform;

    internal Func<ImagePoint, float, AnnotationSnapshot?>? ProbeAnnotation { get; set; }
    internal Func<ImagePoint, float, AnnotationSnapshot?>? ProbeAnnotationOutline { get; set; }
    internal Func<AnnotationSnapshot, ImagePoint, ImagePoint>? ConstrainAnnotationMove { get; set; }
    internal event EventHandler<AnnotationSnapshot?>? AnnotationSelectionChanged;

    private sealed class ObjectDrag(AnnotationSnapshot item, ObjectGrip grip, CanvasPoint anchor)
    {
        internal AnnotationSnapshot Item { get; } = item;
        internal ObjectGrip Grip { get; } = grip;
        internal CanvasPoint Anchor { get; } = anchor;
        internal ImageRectangle Frame { get; set; } = ObjectFrame(item);
        internal ImagePoint[]? Endpoints { get; set; } = item.Shape is { Endpoints.Count: 2 } shape ? shape.Endpoints.ToArray() : null;
        internal float Angle { get; set; } = ObjectAngle(item);
        internal ImagePoint MoveDelta { get; set; }
        internal bool Started { get; set; }
    }

    private readonly record struct ObjectHandle(ObjectGrip Grip, CanvasPoint Position);

    private static ImageRectangle ObjectFrame(AnnotationSnapshot item)
    {
        if (item.Emoji is not null) return item.Emoji.Frame;
        if (item.Shape is not null) return item.Shape.GeometryBounds;
        if (item.Text is not null) return new(item.Text.Location.X, item.Text.Location.Y,
            item.Text.LayoutWidth > 0 ? item.Text.LayoutWidth : item.Bounds.Width,
            item.Text.MinimumHeight > 0 ? item.Text.MinimumHeight : item.Bounds.Height);
        return item.Bounds;
    }

    private static float ObjectAngle(AnnotationSnapshot item) => item.Emoji?.RotationDegrees ?? item.Shape?.RotationDegrees ?? 0;

    private CanvasPoint DisplayPoint(ImagePoint point) => view.ViewportAt(new(point.X, point.Y));

    private AnnotationSnapshot? PickObject(CanvasPoint pointer) =>
        ProbeObjectAt(pointer, ProbeAnnotation) ?? SelectedFrameAt(pointer);

    private AnnotationSnapshot? PickDrawingOutline(CanvasPoint pointer)
    {
        if (IsTextEditing || tool is not (PreviewTool.Pen or PreviewTool.Highlighter or PreviewTool.Line or
            PreviewTool.Arrow or PreviewTool.Rectangle or PreviewTool.Ellipse)) return null;
        return ProbeObjectAt(pointer, ProbeAnnotationOutline);
    }

    private AnnotationSnapshot? ProbeObjectAt(CanvasPoint pointer, Func<ImagePoint, float, AnnotationSnapshot?>? probe)
    {
        if (!view.Contains(pointer) || probe is null) return null;
        CanvasPoint pixel = view.ClampedImageAt(pointer);
        float radius = (float)Math.Clamp(SelectionHitRadius / view.Scale, .25, 1024);
        return probe(new(pixel.X, pixel.Y), radius);
    }

    private AnnotationSnapshot? SelectedFrameAt(CanvasPoint pointer)
    {
        if (selectedObject is not { } item || item.Shape?.Endpoints.Count == 2) return null;
        CanvasPoint pixel = view.ImageAt(pointer);
        return ObjectTransformGeometry.NearPerimeter(ObjectFrame(item), new(pixel.X, pixel.Y), ObjectAngle(item), 2.5 / view.Scale)
            ? item : null;
    }

    private void BeginObjectDrag(CanvasPoint pointer, ObjectGrip grip, AnnotationSnapshot? drawingOutline = null)
    {
        AnnotationSnapshot? item = drawingOutline ?? (grip != ObjectGrip.None ? selectedObject : PickObject(pointer));
        selectedObject = item;
        selectionBounds = item?.Bounds;
        objectDrag = item is null ? null : new(item, grip == ObjectGrip.None ? ObjectGrip.Move : grip, pointer);
        AnnotationSelectionChanged?.Invoke(this, item);
        UpdateScene();
        UpdateObjectCursor(pointer);
    }

    private void AdvanceObjectDrag(CanvasPoint pointer, bool snap)
    {
        if (objectDrag is not { } drag) return;
        if (!drag.Started && !ObjectTransformGeometry.StartedDrag(drag.Anchor, pointer)) return;
        bool firstStep = !drag.Started;
        drag.Started = true;
        AnnotationSnapshot item = drag.Item;
        ImageRectangle frame = ObjectFrame(item);
        CanvasPoint anchor = view.ImageAt(drag.Anchor), target = view.ImageAt(pointer);
        var delta = new ImagePoint(target.X - anchor.X, target.Y - anchor.Y);
        if (drag.Grip == ObjectGrip.Move)
        {
            drag.MoveDelta = ConstrainAnnotationMove?.Invoke(item, delta) ?? delta;
            drag.Frame = ObjectTransformGeometry.Translate(frame, drag.MoveDelta);
            if (firstStep) BeginAnnotationDragPreview(item);
            UpdateAnnotationDragPreview(drag.MoveDelta);
            if (item.Shape is { Endpoints.Count: 2 } ends)
            {
                drag.Endpoints![0] = TranslatePoint(ends.Endpoints[0], drag.MoveDelta);
                drag.Endpoints[1] = TranslatePoint(ends.Endpoints[1], drag.MoveDelta);
            }
        }
        else if (drag.Grip == ObjectGrip.Rotate)
        {
            ImagePoint center = ObjectTransformGeometry.Center(frame);
            CanvasPoint displayCenter = DisplayPoint(center);
            if (ObjectTransformGeometry.StartedDrag(displayCenter, pointer))
                drag.Angle = ObjectTransformGeometry.RotateFromPointer(ObjectAngle(item), drag.Anchor, pointer, displayCenter, snap);
            if (firstStep) BeginAnnotationDragPreview(item);
            UpdateAnnotationDragRotation(center, drag.Angle - ObjectAngle(item));
            if (item.Shape is { Endpoints.Count: 2 } ends)
            {
                drag.Endpoints![0] = ObjectTransformGeometry.Rotate(ends.Endpoints[0], center, drag.Angle - ObjectAngle(item));
                drag.Endpoints[1] = ObjectTransformGeometry.Rotate(ends.Endpoints[1], center, drag.Angle - ObjectAngle(item));
            }
        }
        else if (drag.Grip is ObjectGrip.Tail or ObjectGrip.Head)
        {
            int index = drag.Grip == ObjectGrip.Tail ? 0 : 1;
            ImagePoint endpoint = TranslatePoint(item.Shape!.Endpoints[index], delta);
            drag.Endpoints![index] = new(Math.Clamp(endpoint.X, 0, view.ImageWidth), Math.Clamp(endpoint.Y, 0, view.ImageHeight));
        }
        else
        {
            drag.Frame = ObjectTransformGeometry.Resize(frame, drag.Grip, delta, ObjectAngle(item),
                item.Shape is null ? 1 : Math.Min(2, view.ImageWidth), item.Shape is null ? 1 : Math.Min(2, view.ImageHeight));
            // A rotated shape's local frame may extend past the image while
            // its rendered geometry is visible. Keep the opposite grip fixed.
            if (ObjectAngle(item) == 0 || item.Shape is null)
                drag.Frame = ClampObjectFrame(drag.Frame);
        }
        string feedback = drag.Grip switch
        {
            ObjectGrip.Move => "Release to move. Esc cancels.",
            ObjectGrip.Rotate => $"Rotation {drag.Angle:0.#}°. Hold Shift to snap. Esc cancels.",
            ObjectGrip.Tail or ObjectGrip.Head => "Release to apply the new endpoint. Esc cancels.",
            _ => $"Size {drag.Frame.Width:0.#} × {drag.Frame.Height:0.#} px. Esc cancels."
        };
        GestureFeedback?.Invoke(this, feedback);
        UpdateScene();
    }

    private ImageRectangle ClampObjectFrame(ImageRectangle frame)
    {
        double width = Math.Min(view.ImageWidth, frame.Width), height = Math.Min(view.ImageHeight, frame.Height);
        // Clip the dragged edge; keep the opposite edge stationary when
        // its frame is in bounds.
        double left = Math.Clamp(frame.Left, 0, view.ImageWidth - 1);
        double top = Math.Clamp(frame.Top, 0, view.ImageHeight - 1);
        double right = Math.Clamp(frame.Right, left + 1, view.ImageWidth);
        double bottom = Math.Clamp(frame.Bottom, top + 1, view.ImageHeight);
        return new(left, top, Math.Min(width, right - left), Math.Min(height, bottom - top));
    }

    private static ImagePoint TranslatePoint(ImagePoint point, ImagePoint delta) => new(point.X + delta.X, point.Y + delta.Y);

    private void FinishObjectDrag(CanvasPoint pointer, bool snap)
    {
        AdvanceObjectDrag(pointer, snap);
        SelectionTransformEventArgs? result = null;
        if (objectDrag is { Started: true } drag)
        {
            if (drag.Grip == ObjectGrip.Move && drag.MoveDelta != default)
                result = new(drag.Item, drag.Frame, null, null, drag.MoveDelta);
            else if (drag.Grip == ObjectGrip.Rotate && drag.Angle != ObjectAngle(drag.Item))
                result = new(drag.Item, drag.Frame, null, drag.Angle);
            else if (drag.Grip is ObjectGrip.Tail or ObjectGrip.Head && ObjectTransformGeometry.StartedDrag(drag.Anchor, pointer))
            {
                ImagePoint[] endpoints = drag.Endpoints!;
                IReadOnlyList<ImagePoint> original = drag.Item.Shape!.Endpoints;
                double x = endpoints[1].X - endpoints[0].X, y = endpoints[1].Y - endpoints[0].Y;
                if (x * x + y * y >= 1 && (endpoints[0] != original[0] || endpoints[1] != original[1]))
                    result = new(drag.Item, drag.Frame, endpoints, null);
            }
            else if (drag.Grip is >= ObjectGrip.TopLeft and <= ObjectGrip.Left && drag.Frame != ObjectFrame(drag.Item))
                result = new(drag.Item, drag.Frame, null, null);
        }
        if (result is not null && SelectionTransformCommitted is not null) pendingObjectTransform = objectDrag;
        CancelGesture();
        UpdateObjectCursor(pointer);
        if (result is not null) SelectionTransformCommitted?.Invoke(this, result);
    }

    internal void ClearObjectTransformPreview()
    {
        if (pendingObjectTransform is null) return;
        pendingObjectTransform = null;
        ClearAnnotationDragPreview();
        UpdateScene();
    }

    private bool ShowsObjectHandles => tool == PreviewTool.Select ||
        (tool.IsShapeTool() && (selectedObject is { Shape: not null } or { Emoji: not null }));

    private IEnumerable<ObjectHandle> ObjectHandlesAt(AnnotationSnapshot item, ImageRectangle frame, float angle,
        IReadOnlyList<ImagePoint>? endpoints)
    {
        ImagePoint center = ObjectTransformGeometry.Center(frame);
        if (tool == PreviewTool.Select && endpoints is { Count: 2 })
        {
            CanvasPoint tail = DisplayPoint(endpoints[0]), head = DisplayPoint(endpoints[1]);
            double x = head.X - tail.X, y = head.Y - tail.Y;
            if (x * x + y * y >= 16 * 16)
            {
                yield return new(ObjectGrip.Tail, tail);
                yield return new(ObjectGrip.Head, head);
            }
        }
        else if (tool == PreviewTool.Select && item.Kind is not AnnotationKind.Ink &&
            frame.Width * view.Scale >= 16 && frame.Height * view.Scale >= 16)
        {
            yield return GripAt(ObjectGrip.TopLeft, frame.Left, frame.Top);
            yield return GripAt(ObjectGrip.TopRight, frame.Right, frame.Top);
            yield return GripAt(ObjectGrip.BottomRight, frame.Right, frame.Bottom);
            yield return GripAt(ObjectGrip.BottomLeft, frame.Left, frame.Bottom);
            if (frame.Width * view.Scale >= 36)
            {
                yield return GripAt(ObjectGrip.Top, center.X, frame.Top);
                yield return GripAt(ObjectGrip.Bottom, center.X, frame.Bottom);
            }
            if (frame.Height * view.Scale >= 36)
            {
                yield return GripAt(ObjectGrip.Right, frame.Right, center.Y);
                yield return GripAt(ObjectGrip.Left, frame.Left, center.Y);
            }
        }
        if (item.Shape is not null || item.Emoji is not null)
        {
            var rotation = RotationGrip(frame, angle, endpoints);
            yield return new(ObjectGrip.Rotate, rotation.Handle);
        }

        ObjectHandle GripAt(ObjectGrip grip, double x, double y) =>
            new(grip, DisplayPoint(ObjectTransformGeometry.Rotate(new(x, y), center, angle)));
    }

    private (CanvasPoint Stem, CanvasPoint Handle) RotationGrip(ImageRectangle frame, float degrees,
        IReadOnlyList<ImagePoint>? endpoints)
    {
        ImagePoint center = ObjectTransformGeometry.Center(frame);
        double stemLength = endpoints is { Count: 2 } ? 0 : frame.Height / 2;
        CanvasPoint stem = DisplayPoint(ObjectTransformGeometry.Rotate(new(center.X, center.Y - stemLength), center, degrees));
        CanvasPoint handle = DisplayPoint(ObjectTransformGeometry.Rotate(
            new(center.X, center.Y - stemLength - RotationHandleGap / view.Scale), center, degrees));
        if (handle.X >= 10 && handle.X <= view.Width - 10 && handle.Y >= 10 && handle.Y <= view.Height - 10)
            return (stem, handle);
        CanvasPoint middle = DisplayPoint(center);
        // Put the grip on the other side when the usual top grip is outside
        // the visible viewport. A fixed-DIP gap keeps it usable at any zoom.
        return (new(2 * middle.X - stem.X, 2 * middle.Y - stem.Y), new(2 * middle.X - handle.X, 2 * middle.Y - handle.Y));
    }

    private ObjectGrip HitObjectGrip(CanvasPoint pointer)
    {
        if (!ShowsObjectHandles || selectedObject is not { } item) return ObjectGrip.None;
        ObjectGrip nearest = ObjectGrip.None;
        double nearestDistance = double.PositiveInfinity;
        foreach (ObjectHandle handle in ObjectHandlesAt(item, ObjectFrame(item), ObjectAngle(item), item.Shape?.Endpoints))
        {
            double x = handle.Position.X - pointer.X, y = handle.Position.Y - pointer.Y;
            double distance = x * x + y * y, radius = handle.Grip == ObjectGrip.Rotate ? 8 : 5;
            if (distance > radius * radius || distance >= nearestDistance) continue;
            nearest = handle.Grip;
            nearestDistance = distance;
        }
        return nearest;
    }

    private void DrawObjectSelection()
    {
        ObjectHandles.Children.Clear();
        ObjectOutlinePreview.Children.Clear();
        SelectionFrame.RenderTransform = null;
        if (tool == PreviewTool.Crop || selectionBounds is not { } bounds)
        {
            SelectionFrame.Visibility = Visibility.Collapsed;
            return;
        }
        ObjectDrag? drag = objectDrag ?? pendingObjectTransform;
        if (selectedObject is not { } item || (!ShowsObjectHandles && drag is null))
        {
            PositionRectangle(SelectionFrame, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            SelectionFrame.Visibility = Visibility.Visible;
            return;
        }
        ImageRectangle frame = drag?.Frame ?? ObjectFrame(item);
        float angle = drag?.Angle ?? ObjectAngle(item);
        IReadOnlyList<ImagePoint>? endpoints = drag?.Endpoints ?? item.Shape?.Endpoints;
        if (item.Shape is { } shape && drag is { Started: true } && drag.Grip != ObjectGrip.Move && !HasAnnotationDragPreview)
            DrawStyledShapePreview(shape, frame, angle, endpoints);
        if (endpoints is { Count: 2 })
        {
            SelectionFrame.Visibility = Visibility.Collapsed;
            DrawLine(ObjectOutlinePreview, DisplayPoint(endpoints[0]), DisplayPoint(endpoints[1]), 1.5, dashed: true);
        }
        else
        {
            PositionRectangle(SelectionFrame, frame.X, frame.Y, frame.Width, frame.Height);
            if (angle != 0) SelectionFrame.RenderTransform = new RotateTransform { Angle = angle };
            SelectionFrame.Visibility = Visibility.Visible;
            if (item.Shape?.Shape == AnnotationShape.Ellipse && drag is { Started: true })
            {
                var ellipse = new Ellipse { Stroke = objectAccent, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 2 },
                    RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform { Angle = angle } };
                PositionRectangle(ellipse, frame.X, frame.Y, frame.Width, frame.Height);
                ObjectOutlinePreview.Children.Add(ellipse);
            }
        }
        if (!ShowsObjectHandles) return;
        foreach (ObjectHandle handle in ObjectHandlesAt(item, frame, angle, endpoints))
        {
            bool rotation = handle.Grip == ObjectGrip.Rotate;
            if (rotation)
            {
                var connector = RotationGrip(frame, angle, endpoints);
                DrawLine(ObjectHandles, connector.Stem, connector.Handle, 1, dashed: false);
            }
            double size = rotation ? 16 : 9;
            var dot = new Ellipse { Width = size, Height = size, Fill = gripBackground, Stroke = objectAccent, StrokeThickness = 1.5 };
            CenterElementOn(ObjectHandles, dot, handle.Position, size);
            if (rotation)
            {
                var icon = new FontIcon { Glyph = "\uE72C", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 9,
                    Foreground = objectAccent, Width = size, Height = size };
                CenterElementOn(ObjectHandles, icon, handle.Position, size);
            }
        }
    }

    private void DrawStyledShapePreview(ShapeAnnotationSnapshot shape, ImageRectangle frame, float degrees,
        IReadOnlyList<ImagePoint>? endpoints)
    {
        Brush? outline = PreviewColor(shape.OutlineArgb, shape.OutlineOpacity);
        double width = Math.Max(1, shape.OutlineWidth * view.Scale);
        DoubleCollection pattern = PreviewDashPattern(shape.OutlinePattern);
        if (endpoints is { Count: 2 })
        {
            if (outline is null) return;
            CanvasPoint start = DisplayPoint(endpoints[0]), end = DisplayPoint(endpoints[1]);
            CanvasPoint shaft = end;
            if (shape.Shape == AnnotationShape.Arrow && ObjectTransformGeometry.ArrowHead(start, end, width) is { } head)
            {
                shaft = head.Base;
                ObjectOutlinePreview.Children.Add(new Polygon { Fill = outline, Points = ArrowPoints(end, head.Left, head.Right) });
            }
            ObjectOutlinePreview.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = shaft.X, Y2 = shaft.Y,
                Stroke = outline, StrokeThickness = width, StrokeDashArray = pattern, StrokeDashCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round });
            return;
        }
        Shape element = shape.Shape == AnnotationShape.Ellipse ? new Ellipse() : new Rectangle();
        element.Stroke = outline;
        element.StrokeThickness = width;
        element.StrokeDashArray = pattern;
        element.StrokeDashCap = PenLineCap.Round;
        element.Fill = PreviewColor(shape.FillArgb, shape.FillOpacity);
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = new RotateTransform { Angle = degrees };
        PositionRectangle(element, frame.X, frame.Y, frame.Width, frame.Height);
        ObjectOutlinePreview.Children.Add(element);
    }

    private static Brush? PreviewColor(uint? argb, int opacity) => argb is { } color && opacity > 0
        ? new SolidColorBrush(Color.FromArgb((byte)Math.Round((color >> 24) * opacity * .0065),
            (byte)(color >> 16), (byte)(color >> 8), (byte)color))
        : null;

    private static DoubleCollection PreviewDashPattern(ShapeOutlinePattern pattern) => pattern switch
    {
        ShapeOutlinePattern.Dashed => [3, 2],
        ShapeOutlinePattern.Dotted => [.2, 2.5],
        _ => []
    };

    private static PointCollection ArrowPoints(CanvasPoint tip, CanvasPoint left, CanvasPoint right) =>
        [new Point(tip.X, tip.Y), new Point(left.X, left.Y), new Point(right.X, right.Y)];

    private void DrawLine(Canvas layer, CanvasPoint from, CanvasPoint to, double width, bool dashed)
    {
        var line = new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, Stroke = objectAccent, StrokeThickness = width };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 4, 2 };
        layer.Children.Add(line);
    }

    private static void CenterElementOn(Canvas layer, FrameworkElement element, CanvasPoint position, double size)
    {
        Canvas.SetLeft(element, position.X - size / 2);
        Canvas.SetTop(element, position.Y - size / 2);
        layer.Children.Add(element);
    }

    private void UpdateObjectCursor(CanvasPoint pointer)
    {
        if (tool == PreviewTool.Crop)
        {
            UpdateCropCursor(pointer);
            return;
        }
        if (!IsEnabled || !view.HasImage || panning)
        {
            SetObjectCursor(null);
            return;
        }
        ObjectGrip grip = objectDrag?.Grip ?? HitObjectGrip(pointer);
        if (grip == ObjectGrip.None && !gestureActive &&
            (tool == PreviewTool.Select ? PickObject(pointer) : PickDrawingOutline(pointer)) is not null)
            grip = ObjectGrip.Move;
        if (grip is >= ObjectGrip.TopLeft and <= ObjectGrip.Left)
        {
            double angle = ((int)grip - (int)ObjectGrip.Right) * 45 + (selectedObject is { } item ? ObjectAngle(item) : 0);
            int axis = ((int)Math.Round(angle / 45, MidpointRounding.AwayFromZero) % 4 + 4) % 4;
            SetObjectCursor(axis switch
            {
                0 => InputSystemCursorShape.SizeWestEast,
                1 => InputSystemCursorShape.SizeNorthwestSoutheast,
                2 => InputSystemCursorShape.SizeNorthSouth,
                _ => InputSystemCursorShape.SizeNortheastSouthwest
            });
            return;
        }
        SetObjectCursor(grip switch
        {
            ObjectGrip.Move => InputSystemCursorShape.SizeAll,
            ObjectGrip.Rotate => InputSystemCursorShape.Hand,
            ObjectGrip.Head or ObjectGrip.Tail => InputSystemCursorShape.Cross,
            _ => null
        });
    }

    private void SetObjectCursor(InputSystemCursorShape? next)
    {
        if (next == objectCursor) return;
        objectCursor = next;
        if (next is not { } shape)
        {
            ProtectedCursor = null;
            return;
        }
        if (!objectCursors.TryGetValue(shape, out InputSystemCursor? cursor))
            objectCursors[shape] = cursor = InputSystemCursor.Create(shape);
        ProtectedCursor = cursor;
    }

    private void OnPointerEnteredCanvas(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        Point pointer = args.GetCurrentPoint(Host).Position;
        UpdateObjectCursor(new(pointer.X, pointer.Y));
    }

    private void OnPointerExitedCanvas(object sender, PointerRoutedEventArgs args)
    {
        if (!gestureActive) SetObjectCursor(null);
    }

    private bool TryNudgeObject(KeyRoutedEventArgs args)
    {
        if (tool != PreviewTool.Select || selectedObject is not { } item || !IsEnabled || gestureActive || IsTextEditing ||
            XamlRoot is null || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this) ||
            KeyIsDown(VirtualKey.Control) || KeyIsDown(VirtualKey.Menu)) return false;
        int step = KeyIsDown(VirtualKey.Shift) ? 10 : 1;
        ImagePoint delta = args.Key switch
        {
            VirtualKey.Left => new(-step, 0),
            VirtualKey.Right => new(step, 0),
            VirtualKey.Up => new(0, -step),
            VirtualKey.Down => new(0, step),
            _ => default
        };
        if (delta == default) return false;
        ImagePoint constrained = ConstrainAnnotationMove?.Invoke(item, delta) ?? delta;
        if (constrained != default)
            SelectionTransformCommitted?.Invoke(this, new(item, ObjectFrame(item), null, null, constrained));
        return true;
    }

    private static bool KeyIsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
}
