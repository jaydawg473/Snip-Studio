#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

public sealed record DesktopExternalEditorResult(string SnapshotPath, string Publisher);

/// <summary>An explicitly chosen, signed editor receives a frozen image copy.</summary>
public static class DesktopExternalEditorService
{
    private const long MaximumRetainedBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim gate = new(1, 1);

    public static async Task<DesktopExternalEditorResult> OpenAsync(byte[] png, string executablePath, nint owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (owner == 0) throw new ArgumentException("An editor window is required.", nameof(owner));
        if (png.Length is < 8 or > 128 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("The external editor requires a PNG snapshot no larger than 128 MB.", nameof(png));
        if (!Path.IsPathFullyQualified(executablePath) || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose the external editor's absolute .exe path.", nameof(executablePath));
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("The previous editor signature check is still finishing. Try again later.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var launchState = new LaunchState();
        // Detach the snapshot from caller-owned buffers before background work.
        byte[] frozen = (byte[])png.Clone();
        Task<DesktopExternalEditorResult> worker;
        try
        {
            worker = Task.Run(() =>
            {
                try { return OpenCore(frozen, executablePath, timeout.Token, launchState); }
                finally { gate.Release(); }
            });
        }
        catch { gate.Release(); timeout.Dispose(); throw; }
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(16), cancellationToken); }
        catch (TimeoutException)
        {
            timeout.Cancel();
            bool preventedLaunch = Interlocked.CompareExchange(ref launchState.Value, -1, 0) == 0;
            throw new TimeoutException(preventedLaunch
                ? "The editor signature check did not finish within 16 seconds. Launch was canceled; your document is kept."
                : "Windows has not finished the external-editor activation. It may still open; check the selected editor before retrying. Your document is kept.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The editor signature check exceeded its time limit. Launch was canceled; your document is kept."); }
        finally
        {
            if (worker.IsCompleted) timeout.Dispose();
            else
            {
                timeout.Cancel();
                _ = Interlocked.CompareExchange(ref launchState.Value, -1, 0);
                _ = ObserveAndDisposeAsync(worker, timeout);
            }
        }
    }

    private static DesktopExternalEditorResult OpenCore(byte[] png, string executablePath, CancellationToken cancellationToken, LaunchState launchState)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string executable = Path.GetFullPath(executablePath);
        // Deny writes and replacement from signature verification through image
        // activation. Process.Start receives this exact path without a shell.
        using var executableLock = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        string publisher = DesktopExecutableTrust.VerifySignedTimestampedExecutable(executable);
        cancellationToken.ThrowIfCancellationRequested();
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipStudio.WinUI3", "ExternalEditor");
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The external-editor working-copy folder is a redirected link. Choose Save as instead.");
        var retained = new DirectoryInfo(folder).EnumerateFiles("snip-*.png", SearchOption.TopDirectoryOnly).Take(1025).ToArray();
        if (retained.Length > 1024) throw new IOException("The external-editor working-copy folder has too many files. Review and save your work before removing copies from " + folder + ".");
        // These files are editable working copies. Age does not prove that
        // their user edits have been saved elsewhere, so never delete them
        // automatically. The explicit budget bounds storage for this feature.
        if (retained.Length >= 32 || retained.Sum(item => item.Length) + png.LongLength > MaximumRetainedBytes)
            throw new IOException("External-editor copies reached the limit of 32 files or 512 MB. Review and save your work before removing copies from " + folder + ". Existing copies have been kept.");
        string snapshotPath = Path.Combine(folder, "snip-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".png");
        bool launched = false;
        try
        {
            using (var output = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                output.Write(png);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add(snapshotPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref launchState.Value, 1, 0) != 0) throw new OperationCanceledException(cancellationToken);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the selected editor.");
            launched = true;
            return new(snapshotPath, publisher);
        }
        finally
        {
            if (!launched)
            {
                try { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task ObserveAndDisposeAsync(Task worker, CancellationTokenSource timeout)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception) { /* Observe a late native trust result after UI timeout. */ }
        finally { timeout.Dispose(); }
    }

    private sealed class LaunchState { internal int Value; }
}
