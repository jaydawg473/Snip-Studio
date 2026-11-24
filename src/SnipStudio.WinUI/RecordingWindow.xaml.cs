using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using SnipStudio.WinUI.Backend;
using Windows.ApplicationModel.DataTransfer;
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
    private CancellationTokenSource? recordingCountdown;
    private CancellationTokenSource? metadataCancellation;
    private readonly HashSet<string> trimOriginalPaths = new(StringComparer.OrdinalIgnoreCase);
    private MediaPlayer? player;
    private MediaSource? source;
    private CandidateVideoOpen? pendingVideoOpen;
    private string? candidateCopyPath;
    private global::SnipStudio.VideoMetadata? metadata;
    private RecordingRegion? customRegion;
    private string? videoPath;
    private bool busy, closed, allowClose, closePending, publishedRecordingState;
    private bool audioLoading, hasRequestedAudioDevices;
    private Task<string?>? stopTask;
    private Task<StorageFile>? videoFileRead;
    private Task<string>? automaticRecordingPathRequest;
    private int videoGeneration;
    private int recordingGeneration;
    private readonly string? initialPath;
    private readonly bool captureWorkflow;
    private long videoNoticeOpenToken;
    private string recordingFolder = "";
    private bool autoCopyRecordingChanges = true;
    private bool autoSaveOriginalRecordings = true;
    private bool askSaveEditedRecordings = true;
    private bool hideTaskbars;
    private bool keepPrimaryTaskbarVisible = true;
    private (int Generation, TimeSpan Start, TimeSpan End)? savedTrimRange;
    private XamlRoot? minimumSizingRoot;
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? RecordingSaved;
    public bool IsRecording => recorder.IsActive;
    public bool IsClosed => closed;
    internal bool CanCloseWithoutPrompt => !busy && !closePending && !recorder.IsActive && stopTask is null && !RequiresTrimConfirmation;

    private enum ReviewCopyType { None, Trimmed, Exported }

    private sealed class CandidateVideoOpen
    {
        public CandidateVideoOpen(string path, MediaPlayer player, MediaSource source, ReviewCopyType copyType,
            long? exportedBytes, CancellationToken token)
        {
            Path = path;
            Player = player;
            Source = source;
            CopyType = copyType;
            ExportedBytes = exportedBytes;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        public string Path { get; }
        public MediaPlayer Player { get; }
        public MediaSource Source { get; }
        public ReviewCopyType CopyType { get; }
        public long? ExportedBytes { get; }
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
        ConfigureFilmstrip();
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Resize(this.captureWorkflow && string.IsNullOrWhiteSpace(initialVideoPath)
            ? FitRecordingWindow(460, 128, compact: true)
            : FitRecordingWindow(1040, string.IsNullOrWhiteSpace(initialVideoPath) ? 480 : 820, compact: false));
        UpdateRecordingMinimums();
        videoNoticeOpenToken = VideoNotice.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) => UpdateCommands());
        AdvancedRecordingFlyout.Opening += async (_, _) =>
        {
            UpdateRecordingOptionsBounds();
            if (!hasRequestedAudioDevices) await RefreshAudioAsync();
        };
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        InstallerExitHook.Attach(this);
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

    public void ConfigureDefaults(bool microphone, bool systemAudio, string recordingFolder,
        string microphoneDeviceId = "", string systemAudioDeviceId = "", bool autoCopyRecordingChanges = true,
        bool autoSaveOriginalRecordings = true, bool askSaveEditedRecordings = true,
        bool hideTaskbars = false, bool keepPrimaryTaskbarVisible = true)
    {
        if (closed || busy || recorder.IsActive) return;
        if (!string.IsNullOrWhiteSpace(recordingFolder) && !Path.IsPathFullyQualified(recordingFolder))
            throw new ArgumentException("Choose a full recording folder path in Settings.", nameof(recordingFolder));
        this.recordingFolder = string.IsNullOrWhiteSpace(recordingFolder) ? "" : Path.GetFullPath(recordingFolder);
        this.autoCopyRecordingChanges = autoCopyRecordingChanges;
        this.autoSaveOriginalRecordings = autoSaveOriginalRecordings;
        this.askSaveEditedRecordings = askSaveEditedRecordings;
        this.hideTaskbars = hideTaskbars;
        this.keepPrimaryTaskbarVisible = keepPrimaryTaskbarVisible;
        MicrophoneCheck.IsChecked = microphone;
        SystemAudioCheck.IsChecked = systemAudio;
        SeedAudioDevice(MicrophoneBox, microphoneDeviceId);
        SeedAudioDevice(SystemAudioBox, systemAudioDeviceId);
        UpdateRecordingAudioTooltips();
    }

    private static void SeedAudioDevice(ComboBox box, string deviceId) =>
        FillDevices(box, box.Items.OfType<RecordingAudioDevice>().Where(device => device.Id.Length > 0).ToArray(), deviceId);

    private void PublishSaved(string path)
    {
        try { RecordingSaved?.Invoke(this, path); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            VideoNotice.Message = "The video was saved, but recent files could not be updated: " + exception.Message;
            VideoNotice.IsOpen = true;
        }
    }

    private async Task<bool> CopySavedRecordingAsync(string path)
    {
        PublishSaved(path);
        if (!autoCopyRecordingChanges) return false;
        try { return await CopyVideoFileAsync(path); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed)
            {
                VideoNotice.Title = "Video saved";
                VideoNotice.Severity = InfoBarSeverity.Warning;
                VideoNotice.Message = "The video was saved at " + path + ". Windows could not copy the file to the clipboard. " + exception.Message;
                VideoNotice.IsOpen = true;
            }
            return false;
        }
    }

    private async Task<bool> CopyVideoFileAsync(string path)
    {
        long intent = DesktopImageActions.BeginClipboardWrite();
        StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask().WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token);
        if (closed || !DesktopImageActions.IsCurrentClipboardWrite(intent)) return false;
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.Properties.Title = file.Name;
        data.SetStorageItems(new[] { file }, true);
        if (!Clipboard.SetContentWithOptions(data, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false }))
            throw new InvalidOperationException("Windows did not accept the clipboard file.");
        Clipboard.Flush();
        return true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        RecordingRoot.Loaded -= OnLoaded;
        minimumSizingRoot = RecordingRoot.XamlRoot;
        if (minimumSizingRoot is not null) minimumSizingRoot.Changed += OnRecordingMinimumRootChanged;
        UpdateRecordingMinimums();
        if (captureWorkflow)
        {
            RefreshRecordingPreview();
            return;
        }
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            await RunAsync("Opening video…", () => LoadVideoAsync(initialPath));
            if (closed || candidateCopyPath != null || videoPath != null) return;
        }
        await RefreshAudioAsync();
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
        {
            SelectedAreaSummary.Text = $"Selected area · {area.Region.Width} × {area.Region.Height} pixels";
            if (minimumSizingRoot is not null && !busy) RefreshRecordingPreview();
        }
    }

    private void OnRecordingAudioClicked(object sender, RoutedEventArgs args) => UpdateRecordingAudioTooltips();

    private void UpdateRecordingAudioTooltips()
    {
        ToolTipService.SetToolTip(MicrophoneCheck, MicrophoneCheck.IsChecked == true
            ? "Microphone on — click to mute" : "Microphone off — click to record microphone audio");
        ToolTipService.SetToolTip(SystemAudioCheck, SystemAudioCheck.IsChecked == true
            ? "System audio on — click to mute" : "System audio off — click to record system audio");
    }

    private async void OnSelectAreaClicked(object sender, RoutedEventArgs args) => await SelectRecordingRegionAsync(CaptureKind.Rectangle);
    private async void OnSelectWindowClicked(object sender, RoutedEventArgs args) => await SelectRecordingRegionAsync(CaptureKind.Window);

    private async void OnAdjustRecordingAreaClicked(object sender, RoutedEventArgs args)
    {
        if (closed || busy || recorder.IsActive || stopTask != null || RegionBox.SelectedItem is not RecordingDisplay current) return;
        AdvancedRecordingFlyout.Hide();
        await RunAsync("Adjust the recording area. Escape keeps the previous area.", async () =>
        {
            selectingRecordingRegion = true;
            ReleaseRecordingOutline(ref recordingPreview);
            try
            {
                CaptureRectangle? selected = await CaptureCoordinator.AdjustRecordingRegionAsync(
                    WinRT.Interop.WindowNative.GetWindowHandle(this), current.Region, lifetime.Token);
                if (closed) return;
                if (selected is { } region)
                {
                    AddCustomRegion(new(region.X, region.Y, region.Width, region.Height));
                    SetStatus("Recording area updated. Choose Start when ready.");
                }
                else SetStatus("Adjustment canceled. The previous recording area is kept.");
            }
            finally
            {
                selectingRecordingRegion = false;
                if (!closed)
                {
                    AppWindow.Show();
                    Activate();
                    RefreshRecordingPreview();
                }
            }
        });
    }

    private Task SelectRecordingRegionAsync(CaptureKind kind)
    {
        AdvancedRecordingFlyout.Hide();
        return RunAsync(kind == CaptureKind.Window ? "Select a window to record. Escape cancels." : "Select the recording area. Escape cancels.", async () =>
        {
            selectingRecordingRegion = true;
            ReleaseRecordingOutline(ref recordingPreview);
            try
            {
                CaptureRectangle? selected = await CaptureCoordinator.SelectRecordingRegionAsync(new CaptureRequest
                {
                    OwnerWindow = WinRT.Interop.WindowNative.GetWindowHandle(this), Kind = kind
                }, lifetime.Token);
                if (closed) return;
                if (selected is { } region)
                {
                    AddCustomRegion(new(region.X, region.Y, region.Width, region.Height));
                    SetStatus("Area selected. Choose Adjust area to refine it, or Start to record.");
                }
                else SetStatus("Area selection canceled. The previous area is kept.");
            }
            finally
            {
                selectingRecordingRegion = false;
                if (!closed)
                {
                    AppWindow.Show();
                    Activate();
                    RefreshRecordingPreview();
                }
            }
        });
    }

    private async void OnRefreshAudioClicked(object sender, RoutedEventArgs args) => await RefreshAudioAsync();
    private async Task RefreshAudioAsync()
    {
        if (closed || busy || recorder.IsActive || audioLoading) return;
        hasRequestedAudioDevices = true;
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
                FillDevices(MicrophoneBox, Array.Empty<RecordingAudioDevice>(), microphoneId);
                FillDevices(SystemAudioBox, Array.Empty<RecordingAudioDevice>(), outputId);
                ShowError(exception.Message + " Your audio selections are kept. Windows default devices remain selectable.");
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
        if (string.IsNullOrEmpty(selectedId)) { box.SelectedIndex = 0; return; }
        var selected = choices.FirstOrDefault(device => string.Equals(device.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            selected = new RecordingAudioDevice(selectedId, "Saved device (unavailable)");
            box.Items.Add(selected);
        }
        box.SelectedItem = selected;
    }

    private async void OnStartRecordingClicked(object sender, RoutedEventArgs args)
    {
        if (recordingCountdown is { } countdown)
        {
            countdown.Cancel();
            return;
        }

        await RunAsync(autoSaveOriginalRecordings ? "Preparing the recording…" : "Choose where to save the recording…", async () =>
        {
            if (RegionBox.SelectedItem is not RecordingDisplay area) throw new InvalidOperationException("Choose a monitor or select an area first.");
            string? path = autoSaveOriginalRecordings ? await PrepareAutomaticRecordingPathAsync()
                : await ChooseSavePathAsync("Snip Studio recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"), ".mp4", "MP4 video");
            if (path == null) { SetStatus("Recording canceled."); return; }
            ImageSaveTarget target = await RecordingController.PrepareSaveTargetAsync(path, !autoSaveOriginalRecordings, lifetime.Token);
            if (!await WaitForRecordingCountdownAsync()) return;
            if (captureWorkflow)
            {
                RecordingDestinationText.Text = "Recording destination: " + path;
                RecordingDestinationText.Visibility = Visibility.Visible;
            }
            CancelPendingVideoOpen();
            player?.Pause();
            RecordingSettings settings = new(MicrophoneCheck.IsChecked == true, SystemAudioCheck.IsChecked == true,
                CursorCheck.IsChecked == true, (int)Math.Clamp(double.IsFinite(FpsBox.Value) ? FpsBox.Value : 15, 5, 30),
                (MicrophoneBox.SelectedItem as RecordingAudioDevice)?.Id ?? "", (SystemAudioBox.SelectedItem as RecordingAudioDevice)?.Id ?? "")
            {
                HideTaskbars = hideTaskbars,
                KeepPrimaryTaskbarVisible = keepPrimaryTaskbarVisible
            };
            SetStatus("Starting recording…");
            ++recordingGeneration;
            CancellationToken token = lifetime.Token;
            try
            {
                PublishRecordingState(true);
                token.ThrowIfCancellationRequested();
                ReleaseRecordingOutline(ref recordingPreview);
                RecordingRegion region = await PrepareRecordingControlsAsync(area.Region, token);
                token.ThrowIfCancellationRequested();
                await recorder.StartPreparedAsync(region, target, settings, token);
                durationTimer.Start();
                SetStatus("Recording. Choose Stop to save.");
            }
            catch
            {
                ReleaseRecordingBoundary();
                if (!closed)
                {
                    RestoreRecordingControls();
                    PublishRecordingState(recorder.IsActive);
                    RefreshRecordingPreview();
                }
                throw;
            }
        });
    }

    private async Task<bool> WaitForRecordingCountdownAsync()
    {
        using var countdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        recordingCountdown = countdown;
        VideoProgress.Visibility = Visibility.Collapsed;
        UpdateCommands();
        StartRecordingButton.Focus(FocusState.Programmatic);
        try
        {
            for (int seconds = 3; seconds > 0; seconds--)
            {
                SetStatus($"Recording starts in {seconds}…");
                await Task.Delay(TimeSpan.FromSeconds(1), countdown.Token);
            }
            // Cancel can run after the timer completes but before this UI continuation.
            countdown.Token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Recording canceled.");
            return false;
        }
        finally
        {
            recordingCountdown = null;
            if (!closed)
            {
                if (!countdown.IsCancellationRequested) VideoProgress.Visibility = Visibility.Visible;
                UpdateCommands();
            }
        }
    }

    private void OnCancelRecordingCountdown(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (recordingCountdown is not { } countdown) return;
        args.Handled = true;
        countdown.Cancel();
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
                    RestoreRecordingControls();
                    PublishRecordingState(recorder.IsActive);
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
                await CopySavedRecordingAsync(completed);
                string[] warnings = new[] { recorder.LastAudioWarning, recorder.LastTaskbarWarning }
                    .OfType<string>().Where(message => message.Length > 0).ToArray();
                if (warnings.Length > 0)
                {
                    string otherWarning = VideoNotice.IsOpen ? "\n" + VideoNotice.Message : "";
                    VideoNotice.Title = "Recording saved with a warning";
                    VideoNotice.Severity = InfoBarSeverity.Warning;
                    VideoNotice.Message = string.Join("\n", warnings) + otherWarning;
                    VideoNotice.IsOpen = true;
                }
                await LoadVideoAsync(completed);
            }
            else
            {
                RefreshRecordingPreview();
                SetStatus("Recording stopped.");
            }
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
        finally
        {
            if (!closed)
            {
                durationTimer.Stop();
                RestoreRecordingControls();
                PublishRecordingState(false);
                RefreshRecordingPreview();
                UpdateCommands();
            }
        }
    }

    private async void OnOpenVideoClicked(object sender, RoutedEventArgs args) => await RunAsync("Choose a video…", async () =>
    {
        var picker = new FileOpenPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.VideosLibrary, CommitButtonText = "Open video" };
        if (!string.IsNullOrWhiteSpace(recordingFolder)) picker.SuggestedFolder = recordingFolder;
        foreach (string extension in SupportedVideoExtensions) picker.FileTypeFilter.Add(extension);
        var chosen = await picker.PickSingleFileAsync();
        if (chosen == null) { SetStatus("Open canceled. The current video is kept."); return; }
        if (!await ConfirmPendingTrimAsync("opening another video"))
        { SetStatus("The current video and trim range are kept."); return; }
        await LoadVideoAsync(chosen.Path);
    });

    private static readonly string[] SupportedVideoExtensions = [".mp4", ".mov", ".wmv", ".avi", ".m4v", ".webm", ".mkv"];

    internal static bool IsSupportedVideoPath(string path) =>
        SupportedVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private async Task LoadVideoAsync(string path, ReviewCopyType copyType = ReviewCopyType.None, long? exportedBytes = null)
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
            var candidate = new CandidateVideoOpen(fullPath, nextPlayer, nextSource, copyType, exportedBytes, lifetime.Token);
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
        SetStatus(copyType == ReviewCopyType.None
            ? "Checking the selected video's playback. Save selected file copy is available; the current review is kept until opening succeeds."
            : $"{copyType} copy saved. Checking playback before opening it for review…");
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
        if (candidate.CopyType == ReviewCopyType.None) trimOriginalPaths.Clear();
        else if (captureWorkflow) RecordingDestinationText.Text = $"Reviewing {candidate.CopyType.ToString().ToLowerInvariant()} copy: " + candidate.Path;
        candidateCopyPath = null;
        CandidatePanel.Visibility = Visibility.Collapsed;
        metadata = null;
        exportSourceBytes = candidate.ExportedBytes;
        exportSourceSizeRequest = null;
        exportSourceSizeError = null;
        ResetFilmstrip();
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
        VideoInfoText.Text = "Reading video details…";
        ToolTipService.SetToolTip(VideoInfoText, candidate.Path);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(VideoInfoText, candidate.Path);
        SetReviewMuteIcon(false);
        reviewPlaybackStatusUnavailable = false;
        Title = "Snip Studio — " + Path.GetFileName(candidate.Path);
        SetStatus("Reading video details. Playback and Save copy are available.");
        metadataCancellation?.Cancel();
        metadataCancellation?.Dispose();
        metadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = LoadMetadataAsync(candidate.Path, ++videoGeneration, candidate.ExportedBytes, metadataCancellation.Token);
        UpdateCommands();
        if (captureWorkflow)
        {
            if (previousPlayer == null && RecordingRoot.ActualHeight < 550) ExpandForVideoReview();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (closed || !ReferenceEquals(player, candidate.Player)) return;
                RecordingRoot.UpdateLayout();
                RecordingBodyScroll.ChangeView(null, 0d, null, true);
            });
        }
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

    private async Task LoadMetadataAsync(string path, int generation, long? exportedBytes, CancellationToken token)
    {
        try
        {
            var info = await VideoReviewService.InspectAsync(path, token);
            if (closed || token.IsCancellationRequested || generation != videoGeneration || videoPath != path) return;
            if (info.Duration <= TimeSpan.Zero || info.Width < 2 || info.Height < 2)
                throw new InvalidOperationException("Windows did not report a usable video size and duration.");
            metadata = info;
            BeginFilmstrip(path, generation, token);
            string size = exportedBytes is { } bytes ? " · " + FormatVideoSize(bytes) : "";
            VideoInfoText.Text = $"{info.Width} × {info.Height} · {FormatDuration(info.Duration)} · {(info.HasAudio ? "Audio" : "No audio")}{size}";
            if (!busy) SetStatus("Click the filmstrip to seek, or drag its blue handles to trim.");
            UpdateCommands();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (closed || token.IsCancellationRequested || generation != videoGeneration || videoPath != path) return;
            metadata = null;
            ResetFilmstrip();
            FilmstripMessage.Text = FilmstripDuration > 0
                ? "Video details unavailable. You can still seek or save a copy."
                : "Video details unavailable. Playback and Save copy remain available.";
            VideoInfoText.Text = "Video details unavailable";
            VideoNotice.Title = "Current video details unavailable";
            VideoNotice.Severity = InfoBarSeverity.Warning;
            VideoNotice.Message = Path.GetFileName(path) + ": " + exception.Message + " Playback and Save copy remain available.";
            VideoNotice.IsOpen = true;
            if (!busy) SetStatus("Current video details are unavailable. Playback and Save copy remain available.");
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
    { if (FilmstripAvailable) SetFilmstripTrim(true, filmstripPosition); }
    private void OnSetEndClicked(object sender, RoutedEventArgs args)
    { if (FilmstripAvailable) SetFilmstripTrim(false, filmstripPosition); }

    private async void OnCopyVideoClicked(object sender, RoutedEventArgs args) => await CopyVideoPathAsync(videoPath, selected: false);
    private async void OnCopySelectedVideoClicked(object sender, RoutedEventArgs args) => await CopyVideoPathAsync(candidateCopyPath, selected: true);
    private async void OnCopyVideoFileClicked(object sender, RoutedEventArgs args) => await RunAsync("Copying the video file…", async () =>
    {
        if (videoPath != null && await CopyVideoFileAsync(videoPath))
            SetStatus("Video file copied. Paste into a folder or an app that accepts files.");
    });

    private Task CopyVideoPathAsync(string? sourcePath, bool selected) => RunAsync("Choose a filename for the " + (selected ? "selected file" : "current video") + " copy…", async () =>
    {
        if (sourcePath == null) return;
        string? destination = await ChooseSavePathAsync(Path.GetFileNameWithoutExtension(sourcePath) + "-copy", Path.GetExtension(sourcePath), "Original video");
        if (destination == null) { SetStatus("Save copy canceled."); return; }
        ProtectTrimOriginal(destination);
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateCommands();
        SetStatus("Saving the original video copy…");
        await VideoReviewService.SaveCopyAsync(sourcePath, destination, operationCancellation.Token);
        PublishSaved(destination);
        SetStatus("Saved " + Path.GetFileName(destination) + ".");
    });

    private async void OnTrimVideoClicked(object sender, RoutedEventArgs args) => await RunAsync("Choose a filename for the trimmed copy…", async () =>
    {
        await SaveTrimmedCopyAsync(reviewCopy: true);
    });

    private bool RequiresTrimConfirmation => askSaveEditedRecordings && TryGetTrimRange(out var start, out var end) &&
        (start != TimeSpan.Zero || end != metadata!.Duration) && savedTrimRange != (videoGeneration, start, end);

    private async Task<bool> ConfirmPendingTrimAsync(string nextAction)
    {
        if (!RequiresTrimConfirmation) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = RecordingRoot.XamlRoot,
            Title = "Save the selected trim?",
            Content = "Save the selected range as a separate video before " + nextAction + ". Your original recording is kept.",
            PrimaryButtonText = "Save trimmed copy",
            SecondaryButtonText = "Discard range",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return (await dialog.ShowAsync()) switch
        {
            ContentDialogResult.Primary => await SaveTrimmedCopyAsync(reviewCopy: false),
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }

    private async Task<bool> SaveTrimmedCopyAsync(bool reviewCopy)
    {
        if (videoPath is not { } original || metadata == null) return false;
        if (!TryGetTrimRange(out var start, out var end))
            throw new ArgumentException("Choose an end after the start, within this video's duration.");
        if (start == TimeSpan.Zero && end == metadata.Duration) { SetStatus("Choose a shorter range to trim. Use Save copy for the full clip."); return false; }
        string? destination = await ChooseSavePathAsync(Path.GetFileNameWithoutExtension(original) + "-trimmed", ".mp4", "MP4 video");
        if (destination == null) { SetStatus("Trim canceled."); return false; }
        ProtectTrimOriginal(destination);
        player?.Pause();
        SetStatus("Saving the exact trim range. Keep this window open until Windows finishes…");
        await VideoReviewService.TrimAsync(original, destination, start, end);
        savedTrimRange = (videoGeneration, start, end);
        trimOriginalPaths.Add(original);
        bool copied = await CopySavedRecordingAsync(destination);
        if (!reviewCopy)
        {
            SetStatus("Trimmed copy saved." + (copied ? " Video file copied." : ""));
            return true;
        }
        await ReviewSavedCopyAsync(destination, ReviewCopyType.Trimmed);
        return true;
    }

    private async Task ReviewSavedCopyAsync(string path, ReviewCopyType copyType, long? exportedBytes = null)
    {
        try { await LoadVideoAsync(path, copyType, exportedBytes); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ShowError($"The copy was saved at {path}, but it could not be opened for review. The current clip is kept. {exception.Message}");
            SetStatus($"{copyType} copy saved. The current clip is kept.");
        }
    }

    private Task ExportReviewedVideoAsync(global::SnipStudio.VideoExportPreset preset, int targetMegabytes) => RunAsync("Checking video size…", async () =>
    {
        if (videoPath is not { } sourcePath) return;
        bool forEmail = preset == global::SnipStudio.VideoExportPreset.TargetAttachmentSize;
        long sourceBytes = await VideoReviewService.ReadSourceSizeAsync(sourcePath, lifetime.Token);
        var preparation = VideoReviewService.PrepareExport(sourcePath, sourceBytes, metadata, preset, targetMegabytes);
        if (!await ConfirmPendingTrimAsync("exporting a copy of the full video"))
        {
            SetStatus("Export canceled. The current video and trim range are kept.");
            return;
        }
        string suffix = forEmail ? "-email" : "-smaller";
        SetStatus(preparation.MaximumCopyBytes != null
            ? "The current MP4 fits. Choose where to save an email copy."
            : "Choose where to save the smaller MP4.");
        string? destination = await ChooseSavePathAsync(Path.GetFileNameWithoutExtension(sourcePath) + suffix, ".mp4", "MP4 video");
        if (destination == null) { SetStatus("Export canceled."); return; }
        ProtectTrimOriginal(destination);
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancellationToken token = operationCancellation.Token;
        UpdateCommands();
        long? exportedBytes = null;
        if (preparation.EncodingPlan is { } plan)
        {
            player?.Pause();
            VideoProgress.IsIndeterminate = false;
            var progress = new Progress<double>(value => { if (!closed) VideoProgress.Value = Math.Clamp(value * 100, 0, 100); });
            SetStatus(forEmail ? $"Compressing the MP4 to fit {targetMegabytes} MB…" : "Exporting a smaller MP4…");
            var exported = await VideoReviewService.ExportAsync(sourcePath, destination, plan, token, progress);
            exportedBytes = exported.Bytes;
        }
        else
        {
            SetStatus("Saving an email copy of the current MP4…");
            await VideoReviewService.SaveCopyAsync(sourcePath, destination, token, preparation.MaximumCopyBytes);
        }
        trimOriginalPaths.Add(sourcePath);
        await CopySavedRecordingAsync(destination);
        await ReviewSavedCopyAsync(destination, ReviewCopyType.Exported, exportedBytes);
    });

    private void ProtectTrimOriginal(string destination)
    {
        if (trimOriginalPaths.Contains(Path.GetFullPath(destination)))
            throw new ArgumentException("Choose a different filename to keep the original video.");
    }

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
            folder = Path.Combine(videos, "Screen Recordings");
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
        bool compact = captureWorkflow && !showReview;
        RecordingHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecordingSettingsPanel.Visibility = captureWorkflow || showReview ? Visibility.Collapsed : Visibility.Visible;
        RecordingBodyScroll.Visibility = compact && !VideoNotice.IsOpen ? Visibility.Collapsed : Visibility.Visible;
        RecordingBodyScroll.MaxHeight = compact ? 96 : double.PositiveInfinity;
        RecordingBodyRow.Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        RecordingRoot.Padding = new Thickness(compact ? 8 : 16);
        RecordingRoot.RowSpacing = compact ? 0 : 12;
        RecordingFooter.Spacing = compact ? 2 : 4;
        RecordActionsRow.Visibility = showReview && !active ? Visibility.Collapsed : Visibility.Visible;
        SelectedAreaSummary.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        StartRecordingButton.Content = recordingCountdown != null ? "Cancel" : compact ? "Start" : "Start recording";
        ToolTipService.SetToolTip(StartRecordingButton, recordingCountdown == null ? null : "Cancel the recording countdown (Escape)");
        StopRecordingButton.Content = compact ? "Stop" : "Stop and save";
        StartRecordingButton.MinWidth = StopRecordingButton.MinWidth = compact ? 80 : 0;
        StartRecordingButton.Visibility = compact && active ? Visibility.Collapsed : Visibility.Visible;
        AdjustRecordingAreaButton.Visibility = active || showReview ? Visibility.Collapsed : Visibility.Visible;
        AdjustRecordingAreaButton.IsEnabled = !busy && !active;
        StopRecordingButton.Visibility = compact && !active ? Visibility.Collapsed : Visibility.Visible;
        VideoStatusText.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        VideoStatusText.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        VideoStatusText.FontSize = compact ? 12 : 14;
        if (compact && CaptureAudioHost.Content is null)
        {
            AudioChoicesHost.Content = null;
            CaptureAudioHost.Content = AudioChecksRow;
        }
        else if (!compact && AudioChoicesHost.Content is null)
        {
            CaptureAudioHost.Content = null;
            AudioChoicesHost.Content = AudioChecksRow;
        }
        CaptureAudioHost.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        RecordingOptionsButton.IsEnabled = !busy && !active;
        UpdateRecordingAudioTooltips();
        RecordingHeading.FontSize = compact ? 20 : 24;
        RecordingHeading.Text = active ? "Recording" : hasVideo ? "Review video" : hasSelection ? "Open video" : "Record video";
        Title = active ? "Snip Studio — Recording" : hasVideo ? "Snip Studio — " + Path.GetFileName(videoPath) : "Snip Studio — Record video";
        ReviewOptionsPanel.Visibility = showReview ? Visibility.Visible : Visibility.Collapsed;
        CandidatePanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        FilmstripPanel.Visibility = VideoFileActions.Visibility = ReviewPlaybackBar.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        RecordingDestinationText.Visibility = !hasVideo && RecordingDestinationText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrimRow.Visibility = metadata == null ? Visibility.Collapsed : Visibility.Visible;
        ReviewPlayButton.IsEnabled = ReviewSoundButton.IsEnabled = !busy && !active && player != null;
        if (player != null && !filmstripTimer.IsEnabled) filmstripTimer.Start();
        UpdateRecordingLayoutBounds(RecordingRoot.ActualHeight);
        UpdateRecordingMinimums();
        StartRecordingButton.IsEnabled = recordingCountdown != null || !busy && !active;
        StopRecordingButton.IsEnabled = !busy && active;
        RegionBox.IsEnabled = SelectAreaButton.IsEnabled = SelectWindowButton.IsEnabled = FpsBox.IsEnabled = !busy && !active;
        MicrophoneCheck.IsEnabled = SystemAudioCheck.IsEnabled = CursorCheck.IsEnabled = !busy && !active;
        MicrophoneBox.IsEnabled = SystemAudioBox.IsEnabled = !busy && !active && !audioLoading;
        RefreshAudioButton.IsEnabled = !busy && !active && !audioLoading;
        OpenVideoButton.IsEnabled = !busy && !active;
        EditInClipchampButton.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        EditInClipchampButton.IsEnabled = !busy && !active && hasVideo;
        CopyVideoButton.IsEnabled = CopyVideoFileButton.IsEnabled = !busy && !active && videoPath != null;
        CopySelectedVideoButton.IsEnabled = !busy && !active && candidateCopyPath != null;
        ReviewExportButton.IsEnabled = !busy && !active && hasVideo && metadata != null;
        CompressEmailButton.IsEnabled = !busy && !active && hasVideo;
        UpdateVideoExportEstimates();
        FilmstripInput.IsEnabled = FilmstripAvailable;
        TrimStartBox.IsEnabled = TrimEndBox.IsEnabled = SetStartButton.IsEnabled = SetEndButton.IsEnabled = FilmstripAvailable && metadata != null;
        if (!FilmstripAvailable)
        {
            EndFilmstripDrag();
            filmstripSeekPending = false;
        }
        UpdateTrimCommand();
        ExportPresetBox.IsEnabled = TargetSizeBox.IsEnabled = !busy && !active;
        CancelVideoButton.IsEnabled = busy && operationCancellation != null && !operationCancellation.IsCancellationRequested || !busy && !active && pendingVideoOpen != null;
        CancelVideoButton.Visibility = operationCancellation != null || pendingVideoOpen != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message)
    {
        if (closed) return;
        VideoStatusText.Text = message;
        ToolTipService.SetToolTip(VideoStatusText, message);
    }
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
        bool compact = captureWorkflow && videoPath == null && candidateCopyPath == null;
        SizeInt32 minimum = FitRecordingWindow(compact ? 460 : 640, compact ? VideoNotice.IsOpen ? 260 : 128 : 480, compact);
        if (presenter.PreferredMinimumWidth != minimum.Width) presenter.PreferredMinimumWidth = minimum.Width;
        if (presenter.PreferredMinimumHeight != minimum.Height) presenter.PreferredMinimumHeight = minimum.Height;
    }

    private const double CompactActionWrapWidth = 390;

    private SizeInt32 FitRecordingWindow(int widthDip, int heightDip, bool compact)
    {
        double scale = RecordingRoot.XamlRoot?.RasterizationScale ??
            RecordingWindowDpi(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        int width = Math.Min((int)Math.Ceiling(widthDip * scale), display?.WorkArea.Width ?? int.MaxValue);
        // Reserve the second action row when the work area cannot fit the full bar and window frame.
        if (compact && width / scale < CompactActionWrapWidth + 16) heightDip += 40;
        int height = Math.Min((int)Math.Ceiling(heightDip * scale), display?.WorkArea.Height ?? int.MaxValue);
        return new SizeInt32(width, height);
    }

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint RecordingWindowDpi(nint window);

    private void UpdateRecordingOptionsBounds()
    {
        double scale = RecordingRoot.XamlRoot?.RasterizationScale ?? 1;
        DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is not null && double.IsFinite(scale) && scale > 0)
            RecordingOptionsScroll.MaxHeight = Math.Max(100, Math.Min(520, display.WorkArea.Height / scale - 80));
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (AreaRow == null || RecordUtilityActions == null) return;
        UpdateRecordingMinimums();
        bool stackTrimFields = args.NewSize.Width < 500;
        Grid.SetRow(TrimEndActions, stackTrimFields ? 1 : 0);
        Grid.SetColumn(TrimEndActions, stackTrimFields ? 0 : 1);
        Grid.SetColumnSpan(TrimStartActions, stackTrimFields ? 2 : 1);
        Grid.SetColumnSpan(TrimEndActions, stackTrimFields ? 2 : 1);
        Grid.SetRow(TrimRangeText, stackTrimFields ? 2 : 1);
        UpdateReviewActionLayout(args.NewSize.Width);
        bool compact = captureWorkflow && videoPath == null && candidateCopyPath == null;
        AudioChecksRow.Orientation = !compact && args.NewSize.Width < 520 ? Orientation.Vertical : Orientation.Horizontal;
        bool stackActions = args.NewSize.Width < (compact ? CompactActionWrapWidth : 430);
        Grid.SetColumnSpan(RecordPrimaryActions, stackActions ? 2 : 1);
        Grid.SetRow(RecordUtilityActions, stackActions ? 1 : 0);
        Grid.SetColumn(RecordUtilityActions, stackActions ? 0 : 1);
        Grid.SetColumnSpan(RecordUtilityActions, stackActions ? 2 : 1);
        RecordActionsRow.RowSpacing = stackActions ? 6 : 0;
        UpdateRecordingLayoutBounds(args.NewSize.Height);
    }

    private void UpdateRecordingLayoutBounds(double height)
    {
        // Reserve vertical space for the filmstrip and exact trim controls as the review window shrinks.
        PlaybackPanel.Height = Math.Max(120, height - 405);
    }

    private void ExpandForVideoReview()
    {
        double scale = RecordingRoot.XamlRoot?.RasterizationScale ?? 1;
        DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null || !double.IsFinite(scale) || scale <= 0) return;
        AppWindow.Resize(new SizeInt32(Math.Min(display.WorkArea.Width, (int)Math.Ceiling(1040 * scale)),
            Math.Min(display.WorkArea.Height, (int)Math.Ceiling(820 * scale))));
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose || closed) return;
        if (CanCloseWithoutPrompt) return;
        args.Cancel = true;
        await RequestCloseAsync();
    }

    /// <summary>Coordinates application Exit without bypassing active recording/file ownership.</summary>
    public async Task<bool> RequestCloseAsync(bool silent = false)
    {
        if (closed) return true;
        if (silent && !CanCloseWithoutPrompt) return false;
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
            if (RequiresTrimConfirmation)
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
                Activate();
                bool readyToClose = false;
                await RunAsync("Checking the selected trim…", async () => readyToClose = await ConfirmPendingTrimAsync("closing"));
                if (!readyToClose) { SetStatus("The current video and trim range are kept."); return false; }
            }
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
        VideoNotice.UnregisterPropertyChangedCallback(InfoBar.IsOpenProperty, videoNoticeOpenToken);
        if (minimumSizingRoot is not null)
        {
            minimumSizingRoot.Changed -= OnRecordingMinimumRootChanged;
            minimumSizingRoot = null;
        }
        CancelPendingVideoOpen();
        ReleaseRecordingOutline(ref recordingPreview);
        ReleaseRecordingBoundary();
        RestoreRecordingControls();
        lifetime.Cancel();
        ++videoGeneration;
        metadataCancellation?.Cancel();
        metadataCancellation?.Dispose();
        operationCancellation?.Cancel();
        durationTimer.Stop();
        filmstripTimer.Stop();
        filmstripTimer.Tick -= OnFilmstripTick;
        EndFilmstripDrag();
        recorder.StatusChanged -= OnRecorderStatusChanged;
        recorder.Dispose();
        ClearPlayback();
        PublishRecordingState(false);
        lifetime.Dispose();
        closeCompletion.TrySetResult(true);
    }

    private void ReleaseRecordingBoundary() => ReleaseRecordingOutline(ref recordingBoundary);

    private void ReleaseRecordingOutline(ref RecordingBoundaryOverlay? outline)
    {
        // All callers run on this window's dispatcher. Never dispose native
        // strip HWNDs from the recorder worker or a late provider continuation.
        RecordingBoundaryOverlay? owned = outline;
        outline = null;
        if (owned == null) return;
        try { owned.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!closed) ShowError("The recording outline could not be closed completely: " + exception.Message);
        }
    }
}
