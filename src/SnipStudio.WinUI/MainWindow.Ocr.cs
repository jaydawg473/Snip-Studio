using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private OcrSnapshot? ocrSnapshot;
    private OcrSelectionSnapshot? ocrSelection;
    private OcrLineEditSnapshot? ocrLineDraft;
    private bool ocrTextNavigation;
    private readonly OfflineTranslationAdapter translation = new();
    private Window? translationWindow;

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
                SetStatus(result.HasUncertainClippedText
                    ? "Recognized text copied. A clipped label could not be read reliably."
                    : "All recognized text copied. Review identifiers and quantities for possible misreads.");
                return true;
            }
            ocrTextNavigation = true;
            SetStatus(result.HasUncertainClippedText ? result.Message : ToolInstruction());
            return true;
        }, commitDraft: !copyOnly);
        if (recognized && !copyOnly && !closed) Viewport.Focus(FocusState.Programmatic);
    }

    private OcrSnapshot? CurrentRecognition() => ocrSnapshot is { Available: true } snapshot && session.IsOcrSnapshotCurrent(snapshot) ? snapshot : null;

    private async Task<OcrSnapshot> GetCurrentRecognitionAsync()
    {
        if (CurrentRecognition() is OcrSnapshot current) return current;
        ClearOcrSelection();
        bool showGlow = selectedTool == PreviewTool.OcrSelect;
        try
        {
            if (showGlow) Viewport.StartTextActionsGlow();
            OcrSnapshot result = await session.RecognizeTextAsync(ActionToken);
            if (!result.Available || string.IsNullOrWhiteSpace(result.Text))
                throw new InvalidOperationException(result.Message.Length == 0 ? "No text was detected. Try a closer, sharper snip." : result.Message);
            ocrSnapshot = result;
            UpdateOcrCommands();
            return result;
        }
        finally
        {
            if (showGlow) Viewport.EndTextActionsGlow();
        }
    }

    private void ClearOcrSelection()
    {
        ocrSelection = null;
        Viewport.SetRecognizedBands(null);
        if (CurrentRecognition() is null) Viewport.SetRecognizedTextHighlights(null);
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
        bool copied = SetClipboardText(selection.Text);
        SetStatus(copied ? "Selected text copied." : "Copy skipped because a newer clipboard action is active.");
        return Task.FromResult(copied);
    });

    private async void OnEditOcrClicked(object sender, RoutedEventArgs args) => await EditSelectedOcrLineAsync();

    private Task EditSelectedOcrLineAsync()
    {
        if (selectedTool != PreviewTool.OcrSelect || CurrentRecognition() is not OcrSnapshot source || ocrSelection?.LineIndices is not { Count: 1 } lines)
            return Task.CompletedTask;
        return RunActionAsync("Matching source text…", () => EditOcrLineAsync(source, lines[0]));
    }

    private async Task<bool> EditOcrLineAsync(OcrSnapshot source, int lineIndex, ImagePoint? caretPoint = null)
    {
        OcrLineEditSnapshot? edit = await session.PrepareRecognizedLineEditAsync(source, lineIndex, ActionToken);
        if (edit is null)
        {
            SetStatus(selectedTool == PreviewTool.OcrSelect
                ? "This line crosses an editable note. Click the note to edit it or choose Copy selection."
                : "This line crosses an editable note. Click the note itself to edit it.");
            return false;
        }

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
            return true;
        }
        BeginTextDraft(new TextEditDraft(-1, edit.Text, edit.Rtf, edit.FontFamily, edit.FontStyle, edit.Location,
            edit.Argb, edit.FontSize, edit.LayoutWidth, 0), edit, source.Lines[lineIndex]);
        if (caretPoint is not ImagePoint point || !Viewport.PlaceSourceTextCaret(point))
            Viewport.SelectInlineText(selectionStart, selectionLength);
        SetStatus("Edit this line. Choose Done or press Ctrl+Enter to keep it. Escape cancels.");
        return true;
    }

    private async void OnConvertOcrClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Matching source text color, size and position…", async () =>
        {
            OcrTextReview review = await GetRecognitionReviewAsync();
            bool hasSelection = ocrSelection is { Text.Length: > 0 };
            var confirm = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = hasSelection ? "Convert selected text?" : "Convert all recognized text?",
                Content = "The recognized strings become editable on the image. Review any misreads. Snip Studio estimates the original font and color. Undo restores the original image.",
                PrimaryButtonText = "Convert", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) { SetStatus(ToolInstruction()); return false; }
            SetStatus("Matching source text color, size and position…");
            OcrConversionResult converted = await session.ConvertRecognizedTextReviewAsync(review, ActionToken);
            if (!converted.Applied) throw new InvalidOperationException(converted.Message);
            await ShowAppliedOcrReviewAsync(converted);
            return true;
        });

    private async void OnTranslateOcrClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Opening text translation…", async () =>
        {
            if (translationWindow is Window existing) { existing.Activate(); return true; }
            OcrTextReview review = await GetRecognitionReviewAsync();
            await ShowTranslationAsync(review);
            return true;
        });

    private async void OnReviewOcrClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Opening recognized text…", async () =>
        {
            OcrSnapshot source = await GetCurrentRecognitionAsync();
            var panel = new StackPanel { Spacing = 8 };
            AddReviewTextBox("Recognized text", source.Text, 210);
            if (!string.Equals(source.RawText.ReplaceLineEndings("\r\n"), source.Text, StringComparison.Ordinal) &&
                !string.Equals(source.RawText, source.Text.Replace("\r\n", " ", StringComparison.Ordinal), StringComparison.Ordinal))
                AddReviewTextBox("Original recognition output", source.RawText, 130);
            if (source.RecoveryRawText.Length > 0)
                AddReviewTextBox("Additional recognition output", source.RecoveryRawText, 130);
            void AddReviewTextBox(string header, string text, double height)
            {
                var box = new TextBox { Header = header, Text = text, IsReadOnly = true,
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = height };
                DesktopTextClipboard.KeepSelectionLocal(box, SetStatus);
                panel.Children.Add(box);
            }
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Text from your snip", Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                PrimaryButtonText = "Copy all text", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                bool copied = SetClipboardText(source.Text);
                SetStatus(copied ? "All recognized text copied." : "Copy skipped because a newer clipboard action is active.");
                return copied;
            }
            else SetStatus(ToolInstruction());
            return true;
        });

    private void UpdateOcrCommands()
    {
        if (OcrContextBar is null || closed) return;
        bool textActions = selectedTool == PreviewTool.OcrSelect && !applicationExitPending;
        OcrSnapshot? recognition = textActions ? CurrentRecognition() : null;
        Viewport.SetRecognizedTextHighlights(PreviewIsCurrent ? recognition?.Lines : null);
        bool active = textActions && !busy;
        bool selected = active && recognition is not null && ocrSelection is { Text.Length: > 0 };
        OcrContextBar.IsEnabled = active;
        EditOcrSelectionButton.IsEnabled = active;
        CopySelectedOcrButton.IsEnabled = DeleteSelectedOcrButton.IsEnabled = selected;
        EditOcrButton.IsEnabled = selected && ocrSelection?.LineIndices.Count == 1;
    }

    private IReadOnlyList<OcrBounds> PreviewOcrSelection(ImagePoint start, ImagePoint end) =>
        CurrentRecognition() is OcrSnapshot snapshot ? session.PreviewRecognizedWordSelection(snapshot, start, end) : [];

    private async Task CommitOcrGestureAsync(DrawGestureEventArgs gesture)
    {
        if (!gesture.IsDrag && TryOpenTextNoteAt(gesture.Points[0])) return;

        OcrSnapshot source = await GetCurrentRecognitionAsync();
        ocrSelection = session.SelectRecognizedText(source, gesture.Points[0], gesture.Points[^1]);
        Viewport.SetRecognizedBands(ocrSelection.WordBands);
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

    private static bool SetClipboardText(string text, long? intent = null) => DesktopTextClipboard.Copy(text, intent);

    private async Task ShowTranslationAsync(OcrTextReview review)
    {
        if (review.Lines.Count > 256)
        {
            SetStatus("Select between 1 and 256 recognized lines to translate.");
            return;
        }

        int[] sourceLineIndices = review.Lines.Select(line => line.LineIndex).ToArray();
        string[] segments = review.Lines.Select(line => line.Text).ToArray();
        IReadOnlyList<TranslationLanguageOption> languages = await translation.GetLanguagesAsync(ActionToken);
        if (closed) return;
        IReadOnlyList<TranslationLanguageOption> offlineLanguages = languages;
        DesktopPreferences saved = new DesktopPreferencesStore().Load();
        string rememberedFrom = saved.TranslationSourceLanguage, rememberedTo = saved.TranslationTargetLanguage;
        var provider = new ComboBox { Header = "Translation provider", HorizontalAlignment = HorizontalAlignment.Stretch };
        provider.Items.Add("Offline on this PC");
        provider.Items.Add("Configured LibreTranslate server");
        var endpoint = new TextBox { Header = "Server HTTPS address", Text = saved.TranslationServerAddress, PlaceholderText = "https://your-server.example/", Visibility = Visibility.Collapsed };
        var apiKey = new PasswordBox { Header = "Server API key (kept only in this dialog)", Visibility = Visibility.Collapsed };
        var serverLanguages = new Button { Content = "Load server languages", Visibility = Visibility.Collapsed };
        var disclosure = new TextBlock { Text = "The selected text will be sent to this server only when you click Translate. Check the address before continuing.", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var sourceLanguage = new ComboBox { Header = "Source language", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var targetLanguage = new ComboBox { Header = "Translate to", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        void SetTargets(string? preferredCode = null)
        {
            if (sourceLanguage.SelectedItem is not TranslationLanguageOption selected) return;
            string preferred = preferredCode ?? (targetLanguage.SelectedItem as TranslationLanguageOption)?.Code ?? rememberedTo;
            var targets = languages.Where(language => selected.Targets.Count == 0 ? language.Code != selected.Code : selected.Targets.Contains(language.Code)).ToArray();
            targetLanguage.ItemsSource = targets;
            targetLanguage.SelectedItem = targets.FirstOrDefault(language => string.Equals(language.Code, preferred, StringComparison.OrdinalIgnoreCase))
                ?? targets.FirstOrDefault();
        }
        void SetLanguages(IReadOnlyList<TranslationLanguageOption> available)
        {
            languages = available;
            sourceLanguage.ItemsSource = available;
            sourceLanguage.SelectedItem = available.FirstOrDefault(language => string.Equals(language.Code, rememberedFrom, StringComparison.OrdinalIgnoreCase))
                ?? available.FirstOrDefault();
            SetTargets(rememberedTo);
        }
        sourceLanguage.SelectionChanged += (_, _) => SetTargets();
        SetLanguages(languages);
        var feedback = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "Translate, then review each result beside the original text."
        };
        var headings = ComparisonColumns();
        headings.Children.Add(new TextBlock { Text = "Line", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var originalHeading = new TextBlock { Text = "Original text", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetColumn(originalHeading, 1);
        headings.Children.Add(originalHeading);
        var translationHeading = new TextBlock { Text = "Translation", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetColumn(translationHeading, 2);
        headings.Children.Add(translationHeading);
        var comparison = new StackPanel { Spacing = 8 };
        comparison.Children.Add(headings);
        var rows = new (Grid Layout, TextBlock Label, TextBox Original, TextBox Translation)[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            int lineNumber = sourceLineIndices[i] + 1;
            var label = new TextBlock { Text = lineNumber.ToString(), Margin = new Thickness(0, 6, 0, 0) };
            var original = new TextBox
            {
                Text = segments[i], IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalAlignment = VerticalAlignment.Top
            };
            var reviewed = new TextBox
            {
                AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 180, VerticalAlignment = VerticalAlignment.Top
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(original, $"Original line {lineNumber}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reviewed, $"Translation for line {lineNumber}");
            DesktopTextClipboard.KeepSelectionLocal(original, message => feedback.Text = message);
            DesktopTextClipboard.KeepSelectionLocal(reviewed, message => feedback.Text = message);
            var row = ComparisonColumns();
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.RowDefinitions.Add(new() { Height = GridLength.Auto });
            row.Children.Add(label);
            row.Children.Add(original);
            row.Children.Add(reviewed);
            comparison.Children.Add(row);
            rows[i] = (row, label, original, reviewed);
        }

        var run = new Button { Content = "Translate offline" };
        var stop = new Button { Content = "Cancel", IsEnabled = false };
        var copyOriginal = new Button { Content = "Copy original" };
        copyOriginal.Click += (_, _) =>
        {
            try { if (SetClipboardText(string.Join(Environment.NewLine, segments))) feedback.Text = "Original text copied."; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { feedback.Text = exception.Message; }
        };
        var languageChoices = TwoColumns();
        languageChoices.Children.Add(sourceLanguage);
        Grid.SetColumn(targetLanguage, 1);
        languageChoices.Children.Add(targetLanguage);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(run);
        actions.Children.Add(stop);
        var panel = new StackPanel { Spacing = 10 };
        foreach (UIElement element in new UIElement[] { provider, endpoint, apiKey, disclosure, serverLanguages, languageChoices, actions, copyOriginal, comparison })
            panel.Children.Add(element);
        var scroll = new ScrollViewer
        {
            Content = panel, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var apply = new Button { Content = "Apply to snip", IsEnabled = false };
        var copyTranslation = new Button { Content = "Copy translation", IsEnabled = false };
        var close = new Button { Content = "Close" };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(apply);
        footer.Children.Add(copyTranslation);
        footer.Children.Add(close);
        var layout = new Grid { RowSpacing = 10, Padding = new Thickness(20) };
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(scroll);
        Grid.SetRow(feedback, 1);
        layout.Children.Add(feedback);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Window window = CreateTextToolWindow("Snip Studio — Translate recognized text", layout);
        translationWindow = window;
        bool? stacked = null;
        layout.SizeChanged += (_, args) =>
        {
            double width = args.NewSize.Width - layout.Padding.Left - layout.Padding.Right;
            actions.Orientation = width < 380 ? Orientation.Vertical : Orientation.Horizontal;
            footer.Orientation = width < 380 ? Orientation.Vertical : Orientation.Horizontal;
            bool stack = width < 480;
            if (stacked == stack) return;
            stacked = stack;
            headings.Visibility = stack ? Visibility.Collapsed : Visibility.Visible;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                row.Layout.RowSpacing = stack ? 4 : 0;
                int lineNumber = sourceLineIndices[i] + 1;
                row.Label.Text = stack ? $"Line {lineNumber}" : lineNumber.ToString();
                row.Original.Header = stack ? "Original text" : null;
                row.Translation.Header = stack ? "Translation" : null;
                Grid.SetColumn(row.Original, stack ? 0 : 1);
                Grid.SetColumn(row.Translation, stack ? 0 : 2);
                Grid.SetColumnSpan(row.Label, stack ? 3 : 1);
                Grid.SetColumnSpan(row.Original, stack ? 3 : 1);
                Grid.SetColumnSpan(row.Translation, stack ? 3 : 1);
                Grid.SetRow(row.Original, stack ? 1 : 0);
                Grid.SetRow(row.Translation, stack ? 2 : 0);
            }
        };

        static Grid TwoColumns()
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        static Grid ComparisonColumns()
        {
            Grid grid = TwoColumns();
            grid.ColumnDefinitions.Insert(0, new() { Width = new GridLength(42) });
            return grid;
        }

        bool running = false;
        bool dialogEnded = false;
        bool hasResult = false;
        bool applying = false;
        string[]? translationBaseline = null;
        pendingTranslationReview = () => running || applying ||
            hasResult && translationBaseline is not null &&
            !rows.Select(row => row.Translation.Text).SequenceEqual(translationBaseline, StringComparer.Ordinal);
        var lifetime = new CancellationTokenSource();
        CancellationTokenSource? cancel = null;
        Task? active = null;
        void SetInputsEnabled(bool enabled)
        {
            sourceLanguage.IsEnabled = targetLanguage.IsEnabled = provider.IsEnabled = endpoint.IsEnabled = apiKey.IsEnabled = serverLanguages.IsEnabled = run.IsEnabled = enabled;
            foreach (var row in rows) row.Translation.IsReadOnly = !enabled || !hasResult;
            stop.IsEnabled = !enabled;
        }
        provider.SelectionChanged += (_, _) =>
        {
            bool remote = provider.SelectedIndex == 1;
            endpoint.Visibility = apiKey.Visibility = serverLanguages.Visibility = disclosure.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            run.Content = remote ? "Translate using this server" : "Translate offline";
            if (!remote) SetLanguages(offlineLanguages);
            hasResult = false;
            foreach (var row in rows) row.Translation.IsReadOnly = true;
            apply.IsEnabled = copyTranslation.IsEnabled = false;
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
            apply.IsEnabled = copyTranslation.IsEnabled = false;
            try
            {
                if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out Uri? address)) throw new ArgumentException("Enter an absolute HTTPS server address.");
                using var remote = new ConfiguredTranslationAdapter(address, apiKey.Password);
                var response = await remote.GetLanguagesAsync(cancel.Token);
                if (dialogEnded || lifetime.IsCancellationRequested) return;
                SetLanguages(response);
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
            apply.IsEnabled = copyTranslation.IsEnabled = false;
            feedback.Text = provider.SelectedIndex == 1 ? "Sending selected text to the configured server…" : "Translating locally…";
            try
            {
                TranslationSnapshot result;
                string? validatedEndpoint = null;
                if (provider.SelectedIndex == 1)
                {
                    if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out Uri? address)) throw new ArgumentException("Enter an absolute HTTPS server address.");
                    using var remote = new ConfiguredTranslationAdapter(address, apiKey.Password);
                    result = await remote.TranslateAsync(segments, from, to, cancel.Token);
                    validatedEndpoint = remote.Endpoint.AbsoluteUri;
                }
                else result = await translation.TranslateAsync(segments, from, to, cancel.Token);
                if (dialogEnded || lifetime.IsCancellationRequested) return;
                if (!result.SourceSegments.SequenceEqual(segments, StringComparer.Ordinal) || result.TranslatedSegments.Count != rows.Length)
                    throw new InvalidOperationException("The translation does not match the selected source lines. No rows were changed.");
                for (int i = 0; i < rows.Length; i++)
                    rows[i].Translation.Text = result.TranslatedSegments[i];
                translationBaseline = rows.Select(row => row.Translation.Text).ToArray();
                rememberedFrom = from;
                rememberedTo = to;
                bool remembered = await Task.Run(() => TryRememberTranslationChoices(from, to, validatedEndpoint));
                if (dialogEnded || lifetime.IsCancellationRequested) return;
                hasResult = true;
                feedback.Text = "Review or edit the translations before applying them to the image." +
                    (remembered ? "" : " The language choices could not be saved.");
                apply.IsEnabled = copyTranslation.IsEnabled = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (dialogEnded || !comparison.IsLoaded) return;
                    Grid firstComparison = headings.Visibility == Visibility.Visible ? headings : rows[0].Layout;
                    firstComparison.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = false, VerticalAlignmentRatio = 0
                    });
                });
            }
            catch (OperationCanceledException) { if (!dialogEnded) feedback.Text = "Translation canceled."; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { if (!dialogEnded) feedback.Text = exception.Message; }
            finally { running = false; if (!dialogEnded) SetInputsEnabled(true); }
        }
        apply.Click += async (_, _) =>
        {
            if (running || applying || dialogEnded || !hasResult) return;
            if (!CanApplyRecognizedReview(review, out string reason))
            {
                feedback.Text = reason;
                return;
            }
            applying = true;
            bool committed = false;
            SetInputsEnabled(false);
            apply.IsEnabled = copyTranslation.IsEnabled = close.IsEnabled = false;
            await RunActionAsync("Applying reviewed translation…", async () =>
            {
                try
                {
                    string[] reviewed = rows.Select(row => row.Translation.Text).ToArray();
                    cancel?.Dispose();
                    cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ActionToken);
                    feedback.Text = "Fitting reviewed translation into the selected source text…";
                    OcrConversionResult applied = await session.ApplyReviewedTranslationAsync(review, reviewed, cancel.Token);
                    if (!applied.Applied || applied.ConvertedLineCount == 0)
                    {
                        feedback.Text = applied.Message;
                        return false;
                    }
                    committed = true;
                    await ShowAppliedOcrReviewAsync(applied);
                    return true;
                }
                catch (Exception exception) when (!committed && exception is not OutOfMemoryException and not AccessViolationException)
                {
                    if (!dialogEnded) feedback.Text = exception is OperationCanceledException ? "Canceled. The reviewed translation is still here." : exception.Message;
                    return false;
                }
            }, commitDraft: false);
            applying = false;
            if (dialogEnded) return;
            if (committed) window.Close();
            else
            {
                SetInputsEnabled(true);
                apply.IsEnabled = copyTranslation.IsEnabled = close.IsEnabled = true;
            }
        };
        copyTranslation.Click += (_, _) =>
        {
            if (running || applying || dialogEnded || !hasResult) return;
            try
            {
                if (SetClipboardText(string.Join(Environment.NewLine, rows.Select(row => row.Translation.Text))))
                    feedback.Text = "Reviewed translation copied.";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { feedback.Text = exception.Message; }
        };
        close.Click += (_, _) => window.Close();
        window.AppWindow.Closing += (_, args) => args.Cancel = applying && !closed;
        window.Closed += async (_, _) =>
        {
            translationWindow = null;
            pendingTranslationReview = null;
            dialogEnded = true;
            lifetime.Cancel();
            if (active is not null) await active;
            cancel?.Dispose();
            lifetime.Dispose();
            apiKey.Password = "";
        };
        provider.SelectedIndex = saved.UseConfiguredTranslationServer && !string.IsNullOrWhiteSpace(endpoint.Text) ? 1 : 0;
        SetStatus("Translation is open in a separate window.");
        window.Activate();
    }

    private static bool TryRememberTranslationChoices(string from, string to, string? validatedEndpoint)
    {
        try
        {
            var store = new DesktopPreferencesStore();
            DesktopPreferences draft = store.Load();
            draft.TranslationSourceLanguage = from;
            draft.TranslationTargetLanguage = to;
            draft.UseConfiguredTranslationServer = validatedEndpoint != null;
            if (validatedEndpoint != null) draft.TranslationServerAddress = validatedEndpoint;
            store.Save(draft);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { return false; }
    }
}
