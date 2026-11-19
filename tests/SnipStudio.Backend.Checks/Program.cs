using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio.WinUI.Backend;

internal static class Program
{
    private static int assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        {
            Console.Error.WriteLine("Usage: SnipStudio.Backend.Checks --output <empty evidence directory>");
            return 2;
        }
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use an empty evidence directory; these checks never replace existing evidence.");
            return 2;
        }
        Directory.CreateDirectory(output);
        Stopwatch clock = Stopwatch.StartNew();
        using StaContext context = new();
        SynchronizationContext.SetSynchronizationContext(context);
        bool passed = false;
        string? failure = null;
        try
        {
            context.Run(CheckWorkflowsAsync(output));
            passed = true;
            return 0;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            clock.Stop();
            string json = JsonSerializer.Serialize(new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                Passed = passed,
                Assertions = assertions,
                ElapsedMilliseconds = clock.ElapsedMilliseconds,
                Scope = "Public backend API on an STA context; no WinUI controls, OCR, capture, clipboard, installation or hardware integration.",
                Failure = failure
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "backend-checks.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private static async Task CheckWorkflowsAsync(string output)
    {
        byte[] source = CreateImage();
        byte[] preservedSource = (byte[])source.Clone();
        using EditorSession session = new();
        Check(!session.HasImage && !session.IsDirty && !session.CanUndo && !session.CanRedo && !session.IsSaving, "Empty session state");
        Check(session.ImageWidth == 0 && session.ImageHeight == 0 && session.Revision == 0, "Empty session dimensions and revision");
        session.Undo(); session.Redo();
        Expect<InvalidOperationException>(() => session.RenderPreviewAsync(), "Empty preview rejected");
        await ExpectAsync<InvalidOperationException>(() => session.SaveAsync(Path.Combine(output, "empty.png")), "Empty save rejected");
        Check(!File.Exists(Path.Combine(output, "empty.png")), "Empty save writes no destination");

        session.OpenImageBytes(source);
        long originalRevision = session.Revision;
        Check(session.HasImage && session.IsDirty, "Opened image is unsaved");
        Check(session.ImageWidth == 64 && session.ImageHeight == 48, "Original dimensions preserved");
        Check(originalRevision > 0 && !session.CanUndo && !session.CanRedo, "Initial history state");
        PreviewSnapshot original = await session.RenderPreviewAsync();
        Check(original.Revision == originalRevision, "Preview identifies captured revision");
        Check(PixelHash(original.PngBytes) == PixelHash(source), "Opening preserves every source pixel");
        Check(ReadPixel(original.PngBytes, 2, 2).ToArgb() == Color.Blue.ToArgb(), "Source marker preserved");

        session.AddStroke(new[] { new ImagePoint(4, 8), new ImagePoint(55, 8) }, 0xFFFF0000, 3, false);
        long strokeRevision = session.Revision;
        PreviewSnapshot stroke = await session.RenderPreviewAsync();
        Check(strokeRevision > originalRevision && session.CanUndo && !session.CanRedo, "Drawing creates one history state");
        Check(ReadPixel(stroke.PngBytes, 24, 8).ToArgb() == Color.Red.ToArgb(), "Pen uses source pixel coordinates and ARGB color");
        Check(PixelHash(stroke.PngBytes) != PixelHash(original.PngBytes), "Drawing changes rendered output");
        session.AddRectangle(new ImagePoint(50, 35), new ImagePoint(12, 14), 0xFF008000, 2);
        long rectangleRevision = session.Revision;
        PreviewSnapshot rectangle = await session.RenderPreviewAsync();
        Check(rectangleRevision > strokeRevision, "Reverse drag rectangle commits");
        Check(PixelHash(rectangle.PngBytes) != PixelHash(stroke.PngBytes), "Rectangle appears in output");

        session.Undo();
        Check(session.Revision == strokeRevision && session.CanRedo, "Undo restores prior revision");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(stroke.PngBytes), "Undo restores exact prior pixels");
        session.Undo();
        Check(session.Revision == originalRevision && !session.CanUndo, "Undo reaches original image");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(source), "Second undo restores original pixels");
        session.Redo(); session.Redo();
        Check(session.Revision == rectangleRevision && !session.CanRedo, "Redo restores committed revision");

        long beforeFailure = session.Revision;
        Expect<ArgumentException>(() => session.OpenImageBytes(new byte[] { 1, 2, 3, 4 }), "Invalid replacement image rejected");
        Expect<ArgumentException>(() => session.OpenImageBytes(Array.Empty<byte>()), "Empty replacement image rejected");
        Expect<ArgumentNullException>(() => session.OpenImageBytes(null!), "Null replacement image rejected");
        Expect<FileNotFoundException>(() => session.Open(Path.Combine(output, "not-present.png")), "Missing replacement file rejected");
        Check(session.Revision == beforeFailure && session.CanUndo, "Open failures preserve revision and history");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(rectangle.PngBytes), "Open failures preserve every rendered pixel");
        Check(source.SequenceEqual(preservedSource), "Input bytes remain unchanged");

        Expect<ArgumentOutOfRangeException>(() => session.AddStroke(new[] { new ImagePoint(double.NaN, 8) }, 0, 3, false), "Nonfinite point rejected");
        Expect<ArgumentOutOfRangeException>(() => session.AddStroke(new[] { new ImagePoint(-1, 8) }, 0, 3, false), "Off-image point rejected");
        Expect<ArgumentOutOfRangeException>(() => session.AddRectangle(new(0, 0), new(10, 10), 0, 0), "Zero width rejected");
        Expect<ArgumentOutOfRangeException>(() => session.AddRectangle(new(0, 0), new(10, 10), 0, float.NaN), "Nonfinite width rejected");
        Check(session.Revision == beforeFailure, "Invalid draw operations create no history state");

        string pngPath = Path.Combine(output, "edited.png");
        Task<SaveSnapshotResult> save = session.SaveAsync(pngPath);
        Check(session.IsSaving && !session.CanUndo && !session.CanRedo, "Save reports active state and suppresses history controls");
        Expect<InvalidOperationException>(() => session.Undo(), "Mutation during save rejected");
        Expect<InvalidOperationException>(() => session.OpenImageBytes(source), "Replacement during save rejected");
        await ExpectAsync<InvalidOperationException>(() => session.SaveAsync(Path.Combine(output, "nested.png")), "Nested save rejected");
        SaveSnapshotResult saved = await save;
        Check(saved.Revision == rectangleRevision && saved.SavedCurrentRevision && saved.Path == pngPath, "Save identifies the published current revision");
        Check(!session.IsSaving && !session.IsDirty && session.CanUndo, "Successful save clears dirty state and restores actions");
        Check(PixelHash(File.ReadAllBytes(pngPath)) == PixelHash(rectangle.PngBytes), "Saved PNG contains exact frozen pixels");
        Check(!File.Exists(Path.Combine(output, "nested.png")), "Nested save writes no file");
        session.Undo(); Check(session.IsDirty, "Undo from saved state is dirty");
        session.Redo(); Check(!session.IsDirty, "Redo to saved revision is clean");

        await ExpectAsync<ArgumentException>(() => session.SaveAsync(Path.Combine(output, "unsupported.txt")), "Unsupported save format rejected");
        Check(!session.IsSaving && !session.IsDirty && !File.Exists(Path.Combine(output, "unsupported.txt")), "Unsupported format preserves state and filesystem");
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => session.SaveAsync(Path.Combine(output, "canceled.png"), canceled.Token), "Pre-canceled save rejected");
            Expect<OperationCanceledException>(() => session.RenderPreviewAsync(canceled.Token), "Pre-canceled preview rejected");
        }
        Check(!session.IsSaving && !File.Exists(Path.Combine(output, "canceled.png")), "Cancellation publishes no image");

        session.AddStroke(new[] { new ImagePoint(4, 40), new ImagePoint(50, 40) }, 0xFF0000FF, 2, false);
        long dirtyRevision = session.Revision;
        byte[] existing = Encoding.UTF8.GetBytes("existing destination must survive failed replacement");
        string lockedPath = Path.Combine(output, "locked.png");
        File.WriteAllBytes(lockedPath, existing);
        using (FileStream locked = new(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await ExpectAsync<IOException>(() => session.SaveAsync(lockedPath), "Locked destination reports failure");
        }
        Check(File.ReadAllBytes(lockedPath).SequenceEqual(existing), "Failed replacement preserves existing destination bytes");
        Check(session.IsDirty && !session.IsSaving && session.Revision == dirtyRevision, "Save failure preserves edits and allows retry");
        SaveSnapshotResult retried = await session.SaveAsync(lockedPath);
        Check(retried.SavedCurrentRevision && !session.IsDirty, "Save retry succeeds without losing edits");
        Check(PixelHash(File.ReadAllBytes(lockedPath)) == PixelHash((await session.RenderPreviewAsync()).PngBytes), "Retry publishes the edited image");

        await ExpectAsync<InvalidOperationException>(() => Task.Run(() => session.Undo()), "Cross-thread document mutation rejected");
        Check(session.Revision == dirtyRevision, "Cross-thread rejection preserves revision");
        await ExpectAsync<InvalidOperationException>(() => Task.Run(() => { using EditorSession wrongThread = new(); }), "MTA construction rejected");

        string fileSource = Path.Combine(output, "open-source.png");
        File.WriteAllBytes(fileSource, source);
        session.Open(fileSource);
        File.Move(fileSource, Path.Combine(output, "open-source-moved.png"));
        Check(session.HasImage && !session.CanUndo && !session.IsDirty, "Opening an existing file resets history and starts clean");
        Check(PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(source), "File source can be renamed after owned copy is taken");

        foreach (string extension in new[] { ".bmp", ".jpg", ".jpeg", ".pdf" })
        {
            string path = Path.Combine(output, "format" + extension);
            SaveSnapshotResult result = await session.SaveAsync(path);
            Check(result.SavedCurrentRevision && File.Exists(path), "Format saved: " + extension);
            if (extension == ".pdf")
            {
                byte[] pdf = File.ReadAllBytes(path);
                Check(Encoding.ASCII.GetString(pdf, 0, 8).StartsWith("%PDF-1.4", StringComparison.Ordinal), "PDF header");
                Check(Encoding.ASCII.GetString(pdf[^32..]).Contains("%%EOF", StringComparison.Ordinal), "PDF has a completed trailer");
                CheckPdfPixels(pdf, source);
            }
            else
            {
                using Bitmap image = new(path);
                Check(image.Width == 64 && image.Height == 48, "Saved dimensions: " + extension);
                if (extension == ".bmp") Check(PixelHash(File.ReadAllBytes(path)) == PixelHash(source), "BMP retains all pixels");
            }
        }

        using (EditorSession transparent = new())
        {
            transparent.OpenImageBytes(CreateImage(transparent: true));
            string jpeg = Path.Combine(output, "transparent.jpg");
            await transparent.SaveAsync(jpeg);
            Color pixel = ReadPixel(File.ReadAllBytes(jpeg), 40, 35);
            Check(pixel.A == 255 && pixel.R >= 245 && pixel.G >= 245 && pixel.B >= 245, "JPEG flattens transparency to white");
        }

        EditorSession disposing = new();
        disposing.OpenImageBytes(source);
        string closeSavePath = Path.Combine(output, "close-during-save.png");
        Task<SaveSnapshotResult> closingSave = disposing.SaveAsync(closeSavePath);
        disposing.Dispose();
        SaveSnapshotResult closedResult = await closingSave;
        Check(!closedResult.SavedCurrentRevision && File.Exists(closeSavePath), "Disposal safely completes the owned worker without marking a closed document");
        Check(PixelHash(File.ReadAllBytes(closeSavePath)) == PixelHash(source), "Disposal does not release an encoder's bitmap early");
        disposing.Dispose();
        Expect<ObjectDisposedException>(() => _ = disposing.HasImage, "Disposed session rejects access");
        Check(!Directory.EnumerateFiles(output, ".snip-*", SearchOption.TopDirectoryOnly).Any(), "All temporary encoding files are cleaned up");
    }

    private static byte[] CreateImage(bool transparent = false)
    {
        using Bitmap image = new(64, 48, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(transparent ? Color.Transparent : Color.White);
        image.SetPixel(2, 2, Color.Blue);
        using MemoryStream output = new();
        image.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static Color ReadPixel(byte[] image, int x, int y)
    {
        using MemoryStream stream = new(image, writable: false);
        using Bitmap bitmap = new(stream);
        return bitmap.GetPixel(x, y);
    }

    private static void CheckPdfPixels(byte[] pdf, byte[] expectedImage)
    {
        // The generated fixture has one image stream. Decode its zlib payload
        // independently of the legacy writer, including checksum verification;
        // this is not a general PDF parser or a native page-rendering check.
        ReadOnlySpan<byte> beginMarker = "stream\n"u8;
        ReadOnlySpan<byte> endMarker = "\nendstream\n"u8;
        int start = pdf.AsSpan().IndexOf(beginMarker);
        Check(start >= 0, "PDF image stream exists");
        start += beginMarker.Length;
        int length = pdf.AsSpan(start).IndexOf(endMarker);
        Check(length > 0, "PDF image stream terminates");
        using MemoryStream input = new(pdf, start, length, writable: false);
        using ZLibStream decoder = new(input, CompressionMode.Decompress);
        using MemoryStream decoded = new();
        decoder.CopyTo(decoded);
        byte[] rgb = decoded.ToArray();
        using MemoryStream imageStream = new(expectedImage, writable: false);
        using Bitmap image = new(imageStream);
        Check(rgb.Length == image.Width * image.Height * 3, "PDF image stream has the correct decoded dimensions");
        bool equal = true;
        int position = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                Color pixel = image.GetPixel(x, y);
                equal &= rgb[position++] == pixel.R;
                equal &= rgb[position++] == pixel.G;
                equal &= rgb[position++] == pixel.B;
            }
        Check(equal, "PDF image payload preserves every source RGB pixel");
    }

    private static string PixelHash(byte[] image)
    {
        using MemoryStream stream = new(image, writable: false);
        using Bitmap bitmap = new(stream);
        byte[] pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        int index = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                uint argb = unchecked((uint)bitmap.GetPixel(x, y).ToArgb());
                pixels[index++] = (byte)(argb >> 24);
                pixels[index++] = (byte)(argb >> 16);
                pixels[index++] = (byte)(argb >> 8);
                pixels[index++] = (byte)argb;
            }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + message);
        assertions++;
    }

    private static void Expect<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { Check(true, message); return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + message);
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { Check(true, message); return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ": " + message);
    }

    private sealed class StaContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));

        internal void Run(Task task)
        {
            while (!task.IsCompleted)
                if (queue.TryTake(out var work, 100)) work.Callback(work.State);
            task.GetAwaiter().GetResult();
        }

        public void Dispose() => queue.Dispose();
    }
}
