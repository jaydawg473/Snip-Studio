using Microsoft.UI.Xaml;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private async void OnCopyOcrTableClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed || selectedTool != PreviewTool.OcrSelect) return;
        long intent = DesktopImageActions.BeginClipboardWrite();
        await RunActionAsync("Finding table cells…", async () =>
        {
            var source = await GetCurrentRecognitionAsync();
            var table = session.PrepareRecognizedTable(source, ocrSelection is { Text.Length: > 0 } ? ocrSelection : null);
            if (table is null)
            {
                ErrorNotice.Title = "Couldn’t copy a table";
                ErrorNotice.Message = "Select aligned text across at least two rows, then try again. Your previous clipboard content is still available.";
                ErrorNotice.IsOpen = true;
                SetStatus("Copy as table couldn’t find aligned cells.");
                return false;
            }
            string? tsv = table.TabSeparatedText;
            bool copied = DesktopTextClipboard.Copy(tsv, intent, html: table.Html);
            SetStatus(copied ? $"Copied a table with {table.RowCount} rows and {table.ColumnCount} columns."
                + (tsv is null ? " Paste as a formatted table." : "")
                : "Copy skipped because a newer clipboard action is active.");
            return copied;
        }, commitDraft: false);
    }
}
