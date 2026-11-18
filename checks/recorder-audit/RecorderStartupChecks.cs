using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SnipStudio;

// Sign this check executable before running it. It draws synthetic frames and
// does not read screen pixels, microphones, loopback audio or user recordings.
internal static class RecorderStartupChecks
{
    private static int assertions;
    private static void Check(bool passed, string label)
    { if (!passed) throw new InvalidOperationException(label); assertions++; }

    private static void SyntheticFrame(Bitmap bitmap, Rectangle region, bool cursor)
    { using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue); }

    private static void Complete(Task task, string label)
    {
        Task winner = Task.WhenAny(task, Task.Delay(10000)).GetAwaiter().GetResult();
        Check(Object.ReferenceEquals(winner, task), label + " finishes within ten seconds");
        task.GetAwaiter().GetResult();
    }

    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            string directory = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(directory);
            Rectangle desktop = SystemInformation.VirtualScreen;
            Rectangle region = new Rectangle(desktop.Left, desktop.Top, Math.Min(64, desktop.Width), Math.Min(64, desktop.Height));
            ScreenRecordingOptions options = new ScreenRecordingOptions { RecordMicrophone = false, RecordSystemAudio = false, IncludeCursor = false };
            int attempts = 0, captured = 0;
            string path = Path.Combine(directory, "startup-retry-synthetic.mp4");
            using (ScreenRecorder recorder = new ScreenRecorder(delegate(Bitmap frame, Rectangle source, bool cursor)
                { captured++; SyntheticFrame(frame, source, cursor); }, delegate(Thread thread)
                {
                    attempts++;
                    if (attempts == 1) throw new InvalidOperationException("Synthetic worker startup failure");
                    thread.Start();
                }))
            {
                bool failed = false;
                try { recorder.StartAsync(region, path, options); }
                catch (InvalidOperationException error) { failed = error.Message == "Synthetic worker startup failure"; }
                Check(failed, "First startup propagates the original thread failure");
                Check(!recorder.IsRecording && captured == 0, "Failed startup captures no frame and is not recording");
                Check(recorder.StopAsync().IsCompleted, "Stop after a failed startup never waits for a nonexistent worker");
                Check(recorder.Error != null && recorder.Error.Message == "Synthetic worker startup failure", "Original thread failure remains available to diagnostics");
                Check(!File.Exists(path), "Failed startup does not create an output file");
                Complete(recorder.StartAsync(region, path, options), "Retry startup");
                Check(recorder.IsRecording && recorder.Error == null, "Retry starts and clears the previous failure");
                Thread.Sleep(120);
                Complete(recorder.StopAsync(), "Retry stop");
                Check(attempts == 2 && captured > 0, "Retry uses a fresh actual worker");
                Check(File.Exists(path) && new FileInfo(path).Length > 0, "Retry finalizes its synthetic recording");
            }

            using (ScreenRecorder recorder = new ScreenRecorder(SyntheticFrame, delegate(Thread thread)
                { throw new InvalidOperationException("Repeated worker startup failure"); }))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    bool failed = false;
                    try { recorder.StartAsync(region, Path.Combine(directory, "never-started-" + attempt + ".mp4"), options); }
                    catch (InvalidOperationException error) { failed = error.Message == "Repeated worker startup failure"; }
                    Check(failed, "Repeated failure remains retryable at attempt " + attempt);
                    Check(recorder.StopAsync().IsCompleted && !recorder.IsRecording, "Repeated failure has no pending worker at attempt " + attempt);
                }
            }
            Console.WriteLine("PASS: " + assertions + " recorder worker-start recovery checks; synthetic frames only.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
