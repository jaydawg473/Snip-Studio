using Microsoft.UI.Xaml;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private PreviewTool beforeCrop = PreviewTool.Select;

    private async void OnCropClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed || !PreviewIsCurrent) return;
        if (selectedTool != PreviewTool.Crop)
        {
            PreviewTool previous = selectedTool;
            await ChooseToolAsync(PreviewTool.Crop);
            if (selectedTool != PreviewTool.Crop) return;
            beforeCrop = previous == PreviewTool.OcrSelect ? PreviewTool.Select : previous;
        }
        if (Viewport.CropFrame is null) Viewport.BeginCropFrame();
        Viewport.Focus(FocusState.Programmatic);
    }

    private async void OnApplyCropClicked(object sender, RoutedEventArgs args) => await ApplyCropAsync();
    private void OnCancelCropClicked(object sender, RoutedEventArgs args) => CancelCrop();

    private void CancelCrop()
    {
        if (busy || closed || selectedTool != PreviewTool.Crop) return;
        SetTool(beforeCrop);
        SetStatus("Crop canceled.");
    }

    private void UpdateCropCommands()
    {
        ApplyCropButton.IsEnabled = !busy && selectedTool == PreviewTool.Crop && Viewport.CanApplyCropFrame;
        CancelCropButton.IsEnabled = !busy;
    }

    private Task ApplyCropAsync() => RunActionAsync("Cropping the snip…", async () =>
    {
        if (selectedTool != PreviewTool.Crop || !Viewport.CanApplyCropFrame ||
            Viewport.CropFrame is not { } frame) return false;
        if (!session.Crop(new ImagePoint(frame.X, frame.Y),
            new ImagePoint(frame.X + frame.Width, frame.Y + frame.Height))) return false;
        ResetDocumentSelection();
        SetTool(PreviewTool.Select, whileBusy: true);
        // An edit uses the normal copy path. The viewport fits the new image dimensions.
        await RefreshPreviewAsync(resetView: false);
        SetStatus("Crop applied. Undo restores the full snip.");
        return true;
    });
}
