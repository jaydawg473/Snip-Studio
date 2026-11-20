#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

/// <summary>
/// Adapts the existing document to a WinUI-owned STA. No application startup,
/// hotkey, tray, update, clipboard or capture integration is activated here.
/// </summary>
public sealed partial class EditorSession : IDisposable
{
    private const int MaximumImageDimension = 32768;
    private const long MaximumImagePixels = 64L * 1024 * 1024;
    private const int MaximumStrokePoints = 100000;
    private readonly int ownerThreadId;
    private readonly global::SnipStudio.EditorDocument document = new();
    private Task? saveWorker;
    private bool isSaving;
    private bool disposed;

    public EditorSession()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Create the editor on the application's STA UI thread.");
        ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public bool HasImage { get { VerifyAccess(); return document.HasImage; } }
    public bool CanUndo { get { VerifyAccess(); return document.CanUndo && !isSaving; } }
    public bool CanRedo { get { VerifyAccess(); return document.CanRedo && !isSaving; } }
    public bool IsDirty { get { VerifyAccess(); return document.IsDirty; } }
    public bool IsSaving { get { VerifyAccess(); return isSaving; } }
    public int ImageWidth { get { VerifyAccess(); return document.ImageSize.Width; } }
    public int ImageHeight { get { VerifyAccess(); return document.ImageSize.Height; } }
    public long Revision { get { VerifyAccess(); return document.Revision; } }

    public readonly record struct CaptureBaseline(object ImageIdentity, long Revision);

    public CaptureBaseline GetCaptureBaseline()
    {
        VerifyAccess();
        RequireImage();
        return new(document.BaseIdentity, document.Revision);
    }

    // A saved original still belongs to this snip after annotation edits, but
    // only the exact exported revision may be marked clean.
    public bool TryRecordSavedCapture(CaptureBaseline baseline)
    {
        VerifyAccess();
        if (!document.HasImage || !ReferenceEquals(document.BaseIdentity, baseline.ImageIdentity)) return false;
        if (document.Revision == baseline.Revision) document.MarkSaved();
        return true;
    }

    public void Open(string path)
    {
        VerifyCanMutate();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose an image to open.", nameof(path));
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Bitmap decoded = new(input);
        ValidateImage(decoded);
        // SetImage validates and copies before releasing any current history.
        document.SetImage(decoded);
        // Opening an existing file starts clean, matching the released editor.
        // Clipboard/image bytes remain an unsaved new document.
        document.MarkSaved();
    }

    public void OpenImageBytes(byte[] bytes)
    {
        VerifyCanMutate();
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("The image contains no data.", nameof(bytes));
        using MemoryStream input = new(bytes, writable: false);
        using Bitmap decoded = new(input);
        ValidateImage(decoded);
        document.SetImage(decoded);
    }

    public void AddStroke(IReadOnlyList<ImagePoint> points, uint argb, float width, bool highlighter)
    {
        VerifyCanMutate();
        RequireImage();
        ArgumentNullException.ThrowIfNull(points);
        ValidateWidth(width);
        if (points.Count > MaximumStrokePoints)
            throw new ArgumentException("A single stroke contains too many points. Finish it and start another stroke.", nameof(points));
        PointF[] copy = new PointF[points.Count];
        for (int index = 0; index < copy.Length; index++) copy[index] = ConvertPoint(points[index], nameof(points));
        document.AddStroke(copy, Color.FromArgb(unchecked((int)argb)), width, highlighter);
    }

    public void AddRectangle(ImagePoint start, ImagePoint end, uint argb, float width)
    {
        VerifyCanMutate();
        RequireImage();
        ValidateWidth(width);
        PointF first = ConvertPoint(start, nameof(start)), last = ConvertPoint(end, nameof(end));
        document.AddRectangle(RectangleF.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
            Math.Max(first.X, last.X), Math.Max(first.Y, last.Y)), Color.FromArgb(unchecked((int)argb)), width);
    }

    public void Undo() { VerifyCanMutate(); document.Undo(); }
    public void Redo() { VerifyCanMutate(); document.Redo(); }

    public Task<PreviewSnapshot> RenderPreviewAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        RequireImage();
        cancellationToken.ThrowIfCancellationRequested();
        long revision = document.Revision;
        Bitmap snapshot = document.RenderImage();
        try
        {
            return Task.Run(() =>
            {
                using (snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using MemoryStream output = new();
                    snapshot.Save(output, ImageFormat.Png);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new PreviewSnapshot(revision, output.ToArray());
                }
            });
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    public async Task<SaveSnapshotResult> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        VerifyCanMutate();
        RequireImage();
        cancellationToken.ThrowIfCancellationRequested();
        if (SynchronizationContext.Current == null)
            throw new InvalidOperationException("Save from the application's UI context so document state can complete safely.");
        string destination = AtomicImageWriter.ValidatePath(path);
        long revision = document.Revision;
        object identity = document.BaseIdentity;
        Bitmap snapshot = document.RenderImage();
        Task worker;
        isSaving = true;
        try
        {
            // Do not pass the token to Task.Run: the worker must start so it can
            // dispose its snapshot even when the token is already canceled.
            worker = Task.Run(() =>
            {
                using (snapshot) AtomicImageWriter.Save(snapshot, destination, cancellationToken);
            });
            saveWorker = worker;
        }
        catch
        {
            isSaving = false;
            snapshot.Dispose();
            throw;
        }

        try
        {
            await worker;
            VerifyOwner();
            bool current = !disposed && document.HasImage && document.Revision == revision &&
                ReferenceEquals(document.BaseIdentity, identity);
            if (current) document.MarkSaved();
            return new SaveSnapshotResult(revision, current, destination);
        }
        finally
        {
            isSaving = false;
            if (ReferenceEquals(saveWorker, worker)) saveWorker = null;
        }
    }

    public void Dispose()
    {
        VerifyOwner();
        if (disposed) return;
        try
        {
            // Await the pure encoder, never its UI continuation. The worker owns
            // its detached bitmap and does not need the document/UI to finish.
            if (saveWorker != null)
            {
                try { saveWorker.GetAwaiter().GetResult(); }
                catch { /* The SaveAsync task retains the original save failure. */ }
            }
        }
        finally
        {
            disposed = true;
            isSaving = false;
            document.Dispose();
            global::SnipStudio.RichTextLayout.ClearCache();
        }
    }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("Access the editor from its owning UI thread.");
    }

    private void VerifyAccess()
    {
        VerifyOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void VerifyCanMutate()
    {
        VerifyAccess();
        if (isSaving) throw new InvalidOperationException("Wait for the current image save to finish.");
    }

    private void RequireImage()
    {
        if (!document.HasImage) throw new InvalidOperationException("Open or paste an image first.");
    }

    private PointF ConvertPoint(ImagePoint point, string name)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 ||
            point.X > document.ImageSize.Width || point.Y > document.ImageSize.Height)
            throw new ArgumentOutOfRangeException(name, "Choose a point inside the source image.");
        return new PointF((float)point.X, (float)point.Y);
    }

    private static void ValidateWidth(float width)
    {
        if (!float.IsFinite(width) || width < 1 || width > 100)
            throw new ArgumentOutOfRangeException(nameof(width), "Choose a stroke width from 1 to 100 pixels.");
    }

    private static void ValidateImage(Bitmap image)
    {
        if (image.Width < 1 || image.Height < 1 || image.Width > MaximumImageDimension || image.Height > MaximumImageDimension ||
            (long)image.Width * image.Height > MaximumImagePixels)
            throw new ArgumentException("This migration preview supports images up to 32,768 pixels per side and 64 million pixels in total.");
    }
}
