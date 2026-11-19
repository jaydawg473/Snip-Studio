#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

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

    public static Task TrimAsync(string source, string destination, TimeSpan start, TimeSpan end) => Task.Run(() =>
        global::SnipStudio.VideoTrimService.TrimAsync(source, destination, start, end));

    public static Task<global::SnipStudio.VideoExportResult> ExportAsync(string source, string destination,
        global::SnipStudio.VideoMetadata metadata, global::SnipStudio.VideoExportPreset preset, int targetMegabytes,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = global::SnipStudio.VideoTrimService.CreateExportPlan(metadata, new FileInfo(source).Length, preset, targetMegabytes);
            return global::SnipStudio.VideoTrimService.ExportAsync(source, destination, plan, cancellationToken, progress!);
        }, cancellationToken);
    }

    public static Task SaveCopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        string input = Path.GetFullPath(source), output = Path.GetFullPath(destination);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different filename for the copy. The original video is kept.", nameof(destination));
        if (!string.Equals(Path.GetExtension(input), Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save a copy with the original video's extension. Use Export for an MP4 conversion.", nameof(destination));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new DirectoryNotFoundException("The chosen folder is unavailable.");
            string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".snip-video-copy-" + Guid.NewGuid().ToString("N") + Path.GetExtension(output));
            try
            {
                using (var from = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[128 * 1024];
                    int count;
                    while ((count = from.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        to.Write(buffer, 0, count);
                    }
                    to.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(output)) File.Replace(temporary, output, null);
                else File.Move(temporary, output);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, cancellationToken);
    }
}
