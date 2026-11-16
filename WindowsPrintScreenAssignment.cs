using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace SnipStudio
{
    internal enum WindowsPrintScreenAssignmentState { ExplicitOff, ExplicitOn, WindowsDefault, Unavailable }

    // Read-only observation of Windows' current-user keyboard preference.
    // Setup opens Microsoft's supported Settings page; this class never writes
    // an undocumented Windows value or changes another application's shortcuts.
    internal static class WindowsPrintScreenAssignment
    {
        internal const string SettingsUri = "ms-settings:easeofaccess-keyboard";
        internal const string SetupInstructions = "In Windows keyboard settings, turn off ‘Use the Print screen key to open screen capture’ (or ‘Use the PrtScn button to open screen snipping’). Return to Snip Studio and choose Retry Print Screen. Windows may require a restart before the change takes effect.";
        internal const string LifetimeInstructions = "Print Screen also needs Use Print Screen enabled in Snip Studio and an editor or its shortcut listener running. Exit Snip Studio stops the listener. Start with Windows is optional.";
        private static readonly object watchGate = new object();
        private static int watchers;
        private static bool watching;
        internal static event EventHandler RefreshRequested;

        internal static WindowsPrintScreenAssignmentState Evaluate(object value)
        {
            if (value == null) return WindowsPrintScreenAssignmentState.WindowsDefault;
            if (value is int && (int)value == 0) return WindowsPrintScreenAssignmentState.ExplicitOff;
            if (value is int && (int)value == 1) return WindowsPrintScreenAssignmentState.ExplicitOn;
            return WindowsPrintScreenAssignmentState.Unavailable;
        }

        internal static WindowsPrintScreenAssignmentState Read(Func<object> readValue)
        {
            if (readValue == null) throw new ArgumentNullException("readValue");
            try { return Evaluate(readValue()); }
            catch (SecurityException) { return WindowsPrintScreenAssignmentState.Unavailable; }
            catch (UnauthorizedAccessException) { return WindowsPrintScreenAssignmentState.Unavailable; }
            catch (IOException) { return WindowsPrintScreenAssignmentState.Unavailable; }
            catch (ArgumentException) { return WindowsPrintScreenAssignmentState.Unavailable; }
            catch (ObjectDisposedException) { return WindowsPrintScreenAssignmentState.Unavailable; }
        }

        internal static WindowsPrintScreenAssignmentState Read()
        {
            return Read(delegate
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard", false))
                    return key == null ? null : key.GetValue("PrintScreenKeyForSnippingEnabled", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            });
        }

        internal static string Describe(WindowsPrintScreenAssignmentState state)
        {
            switch (state)
            {
                case WindowsPrintScreenAssignmentState.ExplicitOff: return "Windows screen-capture shortcut: Off. Snip Studio can register Print Screen while it is running.";
                case WindowsPrintScreenAssignmentState.ExplicitOn: return "Windows screen-capture shortcut: On. Windows Snipping Tool can take Print Screen first.";
                case WindowsPrintScreenAssignmentState.WindowsDefault: return "Windows screen-capture shortcut: Windows default. Windows Snipping Tool may take Print Screen first.";
                default: return "Windows screen-capture shortcut could not be checked. Review its assignment in Windows keyboard settings.";
            }
        }

        internal static string DescribeShortcut(bool enabled, bool registered, int error, WindowsPrintScreenAssignmentState state)
        {
            if (!enabled) return "Print Screen is off in Snip Studio. Use New or Ctrl+Alt+S.";
            if (!registered) return "Print Screen could not be registered (Windows error " + error + "). Use New or Ctrl+Alt+S. " + Describe(state);
            if (state != WindowsPrintScreenAssignmentState.ExplicitOff)
                return "Print Screen is registered, but Windows may still open Snipping Tool first. Review Windows keyboard settings, turn off its Print Screen capture option, then retry.";
            return "Print Screen is registered for Snip Studio while an editor or its shortcut listener is running.";
        }

        internal static void RequestRefresh()
        {
            EventHandler refresh = RefreshRequested;
            if (refresh != null) refresh(null, EventArgs.Empty);
        }

        internal static void StartWatching()
        {
            lock (watchGate)
            {
                watchers++;
                if (watching) return;
                try { SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged; watching = true; }
                catch (InvalidOperationException) { }
                catch (ExternalException) { }
                catch (SecurityException) { }
            }
        }

        internal static void StopWatching()
        {
            lock (watchGate)
            {
                if (watchers > 0) watchers--;
                if (watchers != 0 || !watching) return;
                try { SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged; }
                catch (InvalidOperationException) { }
                catch (ExternalException) { }
                catch (SecurityException) { }
                finally { watching = false; }
            }
        }

        private static void OnWindowsPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // The event can arrive on the system-events thread. Subscribers
            // post to their own UI window; no registry work happens here.
            if (e.Category == UserPreferenceCategory.Keyboard || e.Category == UserPreferenceCategory.General ||
                e.Category == UserPreferenceCategory.Accessibility) RequestRefresh();
        }
    }
}
