using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Graphics;
using System.Runtime.InteropServices;

namespace SnipStudio.WinUI;

/// <summary>A WinUI-owned recording and review window; encoding remains in Windows Media Foundation.</summary>
public sealed partial class RecordingWindow : Window
{
    private static readonly SemaphoreSlim audioEnumerationSlots = new(2, 2);
    private readonly RecordingController recorder = new();
    private RecordingBoundaryOverlay? recordingBoundary;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer durationTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TaskCompletionSource<bool> closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? metadataCancellation;
    private MediaPlayer? player;
    private MediaSource? source;
    private CandidateVideoOpen? pendingVideoOpen;
    private string? candidateCopyPath;
    private global::SnipStudio.VideoMetadata? metadata;
    private RecordingRegion? customRegion;
    private string? videoPath;
    private bool busy, closed, allowClose, closePending, publishedRecordingState;
    private bool audioLoading;
    private Task<string?>? stopTask;
    private Task<StorageFile>? videoFileRead;
    private Task<string>? automaticRecordingPathRequest;
    private int videoGeneration;
    private int recordingGeneration;
    private readonly string? initialPath;
    private readonly bool captureWorkflow;
    private bool recordingOptionsExpanded;
    private string recordingFolder = "";
    private XamlRoot? minimumSizingRoot;
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? RecordingSaved;
    public bool IsRecording => recorder.IsActive;
    public bool IsClosed => closed;

    private sealed class CandidateVideoOpen
    {
        public CandidateVideoOpen(string path, MediaPlayer player, MediaSource source, CancellationToken token)
        { Path = path; Player = player; Source = source; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public string Path { get; }
        public MediaPlayer Player { get; }
        public MediaSource Source { get; }
        public CancellationTokenSource Cancellation { get; }
        public bool Opened { get; set; }
        public string? FailureMessage;
    }

    public RecordingWindow(string? initialVideoPath = null, RecordingRegion? initialRegion = null, bool captureWorkflow = false)
    {
        initialPath = initialVideoPath;
        customRegion = initialRegion;
        this.captureWorkflow = captureWorkflow && initialRegion != null;
        InitializeComponent();
        AppWindow.Resize(this.captureWorkflow && string.IsNullOrWhiteSpace(initialVideoPath)
            ? new SizeInt32(760, 300)
            : new SizeInt32(1040, string.IsNullOrWhiteSpace(initialVideoPath) ? 480 : 820));
        UpdateRecordingMinimums();
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        RecordingRoot.Loaded += OnLoaded;
        recorder.StatusChanged += OnRecorderStatusChanged;
        durationTimer.Tick += (_, _) =>
        {
            if (!closed && recorder.IsActive) ElapsedText.Text = FormatDuration(recorder.Duration);
        };
        foreach (RecordingDisplay display in RecordingController.GetDisplays())
            RegionBox.Items.Add(display with { Name = (display.IsPrimary ? "Primary · " : "") + display.Name + $" · {display.Region.Width} × {display.Region.Height}" });
        RegionBox.Items.Add(new RecordingDisplay("All monitors", RecordingController.DesktopRegion, false));
        if (customRegion is { } region) AddCustomRegion(region);
        else RegionBox.SelectedIndex = 0;
        UpdateCommands();
    }

    public void ConfigureDefaults(bool microphone, bool systemAudio, string recordingFolder)
    {
        if (closed || busy || recorder.IsActive) return;
        MicrophoneCheck.IsChecked = microphone;
        SystemAudioCheck.IsChecked = systemAudio;
        if (!string.IsNullOrWhiteSpace(recordingFolder) && !Path.IsPathFullyQualified(recordingFolder))
            throw new ArgumentException("Choose a full recording folder path in Settings.", nameof(recordingFolder));
        this.recordingFolder = string.IsNullOrWhiteSpace(recordingFolder) ? "" : Path.GetFullPath(recordingFolder);
    }

    private void PublishSaved(string path)
    {
        try { RecordingSaved?.Invoke(this, path); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            VideoNotice.Message = "The video was saved, but recent files could not be updated: " + exception.Message;
            VideoNotice.IsOpen = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        RecordingRoot.Loaded -= OnLoaded;
        minimumSizingRoot = RecordingRoot.XamlRoot;
        if (minimumSizingRoot is not null) minimumSizingRoot.Changed += OnRecordingMinimumRootChanged;
        UpdateRecordingMinimums();
        await RefreshAudioAsync();
        if (!closed && !string.IsNullOrWhiteSpace(initialPath))
            await RunAsync("Opening video…", () => LoadVideoAsync(initialPath));
    }

    private static string FormatDuration(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    private void PublishRecordingState(bool active)
    {
        if (publishedRecordingState == active) return;
        publishedRecordingState = active;
        RecordingStateChanged?.Invoke(this, active);
    }

    private void AddCustomRegion(RecordingRegion region)
    {
        customRegion = region;
        for (int index = RegionBox.Items.Count - 1; index >= 0; index--)
            if (RegionBox.Items[index] is RecordingDisplay item && item.Name.StartsWith("Selected area", StringComparison.Ordinal)) RegionBox.Items.RemoveAt(index);
        RegionBox.Items.Add(new RecordingDisplay($"Selected area · {region.Width} × {region.Height}", region, false));
        RegionBox.SelectedIndex = RegionBox.Items.Count - 1;
    }

    private void OnRecordingAreaChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SelectedAreaSummary is not null && RegionBox.SelectedItem is RecordingDisplay area)
            SelectedAreaSummary.Text = $"Selected area · {area.Region.Width} × {area.Region.Height} pixels";
    }

    private void OnRecordingOptionsClicked(object sender, RoutedEventArgs args)
    {
        recordingOptionsExpanded = RecordingOptionsButton.IsChecked == true;
        UpdateCommands();
    }

    private async void OnSelectAreaClicked(object sender, RoutedEventArgs args) => await SelectRecordingRegionAsync(CaptureKind.Rectangle);
    private async void OnSelectWindowClicked(object sender, RoutedEventArgs args) => await SelectRecordingRegionAsync(CaptureKind.Window);
    private Task SelectRecordingRegionAsync(CaptureKind kind) => RunAsync(kind == CaptureKind.Window ? "Select a window to record. Escape cancels." : "Select the recording area. Escape cancels.", async () =>
    {
        CaptureRectangle? selected = await CaptureCoordinator.SelectRecordingRegionAsync(new CaptureRequest
        {
            OwnerWindow = WinRT.Interop.WindowNative.GetWindowHandle(this), Kind = kind
        }, lifetime.Token);
        if (selected is { } region) { AddCustomRegion(new(region.X, region.Y, region.Width, region.Height)); SetStatus("Recording area selected."); }
        else SetStatus("Area selection canceled. The previous area is kept.");
    });

    private async void OnRefreshAudioClicked(object sender, RoutedEventArgs args) => await RefreshAudioAsync();
    private async Task RefreshAudioAsync()
    {
        if (closed || busy || recorder.IsActive || audioLoading) return;
        audioLoading = true;
        UpdateCommands();
        string microphoneId = (MicrophoneBox.SelectedItem as RecordingAudioDevice)?.Id ?? "";
        string outputId = (SystemAudioBox.SelectedItem as RecordingAudioDevice)?.Id ?? "";
        // Device enumeration uses synchronous Core Audio COM. One request per
        // window stays leased after the UI timeout, and its late fault is observed.
        Task<(IReadOnlyList<RecordingAudioDevice> Microphones, IReadOnlyList<RecordingAudioDevice> Outputs)> pending = EnumerateAudioAsync(lifetime.Token);
        try
        {
            var devices = await pending.WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token);
            if (closed) return;
            FillDevices(MicrophoneBox, devices.Microphones, microphoneId);
            FillDevices(SystemAudioBox, devices.Outputs, outputId);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed)
            {
                FillDevices(MicrophoneBox, Array.Empty<RecordingAudioDevice>(), "");
                FillDevices(SystemAudioBox, Array.Empty<RecordingAudioDevice>(), "");
                ShowError(exception.Message + " Windows default devices are still selectable; recording startup will report an unavailable source.");
            }
        }
        finally
        {
            _ = FinishAudioEnumerationAsync(pending);
        }
    }

    private static async Task<(IReadOnlyList<RecordingAudioDevice> Microphones, IReadOnlyList<RecordingAudioDevice> Outputs)> EnumerateAudioAsync(CancellationToken token)
    {
        await audioEnumerationSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => (RecordingController.GetMicrophones(), RecordingController.GetSystemAudioDevices()), token).ConfigureAwait(false);
        }
        finally { audioEnumerationSlots.Release(); }
    }

    private async Task FinishAudioEnumerationAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); } catch (Exception) { }
        finally
        {
            DispatcherQueue.TryEnqueue(() => { audioLoading = false; if (!closed) UpdateCommands(); });
        }
    }

    private static void FillDevices(ComboBox box, IReadOnlyList<RecordingAudioDevice> choices, string selectedId)
    {
        box.Items.Clear();
        box.Items.Add(new RecordingAudioDevice("", "Windows default"));
        foreach (var device in choices) box.Items.Add(device);
        box.SelectedIndex = 0;
        for (int index = 1; index < box.Items.Count; index++)
            if (box.Items[index] is RecordingAudioDevice device && device.Id == selectedId) { box.SelectedIndex = index; break; }
    }

    private async void OnStartRecordingClicked(object sender, RoutedEventArgs args)
    {
        await RunAsync(captureWorkflow ? "Preparing the recording…" : "Choose where to save the recording…", async () =>
        {
            if (RegionBox.SelectedItem is not RecordingDisplay area) throw new InvalidOperationException("Choose a monitor or select an area first.");
            string? path = captureWorkflow ? await PrepareAutomaticRecordingPathAsync()
                : await ChooseSavePathAsync("Snip Studio recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"), ".mp4", "MP4 video");
            if (path == null) { SetStatus("Recording canceled."); return; }
            if (captureWorkflow)
            {
                RecordingDestinationText.Text = "Recording destination: " + path;
                RecordingDestinationText.Visibility = Visibility.Visible;
            }
            CancelPendingVideoOpen();
            player?.Pause();
            RecordingSettings settings = new(MicrophoneCheck.IsChecked == true, SystemAudioCheck.IsChecked == true,
                CursorCheck.IsChecked == true, (int)Math.Clamp(double.IsFinite(FpsBox.Value) ? FpsBox.Value : 15, 5, 30),
                (MicrophoneBox.SelectedItem as RecordingAudioDevice)?.Id ?? "", (SystemAudioBox.SelectedItem as RecordingAudioDevice)?.Id ?? "");
            SetStatus("Starting recording…");
            ++recordingGeneration;
            try
            {
                PublishRecordingState(true);
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
                // Give the compositor time to remove the minimized controls.
                await Task.Delay(250, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                recordingBoundary = new RecordingBoundaryOverlay(area.Region);
                recordingBoundary.Show();
                if (!recordingBoundary.HasCompleteOutline)
                {
                    VideoNotice.Title = "Recording outline";
                    VideoNotice.Severity = InfoBarSeverity.Warning;
                    VideoNotice.Message = "Some screen-edge markers are hidden because Windows could not exclude them from capture. Recording still uses the selected area. Reopen this window from the taskbar to stop.";
                    VideoNotice.IsOpen = true;
                }
                await recorder.StartAsync(recordingBoundary.Region, path, settings, lifetime.Token);
                durationTimer.Start();
                SetStatus("Recording. Reopen this window from the taskbar and choose Stop and save.");
            }
            catch
            {
                ReleaseRecordingBoundary();
                if (!closed)
                {
                    if (AppWindow.Presenter is OverlappedPresenter failedPresenter) failedPresenter.Restore();
                    PublishRecordingState(recorder.IsActive);
                }
                throw;
            }
        });
    }

    private async void OnStopRecordingClicked(object sender, RoutedEventArgs args) => await StopAndReviewAsync();

    private async Task<bool> StopAndReviewAsync()
    {
        if (closed || (busy && stopTask == null)) return false;
        bool stopped = false;
        await RunAsync("Finishing MP4. Keep this window open…", async () =>
        {
            stopTask ??= recorder.StopAsync();
            ReleaseRecordingBoundary();
            string? completed;
            try { completed = await stopTask.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token); }
            catch (TimeoutException)
            {
                SetStatus("Windows is still finishing the recording. Your file remains owned by this session; choose Stop again to check.");
                return;
            }
            finally
            {
                if (stopTask.IsCompleted)
                {
                    stopTask = null;
                    durationTimer.Stop();
                    PublishRecordingState(recorder.IsActive);
                    if (AppWindow.Presenter is OverlappedPresenter finishedPresenter) finishedPresenter.Restore();
                }
            }
            durationTimer.Stop();
            PublishRecordingState(false);
            stopped = true;
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
            Activate();
            if (completed != null)
            {
                if (captureWorkflow) RecordingDestinationText.Text = "Saved to: " + completed;
                PublishSaved(completed);
                await LoadVideoAsync(completed);
            }
            else SetStatus("Recording stopped.");
        });
        return stopped;
    }

    private void OnRecorderStatusChanged(object? sender, RecordingProgress progress)
    {
        int generation = Volatile.Read(ref recordingGeneration);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || generation != recordingGeneration) return;
            if (!progress.IsActive) ReleaseRecordingBoundary();
            ElapsedText.Text = FormatDuration(progress.Duration);
            SetStatus(progress.Message);
            if (progress.Error != null)
            {
                ShowError(progress.Error.Message + (string.IsNullOrWhiteSpace(progress.RecoveryPath) ? "" : " Completed video recovery file: " + progress.RecoveryPath));
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
                _ = ObserveRecordingFailureAsync();
            }
            UpdateCommands();
        });
    }

    private async Task ObserveRecordingFailureAsync()
    {
        ReleaseRecordingBoundary();
        try { await recorder.StopAsync(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        { if (!closed) ShowError(exception.Message); }
        finally { if (!closed) { durationTimer.Stop(); PublishRecordingState(false); UpdateCommands(); } }
    }

    private async void OnOpenVideoClicked(object sender, RoutedEventArgs args) => await RunAsync("Choose a video…", async () =>
    {
        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.VideosLibrary, CommitButtonText = "Open video" };
        foreach (string extension in new[] { ".mp4", ".mov", ".wmv", ".avi", ".m4v" }) picker.FileTypeFilter.Add(extension);
        var chosen = await picker.PickSingleFileAsync();
        if (chosen != null) await LoadVideoAsync(chosen.Path);
        else SetStatus("Open canceled. The current video is kept.");
    });

    private async Task LoadVideoAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (videoFileRead is { IsCompleted: false })
            throw new InvalidOperationException("The previous file request is still finishing. The current video is kept; try opening again later.");
        CancelPendingVideoOpen();
        var fileRequest = Task.Run(async () =>
        {
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The video file is unavailable.", fullPath);
            return await StorageFile.GetFileFromPathAsync(fullPath).AsTask().ConfigureAwait(false);
        });
        videoFileRead = fileRequest;
        _ = ObserveVideoFileReadAsync(fileRequest);
        StorageFile file;
        try { file = await fileRequest.WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token); }
        catch (TimeoutException)
        {
            throw new TimeoutException("The video file did not respond within eight seconds. The current video is kept; try opening again after the provider finishes.");
        }
        if (closed) return;
        candidateCopyPath = fullPath;
        CandidatePanel.Visibility = Visibility.Visible;
        CandidateInfoText.Text = "Selected file: " + Path.GetFileName(fullPath) + " · Checking playback. Save selected file copy is available." +
            (videoPath == null ? "" : " Current review is kept until the selected video opens successfully.");
        if (player == null) NoVideoText.Text = "Opening the selected video. Save selected file copy is available while Windows checks playback.";
        MediaSource? nextSource = null;
        MediaPlayer? nextPlayer = null;
        try
        {
            nextSource = MediaSource.CreateFromStorageFile(file);
            nextPlayer = new MediaPlayer { AutoPlay = false };
            var candidate = new CandidateVideoOpen(fullPath, nextPlayer, nextSource, lifetime.Token);
            pendingVideoOpen = candidate;
            nextPlayer.MediaFailed += OnMediaFailed;
            nextPlayer.MediaOpened += OnCandidateMediaOpened;
            nextPlayer.MediaFailed += OnCandidateMediaFailed;
            nextPlayer.Source = nextSource;
            _ = ObserveCandidateOpenTimeoutAsync(candidate);
        }
        catch
        {
            if (pendingVideoOpen is { } candidate && ReferenceEquals(candidate.Player, nextPlayer))
                FailCandidateOpen(candidate, "Windows could not initialize playback for the selected file.");
            else
            {
                if (nextPlayer != null) { nextPlayer.MediaOpened -= OnCandidateMediaOpened; nextPlayer.MediaFailed -= OnCandidateMediaFailed; nextPlayer.MediaFailed -= OnMediaFailed; nextPlayer.Source = null; nextPlayer.Dispose(); }
                nextSource?.Dispose();
                CandidateInfoText.Text = "Selected file: " + Path.GetFileName(fullPath) + " · Playback unavailable. Save selected file copy remains available.";
                if (player == null) NoVideoText.Text = "Windows could not play the selected file. Save selected file copy is available.";
            }
            throw;
        }
        SetStatus("Checking the selected video's playback. Save selected file copy is available; the current review is kept until opening succeeds.");
    }

    private void OnCandidateMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || pendingVideoOpen is not { } candidate || !ReferenceEquals(candidate.Player, sender)) return;
            if (Volatile.Read(ref candidate.FailureMessage) is { } failure) { FailCandidateOpen(candidate, "Windows could not play the selected file: " + failure); return; }
            candidate.Opened = true;
            candidate.Cancellation.Cancel();
            CommitReadyCandidate();
        });
    }

    private void OnCandidateMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        string reason = args.ErrorMessage;
        CandidateVideoOpen? failedCandidate = Volatile.Read(ref pendingVideoOpen);
        if (failedCandidate != null && ReferenceEquals(failedCandidate.Player, sender))
            Interlocked.Exchange(ref failedCandidate.FailureMessage, reason);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || pendingVideoOpen is not { } candidate || !ReferenceEquals(candidate.Player, sender)) return;
            FailCandidateOpen(candidate, "Windows could not play the selected file: " + reason);
        });
    }

    private async Task ObserveCandidateOpenTimeoutAsync(CandidateVideoOpen candidate)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), candidate.Cancellation.Token);
            if (!closed && !candidate.Opened && ReferenceEquals(pendingVideoOpen, candidate))
                FailCandidateOpen(candidate, "Windows did not finish opening the selected video within ten seconds.");
        }
        catch (OperationCanceledException) { }
    }

    private void CommitReadyCandidate()
    {
        if (closed || pendingVideoOpen is not { Opened: true } candidate) return;
        if (Volatile.Read(ref candidate.FailureMessage) is { } failure) { FailCandidateOpen(candidate, "Windows could not play the selected file: " + failure); return; }
        if (busy || recorder.IsActive)
        {
            CandidateInfoText.Text = "Selected file: " + Path.GetFileName(candidate.Path) + " · Playback ready. The current review is kept until this action finishes.";
            return;
        }
        try
        {
            VideoPlayer.SetMediaPlayer(candidate.Player);
            if (Volatile.Read(ref candidate.FailureMessage) is { } openingFailure)
                throw new InvalidOperationException(openingFailure);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            string detail = exception.Message;
            try { VideoPlayer.SetMediaPlayer(player); }
            catch (Exception restoreError) when (restoreError is not OutOfMemoryException and not AccessViolationException)
            { detail += " The current clip is retained, but its display could not be restored: " + restoreError.Message; }
            FailCandidateOpen(candidate, "Windows could not display the selected video: " + detail);
            return;
        }
        pendingVideoOpen = null;
        candidate.Player.MediaOpened -= OnCandidateMediaOpened;
        candidate.Player.MediaFailed -= OnCandidateMediaFailed;
        candidate.Cancellation.Cancel();
        candidate.Cancellation.Dispose();
        MediaPlayer? previousPlayer = player;
        MediaSource? previousSource = source;
        player = candidate.Player;
        source = candidate.Source;
        videoPath = candidate.Path;
        candidateCopyPath = null;
        CandidatePanel.Visibility = Visibility.Collapsed;
        metadata = null;
        string? cleanupWarning = null;
        Action[] previousCleanup =
        {
            () => { if (previousPlayer != null) previousPlayer.MediaFailed -= OnMediaFailed; },
            () => previousPlayer?.Pause(),
            () => { if (previousPlayer != null) previousPlayer.Source = null; },
            () => previousSource?.Dispose(),
            () => previousPlayer?.Dispose()
        };
        foreach (Action release in previousCleanup)
        {
            try { release(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            { cleanupWarning ??= exception.Message; }
        }
        NoVideoText.Visibility = Visibility.Collapsed;
        TrimStartBox.Value = 0;
        TrimEndBox.Value = 0;
        TrimStartBox.Maximum = TrimEndBox.Maximum = double.MaxValue;
        VideoInfoText.Text = Path.GetFileName(candidate.Path) + " · Reading video details…";
        Title = "Snip Studio — " + Path.GetFileName(candidate.Path);
        SetStatus("Playback and Save current video copy are available. Reading video details for Trim and Export…");
        metadataCancellation?.Cancel();
        metadataCancellation?.Dispose();
        metadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = LoadMetadataAsync(candidate.Path, ++videoGeneration, metadataCancellation.Token);
        UpdateCommands();
        if (captureWorkflow)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (closed || !ReferenceEquals(player, candidate.Player)) return;
                RecordingRoot.UpdateLayout();
                RecordingBodyScroll.ChangeView(null, 0d, null, true);
            });
        if (cleanupWarning != null)
        {
            VideoNotice.Title = "Video opened";
            VideoNotice.Severity = InfoBarSeverity.Warning;
            VideoNotice.Message = "The selected video opened, but Windows could not release all previous playback resources: " + cleanupWarning;
            VideoNotice.IsOpen = true;
        }
    }

    private void FailCandidateOpen(CandidateVideoOpen candidate, string reason)
    {
        if (!ReferenceEquals(pendingVideoOpen, candidate)) return;
        pendingVideoOpen = null;
        string? cleanupWarning = DisposeCandidate(candidate);
        CandidateInfoText.Text = "Selected file: " + Path.GetFileName(candidate.Path) + " · Playback unavailable. Save selected file copy remains available.";
        CandidatePanel.Visibility = Visibility.Visible;
        if (player == null) NoVideoText.Text = "Windows could not play the selected file. Save selected file copy is available.";
        VideoNotice.Title = "Selected video could not open";
        VideoNotice.Severity = InfoBarSeverity.Warning;
        VideoNotice.Message = reason + (videoPath == null ? " The selected file is preserved and can still be copied." : " Your current clip, playback position, and trim range are kept. The selected file can still be copied separately.") +
            (cleanupWarning == null ? "" : " Windows could not release all selected-file playback resources: " + cleanupWarning);
        VideoNotice.IsOpen = true;
        if (!busy) SetStatus("The selected video did not open. " + (videoPath == null ? "Its original file can still be copied." : "The current review is kept."));
        UpdateCommands();
    }

    private void CancelPendingVideoOpen()
    {
        CandidateVideoOpen? candidate = pendingVideoOpen;
        pendingVideoOpen = null;
        if (candidate == null) return;
        string? cleanupWarning = DisposeCandidate(candidate);
        if (!closed)
        {
            CandidateInfoText.Text = "Selected file: " + Path.GetFileName(candidate.Path) + " · Opening canceled. Save selected file copy remains available.";
            if (player == null) NoVideoText.Text = "No video is playing. Save selected file copy is available, or open another video.";
            if (cleanupWarning != null) ShowError("Opening was canceled and the current review is kept, but Windows could not release all selected-file playback resources: " + cleanupWarning);
        }
    }

    private string? DisposeCandidate(CandidateVideoOpen candidate)
    {
        string? failure = null;
        Action[] cleanup =
        {
            () => candidate.Player.MediaOpened -= OnCandidateMediaOpened,
            () => candidate.Player.MediaFailed -= OnCandidateMediaFailed,
            () => candidate.Player.MediaFailed -= OnMediaFailed,
            candidate.Cancellation.Cancel,
            candidate.Cancellation.Dispose,
            () => candidate.Player.Source = null,
            candidate.Source.Dispose,
            candidate.Player.Dispose
        };
        foreach (Action release in cleanup)
        {
            try { release(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            { failure ??= exception.Message; }
        }
        return failure;
    }

    private async Task LoadMetadataAsync(string path, int generation, CancellationToken token)
    {
        try
        {
            var info = await VideoReviewService.InspectAsync(path, token);
            if (closed || token.IsCancellationRequested || generation != videoGeneration || videoPath != path) return;
            if (info.Duration <= TimeSpan.Zero || info.Width < 2 || info.Height < 2)
                throw new InvalidOperationException("Windows did not report a usable video size and duration.");
            metadata = info;
            TrimStartBox.Value = 0;
            TrimEndBox.Value = info.Duration.TotalSeconds;
            TrimStartBox.Maximum = TrimEndBox.Maximum = info.Duration.TotalSeconds;
            VideoInfoText.Text = Path.GetFileName(path) + $" · {info.Width} × {info.Height} · {FormatDuration(info.Duration)} · {(info.HasAudio ? "Audio" : "No audio")}";
            if (!busy) SetStatus("Video ready. Playback controls seek and change volume. Save a copy, trim, or export a smaller MP4.");
            UpdateCommands();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (closed || token.IsCancellationRequested || generation != videoGeneration || videoPath != path) return;
            metadata = null;
            VideoInfoText.Text = Path.GetFileName(path) + " · Metadata unavailable";
            VideoNotice.Title = "Current video details unavailable";
            VideoNotice.Severity = InfoBarSeverity.Warning;
            VideoNotice.Message = Path.GetFileName(path) + ": " + exception.Message + " Playback and Save current video copy remain available.";
            VideoNotice.IsOpen = true;
            if (!busy) SetStatus("Current video details are unavailable. Playback and Save current video copy remain available.");
            UpdateCommands();
        }
    }

    private async Task ObserveVideoFileReadAsync(Task<StorageFile> pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* Observe a late provider failure after UI timeout/close. */ }
        finally { _ = Interlocked.CompareExchange(ref videoFileRead, null, pending); }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && ReferenceEquals(player, sender)) ShowError("Windows could not play this video: " + args.ErrorMessage + " The original file is preserved and can still be copied.");
        });
    }

    private void OnSetStartClicked(object sender, RoutedEventArgs args)
    { if (!busy && player != null && metadata != null) TrimStartBox.Value = Math.Clamp(player.PlaybackSession.Position.TotalSeconds, 0, metadata.Duration.TotalSeconds); }
    private void OnSetEndClicked(object sender, RoutedEventArgs args)
    { if (!busy && player != null && metadata != null) TrimEndBox.Value = Math.Clamp(player.PlaybackSession.Position.TotalSeconds, 0, metadata.Duration.TotalSeconds); }

    private async void OnCopyVideoClicked(object sender, RoutedEventArgs args) => await CopyVideoPathAsync(videoPath, selected: false);
    private async void OnCopySelectedVideoClicked(object sender, RoutedEventArgs args) => await CopyVideoPathAsync(candidateCopyPath, selected: true);
    private Task CopyVideoPathAsync(string? sourcePath, bool selected) => RunAsync("Choose a filename for the " + (selected ? "selected file" : "current video") + " copy…", async () =>
    {
        if (sourcePath == null) return;
        string? destination = await ChooseSavePathAsync(Path.GetFileNameWithoutExtension(sourcePath) + "-copy", Path.GetExtension(sourcePath), "Original video");
        if (destination == null) { SetStatus("Save copy canceled."); return; }
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateCommands();
        SetStatus("Saving the original video copy…");
        await VideoReviewService.SaveCopyAsync(sourcePath, destination, operationCancellation.Token);
        PublishSaved(destination);
        SetStatus("Saved " + Path.GetFileName(destination) + ".");
    });

    private async void OnTrimVideoClicked(object sender, RoutedEventArgs args) => await RunAsync("Choose a filename for the trimmed copy…", async () =>
    {
        if (videoPath == null || metadata == null) return;
        if (!double.IsFinite(TrimStartBox.Value) || !double.IsFinite(TrimEndBox.Value) || TrimStartBox.Value < 0 ||
            TrimEndBox.Value <= TrimStartBox.Value || TrimEndBox.Value > metadata.Duration.TotalSeconds)
            throw new ArgumentException("Choose an end after the start, within this video's duration.");
        string? destination = await ChooseSavePathAsync(Path.GetFileNameWithoutExtension(videoPath) + "-trimmed", ".mp4", "MP4 video");
        if (destination == null) { SetStatus("Trim canceled."); return; }
        player?.Pause();
        SetStatus("Saving the exact trim range. Keep this window open until Windows finishes…");
        await VideoReviewService.TrimAsync(videoPath, destination, TimeSpan.FromSeconds(TrimStartBox.Value), TimeSpan.FromSeconds(TrimEndBox.Value));
        PublishSaved(destination);
        SetStatus("Saved trimmed copy " + Path.GetFileName(destination) + ". The original video is kept.");
    });

    private async void OnExportVideoClicked(object sender, RoutedEventArgs args) => await RunAsync("Choose a filename for the smaller MP4…", async () =>
    {
        if (videoPath == null || metadata == null) return;
        string? destination = await ChooseSavePathAsync(Path.GetFileNameWithoutExtension(videoPath) + "-smaller", ".mp4", "MP4 video");
        if (destination == null) { SetStatus("Export canceled."); return; }
        player?.Pause();
        var preset = ExportPresetBox.SelectedIndex switch
        {
            1 => global::SnipStudio.VideoExportPreset.Small480p,
            2 => global::SnipStudio.VideoExportPreset.TargetAttachmentSize,
            _ => global::SnipStudio.VideoExportPreset.Balanced720p
        };
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateCommands();
        VideoProgress.IsIndeterminate = false;
        var progress = new Progress<double>(value => { if (!closed) VideoProgress.Value = Math.Clamp(value * 100, 0, 100); });
        SetStatus("Exporting a smaller MP4. Attachment size is an estimate; the final result is checked.");
        var result = await VideoReviewService.ExportAsync(videoPath, destination, metadata, preset,
            (int)Math.Clamp(double.IsFinite(TargetSizeBox.Value) ? TargetSizeBox.Value : 20, 1, 1000), operationCancellation.Token, progress);
        PublishSaved(result.Path);
        SetStatus($"Saved {Path.GetFileName(result.Path)} · {result.Bytes / 1000000.0:0.##} MB." +
            (result.TargetMet ? "" : " The requested attachment target was exceeded; choose a larger target or trim the duration."));
    });

    private void OnCancelVideoClicked(object sender, RoutedEventArgs args)
    {
        if (!busy && pendingVideoOpen != null)
        {
            CancelPendingVideoOpen();
            SetStatus("Opening canceled. The current review and selected file are kept.");
            UpdateCommands();
            return;
        }
        operationCancellation?.Cancel();
        CancelVideoButton.IsEnabled = false;
        SetStatus("Cancel requested. Windows will stop at the next safe codec/file boundary.");
    }

    private async Task<string?> ChooseSavePathAsync(string name, string extension, string label)
    {
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary, SuggestedFileName = name,
            DefaultFileExtension = extension, CommitButtonText = "Save video"
        };
        if (!string.IsNullOrWhiteSpace(recordingFolder)) picker.SuggestedFolder = recordingFolder;
        picker.FileTypeChoices.Add(label, new List<string> { extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task<string> PrepareAutomaticRecordingPathAsync()
    {
        if (automaticRecordingPathRequest is { IsCompleted: false })
            throw new InvalidOperationException("The previous recording-folder request is still finishing. Check the folder and try again after it responds.");
        string folder = recordingFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos) || !Path.IsPathFullyQualified(videos))
                throw new DirectoryNotFoundException("Windows did not provide a Videos folder. Choose a recording folder in Settings.");
            folder = Path.Combine(videos, "Snip Studio WinUI3");
        }
        if (!Path.IsPathFullyQualified(folder))
            throw new DirectoryNotFoundException("Choose a full recording folder path in Settings.");
        CancellationToken token = lifetime.Token;
        Task<string> pending = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Snip Studio recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".mp4");
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("The generated recording filename is already in use. Try Start again.");
            return path;
        }, token);
        automaticRecordingPathRequest = pending;
        _ = ObserveAutomaticRecordingPathAsync(pending);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(8), token); }
        catch (TimeoutException)
        { throw new TimeoutException("The recording folder did not respond within eight seconds. No recording was started. Check the folder in Settings and try again."); }
    }

    private async Task ObserveAutomaticRecordingPathAsync(Task<string> pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* Observe a late folder-provider failure. */ }
        finally { _ = Interlocked.CompareExchange(ref automaticRecordingPathRequest, null, pending); }
    }

    private async Task RunAsync(string message, Func<Task> action)
    {
        if (closed || busy || recorder.IsActive && stopTask == null && !message.StartsWith("Finishing", StringComparison.Ordinal)) return;
        busy = true;
        VideoNotice.IsOpen = false;
        VideoNotice.Title = "The action could not finish";
        VideoNotice.Severity = InfoBarSeverity.Error;
        VideoProgress.Value = 0;
        VideoProgress.IsIndeterminate = true;
        VideoProgress.Visibility = Visibility.Visible;
        SetStatus(message);
        UpdateCommands();
        try { await action(); }
        catch (OperationCanceledException) { if (!closed) SetStatus("Canceled. The original video is kept."); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        { if (!closed) ShowError(exception.Message); }
        finally
        {
            busy = false;
            operationCancellation?.Dispose();
            operationCancellation = null;
            if (!closed) { VideoProgress.Visibility = Visibility.Collapsed; CommitReadyCandidate(); UpdateCommands(); }
        }
    }

    private void UpdateCommands()
    {
        if (closed) return;
        bool active = recorder.IsActive || stopTask != null;
        bool hasVideo = videoPath != null;
        bool hasSelection = candidateCopyPath != null;
        bool showReview = hasVideo || hasSelection;
        bool compact = captureWorkflow && !showReview && !recordingOptionsExpanded;
        RecordingSettingsPanel.Visibility = captureWorkflow && showReview ? Visibility.Collapsed : Visibility.Visible;
        AdvancedRecordingOptionsPanel.Visibility = !captureWorkflow || recordingOptionsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SelectedAreaSummary.Visibility = captureWorkflow ? Visibility.Visible : Visibility.Collapsed;
        RecordingOptionsButton.Visibility = captureWorkflow && !showReview ? Visibility.Visible : Visibility.Collapsed;
        RecordingOptionsButton.IsEnabled = !busy && !active;
        RecordingInstructions.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecordingHeading.FontSize = compact ? 20 : 24;
        RecordingHeading.Text = active ? "Recording" : hasVideo ? "Review video" : hasSelection ? "Open video" : "Record video";
        Title = active ? "Snip Studio — Recording" : hasVideo ? "Snip Studio — " + Path.GetFileName(videoPath) : "Snip Studio — Record video";
        PlaybackPanel.Visibility = ReviewOptionsPanel.Visibility = showReview ? Visibility.Visible : Visibility.Collapsed;
        CandidatePanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        TrimRow.Visibility = ExportRow.Visibility = VideoInfoText.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoPlayer.AreTransportControlsEnabled = hasVideo;
        UpdateRecordingLayoutBounds(RecordingRoot.ActualHeight);
        UpdateRecordingMinimums();
        StartRecordingButton.IsEnabled = !busy && !active;
        StopRecordingButton.IsEnabled = !busy && active;
        RegionBox.IsEnabled = SelectAreaButton.IsEnabled = SelectWindowButton.IsEnabled = FpsBox.IsEnabled = !busy && !active;
        MicrophoneCheck.IsEnabled = SystemAudioCheck.IsEnabled = CursorCheck.IsEnabled = !busy && !active;
        MicrophoneBox.IsEnabled = SystemAudioBox.IsEnabled = !busy && !active && !audioLoading;
        RefreshAudioButton.IsEnabled = !busy && !active && !audioLoading;
        OpenVideoButton.IsEnabled = !busy && !active;
        CopyVideoButton.IsEnabled = !busy && !active && videoPath != null;
        CopySelectedVideoButton.IsEnabled = !busy && !active && candidateCopyPath != null;
        TrimVideoButton.IsEnabled = ExportVideoButton.IsEnabled = !busy && !active && videoPath != null && metadata != null;
        TrimStartBox.IsEnabled = TrimEndBox.IsEnabled = SetStartButton.IsEnabled = SetEndButton.IsEnabled = !busy && !active && metadata != null;
        ExportPresetBox.IsEnabled = TargetSizeBox.IsEnabled = !busy && !active;
        CancelVideoButton.IsEnabled = busy && operationCancellation != null && !operationCancellation.IsCancellationRequested || !busy && !active && pendingVideoOpen != null;
        CancelVideoButton.Visibility = operationCancellation != null || pendingVideoOpen != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message) { if (!closed) VideoStatusText.Text = message; }
    private void ShowError(string message)
    {
        if (closed) return;
        VideoNotice.Title = "The action could not finish";
        VideoNotice.Severity = InfoBarSeverity.Error;
        VideoNotice.Message = message;
        VideoNotice.IsOpen = true;
        SetStatus("The action could not finish. Review the message above.");
    }

    private void OnRecordingMinimumRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateRecordingMinimums();

    private void UpdateRecordingMinimums()
    {
        if (closed || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        double scale = RecordingRoot.XamlRoot?.RasterizationScale ??
            RecordingWindowDpi(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        bool compact = captureWorkflow && videoPath == null && candidateCopyPath == null && !recordingOptionsExpanded;
        int width = (int)Math.Ceiling(640 * scale), height = (int)Math.Ceiling((compact ? 260 : 480) * scale);
        DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is not null)
        {
            width = Math.Min(width, display.WorkArea.Width);
            height = Math.Min(height, display.WorkArea.Height);
        }
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint RecordingWindowDpi(nint window);

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (AreaRow == null) return;
        UpdateRecordingMinimums();
        bool narrow = args.NewSize.Width < 900;
        Orientation direction = narrow ? Orientation.Vertical : Orientation.Horizontal;
        AreaRow.Orientation = DevicesRow.Orientation = TrimRow.Orientation = ExportRow.Orientation = direction;
        AudioChecksRow.Orientation = args.NewSize.Width < 520 ? Orientation.Vertical : Orientation.Horizontal;
        RecordActionsRow.Orientation = args.NewSize.Width < 430 ? Orientation.Vertical : Orientation.Horizontal;
        UpdateRecordingLayoutBounds(args.NewSize.Height);
    }

    private void UpdateRecordingLayoutBounds(double height)
    {
        // Setup, playback and editing share one body scroller. Fixed actions
        // and status remain reachable even at the recording window minimum.
        PlaybackPanel.Height = Math.Clamp(height * .40, 120, 400);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        if (!busy && !closePending && !recorder.IsActive && stopTask == null) return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    /// <summary>Coordinates application Exit without bypassing active recording/file ownership.</summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (closed) return true;
        if (busy || closePending)
        {
            SetStatus("Finish or cancel the current file operation before closing.");
            return false;
        }
        closePending = true;
        try
        {
            if (recorder.IsActive || stopTask != null)
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
                Activate();
                var dialog = new ContentDialog
                {
                    XamlRoot = RecordingRoot.XamlRoot, Title = "Stop recording before closing?",
                    Content = "Stop and finish saving the MP4, or keep recording.",
                    PrimaryButtonText = "Stop and save", CloseButtonText = "Keep recording", DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
                if (!await StopAndReviewAsync()) return false;
            }
            if (recorder.IsActive || stopTask != null || busy) return false;
            allowClose = true;
            Close();
            try { return await closeCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                if (!closed) SetStatus("Windows is still closing this video window. Try Exit again after it finishes.");
                return closed;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed) ShowError(exception.Message);
            return closed;
        }
        finally { closePending = false; }
    }

    private void ClearPlayback()
    {
        if (player != null)
        {
            player.MediaFailed -= OnMediaFailed;
            player.Pause();
            player.Source = null;
        }
        VideoPlayer.SetMediaPlayer(null);
        source?.Dispose();
        source = null;
        player?.Dispose();
        player = null;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        closed = true;
        if (minimumSizingRoot is not null)
        {
            minimumSizingRoot.Changed -= OnRecordingMinimumRootChanged;
            minimumSizingRoot = null;
        }
        CancelPendingVideoOpen();
        ReleaseRecordingBoundary();
        lifetime.Cancel();
        ++videoGeneration;
        metadataCancellation?.Cancel();
        metadataCancellation?.Dispose();
        operationCancellation?.Cancel();
        durationTimer.Stop();
        recorder.StatusChanged -= OnRecorderStatusChanged;
        recorder.Dispose();
        ClearPlayback();
        PublishRecordingState(false);
        lifetime.Dispose();
        closeCompletion.TrySetResult(true);
    }

    private void ReleaseRecordingBoundary()
    {
        // All callers run on this window's dispatcher. Never dispose native
        // strip HWNDs from the recorder worker or a late provider continuation.
        RecordingBoundaryOverlay? owned = recordingBoundary;
        recordingBoundary = null;
        if (owned == null) return;
        try { owned.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed) ShowError("The recording outline could not be closed completely: " + exception.Message);
        }
    }
}
