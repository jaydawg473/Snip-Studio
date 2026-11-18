using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        // Encoding is bounded across snip windows, rather than one worker for
        // every edit/window competing with the desktop compositor.
        private static readonly SemaphoreSlim pngEncodeSlots = new SemaphoreSlim(2, 2);
        // A slow encode in an older snip must never replace a newer snip's
        // clipboard content, even when each window's own image is unchanged.
        private static long latestClipboardIntent;
        private long automaticClipboardIntent;
        private uint automaticClipboardSequence;
        private ClipboardPublisher clipboardPublisher = new ClipboardPublisher();
        private readonly SemaphoreSlim clipboardSlot = new SemaphoreSlim(1, 1);
        private bool captureProcessingPending, captureProcessingDisposed;
        private bool closeWhenCaptureReady, closeCaptureForceExit;
        private bool automaticCopyRequested, clipboardCopyBusy;
        private int captureProcessingGeneration;
        private CaptureProcessingJob queuedCaptureWork;
        private PaintEventHandler captureFirstPaint;
        private System.Windows.Forms.Timer captureStartFallback;

        // The close/replace guard also covers the initial clipboard operation:
        // closing immediately after a snip must not outrun its automatic copy.
        private bool IsCaptureSavePending { get { return captureProcessingPending; } }
        private bool IsCaptureProcessingPending { get { return captureProcessingPending; } }

        private sealed class CaptureProcessingJob
        {
            internal Bitmap Snapshot;
            internal object ImageIdentity;
            internal long Revision;
            internal int Generation;
            internal long ClipboardIntent;
            internal uint ClipboardSequence;
            internal bool SaveOriginal, CopyOriginal;
            internal bool Started;
            internal string Folder, Destination;
        }

        private sealed class CaptureProcessingResult
        {
            internal byte[] Png;
            internal Exception SaveError;
        }

        private void PrepareCaptureImage(Bitmap image)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (!preferences.AddScreenshotBorder) return;
            // Capture owns this bitmap. Draw the border before the editor's
            // single snapshot instead of cloning and resetting the editor twice.
            using (Graphics graphics = Graphics.FromImage(image))
            using (Pen pen = new Pen(Color.FromArgb(preferences.ScreenshotBorderArgb), Math.Max(1, preferences.ScreenshotBorderWidth)))
            {
                pen.Alignment = PenAlignment.Inset;
                graphics.DrawRectangle(pen, 0, 0, image.Width - 1, image.Height - 1);
            }
        }

        private void BeginCapturePolicies(Bitmap image)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (captureProcessingPending) throw new InvalidOperationException("The previous capture is still finishing. Please wait briefly and try again.");
            if (automaticCopyTimer != null) automaticCopyTimer.Stop();
            automaticCopyRequested = false;
            bool save = preferences.AutoSaveOriginalScreenshots;
            bool copy = preferences.AutoCopyScreenshotChanges;
            long clipboardIntent = copy ? RegisterClipboardIntent() : 0;
            if (copy)
            {
                automaticClipboardIntent = clipboardIntent;
                automaticClipboardSequence = clipboardPublisher.CaptureSequence();
            }
            capturePolicyMessage = save ? "Captured. Saving the original in the background…"
                : copy ? "Captured. Preparing the clipboard…" : "Captured. Annotate your snip, then Copy or Save.";
            if (!save && !copy) return;

            Bitmap snapshot = image.Clone(new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppArgb);
            CaptureProcessingJob job = new CaptureProcessingJob
            {
                Snapshot = snapshot,
                ImageIdentity = canvas.ImageIdentity,
                Revision = canvas.DocumentRevision,
                Generation = ++captureProcessingGeneration,
                ClipboardIntent = clipboardIntent,
                ClipboardSequence = automaticClipboardSequence,
                SaveOriginal = save,
                CopyOriginal = copy,
                Folder = preferences.ScreenshotFolder
            };
            if (save) job.Destination = Path.Combine(job.Folder, "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
            captureProcessingPending = true;
            queuedCaptureWork = job;
            SetStatus(capturePolicyMessage);

            // A posted callback alone can precede WM_PAINT. Post from the first
            // paint so the image draw completes before PNG work is queued.
            captureFirstPaint = delegate { StartQueuedCaptureWork(job, true); };
            canvas.Paint += captureFirstPaint;
            captureStartFallback = new System.Windows.Forms.Timer { Interval = 150 };
            captureStartFallback.Tick += delegate { StartQueuedCaptureWork(job, false); };
            captureStartFallback.Start();
            canvas.Invalidate();
        }

        private void DetachCaptureStart()
        {
            if (captureFirstPaint != null) { canvas.Paint -= captureFirstPaint; captureFirstPaint = null; }
            if (captureStartFallback != null) { captureStartFallback.Stop(); captureStartFallback.Dispose(); captureStartFallback = null; }
        }

        private void StartQueuedCaptureWork(CaptureProcessingJob job, bool afterPaint)
        {
            if (!Object.ReferenceEquals(queuedCaptureWork, job)) return;
            DetachCaptureStart();
            if (afterPaint && IsHandleCreated && !IsDisposed && !Disposing)
            {
                try { BeginInvoke((MethodInvoker)delegate { ProcessCapturedOriginalAsync(job); }); return; }
                catch (InvalidOperationException) { }
            }
            ProcessCapturedOriginalAsync(job);
        }

        private async void ProcessCapturedOriginalAsync(CaptureProcessingJob job)
        {
            if (job.Started) return;
            job.Started = true;
            if (Object.ReferenceEquals(queuedCaptureWork, job)) queuedCaptureWork = null;
            bool saveSucceeded = !job.SaveOriginal, copied = false, clipboardUnavailable = false;
            Exception failure = null;
            try
            {
                CaptureProcessingResult result = await EncodeCaptureAsync(job).ConfigureAwait(true);
                failure = result.SaveError;
                saveSucceeded = job.SaveOriginal ? failure == null : true;
                // The await resumes on the UI/STA thread. A saved original is a
                // real recent document even if another image has since opened;
                // failed saves and clipboard-only snips never enter the list.
                if (job.SaveOriginal && saveSucceeded) TaskbarJumpList.RecordRecentFile(job.Destination);
                if (CaptureCompletionIsCurrent(job))
                {
                    if (job.SaveOriginal && saveSucceeded && String.IsNullOrEmpty(currentPath))
                    {
                        currentPath = job.Destination;
                        // An annotation may have been added while the original
                        // encoded. Its edit history remains dirty in that case.
                        if (canvas.DocumentRevision == job.Revision) canvas.MarkSaved();
                    }
                    if (job.CopyOriginal && preferences.AutoCopyScreenshotChanges && canvas.DocumentRevision == job.Revision)
                    {
                        try
                        {
                            ClipboardPublishResult publication = await PublishClipboardAsync(job.Snapshot, result.Png, job.ImageIdentity, job.Revision, true, job.ClipboardIntent, job.ClipboardSequence);
                            copied = publication == ClipboardPublishResult.Copied;
                            clipboardUnavailable = publication == ClipboardPublishResult.Unavailable;
                        }
                        catch (Exception error)
                        {
                            if (failure == null) failure = error;
                        }
                    }
                    else if (preferences.AutoCopyScreenshotChanges) RetainLatestAutomaticCopyRequest();

                    capturePolicyMessage = job.SaveOriginal && saveSucceeded
                        ? "Captured and original saved. Your edits stay separate until you save."
                        : copied ? "Captured and copied to the clipboard." : "Captured. Annotate your snip, then Copy or Save.";
                    if (clipboardUnavailable)
                        capturePolicyMessage = job.SaveOriginal && saveSucceeded
                            ? "Original saved. The clipboard could not be updated; use Copy image to retry. Your snip is still open."
                            : "Captured. The clipboard could not be updated; use Copy image to retry. Your snip is still open.";
                    if (failure != null)
                    {
                        capturePolicyMessage = job.SaveOriginal && !saveSucceeded
                            ? "Captured. Automatic save failed; your snip is still open. Use Save to choose another folder."
                            : job.SaveOriginal ? "The original was saved, but automatic copy failed. Your snip is still open; use Copy image to retry."
                            : "Captured. Automatic copy failed; your snip is still open. Use Copy image to retry.";
                        ShowFailure(capturePolicyMessage, failure);
                    }
                    else if (canvas.DocumentRevision == job.Revision) SetStatus(capturePolicyMessage);
                    UpdateDocumentState();
                }
                else if (failure != null)
                {
                    if (job.SaveOriginal && CaptureRequestCanReport(job))
                        ShowFailure("The original capture could not be saved automatically. Your current image and edits are still open; use Save to keep them", failure);
                    else Trace.WriteLine("Capture post-processing: " + failure.Message);
                }
            }
            catch (Exception error)
            {
                failure = error;
                if (CaptureCompletionIsCurrent(job) || (job.SaveOriginal && CaptureRequestCanReport(job))) ShowFailure("The automatic capture action could not finish. Your snip is still open; use Save or Copy to retry", error);
                else Trace.WriteLine("Capture post-processing: " + error.Message);
            }
            finally
            {
                job.Snapshot.Dispose();
                if (job.Generation == captureProcessingGeneration)
                {
                    captureProcessingPending = false;
                    bool deferredCloseFailed = closeWhenCaptureReady && (!saveSucceeded || failure != null);
                    bool retryClose = closeWhenCaptureReady && saveSucceeded && failure == null;
                    closeWhenCaptureReady = false;
                    if (deferredCloseFailed)
                    {
                        forceExit = false;
                        Program.CancelApplicationExit();
                    }
                    if (!captureProcessingDisposed && !IsDisposed && !Disposing)
                    {
                        CompleteInitialImageView(job.ImageIdentity, job.Revision);
                        ScheduleLatestAutomaticCopy();
                        if (retryClose)
                        {
                            forceExit = closeCaptureForceExit;
                            Close();
                        }
                    }
                }
            }
        }

        private static async Task<CaptureProcessingResult> EncodeCaptureAsync(CaptureProcessingJob job)
        {
            await pngEncodeSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(delegate
                {
                    CaptureProcessingResult result = new CaptureProcessingResult { Png = EncodePng(job.Snapshot) };
                    if (job.SaveOriginal)
                    {
                        try { Directory.CreateDirectory(job.Folder); WritePngBytesAtomic(result.Png, job.Destination); }
                        catch (Exception error) { result.SaveError = error; }
                    }
                    return result;
                }).ConfigureAwait(false);
            }
            finally { pngEncodeSlots.Release(); }
        }

        private bool CaptureCompletionIsCurrent(CaptureProcessingJob job)
        {
            return CaptureRequestCanReport(job) &&
                canvas.HasImage && Object.ReferenceEquals(canvas.ImageIdentity, job.ImageIdentity);
        }

        private bool CaptureRequestCanReport(CaptureProcessingJob job)
        {
            return !captureProcessingDisposed && !IsDisposed && !Disposing && job.Generation == captureProcessingGeneration;
        }

        private bool TryDelayCloseForCaptureSave(FormClosingEventArgs e)
        {
            if (!captureProcessingPending) return false;
            e.Cancel = true;
            closeWhenCaptureReady = true;
            closeCaptureForceExit = forceExit;
            SetStatus("Finishing the capture actions before closing…");
            return true;
        }

        private async Task<bool> CopySnapshotToClipboardAsync(bool onlyIfCurrent)
        {
            if (!canvas.HasImage || IsDisposed || Disposing) return false;
            long intent = onlyIfCurrent ? automaticClipboardIntent : RegisterClipboardIntent();
            uint initialSequence = onlyIfCurrent ? automaticClipboardSequence : clipboardPublisher.CaptureSequence();
            if (!IsLatestClipboardIntent(intent)) return false;
            if (onlyIfCurrent && (captureProcessingPending || clipboardCopyBusy)) { RetainLatestAutomaticCopyRequest(); return false; }
            object identity = canvas.ImageIdentity;
            long revision = canvas.DocumentRevision;
            await clipboardSlot.WaitAsync();
            clipboardCopyBusy = true;
            try
            {
                if (!ClipboardSourceIsCurrent(identity, revision, onlyIfCurrent, intent)) return false;
                using (Bitmap image = onlyIfCurrent ? canvas.RenderCommittedImage() : canvas.RenderImage())
                {
                    byte[] png;
                    await pngEncodeSlots.WaitAsync();
                    try { png = await Task.Run(delegate { return EncodePng(image); }); }
                    finally { pngEncodeSlots.Release(); }
                    if (!ClipboardSourceIsCurrent(identity, revision, onlyIfCurrent, intent))
                    { if (onlyIfCurrent) RetainLatestAutomaticCopyRequest(); return false; }
                    ClipboardPublishResult publication = await clipboardPublisher.PublishAsync(image, png, initialSequence,
                        delegate { return ClipboardSourceIsCurrent(identity, revision, onlyIfCurrent, intent); });
                    if (publication == ClipboardPublishResult.Unavailable && ClipboardSourceIsCurrent(identity, revision, onlyIfCurrent, intent))
                        SetStatus("Your snip is kept. The clipboard could not be updated; use Copy image to retry.");
                    return publication == ClipboardPublishResult.Copied;
                }
            }
            finally
            {
                clipboardCopyBusy = false;
                clipboardSlot.Release();
                ScheduleLatestAutomaticCopy();
            }
        }

        private async Task<ClipboardPublishResult> PublishClipboardAsync(Bitmap image, byte[] png, object identity, long revision, bool automatic, long intent, uint initialSequence)
        {
            await clipboardSlot.WaitAsync();
            clipboardCopyBusy = true;
            try
            {
                if (!ClipboardSourceIsCurrent(identity, revision, automatic, intent)) { if (automatic) RetainLatestAutomaticCopyRequest(); return ClipboardPublishResult.Superseded; }
                return await clipboardPublisher.PublishAsync(image, png, initialSequence,
                    delegate { return ClipboardSourceIsCurrent(identity, revision, automatic, intent); });
            }
            finally { clipboardCopyBusy = false; clipboardSlot.Release(); }
        }

        private bool ClipboardSourceIsCurrent(object identity, long revision, bool automatic, long intent)
        {
            return !captureProcessingDisposed && !IsDisposed && !Disposing && canvas.HasImage &&
                IsLatestClipboardIntent(intent) &&
                Object.ReferenceEquals(canvas.ImageIdentity, identity) && canvas.DocumentRevision == revision &&
                (!automatic || preferences.AutoCopyScreenshotChanges);
        }

        internal static long RegisterClipboardIntent()
        {
            return Interlocked.Increment(ref latestClipboardIntent);
        }

        private static bool IsLatestClipboardIntent(long intent)
        {
            return intent != 0 && intent == Interlocked.Read(ref latestClipboardIntent);
        }

        private void RequestAutomaticClipboardCopy()
        {
            automaticClipboardIntent = RegisterClipboardIntent();
            automaticClipboardSequence = clipboardPublisher.CaptureSequence();
            automaticCopyRequested = true;
        }

        private void RetainLatestAutomaticCopyRequest()
        {
            if (IsLatestClipboardIntent(automaticClipboardIntent)) automaticCopyRequested = true;
        }

        private void ScheduleLatestAutomaticCopy()
        {
            if (!IsLatestClipboardIntent(automaticClipboardIntent)) { automaticCopyRequested = false; return; }
            if (!automaticCopyRequested || captureProcessingPending || captureProcessingDisposed || IsDisposed || Disposing ||
                !preferences.AutoCopyScreenshotChanges || !canvas.HasImage || automaticCopyTimer == null) return;
            automaticCopyRequested = false;
            automaticCopyTimer.Stop(); automaticCopyTimer.Start();
        }

        private static byte[] EncodePng(Bitmap image)
        {
            using (MemoryStream png = new MemoryStream()) { image.Save(png, ImageFormat.Png); return png.ToArray(); }
        }

        private static void WritePngBytesAtomic(byte[] bytes, string destination)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".snip-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                // Original capture names are unique. Never overwrite an unrelated
                // file that appeared at the destination while encoding.
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
        }

        private void DisposeCaptureProcessing()
        {
            captureProcessingDisposed = true;
            DetachCaptureStart();
            CaptureProcessingJob queued = queuedCaptureWork;
            queuedCaptureWork = null;
            if (queued == null || queued.Started) return;
            queued.Started = true;
            // Normal Close is deferred. A hard Dispose still lets the mandatory
            // original finish without relying on a dead UI synchronization context.
            Task.Run(async delegate
            {
                try
                {
                    CaptureProcessingResult result = await EncodeCaptureAsync(queued).ConfigureAwait(false);
                    if (result.SaveError != null) Trace.WriteLine("Capture save during disposal: " + result.SaveError.Message);
                }
                catch (Exception error) { Trace.WriteLine("Capture save during disposal: " + error.Message); }
                finally { queued.Snapshot.Dispose(); }
            });
        }
    }
}
