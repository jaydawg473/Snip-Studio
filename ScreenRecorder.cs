using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed class ScreenRecordingOptions
    {
        public bool RecordMicrophone = true;
        public bool RecordSystemAudio = true;
        public bool IncludeCursor = true;
        public int FramesPerSecond = 15;
        public string MicrophoneDeviceId = "";
        public string SystemAudioDeviceId = "";
        internal Action BeforeFrameCapture;
        internal bool HideTaskbars;
        internal bool KeepPrimaryTaskbarVisible = true;
    }

    public sealed class RecordingStatusEventArgs : EventArgs
    {
        public bool IsRecording { get; private set; }
        public TimeSpan Duration { get; private set; }
        public string Message { get; private set; }
        public Exception Error { get; private set; }
        internal RecordingStatusEventArgs(bool active, TimeSpan duration, string message, Exception error)
        { IsRecording = active; Duration = duration; Message = message; Error = error; }
    }

    public sealed class RecordingSaveException : IOException
    {
        public string RecoveryPath { get; private set; }
        internal RecordingSaveException(string recoveryPath, string destinationPath, Exception inner)
            : base(recoveryPath != null
                ? "The recording finished, but it could not be saved to the chosen filename. Your completed recording is preserved at:\n\n" + recoveryPath + "\n\n" + inner.Message
                : "The recording finished, but publishing it could not be verified. Check the chosen filename:\n\n" + destinationPath + "\n\n" + inner.Message, inner)
        { RecoveryPath = recoveryPath; }
    }

    public sealed class ScreenRecorder : IDisposable
    {
        private readonly object gate = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Action<Bitmap, Rectangle, bool> captureFrame;
        private readonly Action<Thread> startWorker;
        private readonly Func<ScreenRecordingOptions, IRecordingAudio> createAudio;
        private Stopwatch clock = new Stopwatch();
        private Thread worker;
        private TaskCompletionSource<bool> finished;
        private volatile bool recording;
        private bool disposed, starting;
        private long stoppedAt = -1;
        private Size outputSize;
        private Rectangle sourceRegion;
        private int fps = 15;
        private Exception lastError;
        private IRecordingAudio activeAudio;
        public event EventHandler<RecordingStatusEventArgs> StatusChanged;
        public bool IsRecording { get { return recording; } }
        public TimeSpan Duration { get { lock (gate) return stoppedAt >= 0 ? TimeSpan.FromTicks(stoppedAt) : activeAudio == null ? clock.Elapsed : activeAudio.Duration; } }
        public Size OutputSize { get { return outputSize; } }
        // The indicator uses the exact physical rectangle accepted by the worker,
        // including clipping at the desktop edge. It never guesses from output size.
        public Rectangle SourceRegion { get { lock (gate) return sourceRegion; } }
        public int FramesPerSecond { get { return fps; } }
        public Exception Error { get { return lastError; } }
        public string DestinationPath { get; private set; }
        public string RecoveryPath { get; private set; }
        public string AudioWarning { get; private set; }
        internal string TaskbarWarning { get; private set; }

        public ScreenRecorder() : this(CaptureFrame) { }
        internal ScreenRecorder(Action<Bitmap, Rectangle, bool> captureFrame)
            : this(captureFrame, StartWorkerThread) { }
        internal ScreenRecorder(Action<Bitmap, Rectangle, bool> captureFrame, Action<Thread> startWorker)
            : this(captureFrame, startWorker, CreateAudio) { }
        internal ScreenRecorder(Action<Bitmap, Rectangle, bool> captureFrame, Action<Thread> startWorker,
            Func<ScreenRecordingOptions, IRecordingAudio> createAudio)
        {
            if (captureFrame == null) throw new ArgumentNullException("captureFrame");
            if (startWorker == null) throw new ArgumentNullException("startWorker");
            if (createAudio == null) throw new ArgumentNullException("createAudio");
            this.captureFrame = captureFrame;
            this.startWorker = startWorker;
            this.createAudio = createAudio;
        }

        private static void StartWorkerThread(Thread thread) { thread.Start(); }

        private static IRecordingAudio CreateAudio(ScreenRecordingOptions options)
        {
            return new AudioCaptureMixer(options.RecordMicrophone, options.RecordSystemAudio,
                options.MicrophoneDeviceId, options.SystemAudioDeviceId);
        }

        public Task StartAsync(Rectangle screenRegion, string destinationPath)
        { return StartAsync(screenRegion, destinationPath, new ScreenRecordingOptions()); }

        public Task StartAsync(Rectangle screenRegion, string destinationPath, ScreenRecordingOptions options)
        { return StartAsync(screenRegion, destinationPath, options, null); }

        internal Task StartAsync(Rectangle screenRegion, string destinationPath, ScreenRecordingOptions options,
            Action<string> publishCompleted)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (String.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException("destinationPath");
            string path = Path.GetFullPath(destinationPath);
            if (!String.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an .mp4 recording filename.");
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("The recording folder is unavailable.");
            Rectangle region = Rectangle.Intersect(screenRegion, SystemInformation.VirtualScreen);
            if (region.Width < 2 || region.Height < 2) throw new ArgumentException("Choose a screen area at least 2 by 2 pixels.");
            if (options.FramesPerSecond < 5 || options.FramesPerSecond > 30) throw new ArgumentOutOfRangeException("options", "Choose a frame rate between 5 and 30.");
            ScreenRecordingOptions snapshot = new ScreenRecordingOptions();
            snapshot.RecordMicrophone = options.RecordMicrophone;
            snapshot.RecordSystemAudio = options.RecordSystemAudio;
            snapshot.IncludeCursor = options.IncludeCursor;
            snapshot.FramesPerSecond = options.FramesPerSecond;
            snapshot.MicrophoneDeviceId = options.MicrophoneDeviceId ?? "";
            snapshot.SystemAudioDeviceId = options.SystemAudioDeviceId ?? "";
            snapshot.BeforeFrameCapture = options.BeforeFrameCapture;
            snapshot.HideTaskbars = options.HideTaskbars;
            snapshot.KeepPrimaryTaskbarVisible = options.KeepPrimaryTaskbarVisible;
            bool replaceExisting = publishCompleted == null && File.Exists(path);
            TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>();
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ScreenRecorder");
                if (recording || starting || (finished != null && !finished.Task.IsCompleted)) throw new InvalidOperationException("A recording is already active.");
                starting = true;
                stop.Reset();
                stoppedAt = -1;
                lastError = null;
                clock = new Stopwatch();
                fps = snapshot.FramesPerSecond;
                outputSize = CalculateOutputSize(region.Size);
                sourceRegion = region;
                DestinationPath = path;
                RecoveryPath = null;
                AudioWarning = null;
                TaskbarWarning = null;
                finished = new TaskCompletionSource<bool>();
                try
                {
                    worker = new Thread(new ThreadStart(delegate { Record(region, path, snapshot, ready, publishCompleted, replaceExisting); }));
                    worker.IsBackground = true;
                    worker.Name = "Snip Studio screen recorder";
                    worker.SetApartmentState(ApartmentState.MTA);
                    startWorker(worker);
                }
                catch (Exception error)
                {
                    // No worker can complete the pending task when thread
                    // initialization fails. Leave this recorder retryable and
                    // let the caller handle the original startup exception.
                    starting = false;
                    worker = null;
                    finished = null;
                    lastError = error;
                    throw;
                }
            }
            return ready.Task;
        }

        public Task StopAsync()
        {
            lock (gate)
            {
                if (finished == null) return Task.FromResult(true);
                if (disposed) return finished.Task;
                if (finished.Task.IsCompleted) return finished.Task;
                if (stoppedAt < 0 && clock.IsRunning) stoppedAt = activeAudio == null ? clock.Elapsed.Ticks : activeAudio.Duration.Ticks;
                stop.Set();
                return finished.Task;
            }
        }

        public static Size CalculateOutputSize(Size source)
        {
            if (source.Width < 2 || source.Height < 2) throw new ArgumentOutOfRangeException("source");
            double scale = Math.Min(1.0, Math.Min(1920.0 / source.Width, 1080.0 / source.Height));
            return new Size(Math.Max(2, (int)(source.Width * scale) & ~1), Math.Max(2, (int)(source.Height * scale) & ~1));
        }

        private void Record(Rectangle region, string path, ScreenRecordingOptions options, TaskCompletionSource<bool> ready,
            Action<string> publishCompleted, bool replaceExisting)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".snip-recording-" + Guid.NewGuid().ToString("N") + ".mp4");
            IRecordingAudio audio = null;
            TaskbarVisibilityScope taskbars = null;
            bool published = false;
            bool finalized = false;
            Exception failure = null;
            try
            {
                bool withAudio = options.RecordMicrophone || options.RecordSystemAudio;
                using (MediaFoundationVideoWriter encoder = new MediaFoundationVideoWriter(temporary, outputSize.Width, outputSize.Height, fps, withAudio))
                using (Bitmap frame = new Bitmap(outputSize.Width, outputSize.Height, PixelFormat.Format32bppRgb))
                {
                    if (withAudio)
                    {
                        audio = createAudio(options);
                        audio.Start();
                    }
                    if (options.HideTaskbars)
                        taskbars = TaskbarVisibilityScope.Hide(options.KeepPrimaryTaskbarVisible);
                    lock (gate) { activeAudio = audio; clock.Start(); recording = true; starting = false; }
                    ready.TrySetResult(true);
                    Notify(true, withAudio ? audio.Status : "Recording screen without audio", null);
                    long nextVideo = 0, lastStatus = 0, audioFrames = 0;
                    long frameDuration = TimeSpan.TicksPerSecond / fps;
                    bool firstFrame = true;
                    while (firstFrame || !stop.WaitOne(0))
                    {
                        firstFrame = false;
                        long now = ElapsedTicks(audio);
                        if (now >= nextVideo)
                        {
                            if (options.BeforeFrameCapture != null || taskbars != null)
                            {
                                options.BeforeFrameCapture?.Invoke();
                                taskbars?.PrepareFrame();
                                // A taskbar re-hide can briefly wait for the
                                // desktop compositor. Timestamp the actual frame.
                                now = ElapsedTicks(audio);
                            }
                            captureFrame(frame, region, options.IncludeCursor);
                            encoder.WriteVideo(frame, now, frameDuration);
                            // No unbounded queue: slow capture/encoding drops a frame and keeps real timestamps.
                            nextVideo = Math.Max(nextVideo + frameDuration, ElapsedTicks(audio));
                        }
                        if (audio != null)
                        {
                            CheckAudioInterruption(audio);
                            long availableTicks = Math.Max(0, ElapsedTicks(audio) - TimeSpan.TicksPerMillisecond * 100);
                            WriteAudioUntil(encoder, audio, availableTicks * 48000 / TimeSpan.TicksPerSecond, ref audioFrames);
                        }
                        if (now - lastStatus >= TimeSpan.TicksPerSecond / 2)
                        {
                            lastStatus = now;
                            Notify(true, AudioWarning == null ? "Recording" : "Recording with interrupted audio", null);
                        }
                        long wait = nextVideo - ElapsedTicks(audio);
                        if (wait > 0) stop.WaitOne((int)Math.Min(20, Math.Max(1, wait / TimeSpan.TicksPerMillisecond)));
                    }
                    long endTicks;
                    lock (gate)
                    {
                        endTicks = stoppedAt >= 0 ? stoppedAt : ElapsedTicks(audio);
                        stoppedAt = Math.Max(endTicks, frameDuration);
                        clock.Stop();
                        recording = false;
                    }
                    Notify(false, "Finishing MP4…", null);
                    if (audio != null)
                    {
                        // Let the last device-period packets arrive; the exported end timestamp stays fixed.
                        Thread.Sleep(100);
                        audio.Stop();
                        CheckAudioInterruption(audio);
                        WriteAudioUntil(encoder, audio, Math.Max(1, endTicks * 48000 / TimeSpan.TicksPerSecond), ref audioFrames);
                    }
                    encoder.Complete();
                }
                finalized = true;
                try
                {
                    if (publishCompleted != null) publishCompleted(temporary);
                    else if (replaceExisting) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                catch (Exception error)
                {
                    RecoveryPath = File.Exists(temporary) ? temporary : null;
                    throw new RecordingSaveException(RecoveryPath, path, error);
                }
                published = true;
                Notify(false, AudioWarning == null ? "Recording saved" : "Recording saved with interrupted audio", null);
            }
            catch (Exception error)
            {
                lastError = error;
                failure = error;
                ready.TrySetException(error);
                Notify(false, "Recording could not finish", error);
            }
            finally
            {
                lock (gate) { activeAudio = null; recording = false; starting = false; clock.Stop(); }
                if (taskbars != null)
                {
                    try { TaskbarWarning = taskbars.RestoreAfterCapture(); }
                    catch (Exception error)
                    {
                        TaskbarWarning = "Windows could not confirm taskbar restoration: " + error.Message;
                    }
                    if (!String.IsNullOrEmpty(TaskbarWarning)) Trace.WriteLine(TaskbarWarning, "Recording taskbars");
                }
                if (audio != null) audio.Dispose();
                if (!published && !finalized && File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } }
                lock (gate) { if (disposed) stop.Dispose(); }
                if (failure == null) finished.TrySetResult(true);
                else finished.TrySetException(failure);
            }
        }

        private void CheckAudioInterruption(IRecordingAudio audio)
        {
            Exception failure = audio.Error;
            if (failure != null && AudioWarning == null)
                AudioWarning = "An audio source stopped. Some audio may be missing. " + failure.Message;
        }

        private long ElapsedTicks(IRecordingAudio audio) { return audio == null ? clock.Elapsed.Ticks : audio.Duration.Ticks; }

        private static void WriteAudioUntil(MediaFoundationVideoWriter encoder, IRecordingAudio audio, long desiredFrames, ref long framesWritten)
        {
            while (framesWritten < desiredFrames)
            {
                int frames = (int)Math.Min(4800, desiredFrames - framesWritten);
                byte[] bytes = audio.ReadFrames(frames);
                long time = framesWritten * TimeSpan.TicksPerSecond / 48000;
                long next = (framesWritten + frames) * TimeSpan.TicksPerSecond / 48000;
                encoder.WriteAudio(bytes, time, Math.Max(1, next - time));
                framesWritten += frames;
            }
        }

        private void Notify(bool active, string message, Exception error)
        {
            EventHandler<RecordingStatusEventArgs> handler = StatusChanged;
            if (handler == null) return;
            try { handler(this, new RecordingStatusEventArgs(active, Duration, message, error)); }
            catch (Exception) { /* A UI subscriber cannot interrupt encoding or cleanup. */ }
        }

        private static void CaptureFrame(Bitmap target, Rectangle region, bool includeCursor)
        {
            using (Graphics graphics = Graphics.FromImage(target))
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "The screen is unavailable.");
                try
                {
                    IntPtr dc = graphics.GetHdc();
                    try
                    {
                        SetStretchBltMode(dc, 4);
                        if (!StretchBlt(dc, 0, 0, target.Width, target.Height, screen, region.Left, region.Top, region.Width, region.Height, 0x40CC0020))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "The recording frame could not be read.");
                        if (includeCursor) DrawPointer(dc, region, target.Size);
                    }
                    finally { graphics.ReleaseHdc(dc); }
                }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }
        }

        private static void DrawPointer(IntPtr dc, Rectangle region, Size size)
        {
            CursorInfo cursor = new CursorInfo();
            cursor.Size = Marshal.SizeOf(typeof(CursorInfo));
            if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || !region.Contains(cursor.Position)) return;
            IconInfo icon;
            if (!GetIconInfo(cursor.Handle, out icon)) return;
            try
            {
                double sx = (double)size.Width / region.Width, sy = (double)size.Height / region.Height;
                int x = (int)((cursor.Position.X - region.Left - icon.HotspotX) * sx);
                int y = (int)((cursor.Position.Y - region.Top - icon.HotspotY) * sy);
                DrawIconEx(dc, x, y, cursor.Handle, Math.Max(1, (int)(SystemInformation.CursorSize.Width * sx)), Math.Max(1, (int)(SystemInformation.CursorSize.Height * sy)), 0, IntPtr.Zero, 3);
            }
            finally { if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); }
        }

        public void Dispose()
        {
            bool disposeSignal;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stop.Set();
                disposeSignal = finished == null || finished.Task.IsCompleted;
            }
            // Shutdown is signalled immediately; callers should await StopAsync before disposing.
            // The worker owns all native resources and disposes them even when the window closes.
            if (disposeSignal) stop.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { internal int Size, Flags; internal IntPtr Handle; internal Point Position; }
        [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon; internal uint HotspotX, HotspotY; internal IntPtr Mask, Color; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr handle, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    }
}
