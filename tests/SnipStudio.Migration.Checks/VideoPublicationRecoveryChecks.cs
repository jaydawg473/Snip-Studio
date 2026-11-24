using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckVideoPublicationRecoveryAsync(string source, VideoMetadata metadata, long sourceBytes,
        string output, VideoSmokeEvidence observed, CancellationToken budget)
    {
        await VideoSmokeStepAsync("Match the displaced local video to its continuously write-denied original handle", () =>
        {
            string destination = Path.Combine(output, "identity-destination.mp4");
            string staged = Path.Combine(output, "identity-staged.mp4");
            string backup = Path.Combine(output, "identity-backup.mp4");
            byte[] original = [41, 42, 43];
            byte[] replacement = [61, 62, 63];
            File.WriteAllBytes(destination, original);
            File.WriteAllBytes(staged, replacement);
            using var approved = File.OpenHandle(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            File.Replace(staged, destination, backup);
            Check(File.ReadAllBytes(destination).SequenceEqual(replacement) && File.ReadAllBytes(backup).SequenceEqual(original),
                "The native replace works with the old file held and retains its exact bytes in the backup");
            Check(!AtomicImageWriter.IsDisplacedFileStillHeld(destination, approved),
                "The published replacement's distinct file object cannot stand in for the displaced one");
            string? root = Path.GetPathRoot(destination);
            if (root is { Length: 3 } && root[1] == ':' && new DriveInfo(root) is
                { DriveType: DriveType.Fixed, DriveFormat: "NTFS" })
            {
                Check(AtomicImageWriter.IsDisplacedFileStillHeld(backup, approved),
                    "The mounted local NTFS recovery path identifies the same file object as its continuously held original");
                string namedStream = destination + ":identity-check";
                File.WriteAllBytes(namedStream, original);
                using var heldStream = File.OpenHandle(namedStream, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                Check(!AtomicImageWriter.IsDisplacedFileStillHeld(destination, heldStream) &&
                    !AtomicImageWriter.IsDisplacedFileStillHeld(namedStream, heldStream),
                    "An alternate NTFS data stream cannot prove the default byte stream stayed write-denied");
            }
            bool writeDenied = false;
            try
            {
                using var writer = File.OpenHandle(backup, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) { writeDenied = true; }
            Check(writeDenied, "An independent writer cannot change the displaced bytes during identity validation");
            return Task.CompletedTask;
        }, observed, budget).ConfigureAwait(false);

        var plan = VideoTrimService.CreateExportPlan(metadata, sourceBytes, VideoExportPreset.Small480p, 1);
        byte[] competingBytes = [11, 28, 45, 62];
        await VideoSmokeStepAsync("Keep an encoded MP4 after a publication conflict followed by cancellation", async () =>
        {
            string destination = Path.Combine(output, "export-competing-destination.mp4");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(budget);
            string? stage = null, stageHash = null;
            IOException? conflict = null;
            RecordingSaveException? failure = null;
            try
            {
                await VideoTrimService.ExportWithPreparedDestinationAsync(source, destination, plan, cancel.Token, null,
                    enforceTargetLimit: true, (path, token) =>
                    {
                        ImageSaveTarget target = AtomicImageWriter.PrepareDestination(path, token);
                        return prepared =>
                        {
                            stage = prepared;
                            stageHash = VideoSmokeHash(prepared);
                            using (var competitor = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                competitor.Write(competingBytes);
                            try { AtomicImageWriter.PublishCopy(prepared, target, token); }
                            catch (IOException error)
                            {
                                conflict = error;
                                cancel.Cancel();
                                throw;
                            }
                        };
                    }).ConfigureAwait(false);
            }
            catch (RecordingSaveException error) { failure = error; }

            Check(conflict is not null && cancel.IsCancellationRequested && ReferenceEquals(failure?.InnerException, conflict),
                "A real destination conflict remains a save failure even if cancellation is requested before cleanup");
            Check(stage is not null && stage == failure?.RecoveryPath && File.Exists(stage) && VideoSmokeHash(stage) == stageHash,
                "The reported recovery path retains the exact completed MP4 bytes");
            Check(File.ReadAllBytes(destination).SequenceEqual(competingBytes),
                "The file that appeared during encoding retains its exact bytes");
            VideoMetadata recovered = await VideoReviewService.InspectAsync(stage!, budget).ConfigureAwait(false);
            Check(recovered.Duration > TimeSpan.Zero && recovered.Width == plan.Width && recovered.Height == plan.Height,
                "Windows decodes the recovered MP4 at the configured output size");
            RecordVideoSmokeFile(stage!, "Completed video retained after publication failure and asynchronous cancellation", observed);
        }, observed, budget).ConfigureAwait(false);

        await VideoSmokeStepAsync("Cancel final video publication without a conflict", async () =>
        {
            string destination = Path.Combine(output, "export-canceled-at-publication.mp4");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(budget);
            string? stage = null;
            await ExpectAsync<OperationCanceledException>(() => VideoTrimService.ExportWithPreparedDestinationAsync(
                source, destination, plan, cancel.Token, null, enforceTargetLimit: true, (path, token) =>
                {
                    ImageSaveTarget target = AtomicImageWriter.PrepareDestination(path, token);
                    return prepared =>
                    {
                        stage = prepared;
                        cancel.Cancel();
                        AtomicImageWriter.PublishCopy(prepared, target, token);
                    };
                }), "Cancellation before any write remains ordinary cancellation").ConfigureAwait(false);
            Check(stage is not null && !File.Exists(stage) && !File.Exists(destination),
                "A canceled publication removes the staged MP4 and publishes nothing");
        }, observed, budget).ConfigureAwait(false);
    }
}
