using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

/// <summary>Native WinUI dialogs. No settings or OS integration changes occur on Cancel.</summary>
public static class DesktopDialogs
{
    public static async Task<DesktopPreferences?> ShowSettingsAsync(XamlRoot root, DesktopPreferences preferences, DesktopIntegrationService integration)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(integration);
        DesktopPreferences draft = preferences.Clone();
        var theme = new ComboBox { Header = "Appearance", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string item in new[] { "System", "Light", "Dark" }) theme.Items.Add(item);
        theme.SelectedItem = draft.Theme;
        var ctrlWheel = Toggle("Require Ctrl when using the wheel to zoom", draft.RequireCtrlForWheelZoom);
        var resizeToSnip = Toggle("Resize the window to fit a new snip", draft.ResizeToSnip);
        var delay = new NumberBox { Header = "Capture delay (seconds)", Minimum = 0, Maximum = 10, Value = draft.CaptureDelaySeconds, SmallChange = 1, LargeChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var captureInNewWindow = Toggle("Open captures in a new editor window", draft.CaptureInNewWindow);
        var hdr = Toggle("Use HDR color correction for captures", draft.UseHdrColorCorrection);
        var hideTaskbars = Toggle("Hide taskbars during capture", draft.HideTaskbars);
        var keepPrimaryTaskbar = Toggle("Keep the primary taskbar visible", draft.KeepPrimaryTaskbarVisible);
        keepPrimaryTaskbar.IsEnabled = hideTaskbars.IsOn;
        hideTaskbars.Toggled += (_, _) => keepPrimaryTaskbar.IsEnabled = hideTaskbars.IsOn;
        var copyCaptures = Toggle("Copy captures and edits to the clipboard", draft.AutoCopyCaptures);
        var saveOriginals = Toggle("Save original captures before editing", draft.AutoSaveOriginalCaptures);
        var microphone = Toggle("Use microphone for new recordings", draft.DefaultMicrophone);
        var systemAudio = Toggle("Use system audio for new recordings", draft.DefaultSystemAudio);
        var shortcuts = Toggle("Enable global capture and recording shortcuts", draft.HotkeysEnabled);
        var tray = Toggle("Keep running in the notification area when closing", draft.KeepRunningInTray);
        var startup = Toggle("Start this WinUI build when signing in to Windows", draft.StartupWithWindows);
        var screenshotFolder = new TextBox { Header = "Screenshot folder", Text = draft.ScreenshotFolder };
        var recordingFolder = new TextBox { Header = "Recording folder", Text = draft.RecordingFolder };
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error, IsClosable = false };
        var content = new StackPanel { Spacing = 14, MinWidth = 0, MaxWidth = 560 };
        content.Children.Add(new TextBlock { Text = "Settings apply to this WinUI build. Your stable Snip Studio settings stay separate.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(theme);
        content.Children.Add(ctrlWheel);
        content.Children.Add(resizeToSnip);
        content.Children.Add(new TextBlock { Text = "Fits new images to the current display. Manually resizing the editor keeps your chosen window size.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(delay);
        content.Children.Add(captureInNewWindow);
        content.Children.Add(hdr);
        content.Children.Add(new TextBlock { Text = "HDR correction requires compatible Windows capture support and display drivers. Leave it off for the standard capture path.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(hideTaskbars);
        content.Children.Add(keepPrimaryTaskbar);
        content.Children.Add(copyCaptures);
        content.Children.Add(saveOriginals);
        content.Children.Add(new TextBlock { Text = "Saving originals writes each new capture to the screenshot folder before annotations. Later edits need their own Save action.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(microphone);
        content.Children.Add(systemAudio);
        content.Children.Add(shortcuts);
        content.Children.Add(new TextBlock { Text = DesktopIntegrationService.CaptureShortcutLabel + " captures. " + DesktopIntegrationService.RecordingShortcutLabel + " opens recording. Windows may reserve a shortcut or another app may already own it.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(tray);
        content.Children.Add(startup);
        try
        {
            bool actual = integration.GetStartupEnabled();
            content.Children.Add(new TextBlock { Text = actual ? "Windows startup entry: enabled for this executable." : "Windows startup entry: not enabled for this executable.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            content.Children.Add(new TextBlock { Text = "Startup entry could not be read: " + failure.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        content.Children.Add(screenshotFolder);
        content.Children.Add(recordingFolder);
        content.Children.Add(new TextBlock { Text = "Folders are used by capture and recording workflows. Changing a folder does not move existing files. Windows startup and shortcuts change only when you save this dialog.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(error);
        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Snip Studio settings",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = scroller
        };
        void UpdateSettingsBounds()
        {
            var size = root.Size;
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0) return;
            // Reserve room for the title, action buttons, and dialog margins.
            // A fixed minimum viewport can force those actions off a small host.
            dialog.MaxHeight = Math.Max(1, size.Height - 32);
            scroller.MaxHeight = Math.Max(1, Math.Min(580, size.Height - 220));
            content.MaxWidth = Math.Max(1, Math.Min(560, size.Width - 80));
        }
        void OnSettingsRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateSettingsBounds();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (!double.IsFinite(delay.Value) || delay.Value != Math.Truncate(delay.Value)) throw new ArgumentException("Capture delay must be a whole number from 0 to 10.");
                draft.Theme = theme.SelectedItem as string ?? "System";
                draft.RequireCtrlForWheelZoom = ctrlWheel.IsOn;
                draft.ResizeToSnip = resizeToSnip.IsOn;
                draft.CaptureDelaySeconds = checked((int)delay.Value);
                draft.CaptureInNewWindow = captureInNewWindow.IsOn;
                draft.UseHdrColorCorrection = hdr.IsOn;
                draft.HideTaskbars = hideTaskbars.IsOn;
                draft.KeepPrimaryTaskbarVisible = keepPrimaryTaskbar.IsOn;
                draft.AutoCopyCaptures = copyCaptures.IsOn;
                draft.AutoSaveOriginalCaptures = saveOriginals.IsOn;
                draft.DefaultMicrophone = microphone.IsOn;
                draft.DefaultSystemAudio = systemAudio.IsOn;
                draft.HotkeysEnabled = shortcuts.IsOn;
                draft.KeepRunningInTray = tray.IsOn;
                draft.StartupWithWindows = startup.IsOn;
                draft.ScreenshotFolder = ValidateFolder(screenshotFolder.Text);
                draft.RecordingFolder = ValidateFolder(recordingFolder.Text);
                draft.Normalize();
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException or OverflowException)
            {
                args.Cancel = true;
                error.Message = failure.Message;
                error.IsOpen = true;
            }
        };
        root.Changed += OnSettingsRootChanged;
        try
        {
            UpdateSettingsBounds();
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? draft : null;
        }
        finally { root.Changed -= OnSettingsRootChanged; }
    }

    public static async Task<string?> ShowRecentAsync(XamlRoot root, IReadOnlyList<string> recentPaths)
    {
        ArgumentNullException.ThrowIfNull(recentPaths);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
        var paths = recentPaths.Take(32).ToArray();
        foreach (string path in paths)
        {
            var row = new StackPanel { Spacing = 3, Margin = new Thickness(0, 5, 0, 5) };
            row.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 });
            list.Items.Add(row);
        }
        var panel = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = paths.Length == 0 ? "No recent files yet. Files you open or save in this WinUI build will appear here." : "Choose a file to open. Recent history is stored only on this PC; unavailable files are kept in the list so removable drives can reconnect.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Recent snips and recordings",
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
        var answer = await dialog.ShowAsync();
        return answer == ContentDialogResult.Primary && list.SelectedIndex >= 0 ? paths[list.SelectedIndex] : null;
    }

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        string version = typeof(DesktopDialogs).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Snip Studio {version} — WinUI 3\nAutomation Forge LLC\n\nCapture screenshots, annotate and extract text, record your screen, and save images or recordings. Capture and OCR use Windows services. No screenshot or extracted text is uploaded by these local workflows.\n\nCtrl+O: Open\nCtrl+V: Paste image\nCtrl+S: Save as\nCtrl+Z / Ctrl+Y: Undo / Redo\nCtrl+F: Fit\nCtrl+0: 100%\nCtrl+wheel: Zoom around the pointer\nEscape: Cancel the current gesture\n\nSettings has optional global shortcuts, Windows startup and notification-area behavior. These are separate from the stable app. Save files before exiting; image exports are flattened, so keep the editor open to preserve editable annotations and Undo history.\n\nText recognition can make mistakes. Check identifiers, quantities, duplicate rows and selected text against the source image. Text is never silently spell-corrected.\n\nUpdate review checks this migration repository’s published release information and displays its review address. It does not install or execute downloaded software. Signed installers and portable releases must be approved before installation.\n\nThird-party licenses and attribution are included in the release’s licenses folder."
        };
        await new ContentDialog { XamlRoot = root, Title = "About Snip Studio", CloseButtonText = "Close", Content = new ScrollViewer { Content = text, MaxHeight = Math.Max(180, Math.Min(520, root.Size.Height - 220)) } }.ShowAsync();
    }

    /// <summary>Manual review only; queries metadata and never launches a downloaded program.</summary>
    public static async Task ShowUpdatesAsync(XamlRoot root)
    {
        DesktopUpdateInfo info = await DesktopUpdateService.CheckAsync();
        var panel = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = info.Message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Release review address (copy to your browser):", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = info.ReleaseUri.AbsoluteUri, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false });
        panel.Children.Add(new TextBlock { Text = "This check does not download, install or execute updates. Verify the timestamped Automation Forge signatures on an approved installer or portable build before running it.", TextWrapping = TextWrapping.Wrap });
        await new ContentDialog { XamlRoot = root, Title = "WinUI build update review", CloseButtonText = "Close", Content = panel }.ShowAsync();
    }

    private static ToggleSwitch Toggle(string label, bool value)
    {
        var toggle = new ToggleSwitch { Header = label, IsOn = value, OnContent = "On", OffContent = "Off" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    private static string ValidateFolder(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) || candidate.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0)
            throw new ArgumentException("Choose an absolute screenshot and recording folder path.");
        return Path.GetFullPath(candidate);
    }
}
