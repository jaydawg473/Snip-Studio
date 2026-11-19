#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio.WinUI.Backend;

/// <summary>Physical desktop pixels, including negative coordinates on secondary monitors.</summary>
public readonly record struct RecordingRegion(int X, int Y, int Width, int Height)
{
    internal Rectangle Rectangle => new(X, Y, Width, Height);
}

public sealed record RecordingDisplay(string Name, RecordingRegion Region, bool IsPrimary);
public sealed record RecordingAudioDevice(string Id, string Name);
public sealed record RecordingSettings(bool Microphone = true, bool SystemAudio = true,
    bool IncludeCursor = true, int FramesPerSecond = 15, string MicrophoneDeviceId = "", string SystemAudioDeviceId = "");
public sealed record RecordingProgress(bool IsActive, TimeSpan Duration, string Message, Exception? Error,
    string? DestinationPath, string? RecoveryPath);

/// <summary>
/// Owns one native recorder at a time. Native finalization is never abandoned in
/// order to start a competing session. Events may originate on the capture worker.
/// </summary>
public sealed class RecordingController : IDisposable
{
    private static readonly SemaphoreSlim startupSlots = new(2, 2);
    private readonly object gate = new();
    private global::SnipStudio.ScreenRecorder? recorder;
    private bool disposed;
    private bool starting;
    public event EventHandler<RecordingProgress>? StatusChanged;

    public bool IsActive { get { lock (gate) return starting || recorder != null; } }
    public TimeSpan Duration { get { lock (gate) return recorder?.Duration ?? TimeSpan.Zero; } }
    public string? DestinationPath { get { lock (gate) return recorder?.DestinationPath; } }
    public static RecordingRegion DesktopRegion
    {
        get { Rectangle area = SystemInformation.VirtualScreen; return new(area.X, area.Y, area.Width, area.Height); }
    }
    public static IReadOnlyList<RecordingDisplay> GetDisplays() => Screen.AllScreens.Select(screen =>
        new RecordingDisplay(screen.DeviceName, new(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height), screen.Primary)).ToArray();
    public static IReadOnlyList<RecordingAudioDevice> GetMicrophones() => global::SnipStudio.AudioCaptureMixer.GetMicrophoneDevices()
        .Select(device => new RecordingAudioDevice(device.Id ?? "", device.Name ?? "Microphone")).ToArray();
    public static IReadOnlyList<RecordingAudioDevice> GetSystemAudioDevices() => global::SnipStudio.AudioCaptureMixer.GetSystemAudioDevices()
        .Select(device => new RecordingAudioDevice(device.Id ?? "", device.Name ?? "Output device")).ToArray();

    public async Task StartAsync(RecordingRegion region, string destination, RecordingSettings options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (region.Width < 2 || region.Height < 2) throw new ArgumentException("Select a recording area at least 2 × 2 pixels.", nameof(region));
        global::SnipStudio.ScreenRecorder current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recorder != null || starting) throw new InvalidOperationException("The previous recording is still active or finishing.");
            current = new global::SnipStudio.ScreenRecorder();
            recorder = current;
            starting = true;
        }
        current.StatusChanged += (_, args) =>
        {
            lock (gate) { if (disposed || !ReferenceEquals(recorder, current)) return; }
            StatusChanged?.Invoke(this, new(args.IsRecording, args.Duration, args.Message, args.Error,
                current.DestinationPath, current.RecoveryPath));
        };
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var nativeOptions = new global::SnipStudio.ScreenRecordingOptions
            {
                RecordMicrophone = options.Microphone, RecordSystemAudio = options.SystemAudio,
                IncludeCursor = options.IncludeCursor, FramesPerSecond = options.FramesPerSecond,
                MicrophoneDeviceId = options.MicrophoneDeviceId ?? "", SystemAudioDeviceId = options.SystemAudioDeviceId ?? ""
            };
            Task ready = StartCoreAsync(current, region, destination, nativeOptions, startupCancellation.Token);
            _ = ObservePendingStartupAsync(ready);
            await ready.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            startupCancellation.Cancel();
            // A codec/device open cannot always be interrupted. Signal stop,
            // retain this session's lease and observe its eventual completion.
            _ = ObserveFailedStartAsync(current);
            throw;
        }
        finally { lock (gate) { if (ReferenceEquals(recorder, current)) starting = false; } }
    }

    private static async Task ObservePendingStartupAsync(Task pending)
    { try { await pending.ConfigureAwait(false); } catch (Exception) { /* The waiting UI receives the original failure or timeout. */ } }

    private static async Task StartCoreAsync(global::SnipStudio.ScreenRecorder current, RecordingRegion region, string destination,
        global::SnipStudio.ScreenRecordingOptions options, CancellationToken token)
    {
        await startupSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Destination validation may touch an unavailable network share.
            // It shares the same bounded lease as device/encoder initialization.
            await Task.Run(() => current.StartAsync(region.Rectangle, destination, options), token).ConfigureAwait(false);
        }
        finally { startupSlots.Release(); }
    }

    private async Task ObserveFailedStartAsync(global::SnipStudio.ScreenRecorder current)
    {
        try { await current.StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* The initiating call already reports the startup failure. */ }
        finally { Release(current); }
    }

    public async Task<string?> StopAsync()
    {
        global::SnipStudio.ScreenRecorder? current;
        lock (gate) current = recorder;
        if (current == null) return null;
        try
        {
            await current.StopAsync().ConfigureAwait(false);
            return current.DestinationPath;
        }
        finally { Release(current); }
    }

    private void Release(global::SnipStudio.ScreenRecorder current)
    {
        lock (gate)
        {
            if (!ReferenceEquals(recorder, current)) return;
            recorder = null;
            starting = false;
        }
        current.Dispose();
    }

    public void Dispose()
    {
        global::SnipStudio.ScreenRecorder? current;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            current = recorder;
        }
        if (current != null) _ = ObserveFailedStartAsync(current);
    }
}
