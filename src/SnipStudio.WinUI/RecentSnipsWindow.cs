using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SnipStudio.WinUI.Backend;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using FileAttributes = System.IO.FileAttributes;

namespace SnipStudio.WinUI;

internal sealed class RecentSnipsWindow : Window
{
    private static readonly SemaphoreSlim thumbnailWorkers = new(2, 2);
    private readonly TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<IReadOnlyList<string>> readAppPaths;
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock previewText = new() { Text = "Select a file to preview.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock previewPath = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
    private readonly Image previewImage = new() { Stretch = Stretch.Uniform };
    private readonly Button refresh = new() { Content = "Refresh" };
    private readonly Button open = new() { Content = "Open", IsEnabled = false, MinWidth = 88 };
    private readonly Grid contentArea = new() { ColumnSpacing = 16, RowSpacing = 12 };
    private readonly Grid previewArea = new() { RowSpacing = 8 };
    private readonly ColumnDefinition listColumn = new();
    private readonly ColumnDefinition previewColumn = new();
    private readonly RowDefinition previewRow = new();
    private readonly DispatcherTimer hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private IReadOnlyList<string> shownPaths = Array.Empty<string>();
    private ListViewItem? hoveredItem;
    private CancellationTokenSource? rowThumbnails;
    private CancellationTokenSource? selectedThumbnail;
    private string? acceptedPath;
    private bool closed;

    private RecentSnipsWindow(WindowId owner, Func<IReadOnlyList<string>> readAppPaths, string theme)
    {
        this.readAppPaths = readAppPaths;
        Title = "Snip Studio — Recent files";
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var layout = new Grid
        {
            Padding = new Thickness(20, 16, 20, 16),
            RowSpacing = 12,
            RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default }
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "Recent snips and recordings",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        ToolTipService.SetToolTip(refresh, "Refresh Snip Studio and Windows Recent files");
        Grid.SetColumn(refresh, 1);
        heading.Children.Add(refresh);
        layout.Children.Add(heading);
        AddInRow(layout, status, 1);

        contentArea.ColumnDefinitions.Add(listColumn);
        contentArea.ColumnDefinitions.Add(previewColumn);
        contentArea.RowDefinitions.Add(new RowDefinition());
        contentArea.RowDefinitions.Add(previewRow);
        contentArea.Children.Add(list);
        contentArea.Children.Add(previewArea);
        previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewArea.RowDefinitions.Add(new RowDefinition());
        previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewArea.Children.Add(previewText);
        AddInRow(previewArea, previewImage, 1);
        AddInRow(previewArea, previewPath, 2);
        AddInRow(layout, contentArea, 2);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        AddInRow(layout, buttons, 3);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        layout.KeyboardAccelerators.Add(escape);
        var enter = new KeyboardAccelerator { Key = VirtualKey.Enter, ScopeOwner = list };
        enter.Invoked += (_, args) =>
        {
            if (!open.IsEnabled) return;
            args.Handled = true;
            OpenSelection();
        };
        list.KeyboardAccelerators.Add(enter);
        AutomationProperties.SetName(list, "Recent files");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        refresh.Click += async (_, _) => await RefreshAsync();
        open.Click += (_, _) => OpenSelection();
        cancel.Click += (_, _) => Close();
        hoverDelay.Tick += (_, _) =>
        {
            hoverDelay.Stop();
            ListViewItem? item = hoveredItem;
            hoveredItem = null;
            if (item is not null) list.SelectedItem = item;
        };
        list.SelectionChanged += async (_, _) =>
        {
            hoverDelay.Stop();
            hoveredItem = null;
            await ShowSelectionAsync();
        };
        contentArea.SizeChanged += (_, args) => ArrangePreview(args.NewSize.Width);
        Closed += OnClosed;
        Content = layout;
        ArrangePreview(770);
        _ = new DesktopWindowSizing(this, owner, 850, 650, 620, 450);
    }

    internal static async Task<string?> ShowAsync(WindowId owner, Func<IReadOnlyList<string>> readAppPaths, string theme, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(readAppPaths);
        token.ThrowIfCancellationRequested();
        var window = new RecentSnipsWindow(owner, readAppPaths, theme);
        using CancellationTokenRegistration registration = token.Register(() =>
            window.DispatcherQueue.TryEnqueue(() => { if (!window.closed) window.Close(); }));
        window.Activate();
        _ = window.RefreshAsync();
        return await window.completion.Task;
    }

    private void ArrangePreview(double width)
    {
        bool narrow = width < 510;
        listColumn.Width = new GridLength(narrow ? 1 : 3, GridUnitType.Star);
        previewColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        previewRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(previewArea, narrow ? 0 : 1);
        Grid.SetRow(previewArea, narrow ? 1 : 0);
    }

    private async Task RefreshAsync()
    {
        if (closed || !refresh.IsEnabled) return;
        refresh.IsEnabled = false;
        status.Text = "Refreshing recent files…";
        CancellationToken token = lifetime.Token;
        try
        {
            IReadOnlyList<string> appPaths = await Task.Run(readAppPaths, token).WaitAsync(token);
            if (shownPaths.Count == 0) Populate(DesktopRecentHistory.Merge(appPaths, Array.Empty<string>()));
            DesktopRecentHistoryResult windows = await DesktopRecentHistory.ReadWindowsAsync(token);
            if (closed) return;
            Populate(DesktopRecentHistory.Merge(appPaths, windows.Paths));
            status.Text = shownPaths.Count == 0
                ? "No recent files. Open or save a snip or recording to add it here."
                : windows.WindowsHistoryAvailable ? "Snip Studio and Windows Recent files" : "Snip Studio files. Windows Recent is unavailable.";
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed) status.Text = "Recent files could not be refreshed. Try again.";
        }
        finally
        {
            if (!closed) refresh.IsEnabled = true;
        }
    }

    private void Populate(IReadOnlyList<string> paths)
    {
        if (closed || shownPaths.SequenceEqual(paths, StringComparer.Ordinal)) return;
        string? prior = (list.SelectedItem as ListViewItem)?.Tag as string;
        hoverDelay.Stop();
        hoveredItem = null;
        rowThumbnails?.Cancel();
        rowThumbnails?.Dispose();
        rowThumbnails = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        shownPaths = paths;
        list.Items.Clear();
        var thumbnails = new List<(string Path, Image Image)>();
        foreach (string path in paths)
        {
            var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = new Grid { Width = 56, Height = 40 };
            tile.Children.Add(new FontIcon { Glyph = FileGlyph(path), FontSize = 19, Opacity = .65 });
            var image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
            tile.Children.Add(image);
            row.Children.Add(tile);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(path),
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = Path.GetDirectoryName(path) ?? "",
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                Opacity = .75
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var item = new ListViewItem { Content = row, Tag = path, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, path);
            ToolTipService.SetToolTip(item, path);
            item.PointerEntered += (_, args) =>
            {
                if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
                hoveredItem = item;
                hoverDelay.Stop();
                hoverDelay.Start();
            };
            item.PointerExited += (_, _) =>
            {
                if (!ReferenceEquals(hoveredItem, item)) return;
                hoveredItem = null;
                hoverDelay.Stop();
            };
            item.DoubleTapped += (_, _) =>
            {
                list.SelectedItem = item;
                if (open.IsEnabled) OpenSelection();
            };
            list.Items.Add(item);
            thumbnails.Add((path, image));
            if (string.Equals(prior, path, StringComparison.OrdinalIgnoreCase)) list.SelectedItem = item;
        }
        if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
        _ = LoadRowThumbnailsAsync(thumbnails, rowThumbnails.Token);
    }

    private static async Task LoadRowThumbnailsAsync(IEnumerable<(string Path, Image Image)> rows, CancellationToken token)
    {
        foreach (var (path, target) in rows)
        {
            if (token.IsCancellationRequested) return;
            BitmapImage? image = await ReadThumbnailAsync(path, 96, token);
            if (token.IsCancellationRequested) return;
            if (image is null) continue;
            target.Source = image;
            target.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowSelectionAsync()
    {
        if (closed) return;
        selectedThumbnail?.Cancel();
        selectedThumbnail?.Dispose();
        selectedThumbnail = null;
        previewImage.Source = null;
        string? path = (list.SelectedItem as ListViewItem)?.Tag as string;
        open.IsEnabled = path is not null && !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        previewPath.Text = path ?? "";
        if (path is null)
        {
            previewText.Text = "Select a file to preview.";
            return;
        }
        if (!open.IsEnabled)
        {
            previewText.Text = "Exported PDF. Open its source snip to keep editing.";
            return;
        }
        if (path.EndsWith(".snipstudio", StringComparison.OrdinalIgnoreCase))
        {
            previewText.Text = "Editable Snip Studio project";
            return;
        }
        previewText.Text = "Loading preview…";
        selectedThumbnail = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = selectedThumbnail.Token;
        BitmapImage? image = await ReadThumbnailAsync(path, 640, token);
        if (closed || token.IsCancellationRequested) return;
        previewImage.Source = image;
        previewText.Text = image is null ? "No preview available." : Path.GetFileName(path);
    }

    private static async Task<BitmapImage?> ReadThumbnailAsync(string path, uint edge, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        CancellationToken checkToken = timeout.Token;
        try
        {
            await thumbnailWorkers.WaitAsync(checkToken);
            // Waiting times out immediately. Keep the worker lease until any
            // unresponsive filesystem or thumbnail provider returns.
            return await ReadThumbnailCoreAsync(path, edge, checkToken).WaitAsync(checkToken);
        }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<BitmapImage?> ReadThumbnailCoreAsync(string path, uint edge, CancellationToken token)
    {
        try
        {
            if (!await Task.Run(() => IsLocalPreviewFile(path)) || token.IsCancellationRequested) return null;
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            token.ThrowIfCancellationRequested();
            using StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, edge, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return null;
            token.ThrowIfCancellationRequested();
            var bitmap = new BitmapImage { DecodePixelWidth = checked((int)edge) };
            await bitmap.SetSourceAsync(thumbnail);
            return token.IsCancellationRequested ? null : bitmap;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException) { return null; }
        finally { thumbnailWorkers.Release(); }
    }

    private static bool IsLocalPreviewFile(string path)
    {
        // Recent entries can point at network shares or offline placeholders.
        // Opening remains an explicit action. Background thumbnails use only
        // local, connected files and do not follow filesystem redirects.
        string? root = Path.GetPathRoot(path);
        if (root is null || root.Length != 3 || new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable)
            || path.EndsWith(".snipstudio", StringComparison.OrdinalIgnoreCase))
            return false;
        for (string? entry = path; entry is not null && entry.Length > root.Length; entry = Path.GetDirectoryName(entry))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) return false;
        }
        return true;
    }

    private void OpenSelection()
    {
        if (!open.IsEnabled || (list.SelectedItem as ListViewItem)?.Tag is not string path) return;
        acceptedPath = path;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        hoverDelay.Stop();
        hoveredItem = null;
        lifetime.Cancel();
        rowThumbnails?.Dispose();
        selectedThumbnail?.Dispose();
        lifetime.Dispose();
        completion.TrySetResult(acceptedPath);
    }

    private static void AddInRow(Grid grid, FrameworkElement child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static string FileGlyph(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".wmv" or ".mov" or ".avi" or ".m4v" or ".webm" or ".mkv" => "\uE714",
        ".pdf" or ".snipstudio" => "\uE8A5",
        _ => "\uEB9F"
    };
}
