using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
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
    private static bool installerExitQueued;
    private static int sessionCaptureDelaySeconds;
    private readonly DesktopPreferencesStore preferencesStore = new();
    private DesktopPreferences preferences = new();
    private DesktopIntegrationService? desktopIntegration;
    private MainWindow? shortcutListenerSuccessor;
    private readonly HashSet<RecordingWindow> recordingWindows = [];
    private RecordingWindow? recordingCaptureWindow;
    private bool selectingRecordingCapture, restoreEditorAfterCanceledExit;
    private bool explicitExit;
    private bool desktopVisualInitialized;
    private string? externalSnapshotPath;
    private CaptureKind selectedCaptureKind = CaptureKind.Rectangle;
    private CaptureKind selectedRecordingCaptureKind = CaptureKind.Rectangle;
    private bool recordingCaptureMode;
    private CaptureKind ActiveCaptureKind => recordingCaptureMode ? selectedRecordingCaptureKind : selectedCaptureKind;
    private int ScreenshotDelaySeconds => preferences.RememberCaptureDelay ? preferences.CaptureDelaySeconds : sessionCaptureDelaySeconds;
    private int ActiveCaptureDelaySeconds => recordingCaptureMode ? 0 : ScreenshotDelaySeconds;
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void InitializeDesktopIntegration()
    {
        editorWindows.Add(this);
        try
        {
            if (!desktopVisualInitialized)
            {
                preferences = preferencesStore.Load();
                selectedCaptureKind = preferences.LastScreenshotCaptureKind;
                ApplyVisualPreferences();
                desktopVisualInitialized = true;
            }
            if (desktopOwner is not null && desktopOwner != this) return;
            desktopOwner = this;
            if (desktopIntegration is not null) return;
            desktopIntegration = new DesktopIntegrationService(WindowHandle);
            desktopIntegration.CaptureRequested += async (_, _) => { if (!busy && !applicationExitPending) await CaptureAsync(CaptureKind.Rectangle, false); };
            desktopIntegration.RecordingRequested += async (_, _) => { if (!busy && !applicationExitPending) await BeginRecordingCaptureAsync(); };
            desktopIntegration.RestoreRequested += (_, _) =>
            {
                if (!selectingRecordingCapture && !TryActivateRecordingCapture()) { AppWindow.Show(); Activate(); }
            };
            desktopIntegration.ExitRequested += async (_, _) => await RequestApplicationExitAsync();
            desktopIntegration.IntegrationFailed += (_, args) => SetStatus("Windows integration could not finish: " + args.Exception.Message);
            AttachDesktopShellCommands(desktopIntegration);
            ApplyDesktopIntegrationPreferences(desktopIntegration, preferences);
            if ((preferencesStore.LoadWarning ?? desktopIntegration.GetPrintScreenSetupHint()) is string warning) SetStatus(warning);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            SetStatus("Optional desktop settings could not finish: " + exception.Message);
        }
        if (desktopOwner == this) _ = CheckForAutomaticUpdateAsync();
    }

    private static void ApplyDesktopIntegrationPreferences(DesktopIntegrationService integration, DesktopPreferences settings)
    {
        var failures = new List<Exception>();
        Apply(() => integration.SetHotkeysEnabled(settings.HotkeysEnabled));
        Apply(() => integration.SetPrintScreenEnabled(settings.UsePrintScreen));
        Apply(() => integration.SetTrayEnabled(settings.KeepRunningInTray ||
            settings.KeepShortcutsAfterClose && HasRegisteredShortcut(integration)));
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(" ", failures.Select(failure => failure.Message)), failures[0]);

        void Apply(Action operation)
        {
            try { operation(); }
            catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
            { failures.Add(failure); }
        }
    }

    private static bool HasRegisteredShortcut(DesktopIntegrationService integration) => integration.HotkeysEnabled || integration.PrintScreenEnabled;

    private async Task CheckForAutomaticUpdateAsync()
    {
        if (!preferences.AutoCheckForUpdates || desktopOwner != this) return;
        DesktopUpdateInfo? update;
        try
        {
            // Let the launcher render and give a user-initiated check priority.
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (closed || !preferences.AutoCheckForUpdates || desktopOwner != this) return;
            update = await Task.Run(() => DesktopUpdateService.CheckWhenDueAsync());
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException) { return; }
        if (closed || !preferences.AutoCheckForUpdates || update is not { NewerPublishedVersion: true } || PreviewNotice.IsOpen) return;
        PreviewNotice.Title = "Snip Studio " + update.PublishedTag + " is available";
        PreviewNotice.Message = "Choose Check for updates to see the Snip Studio release.";
        PreviewNotice.IsOpen = true;
    }

    private void ApplyVisualPreferences(bool initializeInk = true)
    {
        Root.RequestedTheme = preferences.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (initializeInk)
        {
            penColor = preferences.InkArgb;
            highlighterColor = preferences.HighlighterArgb;
            penWidth = highlighterWidth = shapeWidth = (float)preferences.StrokeWidth;
        }
        Viewport.RequireCtrlForWheelZoom = preferences.RequireCtrlForWheelZoom;
        ApplyWorkspacePreferences();
        UpdateCaptureControls();
        SetTool(selectedTool, whileBusy: true, revealOptions: !contextDismissed);
    }

    private async void OnRecentClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Recent files", async () =>
    {
        string? path = await DesktopDialogs.ShowRecentAsync(AppWindow.Id, () => preferencesStore.RecentPaths, preferences.Theme, ActionToken);
        if (path is null) return false;
        ActionToken.ThrowIfCancellationRequested();
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
        CancellationToken cancellationToken = ActionToken;
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (!await ConfirmReplaceAsync()) return false;
        bool project = IsEditableProjectPath(path);
        SetStatus(project ? "Opening editable snip…" : "Opening image…");
        ImageSaveTarget? saveTarget = null;
        if (project) await session.OpenEditableProjectAsync(path, cancellationToken);
        else saveTarget = await session.OpenWithSaveTargetAsync(path, cancellationToken);
        if (Viewport.IsTextEditing) Viewport.EndTextEdit();
        ResetDocumentSelection();
        displayPath = path;
        currentImageSaveTarget = saveTarget;
        suggestedName = Path.GetFileNameWithoutExtension(path) + "-edited";
        AddRecentSafely(path);
        await RefreshPreviewAsync(resetView: true);
        SetStatus("Opened " + Path.GetFileName(path) + ". " + ToolInstruction()); return true;
    }

    private static bool IsVideoPath(string path) => RecordingWindow.IsSupportedVideoPath(path);

    private void AddRecentSafely(string path)
    {
        try
        {
            preferencesStore.AddRecent(path);
            DesktopJumpList.AddRecent(path);
        }
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

    private async void OnCaptureClicked(object sender, RoutedEventArgs args) => await NewCaptureAsync();
    private Task NewCaptureAsync()
    {
        if (busy || closed || closePending || applicationExitPending) return Task.CompletedTask;
        return recordingCaptureMode ? BeginRecordingCaptureAsync()
            : CaptureAsync(selectedCaptureKind, scrolling: false, preserveOpenSnip: true);
    }

    private void OnScreenshotModeClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed || applicationExitPending) return;
        recordingCaptureMode = false;
        UpdateCaptureControls();
        SetStatus("Screenshot mode. Choose a capture shape, then select New.");
    }
    private void OnCaptureModeSelected(object sender, RoutedEventArgs args)
    {
        if (busy || closed) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out CaptureKind mode))
        {
            if (recordingCaptureMode)
            {
                if (mode is not CaptureKind.Rectangle and not CaptureKind.Window) return;
                selectedRecordingCaptureKind = mode;
            }
            else if (!RememberScreenshotCaptureKind(mode))
            {
                UpdateCaptureControls();
                return;
            }
            UpdateCaptureControls();
            SetStatus(CaptureModeLabel.Text + " capture ready. Select New to start.");
        }
    }
    private async void OnCaptureDelayClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out int seconds) || seconds < 0 || seconds > 10) return;
        if (recordingCaptureMode || busy || closed || applicationExitPending) return;
        string status = seconds == 0 ? "Capture delay off." : $"Capture starts after {seconds} seconds.";
        if (!preferences.RememberCaptureDelay)
        {
            sessionCaptureDelaySeconds = seconds;
            foreach (MainWindow editor in editorWindows) editor.UpdateCaptureControls();
            SetStatus(status);
            return;
        }
        await RunActionAsync("Saving capture delay…", () =>
        {
            var delayStore = new DesktopPreferencesStore();
            DesktopPreferences draft = delayStore.Load();
            draft.CaptureDelaySeconds = seconds;
            delayStore.Save(draft);
            preferences = delayStore.Load();
            foreach (MainWindow editor in editorWindows)
            {
                editor.preferences = preferences.Clone();
                editor.ApplyVisualPreferences(initializeInk: false);
            }
            SetStatus(status);
            return Task.FromResult(true);
        }, commitDraft: false);
        if (!closed) UpdateCaptureControls();
    }
    private void UpdateCaptureControls()
    {
        CaptureModeLabel.Text = ActiveCaptureKind switch { CaptureKind.AllMonitors => "All monitors", CaptureKind.Monitor => "Full screen", _ => ActiveCaptureKind.ToString() };
        foreach (FrameworkElement icon in new FrameworkElement[] { RectangleCaptureIcon, WindowCaptureIcon, FullScreenCaptureIcon, FreeformCaptureIcon, AllMonitorsCaptureIcon })
            icon.Visibility = Visibility.Collapsed;
        FrameworkElement selectedIcon = ActiveCaptureKind switch
        {
            CaptureKind.Window => WindowCaptureIcon,
            CaptureKind.Monitor => FullScreenCaptureIcon,
            CaptureKind.Freeform => FreeformCaptureIcon,
            CaptureKind.AllMonitors => AllMonitorsCaptureIcon,
            _ => RectangleCaptureIcon
        };
        selectedIcon.Visibility = Visibility.Visible;
        CaptureDelayLabel.Text = ActiveCaptureDelaySeconds == 0 ? "Off" : ActiveCaptureDelaySeconds + " s";
        DelayButton.IsEnabled = !recordingCaptureMode;
        ScreenshotModeButton.IsChecked = !recordingCaptureMode;
        RecordButton.IsChecked = recordingCaptureMode;
        ScreenshotModeUnderline.Visibility = recordingCaptureMode ? Visibility.Collapsed : Visibility.Visible;
        RecordingModeUnderline.Visibility = recordingCaptureMode ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CaptureButton, recordingCaptureMode ? "New recording" : "New snip");
        SetWorkspaceToolTip(CaptureButton, recordingCaptureMode ? "New recording (Ctrl+N)" : "New snip (Ctrl+N)");
        string shapeDescription = (recordingCaptureMode ? "Recording area: " : "Capture shape: ") + CaptureModeLabel.Text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CaptureOptionsButton, shapeDescription);
        SetWorkspaceToolTip(CaptureOptionsButton, shapeDescription + ". Select New to start.");
        SetWorkspaceToolTip(DelayButton, recordingCaptureMode ? "Capture delay is unavailable in video mode" : "Screenshot capture delay");
        LauncherHint.Text = recordingCaptureMode ? "Press Ctrl+N to select a recording area." : "Press Ctrl+N to take a screenshot.";
        SetCaptureMenuChecks(CaptureModeMenu.Items);
        SetCaptureMenuChecks(CaptureDelayMenu.Items);
        if (CaptureMoreButton.Flyout is MenuFlyout more) SetCaptureMenuChecks(more.Items);
    }

    private void SetCaptureMenuChecks(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (MenuFlyoutItemBase item in items)
        {
            if (item.Tag is string itemTag)
                item.Visibility = recordingCaptureMode && itemTag is ("Freeform" or "Monitor" or "AllMonitors" or "ScreenshotOnly")
                    ? Visibility.Collapsed : Visibility.Visible;
            if (item is MenuFlyoutSubItem submenu)
            {
                if (submenu.Tag is "ScreenshotDelay") submenu.IsEnabled = !recordingCaptureMode;
                SetCaptureMenuChecks(submenu.Items);
            }
            else if (item is ToggleMenuFlyoutItem { Tag: string tag } choice)
            {
                if (int.TryParse(tag, out _)) choice.IsEnabled = !recordingCaptureMode;
                choice.IsChecked = tag switch
                {
                    "ScreenshotMode" => !recordingCaptureMode,
                    "RecordingMode" => recordingCaptureMode,
                    _ => int.TryParse(tag, out int seconds) ? seconds == ActiveCaptureDelaySeconds : tag == ActiveCaptureKind.ToString()
                };
            }
        }
    }
    private async void OnScrollingClicked(object sender, RoutedEventArgs args)
    {
        if (!recordingCaptureMode) await CaptureAsync(CaptureKind.Rectangle, true);
    }

    private async void OnCaptureOpenSnipsClicked(object sender, RoutedEventArgs args) =>
        await CaptureAsync(selectedCaptureKind, scrolling: false, includeOpenSnips: true);

    private async Task CaptureAsync(CaptureKind kind, bool scrolling, bool includeOpenSnips = false,
        int? overrideDelaySeconds = null, bool preserveOpenSnip = false)
    {
        await RunActionAsync(scrolling ? "Choose the scrolling area…" : "Preparing capture…", async () =>
        {
            if (editorWindows.Any(editor => editor != this && !editor.closed && (editor.busy || editor.closePending)))
            {
                SetStatus("Finish or cancel the open action in the other Snip Studio window before capturing.");
                return false;
            }
            bool newWindow = session.HasImage && (preserveOpenSnip || preferences.CaptureInNewWindow);
            if (!newWindow && !await ConfirmReplaceAsync()) return false;
            List<nint> hiddenWindows = CaptureTransientWindowHandles();
            if (!includeOpenSnips)
                foreach (MainWindow editor in editorWindows)
                    if (editor != this && !editor.closed) hiddenWindows.Add(editor.WindowHandle);
            var request = new CaptureRequest
            {
                OwnerWindow = WindowHandle,
                KeepOwnerVisible = includeOpenSnips,
                AdditionalWindowsToHide = hiddenWindows,
                Kind = kind,
                DelaySeconds = overrideDelaySeconds ?? ScreenshotDelaySeconds,
                UseHdrColorCorrection = preferences.UseHdrColorCorrection,
                HideTaskbars = preferences.HideTaskbars,
                KeepPrimaryTaskbarVisible = preferences.KeepPrimaryTaskbarVisible,
                ScreenshotBorderArgb = preferences.AddScreenshotBorder ? preferences.ScreenshotBorderArgb : null,
                ScreenshotBorderWidth = preferences.ScreenshotBorderWidth
            };
            CapturedImage? capture;
            try
            {
                if (scrolling)
                    capture = await CaptureCoordinator.CaptureScrollingAsync(request,
                        new Progress<CaptureProgress>(progress => SetStatus(progress.Status)),
                        cancellationToken: ActionToken, previewAppearance: Root.ActualTheme == ElementTheme.Dark
                            ? ScrollingReviewAppearance.Dark : ScrollingReviewAppearance.Light);
                else capture = await CaptureCoordinator.CaptureAsync(request, ActionToken);
            }
            finally { if (!closed) { AppWindow.Show(); Activate(); } }
            if (capture is null) { SetStatus("Capture canceled. Your previous snip is kept."); return false; }
            string captureName = "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
            MainWindow capturedEditor = this;
            EditorSession.CaptureBaseline baseline;
            long clipboardIntent = preferences.AutoCopyCaptures ? DesktopImageActions.BeginClipboardWrite() : 0;
            if (newWindow)
            {
                capturedEditor = new MainWindow();
                capturedEditor.Activate();
                if (await capturedEditor.OpenCapturedImageAsync(capture.PngBytes, captureName) is not { } opened) return false;
                baseline = opened;
            }
            else
            {
                baseline = OpenCaptureDocument(capture.PngBytes, captureName);
                await RefreshPreviewAsync(resetView: true);
            }
            string feedback = capture.Warning ?? "";
            if (clipboardIntent != 0 && !capturedEditor.closed)
            {
                try
                {
                    if (await DesktopImageActions.CopyPngAsync(capture.PngBytes, clipboardIntent, () =>
                        !capturedEditor.closed && capturedEditor.preferences.AutoCopyCaptures && capturedEditor.session.Revision == baseline.Revision))
                        feedback += " Copied to the clipboard.";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
                {
                    capturedEditor.ShowAutomaticCopyWarning(exception);
                    feedback += " Automatic copy could not finish. Use Copy image to retry.";
                }
            }
            if (preferences.AutoSaveOriginalCaptures)
            {
                try
                {
                    string folder = preferences.ScreenshotFolder;
                    string destination = Path.Combine(folder, captureName + "-" + Guid.NewGuid().ToString("N")[..6] + ".png");
                    await Task.Run(() => WriteOriginalCapture(destination, capture.PngBytes));
                    AddRecentSafely(destination);
                    if (!capturedEditor.closed && capturedEditor.displayPath is null &&
                        capturedEditor.session.TryRecordSavedCapture(baseline))
                    {
                        capturedEditor.displayPath = destination;
                        capturedEditor.UpdateCommands();
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { feedback += " Original capture could not be saved automatically: " + exception.Message; }
            }
            if (newWindow) SetStatus("New capture opened in a separate window. Your current snip is kept.");
            if (!capturedEditor.closed)
                capturedEditor.SetStatus((scrolling ? $"Captured {capture.FrameCount} scrolling frames. " : "Capture ready. ") +
                    feedback + " " + capturedEditor.ToolInstruction());
            return true;
        }, commitDraft: false);
    }

    private static List<nint> CaptureTransientWindowHandles()
    {
        List<nint> windows = OpenOcrToolWindowHandles().ToList();
        if (settingsWindow is { } settings) windows.Add(settings.WindowHandle);
        return windows;
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

    private void OnRecordClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed || applicationExitPending) return;
        recordingCaptureMode = true;
        UpdateCaptureControls();
        SetStatus("Video mode. Select New to choose an area, then Start recording.");
    }

    private async Task BeginRecordingCaptureAsync()
    {
        if (busy || closed || applicationExitPending) return;
        if (TryActivateRecordingCapture()) return;
        recordingCaptureMode = true;
        UpdateCaptureControls();
        CaptureRectangle? selectedRegion = null;
        bool editorWasVisible = AppWindow.IsVisible;
        bool handedOff = false;
        selectingRecordingCapture = true;
        try
        {
            bool selectedArea = await RunActionAsync("Choose the recording area. Escape cancels.", async () =>
            {
                // Keep the editor out of the handoff to the recorder. The coordinator
                // preserves the visibility of the capture owner it receives.
                if (editorWasVisible) AppWindow.Hide();
                selectedRegion = await CaptureCoordinator.SelectRecordingRegionAsync(new CaptureRequest
                {
                    OwnerWindow = WindowHandle, Kind = selectedRecordingCaptureKind,
                    AdditionalWindowsToHide = CaptureTransientWindowHandles(),
                    DelaySeconds = 0, HideTaskbars = preferences.HideTaskbars,
                    KeepPrimaryTaskbarVisible = preferences.KeepPrimaryTaskbarVisible
                }, ActionToken);
                ActionToken.ThrowIfCancellationRequested();
                if (selectedRegion.HasValue) return true;
                SetStatus("Recording canceled. Your previous snip is kept.");
                return false;
            }, commitDraft: false);
            if (selectedArea && selectedRegion is { } selected && !closed && !applicationExitPending)
                handedOff = OpenRecordingWindow(initialRegion: new(selected.X, selected.Y, selected.Width, selected.Height),
                    captureWorkflow: true, restoreEditorOnClose: editorWasVisible);
        }
        finally
        {
            selectingRecordingCapture = false;
            if (!handedOff && editorWasVisible) RestoreEditorAfterRecording();
        }
    }

    private bool TryActivateRecordingCapture()
    {
        if (recordingCaptureWindow is not { IsClosed: false } window) return false;
        window.ActivateRecordingCapture();
        return true;
    }

    private void RestoreEditorAfterRecording()
    {
        if (closed) return;
        if (applicationExitPending)
        {
            restoreEditorAfterCanceledExit = true;
            return;
        }
        AppWindow.Show();
        Activate();
        Viewport.RestoreTextFocus();
    }

    private void OnVideoReviewClicked(object sender, RoutedEventArgs args) { if (!busy) OpenRecordingWindow(); }
    private bool OpenRecordingWindow(string? path = null, RecordingRegion? initialRegion = null, bool captureWorkflow = false,
        bool restoreEditorOnClose = false)
    {
        if (closed || applicationExitPending) return false;
        RecordingWindow window;
        try { window = new RecordingWindow(path, initialRegion, captureWorkflow); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { ReportUnexpectedFailure(exception); return false; }
        window.ConfigureDefaults(preferences.DefaultMicrophone, preferences.DefaultSystemAudio, preferences.RecordingFolder,
            preferences.DefaultMicrophoneDeviceId, preferences.DefaultSystemAudioDeviceId, preferences.AutoCopyRecordingChanges,
            preferences.AutoSaveOriginalRecordings, preferences.AskSaveEditedRecordings,
            hideTaskbars: preferences.HideTaskbars, keepPrimaryTaskbarVisible: preferences.KeepPrimaryTaskbarVisible);
        recordingWindows.Add(window);
        allRecordingWindows.Add(window);
        if (captureWorkflow) recordingCaptureWindow = window;
        else window.RecordingStateChanged += (_, active) =>
        {
            if (closed || applicationExitPending || recordingCaptureWindow is { IsClosed: false }) return;
            if (active) AppWindow.Hide(); else if (!recordingWindows.Any(candidate => candidate.IsRecording)) { AppWindow.Show(); Activate(); }
        };
        window.RecordingSaved += (_, savedPath) => AddRecentSafely(savedPath);
        window.Closed += (_, _) =>
        {
            allRecordingWindows.Remove(window);
            recordingWindows.Remove(window);
            if (recordingCaptureWindow == window)
            {
                recordingCaptureWindow = null;
                if (restoreEditorOnClose) RestoreEditorAfterRecording();
            }
            else if (!closed && !applicationExitPending && recordingCaptureWindow is null &&
                !recordingWindows.Any(candidate => candidate.IsRecording)) AppWindow.Show();
        };
        window.Activate();
        return true;
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs args) => await CopyAsync(imageOnly: true);

    private async Task CopyEditedPreviewAsync(PreviewSnapshot image, long clipboardIntent)
    {
        try
        {
            await DesktopImageActions.CopyPngAsync(image.PngBytes, clipboardIntent, () =>
                !closed && preferences.AutoCopyCaptures && session.Revision == image.Revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ShowAutomaticCopyWarning(exception);
        }
    }

    private void ShowAutomaticCopyWarning(Exception exception)
    {
        if (closed) return;
        PreviewNotice.Title = "Automatic copy could not finish";
        PreviewNotice.Message = "Your image is kept. Use Copy image to retry. " + exception.Message;
        PreviewNotice.IsOpen = true;
    }

    private Task CopyAsync() => CopyAsync(imageOnly: false);

    private async Task CopyAsync(bool imageOnly)
    {
        if (closed || busy || !session.HasImage) return;
        long intent = DesktopImageActions.BeginClipboardWrite();
        await RunActionAsync("Copying…", async () =>
        {
            if (!imageOnly)
            {
                string selectedText = Viewport.SelectedInlineText;
                if (selectedText.Length == 0 && CurrentRecognition() is not null)
                    selectedText = ocrSelection?.Text ?? "";
                if (selectedText.Length > 0)
                {
                    bool copiedText = SetClipboardText(selectedText, intent);
                    SetStatus(copiedText ? "Selected text copied." : "Copy skipped because a newer clipboard action is active.");
                    return copiedText;
                }
            }
            if (Viewport.IsTextEditing)
            {
                CommitTextDraft();
                await RefreshPreviewAsync(resetView: false, allowAutoCopy: false);
            }
            CancellationToken cancellationToken = ActionToken;
            var image = await session.RenderPreviewAsync(cancellationToken);
            bool copied = await DesktopImageActions.CopyPngAsync(image.PngBytes, intent, () =>
                !cancellationToken.IsCancellationRequested && !closed && session.Revision == image.Revision);
            if (!copied) cancellationToken.ThrowIfCancellationRequested();
            if (!closed) SetStatus(copied ? "Image copied to the clipboard." : "Copy skipped because a newer clipboard action is active.");
            return copied;
        }, commitDraft: false);
    }

    private async void OnShareClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Preparing Windows Share…", async () =>
    { var image = await session.RenderPreviewAsync(ActionToken); await DesktopImageActions.SharePngAsync(image.PngBytes, WindowHandle); SetStatus("Choose an app in Windows Share."); return true; });
    private async void OnPrintClicked(object sender, RoutedEventArgs args) => await PrintAsync();
    private Task PrintAsync() => RunActionAsync("Preparing print…", async () =>
    {
        if (!session.HasImage) return false;
        var image = await session.RenderPreviewAsync(ActionToken);
        bool printed = await WindowsPrintSession.PrintAsync(Root, WindowHandle, image.PngBytes, ActionToken);
        SetStatus(printed ? "Image sent to the selected printer." : "Print canceled. Your image is kept."); return printed;
    });

    private void OnNewWindowClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed || applicationExitPending) return;
        var editor = new MainWindow(); editor.Activate();
    }

    private EditorSession.CaptureBaseline OpenCaptureDocument(byte[] png, string name)
    {
        session.OpenImageBytes(png);
        ResetDocumentSelection();
        if (Viewport.IsTextEditing) Viewport.EndTextEdit();
        displayPath = null;
        currentImageSaveTarget = null;
        suggestedName = name;
        return session.GetCaptureBaseline();
    }

    private async Task<EditorSession.CaptureBaseline?> OpenCapturedImageAsync(byte[] png, string name)
    {
        EditorSession.CaptureBaseline baseline = default;
        bool opened = await RunActionAsync("Opening capture…", async () =>
        {
            baseline = OpenCaptureDocument(png, name);
            await RefreshPreviewAsync(resetView: true);
            SetStatus("Capture ready. " + ToolInstruction());
            return true;
        });
        return opened ? baseline : null;
    }

    private async void OnUpdatesClicked(object sender, RoutedEventArgs args) => await OpenUpdatesAsync();
    private void OnFullScreenClicked(object sender, RoutedEventArgs args) => ToggleFullScreen();
    private void ToggleFullScreen()
    {
        if (busy || closed) return;
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);
    }

    private async void OnExternalEditorClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Opening the external image editor…", async () =>
    {
        if (!session.HasImage) return false;
        string editorPath = preferences.EditorApplicationPath;
        if (string.IsNullOrWhiteSpace(editorPath))
        {
            var picker = new FileOpenPicker(AppWindow.Id) { CommitButtonText = "Open image in editor" };
            picker.FileTypeFilter.Add(".exe");
            var chosen = await picker.PickSingleFileAsync();
            if (chosen is null) return false;
            editorPath = chosen.Path;
        }
        var image = await session.RenderPreviewAsync(ActionToken);
        DesktopExternalEditorResult result = await DesktopExternalEditorService.OpenAsync(image.PngBytes, editorPath, WindowHandle, ActionToken);
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
    public bool TryStartInBackground() => TryHideToTray() || TryEnterShortcutListener();

    private bool TryEnterShortcutListener()
    {
        if (desktopIntegration is not { } integration || !preferences.KeepShortcutsAfterClose || !HasRegisteredShortcut(integration)) return false;
        try { return integration.HideToTray(); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        { SetStatus("The shortcut listener could not use the notification area: " + failure.Message); return false; }
    }

    private bool PrepareShortcutListenerForClose()
    {
        if (explicitExit || desktopIntegration is not { } integration || !preferences.KeepShortcutsAfterClose ||
            !HasRegisteredShortcut(integration) || editorWindows.Any(editor => editor != this && !editor.closed)) return true;
        // Create an empty launcher before closing the last editor so the same
        // process can keep listening without keeping the document in memory.
        try { shortcutListenerSuccessor = new MainWindow(); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            SetStatus("The shortcut listener could not start: " + failure.Message);
            return false;
        }
        return true;
    }

    private void DisposeDesktopIntegration()
    {
        translation.Dispose();
        DesktopImageActions.ReleaseWindow(WindowHandle);
        desktopIntegration?.Dispose(); desktopIntegration = null;
        editorWindows.Remove(this);
        if (desktopOwner == this)
        {
            desktopOwner = null;
            MainWindow? next = shortcutListenerSuccessor ?? editorWindows.FirstOrDefault(editor => !editor.closed);
            next?.InitializeDesktopIntegration();
            if (shortcutListenerSuccessor is { } listener && !listener.TryEnterShortcutListener())
                listener.Activate();
            shortcutListenerSuccessor = null;
        }
    }

    internal static bool QueueInstallerExit(DispatcherQueue dispatcher, bool silent)
    {
        if (applicationExitPending || installerExitQueued) return true;
        if (silent && !CanExitWithoutPrompt()) return false;
        installerExitQueued = dispatcher.TryEnqueue(async () =>
        {
            MainWindow? editor = editorWindows.FirstOrDefault(window => !window.closed);
            try { await RequestApplicationExitAsync(editor, silent); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                if (editor is { closed: false }) editor.ReportUnexpectedFailure(exception);
            }
            finally { installerExitQueued = false; }
        });
        return installerExitQueued;
    }

    private static bool CanExitWithoutPrompt() =>
        settingsWindow?.CanCloseWithoutPrompt != false &&
        editorWindows.All(editor => editor.closed || (!editor.busy && !editor.closePending &&
            !editor.session.IsDirty && !editor.Viewport.IsTextEditing && !editor.HasPendingTextToolWork)) &&
        allRecordingWindows.All(recording => recording.IsClosed || recording.CanCloseWithoutPrompt);

    private Task RequestApplicationExitAsync() => RequestApplicationExitAsync(this, silent: false);

    private static async Task RequestApplicationExitAsync(MainWindow? initiator, bool silent)
    {
        if (applicationExitPending || silent && !CanExitWithoutPrompt()) return;
        if (settingsWindow is { CanCloseWithoutPrompt: false } pendingSettings)
        {
            pendingSettings.ShowPendingExit();
            initiator?.SetStatus("Save or cancel Settings before exiting Snip Studio.");
            return;
        }
        MainWindow[] editors = editorWindows.Where(editor => !editor.closed).ToArray();
        if (HasBlockingTextReview()) return;
        if (editors.Any(editor => editor.busy || editor.closePending))
        {
            initiator?.desktopIntegration?.RestoreWindow();
            initiator?.SetStatus("Finish or cancel the current action before exiting Snip Studio.");
            return;
        }
        applicationExitPending = true;
        foreach (MainWindow editor in editors) editor.UpdateCommands();
        try
        {
            foreach (RecordingWindow recording in allRecordingWindows.Where(window => !window.IsClosed).ToArray())
                if (!await recording.RequestCloseAsync(silent)) return;
            var reviewed = new List<(MainWindow Editor, long Revision, TextEditDraft? Draft)>();
            foreach (MainWindow editor in editors)
            {
                if (editor.closed) continue;
                if (editor.session.IsDirty || editor.Viewport.IsTextEditing)
                {
                    if (silent) return;
                    bool confirmed = await editor.RunActionAsync("Review changes before exiting…", editor.ConfirmApplicationExitAsync, commitDraft: false);
                    if (!confirmed) return;
                }
                reviewed.Add((editor, editor.session.Revision, editor.Viewport.CaptureTextDraft()));
            }
            if (reviewed.Any(item => !item.Editor.closed && (item.Editor.busy || item.Editor.closePending ||
                item.Editor.Viewport.CaptureTextDraft() != item.Draft || item.Editor.session.Revision != item.Revision)))
            { initiator?.SetStatus("An editor changed while exit was being reviewed. Your windows are kept; review them before exiting again."); return; }
            if (HasBlockingTextReview()) return;
            if (settingsWindow is { CanCloseWithoutPrompt: false } changedSettings)
            {
                if (!silent)
                {
                    changedSettings.ShowPendingExit();
                    initiator?.SetStatus("Save or cancel Settings before exiting Snip Studio.");
                }
                return;
            }
            settingsWindow?.Close();
            foreach (MainWindow editor in editors.Where(editor => !editor.closed))
            { editor.explicitExit = true; editor.allowClose = true; editor.Close(); }
        }
        finally
        {
            applicationExitPending = false;
            foreach (MainWindow editor in editorWindows.ToArray())
            {
                if (editor.closed) continue;
                if (editor.restoreEditorAfterCanceledExit)
                {
                    editor.restoreEditorAfterCanceledExit = false;
                    if (!editor.AppWindow.IsVisible) editor.RestoreEditorAfterRecording();
                }
                editor.UpdateCommands();
            }
        }

        bool HasBlockingTextReview()
        {
            MainWindow? editor = editors.FirstOrDefault(window => !window.closed && window.HasPendingTextToolWork);
            if (editor is null) return false;
            if (!silent) editor.ShowPendingTextToolWork();
            return true;
        }
    }
}
