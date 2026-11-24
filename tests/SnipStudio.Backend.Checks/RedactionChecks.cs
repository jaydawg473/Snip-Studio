using System;
using System.Buffers.Binary;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckRedactionAsync(string output)
    {
        byte[] original = CreateImage(transparent: true);
        using EditorSession editor = new();
        editor.OpenImageBytes(original);
        editor.AddStroke([new ImagePoint(0, 4), new ImagePoint(63, 4)], 0xFFFF5500, 2, false);
        PreviewSnapshot flattened = await editor.RenderPreviewAsync();
        string revisionPixels = PixelHash(flattened.PngBytes);

        byte[] withMetadata = AddRedactionFixtureTextChunk(flattened.PngBytes);
        var supplied = new PreviewSnapshot(flattened.Revision, withMetadata);
        ImageRectangle[] areas = [new(-1, 1.2, 8.1, 4.6), new(60.4, 44.8, 40, 10)];
        RedactedImage result = await EditorSession.RenderRedactedCopyAsync(supplied, areas);
        byte[] written = result.PngBytes.ToArray();
        Check(withMetadata.AsSpan().IndexOf("private-source-meta"u8) >= 0 &&
            written.AsSpan().IndexOf("private-source-meta"u8) < 0, "Redaction emits a fresh PNG without source text metadata");

        using (MemoryStream redactedStream = new(written, writable: false))
        using (Bitmap redactedBitmap = new(redactedStream))
        using (MemoryStream sourceStream = new(flattened.PngBytes, writable: false))
        using (Bitmap sourceBitmap = new(sourceStream))
        {
            Check(redactedBitmap.Size == sourceBitmap.Size, "Redacted copy preserves dimensions");
            bool covered = true, outside = true;
            for (int y = 0; y < sourceBitmap.Height; y++)
            {
                for (int x = 0; x < sourceBitmap.Width; x++)
                {
                    Color actual = redactedBitmap.GetPixel(x, y);
                    if (x < 8 && y is >= 1 and < 6 || x >= 60 && y >= 44)
                    {
                        covered &= actual.ToArgb() == Color.Black.ToArgb();
                        continue;
                    }
                    Color expected = sourceBitmap.GetPixel(x, y);
                    outside &= actual.ToArgb() == expected.ToArgb() || actual.A == 0 && expected.A == 0;
                }
            }
            Check(covered, "Redaction replaces every intersecting pixel, including annotation ink and clipped edges, with opaque black");
            Check(outside, "Redaction keeps visible pixels outside its boundaries");
        }

        string saved = Path.Combine(output, "redacted-preview.png");
        await result.SaveCopyAsync(saved);
        Check(File.ReadAllBytes(saved).SequenceEqual(written), "Saved PNG bytes are identical to the redaction preview");
        await ExpectAsync<IOException>(() => result.SaveCopyAsync(saved), "Redaction refuses to overwrite any existing destination");
        Check(File.ReadAllBytes(saved).SequenceEqual(written), "Redaction collision leaves the existing file intact");
        Expect<ArgumentException>(() => result.SaveCopyAsync(Path.Combine(output, "redacted.pdf")), "Redaction publishes PNG copies only");

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        string canceledPath = Path.Combine(output, "redaction-canceled.png");
        Expect<OperationCanceledException>(() => result.SaveCopyAsync(canceledPath, canceled.Token), "Canceled redaction save stops before staging");
        Check(!File.Exists(canceledPath), "Canceled redaction writes no output");
        await ExpectAsync<ArgumentException>(() => EditorSession.RenderRedactedCopyAsync(supplied, [new(64, 2, 4, 5)]),
            "Redaction rejects areas entirely outside the source image");
        await ExpectAsync<ArgumentException>(() => EditorSession.RenderRedactedCopyAsync(supplied, [new(double.NaN, 2, 4, 5)]),
            "Redaction rejects non-finite bounds");
        Check(editor.Revision == flattened.Revision && editor.CanUndo && editor.IsDirty &&
            PixelHash((await editor.RenderPreviewAsync()).PngBytes) == revisionPixels,
            "Redaction leaves the source revision, preview, dirty state and undo history intact");
        editor.Undo();
        Check(PixelHash((await editor.RenderPreviewAsync()).PngBytes) == PixelHash(original), "Original annotation Undo still works after redaction");
    }

    private static byte[] AddRedactionFixtureTextChunk(byte[] source)
    {
        // Insert an ancillary text chunk immediately after PNG's fixed IHDR.
        ReadOnlySpan<byte> payload = "tEXtAuthor\0private-source-meta"u8;
        Span<byte> number = stackalloc byte[4];
        using MemoryStream output = new();
        output.Write(source.AsSpan(0, 33));
        BinaryPrimitives.WriteInt32BigEndian(number, payload.Length - 4);
        output.Write(number);
        output.Write(payload);
        uint crc = uint.MaxValue;
        foreach (byte value in payload)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
        output.Write(source.AsSpan(33));
        return output.ToArray();
    }
}
