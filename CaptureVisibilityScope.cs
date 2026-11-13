using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    // This scope hides the initiating editor and Settings, leaving other open
    // snips available to capture. It does not close work or change system policy.
    internal sealed class CaptureVisibilityScope : IDisposable
    {
        private sealed class WindowState
        {
            internal Form Form;
            internal IntPtr Handle;
            internal int PreviousTransitions;
            internal bool ChangedTransitions;
            internal FormWindowState OriginalWindowState;
        }

        private const int TransitionsForceDisabled = 3;
        private const int SettleFallbackMilliseconds = 220;
        private static readonly object captureGate = new object();
        private static readonly object flushGate = new object();
        private static bool captureActive;
        private static Task<int> outstandingFlush;
        private static long lastUncontrolledTransientHidden;
        private readonly List<WindowState> windows = new List<WindowState>();
        private readonly int threadId;
        private bool ownsGate, disposed, needsFallbackSettle;
        private long hiddenAt;

        private CaptureVisibilityScope() { threadId = Thread.CurrentThread.ManagedThreadId; }

        internal static CaptureVisibilityScope HideApplicationWindows(MainForm owner)
        {
            return HideApplicationWindows(owner, false);
        }

        // The explicit Capture open snips action also retains the initiating
        // editor. Ordinary New, recording and scrolling still hide their owner.
        internal static CaptureVisibilityScope HideApplicationWindows(MainForm owner, bool includeOpenSnips)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (owner.IsDisposed || owner.Disposing) throw new ObjectDisposedException("owner");
            if (owner.InvokeRequired) throw new InvalidOperationException("Prepare capture on the application window's thread.");
            CaptureVisibilityScope scope = new CaptureVisibilityScope();
            lock (captureGate)
            {
                if (captureActive) throw new InvalidOperationException("Another screen capture is already being prepared.");
                captureActive = true;
                scope.ownsGate = true;
            }
            try
            {
                List<Form> popupWindows = new List<Form>();
                // Snapshot before mutation. Hiding an owner must not obscure an
                // unsaved modal dialog and leave it inaccessible after capture.
                foreach (Form form in Application.OpenForms)
                {
                    MainForm snip = form as MainForm;
                    SettingsForm settings = form as SettingsForm;
                    if ((snip == null && settings == null) || form.IsDisposed || form.Disposing) continue;
                    foreach (Form owned in form.OwnedForms)
                        if (!owned.IsDisposed && owned.Visible && !(owned is MainForm) && !(owned is SettingsForm))
                            throw new InvalidOperationException("Close the open Snip Studio dialog before starting a capture.");
                    if (form.Modal && form.Visible)
                        throw new InvalidOperationException("Close the open Snip Studio dialog before starting a capture.");
                    if (form.Visible && form.WindowState != FormWindowState.Minimized && form.IsHandleCreated)
                    {
                        bool hide = ShouldHideWindowForCapture(snip != null, settings != null,
                            Object.ReferenceEquals(form, owner), includeOpenSnips);
                        if (hide)
                            scope.windows.Add(new WindowState { Form = form, Handle = form.Handle, OriginalWindowState = form.WindowState });
                        if (hide || Object.ReferenceEquals(form, owner)) popupWindows.Add(form);
                    }
                }
                foreach (WindowState state in scope.windows)
                {
                    bool priorWasReadable;
                    state.PreviousTransitions = ReadTransitions(state.Handle, out priorWasReadable);
                    // The attribute is documented primarily as a setter. When a
                    // getter is unsupported, FALSE is this app's known policy:
                    // no other application code changes this per-window flag.
                    if (!priorWasReadable) state.PreviousTransitions = 0;
                    state.ChangedTransitions = SetTransitions(state.Handle, 1);
                    if (!state.ChangedTransitions) scope.needsFallbackSettle = true;
                }
                scope.needsFallbackSettle = owner.CloseCapturePopups() || scope.needsFallbackSettle;
                // Other editors remain untouched, including their open menus.
                // Only the initiator's menu and the hidden Settings are closed.
                foreach (Form form in popupWindows)
                {
                    if (ClosePopups(form)) scope.needsFallbackSettle = true;
                }
                foreach (WindowState state in scope.windows)
                {
                    if (SameWindow(state)) state.Form.Hide();
                }
                scope.hiddenAt = Stopwatch.GetTimestamp();
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        internal static bool ShouldHideWindowForCapture(bool isEditor, bool isSettings,
            bool isInitiator, bool includeOpenSnips)
        {
            return isSettings || (isEditor && isInitiator && !includeOpenSnips);
        }

        // Use on a newly created countdown/selection window before it is shown.
        // Its transition policy lasts only as long as that transient HWND.
        internal static void DisableTransientAnimations(Form form)
        {
            if (form == null) throw new ArgumentNullException("form");
            bool controlled = false;
            EventHandler apply = delegate
            {
                controlled = !form.IsDisposed && form.IsHandleCreated && SetTransitions(form.Handle, 1);
            };
            form.HandleCreated += apply;
            form.VisibleChanged += delegate
            {
                if (!form.Visible && !controlled) Interlocked.Exchange(ref lastUncontrolledTransientHidden, Stopwatch.GetTimestamp());
            };
            form.FormClosed += delegate
            {
                if (!controlled) Interlocked.Exchange(ref lastUncontrolledTransientHidden, Stopwatch.GetTimestamp());
            };
            if (form.IsHandleCreated) apply(form, EventArgs.Empty);
        }

        internal static void NotifyNativeDialogClosed()
        {
            Interlocked.Exchange(ref lastUncontrolledTransientHidden, Stopwatch.GetTimestamp());
        }

        internal Task WaitUntilHiddenAsync()
        {
            return WaitUntilHiddenAsync(CancellationToken.None);
        }

        internal async Task WaitUntilHiddenAsync(CancellationToken cancellationToken)
        {
            CheckThreadAndLifetime();
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!AllWindowsHidden())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= 250)
                    throw new InvalidOperationException("Snip Studio did not finish hiding. Please try the capture again.");
                await Task.Delay(8, cancellationToken);
                CheckThreadAndLifetime();
            }
            // Suppressing transitions is the fast path. A short fallback is
            // reserved for unsupported transition control or a closing popup.
            int settle = needsFallbackSettle ? RemainingSettle(hiddenAt) : 0;
            settle = Math.Max(settle, RemainingSettle(Interlocked.Read(ref lastUncontrolledTransientHidden)));
            if (settle > 0) await Task.Delay(settle, cancellationToken);
            await FlushCompositionAsync(cancellationToken);
            await FlushCompositionAsync(cancellationToken);
            CheckThreadAndLifetime();
            if (!AllWindowsHidden())
                throw new InvalidOperationException("A Snip Studio window became visible while preparing the capture. Please try again.");
        }

        private static async Task FlushCompositionAsync(CancellationToken cancellationToken)
        {
            Task<int> flush;
            lock (flushGate)
            {
                // A blocked compositor can retain at most one worker across
                // retries; never create an unbounded queue of native calls.
                if (outstandingFlush == null || outstandingFlush.IsCompleted)
                    outstandingFlush = Task.Run<int>(delegate { return DwmFlush(); });
                flush = outstandingFlush;
            }
            Task deadline = Task.Delay(500, cancellationToken);
            if (await Task.WhenAny(flush, deadline) != flush)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Windows is still preparing the screen. Please try the capture again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            int result = await flush;
            if (result < 0) throw new InvalidOperationException("Windows could not finish hiding Snip Studio before capture. Please try again.");
        }

        private bool AllWindowsHidden()
        {
            foreach (WindowState state in windows)
            {
                if (state.Form.IsDisposed || state.Form.Disposing) continue;
                if (state.Form.Visible || (state.Form.IsHandleCreated && IsWindowVisible(state.Form.Handle))) return false;
            }
            return true;
        }

        private void CheckThreadAndLifetime()
        {
            if (disposed) throw new ObjectDisposedException("CaptureVisibilityScope");
            if (Thread.CurrentThread.ManagedThreadId != threadId)
                throw new InvalidOperationException("Finish capture preparation on the application window's thread.");
        }

        private static int RemainingSettle(long timestamp)
        {
            if (timestamp == 0) return 0;
            double elapsed = (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
            return Math.Max(0, SettleFallbackMilliseconds - (int)Math.Min(SettleFallbackMilliseconds, Math.Max(0, elapsed)));
        }

        private static bool SameWindow(WindowState state)
        {
            return !state.Form.IsDisposed && !state.Form.Disposing && state.Form.IsHandleCreated && state.Form.Handle == state.Handle;
        }

        private static bool ClosePopups(Control parent)
        {
            bool closed = false;
            ComboBox combo = parent as ComboBox;
            if (combo != null && combo.DroppedDown) { combo.DroppedDown = false; closed = true; }
            if (parent.ContextMenuStrip != null && parent.ContextMenuStrip.Visible)
            { parent.ContextMenuStrip.Close(ToolStripDropDownCloseReason.CloseCalled); closed = true; }
            ToolStrip strip = parent as ToolStrip;
            if (strip != null)
                foreach (ToolStripItem item in strip.Items)
                {
                    ToolStripDropDownItem dropdown = item as ToolStripDropDownItem;
                    if (dropdown != null && dropdown.HasDropDownItems && dropdown.DropDown.Visible)
                    { dropdown.HideDropDown(); closed = true; }
                }
            foreach (Control child in parent.Controls) closed = ClosePopups(child) || closed;
            return closed;
        }

        private static int ReadTransitions(IntPtr handle, out bool readable)
        {
            int value = 0;
            try { readable = DwmGetWindowAttribute(handle, TransitionsForceDisabled, out value, sizeof(int)) >= 0; }
            catch (DllNotFoundException) { readable = false; }
            catch (EntryPointNotFoundException) { readable = false; }
            return value;
        }

        private static bool SetTransitions(IntPtr handle, int value)
        {
            try { return DwmSetWindowAttribute(handle, TransitionsForceDisabled, ref value, sizeof(int)) >= 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            if (Thread.CurrentThread.ManagedThreadId != threadId)
                throw new InvalidOperationException("Restore capture windows on the application window's thread.");
            disposed = true;
            Exception restoreFailure = null;
            try
            {
                foreach (WindowState state in windows)
                {
                    if (state.Form.IsDisposed || state.Form.Disposing) continue;
                    try
                    {
                        // A valid Form may have recreated its HWND after DPI or
                        // style changes. Restore the Form, but do not write the
                        // old HWND's DWM policy into a different native window.
                        if (!state.Form.Visible)
                        {
                            SetRestoreWithoutActivation(state.Form, true);
                            try
                            {
                                if (state.Form.WindowState != state.OriginalWindowState)
                                    state.Form.WindowState = state.OriginalWindowState;
                                state.Form.Show();
                            }
                            finally { SetRestoreWithoutActivation(state.Form, false); }
                        }
                    }
                    catch (Exception error) { if (restoreFailure == null) restoreFailure = error; }
                    finally
                    {
                        if (SameWindow(state) && state.ChangedTransitions)
                            SetTransitions(state.Handle, state.PreviousTransitions);
                    }
                }
            }
            finally
            {
                if (ownsGate) lock (captureGate) { captureActive = false; ownsGate = false; }
            }
            if (restoreFailure != null)
                throw new InvalidOperationException("A Snip Studio window could not be restored after capture.", restoreFailure);
        }

        private static void SetRestoreWithoutActivation(Form form, bool value)
        {
            MainForm snip = form as MainForm;
            if (snip != null) snip.CaptureRestoreWithoutActivation = value;
            SettingsForm settings = form as SettingsForm;
            if (settings != null) settings.CaptureRestoreWithoutActivation = value;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    }

    public sealed partial class MainForm
    {
        internal bool CaptureRestoreWithoutActivation;
        protected override bool ShowWithoutActivation { get { return CaptureRestoreWithoutActivation || base.ShowWithoutActivation; } }

        internal bool CloseCapturePopups()
        {
            bool closed = CloseShapePopups();
            foreach (ContextMenuStrip popup in new ContextMenuStrip[] { moreMenu, delayPopup, trayMenu })
                if (popup != null && !popup.IsDisposed && popup.Visible)
                {
                    popup.Close(ToolStripDropDownCloseReason.CloseCalled);
                    closed = true;
                }
            return canvas.CloseMenusForCapture() || closed;
        }
    }
}
