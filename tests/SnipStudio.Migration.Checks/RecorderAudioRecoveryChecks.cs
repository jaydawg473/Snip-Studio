using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static async Task CheckRecorderAudioRecoveryAsync(string output, VideoSmokeEvidence observed, CancellationToken budget)
    {
        Rectangle desktop = SystemInformation.VirtualScreen;
        Rectangle region = new(desktop.Left, desktop.Top, Math.Min(64, desktop.Width), Math.Min(64, desktop.Height));
        var options = new ScreenRecordingOptions { RecordMicrophone = true, RecordSystemAudio = false, IncludeCursor = false };

        foreach (bool failDuringDrain in new[] { false, true })
        {
            string scenario = failDuringDrain ? "during final drain" : "during active capture";
            string destination = Path.Combine(output, failDuringDrain ? "audio-interrupted-at-stop.mp4" : "audio-interrupted-while-capturing.mp4");
            await VideoSmokeStepAsync("Save synthetic recording with audio interrupted " + scenario, async () =>
            {
                var audio = new SyntheticRecordingAudio(failOnStop: failDuringDrain);
                var generated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int frames = 0;
                using var recorder = new ScreenRecorder((bitmap, _, _) =>
                {
                    using Graphics graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(Color.CornflowerBlue);
                    frames++;
                    if (frames == 3 && !failDuringDrain) audio.Interrupt();
                    if (frames == 7) generated.TrySetResult();
                }, thread => thread.Start(), _ => audio);
                try
                {
                    await recorder.StartAsync(region, destination, options).WaitAsync(TimeSpan.FromSeconds(8), budget).ConfigureAwait(false);
                    await generated.Task.WaitAsync(TimeSpan.FromSeconds(5), budget).ConfigureAwait(false);
                }
                finally
                {
                    await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), budget).ConfigureAwait(false);
                }

                Check(recorder.Error is null && recorder.AudioWarning?.Contains("Synthetic audio endpoint interrupted", StringComparison.Ordinal) == true,
                    "The recording reports a partial-audio warning without a save failure");
                Check(audio.ReadsAfterInterruption > 0, "Buffered audio is read after the interruption");
                var metadata = await VideoReviewService.InspectAsync(destination, budget).ConfigureAwait(false);
                Check(metadata.HasAudio && metadata.Duration >= TimeSpan.FromMilliseconds(250),
                    "Windows decodes the finalized MP4 with the original video interval and an audio stream");
                RecordVideoSmokeFile(destination, "Synthetic screen frames and audio with injected interruption " + scenario, observed);
            }, observed, budget).ConfigureAwait(false);
        }

        await VideoSmokeStepAsync("Reject synthetic audio startup failure without publishing a file", async () =>
        {
            string destination = Path.Combine(output, "audio-never-started.mp4");
            var audio = new SyntheticRecordingAudio(failOnStart: true);
            int frames = 0;
            using var recorder = new ScreenRecorder((_, _, _) => frames++, thread => thread.Start(), _ => audio);
            await ExpectAsync<InvalidOperationException>(() => recorder.StartAsync(region, destination, options).WaitAsync(TimeSpan.FromSeconds(8), budget),
                "Audio start failure remains a hard failure").ConfigureAwait(false);
            await ExpectAsync<InvalidOperationException>(() => recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(8), budget),
                "Finishing propagates the startup failure").ConfigureAwait(false);
            Check(frames == 0 && !File.Exists(destination) && !Directory.EnumerateFiles(output, ".snip-recording-*").Any(),
                "Failed audio startup publishes no frames or output and cleans the staging file");
        }, observed, budget).ConfigureAwait(false);

        await CheckRecordingDestinationAsync(region, output, observed, budget).ConfigureAwait(false);
    }

    private static async Task CheckRecordingDestinationAsync(Rectangle rectangle, string output, VideoSmokeEvidence observed,
        CancellationToken budget)
    {
        var region = new RecordingRegion(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        var options = new ScreenRecordingOptions { RecordMicrophone = false, RecordSystemAudio = false, IncludeCursor = false };

        foreach (var scenario in new[] { (Name: "late-file", Existing: false, Conflict: true),
            (Name: "changed-existing", Existing: true, Conflict: true), (Name: "approved-existing", Existing: true, Conflict: false) })
        {
            await VideoSmokeStepAsync("Finalize a synthetic recording with destination " + scenario.Name, async () =>
            {
                string destination = Path.Combine(output, "recording-destination-" + scenario.Name + ".mp4");
                const string previousBytes = "synthetic-file-A";
                const string competingBytes = "synthetic-file-B";
                if (scenario.Existing) File.WriteAllText(destination, previousBytes);
                DateTime written = scenario.Existing ? File.GetLastWriteTimeUtc(destination) : default;
                ImageSaveTarget target = await RecordingController.PrepareSaveTargetAsync(destination, scenario.Existing, budget).ConfigureAwait(false);
                var generated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int frames = 0;
                using var recorder = new ScreenRecorder((bitmap, _, _) =>
                {
                    using Graphics graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(Color.CornflowerBlue);
                    if (++frames == 3) generated.TrySetResult();
                });
                RecordingSaveException? saveFailure = null;
                try
                {
                    await RecordingController.StartCoreAsync(recorder, region, destination, options, target, budget)
                        .WaitAsync(TimeSpan.FromSeconds(8), budget).ConfigureAwait(false);
                    await generated.Task.WaitAsync(TimeSpan.FromSeconds(5), budget).ConfigureAwait(false);
                    if (scenario.Conflict)
                    {
                        File.WriteAllText(destination, competingBytes);
                        if (scenario.Existing) File.SetLastWriteTimeUtc(destination, written);
                    }
                }
                finally
                {
                    try { await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), budget).ConfigureAwait(false); }
                    catch (RecordingSaveException failure) { saveFailure = failure; }
                }

                string finishedPath = destination;
                if (scenario.Conflict)
                {
                    Check(saveFailure?.RecoveryPath is { } recovery && File.Exists(recovery) && recovery == recorder.RecoveryPath,
                        "A conflicting file leaves a recoverable MP4 with an accurate path");
                    Check(File.ReadAllText(destination) == competingBytes, "The competing file retains its exact bytes");
                    finishedPath = saveFailure!.RecoveryPath;
                }
                else
                {
                    Check(saveFailure is null && recorder.RecoveryPath is null, "An unchanged explicitly approved file can be replaced");
                }
                var metadata = await VideoReviewService.InspectAsync(finishedPath, budget).ConfigureAwait(false);
                Check(!metadata.HasAudio && metadata.Duration > TimeSpan.Zero, "Windows decodes the completed synthetic MP4");
                RecordVideoSmokeFile(finishedPath, "Synthetic recording, " + scenario.Name, observed);
            }, observed, budget).ConfigureAwait(false);
        }
    }

    private sealed class SyntheticRecordingAudio(bool failOnStop = false, bool failOnStart = false) : IRecordingAudio
    {
        private readonly Stopwatch duration = new();
        public Exception? Error { get; private set; }
        public string Status => "Synthetic audio running";
        public TimeSpan Duration => duration.Elapsed;
        public int ReadsAfterInterruption { get; private set; }

        public void Start()
        {
            if (failOnStart)
            {
                Interrupt();
                throw Error!;
            }
            duration.Start();
        }

        public void Stop()
        {
            duration.Stop();
            if (failOnStop) Interrupt();
        }

        public void Interrupt() => Error ??= new InvalidOperationException("Synthetic audio endpoint interrupted");

        public byte[] ReadFrames(int frameCount)
        {
            if (Error != null) ReadsAfterInterruption++;
            return new byte[checked(frameCount * 4)];
        }

        public void Dispose() => duration.Stop();
    }
}
