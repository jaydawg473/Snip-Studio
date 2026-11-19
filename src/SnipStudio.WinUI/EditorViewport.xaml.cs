using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SnipStudio.WinUI.Backend;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Microsoft.UI.Text;

namespace SnipStudio.WinUI;

internal enum PreviewTool { Pan, Select, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Eraser, Crop, RemoveArea, Emoji, OcrSelect }

internal sealed record TextEditDraft(int AnnotationIndex, string Text, string Rtf, string FontFamily,
    TextFontStyle Style, ImagePoint Location, uint Argb, float FontSize, float LayoutWidth, float MinimumHeight);

internal sealed record SelectionTransformEventArgs(AnnotationSnapshot Selection, ImageRectangle Frame,
    IReadOnlyList<ImagePoint>? Endpoints, float? RotationDegrees);

internal sealed class DrawGestureEventArgs(PreviewTool tool, IReadOnlyList<ImagePoint> points,
    uint argb, float width, bool reachedPointLimit = false, bool freehand = false) : EventArgs
{
    internal PreviewTool Tool { get; } = tool;
    internal IReadOnlyList<ImagePoint> Points { get; } = points;
    internal uint Argb { get; } = argb;
    internal float Width { get; } = width;
    internal bool ReachedPointLimit { get; } = reachedPointLimit;
    internal bool Freehand { get; } = freehand;
}

public sealed partial class EditorViewport : UserControl
{
    private const int MaximumPreviewPoints = 16_384;
    private readonly ViewportState view = new();
    private readonly List<ImagePoint> gesturePoints = [];
    private PreviewTool tool = PreviewTool.Pen;
    private bool gestureActive, panning;
    private bool gestureFreehand;
    private uint pointerId;
    private CanvasPoint previousPan;
    private uint gestureArgb;
    private float gestureWidth;
    private XamlRoot? observedRoot;
    private ImageRectangle? selectionBounds;
    private IReadOnlyList<OcrBounds> recognizedBands = [];
    private TextEditDraft? textDraft;
    private bool loadingText;
    private bool draftFormatChanged;
    private AnnotationSnapshot? selectedObject;
    private int transformHandle;
    private ImageRectangle proposedFrame;
    private ImagePoint[]? proposedEndpoints;
    private float proposedRotation;
    private double initialRotationAngle;

    internal event EventHandler<DrawGestureEventArgs>? GestureCommitted;
    internal event EventHandler? ViewChanged;
    internal event EventHandler? TextApplyRequested;
    internal event EventHandler? TextCancelRequested;
    internal event EventHandler? DraftChanged;
    internal event EventHandler<SelectionTransformEventArgs>? SelectionTransformCommitted;
    internal event EventHandler<string>? GestureFeedback;
    internal bool HasImage => view.HasImage;
    internal bool IsFit => view.IsFit;
    internal double ZoomPercent => view.ZoomPercent;
    internal uint InkArgb { get; set; } = 0xFFE24046;
    internal float InkWidth { get; set; } = 3;
    internal bool RequireCtrlForWheelZoom { get; set; } = true;
    internal bool IsTextEditing => textDraft is not null;
    internal bool TextDraftWithinLimit { get; private set; } = true;
    internal bool TextCanUndo => IsTextEditing && InlineEditor.Document.CanUndo();
    internal bool TextCanRedo => IsTextEditing && InlineEditor.Document.CanRedo();
    internal string SelectedInlineText => IsTextEditing ? InlineEditor.Document.Selection.Text : "";
    internal void UndoText() { if (TextCanUndo) InlineEditor.Document.Undo(); }
    internal void RedoText() { if (TextCanRedo) InlineEditor.Document.Redo(); }
    internal PreviewTool Tool
    {
        get => tool;
        set { CancelGesture(); tool = value; UpdateScene(); }
    }

    public EditorViewport() => InitializeComponent();

    internal void SetSelection(ImageRectangle? bounds)
    {
        selectedObject = null;
        selectionBounds = bounds;
        UpdateScene();
    }

    internal void SetSelectedAnnotation(AnnotationSnapshot? annotation)
    {
        selectedObject = annotation;
        selectionBounds = annotation?.Bounds;
        UpdateScene();
    }

    internal void SetRecognizedBands(IReadOnlyList<OcrBounds>? bands)
    {
        recognizedBands = bands ?? [];
        UpdateScene();
    }

    internal void BeginTextEdit(TextEditDraft draft)
    {
        CancelGesture();
        textDraft = draft;
        draftFormatChanged = false;
        loadingText = true;
        try
        {
            InlineEditor.FontFamily = new FontFamily(draft.FontFamily);
            InlineEditor.FontSize = draft.FontSize;
            if (!string.IsNullOrEmpty(draft.Rtf)) InlineEditor.Document.SetText(TextSetOptions.FormatRtf, draft.Rtf);
            else InlineEditor.Document.SetText(TextSetOptions.None, draft.Text);
            InlineEditor.Document.ClearUndoRedoHistory();
            InlineEditor.Width = Math.Max(8, draft.LayoutWidth > 0 ? draft.LayoutWidth : Math.Min(Math.Max(80, draft.Text.Split('\n').Max(line => line.Length) * draft.FontSize * .65 + 12), Math.Max(8, view.ImageWidth - draft.Location.X)));
            TextEditorFrame.Width = InlineEditor.Width + 2;
            InlineEditor.MinHeight = Math.Max(draft.FontSize * 1.5, draft.MinimumHeight);
            TextLayer.Visibility = Visibility.Visible;
            UpdateScene();
        }
        finally { loadingText = false; }
        OnInlineTextChanged(InlineEditor, new RoutedEventArgs());
        InlineEditor.Focus(FocusState.Programmatic);
    }

    internal TextEditDraft? CaptureTextDraft()
    {
        if (textDraft is null) return null;
        InlineEditor.Document.GetText(TextGetOptions.None, out string plain);
        InlineEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
        // RichEdit contributes one terminal paragraph; preserve user-entered
        // paragraphs and remove only that native terminator.
        if (plain.EndsWith("\r", StringComparison.Ordinal)) plain = plain[..^1];
        plain = plain.Replace("\r", "\n", StringComparison.Ordinal);
        if ((!draftFormatChanged || !InlineEditor.Document.CanUndo()) && string.Equals(plain, textDraft.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal), StringComparison.Ordinal))
        { plain = textDraft.Text; rtf = textDraft.Rtf; }
        return textDraft with { Text = plain, Rtf = rtf };
    }

    internal void FormatText(string? family, float? size, uint? argb, TextFontStyle? style)
    {
        if (textDraft is null) return;
        draftFormatChanged = true;
        var format = InlineEditor.Document.Selection.CharacterFormat;
        if (family is not null) format.Name = family;
        if (size.HasValue) format.Size = size.Value * 0.75f;
        if (argb.HasValue) format.ForegroundColor = ToColor(argb.Value);
        if (style.HasValue)
        {
            format.Bold = style.Value.HasFlag(TextFontStyle.Bold) ? FormatEffect.On : FormatEffect.Off;
            format.Italic = style.Value.HasFlag(TextFontStyle.Italic) ? FormatEffect.On : FormatEffect.Off;
            format.Underline = style.Value.HasFlag(TextFontStyle.Underline) ? UnderlineType.Single : UnderlineType.None;
            format.Strikethrough = style.Value.HasFlag(TextFontStyle.Strikeout) ? FormatEffect.On : FormatEffect.Off;
        }
        InlineEditor.Document.Selection.CharacterFormat = format;
        textDraft = textDraft with { FontFamily = family ?? textDraft.FontFamily, FontSize = size ?? textDraft.FontSize,
            Argb = argb ?? textDraft.Argb, Style = style ?? textDraft.Style };
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void EndTextEdit()
    {
        textDraft = null;
        TextLayer.Visibility = Visibility.Collapsed;
        InlineEditor.Document.SetText(TextSetOptions.None, "");
        TextDraftWithinLimit = true;
        Focus(FocusState.Programmatic);
    }
    internal void RestoreTextFocus() { if (IsTextEditing) InlineEditor.Focus(FocusState.Programmatic); }

    internal void FormatTextStyle(TextFontStyle flag, bool enabled)
    {
        if (textDraft is null) return;
        var format = InlineEditor.Document.Selection.CharacterFormat;
        if (flag == TextFontStyle.Bold) format.Bold = enabled ? FormatEffect.On : FormatEffect.Off;
        else if (flag == TextFontStyle.Italic) format.Italic = enabled ? FormatEffect.On : FormatEffect.Off;
        else if (flag == TextFontStyle.Underline) format.Underline = enabled ? UnderlineType.Single : UnderlineType.None;
        InlineEditor.Document.Selection.CharacterFormat = format;
        textDraft = textDraft with { Style = enabled ? textDraft.Style | flag : textDraft.Style & ~flag };
        draftFormatChanged = true;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void OnInlineTextChanged(object sender, RoutedEventArgs args)
    {
        if (loadingText || textDraft is null) return;
        InlineEditor.Document.GetText(TextGetOptions.None, out string text);
        TextDraftWithinLimit = text.Length <= 16000;
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnInlineKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; TextCancelRequested?.Invoke(this, EventArgs.Empty); }
        else if (args.Key == VirtualKey.Enter && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { args.Handled = true; TextApplyRequested?.Invoke(this, EventArgs.Empty); }
    }

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
        bool transformed = transformHandle != 0;
        transformHandle = 0;
        proposedEndpoints = null;
        gestureActive = false;
        panning = false;
        gesturePoints.Clear();
        StrokePreview.Points.Clear();
        StrokePreview.Visibility = Visibility.Collapsed;
        RectanglePreview.Visibility = Visibility.Collapsed;
        EllipsePreview.Visibility = Visibility.Collapsed;
        Host.ReleasePointerCaptures();
        if (transformed)
        {
            selectionBounds = selectedObject?.Bounds;
            UpdateScene();
        }
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
            if (selectionBounds is ImageRectangle bounds)
            {
                PositionRectangle(SelectionFrame, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                SelectionFrame.Visibility = Visibility.Visible;
            }
            else SelectionFrame.Visibility = Visibility.Collapsed;
            RecognizedBands.Children.Clear();
            foreach (OcrBounds band in recognizedBands)
            {
                var rectangle = new Microsoft.UI.Xaml.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(75, 0, 120, 215)) };
                PositionRectangle(rectangle, band.X, band.Y, band.Width, band.Height);
                RecognizedBands.Children.Add(rectangle);
            }
            DrawObjectHandles();
            if (textDraft is not null)
            {
                CanvasPoint location = view.ViewportAt(new CanvasPoint(textDraft.Location.X, textDraft.Location.Y));
                Canvas.SetLeft(TextEditorFrame, location.X - view.Scale);
                Canvas.SetTop(TextEditorFrame, location.Y - view.Scale);
                TextEditorFrame.RenderTransform = new ScaleTransform { ScaleX = view.Scale, ScaleY = view.Scale };
            }
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!view.HasImage || gestureActive || !IsEnabled || IsTextEditing) return;
        var current = args.GetCurrentPoint(Host);
        bool middle = current.Properties.IsMiddleButtonPressed;
        if (!middle && !current.Properties.IsLeftButtonPressed) return;
        CanvasPoint position = new(current.Position.X, current.Position.Y);
        bool pan = middle || tool == PreviewTool.Pan;
        int handle = !middle && tool == PreviewTool.Select ? HitHandle(position) : 0;
        if (!pan && handle == 0 && !view.Contains(position)) return;
        if (!Host.CapturePointer(args.Pointer)) return;
        Focus(FocusState.Pointer);
        pointerId = args.Pointer.PointerId;
        gestureActive = true;
        panning = pan;
        previousPan = position;
        gestureArgb = InkArgb;
        gestureWidth = Math.Clamp(InkWidth, 1, 100);
        gestureFreehand = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (handle != 0 && selectedObject is not null)
        {
            transformHandle = handle;
            proposedFrame = ObjectFrame(selectedObject);
            proposedEndpoints = selectedObject.Shape?.Endpoints.ToArray();
            proposedRotation = selectedObject.Emoji?.RotationDegrees ?? 0;
            CanvasPoint image = view.ImageAt(position);
            initialRotationAngle = Math.Atan2(image.Y - (proposedFrame.Y + proposedFrame.Height / 2), image.X - (proposedFrame.X + proposedFrame.Width / 2));
            args.Handled = true;
            return;
        }
        if (!pan)
        {
            CanvasPoint image = view.ClampedImageAt(position);
            gesturePoints.Add(new ImagePoint(image.X, image.Y));
            var color = Color.FromArgb((byte)(gestureArgb >> 24), (byte)(gestureArgb >> 16),
                (byte)(gestureArgb >> 8), (byte)gestureArgb);
            double width = gestureWidth * view.Scale;
            if (IsBoxTool(tool))
            {
                RectanglePreview.Stroke = new SolidColorBrush(color);
                RectanglePreview.StrokeThickness = width;
                RectanglePreview.Visibility = Visibility.Visible;
                UpdateRectanglePreview(image);
            }
            else if (tool == PreviewTool.Line || tool == PreviewTool.Arrow)
            {
                StrokePreview.Stroke = new SolidColorBrush(color);
                StrokePreview.StrokeThickness = width;
                StrokePreview.Opacity = 1;
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Visibility = Visibility.Visible;
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
        if (transformHandle != 0)
            UpdateObjectTransform(position);
        else if (panning)
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
        if (IsTwoPointTool(tool))
        {
            if (gesturePoints.Count == 1) gesturePoints.Add(next);
            else gesturePoints[1] = next;
            if (tool == PreviewTool.Line || tool == PreviewTool.Arrow)
            {
                CanvasPoint lineDisplay = view.ViewportAt(image);
                StrokePreview.Points[1] = new Point(lineDisplay.X, lineDisplay.Y);
            }
            else UpdateRectanglePreview(image);
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
        if (tool == PreviewTool.Ellipse)
        {
            RectanglePreview.Visibility = Visibility.Collapsed;
            EllipsePreview.Stroke = RectanglePreview.Stroke;
            EllipsePreview.StrokeThickness = RectanglePreview.StrokeThickness;
            Canvas.SetLeft(EllipsePreview, Math.Min(first.X, last.X));
            Canvas.SetTop(EllipsePreview, Math.Min(first.Y, last.Y));
            EllipsePreview.Width = Math.Abs(last.X - first.X);
            EllipsePreview.Height = Math.Abs(last.Y - first.Y);
            EllipsePreview.Visibility = Visibility.Visible;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!gestureActive || args.Pointer.PointerId != pointerId) return;
        if (transformHandle != 0 && selectedObject is not null)
        {
            var point = args.GetCurrentPoint(Host);
            UpdateObjectTransform(new CanvasPoint(point.Position.X, point.Position.Y));
            var result = new SelectionTransformEventArgs(selectedObject, proposedFrame,
                transformHandle is 9 or 10 ? proposedEndpoints : null, transformHandle == 11 ? proposedRotation : null);
            CancelGesture();
            SelectionTransformCommitted?.Invoke(this, result);
            args.Handled = true;
            return;
        }
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
        if (commit && points.Length > 0 && (!IsAreaTool(completedTool) ||
            (points.Length > 1 && Math.Abs(points[0].X - points[1].X) >= 0.5 && Math.Abs(points[0].Y - points[1].Y) >= 0.5)))
            GestureCommitted?.Invoke(this, new DrawGestureEventArgs(completedTool, points, argb, width, reachedPointLimit, gestureFreehand));
    }

    private static bool IsBoxTool(PreviewTool value) => value is PreviewTool.Rectangle or PreviewTool.Ellipse or PreviewTool.Crop or PreviewTool.RemoveArea or PreviewTool.Emoji or PreviewTool.OcrSelect or PreviewTool.Select or PreviewTool.Text;
    private static bool IsTwoPointTool(PreviewTool value) => IsBoxTool(value) || value is PreviewTool.Line or PreviewTool.Arrow;
    private static bool IsAreaTool(PreviewTool value) => value is PreviewTool.Rectangle or PreviewTool.Ellipse or PreviewTool.Crop or PreviewTool.RemoveArea or PreviewTool.Emoji;

    private void PositionRectangle(FrameworkElement element, double x, double y, double width, double height)
    {
        CanvasPoint origin = view.ViewportAt(new CanvasPoint(x, y));
        Canvas.SetLeft(element, origin.X);
        Canvas.SetTop(element, origin.Y);
        element.Width = Math.Max(0, width * view.Scale);
        element.Height = Math.Max(0, height * view.Scale);
    }

    private static ImageRectangle ObjectFrame(AnnotationSnapshot item)
    {
        if (item.Emoji is not null) return item.Emoji.Frame;
        if (item.Shape is not null) return item.Shape.GeometryBounds;
        if (item.Text is not null) return new ImageRectangle(item.Text.Location.X, item.Text.Location.Y,
            item.Text.LayoutWidth > 0 ? item.Text.LayoutWidth : item.Bounds.Width,
            item.Text.MinimumHeight > 0 ? item.Text.MinimumHeight : item.Bounds.Height);
        return item.Bounds;
    }

    private IEnumerable<(int Code, CanvasPoint Position)> HandleLocations()
    {
        if (selectedObject is null || tool != PreviewTool.Select || selectedObject.Kind == AnnotationKind.Ink) yield break;
        if (selectedObject.Shape?.Endpoints.Count == 2)
        {
            IReadOnlyList<ImagePoint> ends = transformHandle is 9 or 10 && proposedEndpoints is not null ? proposedEndpoints : selectedObject.Shape.Endpoints;
            for (int index = 0; index < 2; index++) yield return (9 + index, view.ViewportAt(new CanvasPoint(ends[index].X, ends[index].Y)));
            yield break;
        }
        ImageRectangle bounds = transformHandle is >= 1 and <= 8 ? proposedFrame : ObjectFrame(selectedObject);
        double middleX = bounds.X + bounds.Width / 2, middleY = bounds.Y + bounds.Height / 2;
        (double X, double Y)[] points = [(bounds.Left, bounds.Top), (middleX, bounds.Top), (bounds.Right, bounds.Top),
            (bounds.Right, middleY), (bounds.Right, bounds.Bottom), (middleX, bounds.Bottom), (bounds.Left, bounds.Bottom), (bounds.Left, middleY)];
        for (int index = 0; index < points.Length; index++)
            yield return (index + 1, view.ViewportAt(new CanvasPoint(points[index].X, points[index].Y)));
        if (selectedObject.Emoji is not null)
        {
            CanvasPoint top = view.ViewportAt(new CanvasPoint(middleX, bounds.Top));
            yield return (11, new CanvasPoint(top.X, top.Y - 24));
        }
    }

    private int HitHandle(CanvasPoint position)
    {
        int nearest = 0;
        double nearestDistance = 64;
        foreach (var handle in HandleLocations())
        {
            double dx = handle.Position.X - position.X, dy = handle.Position.Y - position.Y;
            double distance = dx * dx + dy * dy;
            if (distance <= nearestDistance) { nearest = handle.Code; nearestDistance = distance; }
        }
        return nearest;
    }

    private void DrawObjectHandles()
    {
        ObjectHandles.Children.Clear();
        foreach (var handle in HandleLocations())
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = handle.Code == 11 ? 12 : 9, Height = handle.Code == 11 ? 12 : 9,
                Fill = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)), StrokeThickness = 1.5 };
            Canvas.SetLeft(dot, handle.Position.X - dot.Width / 2); Canvas.SetTop(dot, handle.Position.Y - dot.Height / 2);
            ObjectHandles.Children.Add(dot);
        }
    }

    private void UpdateObjectTransform(CanvasPoint position)
    {
        if (selectedObject is null || transformHandle == 0) return;
        CanvasPoint image = view.ClampedImageAt(position);
        ImageRectangle original = ObjectFrame(selectedObject);
        if (transformHandle is 9 or 10 && proposedEndpoints is not null)
        {
            proposedEndpoints[transformHandle - 9] = new ImagePoint(image.X, image.Y);
            ImagePoint a = proposedEndpoints[0], b = proposedEndpoints[1];
            proposedFrame = new ImageRectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
        else if (transformHandle == 11)
        {
            CanvasPoint unbounded = view.ImageAt(position);
            double angle = Math.Atan2(unbounded.Y - (original.Y + original.Height / 2), unbounded.X - (original.X + original.Width / 2));
            proposedRotation = (float)((selectedObject.Emoji?.RotationDegrees ?? 0) + (angle - initialRotationAngle) * 180 / Math.PI);
        }
        else
        {
            double left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
            if (transformHandle is 1 or 7 or 8) left = Math.Min(image.X, right - 1);
            if (transformHandle is 3 or 4 or 5) right = Math.Max(image.X, left + 1);
            if (transformHandle is 1 or 2 or 3) top = Math.Min(image.Y, bottom - 1);
            if (transformHandle is 5 or 6 or 7) bottom = Math.Max(image.Y, top + 1);
            proposedFrame = new ImageRectangle(left, top, right - left, bottom - top);
        }
        selectionBounds = proposedFrame;
        GestureFeedback?.Invoke(this, transformHandle == 11 ? $"Emoji rotation: {proposedRotation:0.#}°. Release to apply; Escape cancels."
            : $"Object size: {proposedFrame.Width:0.#} × {proposedFrame.Height:0.#} px. Release to apply; Escape cancels.");
        UpdateScene();
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
        if (!RequireCtrlForWheelZoom || (args.KeyModifiers & VirtualKeyModifiers.Control) != 0)
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
