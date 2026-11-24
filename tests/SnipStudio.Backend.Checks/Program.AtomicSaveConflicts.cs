using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckAtomicSaveConflictsAsync(string output)
    {
        string folder = Path.Combine(output, "atomic-save-conflicts");
        Directory.CreateDirectory(folder);
        string imagePath = Path.Combine(folder, "existing.png");
        File.WriteAllBytes(imagePath, "previous revision A"u8.ToArray());

        using EditorSession image = new();
        image.OpenImageBytes(CreateImage());
        image.AddRectangle(new(2, 2), new(31, 22), 0xFF663333, 3);
        long revision = image.Revision;
        ImageSaveTarget original = await EditorSession.PrepareSaveTargetAsync(imagePath);
        byte[] competing = ChangeBytesWithoutChangingStamp(imagePath);
        await ExpectAsync<IOException>(() => image.SavePreparedAsync(original),
            "A same-size image edit with the original timestamps invalidates an earlier replacement");
        Check(File.ReadAllBytes(imagePath).SequenceEqual(competing) && image.IsDirty && image.CanUndo &&
            image.Revision == revision && !image.IsSaving, "The conflict preserves the competing bytes and the active edits");
        Check(Directory.GetFiles(folder).Length == 1, "An early save conflict leaves no staging or recovery file");

        ImageSaveTarget confirmed = await EditorSession.PrepareSaveTargetAsync(imagePath);
        SaveSnapshotResult saved = await image.SavePreparedAsync(confirmed);
        Check(saved.SavedCurrentRevision && saved.SavedTarget is not null && !image.IsDirty &&
            PixelHash(File.ReadAllBytes(imagePath)) == PixelHash((await image.RenderPreviewAsync()).PngBytes),
            "Reselecting the destination saves the complete image and supplies its new replacement target");

        image.AddRectangle(new(12, 12), new(18, 23), 0xFF443366, 1);
        saved = await image.SavePreparedAsync(saved.SavedTarget!);
        Check(saved.SavedCurrentRevision && saved.SavedTarget is not null && !image.IsDirty &&
            Directory.GetFiles(folder).Length == 1, "A subsequent unchanged destination can be saved in place");

        image.AddRectangle(new(3, 10), new(19, 18), 0xFF553344, 2);
        competing = ChangeBytesWithoutChangingStamp(imagePath);
        await ExpectAsync<IOException>(() => image.SavePreparedAsync(saved.SavedTarget!),
            "The target returned by a successful save rejects later changes with equivalent metadata");
        using (FileStream locked = new(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await ExpectAsync<IOException>(() => EditorSession.PrepareSaveTargetAsync(imagePath),
                "An unreadable existing destination is never treated as a new filename");
        FileAttributes originalAttributes = File.GetAttributes(imagePath);
        File.SetAttributes(imagePath, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            ImageSaveTarget readOnlyTarget = await EditorSession.PrepareSaveTargetAsync(imagePath);
            await ExpectAsync<UnauthorizedAccessException>(() => image.SavePreparedAsync(readOnlyTarget),
                "Checking the contents of a read-only target does not authorize replacing it");
        }
        finally { File.SetAttributes(imagePath, originalAttributes); }
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => EditorSession.PrepareSaveTargetAsync(imagePath, canceled.Token),
                "A pre-canceled fingerprint does not open or replace the destination");
        }
        Check(File.ReadAllBytes(imagePath).SequenceEqual(competing) && image.IsDirty,
            "A locked or read-only target or canceled preparation keeps the newer image and the current edits");

        string projectPath = Path.Combine(folder, "existing" + EditorSession.EditableProjectExtension);
        ImageSaveTarget projectTarget = await image.PrepareEditableProjectSaveTargetAsync(projectPath);
        await image.SaveEditableProjectAsync(projectTarget);

        using EditorSession project = new();
        await project.OpenEditableProjectAsync(projectPath);
        project.AddRectangle(new(1, 1), new(11, 13), 0xFF117799, 2);
        ImageSaveTarget openedTarget = await project.PrepareEditableProjectSaveTargetAsync(projectPath);
        competing = ChangeBytesWithoutChangingStamp(projectPath);
        await ExpectAsync<IOException>(() => project.SaveEditableProjectAsync(openedTarget),
            "A reopened editable project detects a same-metadata external revision change");
        Check(File.ReadAllBytes(projectPath).SequenceEqual(competing) && project.IsDirty && project.CanUndo,
            "The conflicting editable project and active annotation history remain intact");
    }

    private static byte[] ChangeBytesWithoutChangingStamp(string path)
    {
        DateTime created = File.GetCreationTimeUtc(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
        FileInfo current = new(path);
        Check(current.Length == bytes.Length && current.CreationTimeUtc == created && current.LastWriteTimeUtc == written,
            "The competing fixture write preserves all three original metadata checks");
        return bytes;
    }
}
