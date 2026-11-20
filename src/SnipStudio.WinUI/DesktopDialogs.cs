using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

/// <summary>Native WinUI dialogs. No settings or OS integration changes occur on Cancel.</summary>
public static class DesktopDialogs
{
    public static async Task<string?> ShowRecentAsync(XamlRoot root, IReadOnlyList<string> recentPaths)
    {
        ArgumentNullException.ThrowIfNull(recentPaths);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
        var paths = recentPaths.Take(32).ToArray();
        foreach (string path in paths)
        {
            var row = new StackPanel { Spacing = 3, Margin = new Thickness(0, 5, 0, 5) };
            row.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 });
            list.Items.Add(row);
        }
        var panel = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = paths.Length == 0 ? "No recent files yet. Files you open or save in this WinUI build will appear here." : "Choose a file to open. Recent history is stored only on this PC; unavailable files are kept in the list so removable drives can reconnect.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Recent snips and recordings",
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
        var answer = await dialog.ShowAsync();
        return answer == ContentDialogResult.Primary && list.SelectedIndex >= 0 ? paths[list.SelectedIndex] : null;
    }

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        string version = typeof(DesktopDialogs).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Snip Studio {version} — WinUI 3\nAutomation Forge LLC\n\nCapture screenshots, annotate and extract text, record your screen, and save images or recordings. Capture and OCR use Windows services. No screenshot or extracted text is uploaded by these local workflows.\n\nCtrl+O: Open\nCtrl+V: Paste image\nCtrl+S: Save as\nCtrl+Z / Ctrl+Y: Undo / Redo\nCtrl+F: Fit\nCtrl+0: 100%\nCtrl+wheel: Zoom around the pointer\nEscape: Cancel the current gesture\n\nSettings has optional global shortcuts, Windows startup and notification-area behavior. These are separate from the stable app. Save files before exiting; image exports are flattened, so keep the editor open to preserve editable annotations and Undo history.\n\nText recognition can make mistakes. Check identifiers, quantities, duplicate rows and selected text against the source image. Text is never silently spell-corrected.\n\nUpdate review checks this migration repository’s published release information and displays its review address. It does not install or execute downloaded software. Signed installers and portable releases must be approved before installation.\n\nThird-party licenses and attribution are included in the release’s licenses folder."
        };
        await new ContentDialog { XamlRoot = root, Title = "About Snip Studio", CloseButtonText = "Close", Content = new ScrollViewer { Content = text, MaxHeight = Math.Max(180, Math.Min(520, root.Size.Height - 220)) } }.ShowAsync();
    }

    /// <summary>Manual review only; queries metadata and never launches a downloaded program.</summary>
    public static async Task ShowUpdatesAsync(XamlRoot root)
    {
        DesktopUpdateInfo info = await DesktopUpdateService.CheckAsync();
        var panel = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = info.Message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Release review address (copy to your browser):", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = info.ReleaseUri.AbsoluteUri, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false });
        panel.Children.Add(new TextBlock { Text = "This check does not download, install or execute updates. Verify the timestamped Automation Forge signatures on an approved installer or portable build before running it.", TextWrapping = TextWrapping.Wrap });
        await new ContentDialog { XamlRoot = root, Title = "WinUI build update review", CloseButtonText = "Close", Content = panel }.ShowAsync();
    }
}
