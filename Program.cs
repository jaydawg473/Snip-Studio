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
[assembly: AssemblyVersion("0.3.0.0")]
[assembly: AssemblyFileVersion("0.3.0.0")]

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

        [STAThread]
        private static void Main(string[] args)
        {
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
            using (StudioApplicationContext context = new StudioApplicationContext())
            {
                applicationContext = context;
                try
                {
                    bool backgroundRequested = args.Length > 0 && String.Equals(args[0], "--background", StringComparison.OrdinalIgnoreCase);
                    // A startup flag must not leave an inaccessible invisible
                    // process when the user has disabled the tray behavior.
                    bool background = backgroundRequested && Preferences.Load().KeepRunningInTray;
                    string imagePath = !backgroundRequested && args.Length > 0 ? args[0] : null;
                    OpenWindow(new MainForm(imagePath), !background);
                    Application.Run(context);
                }
                finally { applicationContext = null; }
            }
        }
    }

    internal sealed class StudioApplicationContext : ApplicationContext
    {
        private readonly HashSet<MainForm> windows = new HashSet<MainForm>();
        private readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;
        private bool idleExitPending;
        private bool exiting;

        internal int WindowCount { get { return windows.Count; } }

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
            }
            base.Dispose(disposing);
        }
    }
}
