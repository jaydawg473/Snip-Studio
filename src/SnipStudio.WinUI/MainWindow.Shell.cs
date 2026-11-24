using Microsoft.UI.Xaml;
using SnipStudio.WinUI.Backend;
using Windows.Storage;
using Windows.System;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    internal Task CaptureFromTaskbarAsync(int delaySeconds) =>
        CaptureAsync(selectedCaptureKind, scrolling: false, overrideDelaySeconds: delaySeconds);

    private void AttachDesktopShellCommands(DesktopIntegrationService integration)
    {
        integration.SettingsRequested += (_, _) => OpenSettingsWindow();
        integration.UpdatesRequested += async (_, _) => await OpenUpdatesAsync();
        integration.KeyboardSettingsRequested += async (_, _) => await RunActionAsync("Opening Windows keyboard settings…", async () =>
        {
            if (!await Launcher.LaunchUriAsync(new Uri(DesktopIntegrationService.KeyboardSettingsUri)))
                throw new InvalidOperationException("Windows could not open Keyboard settings.");
            SetStatus("Windows keyboard settings opened.");
            return true;
        }, commitDraft: false);
        integration.RetryPrintScreenRequested += async (_, _) => await RunActionAsync("Checking Print Screen…", () =>
        {
            if (!preferences.UsePrintScreen)
            {
                SetStatus("Enable Print Screen in Snip Studio Settings before retrying.");
                return Task.FromResult(false);
            }
            integration.SetPrintScreenEnabled(true);
            SetStatus(integration.GetPrintScreenSetupHint() ?? "Snip Studio registered Print Screen.");
            return Task.FromResult(true);
        }, commitDraft: false);
    }

    private Task OpenUpdatesAsync() => RunActionAsync("Checking release information…", async () =>
    {
        await DesktopDialogs.ShowUpdatesAsync(Root.XamlRoot, ActionToken);
        return true;
    }, commitDraft: false);

    private async void OnRefreshTaskbarClicked(object sender, RoutedEventArgs args) =>
        await RunActionAsync("Refreshing taskbar shortcuts…", async () =>
        {
            string status;
            try { status = await DesktopJumpList.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5), ActionToken); }
            catch (TimeoutException) { status = "Windows is still handling the taskbar request. Check the taskbar again in a moment."; }
            SetStatus(status);
            return true;
        }, commitDraft: false);

    private async void OnOpenScreenshotFolderClicked(object sender, RoutedEventArgs args) =>
        await OpenConfiguredFolderAsync(preferences.ScreenshotFolder, "screenshots");

    private async void OnOpenRecordingFolderClicked(object sender, RoutedEventArgs args) =>
        await OpenConfiguredFolderAsync(preferences.RecordingFolder, "recordings");

    private Task OpenConfiguredFolderAsync(string path, string label) =>
        RunActionAsync("Opening " + label + " folder…", async () =>
        {
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException("Choose a " + label + " folder in Settings first.");
            var token = ActionToken;
            await Task.Run(() => Directory.CreateDirectory(path), token);
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            token.ThrowIfCancellationRequested();
            if (!await Launcher.LaunchFolderAsync(folder))
                throw new InvalidOperationException("Windows could not open the " + label + " folder.");
            SetStatus("Opened the " + label + " folder.");
            return true;
        }, commitDraft: false);
}
