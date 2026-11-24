using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

/// <summary>Native WinUI dialogs. No settings or OS integration changes occur on Cancel.</summary>
public static class DesktopDialogs
{
    internal static string ApplicationVersion { get; } =
        typeof(DesktopDialogs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0]
        ?? typeof(DesktopDialogs).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal static async Task<ContentDialogResult> ShowBoundedAsync(ContentDialog dialog, double maximumBodyHeight)
    {
        XamlRoot root = dialog.XamlRoot ?? throw new ArgumentException("The dialog needs an owner.", nameof(dialog));
        if (dialog.Content is not FrameworkElement body)
            throw new ArgumentException("The dialog needs scrollable content.", nameof(dialog));

        // The template applies this padding to both the body and the command row.
        // Reserve extra space for a wrapped title and the buttons outside the scroller.
        dialog.Resources["ContentDialogPadding"] = new Thickness(24, 14, 24, 14);
        const double chromeHeight = 160;
        void FitBody() => body.MaxHeight = Math.Clamp(root.Size.Height - chromeHeight, 1, maximumBodyHeight);
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => FitBody();

        FitBody();
        root.Changed += OnRootChanged;
        try { return await dialog.ShowAsync(); }
        finally { root.Changed -= OnRootChanged; }
    }

    public static Task<string?> ShowRecentAsync(WindowId owner, Func<IReadOnlyList<string>> readAppPaths, string theme, CancellationToken token) =>
        RecentSnipsWindow.ShowAsync(owner, readAppPaths, theme, token);

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        string version = ApplicationVersion;
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"""
                Snip Studio {version}
                Automation Forge LLC

                Capture screenshots, annotate images, extract text, and record your screen. Local capture and OCR keep your images and text on this PC.

                Ctrl+O — Open
                Ctrl+V — Paste image
                Ctrl+S — Save
                Ctrl+Shift+S — Save as
                Ctrl+Z / Ctrl+Y — Undo / Redo
                Ctrl+F — Fit
                Ctrl+0 — 100%
                Ctrl+wheel — Zoom around the pointer
                Escape — Cancel the current gesture

                Settings controls global shortcuts, Windows startup, and notification-area behavior. Choose Save editable snip to keep annotations in a .snipstudio project. Standard image exports flatten annotations.

                Check recognized identifiers, quantities, duplicate rows, and selected text against the source image.

                Check for updates offers the official installer link when a newer stable release is available. The link opens in your browser. Verify the timestamped Automation Forge signature before running Setup.

                Third-party licenses and attribution are included in the release’s licenses folder.
                """
        };
        await ShowBoundedAsync(new ContentDialog
        {
            XamlRoot = root,
            Title = "About Snip Studio",
            CloseButtonText = "Close",
            Content = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        }, 520);
    }

    /// <summary>Reviews release metadata and opens trusted download links only when selected.</summary>
    public static async Task ShowUpdatesAsync(XamlRoot root, CancellationToken cancellationToken = default)
    {
        DesktopUpdateInfo info = await DesktopUpdateService.CheckAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var panel = new StackPanel { Spacing = 12, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = info.Message, TextWrapping = TextWrapping.Wrap });
        if (info.SetupDownloadUri is Uri installer)
        {
            panel.Children.Add(new HyperlinkButton { Content = "Download official installer", NavigateUri = installer });
            panel.Children.Add(new TextBlock
            {
                Text = "The download opens in your browser. Verify the Automation Forge signature before running Setup. Setup closes the installed app when it is ready to exit.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (info.NewerPublishedVersion)
        {
            panel.Children.Add(new TextBlock { Text = "This release did not list a matching Snip Studio installer.", TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(new HyperlinkButton { Content = "View Snip Studio release on GitHub", NavigateUri = info.ReleaseUri });
        await ShowBoundedAsync(new ContentDialog
        {
            XamlRoot = root,
            Title = "Snip Studio updates",
            CloseButtonText = "Close",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        }, 520);
    }
}
