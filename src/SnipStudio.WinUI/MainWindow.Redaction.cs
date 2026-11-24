using Microsoft.UI.Xaml;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private async void OnRedactForSharingClicked(object sender, RoutedEventArgs args) =>
        await ShowRedactionAsync(findSuggestions: false);

    private async void OnQuickRedactClicked(object sender, RoutedEventArgs args) =>
        await ShowRedactionAsync(findSuggestions: true);

    private async Task ShowRedactionAsync(bool findSuggestions)
    {
        if (closed || busy || !session.HasImage) return;
        if (Viewport.IsTextEditing)
        {
            SetStatus("Finish the text edit with Done or Cancel before opening redaction.");
            return;
        }

        await RunActionAsync(findSuggestions ? "Finding email addresses and phone numbers…" : "Preparing redaction preview…", async () =>
        {
            OcrSnapshot? snapshot = findSuggestions ? CurrentRecognition() : null;
            if (findSuggestions && snapshot is null)
            {
                try { snapshot = await session.RecognizeTextAsync(ActionToken); }
                catch (TimeoutException) { /* Keep the manual review available. */ }
            }
            var original = await session.RenderPreviewAsync(ActionToken);
            if (closed) return false;
            OcrRedactionSuggestions? suggestions = findSuggestions
                ? snapshot is { Available: true } && snapshot.Revision == original.Revision && session.IsOcrSnapshotCurrent(snapshot)
                    ? session.SuggestOcrRedactions(snapshot) : new([], [])
                : null;
            var review = new RedactionReview(Root.XamlRoot, AppWindow.Id, original, session.ImageWidth,
                session.ImageHeight, suggestedName, preferences.ScreenshotFolder, suggestions);
            string? published = await review.ShowAsync(ActionToken);
            SetStatus(published ?? "Redaction review closed.");
            return published is not null;
        }, commitDraft: false);
    }
}
