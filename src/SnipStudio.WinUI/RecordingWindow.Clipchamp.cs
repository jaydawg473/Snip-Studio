using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.System;

namespace SnipStudio.WinUI;

public sealed partial class RecordingWindow
{
    // Target the Microsoft Store package by publisher-bound identity, rather than using the default player.
    private const string ClipchampPackageFamily = "Clipchamp.Clipchamp_yxz26nhyzhsrt";
    private static readonly Uri ClipchampHome = new("ms-clipchamp:");

    private async void OnEditInClipchampClicked(object sender, RoutedEventArgs args) =>
        await RunAsync("Opening the video in Clipchamp…", async () =>
        {
            if (videoPath is not { } path) return;
            StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask().WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();

            string? activationError = null;
            try
            {
                var support = await Launcher.QueryFileSupportAsync(file, ClipchampPackageFamily).AsTask().WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (support == LaunchQuerySupportStatus.Available &&
                    await Launcher.LaunchFileAsync(file, new LauncherOptions { TargetApplicationPackageFamilyName = ClipchampPackageFamily }))
                {
                    SetStatus("Windows sent the video to Clipchamp.");
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
            {
                activationError = exception.Message;
            }

            lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                // Open Clipchamp without sending the file when it has no matching file contract.
                var support = await Launcher.QueryUriSupportAsync(ClipchampHome, LaunchQuerySupportType.Uri, ClipchampPackageFamily)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (support == LaunchQuerySupportStatus.Available &&
                    await Launcher.LaunchUriAsync(ClipchampHome, new LauncherOptions { TargetApplicationPackageFamilyName = ClipchampPackageFamily }))
                {
                    SetStatus("Clipchamp opened. Use Import media to add " + Path.GetFileName(path) + ".");
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
            {
                activationError ??= exception.Message;
            }

            lifetime.Token.ThrowIfCancellationRequested();
            StorageFolder? folder = await file.GetParentAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (folder != null)
            {
                var options = new FolderLauncherOptions();
                options.ItemsToSelect.Add(file);
                if (await Launcher.LaunchFolderAsync(folder, options))
                {
                    SetStatus("Video selected in File Explorer. Right-click and choose Edit with Clipchamp, or use Import media in Clipchamp.");
                    return;
                }
            }

            throw new InvalidOperationException("Windows could not open Clipchamp with this video or select it in File Explorer. " +
                "In Clipchamp, choose Import media and select " + path + "." +
                (activationError == null ? "" : " " + activationError));
        });
}
