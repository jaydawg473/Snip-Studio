using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio Video Review Checks")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyVersion("0.6.23.0")]
[assembly: AssemblyFileVersion("0.6.23.0")]

// Compile against the reviewed application, then sign and verify this executable
// before running it. No app entrypoint, capture, clipboard or external player is used.
internal static class VideoReviewAuditChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); checks++; }
    private static object Field(object owner, string name)
    { return owner.GetType().GetField(name, Instance).GetValue(owner); }
    private static void Invoke(object owner, string name, params object[] arguments)
    { owner.GetType().GetMethod(name, Instance).Invoke(owner, arguments); }
    private static bool Enabled(VideoReviewForm form, string name)
    { return ((Control)Field(form, name)).Enabled; }
    private static Task MetadataRead(VideoReviewForm form, string source, TimeSpan wait, bool preserveStatus)
    { return (Task)form.GetType().GetMethod("ReadMetadataAsync", Instance).Invoke(form, new object[] { source, wait, preserveStatus }); }
    private static Task<bool> Copy(VideoReviewForm form, string source, string destination)
    { return (Task<bool>)form.GetType().GetMethod("SaveCopyAsync", Instance).Invoke(form, new object[] { source, destination }); }
    private static void Complete(Task task, string label)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, label + ": bounded completion");
        task.GetAwaiter().GetResult();
    }
    private sealed class HeldInspections : IDisposable
    {
        private readonly SemaphoreSlim slot;
        private int held;
        internal HeldInspections()
        {
            slot = (SemaphoreSlim)typeof(VideoTrimService).GetField("inspectionSlot", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            while (slot.Wait(0)) held++;
            Check(held == 2, "fixture owns both native inspection slots without opening a codec");
        }
        public void Dispose() { if (held > 0) { slot.Release(held); held = 0; } }
    }

    private static void LoadingReview(string source)
    {
        VideoReviewForm form = new VideoReviewForm(source, new Preferences());
        try
        {
            Check((bool)Field(form, "loading"), "fixture starts while metadata is pending");
            Check(Enabled(form, "play") && Enabled(form, "copy") && Enabled(form, "editInClipchamp"),
                "metadata-independent actions remain usable while details are pending");
            Check(!Enabled(form, "applyTrim") && !Enabled(form, "smaller") && !Enabled(form, "startSeconds"),
                "metadata-dependent editing waits for valid details");
            Check(Enabled(form, "saveAs"), "Save as can preserve a separate file while metadata is pending");
            Check(!form.UseWaitCursor, "background metadata does not mark usable file actions as busy");
            Invoke(form, "SetWorking", true, "test operation");
            Check(!Enabled(form, "play") && !Enabled(form, "copy") && !Enabled(form, "editInClipchamp"),
                "active file operations disable competing file actions");
            Check(form.UseWaitCursor, "active file operation has a busy cursor");
            Invoke(form, "SetWorking", false, null);
            Check(Enabled(form, "play") && Enabled(form, "copy") && Enabled(form, "editInClipchamp"),
                "finishing an operation restores metadata-independent actions");
            CancellationToken lifetime = ((CancellationTokenSource)Field(form, "metadataCancellation")).Token;
            form.Dispose();
            Check(lifetime.IsCancellationRequested, "closing the review cancels its pending inspection");
            form.Dispose();
            Invoke(form, "SetWorking", false, null);
            Check(form.IsDisposed, "late completion and repeated disposal do not revive a disposed review");
        }
        finally { form.Dispose(); }
    }

    private static void InspectionCancellation(string source)
    {
        using (CancellationTokenSource canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            bool rejected = false;
            try { VideoTrimService.InspectAsync(source, canceled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "already-canceled metadata does not enter native inspection");
        }
        SemaphoreSlim slot = (SemaphoreSlim)typeof(VideoTrimService).GetField("inspectionSlot", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        int held = 0;
        try
        {
            while (slot.Wait(0)) held++;
            Check(held == 2, "inspection admits at most two concurrent native readers");
            using (CancellationTokenSource queued = new CancellationTokenSource())
            {
                Task<VideoMetadata> waiting = VideoTrimService.InspectAsync(source, queued.Token);
                Check(!waiting.IsCompleted, "metadata waits when both native inspection slots are occupied");
                Stopwatch clock = Stopwatch.StartNew();
                queued.Cancel();
                bool rejected = false;
                try { waiting.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected && waiting.IsCanceled, "closing a queued review cancels without opening its invalid media fixture");
                Console.WriteLine("Queued inspection cancellation: " + clock.Elapsed.TotalMilliseconds.ToString("0.###") + " ms");
            }
        }
        finally { if (held > 0) slot.Release(held); }
    }

    private static void TimeoutAndClose(string source)
    {
        using (HeldInspections held = new HeldInspections())
        using (VideoReviewForm form = new VideoReviewForm(source, new Preferences()))
        {
            Task request = MetadataRead(form, source, TimeSpan.FromMilliseconds(20), false);
            Complete(request, "metadata UI timeout");
            Check(!(bool)Field(form, "loading") && Field(form, "metadata") == null,
                "timed-out metadata leaves honest unavailable state instead of indefinite loading");
            Check(((Label)Field(form, "status")).Text.IndexOf("took too long", StringComparison.OrdinalIgnoreCase) >= 0,
                "metadata timeout explains that details took too long");
            Check(Enabled(form, "saveAs") && Enabled(form, "play") && Enabled(form, "copy") && !Enabled(form, "applyTrim"),
                "timed-out metadata leaves file actions usable and trim unavailable");
            Task closing = MetadataRead(form, source, TimeSpan.FromMinutes(1), false);
            Check((bool)Field(form, "loading"), "replacement metadata request owns loading state");
            form.Dispose();
            Complete(closing, "close pending metadata");
            Check(form.IsDisposed, "closing a pending metadata request remains final");
        }
    }

    private static void LateCompletion()
    {
        MethodInfo wait = typeof(VideoReviewForm).GetMethod("AwaitMetadataAsync", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (bool failLate in new bool[] { false, true })
        {
            TaskCompletionSource<VideoMetadata> operation = new TaskCompletionSource<VideoMetadata>();
            Task<VideoMetadata> pending = (Task<VideoMetadata>)wait.Invoke(null,
                new object[] { operation.Task, TimeSpan.FromMilliseconds(10), CancellationToken.None });
            bool timedOut = false;
            try { Complete(pending, "bounded metadata waiter"); }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut, "an uncompleted native operation releases its UI waiter");
            if (failLate) operation.SetException(new IOException("Synthetic late native failure"));
            else operation.SetResult(new VideoMetadata());
            Check(pending.IsFaulted && pending.Exception.InnerException is TimeoutException,
                "late native success or failure cannot turn an expired UI request into a current result");
        }
    }

    private static void CopyDuringMetadata(string source, string output)
    {
        using (HeldInspections held = new HeldInspections())
        using (VideoReviewForm form = new VideoReviewForm(source, new Preferences()))
        {
            Task oldRequest = MetadataRead(form, source, TimeSpan.FromMinutes(1), false);
            object oldCancellation = Field(form, "metadataRequestCancellation");
            string destination = Path.Combine(output, "copied-review-" + Guid.NewGuid().ToString("N") + ".mp4");
            byte[] before = Hash(source);
            Task<bool> copied = Copy(form, source, destination);
            Complete(copied, "Save as while metadata is pending");
            Check(copied.Result && File.Exists(destination) && Same(before, Hash(destination)) && Same(before, Hash(source)),
                "Save as copies exact original bytes while its metadata is pending");
            Complete(oldRequest, "obsolete metadata after copy");
            Check(String.Equals((string)Field(form, "currentPath"), destination, StringComparison.OrdinalIgnoreCase),
                "successful copy becomes the current file");
            Check((bool)Field(form, "loading") && !Object.ReferenceEquals(oldCancellation, Field(form, "metadataRequestCancellation")),
                "old completion cannot clear the copied file's new loading state");
            Check(((Label)Field(form, "status")).Text.StartsWith("Saved a separate", StringComparison.Ordinal),
                "background metadata restart preserves the completed copy message");
            form.Dispose();
        }
        using (HeldInspections held = new HeldInspections())
        using (VideoReviewForm form = new VideoReviewForm(source, new Preferences()))
        {
            Task oldRequest = MetadataRead(form, source, TimeSpan.FromMinutes(1), false);
            string absentFolder = Path.Combine(output, "absent-" + Guid.NewGuid().ToString("N"));
            Task<bool> copied = Copy(form, source, Path.Combine(absentFolder, "copy.mp4"));
            Complete(copied, "failed Save as while metadata is pending");
            Complete(oldRequest, "obsolete metadata after failed copy");
            Check(!copied.Result && String.Equals((string)Field(form, "currentPath"), source, StringComparison.OrdinalIgnoreCase),
                "copy failure preserves the original current file");
            string failure = ((Label)Field(form, "status")).Text;
            Check(failure.StartsWith("The copy could not be saved", StringComparison.Ordinal),
                "copy failure remains visible when details restart");
            Task timedOut = MetadataRead(form, source, TimeSpan.FromMilliseconds(20), true);
            Complete(timedOut, "background metadata after failed copy");
            Check(((Label)Field(form, "status")).Text == failure,
                "metadata timeout cannot replace the more important copy failure");
            form.Dispose();
        }
    }

    private static byte[] Hash(string path)
    { using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return sha.ComputeHash(stream); }
    private static bool Same(byte[] left, byte[] right)
    { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
    private static void NativeInspection(string source)
    {
        byte[] before = Hash(source);
        Stopwatch clock = Stopwatch.StartNew();
        VideoMetadata value = VideoTrimService.InspectAsync(source, CancellationToken.None).GetAwaiter().GetResult();
        Check(value.Duration > TimeSpan.Zero && value.Width >= 2 && value.Height >= 2 && value.FramesPerSecond >= 1,
            "ordinary signed production inspection still reads usable native video metadata");
        VideoMetadata legacy = VideoTrimService.InspectAsync(source).GetAwaiter().GetResult();
        Check(value.Duration == legacy.Duration && value.Width == legacy.Width && value.Height == legacy.Height && value.HasAudio == legacy.HasAudio,
            "the existing inspection API retains matching metadata");
        Check(Same(before, Hash(source)), "metadata inspection preserves every source file byte");
        Console.WriteLine("Two native metadata inspections: " + clock.Elapsed.TotalMilliseconds.ToString("0.###") + " ms");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("Provide a private output directory; optionally provide an existing synthetic MP4 for native inspection.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            string source = Path.Combine(output, "inert-review-" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllText(source, "Inert fixture: native decoding must not be started for this file.");
            LoadingReview(source);
            InspectionCancellation(source);
            TimeoutAndClose(source);
            LateCompletion();
            CopyDuringMetadata(source, output);
            if (args.Length > 1) NativeInspection(Path.GetFullPath(args[1]));
            Console.WriteLine("PASS: " + checks + " video review audit checks; unshown controls, no player/clipboard/capture or installed preferences.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
