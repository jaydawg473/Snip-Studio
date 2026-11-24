using Microsoft.UI.Xaml;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private async void OnAdvancedTextClicked(object sender, RoutedEventArgs args) => await ShowAdvancedTextAsync();

    private Task ShowAdvancedTextAsync() => RunActionAsync("Opening Advanced text…", async () =>
    {
        if (!Viewport.IsTextEditing)
        {
            if (session.GetAnnotation(selectedAnnotationIndex) is not { Text: { } note } annotation)
            {
                SetStatus("Click existing text or add a text box, then open Advanced text.");
                return false;
            }
            SetTool(PreviewTool.Text, whileBusy: true);
            BeginTextDraft(note.Location, note.Location, annotation);
        }

        TextEditDraft draft = Viewport.CaptureTextDraft()!;
        (int start, int end) = Viewport.GetTextDraftSelection();
        uint? fill = ocrLineDraft is { ExistingAnnotationIndex: < 0 } source
            ? draft.ReplacementBackground ?? source.BackgroundArgb : null;
        float availableWidth = (float)Math.Clamp(session.ImageWidth - draft.Location.X, 1, 32768);
        if (ocrLineDraft is { ExistingAnnotationIndex: < 0 } line)
            availableWidth = Math.Min(availableWidth, line.LayoutWidth);

        var result = await AdvancedTextDialog.ShowAsync(Root.XamlRoot, draft, start, end, availableWidth, fill);
        if (result is null || closed) return false;
        Viewport.ApplyAdvancedTextDraft(result.Draft, result.Start, result.End);
        CommitTextDraft();
        await RefreshPreviewAsync(resetView: false);
        SetStatus("Text kept. Click text to edit it again.");
        return true;
    }, commitDraft: false);
}
