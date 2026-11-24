using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private const string EditableProjectExtension = EditorSession.EditableProjectExtension;

    private static bool IsEditableProjectPath(string path) =>
        string.Equals(Path.GetExtension(path), EditableProjectExtension, StringComparison.OrdinalIgnoreCase);

    private async void OnSaveEditableProjectClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Choose an editable snip file…", SaveEditableProjectWithPickerAsync, commitDraft: false);

    private async Task<bool> SaveEditableProjectWithPickerAsync()
    {
        if (!session.HasImage) { SetStatus("Open or capture an image before saving an editable snip."); return false; }
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFolder = displayPath is null ? preferences.ScreenshotFolder : Path.GetDirectoryName(displayPath),
            SuggestedFileName = suggestedName,
            DefaultFileExtension = EditableProjectExtension,
            CommitButtonText = "Save editable snip"
        };
        picker.FileTypeChoices.Add("Snip Studio editable file", new List<string> { EditableProjectExtension });
        var chosen = await picker.PickSaveFileAsync();
        if (chosen is null) { SetStatus("Save canceled. Your changes are kept."); return false; }
        return await SaveEditableProjectToPathAsync(chosen.Path, replacementConfirmed: true);
    }

    private async Task<bool> SaveEditableProjectToPathAsync(string path, bool replacementConfirmed = false)
    {
        var cancellationToken = ActionToken;
        cancellationToken.ThrowIfCancellationRequested();
        ImageSaveTarget target = await session.PrepareEditableProjectSaveTargetAsync(path, replacementConfirmed, cancellationToken);
        if (Viewport.IsTextEditing)
        {
            CommitTextDraft();
            await RefreshPreviewAsync(resetView: false);
        }
        SetStatus("Saving editable snip…");
        var result = await session.SaveEditableProjectAsync(target, cancellationToken);
        if (!result.SavedCurrentRevision)
        {
            SetStatus("Saved an earlier snip revision. Your current changes remain unsaved.");
            return false;
        }
        displayPath = result.Path;
        suggestedName = Path.GetFileNameWithoutExtension(result.Path);
        AddRecentSafely(result.Path);
        SetStatus("Saved editable snip " + Path.GetFileName(result.Path) + ".");
        return true;
    }
}
