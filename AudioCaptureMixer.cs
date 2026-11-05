using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SnipStudio
{
    public sealed class AudioDeviceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name ?? Id ?? "Audio device"; }
    }

    // Two bounded, timestamp-indexed WASAPI streams feed one PCM16 stereo timeline.
    // No microphone settings are changed and no audio is sent off this computer.
    public sealed class AudioCaptureMixer : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int BitsPerSample = 16;
        private const int BytesPerFrame = Channels * BitsPerSample / 8;
        private readonly CaptureSource microphone;
        private readonly CaptureSource systemAudio;
        private readonly object readLock = new object();
        private readonly ManualResetEvent startGate = new ManualResetEvent(false);
        private long readPosition;
        private long origin;
        private long stoppedAt;
        private bool started;
        private bool stopped;
        private bool disposed;
        private Exception failure;

        public Exception Error
        {
            get
            {
                if (failure != null) return failure;
                if (microphone != null && microphone.Error != null) return microphone.Error;
                return systemAudio == null ? null : systemAudio.Error;
            }
        }

        public string Status
        {
            get
            {
                Exception error = Error;
                if (error != null) return error.Message;
                if (!started) return "Audio is ready to start.";
                if (stopped) return "Audio capture stopped.";
                return microphone != null && systemAudio != null ? "Microphone and system audio recording." :
                    microphone != null ? "Microphone recording." : "System audio recording.";
            }
        }

        public long FramesRead { get { lock (readLock) return readPosition; } }
        public long TimeOrigin100ns { get { return origin; } }
        public TimeSpan Duration { get { return origin == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, (stoppedAt == 0 ? QpcTime() : stoppedAt) - origin)); } }
        public long ElapsedFrames { get { return (long)(Duration.Ticks * ((double)SampleRate / 10000000.0)); } }

        public AudioCaptureMixer(bool microphone, bool systemAudio) : this(microphone, systemAudio, "", "") { }

        public AudioCaptureMixer(bool microphone, bool systemAudio, string microphoneDeviceId, string systemAudioDeviceId)
        {
            if (!microphone && !systemAudio) throw new ArgumentException("Choose at least one audio source.");
            if (microphone) this.microphone = new CaptureSource(false, microphoneDeviceId ?? "");
            if (systemAudio) this.systemAudio = new CaptureSource(true, systemAudioDeviceId ?? "");
        }

        public static IList<AudioDeviceInfo> GetMicrophoneDevices() { return GetDevices(1); }
        public static IList<AudioDeviceInfo> GetSystemAudioDevices() { return GetDevices(0); }

        private static IList<AudioDeviceInfo> GetDevices(int flow)
        {
            List<AudioDeviceInfo> results = new List<AudioDeviceInfo>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Check(enumerator.EnumAudioEndpoints(flow, 1, out collection), "Audio devices could not be listed");
                uint count;
                Check(collection.GetCount(out count), "The audio device list is unavailable");
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    IPropertyStore properties = null;
                    try
                    {
                        Check(collection.Item(i, out device), "An audio device could not be read");
                        string id;
                        Check(device.GetId(out id), "The audio device identifier is unavailable");
                        string name = null;
                        IntPtr rawProperties;
                        if (device.OpenPropertyStore(0, out rawProperties) >= 0 && rawProperties != IntPtr.Zero)
                        {
                            try { properties = (IPropertyStore)Marshal.GetObjectForIUnknown(rawProperties); }
                            finally { Marshal.Release(rawProperties); }
                            PropertyKey key = new PropertyKey();
                            key.Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"); key.Id = 14;
                            PropVariant value;
                            int hr = properties.GetValue(ref key, out value);
                            try { if (hr >= 0 && value.Type == 31 && value.Pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(value.Pointer); }
                            finally { PropVariantClear(ref value); }
                        }
                        results.Add(new AudioDeviceInfo { Id = id, Name = String.IsNullOrWhiteSpace(name) ? (flow == 1 ? "Microphone" : "Playback device") + " — " + id : name });
                    }
                    finally { Release(properties); Release(device); }
                }
                return results.AsReadOnly();
            }
            finally { Release(collection); Release(enumerator); }
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException("AudioCaptureMixer");
            if (started) throw new InvalidOperationException("This audio capture has already started.");
            if (stopped) throw new InvalidOperationException("This audio capture has already stopped. Start a new recording instead.");
            started = true;
            try
            {
                if (microphone != null) microphone.Begin(delegate { return origin; }, startGate);
                if (systemAudio != null) systemAudio.Begin(delegate { return origin; }, startGate);
                if (microphone != null) microphone.WaitUntilReady();
                if (systemAudio != null) systemAudio.WaitUntilReady();
                ThrowIfFailed();
                origin = QpcTime();
                startGate.Set();
                if (microphone != null) microphone.WaitUntilRunning();
                if (systemAudio != null) systemAudio.WaitUntilRunning();
                ThrowIfFailed();
            }
            catch (Exception ex)
            {
                failure = ex;
                Stop();
                throw;
            }
        }

        // Call only for the elapsed recording timeline, leaving about 100 ms of
        // capture headroom. Reading a future timestamp deliberately yields silence.
        public byte[] ReadFrames(int frameCount)
        {
            if (disposed) throw new ObjectDisposedException("AudioCaptureMixer");
            if (!started) throw new InvalidOperationException("Audio capture has not started.");
            if (frameCount < 0 || frameCount > SampleRate) throw new ArgumentOutOfRangeException("frameCount");
            ThrowIfFailed();
            lock (readLock)
            {
                byte[] first = microphone == null ? null : microphone.Read(readPosition, frameCount);
                byte[] second = systemAudio == null ? null : systemAudio.Read(readPosition, frameCount);
                byte[] result = MixPcm16(first, second, checked(frameCount * BytesPerFrame));
                readPosition += frameCount;
                return result;
            }
        }

        internal static byte[] MixPcm16(byte[] first, byte[] second, int length)
        {
            if (length < 0 || (length & 1) != 0) throw new ArgumentOutOfRangeException("length");
            if ((first != null && first.Length != length) || (second != null && second.Length != length))
                throw new ArgumentException("Audio buffers must contain the requested number of samples.");
            byte[] result = new byte[length];
            for (int i = 0; i < length; i += 2)
            {
                int a = first == null ? 0 : (short)(first[i] | (first[i + 1] << 8));
                int b = second == null ? 0 : (short)(second[i] | (second[i + 1] << 8));
                int mixed = first != null && second != null ? (a + b) / 2 : a + b;
                mixed = Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, mixed));
                result[i] = (byte)mixed;
                result[i + 1] = (byte)(mixed >> 8);
            }
            return result;
        }

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            stoppedAt = QpcTime();
            if (microphone != null) microphone.RequestStop();
            if (systemAudio != null) systemAudio.RequestStop();
            if (microphone != null) microphone.Join();
            if (systemAudio != null) systemAudio.Join();
        }

        private void ThrowIfFailed()
        {
            Exception error = Error;
            if (error != null) throw new InvalidOperationException("The chosen recording audio could not be captured. " + error.Message, error);
        }

        internal static long QpcTime()
        {
            return (long)((double)Stopwatch.GetTimestamp() * 10000000.0 / Stopwatch.Frequency);
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            if (microphone != null) microphone.Dispose();
            if (systemAudio != null) systemAudio.Dispose();
            startGate.Dispose();
        }

        private sealed class CaptureSource : IDisposable
        {
            private const int Capacity = SampleRate * 3;
            private const uint LoopbackFlag = 0x00020000;
            private const uint AutoConvertPcm = 0x80000000;
            private const uint HighQualityConversion = 0x08000000;
            private readonly bool loopback;
            private readonly string deviceId;
            private readonly object sync = new object();
            private readonly short[] samples = new short[Capacity * Channels];
            private readonly long[] stamps = new long[Capacity];
            private readonly ManualResetEvent ready = new ManualResetEvent(false);
            private readonly ManualResetEvent runningReady = new ManualResetEvent(false);
            private readonly ManualResetEvent stop = new ManualResetEvent(false);
            private Thread thread;
            private long origin;
            private Func<long> getOrigin;
            private ManualResetEvent startGate;
            private long nextRead;
            private Exception error;

            internal Exception Error { get { lock (sync) return error; } }

            internal CaptureSource(bool loopback, string deviceId)
            {
                this.loopback = loopback;
                this.deviceId = deviceId;
                for (int i = 0; i < stamps.Length; i++) stamps[i] = Int64.MinValue;
            }

            internal void Begin(Func<long> originProvider, ManualResetEvent gate)
            {
                getOrigin = originProvider;
                startGate = gate;
                thread = new Thread(Capture);
                thread.IsBackground = true;
                thread.Name = loopback ? "Snip Studio system audio" : "Snip Studio microphone";
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }

            internal void WaitUntilReady()
            {
                if (!ready.WaitOne(7000)) throw new InvalidOperationException(SourceName + " did not become ready in time.");
                Exception failure = Error;
                if (failure != null) throw failure;
            }

            internal void WaitUntilRunning()
            {
                if (!runningReady.WaitOne(7000)) throw new InvalidOperationException(SourceName + " did not start in time.");
                Exception failure = Error;
                if (failure != null) throw failure;
            }

            internal byte[] Read(long start, int frames)
            {
                byte[] result = new byte[checked(frames * BytesPerFrame)];
                lock (sync)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        long frame = start + f;
                        int index = (int)(frame % Capacity);
                        if (stamps[index] != frame) continue;
                        int sample = index * Channels;
                        int target = f * BytesPerFrame;
                        short left = samples[sample], right = samples[sample + 1];
                        result[target] = (byte)left;
                        result[target + 1] = (byte)(left >> 8);
                        result[target + 2] = (byte)right;
                        result[target + 3] = (byte)(right >> 8);
                    }
                    nextRead = start + frames;
                }
                return result;
            }

            private string SourceName { get { return loopback ? "System audio" : "Microphone"; } }

            private void Capture()
            {
                IMMDeviceEnumerator enumerator = null;
                IMMDevice device = null;
                IAudioClient client = null;
                IAudioCaptureClient capture = null;
                bool running = false;
                try
                {
                    enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    if (String.IsNullOrEmpty(deviceId))
                        Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out device), "The default " + SourceName.ToLowerInvariant() + " device is unavailable");
                    else
                    {
                        Check(enumerator.GetDevice(deviceId, out device), "The selected " + SourceName.ToLowerInvariant() + " device is unavailable. Select another device before recording");
                        uint state;
                        Check(device.GetState(out state), "The selected audio device state is unavailable");
                        if ((state & 1) == 0) throw new InvalidOperationException("The selected " + SourceName.ToLowerInvariant() + " device is disconnected or disabled. Select another device before recording.");
                    }
                    Guid clientId = typeof(IAudioClient).GUID;
                    object activated;
                    Check(device.Activate(ref clientId, 23, IntPtr.Zero, out activated), SourceName + " could not be opened");
                    client = (IAudioClient)activated;
                    WaveFormatEx format = new WaveFormatEx();
                    format.FormatTag = 1;
                    format.Channels = Channels;
                    format.SamplesPerSecond = SampleRate;
                    format.BitsPerSample = BitsPerSample;
                    format.BlockAlign = BytesPerFrame;
                    format.AverageBytesPerSecond = SampleRate * BytesPerFrame;
                    uint flags = AutoConvertPcm | HighQualityConversion | (loopback ? LoopbackFlag : 0);
                    Check(client.Initialize(0, flags, 1000000, 0, ref format, IntPtr.Zero), SourceName + " does not support the requested recording format or access is blocked");
                    Guid captureId = typeof(IAudioCaptureClient).GUID;
                    object service;
                    Check(client.GetService(ref captureId, out service), SourceName + " capture service is unavailable");
                    capture = (IAudioCaptureClient)service;
                    ready.Set();
                    if (WaitHandle.WaitAny(new WaitHandle[] { startGate, stop }) != 0) return;
                    origin = getOrigin();
                    Check(client.Start(), SourceName + " could not start");
                    running = true;
                    runningReady.Set();
                    long fallbackBase = Int64.MinValue;
                    ulong fallbackDevice = 0;
                    while (true)
                    {
                        bool stopping = stop.WaitOne(5);
                        uint packet;
                        Check(capture.GetNextPacketSize(out packet), SourceName + " capture stopped responding");
                        while (packet > 0)
                        {
                            IntPtr data;
                            uint frameCount, bufferFlags;
                            ulong devicePosition, qpcPosition;
                            Check(capture.GetBuffer(out data, out frameCount, out bufferFlags, out devicePosition, out qpcPosition), SourceName + " could not read an audio packet");
                            try
                            {
                                if (frameCount > 0)
                                {
                                    long firstFrame;
                                    if ((bufferFlags & 4) == 0 && qpcPosition != 0)
                                        firstFrame = (long)Math.Round(((long)qpcPosition - origin) * ((double)SampleRate / 10000000.0));
                                    else
                                    {
                                        if (fallbackBase == Int64.MinValue)
                                        {
                                            fallbackBase = Math.Max(0, (long)((QpcTime() - origin) * ((double)SampleRate / 10000000.0)) - frameCount);
                                            fallbackDevice = devicePosition;
                                        }
                                        firstFrame = fallbackBase + (long)(devicePosition - fallbackDevice);
                                    }
                                    if (frameCount > SampleRate) throw new InvalidOperationException(SourceName + " returned an unexpectedly large audio packet.");
                                    short[] block = new short[checked((int)frameCount * Channels)];
                                    bool silent = (bufferFlags & 2) != 0;
                                    if (!silent)
                                    {
                                        if (data == IntPtr.Zero) throw new InvalidOperationException(SourceName + " returned no data for a non-silent packet.");
                                        Marshal.Copy(data, block, 0, block.Length);
                                    }
                                    Store(firstFrame, frameCount, block);
                                }
                            }
                            finally { Check(capture.ReleaseBuffer(frameCount), SourceName + " could not release an audio packet"); }
                            Check(capture.GetNextPacketSize(out packet), SourceName + " capture stopped responding");
                        }
                        if (stopping) break;
                    }
                }
                catch (Exception ex) { lock (sync) error = ex; }
                finally
                {
                    ready.Set();
                    runningReady.Set();
                    if (running && client != null) { try { client.Stop(); } catch (Exception) { } }
                    Release(capture); Release(client); Release(device); Release(enumerator);
                }
            }

            private void Store(long firstFrame, uint frames, short[] block)
            {
                lock (sync)
                {
                    if (firstFrame > nextRead + Capacity || firstFrame + frames > nextRead + Capacity)
                        throw new InvalidOperationException(SourceName + " recording fell more than three seconds behind. Stop and start a new recording.");
                    for (int f = 0; f < frames; f++)
                    {
                        long frame = firstFrame + f;
                        if (frame < 0 || frame < nextRead) continue;
                        int index = (int)(frame % Capacity);
                        samples[index * Channels] = block[f * Channels];
                        samples[index * Channels + 1] = block[f * Channels + 1];
                        stamps[index] = frame;
                    }
                }
            }

            internal void RequestStop() { stop.Set(); }
            internal void Join()
            {
                if (thread != null && thread.IsAlive && !thread.Join(2500))
                    lock (sync) error = new InvalidOperationException(SourceName + " did not stop in time.");
            }
            public void Dispose()
            {
                RequestStop(); Join();
                if (thread == null || !thread.IsAlive) { ready.Dispose(); runningReady.Dispose(); stop.Dispose(); }
            }
        }

        private static void Check(int hr, string message)
        {
            if (hr < 0) throw new InvalidOperationException(message + " (" + hr.ToString("X8") + ").", Marshal.GetExceptionForHR(hr));
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) { try { Marshal.FinalReleaseComObject(value); } catch (Exception) { } }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            internal ushort FormatTag, Channels;
            internal uint SamplesPerSecond, AverageBytesPerSecond;
            internal ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal IntPtr Pointer;
        }
        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
