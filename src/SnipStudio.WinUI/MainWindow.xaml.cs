using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow : Window
{
    private const ulong MaximumClipboardBytes = 128UL * 1024 * 1024;
    private readonly EditorSession session = new();
    private CancellationTokenSource? previewCancellation;
    private Task<byte[]>? clipboardRead;
    private int previewGeneration;
    private long displayedRevision = -1;
    private bool resetPreviewViewPending;
    private bool busy, closed, allowClose, closePending, changingColor;
    private string suggestedName = "Snip Studio image";
    private string? displayPath;
    private uint penColor = 0xFFE24046, highlighterColor = 0xFFFFE33B;
    private PreviewTool selectedTool = PreviewTool.Pen;
    private bool PreviewIsCurrent => session.HasImage && displayedRevision == session.Revision;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        Viewport.GestureCommitted += OnGestureCommitted;
        Viewport.ViewChanged += (_, _) => UpdateViewStatus();
        ConfigureAccelerators();
        SetTool(PreviewTool.Pen);
        UpdateCommands();
    }

    private void ConfigureAccelerators()
    {
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, OpenAsync);
        AddShortcut(VirtualKey.V, VirtualKeyModifiers.Control, PasteAsync, respectsTextInput: true);
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, () => RunActionAsync("Saving image…", SaveWithPickerAsync));
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => RunActionAsync("Saving image…", SaveWithPickerAsync));
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control, () => ChangeHistoryAsync(false), respectsTextInput: true);
        AddShortcut(VirtualKey.Y, VirtualKeyModifiers.Control, () => ChangeHistoryAsync(true), respectsTextInput: true);
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ChangeHistoryAsync(true), respectsTextInput: true);
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => ChangeViewAsync(Viewport.Fit));
        AddShortcut(VirtualKey.Number0, VirtualKeyModifiers.Control, () => ChangeViewAsync(Viewport.ActualPixels));
        AddShortcut(VirtualKey.Add, VirtualKeyModifiers.Control, () => ChangeViewAsync(() => Viewport.ZoomBy(1.1)));
        AddShortcut((VirtualKey)187, VirtualKeyModifiers.Control, () => ChangeViewAsync(() => Viewport.ZoomBy(1.1)));
        AddShortcut(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => ChangeViewAsync(() => Viewport.ZoomBy(1 / 1.1)));
        AddShortcut((VirtualKey)189, VirtualKeyModifiers.Control, () => ChangeViewAsync(() => Viewport.ZoomBy(1 / 1.1)));
        AddShortcut(VirtualKey.F1, VirtualKeyModifiers.None, ShowHelpAsync);
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action, bool respectsTextInput = false)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        shortcut.Invoked += async (_, args) =>
        {
            if (respectsTextInput && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox) return;
            args.Handled = true;
            await action();
        };
        Root.KeyboardAccelerators.Add(shortcut);
    }

    private async void OnOpenClicked(object sender, RoutedEventArgs args) => await OpenAsync();
    private async void OnPasteClicked(object sender, RoutedEventArgs args) => await PasteAsync();
    private async void OnSaveClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Saving image…", SaveWithPickerAsync);
    private async void OnUndoClicked(object sender, RoutedEventArgs args) => await ChangeHistoryAsync(false);
    private async void OnRedoClicked(object sender, RoutedEventArgs args) => await ChangeHistoryAsync(true);
    private async void OnHelpClicked(object sender, RoutedEventArgs args) => await ShowHelpAsync();
    private async void OnRetryPreviewClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Rendering image preview…", async () =>
    {
        await RefreshPreviewAsync(resetView: false);
        SetStatus("Preview restored. " + ToolInstruction());
        return true;
    });
    private void OnPanClicked(object sender, RoutedEventArgs args) => SetTool(PreviewTool.Pan);
    private void OnPenClicked(object sender, RoutedEventArgs args) => SetTool(PreviewTool.Pen);
    private void OnHighlighterClicked(object sender, RoutedEventArgs args) => SetTool(PreviewTool.Highlighter);
    private void OnRectangleClicked(object sender, RoutedEventArgs args) => SetTool(PreviewTool.Rectangle);
    private void OnFitClicked(object sender, RoutedEventArgs args) => ChangeView(Viewport.Fit);
    private void OnActualSizeClicked(object sender, RoutedEventArgs args) => ChangeView(Viewport.ActualPixels);
    private void OnZoomInClicked(object sender, RoutedEventArgs args) => ChangeView(() => Viewport.ZoomBy(1.1));
    private void OnZoomOutClicked(object sender, RoutedEventArgs args) => ChangeView(() => Viewport.ZoomBy(1 / 1.1));

    private async Task OpenAsync()
    {
        await RunActionAsync("Choose an image…", async () =>
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "Open image"
            };
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(extension);
            var chosen = await picker.PickSingleFileAsync();
            if (chosen is null) { SetStatus("Open canceled. Your current image is kept."); return false; }
            if (!await ConfirmReplaceAsync()) return false;
            SetStatus("Opening image…");
            // The backend validates/copies the replacement before releasing the old document.
            session.Open(chosen.Path);
            displayPath = chosen.Path;
            suggestedName = Path.GetFileNameWithoutExtension(chosen.Path) + "-edited";
            await RefreshPreviewAsync(resetView: true);
            SetStatus("Opened " + Path.GetFileName(chosen.Path) + ". " + ToolInstruction());
            return true;
        });
    }

    private async Task PasteAsync()
    {
        await RunActionAsync("Reading clipboard image…", async () =>
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                SetStatus("The clipboard does not contain an image. Your current work is kept.");
                return false;
            }
            if (clipboardRead is { IsCompleted: false })
                throw new InvalidOperationException("The previous clipboard request is still finishing. Your work is kept; try Paste again later.");
            var read = ReadClipboardImageCoreAsync(content);
            clipboardRead = read;
            _ = ObserveClipboardReadAsync(read);
            byte[] bytes;
            try { bytes = await read.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException)
            {
                throw new TimeoutException("The clipboard did not respond within eight seconds. Your current image is kept.");
            }
            if (!await ConfirmReplaceAsync()) return false;
            session.OpenImageBytes(bytes);
            displayPath = null;
            suggestedName = "Snip Studio image";
            await RefreshPreviewAsync(resetView: true);
            SetStatus("Pasted image. " + ToolInstruction());
            return true;
        });
    }

    private static async Task<byte[]> ReadClipboardImageCoreAsync(DataPackageView content)
    {
        // Start the provider request on the UI thread, then release every late
        // stream off the UI thread even if the window closes after its UI timeout.
        var reference = await content.GetBitmapAsync().AsTask().ConfigureAwait(false);
        using var stream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
        if (stream.Size == 0 || stream.Size > MaximumClipboardBytes)
            throw new ArgumentException("The clipboard image is empty or exceeds the 128 MB preview limit.");
        uint size = checked((uint)stream.Size);
        using var reader = new DataReader(stream);
        uint loaded = await reader.LoadAsync(size).AsTask().ConfigureAwait(false);
        if (loaded != size) throw new IOException("The clipboard image could not be read completely.");
        byte[] bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task ObserveClipboardReadAsync(Task<byte[]> pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* Observe late provider failures after a UI timeout. */ }
        finally { _ = Interlocked.CompareExchange(ref clipboardRead, null, pending); }
    }

    private async Task<bool> ConfirmReplaceAsync()
    {
        if (!session.IsDirty) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Save changes to this image?",
            Content = "Save a copy before continuing, discard these changes, or cancel and keep editing.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.Secondary) return true;
        if (answer != ContentDialogResult.Primary) { SetStatus("Canceled. Your changes are kept."); return false; }
        return await SaveWithPickerAsync();
    }

    private async Task<bool> SaveWithPickerAsync()
    {
        if (!session.HasImage) { SetStatus("Open or paste an image before saving."); return false; }
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".png",
            CommitButtonText = "Save image"
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg", ".jpeg" });
        picker.FileTypeChoices.Add("Bitmap image", new List<string> { ".bmp" });
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        var chosen = await picker.PickSaveFileAsync();
        if (chosen is null) { SetStatus("Save canceled. Your changes are kept."); return false; }
        SetStatus("Saving the image and finishing the file…");
        var result = await session.SaveAsync(chosen.Path);
        if (!result.SavedCurrentRevision)
        {
            SetStatus("Saved an earlier image revision. Your current changes remain unsaved.");
            return false;
        }
        displayPath = result.Path;
        suggestedName = Path.GetFileNameWithoutExtension(result.Path);
        SetStatus("Saved " + Path.GetFileName(result.Path) + ".");
        return true;
    }

    private async Task ChangeHistoryAsync(bool redo)
    {
        if (!session.HasImage || (redo ? !session.CanRedo : !session.CanUndo)) return;
        await RunActionAsync(redo ? "Redoing edit…" : "Undoing edit…", async () =>
        {
            Viewport.CancelGesture();
            if (redo) session.Redo(); else session.Undo();
            await RefreshPreviewAsync(resetView: false);
            SetStatus((redo ? "Redid edit. " : "Undid edit. ") + ToolInstruction());
            return true;
        });
    }

    private async void OnGestureCommitted(object? sender, DrawGestureEventArgs args)
    {
        await RunActionAsync("Rendering edit…", async () =>
        {
            if (args.Tool == PreviewTool.Rectangle)
                session.AddRectangle(args.Points[0], args.Points[^1], args.Argb, args.Width);
            else session.AddStroke(args.Points, args.Argb, args.Width, args.Tool == PreviewTool.Highlighter);
            await RefreshPreviewAsync(resetView: false);
            SetStatus(args.ReachedPointLimit
                ? "Long stroke kept at the preview limit. Lift the pointer, then draw again to continue."
                : "Edit kept. " + ToolInstruction());
            return true;
        });
    }

    private async Task RefreshPreviewAsync(bool resetView)
    {
        // All callers are serialized by busy. A canceled native PNG encode cannot
        // be interrupted, so no replacement worker starts before this one returns.
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        resetPreviewViewPending |= resetView;
        displayedRevision = -1;
        CancellationToken token = previewCancellation.Token;
        int generation = ++previewGeneration;
        PreviewSnapshot preview = await session.RenderPreviewAsync(token);
        if (closed || token.IsCancellationRequested || generation != previewGeneration || preview.Revision != session.Revision) return;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(preview.PngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        if (closed || token.IsCancellationRequested || generation != previewGeneration || preview.Revision != session.Revision) return;
        Viewport.SetImage(bitmap, session.ImageWidth, session.ImageHeight, resetPreviewViewPending);
        resetPreviewViewPending = false;
        displayedRevision = preview.Revision;
        UpdateViewStatus();
    }

    private async Task<bool> RunActionAsync(string message, Func<Task<bool>> action)
    {
        if (busy || closed) return false;
        busy = true;
        Viewport.CancelGesture();
        if (session.HasImage && !PreviewIsCurrent) Viewport.ShowUnavailable();
        ErrorNotice.Title = session.HasImage && !PreviewIsCurrent ? "Preview unavailable" : "The action could not finish";
        ErrorNotice.IsOpen = false;
        SetStatus(message);
        UpdateCommands();
        try { return await action(); }
        catch (OperationCanceledException) { if (!closed) SetStatus("Canceled. Your current work is kept."); return false; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ReportUnexpectedFailure(exception);
            return false;
        }
        finally
        {
            busy = false;
            if (!closed) UpdateCommands();
        }
    }

    internal void ReportUnexpectedFailure(Exception exception)
    {
        if (closed) return;
        Viewport.CancelGesture();
        if (session.HasImage && !PreviewIsCurrent) Viewport.ShowUnavailable();
        ErrorNotice.Title = session.HasImage && !PreviewIsCurrent ? "Preview unavailable" : "The action could not finish";
        ErrorNotice.Message = exception.Message + " Your document has been kept where possible; retry or save it separately.";
        ErrorNotice.IsOpen = true;
        SetStatus("The action could not finish. Review the message above.");
        UpdateCommands();
    }

    private void SetTool(PreviewTool next)
    {
        if (busy || closed) return;
        selectedTool = next;
        Viewport.Tool = next;
        PanButton.IsChecked = next == PreviewTool.Pan;
        PenButton.IsChecked = next == PreviewTool.Pen;
        HighlighterButton.IsChecked = next == PreviewTool.Highlighter;
        RectangleButton.IsChecked = next == PreviewTool.Rectangle;
        uint color = next == PreviewTool.Highlighter ? highlighterColor : penColor;
        Viewport.InkArgb = color;
        changingColor = true;
        InkColorPicker.Color = Windows.UI.Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        changingColor = false;
        if (session.HasImage) SetStatus(ToolInstruction());
    }

    private void OnInkColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (changingColor || Viewport is null || busy) return;
        Viewport.CancelGesture();
        var color = args.NewColor;
        uint argb = 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (selectedTool == PreviewTool.Highlighter) highlighterColor = argb; else penColor = argb;
        Viewport.InkArgb = argb;
    }

    private void OnStrokeWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (Viewport is null) return;
        Viewport.CancelGesture();
        if (double.IsFinite(args.NewValue)) Viewport.InkWidth = (float)Math.Clamp(args.NewValue, 1, 100);
    }

    private string ToolInstruction() => selectedTool switch
    {
        PreviewTool.Pan => "Move view: drag to pan. Ctrl+wheel zooms; Fit restores the view.",
        PreviewTool.Highlighter => "Highlighter: drag over the image. Escape cancels; Ctrl+Z undoes.",
        PreviewTool.Rectangle => "Rectangle: drag between two corners. Escape cancels; Ctrl+Z undoes.",
        _ => "Pen: drag to draw. Middle-drag pans; Ctrl+wheel zooms."
    };

    private void ChangeView(Action action)
    {
        if (busy || closed || !PreviewIsCurrent) return;
        action();
        UpdateViewStatus();
    }

    private Task ChangeViewAsync(Action action) { ChangeView(action); return Task.CompletedTask; }

    private void UpdateCommands()
    {
        if (closed) return;
        FileBar.IsEnabled = !busy;
        ToolBar.IsEnabled = !busy;
        HelpButton.IsEnabled = !busy;
        Viewport.IsEnabled = !busy && PreviewIsCurrent;
        SaveButton.IsEnabled = !busy && session.HasImage;
        UndoButton.IsEnabled = !busy && session.CanUndo;
        RedoButton.IsEnabled = !busy && session.CanRedo;
        ZoomOutButton.IsEnabled = FitButton.IsEnabled = ActualSizeButton.IsEnabled = ZoomInButton.IsEnabled = !busy && PreviewIsCurrent;
        RetryPreviewButton.Visibility = session.HasImage && !PreviewIsCurrent ? Visibility.Visible : Visibility.Collapsed;
        RetryPreviewButton.IsEnabled = !busy;
        if (!busy && session.HasImage && !PreviewIsCurrent) Viewport.ShowUnavailable();
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        string filename = displayPath is null ? "" : " — " + Path.GetFileName(displayPath);
        Title = (session.IsDirty ? "• " : "") + "Snip Studio — WinUI 3 Preview" + filename;
        UpdateViewStatus();
    }

    private void UpdateViewStatus()
    {
        if (closed || ZoomText is null) return;
        DimensionsText.Text = session.HasImage ? $"{session.ImageWidth:N0} × {session.ImageHeight:N0} px" : "";
        ZoomText.Text = !PreviewIsCurrent ? "—" : (Viewport.IsFit ? "Fit · " : "") + Viewport.ZoomPercent.ToString("0.#") + "%";
    }

    private void SetStatus(string message) { if (!closed) StatusText.Text = message; }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ZoomPanel is null) return;
        bool narrow = args.NewSize.Width < 760;
        Grid.SetRow(ZoomPanel, narrow ? 1 : 0);
        Grid.SetColumn(ZoomPanel, narrow ? 0 : 1);
        Grid.SetColumnSpan(ZoomPanel, narrow ? 2 : 1);
        Grid.SetColumnSpan(StatusInfo, narrow ? 2 : 1);
        // Long status text wraps within the available width instead of enlarging
        // its StackPanel and forcing the zoom actions outside a compact window.
        StatusText.MaxWidth = Math.Max(80, args.NewSize.Width - (narrow ? 64 : 490));
        DimensionsText.Visibility = args.NewSize.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ShowHelpAsync()
    {
        await RunActionAsync("Preview capabilities", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Snip Studio — WinUI 3 preview",
                CloseButtonText = "Close",
                Content = new ScrollViewer
                {
                    MaxHeight = 480,
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Automation Forge\n\nThis migration preview opens or pastes images, draws with Pen, Highlighter and Rectangle, keeps Undo/Redo history, and saves flattened PNG, JPEG, BMP or PDF files. It uses the existing Snip Studio document renderer.\n\nUse Fit to center the image in the visible canvas. At 100%, one image pixel maps to one physical display pixel. Ctrl+wheel zooms around the pointer; Move view or middle-drag pans. Escape cancels an unfinished gesture.\n\nCtrl+O: Open\nCtrl+V: Paste image\nCtrl+S: Save as\nCtrl+Z: Undo\nCtrl+Y or Ctrl+Shift+Z: Redo\nCtrl+F: Fit\nCtrl+0: 100%\nCtrl+plus/minus: Zoom\n\nScreenshot and scrolling capture, OCR/text editing, recording/audio/video review, crop, object editing, clipboard export, printing, recent snips, settings, tray, global shortcuts and updates are not migrated in this preview. Use the stable Snip Studio build for those workflows.\n\nThe preview does not load or write stable Snip Studio preferences, register startup or global hotkeys, change file associations, or check the stable update feed. No screenshot upload is performed.\n\nSaving produces a flattened file rather than a project with editable annotations. Keep the editor open to continue using its annotation history."
                    }
                }
            };
            await dialog.ShowAsync();
            SetStatus(session.HasImage ? ToolInstruction() : "Open an image or paste one from the clipboard.");
            return true;
        });
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        if (busy || closePending)
        {
            args.Cancel = true;
            SetStatus(session.IsSaving ? "Saving is still finishing. Keep this window open until it completes." : "Finish or cancel the current action before closing.");
            return;
        }
        if (!session.IsDirty) return;
        args.Cancel = true;
        closePending = true;
        try
        {
            bool confirmed = await RunActionAsync("Review unsaved changes…", ConfirmReplaceAsync);
            if (confirmed && !closed) { allowClose = true; Close(); }
        }
        finally { closePending = false; }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        ++previewGeneration;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
        Viewport.CancelGesture();
        session.Dispose();
    }
}
