using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SnipStudio
{
    // Screenshot scopes end before selection. Recording scopes are owned by
    // MainForm until the recorder has finished its final frame and file work.
    internal sealed class TaskbarVisibilityScope : IDisposable
    {
        private sealed class TaskbarWindow
        {
            internal IntPtr Handle;
            internal uint ProcessId;
            internal string ClassName;
            internal bool HideQueued;
        }

        // A complete per-scope adapter, never a mutable global native override.
        // Deterministic checks can exercise shell delays/failures without
        // enumerating, hiding or restoring any real desktop window.
        internal sealed class Operations
        {
            internal Func<IntPtr[]> Enumerate;
            internal Func<IntPtr, bool> Visible, Exists, Primary;
            internal Func<IntPtr, uint> Process;
            internal Func<IntPtr, string> ClassName;
            internal Func<IntPtr, int, bool> Show;
            internal Func<int> EnumerationError;
            internal Func<long> Clock;
            internal Action<int> Delay;
            internal Action Flush;

            internal void Validate()
            {
                if (Enumerate == null || Visible == null || Exists == null || Primary == null || Process == null ||
                    ClassName == null || Show == null || EnumerationError == null || Clock == null || Delay == null || Flush == null)
                    throw new ArgumentException("Every taskbar operation must be supplied.");
            }
        }

        private readonly List<TaskbarWindow> taskbars = new List<TaskbarWindow>();
        private readonly object stateGate = new object();
        private static readonly object gate = new object();
        private static bool active;
        private bool ownsGate;
        private bool disposed;
        private bool keepPrimaryVisible;
        private readonly Operations operations;
        private readonly bool bestEffort;
        private bool hideConfirmed;
        private string warning = "";

        private TaskbarVisibilityScope(Operations operations, bool bestEffort)
        { this.operations = operations; this.bestEffort = bestEffort; }

        // Last capture-time observation; restoration does not erase the result.
        internal bool HideConfirmed { get { lock (stateGate) return hideConfirmed; } }
        internal string Warning { get { lock (stateGate) return warning; } }

        internal static TaskbarVisibilityScope Hide(bool keepPrimaryVisible)
        {
            return HideWithOperations(keepPrimaryVisible, false, NativeOperations());
        }

        internal static TaskbarVisibilityScope HideBestEffort(bool keepPrimaryVisible)
        {
            return HideWithOperations(keepPrimaryVisible, true, NativeOperations());
        }

        internal static TaskbarVisibilityScope HideWithOperations(bool keepPrimaryVisible, bool bestEffort, Operations operations)
        {
            if (operations == null) throw new ArgumentNullException("operations");
            operations.Validate();
            TaskbarVisibilityScope scope = new TaskbarVisibilityScope(operations, bestEffort);
            lock (gate)
            {
                if (active) throw new InvalidOperationException("Another capture is already hiding the taskbars.");
                active = true;
                scope.ownsGate = true;
            }
            try
            {
                scope.keepPrimaryVisible = keepPrimaryVisible;
                long started = operations.Clock();
                try { scope.MaintainHidden(); }
                catch (Win32Exception)
                {
                    if (!bestEffort) throw;
                    scope.SetWarning();
                    return scope;
                }
                int initialWait = bestEffort ? (int)Math.Max(0, Math.Min(180, 300 - (operations.Clock() - started))) : 180;
                bool hidden = scope.WaitForVisibility(false, initialWait);
                if (!hidden && bestEffort && operations.Clock() - started < 300)
                {
                    // Explorer can be busy after a menu/display transition. One
                    // bounded retry does not extend the ordinary success path.
                    try { scope.MaintainHidden(); }
                    catch (Win32Exception) { scope.SetWarning(); return scope; }
                    int remaining = (int)Math.Max(0, 300 - (operations.Clock() - started));
                    hidden = scope.WaitForVisibility(false, remaining);
                }
                scope.hideConfirmed = hidden;
                if (!hidden)
                {
                    if (!bestEffort)
                        throw new InvalidOperationException("A taskbar did not hide in time. Try again or turn off Hide taskbars during capture.");
                    scope.SetWarning();
                }
                operations.Flush();
                return scope;
            }
            catch (Exception hideFailure)
            {
                try { scope.Dispose(); }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException("Windows could not finish taskbar preparation and restoration.", hideFailure, restoreFailure);
                }
                throw;
            }
        }

        internal void VerifyHidden()
        {
            lock (stateGate) VerifyHiddenCore();
        }

        private void VerifyHiddenCore()
        {
            if (disposed) throw new ObjectDisposedException("TaskbarVisibilityScope");
            if (bestEffort)
            {
                try
                {
                    // Include recreated taskbars, not only handles queued at
                    // startup. The frozen snip may already contain one now.
                    hideConfirmed = FindVisibleTaskbars(keepPrimaryVisible).Count == 0;
                    if (!hideConfirmed) SetWarning();
                }
                catch (Win32Exception) { SetWarning(); }
                return;
            }
            foreach (TaskbarWindow taskbar in taskbars)
                if (SameWindow(taskbar) && operations.Visible(taskbar.Handle))
                    throw new InvalidOperationException("Windows showed a taskbar while preparing the capture. Please try again.");
        }

        private void SetWarning()
        {
            hideConfirmed = false;
            warning = "Windows could not confirm taskbar hiding. This snip may include a taskbar.";
        }

        // The recording owner calls this on its UI timer. If Explorer shows a
        // taskbar again or recreates one after a display change, hide the eligible
        // window without changing shell preferences and retain it for restoration.
        internal void MaintainHidden()
        {
            lock (stateGate) MaintainHiddenCore();
        }

        internal void PrepareFrame()
        {
            lock (stateGate)
            {
                // This callback runs immediately before the recorder's GDI read.
                // Do not add a frame delay when the taskbars remain hidden.
                if (MaintainHiddenCore())
                {
                    if (!WaitForVisibility(false, 180))
                        throw new InvalidOperationException("Windows did not keep a taskbar hidden for recording. The recording has stopped.");
                    operations.Flush();
                }
                VerifyHiddenCore();
            }
        }

        private bool MaintainHiddenCore()
        {
            if (disposed) throw new ObjectDisposedException("TaskbarVisibilityScope");
            bool changed = false;
            foreach (TaskbarWindow taskbar in FindVisibleTaskbars(keepPrimaryVisible))
            {
                if (!SameWindow(taskbar) || !operations.Visible(taskbar.Handle)) continue;
                TaskbarWindow tracked = null;
                foreach (TaskbarWindow existing in taskbars)
                    if (existing.Handle == taskbar.Handle && existing.ProcessId == taskbar.ProcessId && existing.ClassName == taskbar.ClassName) { tracked = existing; break; }
                // Track before queuing so all later failure paths still restore it.
                if (tracked == null) { taskbars.Add(taskbar); tracked = taskbar; }
                bool queued = operations.Show(taskbar.Handle, 0);
                if (queued) tracked.HideQueued = true;
                if (!queued && !bestEffort)
                    throw new InvalidOperationException("Windows could not hide a taskbar for this capture. Turn off Hide taskbars during capture to capture normally.");
                changed = true;
            }
            return changed;
        }

        private List<TaskbarWindow> FindVisibleTaskbars(bool keepPrimaryVisible)
        {
            List<TaskbarWindow> result = new List<TaskbarWindow>();
            IntPtr[] windows = operations.Enumerate();
            if (windows == null)
                throw new Win32Exception(operations.EnumerationError(), "Windows taskbars could not be inspected.");
            foreach (IntPtr handle in windows)
            {
                if (!operations.Visible(handle)) continue;
                string className = operations.ClassName(handle);
                if (className != "Shell_TrayWnd" && className != "Shell_SecondaryTrayWnd") continue;
                if (keepPrimaryVisible && operations.Primary(handle)) continue;
                uint processId = operations.Process(handle);
                if (processId == 0) continue;
                result.Add(new TaskbarWindow { Handle = handle, ProcessId = processId, ClassName = className });
            }
            return result;
        }

        private static bool IsPrimaryTaskbar(IntPtr handle, string className)
        {
            IntPtr monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
            MonitorInfo information = new MonitorInfo();
            information.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref information))
                return (information.Flags & 1) != 0; // MONITORINFOF_PRIMARY
            // If Windows cannot identify the display, retain its primary shell
            // taskbar rather than unexpectedly hiding the user's main taskbar.
            return className == "Shell_TrayWnd";
        }

        private static string WindowClass(IntPtr handle)
        {
            StringBuilder value = new StringBuilder(128);
            return GetClassName(handle, value, value.Capacity) > 0 ? value.ToString() : "";
        }

        private bool SameWindow(TaskbarWindow taskbar)
        {
            return operations.Exists(taskbar.Handle) && operations.ClassName(taskbar.Handle) == taskbar.ClassName &&
                operations.Process(taskbar.Handle) == taskbar.ProcessId;
        }

        private bool WaitForVisibility(bool visible, int maximumMilliseconds)
        {
            long started = operations.Clock();
            while (true)
            {
                bool ready = true;
                foreach (TaskbarWindow taskbar in taskbars)
                    if (SameWindow(taskbar) && operations.Visible(taskbar.Handle) != visible) { ready = false; break; }
                if (ready) return true;
                int remaining = (int)Math.Max(0, maximumMilliseconds - (operations.Clock() - started));
                if (remaining == 0) return false;
                operations.Delay(Math.Min(10, remaining));
            }
        }

        private static void FlushDesktop()
        {
            try { DwmFlush(); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public void Dispose()
        {
            lock (stateGate) RestoreAndDispose();
        }

        // A successfully captured image remains usable if Explorer fails to
        // restore a taskbar. Return the restoration error for visible feedback.
        internal string RestoreAfterCapture()
        {
            try { Dispose(); }
            catch (TaskbarRestorationException failure) { return failure.Message; }
            return "";
        }

        private void RestoreAndDispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                // Do not activate a taskbar or alter its size, position, auto-hide
                // preference, appbar registration, or the Explorer process.
                bool queueFailure = false;
                foreach (TaskbarWindow taskbar in taskbars)
                    if (SameWindow(taskbar))
                    {
                        bool queued = operations.Show(taskbar.Handle, 4); // SW_SHOWNOACTIVATE
                        // A queued hide may execute after this scope ends even
                        // when the taskbar is still visible at this instant.
                        // Require a restore queued behind it, not only a current
                        // visibility check. Retry a rejected queue once.
                        if (!queued && taskbar.HideQueued && !operations.Show(taskbar.Handle, 4)) queueFailure = true;
                    }
                if (queueFailure)
                    throw new TaskbarRestorationException("Windows could not queue restoration for all taskbars.");
                if (!WaitForVisibility(true, 180))
                {
                    foreach (TaskbarWindow taskbar in taskbars)
                        if (SameWindow(taskbar) && !operations.Visible(taskbar.Handle)) operations.Show(taskbar.Handle, 4);
                    if (!WaitForVisibility(true, 180))
                        throw new TaskbarRestorationException("Windows did not confirm that all taskbars were restored.");
                }
                operations.Flush();
            }
            finally
            {
                if (ownsGate)
                {
                    lock (gate) { active = false; ownsGate = false; }
                }
            }
        }

        private sealed class TaskbarRestorationException : InvalidOperationException
        {
            internal TaskbarRestorationException(string message) : base(message) { }
        }

        private static Operations NativeOperations()
        {
            int enumerationError = 0;
            return new Operations
            {
                Enumerate = delegate
                {
                    List<IntPtr> windows = new List<IntPtr>();
                    if (!EnumWindows(delegate(IntPtr handle, IntPtr parameter) { windows.Add(handle); return true; }, IntPtr.Zero))
                    { enumerationError = Marshal.GetLastWin32Error(); return null; }
                    enumerationError = 0;
                    return windows.ToArray();
                },
                EnumerationError = delegate { return enumerationError; },
                Visible = IsWindowVisible,
                Exists = IsWindow,
                ClassName = WindowClass,
                Process = delegate(IntPtr handle) { uint process; return GetWindowThreadProcessId(handle, out process) == 0 ? 0 : process; },
                Primary = delegate(IntPtr handle) { return IsPrimaryTaskbar(handle, WindowClass(handle)); },
                Show = ShowWindowAsync,
                Clock = delegate { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); },
                Delay = Thread.Sleep,
                Flush = FlushDesktop
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int capacity);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo information);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
