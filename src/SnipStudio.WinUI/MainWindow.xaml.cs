using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private CancellationTokenSource? actionCancellation;
    private CancellationToken ActionToken => actionCancellation?.Token ?? CancellationToken.None;
    private Task<byte[]>? clipboardRead;
    private int previewGeneration;
    private long displayedRevision = -1;
    private bool resetPreviewViewPending;
    private bool busy, closed, allowClose, closePending, changingColor;
    private string suggestedName = "Snip Studio image";
    private string? displayPath;
    private uint penColor = 0xFFE24046, highlighterColor = 0xFFFFE33B, textColor = 0xFF000000;
    private PreviewTool selectedTool = PreviewTool.Pen;
    private bool contextDismissed = true;
    private bool PreviewIsCurrent => session.HasImage && displayedRevision == session.Revision;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureTextSizeFormatter();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 650));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        Viewport.GestureCommitted += OnGestureCommitted;
        Viewport.RecognizedSelectionPreview = PreviewOcrSelection;
        Viewport.RecognizedSelectionDismissed += (_, _) => CloseTextActions();
        Viewport.SelectionTransformCommitted += OnSelectionTransformCommitted;
        Viewport.GestureFeedback += (_, message) => SetStatus(message);
        Viewport.TextApplyRequested += async (_, _) => await ApplyTextDraftAsync();
        Viewport.TextCancelRequested += (_, _) => CancelTextDraft();
        Viewport.DraftChanged += (_, _) => UpdateTextDraftCommands();
        Viewport.ViewChanged += (_, _) => UpdateViewStatus();
        ConfigureAccelerators();
        SetTool(PreviewTool.Pen, revealOptions: false);
        InitializeDesktopIntegration();
        InitializeWindowSizing();
        InstallerExitHook.Attach(this);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => CancelPendingSnipResize()), true);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => CancelPendingSnipResize()), true);
        UpdateCommands();
    }

    private void ConfigureAccelerators()
    {
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, OpenAsync);
        AddShortcut(VirtualKey.N, VirtualKeyModifiers.Control, NewCaptureAsync);
        AddShortcut(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, BeginRecordingCaptureAsync);
        AddShortcut(VirtualKey.C, VirtualKeyModifiers.Control, CopyAsync, respectsTextInput: true);
        AddShortcut(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => RecognizeAsync(copyOnly: true));
        AddShortcut(VirtualKey.P, VirtualKeyModifiers.Control, PrintAsync);
        AddShortcut(VirtualKey.Delete, VirtualKeyModifiers.None, DeleteSelectedAsync, respectsTextInput: true);
        var editRecognized = new KeyboardAccelerator { Key = VirtualKey.F2 };
        editRecognized.Invoked += async (_, args) =>
        {
            if (busy || closed || applicationExitPending || selectedTool != PreviewTool.OcrSelect) return;
            args.Handled = true;
            await EditSelectedOcrLineAsync();
        };
        Root.KeyboardAccelerators.Add(editRecognized);
        AddShortcut(VirtualKey.F11, VirtualKeyModifiers.None, () => { ToggleFullScreen(); return Task.CompletedTask; });
        AddShortcut(VirtualKey.V, VirtualKeyModifiers.Control, PasteAsync, respectsTextInput: true);
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, () => RunActionAsync("Saving image…", SaveWithPickerAsync, commitDraft: false));
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => RunActionAsync("Saving image…", SaveWithPickerAsync, commitDraft: false));
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
            if (respectsTextInput && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or RichEditBox)
            {
                if (key == VirtualKey.C) DesktopImageActions.BeginClipboardWrite();
                return;
            }
            if (busy || closed || applicationExitPending) return;
            args.Handled = true;
            await action();
        };
        Root.KeyboardAccelerators.Add(shortcut);
    }

    private async void OnOpenClicked(object sender, RoutedEventArgs args) => await OpenAsync();
    private async void OnPasteClicked(object sender, RoutedEventArgs args) => await PasteAsync();
    private async void OnSaveClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Saving image…", SaveWithPickerAsync, commitDraft: false);
    private async void OnUndoClicked(object sender, RoutedEventArgs args) => await ChangeHistoryAsync(false);
    private async void OnRedoClicked(object sender, RoutedEventArgs args) => await ChangeHistoryAsync(true);
    private async void OnHelpClicked(object sender, RoutedEventArgs args) => await ShowHelpAsync();
    private async void OnRetryPreviewClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Rendering image preview…", async () =>
    {
        await RefreshPreviewAsync(resetView: false);
        SetStatus("Preview restored. " + ToolInstruction());
        return true;
    });
    private async void OnPanClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Pan);
    private async void OnPenClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Pen);
    private async void OnHighlighterClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Highlighter);
    private async void OnRectangleClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Rectangle);
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
            if (Viewport.IsTextEditing) Viewport.EndTextEdit();
            ResetDocumentSelection();
            AddRecentSafely(chosen.Path);
            displayPath = chosen.Path;
            suggestedName = Path.GetFileNameWithoutExtension(chosen.Path) + "-edited";
            await RefreshPreviewAsync(resetView: true);
            SetStatus("Opened " + Path.GetFileName(chosen.Path) + ". " + ToolInstruction());
            return true;
        }, commitDraft: false);
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
            if (Viewport.IsTextEditing) Viewport.EndTextEdit();
            ResetDocumentSelection();
            displayPath = null;
            suggestedName = "Snip Studio image";
            await RefreshPreviewAsync(resetView: true);
            SetStatus("Pasted image. " + ToolInstruction());
            return true;
        }, commitDraft: false);
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
        if (!session.IsDirty && !Viewport.IsTextEditing) return true;
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
            SuggestedFolder = displayPath is null ? preferences.ScreenshotFolder : Path.GetDirectoryName(displayPath),
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
        if (Viewport.IsTextEditing)
        {
            CommitTextDraft();
            await RefreshPreviewAsync(resetView: false);
        }
        SetStatus("Saving the image and finishing the file…");
        var result = await session.SaveAsync(chosen.Path, ActionToken);
        if (!result.SavedCurrentRevision)
        {
            SetStatus("Saved an earlier image revision. Your current changes remain unsaved.");
            return false;
        }
        displayPath = result.Path;
        suggestedName = Path.GetFileNameWithoutExtension(result.Path);
        AddRecentSafely(result.Path);
        SetStatus("Saved " + Path.GetFileName(result.Path) + ".");
        return true;
    }

    private async Task ChangeHistoryAsync(bool redo)
    {
        if (Viewport.IsTextEditing)
        {
            if (redo) Viewport.RedoText(); else Viewport.UndoText();
            UpdateCommands();
            return;
        }
        if (!session.HasImage || (redo ? !session.CanRedo : !session.CanUndo)) return;
        await RunActionAsync(redo ? "Redoing edit…" : "Undoing edit…", async () =>
        {
            Viewport.CancelGesture();
            if (redo) session.Redo(); else session.Undo();
            ResetDocumentSelection();
            await RefreshPreviewAsync(resetView: false);
            SetStatus((redo ? "Redid edit. " : "Undid edit. ") + ToolInstruction());
            return true;
        });
    }

    private async void OnGestureCommitted(object? sender, DrawGestureEventArgs args)
    {
        CancelPendingSnipResize();
        await RunActionAsync(args.Tool == PreviewTool.OcrSelect ? "Reading selection…" : "Rendering edit…", async () =>
        {
            long beforeRevision = session.Revision;
            await CommitGestureAsync(args);
            if (args.ReachedPointLimit) SetStatus("Long stroke kept at the preview limit. Lift the pointer, then draw again to continue.");
            else if (session.Revision != beforeRevision) SetStatus("Edit kept. " + ToolInstruction());
            return true;
        });
    }

    private async Task RefreshPreviewAsync(bool resetView, bool allowAutoCopy = true)
    {
        long sizingIntent = WindowSizingIntentVersion;
        bool copyEdit = allowAutoCopy && !resetView && !resetPreviewViewPending && displayedRevision >= 0 &&
            displayedRevision != session.Revision && preferences.AutoCopyCaptures;
        long clipboardIntent = copyEdit ? DesktopImageActions.BeginClipboardWrite() : 0;
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
        bool resizeForSnip = resetPreviewViewPending;
        Viewport.SetImage(bitmap, session.ImageWidth, session.ImageHeight, resetPreviewViewPending);
        resetPreviewViewPending = false;
        displayedRevision = preview.Revision;
        UpdateViewStatus();
        if (resizeForSnip) ScheduleResizeForSnip(sizingIntent);
        if (copyEdit) await CopyEditedPreviewAsync(preview, clipboardIntent);
    }

    private async Task<bool> RunActionAsync(string message, Func<Task<bool>> action, bool commitDraft = true)
    {
        if (busy || closed) return false;
        CancelPendingSnipResize();
        busy = true;
        actionCancellation = new CancellationTokenSource();
        Viewport.CancelGesture();
        if (session.HasImage && !PreviewIsCurrent) Viewport.ShowUnavailable();
        ErrorNotice.Title = session.HasImage && !PreviewIsCurrent ? "Preview unavailable" : "The action could not finish";
        ErrorNotice.IsOpen = false;
        SetStatus(message);
        UpdateCommands();
        try
        {
            if (commitDraft && Viewport.IsTextEditing)
            {
                CommitTextDraft();
                await RefreshPreviewAsync(resetView: false);
            }
            return await action();
        }
        catch (OperationCanceledException) { if (!closed) SetStatus("Canceled. Your current work is kept."); return false; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ReportUnexpectedFailure(exception);
            return false;
        }
        finally
        {
            busy = false;
            actionCancellation?.Dispose();
            actionCancellation = null;
            if (!closed)
            {
                UpdateCommands();
                if (Viewport.IsTextEditing) Viewport.RestoreTextFocus();
                ResumePendingSnipResize();
            }
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

    private void SetTool(PreviewTool next, bool whileBusy = false, bool revealOptions = true)
    {
        if ((busy && !whileBusy) || closed) return;
        if (selectedTool == PreviewTool.OcrSelect && next != PreviewTool.OcrSelect) ClearOcrSelection();
        selectedTool = next;
        Viewport.Tool = next;
        PanButton.IsChecked = next == PreviewTool.Pan;
        PenButton.IsChecked = next == PreviewTool.Pen;
        HighlighterButton.IsChecked = next == PreviewTool.Highlighter;
        RectangleButton.IsChecked = next == PreviewTool.Rectangle;
        SelectButton.IsChecked = next == PreviewTool.Select;
        TextButton.IsChecked = next is PreviewTool.Text or PreviewTool.OcrSelect;
        EraserButton.IsChecked = next == PreviewTool.Eraser;
        CropButton.IsChecked = next == PreviewTool.Crop;
        LineButton.IsChecked = next == PreviewTool.Line;
        ArrowButton.IsChecked = next == PreviewTool.Arrow;
        EllipseButton.IsChecked = next == PreviewTool.Ellipse;
        EmojiButton.IsChecked = next == PreviewTool.Emoji;
        RemoveAreaButton.IsChecked = next == PreviewTool.RemoveArea;
        OcrSelectButton.IsChecked = next == PreviewTool.OcrSelect;
        ShapesButton.IsChecked = next is PreviewTool.Rectangle or PreviewTool.Line or PreviewTool.Arrow or PreviewTool.Ellipse;
        contextDismissed = !revealOptions;
        UpdateToolContext();
        uint color = next == PreviewTool.Highlighter ? highlighterColor : next == PreviewTool.Text ? textColor : penColor;
        Viewport.InkArgb = color;
        changingColor = true;
        InkColorPicker.Color = Windows.UI.Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        changingColor = false;
        if (session.HasImage) SetStatus(ToolInstruction());
    }

    private void UpdateToolContext()
    {
        bool text = selectedTool == PreviewTool.Text || Viewport.IsTextEditing;
        bool textActions = selectedTool == PreviewTool.OcrSelect;
        bool ink = selectedTool is PreviewTool.Pen or PreviewTool.Highlighter or PreviewTool.Rectangle or PreviewTool.Line or PreviewTool.Arrow or PreviewTool.Ellipse;
        bool eraser = selectedTool == PreviewTool.Eraser;
        ContextBar.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        OcrContextBar.Visibility = textActions ? Visibility.Visible : Visibility.Collapsed;
        InkContextBar.Visibility = ink || text || eraser ? Visibility.Visible : Visibility.Collapsed;
        ColorButton.Visibility = ink || text ? Visibility.Visible : Visibility.Collapsed;
        StrokeWidthContainer.Visibility = ink ? Visibility.Visible : Visibility.Collapsed;
        SnapHighlighterContainer.Visibility = selectedTool == PreviewTool.Highlighter ? Visibility.Visible : Visibility.Collapsed;
        EraseAllContainer.Visibility = eraser ? Visibility.Visible : Visibility.Collapsed;
        ContextRow.Visibility = session.HasImage && !contextDismissed && (ink || text || eraser || textActions) ? Visibility.Visible : Visibility.Collapsed;
        string closeLabel = textActions ? "Close text actions" : "Close tool options";
        AutomationProperties.SetName(ContextCloseButton, closeLabel);
        ToolTipService.SetToolTip(ContextCloseButton, closeLabel);
        UpdateOcrCommands();
    }

    private void OnContextCloseClicked(object sender, RoutedEventArgs args)
    {
        if (selectedTool == PreviewTool.OcrSelect) { CloseTextActions(); return; }
        contextDismissed = true;
        UpdateToolContext();
    }

    private void OnShapesClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed) return;
        ShapesButton.IsChecked = selectedTool is PreviewTool.Rectangle or PreviewTool.Line or PreviewTool.Arrow or PreviewTool.Ellipse;
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(ShapesButton);
    }

    private void OnInkColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (changingColor || Viewport is null || busy || closed) return;
        Viewport.CancelGesture();
        var color = args.NewColor;
        uint argb = 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (selectedTool == PreviewTool.Highlighter) highlighterColor = argb;
        else if (selectedTool == PreviewTool.Text) textColor = argb;
        else penColor = argb;
        Viewport.InkArgb = argb;
        if (selectedTool == PreviewTool.Text) Viewport.FormatText(null, null, argb, null);
    }

    private void OnStrokeWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (Viewport is null || closed) return;
        Viewport.CancelGesture();
        if (double.IsFinite(args.NewValue)) Viewport.InkWidth = (float)Math.Clamp(args.NewValue, 1, 100);
    }

    private string ToolInstruction() => selectedTool switch
    {
        PreviewTool.Pan => "Move view: drag to pan. Ctrl+wheel zooms; Fit restores the view.",
        PreviewTool.Highlighter => "Highlighter: drag over the image. Escape cancels; Ctrl+Z undoes.",
        PreviewTool.Rectangle => "Rectangle: drag between two corners. Escape cancels; Ctrl+Z undoes.",
        PreviewTool.Select => "Select: click an annotation or drag it to move. Object properties resizes or changes its style; Delete removes it.",
        PreviewTool.Text => "Text: click to create or edit text. Apply text or Ctrl+Enter keeps your draft; Escape cancels it.",
        PreviewTool.Eraser => "Eraser: drag over annotations to remove them. The underlying screenshot is kept.",
        PreviewTool.Crop => "Crop: drag a rectangle, then review and apply the crop. Escape cancels.",
        PreviewTool.RemoveArea => "Remove image area: drag a rectangle and choose its replacement color before applying.",
        PreviewTool.Line => "Line: drag between the endpoints. Select and Object properties can change the endpoints.",
        PreviewTool.Arrow => "Arrow: drag from tail to arrowhead. Select and Object properties can change the endpoints.",
        PreviewTool.Ellipse => "Ellipse: drag its bounding rectangle. Object properties changes outline and fill.",
        PreviewTool.Emoji => $"Click to place a {activeEmoji?.DisplayName ?? "selected"} sticker. Use Move to adjust it.",
        PreviewTool.OcrSelect => "Text actions: click a line to edit, or drag across words to select and copy.",
        _ => "Pen: drag to draw. Middle-drag pans; Ctrl+wheel zooms."
    };

    private void ChangeView(Action action)
    {
        if (busy || closed || !PreviewIsCurrent) return;
        CancelPendingSnipResize();
        action();
        UpdateViewStatus();
    }

    private Task ChangeViewAsync(Action action) { ChangeView(action); return Task.CompletedTask; }

    private void UpdateCommands()
    {
        if (closed) return;
        bool enabled = !busy && !applicationExitPending;
        FileBar.IsEnabled = enabled;
        CaptureBar.IsEnabled = enabled;
        ToolBar.IsEnabled = enabled;
        ToolBar.Visibility = session.HasImage ? Visibility.Visible : Visibility.Collapsed;
        UpdateWindowMinimums();
        ContextBar.IsEnabled = enabled;
        InkContextBar.IsEnabled = enabled;
        ContextCloseButton.IsEnabled = enabled;
        HelpButton.IsEnabled = enabled;
        Viewport.IsEnabled = enabled && PreviewIsCurrent;
        Viewport.Visibility = session.HasImage ? Visibility.Visible : Visibility.Collapsed;
        LauncherHint.Visibility = session.HasImage ? Visibility.Collapsed : Visibility.Visible;
        ZoomPanel.Visibility = session.HasImage ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !busy && session.HasImage;
        SaveButton.Visibility = CopyButton.Visibility = ShareButton.Visibility = PrintButton.Visibility =
            ReloadExternalButton.Visibility = session.HasImage ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = !busy && (Viewport.IsTextEditing ? Viewport.TextCanUndo : session.CanUndo);
        RedoButton.IsEnabled = !busy && (Viewport.IsTextEditing ? Viewport.TextCanRedo : session.CanRedo);
        OcrButton.IsEnabled = !busy && session.HasImage;
        OcrButton.IsChecked = OcrSelectButton.IsChecked = selectedTool == PreviewTool.OcrSelect;
        CopyButton.IsEnabled = ShareButton.IsEnabled = PrintButton.IsEnabled = !busy && session.HasImage;
        ObjectPropertiesButton.IsEnabled = !busy && selectedAnnotationIndex >= 0;
        DeleteObjectButton.IsEnabled = !busy && (selectedAnnotationIndex >= 0 || selectedTool == PreviewTool.OcrSelect && ocrSelection?.Text.Length > 0);
        EraseAllButton.IsEnabled = !busy && session.HasImage && session.AnnotationCount > 0;
        UpdateTextDraftCommands();
        UpdateToolContext();
        ZoomOutButton.IsEnabled = FitButton.IsEnabled = ActualSizeButton.IsEnabled = ZoomInButton.IsEnabled = !busy && PreviewIsCurrent;
        RetryPreviewButton.Visibility = session.HasImage && !PreviewIsCurrent ? Visibility.Visible : Visibility.Collapsed;
        RetryPreviewButton.IsEnabled = !busy;
        if (!busy && session.HasImage && !PreviewIsCurrent) Viewport.ShowUnavailable();
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelActionButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        string filename = displayPath is null ? "" : " — " + Path.GetFileName(displayPath);
        Title = (session.IsDirty ? "• " : "") + "Snip Studio" + filename;
        UpdateViewStatus();
    }

    private void UpdateViewStatus()
    {
        if (closed || ZoomText is null) return;
        DimensionsText.Text = session.HasImage ? $"{session.ImageWidth:N0} × {session.ImageHeight:N0} px" : "";
        ZoomText.Text = !PreviewIsCurrent ? "—" : (Viewport.IsFit ? "Fit · " : "") + Viewport.ZoomPercent.ToString("0.#") + "%";
        ActualSizeButton.Content = !PreviewIsCurrent ? "100%" : Viewport.ZoomPercent.ToString("0.#") + "%";
        if (ActualSizeButton.Flyout is MenuFlyout presets)
            foreach (var item in presets.Items.OfType<ToggleMenuFlyoutItem>())
                item.IsChecked = PreviewIsCurrent && item.Tag is string value && int.TryParse(value, out int percent) && Math.Abs(Viewport.ZoomPercent - percent) < 0.05;
    }

    private void OnZoomPresetClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string value } || !int.TryParse(value, out int percent) || percent <= 0) return;
        ChangeView(() =>
        {
            if (percent == 100) Viewport.ActualPixels();
            else Viewport.ZoomBy(percent / Viewport.ZoomPercent);
        });
    }

    private void SetStatus(string message)
    {
        if (closed) return;
        StatusText.Text = message;
        ToolTipService.SetToolTip(StatusText, message);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ZoomPanel is null) return;
        UpdateWindowMinimums();
        FileBar.MaxWidth = Math.Clamp(args.NewSize.Width - 368, 44, 228);
        DelayButton.Visibility = args.NewSize.Width < 440 ? Visibility.Collapsed : Visibility.Visible;
        CaptureOptionsButton.Visibility = args.NewSize.Width < 380 ? Visibility.Collapsed : Visibility.Visible;
        CaptureMoreButton.Visibility = args.NewSize.Width < 440 ? Visibility.Visible : Visibility.Collapsed;
        CaptureModeGroup.Visibility = args.NewSize.Width < 300 ? Visibility.Collapsed : Visibility.Visible;
        bool narrow = args.NewSize.Width < 560;
        Grid.SetRow(ZoomPanel, narrow ? 1 : 0);
        Grid.SetColumn(ZoomPanel, narrow ? 0 : 1);
        Grid.SetColumnSpan(ZoomPanel, narrow ? 2 : 1);
        Grid.SetColumnSpan(StatusInfo, narrow ? 2 : 1);
        // Bound the status line so it cannot push zoom actions off a compact window.
        StatusText.MaxWidth = Math.Max(80, args.NewSize.Width - (narrow ? 64 : 340));
        DimensionsText.Visibility = args.NewSize.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
        ActualSizeButton.Visibility = args.NewSize.Width < 320 ? Visibility.Collapsed : Visibility.Visible;
        ZoomText.Visibility = Visibility.Collapsed;
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
                        Text = "Snip Studio · Automation Forge\n\nChoose photo or video mode, then New. Photo mode offers rectangle, freeform, window, full screen, all monitors and scrolling capture. Video mode selects a rectangle or window first, then shows audio choices and Start recording. This capture workflow saves a unique MP4 in the recording folder from Settings; Stop and save opens the video for review, trimming and export. Open video review is a separate file-menu action. Photo mode offers Off or a timed capture delay beside the shape. The timer is unavailable in video mode. Escape cancels selection and keeps the previous snip; the scrolling controller also offers Finish to keep collected frames.\n\nDraw with Pen, Highlighter, Line, Arrow, Rectangle or Ellipse. Highlighter can snap to recognized words; hold Shift for freehand. Select moves annotations. Object properties changes geometry, shape outline/fill, or emoji size/rotation. Crop and Remove image area show a review before applying. Eraser removes annotations while keeping screenshot pixels. Undo restores edits.\n\nText creates or edits an annotation on the canvas. Select text inside its editor to change font, image-pixel size, color, bold, italic or underline. Ctrl+Enter applies the draft; Escape cancels it. Text actions works directly on the snip. Click a recognized line to edit it, or drag across words and choose Copy selection. F2 edits a selected line. Done closes Text actions and leaves the Text tool selected. The Text actions menu offers Convert to text and Review recognized text. Conversion keeps recognized strings in order and estimates their original style. Review identifiers, quantities and duplicate rows for OCR errors. Translation is offline by default; a configured server receives text only after you choose it and click Translate. Review translated rows before applying them.\n\nUse Fit to center the image in the visible canvas. At 100%, one image pixel maps to one physical display pixel. Move view or middle-drag pans. Wheel pans and Ctrl+wheel zooms unless changed in Settings. Controls that do not fit move into the toolbar's overflow menu.\n\nCtrl+N: New in selected photo/video mode\nCtrl+Shift+N: Choose recording area\nCtrl+O: Open image\nCtrl+V: Paste\nCtrl+C: Copy selected text or image\nCtrl+Shift+C: Recognize and copy all text\nCtrl+S: Save as\nCtrl+P: Print\nCtrl+Z: Undo\nCtrl+Y or Ctrl+Shift+Z: Redo\nCtrl+F: Fit\nCtrl+0: 100%\nCtrl+plus/minus: Zoom\nDelete: Remove selection\nF11: Full screen\n\nThe file menu includes recent files, Share, Print, signed external-editor copies, new editor windows, Settings and release information. Optional notification-area, Windows startup and global shortcuts are explicit Settings choices for this WinUI build. Stable Snip Studio settings remain separate. Global shortcuts, when enabled, are Ctrl+Alt+Shift+S/R.\n\nSaving produces flattened PNG, JPEG, BMP or PDF files. Keep the editor open to retain editable annotation history; the exported file does not contain project objects. Your images are processed locally unless you deliberately Share or choose a configured translation server."
                    }
                }
            };
            await dialog.ShowAsync();
            SetStatus(session.HasImage ? ToolInstruction() : "Open an image or paste one from the clipboard.");
            return true;
        }, commitDraft: false);
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        if (applicationExitPending) { args.Cancel = true; return; }
        if (busy || closePending)
        {
            args.Cancel = true;
            SetStatus(session.IsSaving ? "Saving is still finishing. Keep this window open until it completes." : "Finish or cancel the current action before closing.");
            return;
        }
        if (TryHideToTray()) { args.Cancel = true; return; }
        if (!session.IsDirty && !Viewport.IsTextEditing) return;
        args.Cancel = true;
        closePending = true;
        try
        {
            bool confirmed = await RunActionAsync("Review unsaved changes…", ConfirmReplaceAsync, commitDraft: false);
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
        actionCancellation?.Cancel();
        actionCancellation?.Dispose();
        actionCancellation = null;
        Viewport.CancelGesture();
        DisposeDesktopIntegration();
        session.Dispose();
    }

    private void OnCancelActionClicked(object sender, RoutedEventArgs args)
    {
        actionCancellation?.Cancel();
        SetStatus("Cancel requested. A native dialog or file operation may need to finish first.");
    }
}
