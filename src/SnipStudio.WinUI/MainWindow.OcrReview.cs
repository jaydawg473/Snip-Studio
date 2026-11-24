using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnipStudio.WinUI.Backend;
using Windows.Graphics;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private Window? ocrReviewWindow;
    private Func<bool>? pendingOcrReview;
    private Func<bool>? pendingTranslationReview;
    private bool HasPendingTextToolWork => pendingOcrReview?.Invoke() == true ||
        pendingTranslationReview?.Invoke() == true;

    private void ShowPendingTextToolWork()
    {
        Window? window = pendingOcrReview?.Invoke() == true ? ocrReviewWindow : translationWindow;
        if (window is null) return;
        window.AppWindow.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        window.AppWindow.MoveInZOrderAtTop();
        window.Activate();
        SetStatus("Finish or close the text review before updating Snip Studio.");
    }

    private static IEnumerable<nint> OpenOcrToolWindowHandles()
    {
        foreach (MainWindow editor in editorWindows)
        {
            if (editor.ocrReviewWindow is Window review)
                yield return WinRT.Interop.WindowNative.GetWindowHandle(review);
            if (editor.translationWindow is Window translate)
                yield return WinRT.Interop.WindowNative.GetWindowHandle(translate);
        }
    }

    private async void OnEditOcrSelectionClicked(object sender, RoutedEventArgs args)
    {
        if (ocrReviewWindow is Window existing)
        {
            existing.Activate();
            return;
        }
        await RunActionAsync("Opening recognized text for editing…", async () =>
        {
            OcrTextReview review = await GetRecognitionReviewAsync();
            ShowOcrReview(review);
            SetStatus("Edit the recognized text together in the separate window.");
            return true;
        });
    }

    private async Task<OcrTextReview> GetRecognitionReviewAsync()
    {
        OcrSelectionSnapshot? selection = ocrSelection is { Text.Length: > 0 } ? ocrSelection : null;
        OcrSnapshot source = await GetCurrentRecognitionAsync();
        if (selection != null && !ReferenceEquals(ocrSelection, selection))
            throw new InvalidOperationException("The snip changed. Select the words again or reopen this action for the entire extraction.");
        return session.PrepareRecognizedTextReview(source, selection);
    }

    private void ShowOcrReview(OcrTextReview review)
    {
        string originalText = string.Join("\r\n", review.Lines.Select(line => line.Text));
        var original = new TextBox
        {
            Header = "Original recognition",
            Text = originalText, AcceptsReturn = true, IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap, MaxHeight = 150
        };
        var edited = new TextBox
        {
            Header = "Edit recognized text",
            Text = originalText, AcceptsReturn = true, MinHeight = 280, MaxHeight = 520,
            TextWrapping = TextWrapping.NoWrap, MaxLength = 512 * 1024 + 8192,
            FontFamily = new FontFamily("Consolas")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(original, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(edited, ScrollBarVisibility.Auto);

        var sourceImage = new Image { Source = Viewport.SourceForTextReview, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
        var imageViewer = new ScrollViewer
        {
            Content = sourceImage, ZoomMode = ZoomMode.Enabled, MaxHeight = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(imageViewer, "Original snip. Zoom or scroll to examine the image.");
        var originalPanel = new StackPanel { Spacing = 8 };
        originalPanel.Children.Add(new TextBlock { Text = "Original snip", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        originalPanel.Children.Add(imageViewer);
        originalPanel.Children.Add(original);
        var comparison = new Grid { ColumnSpacing = 14, RowSpacing = 12 };
        comparison.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        comparison.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        comparison.RowDefinitions.Add(new() { Height = GridLength.Auto });
        comparison.RowDefinitions.Add(new() { Height = GridLength.Auto });
        comparison.Children.Add(originalPanel);
        comparison.Children.Add(edited);
        comparison.SizeChanged += (_, args) =>
        {
            bool stack = args.NewSize.Width < 550;
            Grid.SetColumnSpan(originalPanel, stack ? 2 : 1);
            Grid.SetColumn(edited, stack ? 0 : 1);
            Grid.SetRow(edited, stack ? 1 : 0);
            Grid.SetColumnSpan(edited, stack ? 2 : 1);
        };

        var feedback = new TextBlock
        {
            Text = review.Lines.Count == 1
                ? "Edit the text and select Apply to snip."
                : $"Keep one edited line for each of the {review.Lines.Count} original lines so edits stay in place.",
            TextWrapping = TextWrapping.Wrap
        };
        DesktopTextClipboard.KeepSelectionLocal(original, message => feedback.Text = message);
        DesktopTextClipboard.KeepSelectionLocal(edited, message => feedback.Text = message);
        var apply = new Button { Content = "Apply to snip", IsEnabled = false };
        var copy = new Button { Content = "Copy text" };
        var close = new Button { Content = "Close" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (UIElement button in new UIElement[] { apply, copy, close }) actions.Children.Add(button);
        var panel = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = comparison, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(scroll);
        Grid.SetRow(feedback, 1);
        panel.Children.Add(feedback);
        Grid.SetRow(actions, 2);
        panel.Children.Add(actions);

        Window window = CreateTextToolWindow("Snip Studio — Edit recognized text", panel);
        ocrReviewWindow = window;
        bool applying = false, ended = false, allowClose = false, confirming = false;
        pendingOcrReview = () => applying || confirming || !string.Equals(edited.Text, original.Text, StringComparison.Ordinal);
        edited.TextChanged += (_, _) => apply.IsEnabled = !applying && !string.Equals(edited.Text, original.Text, StringComparison.Ordinal);
        copy.Click += (_, _) =>
        {
            try { if (SetClipboardText(edited.Text)) feedback.Text = "Edited text copied."; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            { feedback.Text = exception.Message; }
        };
        apply.Click += async (_, _) =>
        {
            if (applying || ended) return;
            if (!CanApplyRecognizedReview(review, out string reason))
            {
                feedback.Text = reason;
                return;
            }
            applying = true;
            apply.IsEnabled = false;
            close.IsEnabled = false;
            edited.IsReadOnly = true;
            bool committed = false;
            await RunActionAsync("Applying recognized text edits…", async () =>
            {
                try
                {
                    string[] lines = edited.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    OcrConversionResult result = await session.ApplyRecognizedTextReviewAsync(review, lines, ActionToken);
                    if (!result.Applied)
                    {
                        feedback.Text = result.Message;
                        return false;
                    }
                    committed = true;
                    await ShowAppliedOcrReviewAsync(result);
                    return true;
                }
                catch (Exception exception) when (!committed && exception is not OutOfMemoryException and not AccessViolationException)
                {
                    if (!ended) feedback.Text = exception is OperationCanceledException ? "Canceled. Your text edits are still here." : exception.Message;
                    return false;
                }
            }, commitDraft: false);
            applying = false;
            if (ended) return;
            if (committed)
            {
                allowClose = true;
                window.Close();
            }
            else
            {
                apply.IsEnabled = true;
                close.IsEnabled = true;
                edited.IsReadOnly = false;
            }
        };
        close.Click += async (_, _) => await ConfirmCloseAsync();
        window.AppWindow.Closing += async (_, args) =>
        {
            if (allowClose || closed || applicationExitPending ||
                (!applying && string.Equals(edited.Text, original.Text, StringComparison.Ordinal))) return;
            args.Cancel = true;
            await ConfirmCloseAsync();
        };
        window.Closed += (_, _) =>
        {
            ended = true;
            pendingOcrReview = null;
            ocrReviewWindow = null;
            sourceImage.Source = null;
        };
        window.Activate();

        async Task ConfirmCloseAsync()
        {
            if (applying || confirming || ended) return;
            if (!closed && !applicationExitPending &&
                !string.Equals(edited.Text, original.Text, StringComparison.Ordinal))
            {
                confirming = true;
                try
                {
                    var confirm = new ContentDialog
                    {
                        XamlRoot = panel.XamlRoot, Title = "Discard these text edits?",
                        Content = "The snip will keep its current text.",
                        PrimaryButtonText = "Discard edits", CloseButtonText = "Keep editing", DefaultButton = ContentDialogButton.Close
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
                {
                    if (!ended) feedback.Text = exception.Message;
                    return;
                }
                finally { confirming = false; }
            }
            if (ended) return;
            allowClose = true;
            window.Close();
        }
    }

    private bool CanApplyRecognizedReview(OcrTextReview review, out string reason)
    {
        reason = "";
        if (closed || !session.IsOcrSnapshotCurrent(review.Source))
            reason = "The snip changed after recognition. Copy your work if needed, then recognize the current image again.";
        else if (busy || applicationExitPending)
            reason = "Wait for the current Snip Studio action to finish, then apply again.";
        else if (Viewport.IsTextEditing)
            reason = "Finish or cancel the open text note in the editor, then apply again.";
        return reason.Length == 0;
    }

    private async Task ShowAppliedOcrReviewAsync(OcrConversionResult result)
    {
        if (closed) return;
        selectedAnnotationIndex = result.LastAnnotationIndex;
        ClearOcrSelection();
        SetTool(PreviewTool.Text, whileBusy: true);
        await RefreshPreviewAsync(resetView: false);
        if (closed) return;
        UpdateSelection();
        SetStatus(result.Message + " Text tool selected.");
    }

    private Window CreateTextToolWindow(string title, FrameworkElement content)
    {
        content.RequestedTheme = Root.ActualTheme;
        var window = new Window { Title = title, Content = content };
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
        SetTextToolWindowOwner(WinRT.Interop.WindowNative.GetWindowHandle(window), -8, WindowHandle);
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is { } display)
        {
            RectInt32 area = display.WorkArea;
            double scale = Math.Max(1, Root.XamlRoot.RasterizationScale);
            int width = Math.Min(area.Width, (int)Math.Ceiling(900 * scale));
            int height = Math.Min(area.Height, (int)Math.Ceiling(660 * scale));
            window.AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
                area.Y + (area.Height - height) / 2, width, height));
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = Math.Min(width, (int)Math.Ceiling(430 * scale));
                presenter.PreferredMinimumHeight = Math.Min(height, (int)Math.Ceiling(400 * scale));
            }
        }
        void CloseWithEditor(object sender, WindowEventArgs args) => window.Close();
        Closed += CloseWithEditor;
        window.Closed += (_, _) => Closed -= CloseWithEditor;
        return window;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetTextToolWindowOwner(nint window, int index, nint owner);
}

public sealed partial class EditorViewport
{
    internal ImageSource? SourceForTextReview => Picture.Source;
}
