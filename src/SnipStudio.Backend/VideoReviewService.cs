#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

/// <summary>The current MP4 can be copied within the cap, or it needs an encoding plan.</summary>
public readonly record struct VideoExportPreparation(global::SnipStudio.VideoExportPlan? EncodingPlan, long? MaximumCopyBytes);

/// <summary>Local Windows codec operations; no shell, encoder helper or upload.</summary>
public static class VideoReviewService
{
    private static readonly SemaphoreSlim inspectionSlots = new(2, 2);
    public static async Task<global::SnipStudio.VideoMetadata> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        // Legacy validation itself touches the filesystem; keep that work off
        // the dispatcher as well as the native reader it creates.
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<global::SnipStudio.VideoMetadata> pending = InspectCoreAsync(path, request.Token);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            request.Cancel();
            _ = ObserveAsync(pending);
            throw new TimeoutException("Windows did not finish reading the video within ten seconds. You can still play it or save a copy. Try opening it again later.");
        }
        catch { _ = ObserveAsync(pending); throw; }
    }

    private static async Task<global::SnipStudio.VideoMetadata> InspectCoreAsync(string path, CancellationToken token)
    {
        await inspectionSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => global::SnipStudio.VideoTrimService.InspectAsync(path, token), token).ConfigureAwait(false);
        }
        finally { inspectionSlots.Release(); }
    }

    private static async Task ObserveAsync(Task pending)
    { try { await pending.ConfigureAwait(false); } catch (Exception) { } }

    public static async Task<long> ReadSourceSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<long> pending = ReadSourceSizeCoreAsync(path, request.Token);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            request.Cancel();
            _ = ObserveAsync(pending);
            throw new TimeoutException("Windows did not finish checking the source file size. Try again when its folder is available.");
        }
        catch { _ = ObserveAsync(pending); throw; }
    }

    private static async Task<long> ReadSourceSizeCoreAsync(string path, CancellationToken token)
    {
        await inspectionSlots.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => new FileInfo(path).Length, token).ConfigureAwait(false); }
        finally { inspectionSlots.Release(); }
    }

    public static VideoExportPreparation PrepareExport(string path, long sourceBytes, global::SnipStudio.VideoMetadata? metadata,
        global::SnipStudio.VideoExportPreset preset, int targetMegabytes)
    {
        if (sourceBytes <= 0) throw new ArgumentException("The selected video is empty.", nameof(sourceBytes));
        if (preset == global::SnipStudio.VideoExportPreset.TargetAttachmentSize)
        {
            if (targetMegabytes is < 1 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(targetMegabytes), "Choose a target between 1 and 1000 MB.");
            long maximumBytes = targetMegabytes * 1_000_000L;
            if (sourceBytes <= maximumBytes && Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                return new(null, maximumBytes);
        }
        if (metadata is null)
            throw new InvalidOperationException("Video details are required to compress this file. Try again once they are available.");
        return new(global::SnipStudio.VideoTrimService.CreateExportPlan(metadata, sourceBytes, preset, targetMegabytes), null);
    }

    /// <summary>Returns evenly spaced thumbnails as PNGs without moving the playback position.</summary>
    public static async Task<byte[][]> ReadFilmstripAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = request.Token;
        Task<byte[][]> pending = Task.Run(async () =>
        {
            Bitmap[] frames = await global::SnipStudio.VideoTrimService.ReadFilmstripAsync(
                path, 12, new Size(176, 100), token).ConfigureAwait(false);
            try
            {
                var thumbnails = new byte[frames.Length][];
                for (int index = 0; index < frames.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    using var png = new MemoryStream();
                    frames[index].Save(png, ImageFormat.Png);
                    thumbnails[index] = png.ToArray();
                }
                return thumbnails;
            }
            finally
            {
                foreach (Bitmap frame in frames) frame.Dispose();
            }
        }, token);

        try { return await pending.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            request.Cancel();
            _ = ObserveAsync(pending);
            throw new TimeoutException("Windows did not finish decoding filmstrip thumbnails within twelve seconds.");
        }
        catch { _ = ObserveAsync(pending); throw; }
    }

    public static Task TrimAsync(string source, string destination, TimeSpan start, TimeSpan end) => Task.Run(() =>
        global::SnipStudio.VideoTrimService.TrimAsync(source, destination, start, end, PrepareVideoPublish));

    public static Task<global::SnipStudio.VideoExportResult> ExportAsync(string source, string destination,
        global::SnipStudio.VideoExportPlan plan, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return global::SnipStudio.VideoTrimService.ExportWithPreparedDestinationAsync(
                source, destination, plan, cancellationToken, progress!, enforceTargetLimit: true, PrepareVideoPublish);
        }, cancellationToken);
    }

    public static Task<global::SnipStudio.VideoExportResult> ExportAsync(string source, string destination,
        global::SnipStudio.VideoMetadata metadata, global::SnipStudio.VideoExportPreset preset, int targetMegabytes,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = global::SnipStudio.VideoTrimService.CreateExportPlan(metadata, new FileInfo(source).Length, preset, targetMegabytes);
            return global::SnipStudio.VideoTrimService.ExportWithPreparedDestinationAsync(
                source, destination, plan, cancellationToken, progress!, enforceTargetLimit: false, PrepareVideoPublish);
        }, cancellationToken);
    }

    private static Action<string> PrepareVideoPublish(string destination, CancellationToken cancellationToken)
    {
        ImageSaveTarget target = AtomicImageWriter.PrepareDestination(destination, cancellationToken);
        return staged => AtomicImageWriter.PublishCopy(staged, target, cancellationToken);
    }

    public static Task SaveCopyAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        SaveCopyAsync(source, destination, cancellationToken, maximumBytes: null);

    public static Task SaveCopyAsync(string source, string destination, CancellationToken cancellationToken, long? maximumBytes)
    {
        if (maximumBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        string input = Path.GetFullPath(source), output = Path.GetFullPath(destination);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different filename for the copy. The original video is kept.", nameof(destination));
        if (!string.Equals(Path.GetExtension(input), Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save a copy with the original video's extension. Use Export for an MP4 conversion.", nameof(destination));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new DirectoryNotFoundException("The chosen folder is unavailable.");
            ImageSaveTarget target = AtomicImageWriter.PrepareDestination(output, cancellationToken);
            SavePreparedCopy(input, target, cancellationToken, maximumBytes);
        }, cancellationToken);
    }

    internal static void SavePreparedCopy(string source, ImageSaveTarget target, CancellationToken cancellationToken, long? maximumBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(Path.GetDirectoryName(target.Path)!, ".snip-video-copy-" +
            Guid.NewGuid().ToString("N") + Path.GetExtension(target.Path));
        bool ownsStage = false;
        try
        {
            using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (maximumBytes is { } limit && from.Length > limit)
                    throw new InvalidOperationException("The current video exceeds the attachment limit. Compress the video or choose a larger target.");
                using var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                ownsStage = true;
                byte[] buffer = new byte[128 * 1024];
                int count;
                while ((count = from.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    to.Write(buffer, 0, count);
                }
                to.Flush(true);
            }
            AtomicImageWriter.PublishCopy(temporary, target, cancellationToken);
            ownsStage = false;
        }
        finally
        {
            if (ownsStage)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
