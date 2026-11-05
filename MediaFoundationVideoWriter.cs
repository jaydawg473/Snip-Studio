using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipStudio
{
    // A small native Media Foundation adapter. All COM calls stay on the creating thread.
    public sealed class MediaFoundationVideoWriter : IDisposable
    {
        private IntPtr writer;
        private int videoStream, audioStream = -1;
        private readonly int width, height;
        private bool started, completed, comInitialized;
        private byte[] pixels;
        public int VideoSampleCount { get; private set; }

        public MediaFoundationVideoWriter(string path, int width, int height, int fps, bool includeAudio)
            : this(path, width, height, fps, includeAudio, Math.Max(1500000, Math.Min(12000000, width * height * 4)), 160000)
        {
        }

        public MediaFoundationVideoWriter(string path, int width, int height, int fps, bool includeAudio, int videoBitrate, int audioBitrate)
        {
            if (String.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0) throw new ArgumentException("Video dimensions must be positive even numbers.");
            if (fps < 1 || fps > 60) throw new ArgumentOutOfRangeException("fps");
            if (videoBitrate < 100000 || videoBitrate > 30000000) throw new ArgumentOutOfRangeException("videoBitrate");
            if (includeAudio && audioBitrate != 96000 && audioBitrate != 128000 && audioBitrate != 160000 && audioBitrate != 192000)
                throw new ArgumentOutOfRangeException("audioBitrate", "Choose a supported AAC audio rate.");
            this.width = width; this.height = height;
            try
            {
                int com = CoInitializeEx(IntPtr.Zero, 0);
                if (com >= 0) comInitialized = true;
                else if (com != unchecked((int)0x80010106)) Check(com, "Initialize recording");
                Check(MFStartup(0x00020070, 0), "Start Windows media services");
                started = true;
                Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer), "Create MP4 recorder");
                IntPtr output = IntPtr.Zero, input = IntPtr.Zero;
                try
                {
                    Check(MFCreateMediaType(out output), "Create H.264 output format");
                    SetGuid(output, MajorType, VideoType);
                    SetGuid(output, Subtype, H264);
                    SetUInt32(output, AvgBitrate, videoBitrate);
                    SetUInt32(output, InterlaceMode, 2);
                    SetUInt64(output, FrameSize, Pack(width, height));
                    SetUInt64(output, FrameRate, Pack(fps, 1));
                    SetUInt64(output, PixelAspectRatio, Pack(1, 1));
                    Check(Call<AddStreamCall>(writer, 3)(writer, output, out videoStream), "Select an H.264 encoder");
                    Check(MFCreateMediaType(out input), "Create video input format");
                    SetGuid(input, MajorType, VideoType);
                    SetGuid(input, Subtype, Rgb32);
                    SetUInt32(input, InterlaceMode, 2);
                    SetUInt32(input, DefaultStride, width * 4);
                    SetUInt64(input, FrameSize, Pack(width, height));
                    SetUInt64(input, FrameRate, Pack(fps, 1));
                    SetUInt64(input, PixelAspectRatio, Pack(1, 1));
                    Check(Call<SetInputCall>(writer, 4)(writer, videoStream, input, IntPtr.Zero), "Connect screen pixels to the H.264 encoder");
                }
                finally { Release(ref output); Release(ref input); }
                if (includeAudio) AddAudioStream(audioBitrate);
                Check(Call<NoArgumentCall>(writer, 5)(writer), "Begin MP4 recording");
                pixels = new byte[checked(width * height * 4)];
            }
            catch { Dispose(); throw; }
        }

        private void AddAudioStream(int audioBitrate)
        {
            IntPtr output = IntPtr.Zero, input = IntPtr.Zero;
            try
            {
                Check(MFCreateMediaType(out output), "Create AAC format");
                SetGuid(output, MajorType, AudioType);
                SetGuid(output, Subtype, Aac);
                SetUInt32(output, AudioChannels, 2);
                SetUInt32(output, AudioSamplesPerSecond, 48000);
                SetUInt32(output, AudioBitsPerSample, 16);
                SetUInt32(output, AudioAvgBytesPerSecond, audioBitrate / 8);
                Check(Call<AddStreamCall>(writer, 3)(writer, output, out audioStream), "Select an AAC encoder");
                Check(MFCreateMediaType(out input), "Create PCM audio input");
                SetGuid(input, MajorType, AudioType);
                SetGuid(input, Subtype, Pcm);
                SetUInt32(input, AudioChannels, 2);
                SetUInt32(input, AudioSamplesPerSecond, 48000);
                SetUInt32(input, AudioBitsPerSample, 16);
                SetUInt32(input, AudioBlockAlignment, 4);
                SetUInt32(input, AudioAvgBytesPerSecond, 192000);
                Check(Call<SetInputCall>(writer, 4)(writer, audioStream, input, IntPtr.Zero), "Connect captured audio to the AAC encoder");
            }
            finally { Release(ref output); Release(ref input); }
        }

        public void WriteVideo(Bitmap bitmap, long timestamp, long duration)
        {
            if (writer == IntPtr.Zero || completed) throw new InvalidOperationException("The recorder is not accepting frames.");
            if (bitmap == null || bitmap.Width != width || bitmap.Height != height) throw new ArgumentException("The frame size does not match the recording.");
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width * 4, width * 4);
            }
            finally { bitmap.UnlockBits(data); }
            WriteSample(videoStream, pixels, timestamp, duration);
            VideoSampleCount++;
        }

        public void WriteAudio(byte[] pcm, long timestamp, long duration)
        {
            if (audioStream < 0) throw new InvalidOperationException("Audio was not enabled for this recording.");
            if (pcm == null || pcm.Length == 0 || pcm.Length % 4 != 0) throw new ArgumentException("Audio must be stereo PCM16 frames.");
            WriteSample(audioStream, pcm, timestamp, duration);
        }

        private void WriteSample(int stream, byte[] bytes, long timestamp, long duration)
        {
            if (writer == IntPtr.Zero || completed) throw new InvalidOperationException("The recorder is not accepting samples.");
            if (timestamp < 0 || duration <= 0) throw new ArgumentOutOfRangeException("timestamp", "Media samples need a nonnegative time and positive duration.");
            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                Check(MFCreateMemoryBuffer(bytes.Length, out buffer), "Allocate a recording buffer");
                IntPtr destination; int maximum, current;
                Check(Call<LockBufferCall>(buffer, 3)(buffer, out destination, out maximum, out current), "Lock a recording buffer");
                try { Marshal.Copy(bytes, 0, destination, bytes.Length); }
                finally { Check(Call<NoArgumentCall>(buffer, 4)(buffer), "Unlock a recording buffer"); }
                Check(Call<SetLengthCall>(buffer, 6)(buffer, bytes.Length), "Set media buffer length");
                Check(MFCreateSample(out sample), "Create a media sample");
                Check(Call<AddBufferCall>(sample, 42)(sample, buffer), "Attach media buffer");
                Check(Call<SetTimeCall>(sample, 36)(sample, timestamp), "Timestamp media sample");
                Check(Call<SetTimeCall>(sample, 38)(sample, duration), "Set media sample duration");
                Check(Call<WriteSampleCall>(writer, 6)(writer, stream, sample), "Encode recording sample");
            }
            finally { Release(ref sample); Release(ref buffer); }
        }

        public void Complete()
        {
            if (completed) return;
            if (writer == IntPtr.Zero) throw new ObjectDisposedException("MediaFoundationVideoWriter");
            Check(Call<NoArgumentCall>(writer, 11)(writer), "Finish the MP4 recording");
            completed = true;
        }

        public void Dispose()
        {
            Release(ref writer);
            if (started) { started = false; MFShutdown(); }
            if (comInitialized) { comInitialized = false; CoUninitialize(); }
            pixels = null;
        }

        private static ulong Pack(int first, int second) { return ((ulong)(uint)first << 32) | (uint)second; }
        private static void SetUInt32(IntPtr target, Guid key, int value) { Check(Call<SetUInt32Call>(target, 21)(target, ref key, value), "Set media format"); }
        private static void SetUInt64(IntPtr target, Guid key, ulong value) { Check(Call<SetUInt64Call>(target, 22)(target, ref key, value), "Set media size or rate"); }
        private static void SetGuid(IntPtr target, Guid key, Guid value) { Check(Call<SetGuidCall>(target, 24)(target, ref key, ref value), "Set media type"); }
        internal static T Call<T>(IntPtr instance, int slot) where T : class
        {
            IntPtr address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
        internal static void Check(int result, string action)
        {
            if (result < 0) throw new InvalidOperationException(action + " failed (0x" + result.ToString("X8") + "). " + Marshal.GetExceptionForHR(result).Message);
        }
        internal static void Release(ref IntPtr value) { if (value != IntPtr.Zero) { Marshal.Release(value); value = IntPtr.Zero; } }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NoArgumentCall(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt32Call(IntPtr self, ref Guid key, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt64Call(IntPtr self, ref Guid key, ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidCall(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddStreamCall(IntPtr self, IntPtr type, out int stream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInputCall(IntPtr self, int stream, IntPtr type, IntPtr parameters);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteSampleCall(IntPtr self, int stream, IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBufferCall(IntPtr self, out IntPtr data, out int maximum, out int current);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetLengthCall(IntPtr self, int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddBufferCall(IntPtr self, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetTimeCall(IntPtr self, long value);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MFCreateSinkWriterFromURL(string path, IntPtr stream, IntPtr attributes, out IntPtr writer);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(int length, out IntPtr buffer);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out IntPtr sample);

        private static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid VideoType = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioType = new Guid("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid Rgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid Aac = new Guid("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid Pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid AvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid PixelAspectRatio = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid AudioSamplesPerSecond = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid AudioBitsPerSample = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid AudioAvgBytesPerSecond = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid AudioBlockAlignment = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
    }
}
