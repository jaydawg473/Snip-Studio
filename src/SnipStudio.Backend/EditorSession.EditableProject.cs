#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Document = SnipStudio.EditorDocument;

namespace SnipStudio.WinUI.Backend;

public sealed partial class EditorSession
{
    public const string EditableProjectExtension = EditableProjectFile.Extension;
    private bool isProjectOpenPending;
    private ImageSaveTarget? editableProjectDestination;

    public async Task OpenEditableProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        VerifyCanMutate();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProjectUiContext();
        ValidateProjectPath(path);
        if (isProjectOpenPending || Volatile.Read(ref openWorker) is not null)
            throw new InvalidOperationException("Wait for the current file to open before choosing another file.");

        long revision = document.Revision;
        object? identity = document.BaseIdentity;
        isProjectOpenPending = true;
        try
        {
            var opened = await ReadProjectOnStaAsync(Path.GetFullPath(path), cancellationToken);
            using Document.EditableSnapshot snapshot = opened.Snapshot;
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCanMutate();
            if (document.Revision != revision || !ReferenceEquals(document.BaseIdentity, identity))
                throw new InvalidOperationException("The document changed while the project was opening. Your current snip is kept.");
            document.OpenEditableSnapshot(snapshot);
            editableProjectDestination = opened.Target;
        }
        finally { isProjectOpenPending = false; }
    }

    public ImageSaveTarget PrepareEditableProjectSaveTarget(string path, bool replacementConfirmed = false)
    {
        ImageSaveTarget? current = FindEditableProjectSaveTarget(path, replacementConfirmed, out string fullPath);
        return current ?? PrepareProjectDestination(fullPath, replacementConfirmed, CancellationToken.None);
    }

    public async Task<ImageSaveTarget> PrepareEditableProjectSaveTargetAsync(string path, bool replacementConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        ImageSaveTarget? current = FindEditableProjectSaveTarget(path, replacementConfirmed, out string fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (current is not null) return current;

        EnsureProjectUiContext();
        long revision = document.Revision;
        object? identity = document.BaseIdentity;
        ImageSaveTarget target = await Task.Run(
            () => PrepareProjectDestination(fullPath, replacementConfirmed, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCanMutate();
        if (document.Revision != revision || !ReferenceEquals(document.BaseIdentity, identity))
            throw new InvalidOperationException("The snip changed while its save destination was being checked. Save again.");
        return target;
    }

    private ImageSaveTarget? FindEditableProjectSaveTarget(string path, bool replacementConfirmed, out string fullPath)
    {
        VerifyCanMutate();
        ValidateProjectPath(path);
        fullPath = Path.GetFullPath(path);
        return !replacementConfirmed && editableProjectDestination is { } current &&
            string.Equals(current.Path, fullPath, StringComparison.OrdinalIgnoreCase) ? current : null;
    }

    private static ImageSaveTarget PrepareProjectDestination(string path, bool replacementConfirmed,
        CancellationToken cancellationToken)
    {
        ImageSaveTarget target = AtomicImageWriter.PrepareDestination(path, cancellationToken);
        if (!replacementConfirmed && target.Previous is not null)
            throw new IOException("Choose Save editable snip and confirm replacing that existing file.");
        return target;
    }

    public async Task<SaveSnapshotResult> SaveEditableProjectAsync(ImageSaveTarget target,
        CancellationToken cancellationToken = default)
    {
        VerifyCanMutate();
        RequireImage();
        ArgumentNullException.ThrowIfNull(target);
        ValidateProjectPath(target.Path);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProjectUiContext();
        long revision = document.Revision;
        object identity = document.BaseIdentity;
        Document.EditableSnapshot snapshot = document.CaptureEditableSnapshot();
        Task<ImageSaveTarget> worker;
        isSaving = true;
        try
        {
            worker = Task.Run(() =>
            {
                using (snapshot)
                    return AtomicImageWriter.WriteStaged(target, stream => EditableProjectFile.Save(stream, snapshot, cancellationToken),
                        cancellationToken);
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
            ImageSaveTarget published = await worker;
            VerifyOwner();
            bool current = !disposed && document.HasImage && document.Revision == revision &&
                ReferenceEquals(document.BaseIdentity, identity);
            if (current)
            {
                document.MarkSaved();
                editableProjectDestination = published;
            }
            return new SaveSnapshotResult(revision, current, target.Path) { SavedTarget = published };
        }
        finally
        {
            isSaving = false;
            if (ReferenceEquals(saveWorker, worker)) saveWorker = null;
        }
    }

    private static Task<(Document.EditableSnapshot Snapshot, ImageSaveTarget Target)> ReadProjectOnStaAsync(
        string path, CancellationToken cancellationToken)
    {
        var read = new TaskCompletionSource<(Document.EditableSnapshot, ImageSaveTarget)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            Document.EditableSnapshot? prepared = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageSaveTarget target;
                try
                {
                    // Keep writers and renames out across decoding and stamping.
                    // Decode first so oversized or invalid input is rejected
                    // without a second read.
                    using FileStream guard = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    prepared = EditableProjectFile.Read(path, cancellationToken);
                    target = AtomicImageWriter.PrepareDestination(path, cancellationToken);
                    if (target.Previous is null)
                        throw new IOException("The editable file changed while it was opening. Choose it again.");
                }
                finally { global::SnipStudio.RichTextLayout.ReleaseProjectThreadEditor(); }
                read.SetResult((prepared, target));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                prepared?.Dispose();
                read.SetCanceled(cancellationToken);
            }
            catch (Exception failure)
            {
                prepared?.Dispose();
                read.SetException(failure);
            }
        })
        {
            IsBackground = true,
            Name = "Snip Studio editable file reader"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return read.Task;
    }

    private static void EnsureProjectUiContext()
    {
        if (SynchronizationContext.Current is null)
            throw new InvalidOperationException("Open or save an editable snip from the application's UI context.");
    }

    private static void ValidateProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), EditableProjectExtension,
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .snipstudio editable document.", nameof(path));
    }
}
