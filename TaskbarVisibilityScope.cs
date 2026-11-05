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
        }

        private readonly List<TaskbarWindow> taskbars = new List<TaskbarWindow>();
        private readonly object stateGate = new object();
        private static readonly object gate = new object();
        private static bool active;
        private bool ownsGate;
        private bool disposed;
        private bool keepPrimaryVisible;

        private TaskbarVisibilityScope() { }

        internal static TaskbarVisibilityScope Hide(bool keepPrimaryVisible)
        {
            TaskbarVisibilityScope scope = new TaskbarVisibilityScope();
            lock (gate)
            {
                if (active) throw new InvalidOperationException("Another capture is already hiding the taskbars.");
                active = true;
                scope.ownsGate = true;
            }
            try
            {
                scope.keepPrimaryVisible = keepPrimaryVisible;
                scope.MaintainHidden();
                if (!scope.WaitForVisibility(false, 180))
                    throw new InvalidOperationException("A taskbar did not hide in time. Try again or turn off Hide taskbars during capture.");
                FlushDesktop();
                return scope;
            }
            catch
            {
                scope.Dispose();
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
            foreach (TaskbarWindow taskbar in taskbars)
                if (SameWindow(taskbar) && IsWindowVisible(taskbar.Handle))
                    throw new InvalidOperationException("Windows showed a taskbar while preparing the capture. Please try again.");
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
                    FlushDesktop();
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
                if (!SameWindow(taskbar) || !IsWindowVisible(taskbar.Handle)) continue;
                bool tracked = false;
                foreach (TaskbarWindow existing in taskbars)
                    if (existing.Handle == taskbar.Handle && existing.ProcessId == taskbar.ProcessId && existing.ClassName == taskbar.ClassName) { tracked = true; break; }
                // Track before queuing so all later failure paths still restore it.
                if (!tracked) taskbars.Add(taskbar);
                if (!ShowWindowAsync(taskbar.Handle, 0))
                    throw new InvalidOperationException("Windows could not hide a taskbar for this capture. Turn off Hide taskbars during capture to capture normally.");
                changed = true;
            }
            return changed;
        }

        private static List<TaskbarWindow> FindVisibleTaskbars(bool keepPrimaryVisible)
        {
            List<TaskbarWindow> result = new List<TaskbarWindow>();
            if (!EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                if (!IsWindowVisible(handle)) return true;
                string className = WindowClass(handle);
                if (className != "Shell_TrayWnd" && className != "Shell_SecondaryTrayWnd") return true;
                if (keepPrimaryVisible && IsPrimaryTaskbar(handle, className)) return true;
                uint processId;
                if (GetWindowThreadProcessId(handle, out processId) == 0 || processId == 0) return true;
                result.Add(new TaskbarWindow { Handle = handle, ProcessId = processId, ClassName = className });
                return true;
            }, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows taskbars could not be inspected.");
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

        private static bool SameWindow(TaskbarWindow taskbar)
        {
            uint processId;
            return IsWindow(taskbar.Handle) && WindowClass(taskbar.Handle) == taskbar.ClassName &&
                GetWindowThreadProcessId(taskbar.Handle, out processId) != 0 && processId == taskbar.ProcessId;
        }

        private bool WaitForVisibility(bool visible, int maximumMilliseconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            do
            {
                bool ready = true;
                foreach (TaskbarWindow taskbar in taskbars)
                    if (SameWindow(taskbar) && IsWindowVisible(taskbar.Handle) != visible) { ready = false; break; }
                if (ready) return true;
                Thread.Sleep(10);
            } while (elapsed.ElapsedMilliseconds < maximumMilliseconds);
            return false;
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

        private void RestoreAndDispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                // Do not activate a taskbar or alter its size, position, auto-hide
                // preference, appbar registration, or the Explorer process.
                foreach (TaskbarWindow taskbar in taskbars)
                    if (SameWindow(taskbar)) ShowWindowAsync(taskbar.Handle, 4); // SW_SHOWNOACTIVATE
                if (!WaitForVisibility(true, 180))
                {
                    foreach (TaskbarWindow taskbar in taskbars)
                        if (SameWindow(taskbar) && !IsWindowVisible(taskbar.Handle)) ShowWindowAsync(taskbar.Handle, 4);
                    if (!WaitForVisibility(true, 180))
                        throw new InvalidOperationException("Windows did not confirm that all taskbars were restored.");
                }
                FlushDesktop();
            }
            finally
            {
                if (ownsGate)
                {
                    lock (gate) { active = false; ownsGate = false; }
                }
            }
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
