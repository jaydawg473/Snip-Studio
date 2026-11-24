using System.Drawing;
using System.Drawing.Imaging;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckBoundImageOpenAndFinalPublicationAsync(string output)
    {
        string folder = Path.Combine(output, "bound-image-save");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.PNG");
        byte[] first = SolidFixture(Color.Navy);
        byte[] second = SolidFixture(Color.ForestGreen);
        File.WriteAllBytes(source, first);
        var firstCreation = File.GetCreationTimeUtc(source);
        var firstModification = File.GetLastWriteTimeUtc(source);
        ImageSaveTarget oldApproval = await EditorSession.PrepareSaveTargetAsync(source);

        // Open the intervening revision. Returning its approval from the same
        // source handle prevents an earlier path snapshot from authorizing A.
        File.WriteAllBytes(source, second);
        using var session = new EditorSession();
        ImageSaveTarget? bound = await session.OpenWithSaveTargetAsync(source);
        Check(bound is not null && bound.Path == Path.GetFullPath(source) &&
            bound.Previous != oldApproval.Previous && !session.IsDirty &&
            Pixel((await session.RenderPreviewAsync()).PngBytes, 20, 20) == unchecked((uint)Color.ForestGreen.ToArgb()),
            "Opened pixels, clean editor state, and in-place approval identify the same revision");
        session.AddRectangle(new(1, 1), new(7, 8), 0xFFFF0000, 1);
        long editedRevision = session.Revision;
        byte[] edited = (await session.RenderPreviewAsync()).PngBytes;

        File.WriteAllBytes(source, first);
        File.SetCreationTimeUtc(source, firstCreation);
        File.SetLastWriteTimeUtc(source, firstModification);
        try
        {
            await session.SavePreparedAsync(bound!);
            Check(false, "Restoring A cannot authorize writing edits made to B");
        }
        catch (IOException)
        {
            Check(File.ReadAllBytes(source).SequenceEqual(first) && session.Revision == editedRevision &&
                session.IsDirty && session.CanUndo && !session.IsSaving &&
                PixelHash((await session.RenderPreviewAsync()).PngBytes) == PixelHash(edited),
                "The original file and edited copy both survive an intervening path revision");
        }

        File.WriteAllBytes(source, second);
        ImageSaveTarget? current = await session.OpenWithSaveTargetAsync(source);
        session.AddRectangle(new(1, 1), new(7, 8), 0xFFFF0000, 1);
        byte[] intended = (await session.RenderPreviewAsync()).PngBytes;
        SaveSnapshotResult saved = await session.SavePreparedAsync(current!);
        Check(saved.SavedCurrentRevision && !session.IsDirty && saved.SavedTarget?.Previous is not null &&
            PixelHash(File.ReadAllBytes(source)) == PixelHash(intended),
            "A freshly opened and unchanged approval can save exact edited pixels");
        Check(Directory.GetFiles(folder).Length == 1, "Conflict and successful retry leave no staging residue");

        string copyOnly = Path.Combine(folder, "open-only.gif");
        File.WriteAllBytes(copyOnly, second);
        Check(await session.OpenWithSaveTargetAsync(copyOnly) is null && session.HasImage && !session.IsDirty,
            "An import-only extension still opens and requires a separate save destination");

        string staged = Path.Combine(folder, "audited-staged.png");
        string final = Path.Combine(folder, "audited-destination.png");
        string recovery = Path.Combine(folder, "audited-recovery.png");
        File.WriteAllBytes(staged, [11, 22, 33, 44]);
        File.WriteAllBytes(final, [12, 23, 34, 45]);
        File.WriteAllBytes(recovery, first);
        ImageDestinationStamp stagedStamp = AtomicImageWriter.PrepareDestination(staged).Previous!.Value;
        try
        {
            AtomicImageWriter.VerifyPublishedImage(final, stagedStamp, recovery);
            Check(false, "Same-length mismatched output must not be accepted");
        }
        catch (IOException failure)
        {
            Check(failure.Message.Contains(recovery, StringComparison.Ordinal) &&
                File.ReadAllBytes(final).SequenceEqual(new byte[] { 12, 23, 34, 45 }) &&
                File.ReadAllBytes(recovery).SequenceEqual(first),
                "Wrong same-length bytes decline success and retain both the actual output and prior revision");
        }

        File.Copy(staged, final, overwrite: true);
        ImageDestinationStamp actual = AtomicImageWriter.VerifyPublishedImage(final, stagedStamp, recovery);
        Check(actual == AtomicImageWriter.PrepareDestination(final).Previous,
            "Readable output returns its exact observed bytes and filesystem stamp");
        using FileStream locked = new(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            AtomicImageWriter.VerifyPublishedImage(final, stagedStamp, recovery);
            Check(false, "Unreadable published bytes must not be accepted");
        }
        catch (IOException failure)
        {
            Check(failure.InnerException is IOException && failure.Message.Contains(recovery, StringComparison.Ordinal) &&
                File.ReadAllBytes(recovery).SequenceEqual(first),
                "Readback lock reports failure with a usable recovery path");
        }

        static byte[] SolidFixture(Color color)
        {
            using var bitmap = new Bitmap(24, 24);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }
}

