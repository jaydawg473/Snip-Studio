#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SnipStudio.WinUI.Backend;

/// <summary>A chosen image path and the destination state observed before encoding.</summary>
public sealed class ImageSaveTarget
{
    public string Path { get; }
    internal ImageDestinationStamp? Previous { get; }

    internal ImageSaveTarget(string path, ImageDestinationStamp? previous)
    {
        Path = path;
        Previous = previous;
    }
}

internal readonly record struct ImageDestinationStamp(long Length, DateTime CreatedUtc, DateTime WrittenUtc, string Sha256);

internal static class AtomicImageWriter
{
    internal static ImageSaveTarget Prepare(string path, CancellationToken cancellationToken = default)
    {
        ImageSaveTarget target = PrepareDestination(path, cancellationToken);
        _ = FormatFor(target.Path);
        return target;
    }

    internal static ImageSaveTarget PrepareDestination(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a filename.", nameof(path));
        string fullPath = System.IO.Path.GetFullPath(path);
        return new ImageSaveTarget(fullPath, Inspect(fullPath, cancellationToken));
    }

    internal static ImageSaveTarget CaptureOpenedImageTarget(string path, FileStream input, CancellationToken cancellationToken)
    {
        // Hash the same opened file that will supply the decoder's pixels.
        ImageDestinationStamp stamp = Inspect(input, cancellationToken);
        input.Position = 0;
        return new ImageSaveTarget(path, stamp);
    }

    private static ImageDestinationStamp? Inspect(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream input;
        try
        {
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        // Bind the timestamps and fingerprint to the same opened file, with
        // writers and renames excluded until the fingerprint is complete.
        using (input)
            return Inspect(input, cancellationToken);
    }

    private static ImageDestinationStamp Inspect(FileStream input, CancellationToken cancellationToken)
    {
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var handle = input.SafeFileHandle;
            long length = input.Length;
            DateTime created = File.GetCreationTimeUtc(handle);
            DateTime written = File.GetLastWriteTimeUtc(handle);
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ImageDestinationStamp(length, created, written, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }

    private static ImageFormat? FormatFor(string path)
    {
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            ".pdf" => null,
            _ => throw new ArgumentException("Choose a PNG, JPEG, Bitmap or PDF filename.", nameof(path))
        };
    }

    internal static ImageSaveTarget Save(Bitmap image, ImageSaveTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageFormat? format = FormatFor(target.Path);
        if (format == null)
        {
            string temporary = StagingPath(target, ".pdf");
            try
            {
                global::SnipStudio.PdfExportService.Save(image, temporary);
                ImageSaveTarget published = Publish(temporary, target, cancellationToken);
                temporary = "";
                return published;
            }
            finally { if (temporary.Length != 0) RemoveStagedFile(temporary); }
        }

        return WriteStaged(target, output =>
        {
            if (format.Guid != ImageFormat.Jpeg.Guid)
            {
                image.Save(output, format);
                return;
            }
            using Bitmap flattened = new(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(flattened))
            {
                graphics.Clear(Color.White);
                graphics.DrawImageUnscaled(image, 0, 0);
            }
            flattened.Save(output, format);
        }, cancellationToken);
    }

    internal static ImageSaveTarget WriteStaged(ImageSaveTarget target, Action<Stream> encode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = StagingPath(target, ".tmp");
        bool ownsTemporary = false;
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                encode(output);
                output.Flush(true);
            }
            ImageSaveTarget published = Publish(temporary, target, cancellationToken);
            ownsTemporary = false;
            return published;
        }
        finally { if (ownsTemporary) RemoveStagedFile(temporary); }
    }

    private static string StagingPath(ImageSaveTarget target, string suffix) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target.Path)!, ".snip-winui-" + Guid.NewGuid().ToString("N") + suffix);

    private static void RemoveStagedFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ImageSaveTarget Publish(string temporary, ImageSaveTarget target, CancellationToken cancellationToken)
    {
        ImageDestinationStamp staged = Inspect(temporary, cancellationToken) ??
            throw new IOException("The staged save file could not be read. Your previous file is kept.");
        string? backup = PublishFile(temporary, target, cancellationToken);
        ImageDestinationStamp published = VerifyPublishedImage(target.Path, staged, backup);
        if (backup is not null) RemoveStagedFile(backup);
        return new ImageSaveTarget(target.Path, published);
    }

    internal static ImageDestinationStamp VerifyPublishedImage(string path, ImageDestinationStamp staged, string? backup)
    {
        // Publication is already in progress. Finish this check even if the
        // caller canceled, so the resulting status describes the on-disk bytes.
        const string message = "The save destination could not be verified after writing. Save a new copy.";
        string recovery = backup is null ? "" : " The recovery file is at " + backup + ".";
        ImageDestinationStamp? published;
        try { published = Inspect(path, CancellationToken.None); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new IOException(message + recovery, failure);
        }
        if (published is not ImageDestinationStamp result || result.Length != staged.Length || result.Sha256 != staged.Sha256)
            throw new IOException(message + recovery);
        // Use the actual destination's timestamps, since Windows can preserve
        // the prior creation time during a successful replacement.
        return result;
    }

    // A copy has no subsequent in-place Save target, so it needs no digest
    // of its already staged bytes. Verify the displaced destination as usual.
    internal static void PublishCopy(string temporary, ImageSaveTarget target, CancellationToken cancellationToken)
    {
        long length = new FileInfo(temporary).Length;
        string? backup = PublishFile(temporary, target, cancellationToken);
        _ = InspectPublishedFile(target.Path, length, backup);
        if (backup is not null) RemoveStagedFile(backup);
    }

    private static string? PublishFile(string temporary, ImageSaveTarget target, CancellationToken cancellationToken)
    {
        string path = target.Path;
        if (target.Previous is not ImageDestinationStamp previous)
        {
            // Windows makes the absent-path check and rename one operation.
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, path); }
            catch (IOException failure) when (File.Exists(path))
            {
                throw new IOException("A file appeared at the chosen destination while saving. It has been kept. Choose another filename or confirm the replacement again.", failure);
            }
            return null;
        }

        using SafeFileHandle? approved = CheckReplacementTarget(path, previous, cancellationToken);

        // Keep the displaced bytes until the held file identity or a full
        // fingerprint confirms they belong to the approved revision.
        string backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            ".snip-winui-recovery-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(path));
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Replace(temporary, path, backup); }
        catch (IOException failure) when (File.Exists(backup))
        {
            throw new IOException("Windows could not complete the replacement. The previous file is retained at " + backup, failure);
        }

        if (approved is not null && IsDisplacedFileStillHeld(backup, approved)) return backup;

        ImageDestinationStamp? displaced;
        try { displaced = Inspect(backup, CancellationToken.None); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The file was written, but its previous contents could not be verified. Check the recovery file at " + backup + " before proceeding.", failure);
        }
        if (displaced is not ImageDestinationStamp priorContent ||
            priorContent.Length != previous.Length || priorContent.Sha256 != previous.Sha256)
            throw new IOException("The file was written, but the previous file may have changed during replacement. Check the recovery file at " + backup + " before proceeding.");
        return backup;
    }

    private static SafeFileHandle? CheckReplacementTarget(string path, ImageDestinationStamp previous,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan); }
        catch (FileNotFoundException) { throw ChangedDestination(); }
        catch (DirectoryNotFoundException) { throw ChangedDestination(); }
        using (input)
        {
            if (Inspect(input, cancellationToken) != previous) throw ChangedDestination();
            return TryHoldApprovedFile(path, input.SafeFileHandle);
        }
    }

    private static IOException ChangedDestination() =>
        new("The chosen destination changed while saving. Choose it again or save to a different filename.");

    private static SafeFileHandle? TryHoldApprovedFile(string path, SafeFileHandle original)
    {
        if (path.AsSpan(System.IO.Path.GetPathRoot(path)?.Length ?? 0).Contains(':') ||
            !TryGetLocalNtfsIdentity(original, out LocalFileIdentity expected)) return null;

        SafeFileHandle? lease = null;
        try
        {
            // Open the delete-sharing, write-denying handle before releasing
            // the original no-delete handle. A path alias can still resolve
            // to a different object, so confirm both pinned handles agree.
            lease = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!TryGetLocalNtfsIdentity(lease, out LocalFileIdentity actual) || actual != expected) return null;
            SafeFileHandle held = lease;
            lease = null;
            return held;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
        finally { lease?.Dispose(); }
    }

    internal static bool IsDisplacedFileStillHeld(string backup, SafeFileHandle approved)
    {
        try
        {
            using SafeFileHandle displaced = File.OpenHandle(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
            // An identical live file is still write-locked continuously
            // since approval was rechecked. An unproven identity uses SHA.
            return TryGetLocalNtfsIdentity(approved, out LocalFileIdentity expected) &&
                TryGetLocalNtfsIdentity(displaced, out LocalFileIdentity actual) && actual == expected;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TryGetLocalNtfsIdentity(SafeFileHandle handle, out LocalFileIdentity identity)
    {
        identity = default;
        const string volumePrefix = @"\\?\Volume{";
        const uint volumeGuidWithOpenedName = 0x9;
        var resolvedName = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, resolvedName, (uint)resolvedName.Capacity, volumeGuidWithOpenedName);
        string path = resolvedName.ToString();
        if (length != path.Length || path.Length < volumePrefix.Length + 38) return false;
        int suffix = volumePrefix.Length + 36;
        if (!path.StartsWith(volumePrefix, StringComparison.OrdinalIgnoreCase) || path[suffix] != '}' ||
            path[suffix + 1] != '\\' || path.AsSpan(suffix + 2).Contains(':') ||
            !Guid.TryParseExact(path.AsSpan(volumePrefix.Length, 36), "D", out Guid volume)) return false;
        var fileSystem = new StringBuilder(16);
        if (!GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            fileSystem, (uint)fileSystem.Capacity) || !string.Equals(fileSystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            return false;
        const int fileIdInfo = 18;
        if (!GetFileInformationByHandleEx(handle, fileIdInfo, out NativeFileId id, 24) || (id.Low | id.High) == 0) return false;
        identity = new LocalFileIdentity(volume, id.VolumeSerial, id.Low, id.High);
        return true;
    }

    private readonly record struct LocalFileIdentity(Guid Volume, ulong VolumeSerial, ulong Low, ulong High);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileId
    {
        public ulong VolumeSerial, Low, High;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder name, uint size, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, IntPtr volumeName, uint nameSize,
        IntPtr volumeSerial, IntPtr maximumComponent, IntPtr flags, StringBuilder fileSystem, uint fileSystemSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int type, out NativeFileId info, uint size);

    private static FileInfo InspectPublishedFile(string path, long length, string? backup)
    {
        var published = new FileInfo(path);
        if (!published.Exists || published.Length != length)
            throw new IOException("The save destination changed immediately after writing. Save a new copy." +
                (backup is null ? "" : " The recovery file is at " + backup + "."));
        return published;
    }
}
