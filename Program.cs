using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Snip Studio")]
[assembly: AssemblyDescription("A local screenshot, annotation, and screen recording workspace")]
[assembly: AssemblyCompany("Automation Forge")]
[assembly: AssemblyProduct("Snip Studio")]
[assembly: AssemblyVersion("0.6.1.0")]
[assembly: AssemblyFileVersion("0.6.1.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace SnipStudio
{
    internal static class Program
    {
        private static StudioApplicationContext applicationContext;
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        // All main windows share one UI thread and one message loop. No individual
        // window owns the application lifetime, so closing the first snip leaves
        // any other open snips available.
        internal static void OpenWindow(MainForm form)
        {
            OpenWindow(form, true);
        }

        internal static void OpenWindow(MainForm form, bool show)
        {
            if (applicationContext == null)
                throw new InvalidOperationException("The application is not ready to open a window.");
            applicationContext.OpenWindow(form, show);
        }

        internal static void RememberShortcutPreferences(Preferences preferences)
        {
            if (applicationContext != null) applicationContext.RememberPreferences(preferences);
        }

        internal static void RequestApplicationExit()
        {
            if (applicationContext != null) applicationContext.RequestExit();
        }

        internal static void CancelApplicationExit()
        {
            EditorClosePrompt.CancelQueuedCloses();
            if (applicationContext != null) applicationContext.CancelExit();
        }

        internal static void TrackAuxiliaryWindow(Form form)
        {
            if (applicationContext != null) applicationContext.TrackAuxiliaryWindow(form);
        }

        internal static void UntrackAuxiliaryWindow(Form form)
        {
            if (applicationContext != null) applicationContext.UntrackAuxiliaryWindow(form);
        }

        private static int RunUpdateCheckDiagnostic()
        {
            try
            {
                UpdateCheckResult result = UpdateChecker.CheckAsync(Assembly.GetExecutingAssembly().GetName().Version, true).GetAwaiter().GetResult();
                Console.WriteLine("Endpoint: " + UpdateChecker.LatestReleaseEndpoint);
                Console.WriteLine("Framework: " + AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName);
                Console.WriteLine("Protocols: " + System.Net.ServicePointManager.SecurityProtocol);
                bool disabled;
                Console.WriteLine("StrongCryptoDisabled: " + (AppContext.TryGetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", out disabled) && disabled));
                Console.WriteLine("SystemDefaultTlsDisabled: " + (AppContext.TryGetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", out disabled) && disabled));
                Console.WriteLine("Status: " + result.Status);
                Console.WriteLine(result.UserMessage);
                return result.Status == UpdateCheckStatus.Unavailable ? 2 : 0;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("The update diagnostic could not complete.");
                return 2;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // A signed-product transport diagnostic, before preferences, IPC,
            // native Shell integration or any window is initialized.
            if (args != null && args.Length == 1 && String.Equals(args[0], "--check-updates", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = RunUpdateCheckDiagnostic();
                return;
            }
            ShortcutStartupRequest startupRequest;
            if (!ShortcutStartupRequest.TryFromArguments(args, out startupRequest))
            {
                MessageBox.Show("This shortcut could not be opened. Open Snip Studio normally, choose a taskbar capture action, or open a supported image file.",
                    "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // Reuse this user's existing editor/listener, including when all editor
            // windows have closed. A second process must not compete for hotkeys.
            string startupProblem;
            ShortcutForwardResult forwarded = ShortcutHost.TryForward(args, out startupProblem);
            if (forwarded == ShortcutForwardResult.Forwarded) return;
            if (forwarded == ShortcutForwardResult.IncompatibleHost)
            {
                MessageBox.Show(startupProblem, "A different Snip Studio copy is running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            TooltipService.SetEnabled(Preferences.Load().ShowTooltips);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("The action could not finish. Your current snip has been kept where possible.\n\n" + e.Exception.Message,
                    "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            using (ShortcutHost shortcuts = new ShortcutHost())
            {
                if (!shortcuts.IsPrimary)
                {
                    forwarded = ShortcutHost.TryForward(args, out startupProblem);
                    if (forwarded != ShortcutForwardResult.Forwarded)
                        MessageBox.Show(forwarded == ShortcutForwardResult.IncompatibleHost ? startupProblem :
                            "Snip Studio is already starting or is busy. Please try again in a moment.",
                            "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                TaskbarJumpList.Initialize();
                Preferences preferences = Preferences.Load();
                using (StudioApplicationContext context = new StudioApplicationContext(shortcuts, preferences))
                {
                    applicationContext = context;
                    try
                    {
                        bool backgroundRequested = startupRequest.Kind == ShortcutStartupRequest.RequestKind.Background;
                        bool captureRequested = startupRequest.Kind == ShortcutStartupRequest.RequestKind.Capture;
                        if (backgroundRequested && preferences.KeepShortcutsAfterClose && !preferences.KeepRunningInTray)
                            context.StartIdleListener();
                        else if (captureRequested)
                        {
                            if (startupRequest.CaptureDelaySeconds.HasValue) context.CaptureFromTaskbar(startupRequest.CaptureDelaySeconds.Value);
                            else context.CaptureFromShortcut();
                        }
                        else
                        {
                            bool background = backgroundRequested && preferences.KeepRunningInTray;
                            string imagePath = backgroundRequested ? null : startupRequest.ImagePath;
                            OpenWindow(new MainForm(imagePath), !background);
                        }
                        Application.Run(context);
                    }
                    finally { applicationContext = null; }
                }
            }
        }
    }

    internal sealed class StudioApplicationContext : ApplicationContext
    {
        private readonly HashSet<MainForm> windows = new HashSet<MainForm>();
        private readonly HashSet<Form> auxiliaryWindows = new HashSet<Form>();
        private readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;
        private bool idleExitPending;
        private bool exiting;
        private bool exitRequested;
        private readonly ShortcutHost shortcuts;
        private Preferences preferences;

        internal StudioApplicationContext(ShortcutHost shortcutHost, Preferences initialPreferences)
        {
            shortcuts = shortcutHost;
            preferences = initialPreferences.Clone();
            shortcuts.OpenRequested += OpenFromShortcut;
            shortcuts.CaptureRequested += CaptureFromShortcut;
            shortcuts.TaskCaptureRequested += CaptureFromTaskbar;
            shortcuts.ExitRequested += RequestGracefulExit;
        }

        internal void RememberPreferences(Preferences value) { preferences = value.Clone(); }

        internal void StartIdleListener()
        {
            if (windows.Count == 0 && !exitRequested && !exiting)
                shortcuts.SetIdleShortcuts(preferences.KeepShortcutsAfterClose, preferences.UsePrintScreen);
        }

        private void OpenFromShortcut(string imagePath)
        {
            if (exiting || exitRequested) return;
            // Each explicit launch opens its own editor. The process and global
            // shortcut listener are shared so windows do not compete for keys.
            shortcuts.SetIdleShortcuts(false, preferences.UsePrintScreen);
            try { OpenWindow(new MainForm(imagePath), true); }
            catch { StartIdleListener(); throw; }
        }

        internal void CaptureFromShortcut()
        {
            CaptureFromShortcut(null);
        }

        internal void CaptureFromTaskbar(int seconds)
        {
            CaptureFromShortcut(seconds);
        }

        private void CaptureFromShortcut(int? captureDelaySeconds)
        {
            if (exiting || exitRequested) return;
            foreach (MainForm form in windows)
                if (!form.IsDisposed) { form.CaptureFromShortcut(false, captureDelaySeconds); return; }
            shortcuts.SetIdleShortcuts(false, preferences.UsePrintScreen);
            MainForm created = null;
            try
            {
                created = new MainForm(null);
                OpenWindow(created, false);
                created.BeginInvoke((MethodInvoker)delegate { if (!created.IsDisposed) created.CaptureFromShortcut(true, captureDelaySeconds); });
            }
            catch
            {
                if (created != null)
                {
                    created.FormClosed -= OnWindowClosed;
                    windows.Remove(created);
                    created.Dispose();
                }
                StartIdleListener();
                throw;
            }
        }

        internal void RequestExit()
        {
            exitRequested = true;
            shortcuts.SetIdleShortcuts(false, preferences.UsePrintScreen);
            // Let every editor finish its save/cancel prompt before dismissing
            // a Settings draft. A canceled Exit must keep that draft available.
            if (windows.Count == 0)
                foreach (Form form in new List<Form>(auxiliaryWindows))
                    if (!form.IsDisposed) form.Close();
            if (windows.Count == 0 && auxiliaryWindows.Count == 0) { exiting = true; ExitThread(); }
        }

        internal void RequestGracefulExit()
        {
            if (exiting || exitRequested) return;
            List<MainForm> trackedEditors = new List<MainForm>(windows);
            foreach (MainForm form in trackedEditors)
            {
                if (form.IsDisposed) continue;
                // Reuse the explicit tray Exit path: every editor gets its
                // existing save/cancel prompt and deferred recording/capture
                // shutdown. A canceled prompt restores the ordinary session.
                form.ExitFromTray(trackedEditors);
                return;
            }
            RequestExit();
        }

        internal void TrackAuxiliaryWindow(Form form)
        {
            if (form == null || form.IsDisposed || exiting) return;
            if (auxiliaryWindows.Add(form)) form.FormClosed += OnAuxiliaryClosed;
        }

        internal void UntrackAuxiliaryWindow(Form form)
        {
            if (form == null || !auxiliaryWindows.Remove(form)) return;
            form.FormClosed -= OnAuxiliaryClosed;
            QueueLastWindowCheck();
        }

        private void OnAuxiliaryClosed(object sender, FormClosedEventArgs e)
        {
            UntrackAuxiliaryWindow(sender as Form);
        }

        internal void CancelExit() { if (!exiting) exitRequested = false; }

        internal int WindowCount { get { return windows.Count; } }

        internal static bool ShouldKeepListenerAlive(int windowCount, bool keepShortcuts, bool explicitExit)
        {
            return windowCount == 0 && keepShortcuts && !explicitExit;
        }

        internal void OpenWindow(MainForm form)
        {
            OpenWindow(form, true);
        }

        internal void OpenWindow(MainForm form, bool show)
        {
            if (form == null) throw new ArgumentNullException("form");
            if (form.IsDisposed) throw new ObjectDisposedException("form");
            if (exiting) throw new InvalidOperationException("The application is closing.");
            if (Thread.CurrentThread.ManagedThreadId != uiThreadId)
                throw new InvalidOperationException("Windows must be opened on the application UI thread.");

            shortcuts.SetIdleShortcuts(false, preferences.UsePrintScreen);

            if (windows.Add(form)) form.FormClosed += OnWindowClosed;
            try
            {
                if (show)
                {
                    if (!form.Visible) form.Show();
                    if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                    form.Activate();
                }
                else
                {
                    // A hidden registered form owns the tray/hotkey lifetime.
                    // Accessing Handle deliberately initializes OnHandleCreated
                    // without showing the capture window during Windows startup.
                    IntPtr hiddenHandle = form.Handle;
                }
            }
            catch
            {
                form.FormClosed -= OnWindowClosed;
                windows.Remove(form);
                QueueLastWindowCheck();
                throw;
            }
        }

        private void OnWindowClosed(object sender, FormClosedEventArgs e)
        {
            MainForm form = sender as MainForm;
            if (form != null)
            {
                form.FormClosed -= OnWindowClosed;
                windows.Remove(form);
            }
            QueueLastWindowCheck();
        }

        private void QueueLastWindowCheck()
        {
            if (windows.Count != 0 || idleExitPending || exiting) return;
            // Defer until FormClosed handlers finish: one of them may open a new
            // capture window while the previous last window is being closed.
            idleExitPending = true;
            Application.Idle += ExitWhenIdle;
        }

        private void ExitWhenIdle(object sender, EventArgs e)
        {
            Application.Idle -= ExitWhenIdle;
            idleExitPending = false;
            if (windows.Count == 0 && !exiting)
            {
                // Modeless Settings is useful even after its original snip was
                // closed; keep its draft and message loop alive until it closes.
                if (auxiliaryWindows.Count != 0)
                {
                    if (exitRequested)
                        foreach (Form form in new List<Form>(auxiliaryWindows))
                            if (!form.IsDisposed) form.Close();
                    if (auxiliaryWindows.Count != 0)
                    {
                        if (!exitRequested) StartIdleListener();
                        return;
                    }
                }
                if (ShouldKeepListenerAlive(windows.Count, preferences.KeepShortcutsAfterClose, exitRequested))
                {
                    StartIdleListener();
                    return;
                }
                exiting = true;
                ExitThread();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Application.Idle -= ExitWhenIdle;
                idleExitPending = false;
                foreach (MainForm form in windows) form.FormClosed -= OnWindowClosed;
                windows.Clear();
                foreach (Form form in auxiliaryWindows) form.FormClosed -= OnAuxiliaryClosed;
                auxiliaryWindows.Clear();
                shortcuts.OpenRequested -= OpenFromShortcut;
                shortcuts.CaptureRequested -= CaptureFromShortcut;
                shortcuts.TaskCaptureRequested -= CaptureFromTaskbar;
                shortcuts.ExitRequested -= RequestGracefulExit;
                shortcuts.SetIdleShortcuts(false, preferences.UsePrintScreen);
            }
            base.Dispose(disposing);
        }
    }
}
