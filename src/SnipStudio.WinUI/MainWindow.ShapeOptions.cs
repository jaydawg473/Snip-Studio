using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnipStudio.WinUI.Backend;
using ShapeOutlinePattern = SnipStudio.ShapeOutlinePattern;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private ShapeOutlinePattern drawingLinePattern = ShapeOutlinePattern.Solid;
    private PreviewTool? pendingShapeTool;
    private AnnotationSnapshot? widthSelection;
    private float? pendingSelectionWidth;

    private void UpdateShapeToolIcon(PreviewTool tool)
    {
        (ToggleMenuFlyoutItem? item, string? glyph) = tool switch
        {
            PreviewTool.Rectangle => (RectangleButton, "\u25AD"),
            PreviewTool.Ellipse => (EllipseButton, "\u2B2D"),
            PreviewTool.Line => (LineButton, "\u2571"),
            PreviewTool.Arrow => (ArrowButton, "\u2197"),
            _ => (null, null)
        };
        ShapesButton.IsChecked = item is not null;
        if (glyph is not null) ShapesModeIcon.Glyph = glyph;
        string label = item is null ? "Shapes" : $"Shapes, {item.Text} selected";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShapesButton, label);
        SetWorkspaceToolTip(ShapesButton, label);
    }

    private void OnShapeChoiceClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed) return;
        pendingShapeTool = (sender as ToggleMenuFlyoutItem)?.Name switch
        {
            nameof(RectangleButton) => PreviewTool.Rectangle,
            nameof(EllipseButton) => PreviewTool.Ellipse,
            nameof(LineButton) => PreviewTool.Line,
            nameof(ArrowButton) => PreviewTool.Arrow,
            _ => null
        };
    }

    private void OnShapeFlyoutClosed(object sender, object args)
    {
        PreviewTool? selection = pendingShapeTool;
        pendingShapeTool = null;
        // WinUI closes the flyout after Click. Apply the tool on the next
        // dispatcher turn so the command bars are never rebuilt in the popup.
        if (selection is PreviewTool next)
            DispatcherQueue.TryEnqueue(async () => await ChooseToolAsync(next));
    }

    private static void ShowLinePattern(DropDownButton button, ShapeOutlinePattern? pattern)
    {
        button.Content = pattern is ShapeOutlinePattern value ? $"{value} line" : "Line style";
        if (button.Flyout is MenuFlyout flyout)
            foreach (var item in flyout.Items.OfType<ToggleMenuFlyoutItem>())
                item.IsChecked = TryLinePattern(item, out var itemPattern) && itemPattern == pattern;
    }

    private static bool TryLinePattern(object sender, out ShapeOutlinePattern pattern)
    {
        pattern = default;
        return sender is ToggleMenuFlyoutItem { Tag: string name } && Enum.TryParse(name, out pattern);
    }

    private void OnDrawLinePatternClicked(object sender, RoutedEventArgs args)
    {
        if (busy || !TryLinePattern(sender, out var pattern)) return;
        Viewport.CancelGesture();
        drawingLinePattern = pattern;
        Viewport.ShapeLinePattern = pattern;
        ShowLinePattern(DrawLinePatternButton, pattern);
        UpdateInkStrokePreview();
    }

    private async void OnSelectionLinePatternClicked(object sender, RoutedEventArgs args)
    {
        if (!TryLinePattern(sender, out var pattern)) return;
        await RunActionAsync("Changing line style…", async () =>
        {
            if (session.GetAnnotation(selectedAnnotationIndex) is not { Shape: { } shape } selection ||
                !session.UpdateShapeStyle(selection.Index, shape.FillArgb, shape.OutlineArgb, shape.OutlineWidth,
                    shape.FillOpacity, shape.OutlineOpacity, pattern)) return false;
            await RefreshPreviewAsync(resetView: false);
            UpdateSelection();
            SetStatus(shape.OutlineArgb.HasValue ? $"{pattern} line applied." : $"{pattern} line saved. Turn on the outline in Properties to see it.");
            return true;
        });
    }

    private void OnSelectionWidthOpening(object sender, object args)
    {
        widthSelection = session.GetAnnotation(selectedAnnotationIndex);
        if (widthSelection?.Shape is { } shape) SelectionWidthBox.Value = shape.OutlineWidth;
    }

    private void OnApplySelectionWidthClicked(object sender, RoutedEventArgs args)
    {
        double width = SelectionWidthBox.Value;
        if (widthSelection?.Shape is null || !double.IsFinite(width) || width is < 1 or > 100)
            return;

        pendingSelectionWidth = (float)width;
        SelectionWidthFlyout.Hide();
    }

    private void OnSelectionWidthClosed(object sender, object args)
    {
        AnnotationSnapshot? selection = widthSelection;
        float? width = pendingSelectionWidth;
        widthSelection = null;
        pendingSelectionWidth = null;
        if (selection is { Shape: not null } && width is float value)
            DispatcherQueue.TryEnqueue(async () => await ApplySelectionWidthAsync(selection, value));
    }

    private Task ApplySelectionWidthAsync(AnnotationSnapshot selection, float width) =>
        RunActionAsync("Changing outline width…", async () =>
        {
            if (selection.Index != selectedAnnotationIndex || !session.IsAnnotationCurrent(selection)) return false;
            if (!session.UpdateShapeOutlineWidth(selection, width)) return false;
            await RefreshPreviewAsync(resetView: false);
            UpdateSelection();
            SetStatus(selection.Shape!.OutlineArgb.HasValue
                ? $"Outline width set to {width:0.##} px."
                : $"Outline width set to {width:0.##} px. Turn on the outline in Properties to see it.");
            return true;
        });

    private static DoubleCollection StrokePattern(ShapeOutlinePattern pattern) => pattern switch
    {
        ShapeOutlinePattern.Dashed => new DoubleCollection { 3.4, 2.4 },
        ShapeOutlinePattern.Dotted => new DoubleCollection { 0.25, 2.3 },
        _ => new DoubleCollection()
    };
}
