#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Document = SnipStudio.EditorDocument;
using ProjectVersion = SnipStudio.EditorDocument.EditableFormatVersion;

namespace SnipStudio.WinUI.Backend;

// Version 2 adds closed-shape rotation. Version 3 adds outline patterns and
// makes the recorded freehand highlighter alpha authoritative.
// Only the pixel rows are compressed, and the payload contains no external
// files or paths.
internal static class EditableProjectFile
{
    internal const string Extension = ".snipstudio";
    private const int DigestLength = 32;
    private const int MaxProjectFileBytes = 300 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "SNIPEDIT"u8;

    internal static void Save(Stream destination, Document.EditableSnapshot snapshot, CancellationToken cancellationToken)
    {
        Bitmap bitmap = snapshot.Image;
        cancellationToken.ThrowIfCancellationRequested();
        ProjectVersion version = Document.GetEditableFormatVersion(snapshot.Annotations);
        using MemoryStream records = new();
        using (BinaryWriter metadata = new(records, Encoding.UTF8, leaveOpen: true))
        {
            metadata.Write(bitmap.Width);
            metadata.Write(bitmap.Height);
            Document.WriteEditableAnnotations(metadata, snapshot.Annotations, version, cancellationToken);
        }
        ReadOnlySpan<byte> bytes = records.GetBuffer().AsSpan(0, checked((int)records.Length));
        using BinaryWriter header = new(destination, Encoding.UTF8, leaveOpen: true);
        header.Write(Magic);
        header.Write((int)version);
        header.Write(bytes.Length);
        header.Write(SHA256.HashData(bytes));
        header.Write(bytes);
        long hashOffset = destination.Position;
        header.Write(new byte[DigestLength]);

        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (DeflateStream pixelStream = new(destination, CompressionLevel.Fastest, leaveOpen: true))
        {
            TransferRows(bitmap, ImageLockMode.ReadOnly, (row, pixels) =>
            {
                Marshal.Copy(pixels, row, 0, row.Length);
                digest.AppendData(row);
                pixelStream.Write(row);
            }, cancellationToken);
        }
        long end = destination.Position;
        if (end > MaxProjectFileBytes)
            throw new InvalidOperationException("The editable snip exceeds the maximum project file size. You can still export an image.");
        destination.Position = hashOffset;
        header.Write(digest.GetHashAndReset());
        destination.Position = end;
    }

    internal static Document.EditableSnapshot Read(string path, CancellationToken cancellationToken)
    {
        try
        {
            return ReadCore(path, cancellationToken);
        }
        catch (Exception failure) when (failure is EndOfStreamException or ArgumentException)
        {
            throw new InvalidDataException("The editable snip contains incomplete or invalid data.", failure);
        }
    }

    private static Document.EditableSnapshot ReadCore(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (file.Length > MaxProjectFileBytes) throw new InvalidDataException("The editable snip exceeds the maximum project file size.");
        using BinaryReader input = new(file, Encoding.UTF8, leaveOpen: true);
        if (!input.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("This file is not an editable Snip Studio project.");
        ProjectVersion version = (ProjectVersion)input.ReadInt32();
        if (version is not (ProjectVersion.Original or ProjectVersion.ShapeRotation or ProjectVersion.AnnotationStyles))
            throw new InvalidDataException("This editable snip was saved with an unsupported project version.");
        int length = input.ReadInt32();
        if (length is < 12 or > Document.MaximumEditableMetadataBytes ||
            length + 2L * DigestLength >= file.Length - file.Position)
            throw new InvalidDataException("The editable snip's object data has an invalid length.");
        byte[] expectedMetadataHash = input.ReadBytes(DigestLength);
        byte[] bytes = input.ReadBytes(length);
        if (bytes.Length != length || !CryptographicOperations.FixedTimeEquals(expectedMetadataHash, SHA256.HashData(bytes)))
            throw new InvalidDataException("The editable snip's object data is damaged.");
        byte[] expectedPixelsHash = input.ReadBytes(DigestLength);

        using BinaryReader metadata = new(new MemoryStream(bytes, writable: false));
        int width = metadata.ReadInt32();
        int height = metadata.ReadInt32();
        EditorSession.ValidateImageDimensions(width, height);
        cancellationToken.ThrowIfCancellationRequested();
        Bitmap pixels = new(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (DeflateStream pixelStream = new(file, CompressionMode.Decompress, leaveOpen: true))
            {
                TransferRows(pixels, ImageLockMode.WriteOnly, (row, destination) =>
                {
                    pixelStream.ReadExactly(row);
                    digest.AppendData(row);
                    Marshal.Copy(row, 0, destination, row.Length);
                }, cancellationToken);
                if (pixelStream.ReadByte() != -1 ||
                    !CryptographicOperations.FixedTimeEquals(expectedPixelsHash, digest.GetHashAndReset()))
                    throw new InvalidDataException("The editable snip's image pixels are damaged.");
            }
            return new Document.EditableSnapshot(pixels, Document.ReadEditableAnnotations(metadata, version, cancellationToken));
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    private static void TransferRows(Bitmap bitmap, ImageLockMode mode, Action<byte[], IntPtr> transfer,
        CancellationToken cancellationToken)
    {
        byte[] row = new byte[checked(bitmap.Width * 4)];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), mode, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transfer(row, IntPtr.Add(data.Scan0, y * data.Stride));
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
}
