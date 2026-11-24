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
    private bool closePromptOpen;
    private string suggestedName = "Snip Studio image";
    private string? displayPath;
    private ImageSaveTarget? currentImageSaveTarget;
    private uint penColor = 0xFFE24046, highlighterColor = 0xFFFFE33B, textColor = 0xFF000000;
    private PreviewTool selectedTool = PreviewTool.Pen;
    private bool contextDismissed;
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
        Viewport.SnappedHighlightPreview = PreviewSnappedHighlight;
        Viewport.SnapHighlighterToText = SnapHighlighterButton.IsChecked == true;
        SnapHighlighterButton.Checked += OnSnapHighlighterChanged;
        SnapHighlighterButton.Unchecked += OnSnapHighlighterChanged;
        Viewport.RecognizedSelectionDismissed += (_, _) => CloseTextActions();
        Viewport.ProbeAnnotation = (point, radius) =>
            !busy && PreviewIsCurrent ? session.HitTestMovableAnnotation(point, radius) : null;
        Viewport.ProbeAnnotationOutline = (point, radius) =>
            !busy && PreviewIsCurrent ? session.HitTestAnnotationOutline(point, radius) : null;
        Viewport.ConstrainAnnotationMove = session.ConstrainAnnotationMove;
        Viewport.PrepareAnnotationDragPreview = session.RenderAnnotationDragPreviewAsync;
        Viewport.AnnotationSelectionChanged += OnViewportAnnotationSelectionChanged;
        Viewport.SelectionTransformCommitted += OnSelectionTransformCommitted;
        Viewport.CropFrameChanged += (_, _) => UpdateCropCommands();
        Viewport.CropFrameCanceled += (_, _) => CancelCrop();
        Viewport.CropFrameApplyRequested += async (_, _) => await ApplyCropAsync();
        Viewport.GestureFeedback += (_, message) => SetStatus(message);
        Viewport.TextApplyRequested += async (_, _) => await ApplyTextDraftAsync();
        Viewport.TextCancelRequested += (_, _) => CancelTextDraft();
        Viewport.DraftChanged += (_, _) => UpdateTextDraftCommands();
        Viewport.ViewChanged += (_, _) => UpdateViewStatus();
        Root.ActualThemeChanged += (_, _) => RefreshInkToolIcons();
        ConfigureAccelerators();
        SetTool(PreviewTool.Pen);
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
        var editText = new KeyboardAccelerator { Key = VirtualKey.F2 };
        editText.Invoked += async (_, args) =>
        {
            if (busy || closed || applicationExitPending || Viewport.IsTextEditing) return;
            args.Handled = true;
            if (selectedTool == PreviewTool.OcrSelect) await EditSelectedOcrLineAsync();
            else await EditSelectedTextAnnotationAsync();
        };
        Root.KeyboardAccelerators.Add(editText);
        AddShortcut(VirtualKey.F11, VirtualKeyModifiers.None, () => { ToggleFullScreen(); return Task.CompletedTask; });
        AddShortcut(VirtualKey.V, VirtualKeyModifiers.Control, PasteAsync, respectsTextInput: true);
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control, () => RunActionAsync("Saving…", SavePreferredPathAsync, commitDraft: false));
        AddShortcut(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => RunActionAsync("Saving image…", SaveWithPickerAsync, commitDraft: false));
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control, () => ChangeHistoryAsync(false), respectsTextInput: true);
        AddShortcut(VirtualKey.Y, VirtualKeyModifiers.Control, () => ChangeHistoryAsync(true), respectsTextInput: true);
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ChangeHistoryAsync(true), respectsTextInput: true);
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => ChangeViewAsync(Viewport.Fit));
        AddShortcut(VirtualKey.Number0, VirtualKeyModifiers.Control, () => ChangeViewAsync(Viewport.ActualPixels));
        AddShortcut(VirtualKey.Add, VirtualKeyModifiers.Control, () => ChangeViewAsync(() => Viewport.ZoomBy(1.1)));
        AddShortcut((VirtualKey)187, VirtualKeyModifiers.Control, () => ChangeViewAsync(() => Viewport.ZoomBy(1.1)), respectsTextInput: true);
        AddShortcut((VirtualKey)187, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => ChangeViewAsync(() => Viewport.ZoomBy(1.1)), respectsTextInput: true);
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
    private async void OnSaveClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Saving…", SavePreferredPathAsync, commitDraft: false);
    private async void OnSaveAsClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Saving image…", SaveWithPickerAsync, commitDraft: false);
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
    private void OnFitClicked(object sender, RoutedEventArgs args) => ChangeView(Viewport.Fit);
    private void OnActualSizeClicked(object sender, RoutedEventArgs args) => ChangeView(Viewport.ActualPixels);
    private void OnZoomInClicked(object sender, RoutedEventArgs args) => ChangeView(() => Viewport.ZoomBy(1.1));
    private void OnZoomOutClicked(object sender, RoutedEventArgs args) => ChangeView(() => Viewport.ZoomBy(1 / 1.1));

    private async Task OpenAsync()
    {
        await RunActionAsync("Choose an image or editable snip…", async () =>
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "Open"
            };
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(extension);
            picker.FileTypeFilter.Add(EditableProjectExtension);
            var chosen = await picker.PickSingleFileAsync();
            if (chosen is null) { SetStatus("Open canceled. Your current image is kept."); return false; }
            return await OpenPathCoreAsync(chosen.Path);
        }, commitDraft: false);
    }

    private async Task PasteAsync()
    {
        await RunActionAsync("Reading clipboard image…", async () =>
        {
            CancellationToken cancellationToken = ActionToken;
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
            try { bytes = await read.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken); }
            catch (TimeoutException)
            {
                throw new TimeoutException("The clipboard did not respond within eight seconds. Your current image is kept.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ConfirmReplaceAsync()) return false;
            await session.OpenImageBytesAsync(bytes, cancellationToken);
            if (Viewport.IsTextEditing) Viewport.EndTextEdit();
            ResetDocumentSelection();
            displayPath = null;
            currentImageSaveTarget = null;
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

    private bool ShouldPromptToSave(bool applicationExit = false) =>
        Viewport.IsTextEditing ||
        session.IsDirty && (applicationExit || displayPath is null || IsEditableProjectPath(displayPath) || preferences.AskSaveEditedScreenshots);

    private Task<bool> ConfirmReplaceAsync() => ConfirmSaveChangesAsync(ShouldPromptToSave());

    private Task<bool> ConfirmApplicationExitAsync() => ConfirmSaveChangesAsync(ShouldPromptToSave(applicationExit: true), revealOwner: true);

    private async Task<bool> ConfirmSaveChangesAsync(bool shouldPrompt, bool revealOwner = false)
    {
        if (!shouldPrompt) return true;
        if (revealOwner)
        {
            // Let Windows finish dispatching the close request before activating its owner.
            await Task.Yield();
            if (closed) return false;
            RevealClosePromptOwner();
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Save changes to this snip?",
            Content = "Save your changes, discard them, or cancel to keep editing.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        ContentDialogResult answer;
        closePromptOpen = revealOwner;
        try { answer = await dialog.ShowAsync(); }
        finally { closePromptOpen = false; }
        ActionToken.ThrowIfCancellationRequested();
        if (answer == ContentDialogResult.Secondary) return true;
        if (answer != ContentDialogResult.Primary) { SetStatus("Canceled. Your changes are kept."); return false; }
        return await SavePreferredPathAsync();
    }

    private async Task<bool> SavePreferredPathAsync()
    {
        if (!session.HasImage) { SetStatus("Open or paste an image before saving."); return false; }
        if (displayPath is { } editablePath && IsEditableProjectPath(editablePath))
            return await SaveEditableProjectToPathAsync(editablePath);
        if (currentImageSaveTarget is not { } target ||
            !string.Equals(target.Path, displayPath, StringComparison.OrdinalIgnoreCase))
            return await SaveWithPickerAsync();
        if (!session.IsDirty && !Viewport.IsTextEditing) { SetStatus("The image is already saved."); return true; }
        return await SaveImageToTargetAsync(target);
    }

    private async Task<bool> SaveWithPickerAsync()
    {
        if (!session.HasImage) { SetStatus("Open or paste an image before saving."); return false; }
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFolder = Directory.Exists(preferences.LastSaveFolder)
                ? preferences.LastSaveFolder
                : displayPath is null ? preferences.ScreenshotFolder : Path.GetDirectoryName(displayPath),
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
        ActionToken.ThrowIfCancellationRequested();
        string path = chosen.Path;
        ImageSaveTarget target = await EditorSession.PrepareSaveTargetAsync(path, ActionToken);
        bool saved = await SaveImageToTargetAsync(target);
        if (saved && Path.GetDirectoryName(path) is { } folder) RememberImageSaveFolder(folder);
        return saved;
    }

    private async Task<bool> SaveImageToTargetAsync(ImageSaveTarget target)
    {
        CancellationToken cancellationToken = ActionToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (Viewport.IsTextEditing)
        {
            CommitTextDraft();
            await RefreshPreviewAsync(resetView: false);
        }
        SetStatus("Saving the image and finishing the file…");
        bool exportFromProject = displayPath is { } path && IsEditableProjectPath(path);
        var result = await (exportFromProject
            ? session.ExportPreparedAsync(target, cancellationToken)
            : session.SavePreparedAsync(target, cancellationToken));
        if (!result.SavedCurrentRevision)
        {
            SetStatus("Saved an earlier image revision. Your current changes remain unsaved.");
            return false;
        }
        AddRecentSafely(result.Path);
        if (exportFromProject)
        {
            SetStatus("Exported " + Path.GetFileName(result.Path) + ".");
            return true;
        }
        displayPath = result.Path;
        currentImageSaveTarget = result.SavedTarget;
        suggestedName = Path.GetFileNameWithoutExtension(result.Path);
        SetStatus("Saved " + Path.GetFileName(result.Path) + ".");
        return true;
    }

    private void RememberImageSaveFolder(string folder)
    {
        preferences.LastSaveFolder = folder;
        try
        {
            new DesktopPreferencesStore().RememberLastSaveFolder(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            SetStatus("Image saved. The last-used folder could not be remembered. " + error.Message);
        }
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
            else if (session.Revision != beforeRevision)
                SetStatus(args.Tool.IsShapeTool() ? "Shape added. Use the round handle to rotate or choose Width to change its outline." :
                    "Edit kept. " + ToolInstruction());
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
            Viewport.SetImage(bitmap, session.ImageWidth, session.ImageHeight, resetPreviewViewPending, preview.Revision);
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
        if (selectedTool == PreviewTool.Crop && next != PreviewTool.Crop) Viewport.ClearCropFrame();
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
        RemoveAreaButton.IsChecked = next == PreviewTool.RemoveArea;
        OcrSelectButton.IsChecked = next == PreviewTool.OcrSelect;
        UpdateShapeToolIcon(next);
        contextDismissed = !revealOptions;
        UpdateToolContext();
        uint color = next == PreviewTool.Highlighter ? highlighterColor : next == PreviewTool.Text ? textColor : penColor;
        Viewport.InkArgb = EffectiveInk(color, next);
        changingColor = true;
        InkColorPicker.Color = Windows.UI.Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        changingColor = false;
        RestoreToolStrokeWidth();
        SyncInkStrokeControls();
        RefreshInkToolIcons();
        if (session.HasImage) SetStatus(ToolInstruction());
    }

    private void UpdateToolContext()
    {
        bool text = selectedTool == PreviewTool.Text || Viewport.IsTextEditing;
        bool textActions = selectedTool == PreviewTool.OcrSelect;
        bool ink = HasStrokeWidth;
        bool eraser = selectedTool == PreviewTool.Eraser;
        bool move = selectedTool == PreviewTool.Select && !text;
        bool drawShape = selectedTool.IsShapeTool() && !text;
        AnnotationSnapshot? selection = move || drawShape ? session.GetAnnotation(selectedAnnotationIndex) : null;
        bool objectControls = move || selection is { Shape: not null } or { Emoji: not null };
        bool crop = selectedTool == PreviewTool.Crop && !text;
        ContextBar.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        OcrContextBar.Visibility = textActions ? Visibility.Visible : Visibility.Collapsed;
        ObjectContextBar.Visibility = objectControls ? Visibility.Visible : Visibility.Collapsed;
        CropContextBar.Visibility = crop ? Visibility.Visible : Visibility.Collapsed;
        InkContextBar.Visibility = ink || text || eraser ? Visibility.Visible : Visibility.Collapsed;
        ColorButton.Visibility = ink || text ? Visibility.Visible : Visibility.Collapsed;
        StrokeWidthContainer.Visibility = ink ? Visibility.Visible : Visibility.Collapsed;
        DrawLinePatternContainer.Visibility = drawShape ? Visibility.Visible : Visibility.Collapsed;
        ShowLinePattern(DrawLinePatternButton, drawingLinePattern);
        SnapHighlighterContainer.Visibility = selectedTool == PreviewTool.Highlighter ? Visibility.Visible : Visibility.Collapsed;
        InlineStrokePreviewContainer.Visibility = selectedTool is PreviewTool.Pen or PreviewTool.Highlighter ? Visibility.Visible : Visibility.Collapsed;
        EraseAllContainer.Visibility = eraser ? Visibility.Visible : Visibility.Collapsed;
        ContextRow.Visibility = session.HasImage && !contextDismissed && (ink || text || eraser || textActions || objectControls || crop) ? Visibility.Visible : Visibility.Collapsed;
        string closeLabel = textActions ? "Close text actions" : crop ? "Cancel crop" : "Close tool options";
        AutomationProperties.SetName(ContextCloseButton, closeLabel);
        SetWorkspaceToolTip(ContextCloseButton, closeLabel);
        UpdateObjectCommands(selection, move);
        UpdateCropCommands();
        UpdateOcrCommands();
    }

    private void OnContextCloseClicked(object sender, RoutedEventArgs args)
    {
        if (selectedTool == PreviewTool.OcrSelect) { CloseTextActions(); return; }
        if (selectedTool == PreviewTool.Crop) { CancelCrop(); return; }
        contextDismissed = true;
        UpdateToolContext();
    }

    private void OnShapesClicked(object sender, RoutedEventArgs args)
    {
        if (busy || closed) return;
        ShapesButton.IsChecked = selectedTool.IsShapeTool();
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(ShapesButton);
    }

    private void OnInkColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (changingColor || Viewport is null || busy || closed) return;
        var color = args.NewColor;
        uint argb = 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        ApplyInk(argb);
        unrecordedInk = (argb, selectedTool);
    }

    private string ToolInstruction() => selectedTool switch
    {
        PreviewTool.Pan => "Move view: drag to pan. Ctrl+wheel zooms; Fit restores the view.",
        PreviewTool.Highlighter => "Highlighter: drag over the image. Escape cancels; Ctrl+Z undoes.",
        PreviewTool.Rectangle => "Rectangle: drag between two corners. Escape cancels; Ctrl+Z undoes.",
        PreviewTool.Select => "Move: grab an object at the four-way arrow. Drag a corner to resize or the round handle to rotate. Arrow keys nudge.",
        PreviewTool.Text => "Text: click to create or edit text. Apply text or Ctrl+Enter keeps your draft; Escape cancels it.",
        PreviewTool.Eraser => "Eraser: click or drag over annotations to remove them. Release to apply. Ctrl+Z undoes the gesture.",
        PreviewTool.Crop => "Crop: drag the edges or corners inward, then choose Apply crop. Escape cancels.",
        PreviewTool.RemoveArea => "Remove image area: drag a rectangle and choose its replacement color before applying.",
        PreviewTool.Line => "Line: drag between the endpoints. Select and Object properties can change the endpoints.",
        PreviewTool.Arrow => "Arrow: drag from tail to arrowhead. Select and Object properties can change the endpoints.",
        PreviewTool.Ellipse => "Ellipse: drag its bounding rectangle. Object properties changes outline and fill.",
        PreviewTool.OcrSelect => "Click a line to edit. Drag over words or start on blank space to select an area.",
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
        UpdateAnnotationToolbarState(enabled);
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
        SaveAsButton.IsEnabled = SaveEditableProjectButton.IsEnabled = RedactForSharingButton.IsEnabled = SaveButton.IsEnabled;
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

    private async void OnCustomZoomClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Set a zoom percentage…", async () =>
        {
            if (!PreviewIsCurrent) return false;
            var value = new NumberBox
            {
                Header = "Zoom (%)",
                Value = Math.Round(Viewport.ZoomPercent, 1),
                SmallChange = 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Custom zoom",
                Content = value,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, click) => click.Cancel = !double.IsFinite(value.Value) || value.Value <= 0;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
            Viewport.ZoomBy(value.Value / Viewport.ZoomPercent);
            UpdateViewStatus();
            return true;
        }, commitDraft: false);

    private void SetStatus(string message)
    {
        if (closed) return;
        StatusText.Text = message;
        SetWorkspaceToolTip(StatusText, message);
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
        RotateLeftButton.LabelPosition = RotateRightButton.LabelPosition = args.NewSize.Width < 660
            ? CommandBarLabelPosition.Collapsed : CommandBarLabelPosition.Default;
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
        await RunActionAsync("Opening Help…", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"Snip Studio {DesktopDialogs.ApplicationVersion}",
                CloseButtonText = "Close",
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Snip Studio · Automation Forge\n\nChoose photo or video mode, then New. Photo mode offers rectangle, freeform, window, full screen, all monitors and scrolling capture. Video mode selects a rectangle or window first, then shows audio choices and Start recording. This capture workflow saves a unique MP4 in the recording folder from Settings; Stop and save opens the video for review, trimming and export. Open video review is a separate file-menu action. Photo mode offers Off or a timed capture delay beside the shape. The timer is unavailable in video mode. Escape cancels selection and keeps the previous snip; the scrolling controller also offers Finish to keep collected frames.\n\nDraw with Pen, Highlighter, Line, Arrow, Rectangle or Ellipse. Highlighter can snap to recognized words; hold Shift for freehand. Hover over a placed outline or stroke to see the four-way arrow and drag it while drawing. The Move tool also selects object interiors and shows resize and rotation handles. Drag the round handle or use Rotate left and Rotate right to turn shapes and stickers. Solid, Dashed, and Dotted are available while drawing a shape and when editing its line style. The color picker shows the stroke with its current width and opacity. Choosing an emoji adds it to the visible snip. Crop begins with the whole image selected. Drag an edge or corner and choose Apply crop. Eraser removes annotations while keeping screenshot pixels. Undo restores edits.\n\nText creates or edits an annotation on the canvas. Select text inside its editor to change font, image-pixel size, color, bold, italic or underline. Ctrl+Enter applies the draft; Escape cancels it. Text actions works directly on the snip. Click a recognized line to edit it, or drag across words and choose Copy selection. F2 edits a selected line. Done closes Text actions and leaves the Text tool selected. The Text actions menu offers Convert to text, Edit text together, and Review recognized text. Edit text together lets you correct selected or all recognized rows in one editor and apply them in place. Conversion keeps recognized strings in order and estimates their original style. Review identifiers, quantities and duplicate rows for OCR errors. Translation is offline by default; a configured server receives text only after you choose it and click Translate. Review translated rows before applying them.\n\nUse Fit to center the image in the visible canvas. At 100%, one image pixel maps to one physical display pixel. Move view or middle-drag pans. Wheel pans and Ctrl+wheel zooms unless changed in Settings. Controls that do not fit move into the toolbar's overflow menu.\n\nCtrl+N: New in selected photo/video mode\nCtrl+Shift+N: Choose recording area\nCtrl+O: Open image or editable snip\nCtrl+V: Paste\nCtrl+C: Copy selected text or image\nCtrl+Shift+C: Recognize and copy all text\nCtrl+S: Save\nCtrl+Shift+S: Save image as\nCtrl+P: Print\nCtrl+Z: Undo\nCtrl+Y or Ctrl+Shift+Z: Redo\nCtrl+F: Fit\nCtrl+0: 100%\nCtrl+plus/minus: Zoom\nDelete: Remove selection\nF11: Full screen\n\nThe file menu includes recent files, Share, Print, signed external-editor copies, new editor windows, Settings and release information. Optional notification-area, Windows startup and global shortcuts are explicit Settings choices for this WinUI build. Stable Snip Studio settings remain separate. Global shortcuts, when enabled, are Ctrl+Alt+Shift+S/R.\n\nSaving produces flattened PNG, JPEG, BMP or PDF files. Choose Save editable snip to reopen the cropped image and continue editing annotations. Redact for sharing lets you preview exactly what will be copied or saved. Your images are processed locally unless you deliberately Share or choose a configured translation server."
                    }
                }
            };
            await DesktopDialogs.ShowBoundedAsync(dialog, 480);
            SetStatus(session.HasImage ? ToolInstruction() : "Open an image or paste one from the clipboard.");
            return true;
        }, commitDraft: false);
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        if (closePromptOpen)
        {
            args.Cancel = true;
            await Task.Yield();
            if (!closed && closePromptOpen) RevealClosePromptOwner();
            return;
        }
        if (applicationExitPending) { args.Cancel = true; return; }
        if (busy || closePending)
        {
            args.Cancel = true;
            SetStatus(session.IsSaving ? "Saving is still finishing. Keep this window open until it completes." : "Finish or cancel the current action before closing.");
            return;
        }
        if (TryHideToTray()) { args.Cancel = true; return; }
        if (!ShouldPromptToSave())
        {
            args.Cancel = !PrepareShortcutListenerForClose();
            return;
        }
        args.Cancel = true;
        closePending = true;
        try
        {
            bool confirmed = await RunActionAsync("Review unsaved changes…",
                () => ConfirmSaveChangesAsync(ShouldPromptToSave(), revealOwner: true), commitDraft: false);
            if (confirmed && !closed && PrepareShortcutListenerForClose()) { allowClose = true; Close(); }
        }
        finally { closePending = false; }
    }

    private void RevealClosePromptOwner()
    {
        AppWindow window = AppWindow;
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore(false);
        if (!window.IsVisible) window.Show(false);
        window.MoveInZOrderAtTop();
        Activate();
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
