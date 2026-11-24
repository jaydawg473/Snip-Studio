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
using CropFrameHandle = global::SnipStudio.CropFrameHandle;

namespace SnipStudio.WinUI;

internal enum PreviewTool { Pan, Select, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text, Eraser, Crop, RemoveArea, Emoji, OcrSelect }

internal static class PreviewTools
{
    internal static bool IsShapeTool(this PreviewTool tool) =>
        tool is PreviewTool.Rectangle or PreviewTool.Ellipse or PreviewTool.Line or PreviewTool.Arrow;
}

internal sealed record TextEditDraft(int AnnotationIndex, string Text, string Rtf, string FontFamily,
    TextFontStyle Style, ImagePoint Location, uint Argb, float FontSize, float LayoutWidth, float MinimumHeight)
{
    internal uint? ReplacementBackground { get; init; }
}

internal sealed record SelectionTransformEventArgs(AnnotationSnapshot Selection, ImageRectangle Frame,
    IReadOnlyList<ImagePoint>? Endpoints, float? RotationDegrees, ImagePoint? MoveDelta = null);

internal sealed class DrawGestureEventArgs(PreviewTool tool, IReadOnlyList<ImagePoint> points,
    uint argb, float width, bool reachedPointLimit = false, bool freehand = false, bool isDrag = false,
    double snapTolerance = 0) : EventArgs
{
    internal PreviewTool Tool { get; } = tool;
    internal IReadOnlyList<ImagePoint> Points { get; } = points;
    internal uint Argb { get; } = argb;
    internal float Width { get; } = width;
    internal bool ReachedPointLimit { get; } = reachedPointLimit;
    internal bool Freehand { get; } = freehand;
    internal bool IsDrag { get; } = isDrag;
    internal double SnapTolerance { get; } = snapTolerance;
}

public sealed partial class EditorViewport : UserControl
{
    private const int MaximumPreviewPoints = 16_384;
    private const double EraserDiameterDip = 12;
    private readonly ViewportState view = new();
    private readonly List<ImagePoint> gesturePoints = [];
    private PreviewTool tool = PreviewTool.Pen;
    private bool gestureActive, panning;
    private bool gestureFreehand;
    private bool gestureDragged;
    private uint pointerId;
    private CanvasPoint previousPan;
    private uint gestureArgb;
    private float gestureWidth;
    private double gestureSnapTolerance;
    private XamlRoot? observedRoot;
    private ImageRectangle? selectionBounds;
    private IReadOnlyList<OcrBounds> recognizedBands = [];
    private IReadOnlyList<OcrBounds>? dragRecognizedBands;
    private OcrBounds? recognizedDraftMask;
    private TextEditDraft? textDraft;
    private TextEditDraft? initialTextDraft;
    private string initialNativeText = "";
    private string initialNativeRtf = "";
    private bool loadingText;
    private AnnotationSnapshot? selectedObject;

    internal event EventHandler<DrawGestureEventArgs>? GestureCommitted;
    internal event EventHandler? ViewChanged;
    internal event EventHandler? TextApplyRequested;
    internal event EventHandler? TextCancelRequested;
    internal event EventHandler? DraftChanged;
    internal event EventHandler<SelectionTransformEventArgs>? SelectionTransformCommitted;
    internal event EventHandler<string>? GestureFeedback;
    internal event EventHandler? RecognizedSelectionDismissed;
    internal Func<ImagePoint, ImagePoint, IReadOnlyList<OcrBounds>>? RecognizedSelectionPreview { get; set; }
    internal Func<ImagePoint, ImagePoint, double, IReadOnlyList<OcrBounds>>? SnappedHighlightPreview { get; set; }
    internal bool HasImage => view.HasImage;
    internal ImagePoint VisibleImageCenter
    {
        get
        {
            CanvasPoint center = view.VisibleImageCenter();
            return new(center.X, center.Y);
        }
    }
    internal bool IsFit => view.IsFit;
    internal double ZoomPercent => view.ZoomPercent;
    internal uint InkArgb { get; set; } = 0xFFE24046;
    internal float InkWidth { get; set; } = 3;
    internal bool SnapHighlighterToText { get; set; }
    internal global::SnipStudio.ShapeOutlinePattern ShapeLinePattern { get; set; }
    internal bool RequireCtrlForWheelZoom { get; set; } = true;
    internal bool IsTextEditing => textDraft is not null;
    internal bool TextDraftWithinLimit { get; private set; } = true;
    internal bool TextCanUndo => IsTextEditing && InlineEditor.Document.CanUndo();
    internal bool TextCanRedo => IsTextEditing && InlineEditor.Document.CanRedo();
    internal string SelectedInlineText => IsTextEditing ? InlineEditor.Document.Selection.Text : "";
    internal void UndoText() { if (TextCanUndo) { InlineEditor.Document.Undo(); UpdateTextDraftState(); } }
    internal void RedoText() { if (TextCanRedo) { InlineEditor.Document.Redo(); UpdateTextDraftState(); } }
    internal PreviewTool Tool
    {
        get => tool;
        set
        {
            CancelGesture();
            if (tool == PreviewTool.Crop && value != PreviewTool.Crop) ClearCropFrame();
            bool leaveTextActions = tool == PreviewTool.OcrSelect && tool != value;
            tool = value;
            if (leaveTextActions)
            {
                EndTextActionsGlow();
                SetRecognizedTextHighlights(null);
            }
            SetObjectCursor(null);
            UpdateScene();
        }
    }

    public EditorViewport() => InitializeComponent();

    internal void SetSelection(ImageRectangle? bounds)
    {
        pendingObjectTransform = null;
        ClearAnnotationDragPreview();
        selectedObject = null;
        selectionBounds = bounds;
        UpdateScene();
    }

    internal void SetSelectedAnnotation(AnnotationSnapshot? annotation)
    {
        pendingObjectTransform = null;
        ClearAnnotationDragPreview();
        selectedObject = annotation;
        selectionBounds = annotation?.Bounds;
        UpdateScene();
    }

    internal void SetRecognizedBands(IReadOnlyList<OcrBounds>? bands)
    {
        recognizedBands = bands ?? [];
        UpdateRecognizedBands();
    }

    internal void BeginTextEdit(TextEditDraft draft, OcrLineEditSnapshot? sourceLine = null, OcrLineSnapshot? sourceGeometry = null)
    {
        CancelGesture();
        textDraft = initialTextDraft = draft;
        recognizedDraftMask = sourceLine?.Mask;
        ConfigureSourceTextSelection(sourceGeometry, draft.Text);
        var editorBackground = new SolidColorBrush(sourceLine is null ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0));
        TextEditorFrame.Background = InlineEditor.Background = editorBackground;
        if (sourceLine is not null)
        {
            RecognizedDraftMask.Fill = new SolidColorBrush(ToColor(sourceLine.BackgroundArgb));
            InlineEditor.Resources["TextControlBackgroundFocused"] = editorBackground;
            InlineEditor.Resources["TextControlBackgroundPointerOver"] = editorBackground;
        }
        else
        {
            InlineEditor.Resources.Remove("TextControlBackgroundFocused");
            InlineEditor.Resources.Remove("TextControlBackgroundPointerOver");
        }
        loadingText = true;
        try
        {
            InlineEditor.FontFamily = new FontFamily(draft.FontFamily);
            InlineEditor.FontSize = draft.FontSize;
            if (!string.IsNullOrEmpty(draft.Rtf))
            {
                // Opening a saved document must preserve its paragraph boundaries.
                InlineEditor.Document.SetText(TextSetOptions.FormatRtf | TextSetOptions.ApplyRtfDocumentDefaults, draft.Rtf);
            }
            else InlineEditor.Document.SetText(TextSetOptions.None, draft.Text);
            InlineEditor.Document.ClearUndoRedoHistory();
            initialNativeText = ReadInlineText();
            InlineEditor.Document.GetText(TextGetOptions.FormatRtf, out initialNativeRtf);
            InlineEditor.Width = draft.LayoutWidth > 0 ? draft.LayoutWidth : Math.Min(
                Math.Max(80, draft.Text.Split('\n').Max(line => line.Length) * draft.FontSize * .65 + 12),
                Math.Max(8, view.ImageWidth - draft.Location.X));
            TextEditorFrame.Width = InlineEditor.Width + 2;
            InlineEditor.Height = draft.MinimumHeight > 0 ? draft.MinimumHeight : double.NaN;
            InlineEditor.MinHeight = draft.MinimumHeight > 0 ? 0 : draft.FontSize * 1.5;
            TextLayer.Visibility = Visibility.Visible;
            UpdateScene();
        }
        finally { loadingText = false; }
        UpdateTextDraftState();
        InlineEditor.Focus(FocusState.Programmatic);
    }

    internal TextEditDraft? CaptureTextDraft()
    {
        if (textDraft is null || initialTextDraft is null) return null;
        string plain = ReadInlineText();
        InlineEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
        if (MatchesInitialText(plain, rtf)) return initialTextDraft with
        {
            LayoutWidth = textDraft.LayoutWidth,
            ReplacementBackground = textDraft.ReplacementBackground
        };
        return textDraft with { Text = plain, Rtf = rtf };
    }

    private string ReadInlineText()
    {
        InlineEditor.Document.GetText(TextGetOptions.None, out string plain);
        // RichEdit contributes one terminal paragraph; preserve user-entered
        // paragraphs and remove only that native terminator.
        if (plain.EndsWith('\r')) plain = plain[..^1];
        return plain.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
    }

    private bool MatchesInitialText(string plain, string rtf) => initialTextDraft is not null &&
        string.Equals(plain, initialNativeText, StringComparison.Ordinal) &&
        string.Equals(rtf, initialNativeRtf, StringComparison.Ordinal);

    internal void FormatText(string? family, float? size, uint? argb, TextFontStyle? style)
    {
        if (textDraft is null) return;
        var selection = InlineEditor.Document.Selection;
        if (argb is uint color && selection.StartPosition == selection.EndPosition)
        {
            int textLength = ReadInlineText().Length;
            if (textLength > 0)
            {
                // The picker colors the note when no characters are selected.
                // Use a separate range to keep the active caret in place.
                var note = InlineEditor.Document.GetRange(0, textLength);
                var noteFormat = note.CharacterFormat;
                noteFormat.ForegroundColor = ToColor(color);
                note.CharacterFormat = noteFormat;
            }
        }
        var format = selection.CharacterFormat;
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
        selection.CharacterFormat = format;
        textDraft = textDraft with { FontFamily = family ?? textDraft.FontFamily, FontSize = size ?? textDraft.FontSize,
            Argb = argb ?? textDraft.Argb, Style = style ?? textDraft.Style };
        UpdateTextDraftState();
    }

    internal void EndTextEdit()
    {
        textDraft = initialTextDraft = null;
        initialNativeText = initialNativeRtf = "";
        recognizedDraftMask = null;
        ClearSourceTextSelection();
        RecognizedDraftMask.Visibility = Visibility.Collapsed;
        TextLayer.Visibility = Visibility.Collapsed;
        InlineEditor.Document.SetText(TextSetOptions.None, "");
        TextDraftWithinLimit = true;
        Focus(FocusState.Programmatic);
    }
    internal void RestoreTextFocus() { if (IsTextEditing) InlineEditor.Focus(FocusState.Programmatic); }
    internal void SelectInlineText(int start, int length)
    {
        if (textDraft is null) return;
        int textLength = ReadInlineText().Length;
        start = Math.Clamp(start, 0, textLength);
        int end = start + Math.Clamp(length, 0, textLength - start);
        InlineEditor.Document.Selection.SetRange(start, end);
    }

    internal void FormatTextStyle(TextFontStyle flag, bool enabled)
    {
        if (textDraft is null) return;
        var format = InlineEditor.Document.Selection.CharacterFormat;
        if (flag == TextFontStyle.Bold) format.Bold = enabled ? FormatEffect.On : FormatEffect.Off;
        else if (flag == TextFontStyle.Italic) format.Italic = enabled ? FormatEffect.On : FormatEffect.Off;
        else if (flag == TextFontStyle.Underline) format.Underline = enabled ? UnderlineType.Single : UnderlineType.None;
        InlineEditor.Document.Selection.CharacterFormat = format;
        textDraft = textDraft with { Style = enabled ? textDraft.Style | flag : textDraft.Style & ~flag };
        UpdateTextDraftState();
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void OnInlineTextChanged(object sender, RoutedEventArgs args) => UpdateTextDraftState();

    private void UpdateTextDraftState()
    {
        if (loadingText || textDraft is null) return;
        string text = ReadInlineText();
        TextDraftWithinLimit = text.Length <= 16000;
        if (recognizedDraftMask is not null)
        {
            InlineEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            ShowOriginalSourceText(MatchesInitialText(text, rtf) && textDraft.LayoutWidth == initialTextDraft?.LayoutWidth &&
                textDraft.ReplacementBackground == initialTextDraft?.ReplacementBackground);
        }
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnInlineKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; TextCancelRequested?.Invoke(this, EventArgs.Empty); }
        else if (args.Key == VirtualKey.Enter && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { args.Handled = true; TextApplyRequested?.Invoke(this, EventArgs.Empty); }
    }

    internal void SetImage(BitmapImage bitmap, int width, int height, bool resetView, long? revision = null)
    {
        pendingObjectTransform = null;
        CancelGesture();
        Picture.Source = bitmap;
        bool resetGeometry = resetView || view.ImageWidth != width || view.ImageHeight != height;
        bool changedRevision = revision.HasValue && revision != cropImageRevision;
        cropImageRevision = revision;
        if (resetGeometry || changedRevision) SetRecognizedTextHighlights(null);
        if (resetGeometry) view.SetImage(width, height);
        if (tool == PreviewTool.Crop && (resetGeometry || changedRevision)) ResetCropFrameForImage();
        EmptyState.Visibility = Visibility.Collapsed;
        ImageFrame.Visibility = Visibility.Visible;
        UpdateViewport();
        UpdateScene();
    }

    internal void ShowUnavailable()
    {
        pendingObjectTransform = null;
        CancelGesture();
        Picture.Source = null;
        SetRecognizedTextHighlights(null);
        ImageFrame.Visibility = Visibility.Collapsed;
        UpdateTextActionsGlow();
        DrawCropFrame();
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
        bool transformed = objectDrag is not null;
        bool cropReverted = RevertCropDrag();
        bool hadRecognizedPreview = dragRecognizedBands is not null;
        objectDrag = null;
        if (pendingObjectTransform?.Grip is not (ObjectGrip.Move or ObjectGrip.Rotate)) ClearAnnotationDragPreview();
        gestureActive = false;
        panning = false;
        gestureDragged = false;
        gestureSnapTolerance = 0;
        sourceSelectionPointer = null;
        dragRecognizedBands = null;
        gesturePoints.Clear();
        StrokePreview.Points.Clear();
        StrokePreview.Visibility = Visibility.Collapsed;
        SnappedHighlighterGeometry.Children.Clear();
        ArrowHeadPreview.Visibility = Visibility.Collapsed;
        RectanglePreview.Visibility = Visibility.Collapsed;
        EllipsePreview.Visibility = Visibility.Collapsed;
        EraserPreview.Visibility = Visibility.Collapsed;
        Host.ReleasePointerCaptures();
        if (cropReverted)
        {
            DrawCropFrame();
            CropFrameChanged?.Invoke(this, EventArgs.Empty);
        }
        if (hadRecognizedPreview) UpdateRecognizedBands();
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
        pendingObjectTransform = null;
        CancelGesture();
        EndTextActionsGlow();
        SetObjectCursor(null);
        if (observedRoot is not null) observedRoot.Changed -= OnXamlRootChanged;
        observedRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateViewport();
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateViewport();

    private void OnViewportLayoutUpdated(object? sender, object args)
    {
        if (!view.HasImage || !view.UsesNativePixels || !UpdateRasterRootOffset()) return;
        CancelGesture();
        UpdateScene();
    }

    private bool UpdateRasterRootOffset()
    {
        if (!Host.IsLoaded || XamlRoot?.Content is not UIElement root) return false;
        Point offset = Host.TransformToVisual(root).TransformPoint(new Point(0, 0));
        return double.IsFinite(offset.X) && double.IsFinite(offset.Y) &&
            view.SetRasterRootOffset(new CanvasPoint(offset.X, offset.Y));
    }

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
            if (view.UsesNativePixels) UpdateRasterRootOffset();
            // At actual size, both the image and its one-device-pixel frame
            // start on the raster grid. Overlays and pointer mapping use the
            // same snapped image origin as the inner edge of the frame.
            CanvasPoint origin = view.DisplayOrigin;
            double frame = view.FrameThickness;
            Canvas.SetLeft(ImageFrame, origin.X - frame);
            Canvas.SetTop(ImageFrame, origin.Y - frame);
            ImageFrame.BorderThickness = new Thickness(frame);
            ImageFrame.Width = view.DisplayWidth + 2 * frame;
            ImageFrame.Height = view.DisplayHeight + 2 * frame;
            UpdateTextActionsGlow();
            Picture.Width = view.DisplayWidth;
            Picture.Height = view.DisplayHeight;
            PositionAnnotationDragPreview();
            DrawObjectSelection();
            DrawCropFrame();
            PositionRecognizedTextHighlights();
            UpdateRecognizedBands();
            UpdateSourceTextVisual();
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

    private void UpdateRecognizedBands()
    {
        GeometryCollection tint = RecognizedBandTint.Children;
        GeometryCollection underline = RecognizedBandUnderline.Children;
        tint.Clear();
        underline.Clear();
        if (!view.HasImage) return;

        CanvasPoint image = view.DisplayOrigin;
        double visibleLeft = Math.Max(0, image.X), visibleTop = Math.Max(0, image.Y);
        double visibleRight = Math.Min(view.Width, image.X + view.DisplayWidth);
        double visibleBottom = Math.Min(view.Height, image.Y + view.DisplayHeight);
        double dpi = view.RasterizationScale;
        double thickness = Math.Max(1, Math.Round(dpi, MidpointRounding.AwayFromZero)) / dpi;
        foreach (OcrBounds band in dragRecognizedBands ?? recognizedBands)
        {
            CanvasPoint origin = view.ViewportAt(new CanvasPoint(band.X, band.Y));
            double inkBottom = origin.Y + band.Height * view.Scale;
            double left = Math.Max(visibleLeft, origin.X - 1);
            double top = Math.Max(visibleTop, origin.Y - 1);
            double right = Math.Min(visibleRight, origin.X + band.Width * view.Scale + 1);
            double bottom = Math.Min(visibleBottom, inkBottom + 1);
            if (!(right > left && bottom > top)) continue;

            tint.Add(new RectangleGeometry { Rect = new Rect(left, top, right - left, bottom - top) });
            // Keep the solid indicator below the glyphs and inside the image.
            double ruleTop = Math.Ceiling((inkBottom + .5) * dpi) / dpi;
            if (ruleTop + thickness <= visibleBottom)
                underline.Add(new RectangleGeometry { Rect = new Rect(left, ruleTop, right - left, thickness) });
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!view.HasImage || gestureActive || sourceSelectionPointer.HasValue || !IsEnabled) return;
        var current = args.GetCurrentPoint(Host);
        bool middle = current.Properties.IsMiddleButtonPressed;
        if (!middle && !current.Properties.IsLeftButtonPressed) return;
        CanvasPoint position = new(current.Position.X, current.Position.Y);
        if (!middle && IsTextEditing)
        {
            if (TryBeginSourceSelection(position, args)) return;
            if (!view.Contains(position))
            {
                args.Handled = true;
                TextApplyRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        bool pan = middle || tool == PreviewTool.Pan;
        bool selecting = !pan && tool == PreviewTool.Select;
        bool cropping = !pan && tool == PreviewTool.Crop;
        ObjectGrip handle = pan ? ObjectGrip.None : HitObjectGrip(position);
        AnnotationSnapshot? drawingOutline = !pan && !selecting && !cropping &&
            handle == ObjectGrip.None &&
            args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            ? PickDrawingOutline(position) : null;
        CropFrameHandle cropGrip = cropping ? CropGripAt(position) : CropFrameHandle.None;
        if (cropping && cropGrip == CropFrameHandle.None) return;
        if (!pan && !cropping && handle == ObjectGrip.None && !view.Contains(position) && (!selecting || SelectedFrameAt(position) is null)) return;
        if (!Host.CapturePointer(args.Pointer)) return;
        Focus(FocusState.Pointer);
        pointerId = args.Pointer.PointerId;
        gestureActive = true;
        gestureDragged = false;
        panning = pan;
        previousPan = position;
        gestureArgb = InkArgb;
        gestureWidth = tool == PreviewTool.Eraser && !pan
            ? (float)Math.Min(EraserDiameterDip / view.Scale, 2048)
            : Math.Clamp(InkWidth, 1, 100);
        gestureFreehand = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        // Capture the choice and tolerance once so the preview and the
        // committed stroke use the same image-space hit test.
        gestureSnapTolerance = !pan && tool == PreviewTool.Highlighter && SnapHighlighterToText && !gestureFreehand
            ? Math.Min(20, 8 / view.Scale)
            : 0;
        if (cropping)
        {
            BeginCropDrag(position, cropGrip);
            args.Handled = true;
            return;
        }
        if (selecting || handle != ObjectGrip.None || drawingOutline is not null)
        {
            BeginObjectDrag(position, handle, drawingOutline);
            args.Handled = true;
            return;
        }
        SetObjectCursor(null);
        if (!pan)
        {
            CanvasPoint image = view.ClampedImageAt(position);
            gesturePoints.Add(new ImagePoint(image.X, image.Y));
            var color = Color.FromArgb((byte)(gestureArgb >> 24), (byte)(gestureArgb >> 16),
                (byte)(gestureArgb >> 8), (byte)gestureArgb);
            double width = gestureWidth * view.Scale;
            if (tool == PreviewTool.Eraser)
            {
                ShowEraserPreview(view.ViewportAt(image));
            }
            else if (gestureSnapTolerance > 0)
            {
                SnappedHighlighterPreview.Fill = new SolidColorBrush(color);
                UpdateSnappedHighlight(gesturePoints[0]);
            }
            else if (tool == PreviewTool.OcrSelect)
            {
                dragRecognizedBands = [];
                UpdateRecognizedBands();
            }
            else if (IsBoxTool(tool))
            {
                bool drawsShape = tool is PreviewTool.Rectangle or PreviewTool.Ellipse;
                RectanglePreview.Stroke = new SolidColorBrush(drawsShape ? color : Color.FromArgb(255, 0, 120, 212));
                RectanglePreview.StrokeThickness = drawsShape ? width : 1.5;
                RectanglePreview.StrokeDashArray = drawsShape ? PreviewDashPattern(ShapeLinePattern) : [4, 2];
                RectanglePreview.StrokeDashCap = PenLineCap.Round;
                if (tool == PreviewTool.Ellipse)
                {
                    EllipsePreview.StrokeDashArray = PreviewDashPattern(ShapeLinePattern);
                    EllipsePreview.StrokeDashCap = PenLineCap.Round;
                }
                RectanglePreview.Visibility = Visibility.Visible;
                UpdateRectanglePreview(image);
            }
            else if (tool == PreviewTool.Line || tool == PreviewTool.Arrow)
            {
                StrokePreview.Stroke = new SolidColorBrush(color);
                StrokePreview.StrokeThickness = width;
                StrokePreview.Opacity = 1;
                StrokePreview.StrokeDashArray = PreviewDashPattern(ShapeLinePattern);
                StrokePreview.StrokeDashCap = PenLineCap.Round;
                StrokePreview.StrokeEndLineCap = tool == PreviewTool.Arrow ? PenLineCap.Flat : PenLineCap.Round;
                ArrowHeadPreview.Fill = StrokePreview.Stroke;
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Visibility = Visibility.Visible;
            }
            else
            {
                StrokePreview.Stroke = new SolidColorBrush(color);
                StrokePreview.StrokeThickness = width;
                StrokePreview.Opacity = 1;
                StrokePreview.StrokeDashArray = [];
                StrokePreview.StrokeEndLineCap = PenLineCap.Round;
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Points.Add(current.Position);
                StrokePreview.Visibility = Visibility.Visible;
            }
        }
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (TryContinueSourceSelection(args)) return;
        var current = args.GetCurrentPoint(Host);
        CanvasPoint position = new(current.Position.X, current.Position.Y);
        if (!gestureActive)
        {
            if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) UpdateObjectCursor(position);
            return;
        }
        if (args.Pointer.PointerId != pointerId) return;
        if (panning)
        {
            view.Pan(position.X - previousPan.X, position.Y - previousPan.Y);
            previousPan = position;
            UpdateScene();
        }
        else if (objectDrag is not null || tool == PreviewTool.Select)
            AdvanceObjectDrag(position, (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        else if (tool == PreviewTool.Crop)
            AdvanceCropDrag(position);
        else AddGesturePoint(position);
        args.Handled = true;
    }

    private void AddGesturePoint(CanvasPoint position)
    {
        CanvasPoint image = view.ClampedImageAt(position);
        ImagePoint next = new(image.X, image.Y);
        if (gestureSnapTolerance > 0)
        {
            if (gesturePoints.Count == 1) gesturePoints.Add(next);
            else gesturePoints[1] = next;
            UpdateSnappedHighlight(next);
            return;
        }
        if (IsTwoPointTool(tool))
        {
            if (gesturePoints.Count == 1) gesturePoints.Add(next);
            else gesturePoints[1] = next;
            if (tool == PreviewTool.Line || tool == PreviewTool.Arrow)
            {
                CanvasPoint lineDisplay = view.ViewportAt(image);
                if (tool == PreviewTool.Arrow) UpdateArrowDrawingPreview(lineDisplay);
                else StrokePreview.Points[1] = new Point(lineDisplay.X, lineDisplay.Y);
            }
            else if (tool == PreviewTool.OcrSelect)
            {
                const double dragThresholdDip = 3;
                double dragX = position.X - previousPan.X, dragY = position.Y - previousPan.Y;
                gestureDragged |= dragX * dragX + dragY * dragY >= dragThresholdDip * dragThresholdDip;
                if (gestureDragged && RecognizedSelectionPreview is { } preview)
                {
                    dragRecognizedBands = preview(gesturePoints[0], next);
                    UpdateRecognizedBands();
                }
            }
            else UpdateRectanglePreview(image);
            return;
        }
        ImagePoint last = gesturePoints[^1];
        double dx = next.X - last.X, dy = next.Y - last.Y;
        CanvasPoint display = view.ViewportAt(image);
        if (tool == PreviewTool.Eraser) ShowEraserPreview(display);
        if (dx * dx + dy * dy < 0.0625) return;
        if (gesturePoints.Count < MaximumPreviewPoints)
        {
            gesturePoints.Add(next);
            if (tool != PreviewTool.Eraser) StrokePreview.Points.Add(new Point(display.X, display.Y));
        }
        else
        {
            // Finish the complete bounded path instead of replacing its tail with
            // a false straight chord. The editor reports that another stroke can
            // continue after the user lifts the pointer.
            CommitCurrentGesture(reachedPointLimit: true);
        }
    }

    private void UpdateSnappedHighlight(ImagePoint end)
    {
        GeometryCollection geometry = SnappedHighlighterGeometry.Children;
        geometry.Clear();
        if (SnappedHighlightPreview is not { } preview) return;

        CanvasPoint image = view.DisplayOrigin;
        double visibleLeft = Math.Max(0, image.X), visibleTop = Math.Max(0, image.Y);
        double visibleRight = Math.Min(view.Width, image.X + view.DisplayWidth);
        double visibleBottom = Math.Min(view.Height, image.Y + view.DisplayHeight);
        foreach (OcrBounds band in preview(gesturePoints[0], end, gestureSnapTolerance))
        {
            CanvasPoint origin = view.ViewportAt(new CanvasPoint(band.X, band.Y));
            double left = Math.Max(visibleLeft, origin.X), top = Math.Max(visibleTop, origin.Y);
            double right = Math.Min(visibleRight, origin.X + band.Width * view.Scale);
            double bottom = Math.Min(visibleBottom, origin.Y + band.Height * view.Scale);
            if (right > left && bottom > top)
                geometry.Add(new RectangleGeometry { Rect = new Rect(left, top, right - left, bottom - top) });
        }
    }

    private void ShowEraserPreview(CanvasPoint center)
    {
        double diameter = gestureWidth * view.Scale;
        EraserPreview.Width = diameter;
        EraserPreview.Height = diameter;
        Canvas.SetLeft(EraserPreview, center.X - diameter / 2);
        Canvas.SetTop(EraserPreview, center.Y - diameter / 2);
        EraserPreview.Visibility = Visibility.Visible;
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

    private void UpdateArrowDrawingPreview(CanvasPoint tip)
    {
        Point start = StrokePreview.Points[0];
        if (ObjectTransformGeometry.ArrowHead(new(start.X, start.Y), tip, StrokePreview.StrokeThickness) is not { } head)
        {
            ArrowHeadPreview.Visibility = Visibility.Collapsed;
            StrokePreview.Points[1] = start;
            return;
        }
        StrokePreview.Points[1] = new Point(head.Base.X, head.Base.Y);
        ArrowHeadPreview.Points = ArrowPoints(tip, head.Left, head.Right);
        ArrowHeadPreview.Visibility = Visibility.Visible;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (TryContinueSourceSelection(args))
        {
            sourceSelectionPointer = null;
            Host.ReleasePointerCapture(args.Pointer);
            return;
        }
        if (!gestureActive || args.Pointer.PointerId != pointerId) return;
        if (!panning && tool == PreviewTool.Crop)
        {
            var point = args.GetCurrentPoint(Host);
            FinishCropDrag(new CanvasPoint(point.Position.X, point.Position.Y));
            args.Handled = true;
            return;
        }
        if (!panning && (objectDrag is not null || tool == PreviewTool.Select))
        {
            var point = args.GetCurrentPoint(Host);
            FinishObjectDrag(new CanvasPoint(point.Position.X, point.Position.Y), (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
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
        bool freehand = gestureFreehand;
        bool dragged = gestureDragged;
        double snapTolerance = gestureSnapTolerance;
        CancelGesture();
        if (commit && points.Length > 0 && (!IsAreaTool(completedTool) ||
            (points.Length > 1 && Math.Abs(points[0].X - points[1].X) >= 0.5 && Math.Abs(points[0].Y - points[1].Y) >= 0.5)))
            GestureCommitted?.Invoke(this, new DrawGestureEventArgs(completedTool, points, argb, width, reachedPointLimit, freehand, dragged, snapTolerance));
    }

    private static bool IsBoxTool(PreviewTool value) => value is PreviewTool.Rectangle or PreviewTool.Ellipse or PreviewTool.RemoveArea or PreviewTool.Emoji or PreviewTool.OcrSelect or PreviewTool.Text;
    private static bool IsTwoPointTool(PreviewTool value) => IsBoxTool(value) || value is PreviewTool.Line or PreviewTool.Arrow;
    private static bool IsAreaTool(PreviewTool value) => value is PreviewTool.Rectangle or PreviewTool.Ellipse or PreviewTool.RemoveArea;

    private void PositionRectangle(FrameworkElement element, double x, double y, double width, double height)
    {
        CanvasPoint origin = view.ViewportAt(new CanvasPoint(x, y));
        Canvas.SetLeft(element, origin.X);
        Canvas.SetTop(element, origin.Y);
        element.Width = Math.Max(0, width * view.Scale);
        element.Height = Math.Max(0, height * view.Scale);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (sourceSelectionPointer == args.Pointer.PointerId) sourceSelectionPointer = null;
        if (gestureActive && args.Pointer.PointerId == pointerId) CancelGesture();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (sourceSelectionPointer == args.Pointer.PointerId) sourceSelectionPointer = null;
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
        if (TryHandleCropKey(args.Key)) { args.Handled = true; return; }
        if (TryNudgeObject(args)) { args.Handled = true; return; }
        if (args.Key != VirtualKey.Escape) return;
        bool dismissTextActions = tool == PreviewTool.OcrSelect && !gestureActive;
        CancelGesture();
        if (dismissTextActions) RecognizedSelectionDismissed?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }
}
