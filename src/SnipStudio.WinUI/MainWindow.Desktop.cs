using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private static readonly HashSet<MainWindow> editorWindows = [];
    // Video review windows remain independent when their originating editor
    // closes. Keep their process lifetime separate from that editor's set.
    private static readonly HashSet<RecordingWindow> allRecordingWindows = [];
    private static MainWindow? desktopOwner;
    private static bool applicationExitPending;
    private readonly DesktopPreferencesStore preferencesStore = new();
    private DesktopPreferences preferences = new();
    private DesktopIntegrationService? desktopIntegration;
    private readonly HashSet<RecordingWindow> recordingWindows = [];
    private bool explicitExit;
    private bool desktopVisualInitialized;
    private string? externalSnapshotPath;
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void InitializeDesktopIntegration()
    {
        editorWindows.Add(this);
        try
        {
            if (!desktopVisualInitialized)
            {
                preferences = preferencesStore.Load();
                ApplyVisualPreferences();
                desktopVisualInitialized = true;
            }
            if (desktopOwner is not null && desktopOwner != this) return;
            desktopOwner = this;
            if (desktopIntegration is not null) return;
            desktopIntegration = new DesktopIntegrationService(WindowHandle);
            desktopIntegration.CaptureRequested += async (_, _) => { if (!busy && !applicationExitPending) await CaptureAsync(CaptureKind.Rectangle, false); };
            desktopIntegration.RecordingRequested += (_, _) => { if (!busy && !applicationExitPending) OpenRecordingWindow(); };
            desktopIntegration.RestoreRequested += (_, _) => { AppWindow.Show(); Activate(); };
            desktopIntegration.ExitRequested += async (_, _) => await RequestApplicationExitAsync();
            desktopIntegration.IntegrationFailed += (_, args) => SetStatus("Windows integration could not finish: " + args.Exception.Message);
            desktopIntegration.SetHotkeysEnabled(preferences.HotkeysEnabled);
            desktopIntegration.SetTrayEnabled(preferences.KeepRunningInTray);
            if (preferencesStore.LoadWarning is string warning) SetStatus(warning);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            SetStatus("Optional desktop settings could not finish: " + exception.Message);
        }
    }

    private void ApplyVisualPreferences(bool initializeInk = true)
    {
        Root.RequestedTheme = preferences.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (initializeInk)
        {
            penColor = preferences.InkArgb;
            highlighterColor = preferences.HighlighterArgb;
            StrokeWidthBox.Value = preferences.StrokeWidth;
        }
        Viewport.RequireCtrlForWheelZoom = preferences.RequireCtrlForWheelZoom;
        SetTool(selectedTool, whileBusy: true);
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Settings", async () =>
    {
        DesktopIntegrationService integration = desktopOwner?.desktopIntegration ?? throw new InvalidOperationException("Desktop integration did not initialize. Reopen the editor and review its startup message before changing Windows settings.");
        DesktopPreferences? draft = await DesktopDialogs.ShowSettingsAsync(Root.XamlRoot, preferences, integration);
        if (draft is null) return false;
        DesktopPreferences previous = preferences.Clone();
        bool startupChanged = draft.StartupWithWindows != previous.StartupWithWindows;
        try
        {
            integration.SetHotkeysEnabled(draft.HotkeysEnabled);
            integration.SetTrayEnabled(draft.KeepRunningInTray);
            if (startupChanged) integration.SetStartupEnabled(draft.StartupWithWindows);
            preferencesStore.Save(draft);
            preferences = preferencesStore.Load();
            foreach (MainWindow editor in editorWindows) { editor.preferences = preferences.Clone(); editor.ApplyVisualPreferences(initializeInk: false); }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            var rollbackFailures = new List<string>();
            try { integration.SetHotkeysEnabled(previous.HotkeysEnabled); } catch (Exception rollback) { rollbackFailures.Add(rollback.Message); }
            try { integration.SetTrayEnabled(previous.KeepRunningInTray); } catch (Exception rollback) { rollbackFailures.Add(rollback.Message); }
            if (startupChanged) try { integration.SetStartupEnabled(previous.StartupWithWindows); } catch (Exception rollback) { rollbackFailures.Add(rollback.Message); }
            throw new InvalidOperationException("Settings were not saved. " + failure.Message + (rollbackFailures.Count > 0 ? " Review Windows integration settings: " + string.Join(" ", rollbackFailures) : " Previous desktop choices were restored."), failure);
        }
        SetStatus("Settings saved for this WinUI build."); return true;
    }, commitDraft: false);

    private async void OnRecentClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Recent files", async () =>
    {
        string? path = await DesktopDialogs.ShowRecentAsync(Root.XamlRoot, preferencesStore.RecentPaths);
        if (path is null) return false;
        if (IsVideoPath(path)) { OpenRecordingWindow(path); return true; }
        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This entry is an exported PDF. Open the original image to continue editing; PDF import is not supported.");
        return await OpenPathCoreAsync(path);
    }, commitDraft: false);

    public Task OpenInitialPathAsync(string path) => RunActionAsync("Opening file…", async () =>
    {
        if (IsVideoPath(path)) { OpenRecordingWindow(path); return true; }
        return await OpenPathCoreAsync(path);
    }, commitDraft: false);

    private async Task<bool> OpenPathCoreAsync(string path)
    {
        if (!await ConfirmReplaceAsync()) return false;
        session.Open(path);
        if (Viewport.IsTextEditing) Viewport.EndTextEdit();
        ResetDocumentSelection();
        displayPath = path;
        suggestedName = Path.GetFileNameWithoutExtension(path) + "-edited";
        AddRecentSafely(path);
        await RefreshPreviewAsync(resetView: true);
        SetStatus("Opened " + Path.GetFileName(path) + ". " + ToolInstruction()); return true;
    }

    private static bool IsVideoPath(string path) => new[] { ".mp4", ".wmv", ".mov", ".avi", ".m4v" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void AddRecentSafely(string path)
    {
        try { preferencesStore.AddRecent(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A video review can outlive the editor that opened it. Persist its
            // saved path, but never update an already closed XAML window.
            MainWindow? recipient = closed ? editorWindows.FirstOrDefault(editor => !editor.closed) : this;
            if (recipient is null) return;
            recipient.PreviewNotice.Title = "Recent history could not be saved";
            recipient.PreviewNotice.Message = exception.Message + " Your image and saved file are kept.";
            recipient.PreviewNotice.IsOpen = true;
        }
    }

    private async void OnCaptureClicked(object sender, RoutedEventArgs args) => await CaptureAsync(CaptureKind.Rectangle, false);
    private async void OnCaptureModeClicked(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } && Enum.TryParse(tag, out CaptureKind mode)) await CaptureAsync(mode, false);
    }
    private async void OnScrollingClicked(object sender, RoutedEventArgs args) => await CaptureAsync(CaptureKind.Rectangle, true);

    private async Task CaptureAsync(CaptureKind kind, bool scrolling)
    {
        await RunActionAsync(scrolling ? "Choose the scrolling area…" : "Preparing capture…", async () =>
        {
            bool newWindow = preferences.CaptureInNewWindow && session.HasImage;
            if (!newWindow && !await ConfirmReplaceAsync()) return false;
            var request = new CaptureRequest { OwnerWindow = WindowHandle, Kind = kind, DelaySeconds = preferences.CaptureDelaySeconds, UseHdrColorCorrection = preferences.UseHdrColorCorrection, HideTaskbars = preferences.HideTaskbars, KeepPrimaryTaskbarVisible = preferences.KeepPrimaryTaskbarVisible };
            CapturedImage? capture;
            try
            {
                if (scrolling) capture = await CaptureCoordinator.CaptureScrollingAsync(request, new Progress<CaptureProgress>(progress => SetStatus(progress.Status)), cancellationToken: ActionToken);
                else capture = await CaptureCoordinator.CaptureAsync(request, ActionToken);
            }
            finally { if (!closed) { AppWindow.Show(); Activate(); } }
            if (capture is null) { SetStatus("Capture canceled. Your previous snip is kept."); return false; }
            if (capture.StoppedEarly)
            {
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Keep this partial scrolling capture?", Content = capture.Warning, PrimaryButtonText = "Keep capture", CloseButtonText = "Discard", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { SetStatus("Partial capture discarded. Your previous snip is kept."); return false; }
            }
            string captureName = "Snip Studio " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
            if (newWindow)
            {
                var editor = new MainWindow(); editor.Activate();
                await editor.OpenCapturedImageAsync(capture.PngBytes, captureName);
            }
            else
            {
                session.OpenImageBytes(capture.PngBytes); ResetDocumentSelection();
                if (Viewport.IsTextEditing) Viewport.EndTextEdit();
                displayPath = null; suggestedName = captureName;
                await RefreshPreviewAsync(resetView: true);
            }
            string feedback = capture.Warning ?? "";
            if (preferences.AutoSaveOriginalCaptures)
            {
                try
                {
                    string folder = preferences.ScreenshotFolder;
                    string destination = Path.Combine(folder, captureName + "-" + Guid.NewGuid().ToString("N")[..6] + ".png");
                    await Task.Run(() => WriteOriginalCapture(destination, capture.PngBytes));
                    AddRecentSafely(destination);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { feedback += " Original capture could not be saved automatically: " + exception.Message; }
            }
            if (preferences.AutoCopyCaptures)
            {
                try { await DesktopImageActions.CopyPngAsync(capture.PngBytes); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { feedback += " Automatic copy could not finish: " + exception.Message; }
            }
            SetStatus((scrolling ? $"Captured {capture.FrameCount} scrolling frames. " : "Capture ready. ") + feedback + " " + ToolInstruction());
            return true;
        }, commitDraft: false);
    }

    private static void WriteOriginalCapture(string path, byte[] png)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(png); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void OnRecordClicked(object sender, RoutedEventArgs args) { if (!busy) OpenRecordingWindow(); }
    private void OnVideoReviewClicked(object sender, RoutedEventArgs args) { if (!busy) OpenRecordingWindow(); }
    private void OpenRecordingWindow(string? path = null)
    {
        if (closed || applicationExitPending) return;
        RecordingWindow window;
        try { window = new RecordingWindow(path); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { ReportUnexpectedFailure(exception); return; }
        window.ConfigureDefaults(preferences.DefaultMicrophone, preferences.DefaultSystemAudio, preferences.RecordingFolder);
        recordingWindows.Add(window);
        allRecordingWindows.Add(window);
        window.RecordingStateChanged += (_, active) =>
        {
            if (closed) return;
            if (active) AppWindow.Hide(); else if (!recordingWindows.Any(candidate => candidate.IsRecording)) { AppWindow.Show(); Activate(); }
        };
        window.RecordingSaved += (_, savedPath) => AddRecentSafely(savedPath);
        window.Closed += (_, _) =>
        {
            allRecordingWindows.Remove(window);
            recordingWindows.Remove(window);
            if (!closed && !recordingWindows.Any(candidate => candidate.IsRecording)) AppWindow.Show();
        };
        window.Activate();
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs args) => await CopyAsync();
    private async Task CopyAsync()
    {
        if (busy || !session.HasImage) return;
        string selectedText = Viewport.SelectedInlineText;
        if (selectedText.Length > 0) { SetClipboardText(selectedText); SetStatus("Selected text copied."); return; }
        await RunActionAsync("Copying…", async () =>
        {
            if (ocrSelection is not null && ocrSelection.Text.Length > 0 && ocrSnapshot is not null && session.IsOcrSnapshotCurrent(ocrSnapshot))
            { SetClipboardText(ocrSelection.Text); SetStatus("Selected recognized text copied in reading order."); }
            else { var image = await session.RenderPreviewAsync(ActionToken); await DesktopImageActions.CopyPngAsync(image.PngBytes); SetStatus("Image copied to the clipboard."); }
            return true;
        });
    }

    private async void OnShareClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Preparing Windows Share…", async () =>
    { var image = await session.RenderPreviewAsync(ActionToken); await DesktopImageActions.SharePngAsync(image.PngBytes, WindowHandle); SetStatus("Choose an app in Windows Share."); return true; });
    private async void OnPrintClicked(object sender, RoutedEventArgs args) => await PrintAsync();
    private Task PrintAsync() => RunActionAsync("Preparing print…", async () =>
    {
        if (!session.HasImage) return false;
        var image = await session.RenderPreviewAsync(ActionToken);
        bool printed = DesktopPrintingService.PrintImage(image.PngBytes, WindowHandle);
        SetStatus(printed ? "Image sent to the selected printer." : "Print canceled. Your image is kept."); return printed;
    });

    private void OnNewWindowClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed || applicationExitPending) return;
        var editor = new MainWindow(); editor.Activate();
    }

    private Task OpenCapturedImageAsync(byte[] png, string name) => RunActionAsync("Opening capture…", async () =>
    {
        session.OpenImageBytes(png); ResetDocumentSelection(); displayPath = null; suggestedName = name;
        await RefreshPreviewAsync(resetView: true); SetStatus("Capture ready. " + ToolInstruction()); return true;
    });

    private async void OnUpdatesClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Checking release information…", async () => { await DesktopDialogs.ShowUpdatesAsync(Root.XamlRoot); return true; }, commitDraft: false);
    private void OnFullScreenClicked(object sender, RoutedEventArgs args) => ToggleFullScreen();
    private void ToggleFullScreen()
    {
        if (busy || closed) return;
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);
    }

    private async void OnExternalEditorClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Choose an external image editor…", async () =>
    {
        if (!session.HasImage) return false;
        var picker = new FileOpenPicker(AppWindow.Id) { CommitButtonText = "Open image in editor" };
        picker.FileTypeFilter.Add(".exe");
        var chosen = await picker.PickSingleFileAsync(); if (chosen is null) return false;
        var image = await session.RenderPreviewAsync(ActionToken);
        DesktopExternalEditorResult result = await DesktopExternalEditorService.OpenAsync(image.PngBytes, chosen.Path, WindowHandle, ActionToken);
        externalSnapshotPath = result.SnapshotPath;
        ReloadExternalButton.IsEnabled = true;
        SetStatus("Opened an image copy in the signed editor. Retained working copy: " + result.SnapshotPath + ". Save its changes, then use Import external editor copy to reopen it."); return true;
    });

    private async void OnReloadExternalClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Import external editor copy", async () =>
    {
        if (externalSnapshotPath is null) return false;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Import the external editor copy?", Content = "Retained working copy:\n" + externalSnapshotPath + "\n\nThe imported file is a flattened image. Save your current Snip Studio document first if you want to retain its annotations. Make sure the external editor has saved its changes to the supplied copy.", PrimaryButtonText = "Import copy", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        return await OpenPathCoreAsync(externalSnapshotPath);
    }, commitDraft: false);

    private bool TryHideToTray()
    {
        if (explicitExit || desktopIntegration is null || !preferences.KeepRunningInTray) return false;
        try { return desktopIntegration.HideToTray(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { SetStatus("The notification-area icon could not be reached: " + exception.Message); return false; }
    }
    public bool TryStartInBackground() => TryHideToTray();

    private void DisposeDesktopIntegration()
    {
        translation.Dispose();
        DesktopImageActions.ReleaseWindow(WindowHandle);
        desktopIntegration?.Dispose(); desktopIntegration = null;
        editorWindows.Remove(this);
        if (desktopOwner == this)
        {
            desktopOwner = null;
            MainWindow? next = editorWindows.FirstOrDefault(editor => !editor.closed);
            next?.InitializeDesktopIntegration();
        }
    }

    private async Task RequestApplicationExitAsync()
    {
        if (applicationExitPending) return;
        MainWindow[] editors = editorWindows.Where(editor => !editor.closed).ToArray();
        if (editors.Any(editor => editor.busy || editor.closePending)) { desktopIntegration?.RestoreWindow(); SetStatus("Finish or cancel the current action before exiting Snip Studio."); return; }
        applicationExitPending = true;
        foreach (MainWindow editor in editors) editor.UpdateCommands();
        try
        {
            foreach (RecordingWindow recording in allRecordingWindows.Where(window => !window.IsClosed).ToArray())
                if (!await recording.RequestCloseAsync()) return;
            var reviewed = new List<(MainWindow Editor, long Revision, TextEditDraft? Draft)>();
            foreach (MainWindow editor in editors)
            {
                editor.AppWindow.Show(); editor.Activate();
                bool confirmed = await editor.RunActionAsync("Review changes before exiting…", editor.ConfirmReplaceAsync, commitDraft: false);
                if (!confirmed) return;
                reviewed.Add((editor, editor.session.Revision, editor.Viewport.CaptureTextDraft()));
            }
            if (reviewed.Any(item => item.Editor.busy || item.Editor.closePending || item.Editor.Viewport.CaptureTextDraft() != item.Draft || item.Editor.session.Revision != item.Revision))
            { SetStatus("An editor changed while exit was being reviewed. Your windows are kept; review them before exiting again."); return; }
            foreach (MainWindow editor in editors) { editor.explicitExit = true; editor.allowClose = true; editor.Close(); }
        }
        finally
        {
            applicationExitPending = false;
            foreach (MainWindow editor in editorWindows.ToArray()) if (!editor.closed) editor.UpdateCommands();
        }
    }
}
