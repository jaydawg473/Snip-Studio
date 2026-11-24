using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SnipStudio.WinUI.Backend;
using Windows.Media.Playback;

namespace SnipStudio.WinUI;

public sealed partial class RecordingWindow
{
    private bool loadingReviewVolume;
    private bool reviewPlaybackStatusUnavailable;
    private int? exportSourceSizeRequest;
    private long? exportSourceBytes;
    private string? exportSourceSizeError;

    private void UpdateReviewActionLayout(double width)
    {
        bool wrapEmail = width < 535;
        bool wrapSave = width < 710;
        Grid.SetRow(CompressEmailButton, wrapEmail ? 1 : 0);
        Grid.SetColumn(CompressEmailButton, wrapEmail ? 0 : 3);
        Grid.SetColumnSpan(CompressEmailButton, wrapEmail ? 6 : 1);
        CompressEmailButton.Margin = new Thickness(0, wrapEmail ? 6 : 0, 0, 0);
        Grid.SetRow(TrimVideoButton, wrapEmail ? 2 : wrapSave ? 1 : 0);
        Grid.SetColumn(TrimVideoButton, wrapSave ? 0 : 5);
        Grid.SetColumnSpan(TrimVideoButton, wrapSave ? 6 : 1);
        TrimVideoButton.Margin = new Thickness(0, wrapSave ? 6 : 0, 0, 0);
    }

    private void SetReviewPlaybackButton(bool playing)
    {
        Symbol icon = playing ? Symbol.Pause : Symbol.Play;
        if (ReviewPlayIcon.Symbol == icon) return;
        ReviewPlayIcon.Symbol = icon;
        ReviewPlayText.Text = playing ? "Pause" : "Play";
        AutomationProperties.SetName(ReviewPlayButton, playing ? "Pause video" : "Play video");
    }

    private static bool IsReviewPlaying(MediaPlayer media) =>
        media.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering;

    private void RefreshReviewPlaybackState(MediaPlayer media)
    {
        if (reviewPlaybackStatusUnavailable) return;
        try
        {
            SetReviewPlaybackButton(IsReviewPlaying(media));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            reviewPlaybackStatusUnavailable = true;
            SetStatus("Windows could not read playback status. Try Play again or reopen the video. " + exception.Message);
        }
    }

    private void ApplyReviewPlaybackChange(Action<MediaPlayer> action, string failureMessage)
    {
        if (player is not { } media || closed) return;
        try { action(media); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ShowError(failureMessage + " " + exception.Message);
        }
    }

    private void OnReviewPlayClicked(object sender, RoutedEventArgs args) => ApplyReviewPlaybackChange(media =>
    {
        if (IsReviewPlaying(media)) media.Pause();
        else media.Play();
        reviewPlaybackStatusUnavailable = false;
    }, "Windows could not change playback.");

    private void SetReviewMuteIcon(bool muted)
    {
        Symbol icon = muted ? Symbol.Mute : Symbol.Volume;
        if (ReviewSoundIcon.Symbol != icon) ReviewSoundIcon.Symbol = icon;
    }

    private void OnReviewSoundOpening(object sender, object args) => ApplyReviewPlaybackChange(media =>
    {
        bool muted = media.IsMuted;
        loadingReviewVolume = true;
        try
        {
            ReviewVolumeSlider.Value = Math.Round(media.Volume * 100);
            ReviewMuteButton.IsChecked = muted;
            SetReviewMuteIcon(muted);
        }
        finally { loadingReviewVolume = false; }
    }, "Windows could not read the playback volume.");

    private void OnReviewVolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!loadingReviewVolume)
            ApplyReviewPlaybackChange(media => media.Volume = args.NewValue / 100, "Windows could not set the playback volume.");
    }

    private void OnReviewMuteClicked(object sender, RoutedEventArgs args) => ApplyReviewPlaybackChange(media =>
    {
        bool muted = ReviewMuteButton.IsChecked == true;
        media.IsMuted = muted;
        SetReviewMuteIcon(muted);
    }, "Windows could not mute or unmute playback.");

    private global::SnipStudio.VideoExportPreset ReviewExportPreset => ExportPresetBox.SelectedIndex == 1
        ? global::SnipStudio.VideoExportPreset.Small480p
        : global::SnipStudio.VideoExportPreset.Balanced720p;

    private int EmailTargetMegabytes =>
        (int)Math.Clamp(double.IsFinite(TargetSizeBox.Value) ? TargetSizeBox.Value : 20, 1, 1000);

    private async void OnVideoExportFlyoutOpening(object sender, object args)
    {
        if (closed || videoPath is not { } path || exportSourceSizeRequest != null) return;
        int generation = videoGeneration;
        exportSourceSizeRequest = generation;
        exportSourceSizeError = null;
        exportSourceBytes = null;
        UpdateVideoExportEstimates();
        CancellationToken token = metadataCancellation?.Token ?? lifetime.Token;
        try
        {
            long bytes = await VideoReviewService.ReadSourceSizeAsync(path, token);
            if (!closed && !token.IsCancellationRequested && generation == videoGeneration) exportSourceBytes = bytes;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed && !token.IsCancellationRequested && generation == videoGeneration)
                exportSourceSizeError = "Could not check the source size. Reopen these options to retry. " + exception.Message;
        }
        finally
        {
            if (!closed && generation == videoGeneration)
            {
                exportSourceSizeRequest = null;
                UpdateVideoExportEstimates();
            }
        }
    }

    private void OnExportPresetChanged(object sender, SelectionChangedEventArgs args) => UpdateVideoExportEstimates();

    private void OnEmailTargetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateVideoExportEstimates();

    private void OnEmailSizeShortcutClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string text } && int.TryParse(text, out int size)) TargetSizeBox.Value = size;
    }

    private void UpdateVideoExportEstimates()
    {
        if (closed || VideoExportEstimateText is null || EmailExportEstimateText is null ||
            ExportVideoButton is null || SaveEmailCopyButton is null) return;
        RenderVideoExportEstimate(VideoExportEstimateText, ExportVideoButton, ReviewExportPreset, 20);
        RenderVideoExportEstimate(EmailExportEstimateText, SaveEmailCopyButton,
            global::SnipStudio.VideoExportPreset.TargetAttachmentSize, EmailTargetMegabytes);
    }

    private void RenderVideoExportEstimate(TextBlock estimate, Button action, global::SnipStudio.VideoExportPreset preset, int target)
    {
        action.IsEnabled = false;
        if (videoPath is not { } path)
        {
            estimate.Text = "";
            return;
        }
        if (exportSourceBytes is not { } bytes)
        {
            estimate.Text = exportSourceSizeError ?? "Checking source size…";
            return;
        }
        try
        {
            var preparation = VideoReviewService.PrepareExport(path, bytes, metadata, preset, target);
            string sourceSize = FormatVideoSize(bytes);
            estimate.Text = preparation.EncodingPlan is { } plan
                ? $"Estimated {FormatVideoSize(plan.EstimatedBytes)} from {sourceSize} · {plan.Width} × {plan.Height} · {plan.FramesPerSecond} fps."
                : $"The current MP4 already fits at {sourceSize}.";
            action.IsEnabled = !busy && !recorder.IsActive && stopTask == null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            estimate.Text = exception.Message;
        }
    }

    private static string FormatVideoSize(long bytes) => bytes < 1_000_000
        ? $"{bytes / 1000d:0.#} KB"
        : $"{bytes / 1_000_000d:0.##} MB";

    private async void OnReviewExportClicked(object sender, RoutedEventArgs args)
    {
        var preset = ReviewExportPreset;
        ReviewExportFlyout.Hide();
        await ExportReviewedVideoAsync(preset, 20);
    }

    private async void OnCompressEmailClicked(object sender, RoutedEventArgs args)
    {
        int targetMegabytes = EmailTargetMegabytes;
        CompressEmailFlyout.Hide();
        await ExportReviewedVideoAsync(global::SnipStudio.VideoExportPreset.TargetAttachmentSize, targetMegabytes);
    }
}
