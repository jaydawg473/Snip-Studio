using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;
using Windows.ApplicationModel.DataTransfer;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private OcrSnapshot? ocrSnapshot;
    private OcrSelectionSnapshot? ocrSelection;
    private readonly OfflineTranslationAdapter translation = new();

    private async void OnOcrClicked(object sender, RoutedEventArgs args) => await RecognizeAsync(copyOnly: false);

    private async Task RecognizeAsync(bool copyOnly, bool selectOnly = false)
    {
        if (!session.HasImage) { SetStatus("Capture, open, or paste an image before extracting text."); return; }
        await RunActionAsync("Recognizing text…", async () =>
        {
            if (ocrSnapshot is null || !session.IsOcrSnapshotCurrent(ocrSnapshot))
                ocrSnapshot = await session.RecognizeTextAsync(ActionToken);
            OcrSnapshot result = ocrSnapshot;
            if (!result.Available) throw new InvalidOperationException(result.Message.Length == 0 ? "Text recognition is unavailable for this image." : result.Message);
            if (copyOnly)
            {
                SetClipboardText(result.Text);
                SetStatus("Recognized text copied exactly as returned. Review identifiers and quantities before using it.");
                return true;
            }
            if (selectOnly)
            {
                SetTool(PreviewTool.OcrSelect, whileBusy: true);
                SetStatus(ToolInstruction()); return true;
            }
            var rows = new ListView { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true,
                ItemsSource = result.Lines, DisplayMemberPath = "Text", MaxHeight = 240, MinHeight = 100 };
            var raw = new TextBox { Text = result.RawText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160 };
            var recovered = new TextBox { Text = result.RecoveryRawText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = "Choose rows to convert, or leave every row unchecked to convert all. OCR text is unverified: compare identifiers and quantities with the original image. Conversion keeps the recognized strings verbatim and estimates source font appearance.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(rows);
            panel.Children.Add(new TextBlock { Text = "Original recognition output", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(raw);
            if (result.RecoveryRawText.Length > 0) { panel.Children.Add(new TextBlock { Text = "Additional recognition pass" }); panel.Children.Add(recovered); }
            var translateButton = new Button { Content = "Translate selected rows…" };
            panel.Children.Add(translateButton);
            bool translateRequested = false;
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = $"Image to text · {result.Lines.Count} rows", Content = new ScrollViewer { Content = panel, MaxHeight = 510 },
                PrimaryButtonText = "Convert to editable text", SecondaryButtonText = "Copy text", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            translateButton.Click += (_, _) => { translateRequested = true; dialog.Hide(); };
            ContentDialogResult answer = await dialog.ShowAsync();
            int[] selected = rows.SelectedItems.Cast<OcrLineSnapshot>().Select(line => line.Index).OrderBy(index => index).ToArray();
            if (translateRequested)
            {
                await ShowTranslationAsync(result, selected.Length == 0 ? result.Lines.Select(line => line.Index).ToArray() : selected);
                return true;
            }
            if (answer == ContentDialogResult.Secondary)
            {
                SetClipboardText(selected.Length == 0 ? result.Text : string.Join(Environment.NewLine, result.Lines.Where(line => selected.Contains(line.Index)).Select(line => line.Text)));
                SetStatus("Recognized text copied. No source text was changed."); return true;
            }
            if (answer != ContentDialogResult.Primary) { SetStatus("Text extraction closed. Your image is kept."); return false; }
            SetStatus("Matching source text color, size and position…");
            OcrConversionResult converted = await session.ConvertRecognizedTextAsync(result, selected.Length == 0 ? null : selected, ActionToken);
            if (!converted.Applied) throw new InvalidOperationException(converted.Message);
            selectedAnnotationIndex = converted.LastAnnotationIndex;
            ocrSelection = null; Viewport.SetRecognizedBands(null);
            await RefreshPreviewAsync(resetView: false);
            SetTool(PreviewTool.Text, whileBusy: true);
            UpdateSelection();
            SetStatus(converted.Message + " Text tool selected. Click a text object to edit it.");
            return true;
        });
    }

    private static void SetClipboardText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
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
