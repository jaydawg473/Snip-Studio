using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio
{
    public sealed class VideoMetadata
    {
        public TimeSpan Duration { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int FramesPerSecond { get; internal set; }
        public bool HasAudio { get; internal set; }
    }

    public enum VideoExportPreset { Balanced720p, Small480p, TargetAttachmentSize }

    public sealed class VideoExportPlan
    {
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int FramesPerSecond { get; internal set; }
        public int VideoBitrate { get; internal set; }
        public int AudioBitrate { get; internal set; }
        public long EstimatedBytes { get; internal set; }
        public long TargetBytes { get; internal set; }
        public long SourceBytes { get; internal set; }
        public TimeSpan Duration { get; internal set; }
        public bool HasAudio { get; internal set; }
        internal int SourceWidth, SourceHeight;
    }

    public sealed class VideoExportResult
    {
        public string Path { get; internal set; }
        public long Bytes { get; internal set; }
        public long SourceBytes { get; internal set; }
        public long TargetBytes { get; internal set; }
        public bool TargetMet { get { return TargetBytes <= 0 || Bytes <= TargetBytes; } }
    }

    // Local decode/re-encode keeps arbitrary trim points accurate rather than
    // silently moving the selection to a preceding H.264 keyframe.
    public static class VideoTrimService
    {
        public static Task<VideoMetadata> InspectAsync(string path)
        {
            string fullPath = ValidateSource(path);
            return Task.Run(delegate
            {
                using (NativeReader reader = new NativeReader(fullPath, false)) return reader.Metadata;
            });
        }

        public static Task TrimAsync(string sourcePath, string destinationPath, TimeSpan start, TimeSpan end)
        {
            string source = ValidateSource(sourcePath);
            if (String.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException("destinationPath");
            string destination = Path.GetFullPath(destinationPath);
            if (String.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Save the trimmed recording as a separate file. The original recording is preserved.", "destinationPath");
            if (!String.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an .mp4 filename for the trimmed copy.", "destinationPath");
            if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("The trimmed recording folder is unavailable.");
            if (start < TimeSpan.Zero || end <= start) throw new ArgumentException("Choose an end time after the start time.");
            return Task.Run(delegate { TrimCore(source, destination, start.Ticks, end.Ticks, null, CancellationToken.None, null); });
        }

        public static VideoExportPlan CreateExportPlan(VideoMetadata metadata, long sourceBytes, VideoExportPreset preset, int targetMegabytes)
        {
            if (metadata == null) throw new ArgumentNullException("metadata");
            if (metadata.Width < 2 || metadata.Height < 2 || metadata.Duration <= TimeSpan.Zero || sourceBytes < 1)
                throw new ArgumentException("This recording has no usable video duration or size.");
            if (!Enum.IsDefined(typeof(VideoExportPreset), preset)) throw new ArgumentOutOfRangeException("preset");
            if (preset == VideoExportPreset.TargetAttachmentSize && (targetMegabytes < 1 || targetMegabytes > 1000))
                throw new ArgumentOutOfRangeException("targetMegabytes", "Choose a target between 1 and 1000 MB.");
            // MB means decimal file bytes. Reserve space for MP4 metadata and a
            // margin for variable bitrate; this remains an estimate, not a cap.
            long targetBytes = preset == VideoExportPreset.TargetAttachmentSize ? targetMegabytes * 1000000L : 0;
            double desiredBytes = Math.Min(sourceBytes * .82, targetBytes > 0 ? targetBytes * .92 : Double.MaxValue);
            double seconds = metadata.Duration.TotalSeconds;
            int audioBitrate = metadata.HasAudio ? 96000 : 0;
            double budget = (desiredBytes - 65536) * 8.0 / seconds - audioBitrate;
            if (Double.IsNaN(budget) || Double.IsInfinity(budget) || budget < 200000)
                throw new InvalidOperationException("This recording is too long or already too compact for that smaller copy. Trim its duration, or choose a larger target size.");
            int videoBitrate = (int)Math.Min(preset == VideoExportPreset.Small480p ? 650000 : 1800000, budget);
            int longSide = preset == VideoExportPreset.Small480p || videoBitrate < 650000 ? 854 : 1280;
            int shortSide = longSide == 854 ? 480 : 720;
            int maximumWidth = metadata.Width >= metadata.Height ? longSide : shortSide;
            int maximumHeight = metadata.Width >= metadata.Height ? shortSide : longSide;
            double scale = Math.Min(1.0, Math.Min((double)maximumWidth / metadata.Width, (double)maximumHeight / metadata.Height));
            int width = Math.Max(2, ((int)Math.Floor(metadata.Width * scale)) & ~1);
            int height = Math.Max(2, ((int)Math.Floor(metadata.Height * scale)) & ~1);
            return new VideoExportPlan {
                Width = width, Height = height, FramesPerSecond = Math.Max(1, Math.Min(15, metadata.FramesPerSecond)),
                VideoBitrate = videoBitrate, AudioBitrate = audioBitrate, TargetBytes = targetBytes, SourceBytes = sourceBytes,
                Duration = metadata.Duration, HasAudio = metadata.HasAudio, SourceWidth = metadata.Width, SourceHeight = metadata.Height,
                EstimatedBytes = (long)Math.Ceiling((videoBitrate + (double)audioBitrate) * seconds / 8.0 + 65536)
            };
        }

        public static Task<VideoExportResult> ExportAsync(string sourcePath, string destinationPath, VideoExportPlan plan,
            CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            string source = ValidateSource(sourcePath);
            if (String.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException("destinationPath");
            string destination = Path.GetFullPath(destinationPath);
            if (String.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Save the smaller recording as a separate file. The original stays unchanged.", "destinationPath");
            if (!String.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an .mp4 filename for the smaller copy.", "destinationPath");
            if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("The export folder is unavailable.");
            if (plan.Width < 2 || plan.Height < 2 || plan.Width % 2 != 0 || plan.Height % 2 != 0 ||
                plan.FramesPerSecond < 1 || plan.FramesPerSecond > 15 || plan.VideoBitrate < 200000 || plan.VideoBitrate > 1800000 ||
                plan.Width > plan.SourceWidth || plan.Height > plan.SourceHeight || plan.Width > 1920 || plan.Height > 1920 ||
                plan.Duration <= TimeSpan.Zero || plan.SourceBytes < 1 || plan.TargetBytes < 0 ||
                (plan.HasAudio ? plan.AudioBitrate != 96000 : plan.AudioBitrate != 0))
                throw new ArgumentException("The video export options are invalid. Choose the options again.", "plan");
            return Task.Run(delegate {
                cancellationToken.ThrowIfCancellationRequested();
                long sourceBytes = new FileInfo(source).Length;
                if (sourceBytes != plan.SourceBytes) throw new InvalidOperationException("The recording changed after the export options were chosen. Open the options again.");
                TrimCore(source, destination, 0, plan.Duration.Ticks, plan, cancellationToken, progress);
                return new VideoExportResult { Path = destination, Bytes = new FileInfo(destination).Length,
                    SourceBytes = sourceBytes, TargetBytes = plan.TargetBytes };
            });
        }

        private static string ValidateSource(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException("path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("The recording is unavailable.", fullPath);
            return fullPath;
        }

        private static void TrimCore(string source, string destination, long start, long end, VideoExportPlan export,
            CancellationToken cancellationToken, IProgress<double> progress)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".snip-trim-" + Guid.NewGuid().ToString("N") + ".mp4");
            bool published = false;
            bool finalized = false;
            try
            {
                using (NativeReader reader = new NativeReader(source, true))
                {
                    VideoMetadata metadata = reader.Metadata;
                    if (start >= metadata.Duration.Ticks || end > metadata.Duration.Ticks + TimeSpan.TicksPerMillisecond)
                        throw new ArgumentException("The trim range must be within the recording duration.");
                    end = Math.Min(end, metadata.Duration.Ticks);
                    if (export != null && (metadata.Width != export.SourceWidth || metadata.Height != export.SourceHeight ||
                        metadata.HasAudio != export.HasAudio || Math.Abs(metadata.Duration.Ticks - export.Duration.Ticks) > TimeSpan.TicksPerMillisecond))
                        throw new InvalidOperationException("The recording changed after the export options were chosen. Open the options again.");
                    if (export == null && (metadata.Width % 2 != 0 || metadata.Height % 2 != 0))
                        throw new InvalidOperationException("This recording has odd frame dimensions and cannot be trimmed by this recorder.");
                    reader.Seek(start);
                    int outputWidth = export == null ? metadata.Width : export.Width;
                    int outputHeight = export == null ? metadata.Height : export.Height;
                    int outputFps = export == null ? metadata.FramesPerSecond : export.FramesPerSecond;
                    using (MediaFoundationVideoWriter writer = export == null
                        ? new MediaFoundationVideoWriter(temporary, outputWidth, outputHeight, outputFps, metadata.HasAudio)
                        : new MediaFoundationVideoWriter(temporary, outputWidth, outputHeight, outputFps, metadata.HasAudio, export.VideoBitrate, export.AudioBitrate))
                    using (Bitmap frame = new Bitmap(metadata.Width, metadata.Height, PixelFormat.Format32bppRgb))
                    using (Bitmap resized = export == null ? null : new Bitmap(outputWidth, outputHeight, PixelFormat.Format32bppRgb))
                    using (Graphics scaler = resized == null ? null : Graphics.FromImage(resized))
                    {
                        if (scaler != null) { scaler.InterpolationMode = InterpolationMode.HighQualityBicubic; scaler.PixelOffsetMode = PixelOffsetMode.HighQuality; }
                        bool videoDone = false, audioDone = !metadata.HasAudio;
                        int emptySamples = 0;
                        long nextVideoTime = 0, outputFrameDuration = TimeSpan.TicksPerSecond / outputFps;
                        double lastProgress = -1;
                        Dictionary<int, bool> streamKinds = new Dictionary<int, bool>();
                        while (!videoDone || !audioDone)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            IntPtr sample = IntPtr.Zero;
                            int actual, flags;
                            long timestamp;
                            try
                            {
                                reader.Read(out actual, out flags, out timestamp, out sample);
                                if ((flags & 1) != 0) throw new InvalidOperationException("Windows reported an error while reading the recording.");
                                bool video;
                                if (!streamKinds.TryGetValue(actual, out video))
                                {
                                    video = reader.IsVideoStream(actual);
                                    streamKinds[actual] = video;
                                }
                                // Source Reader can announce its initially negotiated decoder type
                                // after a seek. Accept that only when our exact format is retained.
                                if ((flags & 0x20) != 0) reader.ValidateFormat(actual, video);
                                if (sample == IntPtr.Zero)
                                {
                                    if (++emptySamples > 10000) throw new InvalidOperationException("The recording did not return usable media samples.");
                                }
                                else
                                {
                                    emptySamples = 0;
                                    long duration = reader.SampleDuration(sample, video ? TimeSpan.TicksPerSecond / metadata.FramesPerSecond : 0);
                                    if (timestamp >= end)
                                    {
                                        if (video) videoDone = true; else audioDone = true;
                                        reader.Deselect(actual);
                                    }
                                    else if (video && !videoDone && timestamp + duration > start)
                                    {
                                        reader.CopyVideo(sample, frame);
                                        long effectiveStart = Math.Max(timestamp, start);
                                        long effectiveEnd = Math.Min(timestamp + duration, end);
                                        if (effectiveEnd > effectiveStart)
                                        {
                                            if (export == null) writer.WriteVideo(frame, effectiveStart - start, effectiveEnd - effectiveStart);
                                            else
                                            {
                                                scaler.DrawImage(frame, new Rectangle(0, 0, outputWidth, outputHeight), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
                                                if (nextVideoTime < effectiveStart - start) nextVideoTime = effectiveStart - start;
                                                while (nextVideoTime < effectiveEnd - start)
                                                {
                                                    long outputDuration = Math.Min(outputFrameDuration, end - start - nextVideoTime);
                                                    if (outputDuration <= 0) break;
                                                    writer.WriteVideo(resized, nextVideoTime, outputDuration);
                                                    nextVideoTime += outputFrameDuration;
                                                }
                                            }
                                        }
                                    }
                                    else if (!video && !audioDone)
                                    {
                                        byte[] pcm = reader.CopySampleBytes(sample);
                                        int sourceFrames = pcm.Length / 4;
                                        long first = Math.Max(0, (long)Math.Ceiling((start - timestamp) * (48000.0 / TimeSpan.TicksPerSecond)));
                                        long last = Math.Min(sourceFrames, (long)Math.Ceiling((end - timestamp) * (48000.0 / TimeSpan.TicksPerSecond)));
                                        if (last > first)
                                        {
                                            byte[] clipped = new byte[checked((int)(last - first) * 4)];
                                            Buffer.BlockCopy(pcm, checked((int)first * 4), clipped, 0, clipped.Length);
                                            long time = Math.Max(0, timestamp + first * TimeSpan.TicksPerSecond / 48000 - start);
                                            long countDuration = Math.Max(1, (last - first) * TimeSpan.TicksPerSecond / 48000);
                                            writer.WriteAudio(clipped, time, countDuration);
                                        }
                                    }
                                }
                                if ((flags & 2) != 0)
                                {
                                    if (video) videoDone = true; else audioDone = true;
                                    reader.Deselect(actual);
                                }
                                if (progress != null)
                                {
                                    double value = Math.Max(0, Math.Min(.98, (double)(timestamp - start) / Math.Max(1, end - start)));
                                    if (value - lastProgress >= .01) { lastProgress = value; progress.Report(value); }
                                }
                            }
                            finally { Release(ref sample); }
                        }
                        if (writer.VideoSampleCount == 0) throw new InvalidOperationException("The selected range contains no video frames.");
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.Complete();
                        finalized = true;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (export != null && new FileInfo(temporary).Length >= export.SourceBytes)
                {
                    finalized = false;
                    throw new InvalidOperationException("The encoded copy was not smaller, so it was not saved. Your original and destination are unchanged. Try trimming the video or choosing the Small preset.");
                }
                try
                {
                    if (File.Exists(destination)) File.Replace(temporary, destination, null);
                    else File.Move(temporary, destination);
                }
                catch (Exception error) { throw new RecordingSaveException(temporary, error); }
                published = true;
                if (progress != null) progress.Report(1);
            }
            finally
            {
                if (!published && (!finalized || cancellationToken.IsCancellationRequested) && File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } }
            }
        }

        private sealed class NativeReader : IDisposable
        {
            private const int FirstVideo = unchecked((int)0xFFFFFFFC);
            private const int FirstAudio = unchecked((int)0xFFFFFFFD);
            private const int AnyStream = unchecked((int)0xFFFFFFFE);
            private IntPtr reader;
            private bool comInitialized, mediaStarted;
            private int stride;
            private int decodedHeight;
            public VideoMetadata Metadata { get; private set; }

            internal NativeReader(string path, bool decode)
            {
                IntPtr attributes = IntPtr.Zero, video = IntPtr.Zero, audio = IntPtr.Zero;
                try
                {
                    int com = CoInitializeEx(IntPtr.Zero, 0);
                    if (com >= 0) comInitialized = true;
                    else if (com != unchecked((int)0x80010106)) Check(com, "Initialize video inspection");
                    Check(MFStartup(0x00020070, 0), "Start Windows video services");
                    mediaStarted = true;
                    Check(MFCreateAttributes(out attributes, 1), "Prepare video decoding");
                    SetUInt(attributes, VideoProcessing, 1);
                    Check(MFCreateSourceReaderFromURL(path, attributes, out reader), "Open the recording");
                    Check(Call<GetTypeCall>(reader, 6)(reader, FirstVideo, out video), "Read video format");
                    ulong size;
                    Check(Call<GetUInt64Call>(video, 8)(video, ref FrameSize, out size), "Read video size");
                    ulong rate;
                    int rateResult = Call<GetUInt64Call>(video, 8)(video, ref FrameRate, out rate);
                    int fps = rateResult >= 0 && (uint)rate > 0 ? (int)Math.Round((double)(rate >> 32) / (uint)rate) : 15;
                    bool hasAudio = Call<GetTypeCall>(reader, 6)(reader, FirstAudio, out audio) >= 0;
                    PropVariant duration = new PropVariant();
                    long ticks;
                    try
                    {
                        Check(Call<PresentationAttributeCall>(reader, 12)(reader, -1, ref PresentationDuration, out duration), "Read recording duration");
                        if (duration.Type != 21 && duration.Type != 20) throw new InvalidOperationException("Windows did not report a usable recording duration.");
                        ticks = duration.LongValue;
                    }
                    finally { PropVariantClear(ref duration); }
                    Metadata = new VideoMetadata();
                    Metadata.Width = (int)(size >> 32);
                    Metadata.Height = (int)(uint)size;
                    Metadata.FramesPerSecond = Math.Max(1, Math.Min(60, fps));
                    Metadata.Duration = TimeSpan.FromTicks(ticks);
                    Metadata.HasAudio = hasAudio;
                    stride = Metadata.Width * 4;
                    decodedHeight = Metadata.Height;
                    if (Metadata.Width < 2 || Metadata.Height < 2 || ticks <= 0) throw new InvalidOperationException("The recording has no usable video timeline.");
                    if (decode)
                    {
                        Check(Call<SelectStreamCall>(reader, 4)(reader, AnyStream, false), "Select recording streams");
                        ConfigureVideo();
                        Check(Call<SelectStreamCall>(reader, 4)(reader, FirstVideo, true), "Select video stream");
                        if (hasAudio)
                        {
                            ConfigureAudio();
                            Check(Call<SelectStreamCall>(reader, 4)(reader, FirstAudio, true), "Select audio stream");
                        }
                    }
                }
                catch { Dispose(); throw; }
                finally { Release(ref audio); Release(ref video); Release(ref attributes); }
            }

            private void ConfigureVideo()
            {
                IntPtr type = IntPtr.Zero;
                try
                {
                    Check(MFCreateMediaType(out type), "Prepare decoded video");
                    SetGuid(type, MajorType, VideoType);
                    SetGuid(type, Subtype, Rgb32);
                    Check(Call<SetTypeCall>(reader, 7)(reader, FirstVideo, IntPtr.Zero, type), "Decode video frames");
                    Release(ref type);
                    Check(Call<GetTypeCall>(reader, 6)(reader, FirstVideo, out type), "Read decoded frame format");
                    int decodedStride;
                    if (Call<GetUInt32Call>(type, 7)(type, ref DefaultStride, out decodedStride) >= 0 && decodedStride != 0) stride = decodedStride;
                    ulong decodedSize;
                    if (Call<GetUInt64Call>(type, 8)(type, ref FrameSize, out decodedSize) >= 0) decodedHeight = (int)(uint)decodedSize;
                }
                finally { Release(ref type); }
            }

            private void ConfigureAudio()
            {
                IntPtr type = IntPtr.Zero;
                try
                {
                    Check(MFCreateMediaType(out type), "Prepare decoded audio");
                    SetGuid(type, MajorType, AudioType);
                    SetGuid(type, Subtype, Pcm);
                    SetUInt(type, AudioChannels, 2);
                    SetUInt(type, AudioRate, 48000);
                    SetUInt(type, AudioBits, 16);
                    SetUInt(type, AudioAlignment, 4);
                    SetUInt(type, AudioAverage, 192000);
                    Check(Call<SetTypeCall>(reader, 7)(reader, FirstAudio, IntPtr.Zero, type), "Decode recording audio");
                }
                finally { Release(ref type); }
            }

            internal void Seek(long timestamp)
            {
                Guid format = Guid.Empty;
                PropVariant position = new PropVariant();
                position.Type = 20;
                position.LongValue = timestamp;
                Check(Call<SeekCall>(reader, 8)(reader, ref format, ref position), "Seek to the trim selection");
            }

            internal void Read(out int actual, out int flags, out long time, out IntPtr sample)
            {
                Check(Call<ReadCall>(reader, 9)(reader, AnyStream, 0, out actual, out flags, out time, out sample), "Read recording samples");
            }

            internal bool IsVideoStream(int stream)
            {
                IntPtr type = IntPtr.Zero;
                try
                {
                    Check(Call<GetTypeCall>(reader, 6)(reader, stream, out type), "Identify recording stream");
                    Guid major;
                    Check(Call<GetGuidCall>(type, 10)(type, ref MajorType, out major), "Read recording stream kind");
                    if (major != VideoType && major != AudioType) throw new InvalidOperationException("The recording includes an unsupported selected stream.");
                    return major == VideoType;
                }
                finally { Release(ref type); }
            }

            internal void Deselect(int stream) { Check(Call<SelectStreamCall>(reader, 4)(reader, stream, false), "Finish reading the trim range"); }

            internal void ValidateFormat(int stream, bool video)
            {
                IntPtr type = IntPtr.Zero;
                try
                {
                    Check(Call<GetTypeCall>(reader, 6)(reader, stream, out type), "Check decoded media format");
                    Guid subtype;
                    Check(Call<GetGuidCall>(type, 10)(type, ref Subtype, out subtype), "Check decoded media kind");
                    if (video)
                    {
                        ulong size;
                        Check(Call<GetUInt64Call>(type, 8)(type, ref FrameSize, out size), "Check decoded frame dimensions");
                        int codedWidth = (int)(size >> 32), codedHeight = (int)(uint)size;
                        // H.264 decoders can expose macroblock padding (e.g. 360
                        // visible rows in a368-row buffer). Keep the original visible size.
                        if (subtype != Rgb32 || codedWidth < Metadata.Width || codedWidth > ((Metadata.Width + 15) & ~15) ||
                            codedHeight < Metadata.Height || codedHeight > ((Metadata.Height + 15) & ~15))
                            throw new InvalidOperationException("The recording changes video format mid-stream and cannot be trimmed safely. Decoded " + (int)(size >> 32) + "x" + (int)(uint)size + " " + subtype + ".");
                        decodedHeight = codedHeight;
                        int decodedStride;
                        if (Call<GetUInt32Call>(type, 7)(type, ref DefaultStride, out decodedStride) >= 0 && decodedStride != 0) stride = decodedStride;
                    }
                    else
                    {
                        int channels, rate, bits;
                        Check(Call<GetUInt32Call>(type, 7)(type, ref AudioChannels, out channels), "Check decoded audio channels");
                        Check(Call<GetUInt32Call>(type, 7)(type, ref AudioRate, out rate), "Check decoded audio sample rate");
                        Check(Call<GetUInt32Call>(type, 7)(type, ref AudioBits, out bits), "Check decoded audio precision");
                        if (subtype != Pcm || channels != 2 || rate != 48000 || bits != 16)
                            throw new InvalidOperationException("The recording changes audio format mid-stream and cannot be trimmed safely.");
                    }
                }
                finally { Release(ref type); }
            }

            internal long SampleDuration(IntPtr sample, long fallback)
            {
                long duration;
                return Call<GetTimeCall>(sample, 37)(sample, out duration) >= 0 && duration > 0 ? duration : fallback;
            }

            internal byte[] CopySampleBytes(IntPtr sample)
            {
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    Check(Call<ContiguousCall>(sample, 41)(sample, out buffer), "Read media data");
                    IntPtr data;
                    int maximum, length;
                    Check(Call<LockCall>(buffer, 3)(buffer, out data, out maximum, out length), "Lock media data");
                    try
                    {
                        byte[] bytes = new byte[length];
                        Marshal.Copy(data, bytes, 0, length);
                        return bytes;
                    }
                    finally { Check(Call<MediaFoundationVideoWriter.NoArgumentCall>(buffer, 4)(buffer), "Unlock media data"); }
                }
                finally { Release(ref buffer); }
            }

            internal void CopyVideo(IntPtr sample, Bitmap target)
            {
                byte[] bytes = CopySampleBytes(sample);
                int absoluteStride = Math.Abs(stride);
                if ((long)absoluteStride * decodedHeight > bytes.Length || absoluteStride < target.Width * 4)
                    throw new InvalidOperationException("The decoded video frame has an unsupported stride.");
                BitmapData data = target.LockBits(new Rectangle(0, 0, target.Width, target.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int first = stride < 0 ? (decodedHeight - 1) * absoluteStride : 0;
                    for (int y = 0; y < target.Height; y++)
                        Marshal.Copy(bytes, first + y * stride, IntPtr.Add(data.Scan0, y * data.Stride), target.Width * 4);
                }
                finally { target.UnlockBits(data); }
            }

            public void Dispose()
            {
                Release(ref reader);
                if (mediaStarted) { mediaStarted = false; MFShutdown(); }
                if (comInitialized) { comInitialized = false; CoUninitialize(); }
            }
        }

        private static void SetGuid(IntPtr value, Guid key, Guid item) { Check(Call<SetGuidCall>(value, 24)(value, ref key, ref item), "Set video decoder format"); }
        private static void SetUInt(IntPtr value, Guid key, int item) { Check(Call<SetUIntCall>(value, 21)(value, ref key, item), "Set video decoder options"); }
        private static T Call<T>(IntPtr value, int slot) where T : class { return MediaFoundationVideoWriter.Call<T>(value, slot); }
        private static void Check(int hr, string action) { MediaFoundationVideoWriter.Check(hr, action); }
        private static void Release(ref IntPtr value) { MediaFoundationVideoWriter.Release(ref value); }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal long LongValue; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetTypeCall(IntPtr self, int stream, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetTypeCall(IntPtr self, int stream, IntPtr reserved, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SelectStreamCall(IntPtr self, int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SeekCall(IntPtr self, ref Guid format, ref PropVariant position);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadCall(IntPtr self, int stream, int control, out int actual, out int flags, out long time, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuidCall(IntPtr self, ref Guid key, out Guid result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt32Call(IntPtr self, ref Guid key, out int result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt64Call(IntPtr self, ref Guid key, out ulong result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentationAttributeCall(IntPtr self, int stream, ref Guid key, out PropVariant result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetTimeCall(IntPtr self, out long duration);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ContiguousCall(IntPtr self, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockCall(IntPtr self, out IntPtr data, out int maximum, out int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidCall(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUIntCall(IntPtr self, ref Guid key, int value);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attributes, int count);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string path, IntPtr attributes, out IntPtr reader);
        private static Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid VideoType = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static Guid AudioType = new Guid("73647561-0000-0010-8000-00aa00389b71");
        private static Guid Rgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static Guid Pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static Guid PresentationDuration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        private static Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static Guid VideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static Guid AudioRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static Guid AudioBits = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static Guid AudioAlignment = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static Guid AudioAverage = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    }
}
