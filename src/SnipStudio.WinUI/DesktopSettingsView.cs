using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

internal sealed class DesktopSettingsView
{
    private static readonly string[] Categories = ["Screenshots", "Recording", "Workspace", "Shortcuts", "Updates"];
    private readonly WindowId owner;
    private readonly XamlRoot root;
    private readonly CancellationToken cancellationToken;
    private readonly Action<DesktopPreferences> save;
    private readonly string originalEditorPath;
    private DesktopPreferences draft;
    private readonly ContentDialog dialog;
    private readonly Grid layout = new() { RowSpacing = 12 };
    private readonly Grid body = new();
    private readonly ColumnDefinition navigationColumn = new();
    private readonly ListView navigation = new() { ItemsSource = Categories, SelectionMode = ListViewSelectionMode.Single };
    private readonly ComboBox compactNavigation = new() { ItemsSource = Categories, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer scroller = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly InfoBar notice = new() { IsClosable = true };
    private StackPanel[] pages = [];
    private NumberBox captureDelay = null!;
    private NumberBox borderWidth = null!;
    private bool busy;
    private bool closed;

    internal DesktopSettingsView(XamlRoot root, WindowId owner, DesktopPreferences preferences,
        Action<DesktopPreferences> save, CancellationToken cancellationToken)
    {
        this.root = root;
        this.owner = owner;
        this.save = save;
        this.cancellationToken = cancellationToken;
        originalEditorPath = preferences.EditorApplicationPath;
        draft = preferences.Clone();

        body.ColumnDefinitions.Add(navigationColumn);
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.ColumnSpacing = 20;
        body.Children.Add(compactNavigation);
        Grid.SetColumnSpan(compactNavigation, 2);
        Grid.SetRow(navigation, 1);
        body.Children.Add(navigation);
        Grid.SetRow(scroller, 1);
        Grid.SetColumn(scroller, 1);
        body.Children.Add(scroller);
        AutomationProperties.SetName(navigation, "Settings category");
        AutomationProperties.SetName(compactNavigation, "Settings category");
        var import = new Button { Content = "Import previous settings" };
        import.Click += async (_, _) => await RunPickerActionAsync(ImportAsync);
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(notice, 1);
        Grid.SetRow(import, 2);
        layout.Children.Add(body);
        layout.Children.Add(notice);
        layout.Children.Add(import);
        dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Snip Studio settings",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            Content = layout
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900d;
        dialog.Resources["ContentDialogPadding"] = new Thickness(24, 14, 24, 14);
        dialog.PrimaryButtonClick += SaveDraft;
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        navigation.SelectionChanged += (_, _) => SelectPage(navigation.SelectedIndex);
        compactNavigation.SelectionChanged += (_, _) => SelectPage(compactNavigation.SelectedIndex);
        CreatePages(0);
    }

    internal async Task<DesktopPreferences?> ShowAsync()
    {
        root.Changed += RootChanged;
        try
        {
            UpdateBounds();
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? draft : null;
        }
        finally
        {
            closed = true;
            root.Changed -= RootChanged;
        }
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();

    private void UpdateBounds()
    {
        double width = Math.Max(1, Math.Min(800, root.Size.Width - 76));
        bool compact = width < 555;
        navigation.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        compactNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        compactNavigation.Margin = new Thickness(0, 0, 0, 10);
        navigationColumn.Width = new GridLength(compact ? 0 : 156);
        body.ColumnSpacing = compact ? 0 : 20;
        layout.Width = width;
        layout.Height = Math.Max(1, Math.Min(550, root.Size.Height - 158));
        dialog.MaxHeight = Math.Max(1, root.Size.Height - 24);
    }

    private void SelectPage(int index)
    {
        if (index < 0 || index >= pages.Length) return;
        if (navigation.SelectedIndex != index) navigation.SelectedIndex = index;
        if (compactNavigation.SelectedIndex != index) compactNavigation.SelectedIndex = index;
        if (ReferenceEquals(scroller.Content, pages[index])) return;
        scroller.Content = pages[index];
        scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void CreatePages(int selected)
    {
        scroller.Content = null;
        pages = [CreateCapturePage(), CreateRecordingPage(), CreateWorkspacePage(), CreateShortcutsPage(), CreateUpdatesPage()];
        SelectPage(selected);
    }

    private StackPanel CreateCapturePage()
    {
        var page = Page("Screenshots");
        AddToggle(page, "Copy screenshots and edits to the clipboard", draft.AutoCopyCaptures, value => draft.AutoCopyCaptures = value);
        AddToggle(page, "Save original screenshots automatically", draft.AutoSaveOriginalCaptures, value => draft.AutoSaveOriginalCaptures = value);
        AddToggle(page, "Ask to save edits when leaving a screenshot", draft.AskSaveEditedScreenshots, value => draft.AskSaveEditedScreenshots = value);
        AddPath(page, "Screenshot folder", draft.ScreenshotFolder, value => draft.ScreenshotFolder = value, folder: true);
        AddHeading(page, "Capture behavior");
        AddToggle(page, "Open new screenshots in separate windows", draft.CaptureInNewWindow, value => draft.CaptureInNewWindow = value);
        captureDelay = AddNumber(page, "Delay (seconds)", draft.CaptureDelaySeconds, 0, 10, value => draft.CaptureDelaySeconds = value);
        AddToggle(page, "Remember capture delay between sessions", draft.RememberCaptureDelay, value => draft.RememberCaptureDelay = value);
        AddToggle(page, "Correct colors on HDR displays", draft.UseHdrColorCorrection, value => draft.UseHdrColorCorrection = value);
        var hideTaskbars = AddToggle(page, "Hide taskbars during area selection", draft.HideTaskbars, value => draft.HideTaskbars = value);
        var keepPrimary = AddToggle(page, "Keep the primary taskbar visible", draft.KeepPrimaryTaskbarVisible, value => draft.KeepPrimaryTaskbarVisible = value);
        keepPrimary.IsEnabled = hideTaskbars.IsOn;
        hideTaskbars.Toggled += (_, _) => keepPrimary.IsEnabled = hideTaskbars.IsOn;
        AddHeading(page, "Screenshot border");
        var border = AddToggle(page, "Add a border to screenshots", draft.AddScreenshotBorder, value => draft.AddScreenshotBorder = value);
        var picker = new ColorPicker
        {
            Color = Windows.UI.Color.FromArgb((byte)(draft.ScreenshotBorderArgb >> 24), (byte)(draft.ScreenshotBorderArgb >> 16),
                (byte)(draft.ScreenshotBorderArgb >> 8), (byte)draft.ScreenshotBorderArgb),
            IsAlphaEnabled = true
        };
        var color = new Button { Content = "Choose border color", Flyout = new Flyout { Content = picker } };
        picker.ColorChanged += (_, args) =>
        {
            var selected = args.NewColor;
            draft.ScreenshotBorderArgb = ((uint)selected.A << 24) | ((uint)selected.R << 16) | ((uint)selected.G << 8) | selected.B;
        };
        page.Children.Add(color);
        borderWidth = AddNumber(page, "Thickness (px)", draft.ScreenshotBorderWidth, 1, 20, value => draft.ScreenshotBorderWidth = value);
        color.IsEnabled = borderWidth.IsEnabled = border.IsOn;
        border.Toggled += (_, _) => color.IsEnabled = borderWidth.IsEnabled = border.IsOn;
        return page;
    }

    private StackPanel CreateRecordingPage()
    {
        var page = Page("Recording");
        AddToggle(page, "Copy saved recordings and edits to the clipboard", draft.AutoCopyRecordingChanges, value => draft.AutoCopyRecordingChanges = value);
        AddToggle(page, "Save new recordings automatically", draft.AutoSaveOriginalRecordings, value => draft.AutoSaveOriginalRecordings = value);
        AddToggle(page, "Ask to save trims before leaving a recording", draft.AskSaveEditedRecordings, value => draft.AskSaveEditedRecordings = value);
        AddPath(page, "Recording folder", draft.RecordingFolder, value => draft.RecordingFolder = value, folder: true);
        AddHeading(page, "Audio defaults");
        AddToggle(page, "Include microphone", draft.DefaultMicrophone, value => draft.DefaultMicrophone = value);
        var microphone = new ComboBox { Header = "Microphone device", HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(microphone);
        AddToggle(page, "Include system audio", draft.DefaultSystemAudio, value => draft.DefaultSystemAudio = value);
        var output = new ComboBox { Header = "System audio device", HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(output);
        SetAudioOptions(microphone, [], draft.DefaultMicrophoneDeviceId);
        SetAudioOptions(output, [], draft.DefaultSystemAudioDeviceId);
        microphone.SelectionChanged += (_, _) => { if (microphone.SelectedItem is ComboBoxItem { Tag: string id }) draft.DefaultMicrophoneDeviceId = id; };
        output.SelectionChanged += (_, _) => { if (output.SelectedItem is ComboBoxItem { Tag: string id }) draft.DefaultSystemAudioDeviceId = id; };
        var refresh = new Button { Content = "Refresh audio devices" };
        page.Children.Add(refresh);
        async Task RefreshAsync()
        {
            refresh.IsEnabled = false;
            try
            {
                var devices = await Task.Run(() => (RecordingController.GetMicrophones(), RecordingController.GetSystemAudioDevices()), cancellationToken);
                if (closed || !ReferenceEquals(pages[1], page)) return;
                SetAudioOptions(microphone, devices.Item1, draft.DefaultMicrophoneDeviceId);
                SetAudioOptions(output, devices.Item2, draft.DefaultSystemAudioDeviceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
            {
                if (!closed && ReferenceEquals(scroller.Content, page))
                    ShowNotice(InfoBarSeverity.Warning, "Audio devices could not be listed. " + failure.Message);
            }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await RefreshAsync();
        bool requested = false;
        page.Loaded += async (_, _) =>
        {
            if (requested) return;
            requested = true;
            await RefreshAsync();
        };
        return page;
    }

    private static void SetAudioOptions(ComboBox combo, IReadOnlyList<RecordingAudioDevice> devices, string selected)
    {
        combo.Items.Clear();
        combo.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = "" });
        foreach (var device in devices) combo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        var matching = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected));
        if (matching is null)
        {
            matching = new ComboBoxItem { Content = "Saved device unavailable", Tag = selected };
            combo.Items.Add(matching);
        }
        combo.SelectedItem = matching;
    }

    private StackPanel CreateWorkspacePage()
    {
        var page = Page("Workspace");
        AddChoice(page, "Appearance", ["System", "Light", "Dark"], draft.Theme, value => draft.Theme = value);
        AddChoice(page, "Annotation toolbar position", ["Top", "Right", "Bottom", "Left"], draft.AnnotationToolbarPosition,
            value => draft.AnnotationToolbarPosition = value);
        AddToggle(page, "Show tooltips", draft.ShowTooltips, value => draft.ShowTooltips = value);
        AddToggle(page, "Start with a compact window", draft.StartCompact, value => draft.StartCompact = value);
        AddToggle(page, "Resize the window to fit new snips", draft.ResizeToSnip, value => draft.ResizeToSnip = value);
        AddToggle(page, "Require Ctrl when using the wheel to zoom", draft.RequireCtrlForWheelZoom, value => draft.RequireCtrlForWheelZoom = value);
        AddHeading(page, "External image editor");
        AddPath(page, "Editor executable (optional)", draft.EditorApplicationPath, value => draft.EditorApplicationPath = value, folder: false);
        page.Children.Add(Label("Leave this empty to choose an editor when opening an image externally."));
        return page;
    }

    private StackPanel CreateShortcutsPage()
    {
        var page = Page("Shortcuts and startup");
        page.Children.Add(Label(DesktopIntegrationService.CaptureShortcutLabel + " takes a screenshot. " +
            DesktopIntegrationService.RecordingShortcutLabel + " opens recording."));
        AddToggle(page, "Enable global capture and recording shortcuts", draft.HotkeysEnabled, value => draft.HotkeysEnabled = value);
        AddToggle(page, "Use Print Screen for Snip Studio", draft.UsePrintScreen, value => draft.UsePrintScreen = value);
        page.Children.Add(Label("Turn off the Windows Print Screen shortcut before assigning it to Snip Studio."));
        var keyboard = new Button { Content = "Open Windows keyboard settings" };
        keyboard.Click += async (_, _) => await RunPickerActionAsync(async () =>
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(DesktopIntegrationService.KeyboardSettingsUri)))
                ShowNotice(InfoBarSeverity.Warning, "Windows keyboard settings could not be opened.");
        });
        page.Children.Add(keyboard);
        AddHeading(page, "After closing");
        AddToggle(page, "Keep shortcuts active after closing the editor", draft.KeepShortcutsAfterClose, value => draft.KeepShortcutsAfterClose = value);
        AddToggle(page, "Keep the editor in the notification area", draft.KeepRunningInTray, value => draft.KeepRunningInTray = value);
        AddToggle(page, "Start Snip Studio when I sign in", draft.StartupWithWindows, value => draft.StartupWithWindows = value);
        return page;
    }

    private StackPanel CreateUpdatesPage()
    {
        var page = Page("Updates");
        AddToggle(page, "Check for updates automatically", draft.AutoCheckForUpdates, value => draft.AutoCheckForUpdates = value);
        page.Children.Add(Label("Snip Studio checks release information at most once a day. Open Check for updates from the main menu to check manually."));
        return page;
    }

    private static StackPanel Page(string title)
    {
        var panel = new StackPanel { Spacing = 11, Padding = new Thickness(0, 0, 14, 8) };
        var heading = Label(title);
        heading.FontSize = 22;
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        panel.Children.Add(heading);
        return panel;
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static void AddHeading(StackPanel page, string text)
    {
        var heading = Label(text);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(heading);
    }

    private static ToggleSwitch AddToggle(StackPanel page, string title, bool current, Action<bool> assign)
    {
        var label = Label(title);
        label.VerticalAlignment = VerticalAlignment.Center;
        var toggle = new ToggleSwitch { IsOn = current, OnContent = null, OffContent = null, MinWidth = 0 };
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => assign(toggle.IsOn);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(toggle, 1);
        row.Children.Add(label);
        row.Children.Add(toggle);
        page.Children.Add(row);
        return toggle;
    }

    private static void AddChoice(StackPanel page, string title, string[] choices, string current, Action<string> assign)
    {
        var combo = new ComboBox { Header = title, ItemsSource = choices, SelectedItem = current, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string value) assign(value); };
        page.Children.Add(combo);
    }

    private static NumberBox AddNumber(StackPanel page, string title, int current, int min, int max, Action<int> assign)
    {
        var box = new NumberBox
        {
            Header = title, Minimum = min, Maximum = max, Value = current, Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left, SmallChange = 1, LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        box.ValueChanged += (_, args) =>
        {
            if (double.IsFinite(args.NewValue) && args.NewValue == Math.Truncate(args.NewValue)) assign((int)args.NewValue);
        };
        page.Children.Add(box);
        return box;
    }

    private void AddPath(StackPanel page, string title, string current, Action<string> assign, bool folder)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var path = new TextBox { Header = title, Text = current, MinWidth = 0 };
        path.TextChanged += (_, _) => assign(path.Text);
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(browse, "Browse " + title);
        browse.Click += async (_, _) => await RunPickerActionAsync(async () =>
        {
            string? selection = folder
                ? (await new FolderPicker(owner).PickSingleFolderAsync())?.Path
                : await PickFileAsync(".exe", "Use editor");
            if (selection is not null) path.Text = selection;
        });
        row.Children.Add(path);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        page.Children.Add(row);
    }

    private async Task<string?> PickFileAsync(string extension, string action)
    {
        var picker = new FileOpenPicker(owner) { CommitButtonText = action };
        picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private async Task ImportAsync()
    {
        string? path = File.Exists(LegacySettingsImport.DefaultPath)
            ? LegacySettingsImport.DefaultPath
            : await PickFileAsync(".xml", "Review previous settings");
        if (path is null) return;
        DesktopPreferences before = draft;
        draft = await Task.Run(() => LegacySettingsImport.Import(path, before), cancellationToken);
        CreatePages(navigation.SelectedIndex);
        ShowNotice(InfoBarSeverity.Success, "Previous settings loaded. Review the categories, then select Save to apply them.");
    }

    private void ShowNotice(InfoBarSeverity severity, string message)
    {
        notice.Severity = severity;
        notice.Message = message;
        notice.IsOpen = true;
    }

    private async Task RunPickerActionAsync(Func<Task> action)
    {
        if (busy) return;
        SetBusy(true);
        try { await action(); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            ShowNotice(InfoBarSeverity.Error, failure.Message);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        dialog.IsPrimaryButtonEnabled = !value;
        body.IsHitTestVisible = !value;
    }

    private async void SaveDraft(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        SetBusy(true);
        try
        {
            SelectPageForInvalidNumber(captureDelay, "Capture delay", 0, 10);
            if (draft.AddScreenshotBorder) SelectPageForInvalidNumber(borderWidth, "Border thickness", 1, 20);
            draft.ScreenshotFolder = ValidateFolder(draft.ScreenshotFolder, "Screenshots", 0);
            draft.RecordingFolder = ValidateFolder(draft.RecordingFolder, "Recording", 1);
            if (!string.Equals(draft.EditorApplicationPath, originalEditorPath, StringComparison.Ordinal))
            {
                try
                {
                    draft.EditorApplicationPath = await DesktopExternalEditorService.ValidateConfiguredEditorPathAsync(draft.EditorApplicationPath, cancellationToken);
                }
                catch
                {
                    SelectPage(2);
                    throw;
                }
            }
            draft.Normalize();
            save(draft.Clone());
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            args.Cancel = true;
            ShowNotice(InfoBarSeverity.Error, failure.Message);
        }
        finally
        {
            SetBusy(false);
            deferral.Complete();
        }
    }

    private void SelectPageForInvalidNumber(NumberBox box, string label, int min, int max)
    {
        if (double.IsFinite(box.Value) && box.Value == Math.Truncate(box.Value) && box.Value >= min && box.Value <= max)
            return;
        SelectPage(0);
        box.Focus(FocusState.Programmatic);
        throw new ArgumentException($"{label} must be a whole number from {min} to {max}.");
    }

    private string ValidateFolder(string path, string category, int page)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
        {
            SelectPage(page);
            throw new ArgumentException($"Choose an absolute folder path in {category}.");
        }
        try { return Path.GetFullPath(path); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SelectPage(page);
            throw new ArgumentException($"Choose a valid folder in {category}.", failure);
        }
    }
}
