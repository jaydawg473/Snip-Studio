using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private (uint Argb, PreviewTool Tool)? unrecordedInk;
    private int penOpacity = 100;
    private byte highlighterAlpha = 80;
    private bool syncingStrokeControls;
    private float penWidth = 3, highlighterWidth = 3, shapeWidth = 3;

    private void OnInkFlyoutOpening(object sender, object args)
    {
        unrecordedInk = null;
        SyncInkStrokeControls();
        RecentInkSwatches.Children.Clear();
        foreach (uint argb in preferences.RecentInkColors)
        {
            string name = $"#{argb & 0xFFFFFF:X6}";
            var swatch = new Button
            {
                Width = 28, Height = 28, MinWidth = 0, MinHeight = 0,
                Background = new SolidColorBrush(InkFromArgb(argb)),
                Padding = new Thickness(0)
            };
            AutomationProperties.SetName(swatch, name);
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                InkColorPicker.Color = InkFromArgb(argb);
                ColorButton.Flyout.Hide();
            };
            RecentInkSwatches.Children.Add(swatch);
        }
        RecentInkPanel.Visibility = RecentInkSwatches.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnInkFlyoutClosed(object sender, object args)
    {
        if (unrecordedInk is not { } sample) return;
        unrecordedInk = null;
        RememberInk(sample.Argb, sample.Tool);
    }

    private async void OnInkEyedropperClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed) return;
        if (allRecordingWindows.Any(window => window.IsRecording))
        {
            SetStatus("Stop the recording before sampling a color.");
            return;
        }
        ColorButton.Flyout.Hide();
        await RunActionAsync("Pick a screen color. Esc cancels.", async () =>
        {
            uint? color = await CaptureCoordinator.PickDesktopInkAsync(ActionToken);
            if (color is not { } argb) { SetStatus("Color sampling canceled."); return false; }
            changingColor = true;
            try { InkColorPicker.Color = InkFromArgb(argb); }
            finally { changingColor = false; }
            ApplyInk(argb);
            SetStatus($"Selected color #{argb & 0xFFFFFF:X6}.");
            RememberInk(argb, selectedTool);
            return true;
        }, commitDraft: false);
    }

    private void ApplyInk(uint argb)
    {
        Viewport.CancelGesture();
        if (selectedTool == PreviewTool.Highlighter) highlighterColor = argb;
        else if (selectedTool == PreviewTool.Text) textColor = argb;
        else penColor = argb;
        Viewport.InkArgb = EffectiveInk(argb, selectedTool);
        if (selectedTool == PreviewTool.Text) Viewport.FormatText(null, null, argb, null);
        UpdateInkStrokePreview();
        RefreshInkToolIcons();
    }

    private int ChosenInkPercent => selectedTool == PreviewTool.Highlighter
        ? (int)Math.Round(highlighterAlpha * 100d / 255) : penOpacity;

    private static byte OpacityAlpha(int percent) => (byte)Math.Round(percent * 255 / 100d);

    private uint EffectiveInk(uint argb, PreviewTool tool) => tool == PreviewTool.Text
        ? argb : ((uint)(tool == PreviewTool.Highlighter ? highlighterAlpha : OpacityAlpha(penOpacity)) << 24) | (argb & 0xFFFFFFu);

    private bool HasStrokeWidth => selectedTool is PreviewTool.Pen or PreviewTool.Highlighter || selectedTool.IsShapeTool();

    private void RestoreToolStrokeWidth()
    {
        if (!HasStrokeWidth) return;
        Viewport.InkWidth = selectedTool switch
        {
            PreviewTool.Pen => penWidth,
            PreviewTool.Highlighter => highlighterWidth,
            _ => shapeWidth
        };
        syncingStrokeControls = true;
        try { StrokeWidthBox.Value = Viewport.InkWidth; }
        finally { syncingStrokeControls = false; }
    }

    private void OnStrokeWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (Viewport is null || closed || syncingStrokeControls || !HasStrokeWidth) return;
        if (!double.IsFinite(args.NewValue))
        {
            RestoreSizeValue(sender, Viewport.InkWidth);
            return;
        }
        Viewport.CancelGesture();
        float width = (float)Math.Clamp(args.NewValue, 1, 100);
        if (selectedTool == PreviewTool.Pen) penWidth = width;
        else if (selectedTool == PreviewTool.Highlighter) highlighterWidth = width;
        else shapeWidth = width;
        Viewport.InkWidth = width;
        SyncInkStrokeControls();
    }

    private void SyncInkStrokeControls()
    {
        if (Viewport is null) return;
        bool isStroke = HasStrokeWidth;
        if (isStroke) UpdateInkStrokePreview();
        if (InkStrokeControls is null) return;
        InkStrokeControls.Visibility = isStroke ? Visibility.Visible : Visibility.Collapsed;
        if (!isStroke) return;
        syncingStrokeControls = true;
        try
        {
            PickerStrokeWidth.Value = Viewport.InkWidth;
            PickerStrokeWidth.Header = selectedTool == PreviewTool.Highlighter && SnapHighlighterButton.IsChecked == true
                ? "Freehand width (px)" : "Width (px)";
            PickerStrokeOpacity.Value = ChosenInkPercent;
        }
        finally { syncingStrokeControls = false; }
    }

    private void OnPickerStrokeWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (syncingStrokeControls || Viewport is null || busy || closed || InkStrokeControls?.Visibility != Visibility.Visible) return;
        if (!double.IsFinite(args.NewValue)) { RestoreSizeValue(sender, Viewport.InkWidth); return; }
        StrokeWidthBox.Value = Math.Clamp(args.NewValue, 1, 100);
    }

    private void OnPickerOpacityChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (syncingStrokeControls || Viewport is null || busy || closed || InkStrokeControls?.Visibility != Visibility.Visible) return;
        if (!double.IsFinite(args.NewValue)) { RestoreSizeValue(sender, ChosenInkPercent); return; }
        Viewport.CancelGesture();
        int percent = (int)Math.Round(Math.Clamp(args.NewValue, 1, 100));
        if (selectedTool == PreviewTool.Highlighter) highlighterAlpha = OpacityAlpha(percent);
        else penOpacity = percent;
        uint baseColor = selectedTool == PreviewTool.Highlighter ? highlighterColor : penColor;
        Viewport.InkArgb = EffectiveInk(baseColor, selectedTool);
        SyncInkStrokeControls();
    }

    private void UpdateInkStrokePreview()
    {
        if (Viewport is null) return;
        uint argb = Viewport.InkArgb;
        var color = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var brush = new SolidColorBrush(color);
        double width = Viewport.InkWidth;
        if (InlineStrokePreviewLine is not null && (selectedTool is PreviewTool.Pen or PreviewTool.Highlighter))
        {
            InlineStrokePreviewLine.Stroke = brush;
            InlineStrokePreviewLine.StrokeThickness = width;
        }
        if (InkSampleLine is null) return;
        InkSampleLine.Stroke = brush;
        InkSampleLine.StrokeThickness = width;
        InkSampleLine.StrokeDashArray = StrokePattern(selectedTool.IsShapeTool() ? drawingLinePattern : SnipStudio.ShapeOutlinePattern.Solid);
        InkSampleBackground.Height = Math.Max(32, width + 12);
    }

    private void RememberInk(uint argb, PreviewTool tool)
    {
        if (closed) return;
        try
        {
            var store = new DesktopPreferencesStore();
            bool highlighter = tool == PreviewTool.Highlighter;
            bool pen = tool is not (PreviewTool.Text or PreviewTool.Highlighter);
            var current = store.RememberInk(argb, pen, highlighter);
            if (highlighter) preferences.HighlighterArgb = current.HighlighterArgb;
            if (pen) preferences.InkArgb = current.InkArgb;
            preferences.RecentInkColors = current.RecentInkColors;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            SetStatus("The color was selected. Recent colors could not be saved. " + error.Message);
        }
    }

    private static Windows.UI.Color InkFromArgb(uint argb) =>
        Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
