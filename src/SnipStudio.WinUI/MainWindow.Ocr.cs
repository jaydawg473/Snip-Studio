using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;
using Windows.ApplicationModel.DataTransfer;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private OcrSnapshot? ocrSnapshot;
    private OcrSelectionSnapshot? ocrSelection;
    private OcrLineEditSnapshot? ocrLineDraft;
    private bool ocrTextNavigation;
    private readonly OfflineTranslationAdapter translation = new();

    private async void OnOcrClicked(object sender, RoutedEventArgs args)
    {
        if (selectedTool == PreviewTool.OcrSelect) CloseTextActions();
        else await RecognizeAsync(copyOnly: false);
    }

    private async Task RecognizeAsync(bool copyOnly)
    {
        if (!session.HasImage) { SetStatus("Capture, open, or paste an image before extracting text."); return; }
        if (busy || closed) return;
        long intent = copyOnly ? DesktopImageActions.BeginClipboardWrite() : 0;
        bool recognized = await RunActionAsync("Recognizing text…", async () =>
        {
            if (!copyOnly)
            {
                selectedAnnotationIndex = -1;
                UpdateSelection();
                SetTool(PreviewTool.OcrSelect, whileBusy: true);
                SetStatus("Recognizing text…");
            }
            OcrSnapshot result = await GetCurrentRecognitionAsync();
            if (copyOnly)
            {
                if (!SetClipboardText(result.Text, intent))
                {
                    SetStatus("Copy skipped because a newer clipboard action is active.");
                    return false;
                }
                SetStatus("All recognized text copied. Review identifiers and quantities for possible misreads.");
                return true;
            }
            ocrTextNavigation = true;
            SetStatus(ToolInstruction());
            return true;
        }, commitDraft: !copyOnly);
        if (recognized && !copyOnly && !closed) Viewport.Focus(FocusState.Programmatic);
    }

    private OcrSnapshot? CurrentRecognition() => ocrSnapshot is { Available: true } snapshot && session.IsOcrSnapshotCurrent(snapshot) ? snapshot : null;

    private async Task<OcrSnapshot> GetCurrentRecognitionAsync()
    {
        if (CurrentRecognition() is OcrSnapshot current) return current;
        ClearOcrSelection();
        OcrSnapshot result = await session.RecognizeTextAsync(ActionToken);
        if (!result.Available || string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidOperationException(result.Message.Length == 0 ? "No text was detected. Try a closer, sharper snip." : result.Message);
        ocrSnapshot = result;
        return result;
    }

    private void ClearOcrSelection()
    {
        ocrSelection = null;
        Viewport.SetRecognizedBands(null);
    }

    private void CloseTextActions()
    {
        if (busy || closed || selectedTool != PreviewTool.OcrSelect) return;
        SetTool(PreviewTool.Text, revealOptions: false);
        Viewport.Focus(FocusState.Programmatic);
        SetStatus("Text actions closed. The Text tool is selected.");
    }

    private void OnCloseOcrClicked(object sender, RoutedEventArgs args) => CloseTextActions();

    private async void OnCopyAllOcrClicked(object sender, RoutedEventArgs args) => await RecognizeAsync(copyOnly: true);

    private async void OnCopySelectedOcrClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Copying selected text…", () =>
    {
        if (CurrentRecognition() is null || ocrSelection is not { Text.Length: > 0 } selection) return Task.FromResult(false);
        SetClipboardText(selection.Text);
        SetStatus("Selected text copied.");
        return Task.FromResult(true);
    });

    private async void OnEditOcrClicked(object sender, RoutedEventArgs args) => await EditSelectedOcrLineAsync();

    private Task EditSelectedOcrLineAsync()
    {
        if (selectedTool != PreviewTool.OcrSelect || CurrentRecognition() is not OcrSnapshot source || ocrSelection?.LineIndices is not { Count: 1 } lines)
            return Task.CompletedTask;
        return RunActionAsync("Matching source text…", async () =>
        {
            await EditOcrLineAsync(source, lines[0]);
            return true;
        });
    }

    private async Task EditOcrLineAsync(OcrSnapshot source, int lineIndex, ImagePoint? caretPoint = null)
    {
        OcrLineEditSnapshot edit = await session.PrepareRecognizedLineEditAsync(source, lineIndex, ActionToken);
        AnnotationSnapshot? existing = edit.ExistingAnnotationIndex >= 0 ? session.GetAnnotation(edit.ExistingAnnotationIndex) : null;
        int selectionStart = ocrSelection is { SelectionLength: > 0 } selection ? selection.SelectionStart : edit.Text.Length;
        int selectionLength = caretPoint.HasValue ? 0 : ocrSelection?.SelectionLength ?? 0;
        ClearOcrSelection();
        SetTool(PreviewTool.Text, whileBusy: true);
        if (existing?.Text is { } existingText)
        {
            BeginTextDraft(existingText.Location, existingText.Location, existing);
            if (string.Equals(existingText.Text, source.Lines[lineIndex].Text, StringComparison.Ordinal))
                Viewport.SelectInlineText(selectionStart, selectionLength);
            SetStatus("Editing the existing text. Choose Done or press Ctrl+Enter to keep it.");
            return;
        }
        BeginTextDraft(new TextEditDraft(-1, edit.Text, edit.Rtf, edit.FontFamily, edit.FontStyle, edit.Location,
            edit.Argb, edit.FontSize, edit.LayoutWidth, 0), edit, source.Lines[lineIndex]);
        if (caretPoint is not ImagePoint point || !Viewport.PlaceSourceTextCaret(point))
            Viewport.SelectInlineText(selectionStart, selectionLength);
        SetStatus("Edit this line. Choose Done or press Ctrl+Enter to keep it. Escape cancels.");
    }

    private async void OnConvertOcrClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Matching source text color, size and position…", async () =>
        {
            OcrSnapshot result = await GetCurrentRecognitionAsync();
            var selected = ocrSelection?.LineIndices;
            var confirm = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = selected is { Count: > 0 } ? "Convert selected lines?" : "Convert all recognized text?",
                Content = "The recognized strings become editable on the image. Review any misreads. Snip Studio estimates the original font and color. Undo restores the original image.",
                PrimaryButtonText = "Convert", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) { SetStatus(ToolInstruction()); return false; }
            SetStatus("Matching source text color, size and position…");
            OcrConversionResult converted = await session.ConvertRecognizedTextAsync(result, selected, ActionToken);
            if (!converted.Applied) throw new InvalidOperationException(converted.Message);
            selectedAnnotationIndex = converted.LastAnnotationIndex;
            ClearOcrSelection();
            await RefreshPreviewAsync(resetView: false);
            SetTool(PreviewTool.Text, whileBusy: true);
            UpdateSelection();
            SetStatus(converted.Message + " Text tool selected. Click a text object to edit it.");
            return true;
        });

    private async void OnTranslateOcrClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Opening text translation…", async () =>
        {
            OcrSnapshot source = await GetCurrentRecognitionAsync();
            var selected = ocrSelection?.LineIndices;
            await ShowTranslationAsync(source, selected is { Count: > 0 } ? selected : source.Lines.Select(line => line.Index).ToArray());
            return true;
        });

    private async void OnReviewOcrClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Opening recognized text…", async () =>
        {
            OcrSnapshot source = await GetCurrentRecognitionAsync();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBox { Header = "Recognized text", Text = source.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 210 });
            if (!string.Equals(source.RawText, source.Text, StringComparison.Ordinal))
                panel.Children.Add(new TextBox { Header = "Original recognition output", Text = source.RawText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 130 });
            if (source.RecoveryRawText.Length > 0)
                panel.Children.Add(new TextBox { Header = "Additional recognition output", Text = source.RecoveryRawText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 130 });
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Text from your snip", Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                PrimaryButtonText = "Copy all text", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                SetClipboardText(source.Text);
                SetStatus("All recognized text copied.");
            }
            else SetStatus(ToolInstruction());
            return true;
        });

    private void UpdateOcrCommands()
    {
        if (OcrContextBar is null || closed) return;
        bool active = selectedTool == PreviewTool.OcrSelect && !busy && !applicationExitPending;
        bool selected = active && CurrentRecognition() is not null && ocrSelection is { Text.Length: > 0 };
        OcrContextBar.IsEnabled = active;
        CopySelectedOcrButton.IsEnabled = DeleteSelectedOcrButton.IsEnabled = selected;
        EditOcrButton.IsEnabled = selected && ocrSelection?.LineIndices.Count == 1;
    }

    private IReadOnlyList<OcrBounds> PreviewOcrSelection(ImagePoint start, ImagePoint end) =>
        CurrentRecognition() is OcrSnapshot snapshot ? session.PreviewRecognizedText(snapshot, start, end) : [];

    private async Task CommitOcrGestureAsync(DrawGestureEventArgs gesture)
    {
        OcrSnapshot source = await GetCurrentRecognitionAsync();
        ocrSelection = session.SelectRecognizedText(source, gesture.Points[0], gesture.Points[^1]);
        Viewport.SetRecognizedBands(ocrSelection.Bands);
        if (!gesture.IsDrag && ocrSelection.LineIndices is { Count: 1 } lines)
        {
            SetStatus("Matching source text…");
            await EditOcrLineAsync(source, lines[0], gesture.Points[0]);
            return;
        }
        SetStatus(ocrSelection.Text.Length == 0 ? ToolInstruction() : "Text selected. Choose Copy selection or press Ctrl+C.");
    }

    private async Task<bool> TryNavigateRecognizedTextAsync(ImagePoint point)
    {
        if (!ocrTextNavigation || ocrSnapshot is not OcrSnapshot previous)
            return false;
        if (session.GetAnnotation(session.HitTest(point, 3))?.Text is not null)
            return false;
        // Check the earlier ink footprint before refreshing a stale OCR result.
        // Clicking blank canvas with Text still starts a new text note.
        bool touched = previous.Lines.Any(line => line.Words.Any(word =>
            point.X >= word.Bounds.X && point.X < word.Bounds.X + word.Bounds.Width &&
            point.Y >= word.Bounds.Y && point.Y < word.Bounds.Y + word.Bounds.Height));
        if (!touched) return false;
        OcrSnapshot source = await GetCurrentRecognitionAsync();
        ocrSelection = session.SelectRecognizedText(source, point, point);
        if (ocrSelection.LineIndices is not { Count: 1 } lines) return false;
        SetStatus("Matching source text…");
        await EditOcrLineAsync(source, lines[0], point);
        return true;
    }

    private static bool SetClipboardText(string text, long? intent = null)
    {
        if (!DesktopImageActions.IsCurrentClipboardWrite(intent ?? DesktopImageActions.BeginClipboardWrite())) return false;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        return true;
    }

    private async Task ShowTranslationAsync(OcrSnapshot source, IReadOnlyList<int> indices)
    {
        IReadOnlyList<TranslationLanguageOption> languages = await translation.GetLanguagesAsync(ActionToken);
        IReadOnlyList<TranslationLanguageOption> offlineLanguages = languages;
        var provider = new ComboBox { Header = "Translation provider", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        provider.Items.Add("Offline on this PC"); provider.Items.Add("Configured LibreTranslate server"); provider.SelectedIndex = 0;
        var endpoint = new TextBox { Header = "Server HTTPS address", PlaceholderText = "https://your-server.example/", Visibility = Visibility.Collapsed };
        var apiKey = new PasswordBox { Header = "Server API key (kept only in this dialog)", Visibility = Visibility.Collapsed };
        var serverLanguages = new Button { Content = "Load server languages", Visibility = Visibility.Collapsed };
        var disclosure = new TextBlock { Text = "The selected text will be sent to this server only when you click Translate. Check the address before continuing.", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var sourceLanguage = new ComboBox { Header = "Source language", DisplayMemberPath = "Name", ItemsSource = languages, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var targetLanguage = new ComboBox { Header = "Translate to", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        void SetTargets()
        {
            if (sourceLanguage.SelectedItem is not TranslationLanguageOption selected) return;
            targetLanguage.ItemsSource = languages.Where(language => selected.Targets.Count == 0 ? language.Code != selected.Code : selected.Targets.Contains(language.Code)).ToArray();
            targetLanguage.SelectedIndex = 0;
        }
        sourceLanguage.SelectionChanged += (_, _) => SetTargets(); SetTargets();
        string[] segments = source.Lines.Where(line => indices.Contains(line.Index)).Select(line => line.Text).ToArray();
        var original = new TextBox { Header = "Source text", Text = string.Join(Environment.NewLine, segments), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
        var output = new TextBox { Header = "Review translation · one row per source row", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Offline translation preserves technical identifiers. Review the result before copying it." };
        var run = new Button { Content = "Translate offline" };
        var stop = new Button { Content = "Cancel translation", IsEnabled = false };
        var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(provider); panel.Children.Add(endpoint); panel.Children.Add(apiKey); panel.Children.Add(disclosure); panel.Children.Add(serverLanguages); panel.Children.Add(sourceLanguage); panel.Children.Add(targetLanguage); panel.Children.Add(original); panel.Children.Add(run); panel.Children.Add(stop); panel.Children.Add(feedback); panel.Children.Add(output);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Translate recognized text", Content = new ScrollViewer { Content = panel, MaxHeight = 500 }, PrimaryButtonText = "Apply reviewed translation", SecondaryButtonText = "Copy translation", IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false, CloseButtonText = "Close" };
        bool running = false;
        bool dialogEnded = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ActionToken);
        CancellationTokenSource? cancel = null;
        Task? active = null;
        bool applying = false;
        void SetInputsEnabled(bool enabled)
        {
            sourceLanguage.IsEnabled = targetLanguage.IsEnabled = provider.IsEnabled = endpoint.IsEnabled = apiKey.IsEnabled = serverLanguages.IsEnabled = run.IsEnabled = output.IsEnabled = enabled;
            stop.IsEnabled = !enabled;
        }
        provider.SelectionChanged += (_, _) =>
        {
            bool remote = provider.SelectedIndex == 1;
            endpoint.Visibility = apiKey.Visibility = serverLanguages.Visibility = disclosure.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            run.Content = remote ? "Translate using this server" : "Translate offline";
            if (!remote) { languages = offlineLanguages; sourceLanguage.ItemsSource = languages; sourceLanguage.SelectedIndex = 0; SetTargets(); }
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
        };
        stop.Click += (_, _) => cancel?.Cancel();
        serverLanguages.Click += (_, _) =>
        {
            if (running || applying || dialogEnded) return;
            active = LoadServerLanguagesAsync();
        };
        async Task LoadServerLanguagesAsync()
        {
            running = true;
            cancel?.Dispose(); cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            SetInputsEnabled(false);
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            try
            {
                if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out Uri? address)) throw new ArgumentException("Enter an absolute HTTPS server address.");
                using var remote = new ConfiguredTranslationAdapter(address, apiKey.Password);
                var response = await remote.GetLanguagesAsync(cancel.Token);
                if (dialogEnded || lifetime.IsCancellationRequested) return;
                languages = response;
                sourceLanguage.ItemsSource = languages; sourceLanguage.SelectedIndex = 0; SetTargets();
                feedback.Text = "Server language list loaded. Clicking Translate will send the source text to " + address.GetLeftPart(UriPartial.Authority) + ".";
            }
            catch (OperationCanceledException) { if (!dialogEnded) feedback.Text = "Language list request canceled."; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { if (!dialogEnded) feedback.Text = exception.Message; }
            finally { running = false; if (!dialogEnded) SetInputsEnabled(true); }
        }
        run.Click += (_, _) =>
        {
            if (running || applying || dialogEnded || sourceLanguage.SelectedItem is not TranslationLanguageOption from || targetLanguage.SelectedItem is not TranslationLanguageOption to) return;
            active = RunTranslationAsync(from.Code, to.Code);
        };
        async Task RunTranslationAsync(string from, string to)
        {
            cancel?.Dispose(); cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            running = true; SetInputsEnabled(false);
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            feedback.Text = provider.SelectedIndex == 1 ? "Sending selected text to the configured server…" : "Translating locally…";
            try
            {
                TranslationSnapshot result;
                if (provider.SelectedIndex == 1)
                {
                    if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out Uri? address)) throw new ArgumentException("Enter an absolute HTTPS server address.");
                    using var remote = new ConfiguredTranslationAdapter(address, apiKey.Password);
                    result = await remote.TranslateAsync(segments, from, to, cancel.Token);
                }
                else result = await translation.TranslateAsync(segments, from, to, cancel.Token);
                if (dialogEnded || lifetime.IsCancellationRequested) return;
                output.Text = result.Text; feedback.Text = "Translation ready. Review each row before applying it to the image. Source recognition remains available.";
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
            }
            catch (OperationCanceledException) { if (!dialogEnded) feedback.Text = "Translation canceled."; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { if (!dialogEnded) feedback.Text = exception.Message; }
            finally { running = false; if (!dialogEnded) SetInputsEnabled(true); }
        }
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            if (running || applying || dialogEnded) { args.Cancel = true; deferral.Complete(); return; }
            applying = true;
            SetInputsEnabled(false);
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            try
            {
                string[] reviewed = output.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n');
                if (reviewed.Length != segments.Length) throw new ArgumentException($"Keep exactly {segments.Length} translation rows so every source row has its own reviewed replacement.");
                cancel?.Dispose(); cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                feedback.Text = "Fitting reviewed translation into the source text regions…";
                OcrConversionResult applied = await session.ApplyReviewedTranslationAsync(source, indices, reviewed, cancel.Token);
                if (!applied.Applied) throw new InvalidOperationException(applied.Message);
                selectedAnnotationIndex = applied.LastAnnotationIndex; ocrSelection = null; Viewport.SetRecognizedBands(null);
                await RefreshPreviewAsync(resetView: false); SetTool(PreviewTool.Text, whileBusy: true); UpdateSelection();
                SetStatus(applied.Message + " Text tool selected.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { args.Cancel = true; feedback.Text = exception.Message + " The reviewed translation remains in this dialog."; }
            finally
            {
                applying = false;
                if (args.Cancel) { SetInputsEnabled(true); dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true; }
                else stop.IsEnabled = false;
                deferral.Complete();
            }
        };
        dialog.Closing += (_, args) => { if (applying) args.Cancel = true; else { dialogEnded = true; lifetime.Cancel(); } };
        ContentDialogResult answer = await dialog.ShowAsync();
        cancel?.Cancel();
        if (active is not null) await active;
        cancel?.Dispose(); apiKey.Password = "";
        if (answer == ContentDialogResult.Secondary && output.Text.Length > 0) { SetClipboardText(output.Text); SetStatus("Reviewed translation copied. Source text is kept."); }
    }
}
