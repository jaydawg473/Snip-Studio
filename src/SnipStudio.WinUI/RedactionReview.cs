using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;
using Windows.Storage.Streams;

namespace SnipStudio.WinUI;

internal sealed class RedactionReview
{
    private readonly XamlRoot root;
    private readonly WindowId owner;
    private readonly PreviewSnapshot original;
    private readonly int imageWidth, imageHeight;
    private readonly string suggestedName;
    private readonly string screenshotFolder;
    private readonly OcrRedactionSuggestions? suggestions;
    private readonly ContentDialog dialog;
    private readonly Grid layout = new() { RowSpacing = 8 };
    private readonly EditorViewport canvas = new() { Tool = PreviewTool.Rectangle, InkArgb = 0xFF000000 };
    private readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel categories = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button zoomOut = new() { Content = "−" };
    private readonly Button fit = new() { Content = "Fit" };
    private readonly Button zoomIn = new() { Content = "+" };
    private readonly ToggleButton pan = new() { Content = "Pan" };
    private readonly Button undoArea = new() { Content = "Undo area" };
    private readonly Button clearAreas = new() { Content = "Remove all redactions" };
    private readonly CheckBox emails = new();
    private readonly CheckBox phones = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private ImageRectangle[] manualAreas = [];
    private bool emailsApplied, phonesApplied;
    private RedactedImage? redacted;
    private BitmapImage? originalBitmap;
    private Task? activeOperation;
    private CancellationToken cancellationToken;
    private bool working, publishing, ended, reviewReady;
    private string? published;

    internal RedactionReview(XamlRoot root, WindowId owner, PreviewSnapshot original,
        int width, int height, string suggestedName, string screenshotFolder, OcrRedactionSuggestions? suggestions = null)
    {
        this.root = root;
        this.owner = owner;
        this.original = original;
        imageWidth = width;
        imageHeight = height;
        this.suggestedName = suggestedName + " - redacted";
        this.screenshotFolder = screenshotFolder;
        this.suggestions = suggestions;

        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = "Drag over what you want to hide. Copy or save the reviewed PNG when it’s ready.",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (Control button in new Control[] { zoomOut, fit, zoomIn, pan, undoArea }) toolbar.Children.Add(button);
        AddRow(toolbar, 1);
        if (suggestions is not null)
        {
            emails.Content = $"Email addresses ({suggestions.EmailAddresses.Count})";
            phones.Content = $"Phone numbers ({suggestions.PhoneNumbers.Count})";
            categories.Children.Add(emails);
            categories.Children.Add(phones);
            categories.Children.Add(clearAreas);
            AddRow(categories, 2);
        }
        AddRow(canvas, 3);
        AddRow(feedback, 4);
        AutomationProperties.SetName(canvas, "Redaction preview");
        AutomationProperties.SetName(zoomOut, "Zoom out of redaction preview");
        AutomationProperties.SetName(zoomIn, "Zoom into redaction preview");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);

        dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Redact for sharing", Content = layout,
            PrimaryButtonText = "Copy PNG", SecondaryButtonText = "Save PNG", CloseButtonText = "Close"
        };
        dialog.Resources["ContentDialogMaxWidth"] = 940d;
        dialog.Resources["ContentDialogPadding"] = new Thickness(18, 12, 18, 12);
        dialog.PrimaryButtonClick += async (_, args) => await PublishAsync(args, save: false);
        dialog.SecondaryButtonClick += async (_, args) => await PublishAsync(args, save: true);
        dialog.Closing += (_, args) => { if (publishing) args.Cancel = true; };
        canvas.GestureCommitted += OnDrag;
        zoomOut.Click += (_, _) => canvas.ZoomBy(1 / 1.25);
        zoomIn.Click += (_, _) => canvas.ZoomBy(1.25);
        fit.Click += (_, _) => canvas.Fit();
        pan.Checked += (_, _) => canvas.Tool = PreviewTool.Pan;
        pan.Unchecked += (_, _) => canvas.Tool = PreviewTool.Rectangle;
        undoArea.Click += async (_, _) =>
        {
            if (!working && manualAreas.Length > 0)
                await UpdateAreasAsync(manualAreas[..^1], emailsApplied, phonesApplied);
        };
        clearAreas.Click += async (_, _) =>
        {
            if (!working) await UpdateAreasAsync([], includeEmails: false, includePhones: false);
        };
        emails.Click += OnSuggestionClick;
        phones.Click += OnSuggestionClick;
        UpdateControls();
    }

    internal async Task<string?> ShowAsync(CancellationToken actionToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(actionToken);
        cancellationToken = lifetime.Token;
        originalBitmap = await LoadBitmapAsync(original.PngBytes);
        cancellationToken.ThrowIfCancellationRequested();
        canvas.SetImage(originalBitmap, imageWidth, imageHeight, resetView: true);
        if (suggestions is { } found && (found.EmailAddresses.Count > 0 || found.PhoneNumbers.Count > 0))
            await UpdateAreasAsync([], found.EmailAddresses.Count > 0, found.PhoneNumbers.Count > 0);
        else if (suggestions is not null)
            feedback.Text = "No email addresses or phone numbers were suggested. Draw over any details you want to hide.";
        cancellationToken.ThrowIfCancellationRequested();
        UpdateBounds();
        root.Changed += OnRootChanged;
        try { await dialog.ShowAsync(); }
        finally
        {
            ended = true;
            root.Changed -= OnRootChanged;
            lifetime.Cancel();
            if (activeOperation is not null) await activeOperation;
        }
        return published;
    }

    private void AddRow(FrameworkElement child, int row)
    {
        Grid.SetRow(child, row);
        layout.Children.Add(child);
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();

    private void UpdateBounds()
    {
        layout.Width = Math.Max(1, Math.Min(856, root.Size.Width - 58));
        categories.Orientation = layout.Width < 485 ? Orientation.Vertical : Orientation.Horizontal;
        layout.Height = Math.Max(1, Math.Min(625, root.Size.Height - 140));
        dialog.MaxHeight = Math.Max(1, root.Size.Height - 14);
    }

    private async void OnDrag(object? sender, DrawGestureEventArgs args)
    {
        if (working || ended || args.Points.Count < 2) return;
        ImagePoint start = args.Points[0], end = args.Points[^1];
        var area = new ImageRectangle(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        if (!area.IsEmpty) await UpdateAreasAsync([.. manualAreas, area], emailsApplied, phonesApplied);
    }

    private async void OnSuggestionClick(object sender, RoutedEventArgs args)
    {
        if (!working && !ended)
            await UpdateAreasAsync(manualAreas, emails.IsChecked == true, phones.IsChecked == true);
    }

    private Task UpdateAreasAsync(ImageRectangle[] manual, bool includeEmails, bool includePhones)
    {
        var chosen = new List<ImageRectangle>();
        if (includeEmails && suggestions is not null) chosen.AddRange(suggestions.EmailAddresses);
        if (includePhones && suggestions is not null) chosen.AddRange(suggestions.PhoneNumbers);
        chosen.AddRange(manual);
        reviewReady = false;
        if (chosen.Count > EditorSession.MaximumRedactionAreas)
        {
            feedback.Text = $"Only {EditorSession.MaximumRedactionAreas} areas fit in one review. Use Undo area or turn off a suggestion category.";
            UpdateControls();
            return Task.CompletedTask;
        }
        return RunAsync("Rendering redacted PNG…", async () =>
        {
            RedactedImage? result = null;
            BitmapImage bitmap = originalBitmap!;
            if (chosen.Count > 0)
            {
                result = await EditorSession.RenderRedactedCopyAsync(original, chosen, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bitmap = await LoadBitmapAsync(result.PngBytes.ToArray());
            }
            cancellationToken.ThrowIfCancellationRequested();
            canvas.SetImage(bitmap, imageWidth, imageHeight, resetView: false);
            manualAreas = manual;
            emailsApplied = includeEmails;
            phonesApplied = includePhones;
            redacted = result;
            reviewReady = result is not null;
            string unit = chosen.Count == 1 ? "area" : "areas";
            feedback.Text = chosen.Count == 0 ? "Draw an area to create a redacted preview."
                : suggestions is null ? $"{chosen.Count} {unit} covered. Review the image before copying or saving."
                : $"{chosen.Count} {unit} covered. Check for details OCR missed before copying or saving.";
        });
    }

    private async Task PublishAsync(ContentDialogButtonClickEventArgs args, bool save)
    {
        args.Cancel = true;
        if (working || ended || !reviewReady || redacted is not RedactedImage result) return;
        var deferral = args.GetDeferral();
        publishing = true;
        try
        {
            if (save) await RunAsync("Choose a filename…", async () =>
            {
                var picker = new FileSavePicker(owner)
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFolder = screenshotFolder,
                    SuggestedFileName = suggestedName, DefaultFileExtension = ".png", CommitButtonText = "Save PNG"
                };
                picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
                var file = await picker.PickSaveFileAsync();
                if (file is null) { feedback.Text = "Save canceled. The redacted preview is ready."; return; }
                cancellationToken.ThrowIfCancellationRequested();
                await result.SaveCopyAsync(file.Path, cancellationToken);
                feedback.Text = published = "Saved redacted PNG " + Path.GetFileName(file.Path) + ".";
            });
            else await RunAsync("Copying redacted PNG…", async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long intent = DesktopImageActions.BeginClipboardWrite();
                bool copied = await DesktopImageActions.CopyPngAsync(result.PngBytes.ToArray(), intent,
                    () => !cancellationToken.IsCancellationRequested && !ended && ReferenceEquals(redacted, result));
                if (copied) feedback.Text = published = "Copied the redacted PNG to the clipboard.";
                else feedback.Text = "Copy did not finish. Use Copy PNG again before pasting.";
            });
        }
        finally { publishing = false; deferral.Complete(); }
    }

    private Task RunAsync(string message, Func<Task> action)
    {
        working = true;
        feedback.Text = message;
        UpdateControls();
        activeOperation = CompleteAsync(action);
        return activeOperation;
    }

    private async Task CompleteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!ended) feedback.Text = "Operation canceled. Review the current preview."; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!ended) feedback.Text = exception.Message;
        }
        finally
        {
            working = false;
            if (!ended) UpdateControls();
        }
    }

    private void UpdateControls()
    {
        dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = !working && reviewReady;
        canvas.IsEnabled = zoomOut.IsEnabled = zoomIn.IsEnabled = fit.IsEnabled = pan.IsEnabled = !working;
        undoArea.IsEnabled = !working && manualAreas.Length > 0;
        clearAreas.IsEnabled = !working && redacted is not null;
        emails.IsEnabled = !working && suggestions?.EmailAddresses.Count > 0;
        phones.IsEnabled = !working && suggestions?.PhoneNumbers.Count > 0;
        emails.IsChecked = emailsApplied;
        phones.IsChecked = phonesApplied;
    }

    private static async Task<BitmapImage> LoadBitmapAsync(byte[] png)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
