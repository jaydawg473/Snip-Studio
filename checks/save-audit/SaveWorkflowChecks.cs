using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SnipStudio;

[assembly: AssemblyTitle("Snip Studio Save Workflow Checks")]
[assembly: AssemblyDescription("Synthetic atomic-save and modal save responsiveness checks")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Sign before executing. Full mode briefly displays only this fixture's own
// save-progress dialogs. --no-dialog limits checks to file/state boundaries.
internal static class SaveWorkflowChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static object modalObservation;
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    private static object Field(object target, string name)
    { return target.GetType().GetField(name, Instance).GetValue(target); }
    private static void Field(object target, string name, object value)
    { target.GetType().GetField(name, Instance).SetValue(target, value); }
    private static object Call(object target, string name, params object[] args)
    {
        MethodInfo selected = null;
        foreach (MethodInfo method in target.GetType().GetMethods(Instance))
        {
            if (method.Name != name || method.ContainsGenericParameters) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            bool matches = true;
            for (int index = 0; index < parameters.Length; index++)
                if (args[index] == null ? parameters[index].ParameterType.IsValueType : !parameters[index].ParameterType.IsInstanceOfType(args[index]))
                { matches = false; break; }
            if (!matches) continue;
            if (selected != null) throw new AmbiguousMatchException("Fixture expected one exact argument-compatible " + name + " method.");
            selected = method;
        }
        if (selected == null) throw new MissingMethodException(target.GetType().FullName, name);
        try { return selected.Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Atomic(Bitmap image, string path, ImageFormat format)
    {
        try { typeof(MainForm).GetMethod("SaveRenderedImageAtomic", Static, null, new Type[] { typeof(Bitmap), typeof(string), typeof(ImageFormat) }, null).Invoke(null, new object[] { image, path, format }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (Stream input = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
    }
    private static MainForm Editor()
    { return (MainForm)Activator.CreateInstance(typeof(MainForm), Instance, null, new object[] { null, true }, null); }
    private static Bitmap Image()
    {
        Bitmap image = new Bitmap(96, 64, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Crimson, 2, 2, 30, 20);
            graphics.FillRectangle(Brushes.RoyalBlue, 40, 28, 30, 24);
        }
        return image;
    }
    private static bool Same(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
        return true;
    }
    private static void AtomicBoundaries(string output)
    {
        using (Bitmap image = Image())
        using (Bitmap unchanged = (Bitmap)image.Clone())
        {
            foreach (string extension in new string[] { ".png", ".bmp" })
            {
                string path = Path.Combine(output, "atomic" + extension);
                File.WriteAllText(path, "original destination");
                Atomic(image, path, extension == ".png" ? ImageFormat.Png : ImageFormat.Bmp);
                using (Bitmap loaded = new Bitmap(path)) Check(Same(image, loaded), extension + " preserves every opaque image pixel");
            }
            string lockedPath = Path.Combine(output, "locked.png");
            File.WriteAllText(lockedPath, "KEEP EXISTING DESTINATION");
            bool failed = false;
            using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { Atomic(image, lockedPath, ImageFormat.Png); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
            }
            Check(failed, "Locked destination reports the publish failure");
            Check(File.ReadAllText(lockedPath) == "KEEP EXISTING DESTINATION", "Failed publish retains the original file exactly");
            Check(Directory.GetFiles(output, ".snip-*.tmp").Length == 0, "Failed publish cleans its temporary image");
            string pdf = Path.Combine(output, "atomic.pdf");
            Atomic(image, pdf, null);
            byte[] pdfBytes = File.ReadAllBytes(pdf);
            string pdfText = System.Text.Encoding.ASCII.GetString(pdfBytes);
            Check(pdfText.StartsWith("%PDF-1.4") && pdfText.Contains("/Width 96 /Height 64") && pdfText.Contains("%%EOF"), "Existing atomic PDF writer remains usable");
            Check(Same(image, unchanged), "Every save and failure leaves the frozen source image unchanged");
        }
        using (Bitmap transparent = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
        {
            string jpeg = Path.Combine(output, "white-background.jpg");
            Atomic(transparent, jpeg, ImageFormat.Jpeg);
            using (Bitmap loaded = new Bitmap(jpeg))
            { Color pixel = loaded.GetPixel(32, 32); Check(pixel.R >= 250 && pixel.G >= 250 && pixel.B >= 250, "JPEG preserves the existing white transparency background"); }
        }
    }
    private static void SnapshotState(string output)
    {
        using (MainForm form = Editor())
        using (Bitmap image = Image())
        {
            EditorCanvas canvas = (EditorCanvas)Field(form, "canvas");
            canvas.SetImage(image);
            object identity = canvas.ImageIdentity; long revision = canvas.DocumentRevision;
            string path = Path.Combine(output, "snapshot.png");
            Check((bool)Call(form, "CompleteImageSave", path, identity, revision), "Current snapshot completes the synchronous save contract");
            Check((string)Field(form, "currentPath") == path && !canvas.IsDirty, "Current snapshot sets path and marks only that document saved");
            EditorDocument document = (EditorDocument)Field(canvas, "document");
            identity = canvas.ImageIdentity; revision = canvas.DocumentRevision;
            document.AddText("NEW EDIT 00042", new PointF(10, 10), Color.Black, 11, 80);
            Check(!(bool)Call(form, "CompleteImageSave", Path.Combine(output, "older-copy.png"), identity, revision), "Newer revision does not satisfy close-after-save");
            Check((string)Field(form, "currentPath") == path && canvas.IsDirty, "Newer edit retains its path and dirty state");
            identity = canvas.ImageIdentity; revision = canvas.DocumentRevision;
            canvas.SetImage(image);
            Check(!(bool)Call(form, "CompleteImageSave", Path.Combine(output, "older-image.png"), identity, revision), "Replacement image cannot be marked saved by an older operation");
            Check((string)Field(form, "currentPath") == path && canvas.IsDirty, "Replacement image retains its current state");
            form.Dispose();
            Check(!(bool)Call(form, "CompleteImageSave", path, identity, revision), "Disposed owner does not update save state or show UI");
        }
    }
    private static void ModalResponsiveness()
    {
        using (MainForm form = Editor())
        using (Bitmap image = Image())
        using (ManualResetEventSlim release = new ManualResetEventSlim(false))
        {
            EditorCanvas canvas = (EditorCanvas)Field(form, "canvas"); canvas.SetImage(image);
            int uiThread = Thread.CurrentThread.ManagedThreadId, workerThread = uiThread, ticks = 0, paints = 0, postShownTicks = 0;
            bool busySeen = false, duplicateBlocked = false, closeBlocked = false, modalCloseBlocked = false, attachedPaint = false;
            bool closeAttempted = false, progressCancelAtAttempt = false;
            Exception timerFailure = null;
            FormClosingEventArgs progressCloseArgs = null;
            Form progress = null;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 18 })
            {
                timer.Tick += delegate
                {
                    try
                    {
                    ticks++;
                    busySeen |= (bool)typeof(MainForm).GetProperty("IsSavingImage", Instance).GetValue(form, null);
                    foreach (Form candidate in Application.OpenForms)
                        if (candidate.GetType().FullName == "SnipStudio.SaveProgressForm") { progress = candidate; break; }
                    if (progress != null)
                    {
                        if (!attachedPaint)
                        {
                            progress.Paint += delegate { paints++; };
                            progress.FormClosing += delegate(object sender, FormClosingEventArgs args) { progressCloseArgs = args; };
                            attachedPaint = true;
                        }
                        bool started = (bool)progress.GetType().GetProperty("Started", Instance).GetValue(progress, null);
                        bool completed = (bool)Field(progress, "completed");
                        System.Threading.Tasks.Task work = (System.Threading.Tasks.Task)progress.GetType().GetProperty("OperationTask", Instance).GetValue(progress, null);
                        bool visibleAndRunning = progress.Visible && progress.IsHandleCreated && started && !completed && work != null && !work.IsCompleted;
                        Console.WriteLine("STATE ticks=" + ticks + " postShown=" + postShownTicks + " Visible=" + progress.Visible + " Handle=" + progress.IsHandleCreated +
                            " Started=" + started + " Completed=" + completed + " Task=" + (work == null ? "null" : work.Status.ToString()));
                        if (visibleAndRunning) postShownTicks++;
                        progress.Invalidate(); progress.Update();
                        if (postShownTicks >= 2 && visibleAndRunning && !closeAttempted)
                        {
                            closeAttempted = true;
                            try { Call(form, "RunImageSaveWithProgress", new Action(delegate { })); }
                            catch (InvalidOperationException) { duplicateBlocked = true; }
                            FormClosingEventArgs closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                            Call(form, "OnClosing", form, closing); closeBlocked = closing.Cancel;
                            progress.Close(); modalCloseBlocked = !progress.IsDisposed && progress.Visible;
                            progressCancelAtAttempt = progressCloseArgs != null && progressCloseArgs.Cancel;
                            Console.WriteLine("CLOSE ownerCancel=" + closeBlocked + " progressCancel=" + progressCancelAtAttempt + " VisibleAfter=" + progress.Visible +
                                " DisposedAfter=" + progress.IsDisposed + " CompletedAfter=" + Field(progress, "completed"));
                        }
                    }
                    else Console.WriteLine("STATE ticks=" + ticks + " postShown=" + postShownTicks + " progress=null");
                    }
                    catch (Exception error)
                    {
                        timerFailure = error;
                        timer.Stop();
                        Console.Error.WriteLine("TIMER FAILURE: " + error);
                    }
                    finally
                    {
                        if (timerFailure != null || postShownTicks >= 6) release.Set();
                    }
                };
                timer.Start();
                Call(form, "RunImageSaveWithProgress", new Action(delegate
                {
                    workerThread = Thread.CurrentThread.ManagedThreadId;
                    if (!release.Wait(5000)) throw new TimeoutException("Synthetic UI heartbeat did not continue.");
                }));
                timer.Stop();
            }
            modalObservation = new { Ticks = ticks, VisibleStartedHeartbeats = postShownTicks, Paints = paints, WorkerThreadIsUI = workerThread == uiThread,
                BusySeen = busySeen, DuplicateBlocked = duplicateBlocked, CloseAttempted = closeAttempted, OwnerCloseCanceled = closeBlocked,
                ProgressCloseCanceled = progressCancelAtAttempt, ProgressVisibleAfterCloseAttempt = modalCloseBlocked,
                TimerFailure = timerFailure == null ? null : timerFailure.ToString() };
            if (timerFailure != null) throw new InvalidOperationException("Save fixture timer callback failed; worker was released and dialog completed.", timerFailure);
            Check(workerThread != uiThread, "Encoding operation runs off the editor UI thread");
            Check(postShownTicks >= 6 && paints > 0, "Visible, started modal message loop continues timer and paint work while encoding waits");
            Check(busySeen && duplicateBlocked, "Saving state prevents a duplicate save operation");
            Check(closeAttempted && closeBlocked && modalCloseBlocked && progressCancelAtAttempt, "Owner close and progress close remain deferred until worker completion");
            Check(!(bool)typeof(MainForm).GetProperty("IsSavingImage", Instance).GetValue(form, null), "Successful worker clears the pending state");
            bool failure = false;
            try { Call(form, "RunImageSaveWithProgress", new Action(delegate { throw new IOException("synthetic encode failure"); })); }
            catch (IOException error) { failure = error.Message == "synthetic encode failure"; }
            Check(failure && !(bool)typeof(MainForm).GetProperty("IsSavingImage", Instance).GetValue(form, null), "Worker failure reaches the existing save-error path and clears pending state");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException("Expected app SHA256 and unique output directory, optionally --no-dialog.");
            string app = typeof(MainForm).Assembly.Location;
            Check(Hash(app).Equals(args[0], StringComparison.OrdinalIgnoreCase), "Exact selected production app");
            string output = Path.GetFullPath(args[1]);
            if (Directory.Exists(output) && Directory.GetFiles(output).Length > 0) throw new IOException("Preserve previous evidence; use an empty output directory.");
            Directory.CreateDirectory(output);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            AtomicBoundaries(output); SnapshotState(output);
            bool dialogs = args.Length < 3 || args[2] != "--no-dialog";
            if (dialogs) ModalResponsiveness();
            object report = new { Passed = true, Assertions = checks, AppSHA256 = args[0].ToUpperInvariant(), NativeDialogTestsRun = dialogs, ModalObservation = modalObservation,
                CheckedUtc = DateTimeOffset.UtcNow.ToString("o"), Scope = "Synthetic file/state checks; no installed settings, real clipboard, capture or app entrypoint. Full mode additionally displays this fixture's own brief modal save windows and verifies a native timer/paint loop, worker thread, duplicate-save and close guards. Physical mouse/focus is not established." };
            File.WriteAllText(Path.Combine(output, "save-workflow-checks.json"), new JavaScriptSerializer().Serialize(report));
            Console.WriteLine("PASS: " + checks + " save workflow checks; native dialogs=" + dialogs);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
