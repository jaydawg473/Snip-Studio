using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SnipStudio.WinUI.Backend;
using Windows.Storage.Streams;
using Windows.System;

namespace SnipStudio.WinUI;

public sealed partial class RecordingWindow
{
    private enum FilmstripDrag { None, Playhead, Start, End }

    private readonly DispatcherTimer filmstripTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private FilmstripDrag filmstripDrag;
    private Pointer? filmstripPointer;
    private double filmstripPosition, dragPosition, dragStart, dragEnd;
    private long showRequestedPositionUntil;
    private bool filmstripSeekPending, seekWarningShown;

    private void ConfigureFilmstrip()
    {
        TrimStartBox.ValueChanged += OnTrimValueChanged;
        TrimEndBox.ValueChanged += OnTrimValueChanged;
        filmstripTimer.Tick += OnFilmstripTick;
    }

    private void ResetFilmstrip()
    {
        EndFilmstripDrag();
        filmstripTimer.Stop();
        filmstripSeekPending = seekWarningShown = false;
        showRequestedPositionUntil = 0;
        filmstripPosition = 0;
        FilmstripFrames.Children.Clear();
        FilmstripFrames.ColumnDefinitions.Clear();
        FilmstripFrames.Visibility = Visibility.Collapsed;
        FilmstripMarks.Visibility = Visibility.Collapsed;
        FilmstripMessage.Visibility = Visibility.Visible;
        FilmstripMessage.Text = "Reading video details…";
        ToolTipService.SetToolTip(FilmstripMessage, null);
        FilmstripTime.Text = "";
    }

    private void BeginFilmstrip(string path, int generation, CancellationToken token)
    {
        if (metadata is not { } info) return;
        double duration = info.Duration.TotalSeconds;
        double maximum = Math.Ceiling(duration * 1000) / 1000;
        TrimStartBox.Maximum = TrimEndBox.Maximum = maximum;
        TrimStartBox.Value = 0;
        TrimEndBox.Value = maximum;
        FilmstripMessage.Text = "Reading filmstrip frames…";
        FilmstripMarks.Visibility = Visibility.Visible;
        UpdateFilmstripVisuals();
        filmstripTimer.Start();
        _ = LoadFilmstripAsync(path, generation, token);
    }

    private async Task LoadFilmstripAsync(string path, int generation, CancellationToken token)
    {
        bool Current() => !closed && !token.IsCancellationRequested && generation == videoGeneration && path == videoPath;
        try
        {
            byte[][] thumbnails = await VideoReviewService.ReadFilmstripAsync(path, token);
            var images = new List<BitmapImage>(thumbnails.Length);
            foreach (byte[] png in thumbnails)
            {
                if (!Current()) return;
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(png);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                images.Add(image);
            }
            if (!Current()) return;

            foreach (var image in images)
            {
                int column = FilmstripFrames.ColumnDefinitions.Count;
                FilmstripFrames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var thumbnail = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 1, 0) };
                Grid.SetColumn(thumbnail, column);
                FilmstripFrames.Children.Add(thumbnail);
            }
            FilmstripFrames.Visibility = Visibility.Visible;
            FilmstripMessage.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            if (!Current()) return;
            FilmstripMessage.Text = "Thumbnails unavailable. Scrubbing and trim times are still available.";
            ToolTipService.SetToolTip(FilmstripMessage, exception.Message);
        }
    }

    private double FilmstripDuration => metadata?.Duration.TotalSeconds ?? 0;
    private double FilmstripFrameStep => 1d / Math.Max(1, metadata?.FramesPerSecond ?? 1);
    private bool FilmstripAvailable => !closed && !busy && !recorder.IsActive && stopTask == null && FilmstripDuration > 0 && player != null;

    private void OnFilmstripTrackSizeChanged(object sender, SizeChangedEventArgs args) => UpdateFilmstripVisuals();

    private void UpdateFilmstripVisuals()
    {
        double duration = FilmstripDuration;
        double width = FilmstripTrack.ActualWidth;
        double height = FilmstripTrack.ActualHeight;
        if (duration <= 0 || width <= 0 || height <= 0) return;
        double start = FilmstripX(TrimStartBox.Value);
        double end = FilmstripX(TrimEndBox.Value);
        bool validRange = end > start;
        FilmstripBeforeRange.Visibility = FilmstripAfterRange.Visibility = FilmstripRange.Visibility = validRange ? Visibility.Visible : Visibility.Collapsed;
        FilmstripBeforeRange.Width = start;
        FilmstripAfterRange.Width = width - end;
        FilmstripRange.Width = Math.Max(0, end - start);
        Canvas.SetLeft(FilmstripAfterRange, end);
        Canvas.SetLeft(FilmstripRange, start);
        Canvas.SetLeft(FilmstripStartHandle, start - FilmstripStartHandle.Width / 2);
        Canvas.SetLeft(FilmstripEndHandle, end - FilmstripEndHandle.Width / 2);
        Canvas.SetLeft(FilmstripPlayhead, FilmstripX(filmstripPosition) - FilmstripPlayhead.Width / 2);
        FilmstripBeforeRange.Height = FilmstripAfterRange.Height = FilmstripRange.Height = height;
        FilmstripStartHandle.Height = FilmstripEndHandle.Height = FilmstripPlayhead.Height = height;
        FilmstripTime.Text = $"{FormatFilmstripTime(filmstripPosition)} / {FormatFilmstripTime(duration)}";
    }

    private double FilmstripX(double seconds) => FilmstripTrack.ActualWidth *
        Math.Clamp(double.IsFinite(seconds) && FilmstripDuration > 0 ? seconds / FilmstripDuration : 0, 0, 1);

    private static string FormatFilmstripTime(double seconds)
    {
        long wholeSeconds = (long)Math.Max(0, seconds);
        return wholeSeconds >= 3600
            ? $"{wholeSeconds / 3600}:{wholeSeconds / 60 % 60:00}:{wholeSeconds % 60:00}.{(int)(seconds % 1 * 10)}"
            : $"{wholeSeconds / 60:00}:{wholeSeconds % 60:00}.{(int)(seconds % 1 * 10)}";
    }

    private void OnFilmstripPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(FilmstripTrack);
        if (!FilmstripAvailable || filmstripPointer != null || !point.Properties.IsLeftButtonPressed || FilmstripTrack.ActualWidth <= 0) return;
        if (!FilmstripInput.CapturePointer(args.Pointer)) return;
        FilmstripInput.Focus(FocusState.Pointer);
        filmstripPointer = args.Pointer;
        dragPosition = filmstripPosition;
        dragStart = TrimStartBox.Value;
        dragEnd = TrimEndBox.Value;
        double startDistance = Math.Abs(point.Position.X - FilmstripX(dragStart));
        double endDistance = Math.Abs(point.Position.X - FilmstripX(dragEnd));
        filmstripDrag = Math.Min(startDistance, endDistance) > 10
            ? FilmstripDrag.Playhead : startDistance <= endDistance ? FilmstripDrag.Start : FilmstripDrag.End;
        ApplyFilmstripPointer(point.Position.X);
        FlushFilmstripSeek();
        args.Handled = true;
    }

    private void OnFilmstripMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!FilmstripAvailable || filmstripPointer?.PointerId != args.Pointer.PointerId) return;
        ApplyFilmstripPointer(args.GetCurrentPoint(FilmstripTrack).Position.X);
        args.Handled = true;
    }

    private void OnFilmstripReleased(object sender, PointerRoutedEventArgs args)
    {
        if (filmstripPointer?.PointerId != args.Pointer.PointerId) return;
        if (FilmstripAvailable)
        {
            ApplyFilmstripPointer(args.GetCurrentPoint(FilmstripTrack).Position.X);
            FlushFilmstripSeek();
        }
        EndFilmstripDrag();
        args.Handled = true;
    }

    private void OnFilmstripCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (filmstripPointer?.PointerId != args.Pointer.PointerId) return;
        CancelFilmstripDrag();
        args.Handled = true;
    }

    private void OnFilmstripCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (filmstripPointer?.PointerId == args.Pointer.PointerId) EndFilmstripDrag();
    }

    private void EndFilmstripDrag()
    {
        Pointer? pointer = filmstripPointer;
        filmstripPointer = null;
        filmstripDrag = FilmstripDrag.None;
        if (pointer != null) FilmstripInput.ReleasePointerCapture(pointer);
    }

    private void CancelFilmstripDrag()
    {
        if (filmstripPointer == null) return;
        TrimStartBox.Value = dragStart;
        TrimEndBox.Value = dragEnd;
        QueueFilmstripSeek(dragPosition);
        FlushFilmstripSeek();
        EndFilmstripDrag();
    }

    private void ApplyFilmstripPointer(double x)
    {
        double width = FilmstripTrack.ActualWidth;
        if (width <= 0) return;
        double seconds = FilmstripDuration * Math.Clamp(x / width, 0, 1);
        if (filmstripDrag == FilmstripDrag.Start) seconds = SetFilmstripTrim(true, seconds);
        if (filmstripDrag == FilmstripDrag.End) seconds = SetFilmstripTrim(false, seconds);
        QueueFilmstripSeek(seconds);
    }

    private void OnFilmstripKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!FilmstripAvailable) return;
        if (args.Key == VirtualKey.Escape && filmstripPointer != null)
        {
            CancelFilmstripDrag();
            args.Handled = true;
            return;
        }
        double next = args.Key switch
        {
            VirtualKey.Left => filmstripPosition - FilmstripFrameStep,
            VirtualKey.Right => filmstripPosition + FilmstripFrameStep,
            VirtualKey.Home => 0,
            VirtualKey.End => FilmstripDuration,
            _ => double.NaN
        };
        if (!double.IsFinite(next)) return;
        QueueFilmstripSeek(next);
        FlushFilmstripSeek();
        args.Handled = true;
    }

    private void QueueFilmstripSeek(double seconds)
    {
        filmstripPosition = Math.Clamp(seconds, 0, FilmstripDuration);
        filmstripSeekPending = true;
        UpdateFilmstripVisuals();
    }

    private void FlushFilmstripSeek()
    {
        if (!filmstripSeekPending || player is not { } playing || !FilmstripAvailable) return;
        filmstripSeekPending = false;
        try
        {
            if (!playing.PlaybackSession.CanSeek) { ReportFilmstripSeekFailure(); return; }
            playing.Pause();
            playing.PlaybackSession.Position = TimeSpan.FromSeconds(filmstripPosition);
            showRequestedPositionUntil = Environment.TickCount64 + 450;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ReportFilmstripSeekFailure();
        }
    }

    private void ReportFilmstripSeekFailure()
    {
        if (seekWarningShown) return;
        seekWarningShown = true;
        SetStatus("Windows could not seek this video. You can still enter trim times or save a copy.");
    }

    private void OnFilmstripTick(object? sender, object args)
    {
        if (!FilmstripAvailable || player is not { } playing) return;
        if (filmstripSeekPending) { FlushFilmstripSeek(); return; }
        if (filmstripPointer != null || Environment.TickCount64 < showRequestedPositionUntil) return;
        double position;
        try { position = Math.Clamp(playing.PlaybackSession.Position.TotalSeconds, 0, FilmstripDuration); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            ReportFilmstripSeekFailure();
            return;
        }
        if (Math.Abs(position - filmstripPosition) < .001) return;
        filmstripPosition = position;
        UpdateFilmstripVisuals();
    }

    private double SetFilmstripTrim(bool start, double seconds)
    {
        double duration = FilmstripDuration;
        double minimum = Math.Min(duration, Math.Max(.001, FilmstripFrameStep));
        double startSeconds = double.IsFinite(TrimStartBox.Value) ? TrimStartBox.Value : 0;
        double endSeconds = double.IsFinite(TrimEndBox.Value) ? Math.Min(duration, TrimEndBox.Value) : duration;
        if (start)
        {
            double maximum = Math.Max(0, endSeconds - minimum);
            TrimStartBox.Value = Math.Min(Math.Round(seconds, 3), Math.Floor(maximum * 1000) / 1000);
            return TrimStartBox.Value;
        }

        double earliest = Math.Min(duration, startSeconds + minimum);
        TrimEndBox.Value = Math.Max(Math.Round(seconds, 3), Math.Ceiling(earliest * 1000) / 1000);
        return Math.Min(duration, TrimEndBox.Value);
    }

    private void OnTrimValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateFilmstripVisuals();
        UpdateTrimCommand();
    }

    private bool TryGetTrimRange(out TimeSpan start, out TimeSpan end)
    {
        start = end = TimeSpan.Zero;
        if (metadata is not { } info || !double.IsFinite(TrimStartBox.Value) || !double.IsFinite(TrimEndBox.Value) ||
            TrimStartBox.Value < 0 || TrimStartBox.Value >= info.Duration.TotalSeconds || TrimEndBox.Value <= TrimStartBox.Value) return false;
        start = TimeSpan.FromSeconds(TrimStartBox.Value);
        end = TrimEndBox.Value >= info.Duration.TotalSeconds ? info.Duration : TimeSpan.FromSeconds(TrimEndBox.Value);
        return end > start;
    }

    private void UpdateTrimCommand()
    {
        if (!TryGetTrimRange(out var start, out var end))
        {
            TrimRangeText.Text = "Choose an end after the start.";
            TrimVideoButton.IsEnabled = false;
            return;
        }
        bool wholeClip = start == TimeSpan.Zero && end == metadata!.Duration;
        TrimRangeText.Text = wholeClip ? "Full clip. Drag a handle to trim." : $"Selected length: {(end - start).TotalSeconds:0.###} s";
        TrimVideoButton.IsEnabled = FilmstripAvailable && !wholeClip;
    }
}
