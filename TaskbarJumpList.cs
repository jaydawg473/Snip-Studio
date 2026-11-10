using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnipStudio
{
    // Windows owns the recent list, its privacy preference and removed entries.
    // No capture paths are persisted by this service. Native work is serialized
    // on a bounded background STA worker so Explorer cannot delay a capture.
    internal static class TaskbarJumpList
    {
        internal const string ApplicationId = "AutomationForge.SnipStudio";
        internal const string ImageProgId = "AutomationForge.SnipStudio.Image";
        private const int MaximumPendingFiles = 16;
        private static readonly object sync = new object();
        private static readonly Queue<string> pendingFiles = new Queue<string>();
        private static readonly string[] imageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
        private static bool identityReady;
        private static bool initializePending;
        private static bool workerRunning;
        private static bool shellReady;
        private static string lastError;
        private static TaskCompletionSource<TaskbarRefreshResult> pendingRefresh;

        internal static bool IsAvailable { get { lock (sync) return shellReady; } }
        internal static string LastError { get { lock (sync) return lastError; } }

        // Call on the primary process before its first window is created. Merely
        // loading this type performs no COM, registry or Shell operations.
        internal static void Initialize()
        {
            // The returned task never faults for an optional Shell failure.
            // Startup does not wait for Explorer, registry or shortcut work.
            RefreshAsync();
        }

        internal static Task<TaskbarRefreshResult> RefreshAsync()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
                    Environment.OSVersion.Version < new Version(6, 1))
                    return CompletedFailure("Taskbar menus are unavailable on this version of Windows.");
                bool assignIdentity;
                lock (sync) assignIdentity = !identityReady;
                if (assignIdentity && SetCurrentProcessExplicitAppUserModelID(ApplicationId) < 0)
                    return CompletedFailure("Windows could not assign the taskbar identity.");
                lock (sync)
                {
                    identityReady = true;
                    // One task represents each queued generation. A concurrent
                    // call shares it; at most one in-flight and one queued
                    // generation exist, without retaining a list of waiters.
                    if (pendingRefresh != null) return pendingRefresh.Task;
                    TaskCompletionSource<TaskbarRefreshResult> completion =
                        new TaskCompletionSource<TaskbarRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingRefresh = completion;
                    initializePending = true;
                    try { EnsureWorkerLocked(); }
                    catch (Exception exception)
                    {
                        if (IsFatal(exception)) throw;
                        initializePending = false;
                        pendingRefresh = null;
                        TaskbarRefreshResult failure = FailedRefresh("Windows could not start the taskbar refresh.");
                        lastError = failure.Warnings[0];
                        completion.TrySetResult(failure);
                    }
                    return completion.Task;
                }
            }
            catch (Exception exception)
            {
                if (IsFatal(exception)) throw;
                return CompletedFailure("Windows taskbar integration is unavailable.");
            }
        }

        private static Task<TaskbarRefreshResult> CompletedFailure(string message)
        {
            SetFailure(message);
            return Task.FromResult(FailedRefresh(message));
        }

        private static TaskbarRefreshResult FailedRefresh(string message)
        {
            return new TaskbarRefreshResult(false, false, new string[] { message });
        }

        // Called only after a real successful open/save. File checks and COM are
        // intentionally deferred; a history failure cannot turn a good save bad.
        internal static void RecordRecentFile(string path)
        {
            try
            {
                string normalized;
                if (!TryNormalizeRecentFile(path, out normalized)) return;
                lock (sync)
                {
                    if (!identityReady) return;
                    // Multiple notifications for the same in-flight save need
                    // only one Shell update; retain the newest bounded work.
                    foreach (string pending in pendingFiles)
                        if (String.Equals(pending, normalized, StringComparison.OrdinalIgnoreCase)) return;
                    if (pendingFiles.Count == MaximumPendingFiles) pendingFiles.Dequeue();
                    pendingFiles.Enqueue(normalized);
                    EnsureWorkerLocked();
                }
            }
            catch (Exception exception)
            {
                if (IsFatal(exception)) throw;
                SetFailure("Windows could not update recent snips.");
            }
        }

        internal static CaptureTask[] GetCaptureTasks()
        {
            return new CaptureTask[]
            {
                new CaptureTask("Take a new screenshot", 0),
                new CaptureTask("New screenshot in 3 seconds", 3),
                new CaptureTask("New screenshot in 5 seconds", 5),
                new CaptureTask("New screenshot in 10 seconds", 10)
            };
        }

        internal static string BuildCaptureArguments(int delaySeconds)
        {
            if (delaySeconds != 0 && delaySeconds != 3 && delaySeconds != 5 && delaySeconds != 10)
                throw new ArgumentOutOfRangeException("delaySeconds");
            return "--capture --delay " + delaySeconds.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool TryNormalizeRecentFile(string path, out string normalized)
        {
            normalized = null;
            string full;
            if (!TryNormalizeAbsolutePath(path, out full)) return false;
            string extension = Path.GetExtension(full);
            foreach (string supported in imageExtensions)
                if (String.Equals(extension, supported, StringComparison.OrdinalIgnoreCase)) { normalized = full; return true; }
            return false;
        }

        private static bool TryNormalizeAbsolutePath(string path, out string normalized)
        {
            normalized = null;
            if (String.IsNullOrWhiteSpace(path) || path.Length > 32700 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                path.IndexOfAny(new char[] { '\0', '\r', '\n', '"', '*', '?' }) >= 0 ||
                path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            bool drive = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            bool unc = path.StartsWith(@"\\", StringComparison.Ordinal);
            if (!drive && !unc) return false;
            if (unc)
            {
                string[] segments = path.Substring(2).Split(new char[] { '\\', '/' });
                if (segments.Length < 3 || String.IsNullOrWhiteSpace(segments[0]) || String.IsNullOrWhiteSpace(segments[1])) return false;
            }
            try
            {
                string full = Path.GetFullPath(path);
                if (full.IndexOf(':', drive ? 2 : 0) >= 0) return false;
                normalized = full;
                return true;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            catch (SecurityException) { }
            return false;
        }

        internal static string BuildOpenCommand(string executablePath)
        {
            string normalized;
            if (!TryNormalizeAbsolutePath(executablePath, out normalized) ||
                !String.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The application path is invalid.", "executablePath");
            return QuoteArgument(normalized) + " \"%1\"";
        }

        // Windows command-line quoting, including trailing backslashes and
        // embedded quotes. Shell links launch the EXE directly, never cmd.exe.
        internal static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"')
                {
                    quoted.Append('\\', slashes * 2 + 1);
                    quoted.Append('"');
                }
                else
                {
                    quoted.Append('\\', slashes);
                    quoted.Append(character);
                }
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2);
            return quoted.Append('"').ToString();
        }

        internal static int NativeVariantSize { get { return Marshal.SizeOf(typeof(PropVariant)); } }

        private static void EnsureWorkerLocked()
        {
            if (workerRunning) return;
            Thread worker = new Thread(Work);
            worker.IsBackground = true;
            worker.Name = "Snip Studio taskbar integration";
            worker.SetApartmentState(ApartmentState.STA);
            workerRunning = true;
            try { worker.Start(); }
            catch { workerRunning = false; throw; }
        }

        private static void Work()
        {
            for (;;)
            {
                bool initialize;
                TaskCompletionSource<TaskbarRefreshResult> completion = null;
                string recent = null;
                lock (sync)
                {
                    initialize = initializePending;
                    initializePending = false;
                    if (initialize) { completion = pendingRefresh; pendingRefresh = null; shellReady = false; }
                    if (!initialize && pendingFiles.Count > 0) recent = pendingFiles.Dequeue();
                    if (!initialize && recent == null) { workerRunning = false; return; }
                }
                try
                {
                    if (initialize)
                    {
                        string executable = Application.ExecutablePath;
                        lock (sync) lastError = null;
                        List<string> warnings = new List<string>();
                        RefreshStep(delegate { RegisterImageHandler(executable); }, warnings, "Windows could not register recent image support.");
                        // Populate capture Tasks before any potentially stale
                        // cloud/desktop shortcut is loaded. Every following
                        // shortcut repair still has an independent outcome.
                        bool recentAvailable = true;
                        bool menuUpdated = RefreshStep(delegate { recentAvailable = CreateJumpListCore(executable); }, warnings,
                            "Windows could not update the taskbar menu.");
                        if (menuUpdated && !recentAvailable) AddWarning(warnings, "Windows recent snips are unavailable; capture tasks remain available.");
                        // Each shortcut is independently owned/validated. An
                        // inaccessible shortcut must not suppress capture Tasks.
                        bool shortcutsUpdated = RefreshStep(delegate { RepairOwnedShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                            "Automation Forge", "Snip Studio", "Snip Studio.lnk"), executable); }, warnings, "Windows could not refresh the Start menu shortcut.");
                        shortcutsUpdated &= RefreshStep(delegate { RepairOwnedShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                            "Snip Studio.lnk"), executable); }, warnings, "Windows could not refresh the desktop shortcut.");
                        bool pinsUpdated = true;
                        bool pinsEnumerated = RefreshStep(delegate { pinsUpdated = RepairOwnedPinnedShortcuts(executable, warnings); }, warnings,
                            "Windows could not check pinned taskbar shortcuts.");
                        shortcutsUpdated &= pinsEnumerated && pinsUpdated;
                        TaskbarRefreshResult result = new TaskbarRefreshResult(menuUpdated, shortcutsUpdated, warnings);
                        lock (sync) { shellReady = menuUpdated; lastError = result.HasWarnings ? String.Join(" ", result.Warnings) : null; }
                        if (completion != null) completion.TrySetResult(result);
                    }
                    else AddRecentFile(recent, Application.ExecutablePath);
                }
                catch (Exception exception)
                {
                    if (IsFatal(exception)) throw;
                    string failure = initialize ? "Windows could not finish the taskbar refresh." : "Windows could not update recent snips.";
                    SetFailure(failure);
                    if (completion != null) completion.TrySetResult(FailedRefresh(failure));
                }
            }
        }

        private static void SetFailure(string message) { lock (sync) lastError = message; }
        private static bool RefreshStep(Action action, IList<string> warnings, string failure)
        {
            try { action(); return true; }
            catch (Exception exception)
            {
                if (IsFatal(exception)) throw;
                AddWarning(warnings, failure);
                return false;
            }
        }

        private static void AddWarning(IList<string> warnings, string message)
        {
            if (warnings.Count < 8 && !warnings.Contains(message)) warnings.Add(message);
        }
        private static bool IsFatal(Exception exception)
        {
            return exception is OutOfMemoryException || exception is StackOverflowException || exception is ThreadAbortException || exception is AccessViolationException;
        }

        private static void RegisterImageHandler(string executable)
        {
            // A non-default per-user handler makes image destinations eligible
            // for this AppID. Never set extension defaults or Windows UserChoice.
            bool changed = false;
            using (RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                using (RegistryKey image = classes.CreateSubKey(ImageProgId))
                {
                    changed |= SetRegistryString(image, "", "Snip Studio image");
                    changed |= SetRegistryString(image, "AppUserModelID", ApplicationId);
                    using (RegistryKey application = image.CreateSubKey("Application"))
                    {
                        changed |= SetRegistryString(application, "ApplicationName", "Snip Studio");
                        changed |= SetRegistryString(application, "ApplicationDescription", "Open and annotate an image in Snip Studio");
                    }
                    using (RegistryKey icon = image.CreateSubKey("DefaultIcon"))
                        changed |= SetRegistryString(icon, "", QuoteArgument(executable) + ",0");
                    using (RegistryKey command = image.CreateSubKey(@"shell\open\command"))
                        changed |= SetRegistryString(command, "", BuildOpenCommand(executable));
                }
                foreach (string extension in imageExtensions)
                    using (RegistryKey openWith = classes.CreateSubKey(extension + @"\OpenWithProgids"))
                        changed |= SetRegistryString(openWith, ImageProgId, "");
            }
            if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        private static bool SetRegistryString(RegistryKey key, string name, string value)
        {
            object existing = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (existing is string && String.Equals((string)existing, value, StringComparison.Ordinal)) return false;
            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }

        private static void RepairOwnedShortcut(string shortcutPath, string executable)
        {
            if (!File.Exists(shortcutPath)) return;
            object linkObject = null;
            try
            {
                linkObject = new ShellLinkComObject();
                IShellLinkW link = (IShellLinkW)linkObject;
                IPersistFile file = (IPersistFile)linkObject;
                // Read/validate ownership before requesting write access; the
                // pinned folder also contains shortcuts belonging to other apps.
                file.Load(shortcutPath, 0);
                StringBuilder target = new StringBuilder(32768);
                int result = link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                if (result != 0 || String.IsNullOrWhiteSpace(target.ToString())) return;
                string expanded;
                string expected;
                if (!TryNormalizeAbsolutePath(Environment.ExpandEnvironmentVariables(target.ToString()), out expanded) ||
                    !TryNormalizeAbsolutePath(executable, out expected) ||
                    !String.Equals(expanded, expected, StringComparison.OrdinalIgnoreCase)) return;
                IPropertyStore store = (IPropertyStore)linkObject;
                PropertyKey key = ApplicationIdKey;
                PropVariant existing;
                int read = store.GetValue(ref key, out existing);
                try
                {
                    if (read >= 0 && existing.Kind == 31 && String.Equals(Marshal.PtrToStringUni(existing.Pointer), ApplicationId, StringComparison.Ordinal))
                    {
                        // A manual refresh is also useful when the file is
                        // correct but Explorer still holds older cached data.
                        NotifyShortcutChanged(shortcutPath);
                        return;
                    }
                }
                finally { PropVariantClear(ref existing); }
                // The implementation-default mode returned a read-only property
                // store on actual installed shortcuts. Explicit write access is
                // required only for a matching link whose AppID needs repair.
                // Reopen a fresh ShellLink so no read-only property-store object
                // obtained from the first load is reused for the write.
                ReleaseComObject(linkObject);
                linkObject = null;
                linkObject = new ShellLinkComObject();
                link = (IShellLinkW)linkObject;
                file = (IPersistFile)linkObject;
                file.Load(shortcutPath, 2); // STGM_READWRITE
                target.Length = 0;
                result = link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
                if (result != 0 || !TryNormalizeAbsolutePath(Environment.ExpandEnvironmentVariables(target.ToString()), out expanded) ||
                    !String.Equals(expanded, expected, StringComparison.OrdinalIgnoreCase)) return;
                store = (IPropertyStore)linkObject;
                SetStringProperty(store, ApplicationIdKey, ApplicationId);
                Check(store.Commit());
                file.Save(shortcutPath, true);
                NotifyShortcutChanged(shortcutPath);
            }
            finally { ReleaseComObject(linkObject); }
        }

        private static bool RepairOwnedPinnedShortcuts(string executable, IList<string> warnings)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
            if (!Directory.Exists(folder)) return true;
            int count = 0;
            bool succeeded = true;
            foreach (string shortcut in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                if (count++ == 64) break;
                string candidate = shortcut;
                // Only the AppID property of an exact target match is changed.
                // Do not create/delete pins or alter shortcut arguments/titles.
                succeeded &= RefreshStep(delegate { RepairOwnedShortcut(candidate, executable); }, warnings,
                    "Windows could not refresh a pinned taskbar shortcut.");
            }
            return succeeded;
        }

        private static void NotifyShortcutChanged(string shortcut)
        {
            // SHCNF_PATHW buffers are documented as MAX_PATH. Refuse an invalid
            // notification rather than send a truncated path for another item.
            if (shortcut.Length >= 260) throw new InvalidOperationException("The shortcut path is too long for a Windows refresh notification.");
            IntPtr pointer = Marshal.StringToCoTaskMemUni(shortcut);
            try { SHChangeNotify(0x00002000, 0x3005, pointer, IntPtr.Zero); } // UPDATEITEM; PATHW | FLUSHNOWAIT
            finally { Marshal.FreeCoTaskMem(pointer); }
            string folder = Path.GetDirectoryName(shortcut);
            pointer = Marshal.StringToCoTaskMemUni(folder);
            try { SHChangeNotify(0x00001000, 0x3005, pointer, IntPtr.Zero); } // UPDATEDIR
            finally { Marshal.FreeCoTaskMem(pointer); }
        }

        private static void CreateJumpList(string executable)
        {
            CreateJumpListCore(executable);
        }

        private static bool CreateJumpListCore(string executable)
        {
            object listObject = null;
            object collectionObject = null;
            object removed = null;
            bool begun = false;
            bool committed = false;
            try
            {
                listObject = new DestinationListComObject();
                ICustomDestinationList list = (ICustomDestinationList)listObject;
                Check(list.SetAppID(ApplicationId));
                uint slots;
                Guid arrayId = typeof(IObjectArray).GUID;
                Check(list.BeginList(out slots, ref arrayId, out removed));
                begun = true;
                // This is the OS-managed category, not a replay of our own cache.
                // Windows may leave it empty when recent-document privacy is off.
                bool recentAvailable = list.AppendKnownCategory(2) >= 0;
                collectionObject = new EnumerableObjectCollectionComObject();
                IObjectCollection collection = (IObjectCollection)collectionObject;
                foreach (CaptureTask task in GetCaptureTasks())
                {
                    object link = null;
                    try
                    {
                        link = CreateLink(executable, task.Arguments, task.Title, task.Title);
                        Check(collection.AddObject(link));
                    }
                    finally { ReleaseComObject(link); }
                }
                Check(list.AddUserTasks((IObjectArray)collectionObject));
                Check(list.CommitList());
                committed = true;
                return recentAvailable;
            }
            finally
            {
                if (begun && !committed && listObject != null)
                    try { ((ICustomDestinationList)listObject).AbortList(); } catch (COMException) { }
                ReleaseComObject(removed);
                ReleaseComObject(collectionObject);
                ReleaseComObject(listObject);
            }
        }

        private static object CreateLink(string executable, string arguments, string title, string description)
        {
            object linkObject = new ShellLinkComObject();
            try
            {
                IShellLinkW link = (IShellLinkW)linkObject;
                Check(link.SetPath(executable));
                Check(link.SetArguments(arguments));
                Check(link.SetWorkingDirectory(Path.GetDirectoryName(executable)));
                Check(link.SetDescription(description));
                Check(link.SetIconLocation(executable, 0));
                IPropertyStore properties = (IPropertyStore)linkObject;
                SetStringProperty(properties, TitleKey, title);
                SetStringProperty(properties, ApplicationIdKey, ApplicationId);
                Check(properties.Commit());
                return linkObject;
            }
            catch { ReleaseComObject(linkObject); throw; }
        }

        private static void AddRecentFile(string path, string executable)
        {
            if (!File.Exists(path)) return;
            object linkObject = null;
            IntPtr linkPointer = IntPtr.Zero;
            IntPtr applicationPointer = IntPtr.Zero;
            try
            {
                linkObject = CreateLink(executable, QuoteArgument(path), Path.GetFileName(path), "Open this snip in Snip Studio");
                linkPointer = Marshal.GetComInterfaceForObject(linkObject, typeof(IShellLinkW));
                applicationPointer = Marshal.StringToCoTaskMemUni(ApplicationId);
                RecentLinkInfo info = new RecentLinkInfo();
                info.Link = linkPointer;
                info.ApplicationId = applicationPointer;
                // The link carries this executable and image arguments, so its
                // intended opener is Snip Studio. Windows owns list visibility.
                SHAddToRecentDocs(7, ref info);
            }
            finally
            {
                if (applicationPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(applicationPointer);
                if (linkPointer != IntPtr.Zero) Marshal.Release(linkPointer);
                ReleaseComObject(linkObject);
            }
        }

        private static void SetStringProperty(IPropertyStore store, PropertyKey key, string value)
        {
            PropVariant variant = new PropVariant();
            variant.Kind = 31;
            variant.Pointer = Marshal.StringToCoTaskMemUni(value);
            try { Check(store.SetValue(ref key, ref variant)); }
            finally { PropVariantClear(ref variant); }
        }

        private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        internal sealed class CaptureTask
        {
            internal readonly string Title;
            internal readonly int DelaySeconds;
            internal string Arguments { get { return BuildCaptureArguments(DelaySeconds); } }
            internal CaptureTask(string title, int delaySeconds) { Title = title; DelaySeconds = delaySeconds; }
        }

        internal sealed class TaskbarRefreshResult
        {
            private readonly string[] warnings;
            internal bool MenuUpdated { get; private set; }
            // True means all applicable owned shortcuts were checked/refreshed.
            // Missing links and links owned by another executable stay untouched.
            internal bool ShortcutsUpdated { get; private set; }
            internal bool HasWarnings { get { return warnings.Length != 0; } }
            internal string[] Warnings { get { return (string[])warnings.Clone(); } }
            internal string Message
            {
                get
                {
                    if (MenuUpdated && ShortcutsUpdated)
                        return HasWarnings ? "Taskbar menu refreshed with a warning. Windows has been asked to refresh matching shortcuts." :
                            "Taskbar menu refreshed. Windows has been asked to refresh matching shortcuts.";
                    if (MenuUpdated) return "Taskbar menu refreshed, but some matching shortcuts could not be refreshed.";
                    return ShortcutsUpdated ? "Matching shortcuts were checked, but Windows could not rebuild the taskbar menu." :
                        "Windows could not finish refreshing the taskbar menu and shortcuts.";
                }
            }
            internal TaskbarRefreshResult(bool menuUpdated, bool shortcutsUpdated, IList<string> messages)
            {
                MenuUpdated = menuUpdated;
                ShortcutsUpdated = shortcutsUpdated;
                warnings = new string[Math.Min(messages.Count, 8)];
                for (int index = 0; index < warnings.Length; index++) warnings[index] = messages[index];
            }
        }

        private static PropertyKey TitleKey { get { return new PropertyKey(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2); } }
        private static PropertyKey ApplicationIdKey { get { return new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); } }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            internal Guid Format;
            internal uint Id;
            internal PropertyKey(Guid format, uint id) { Format = format; Id = id; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CountedPointer { internal uint Count; internal IntPtr Pointer; }

        // The counted-pointer union reserves the native full 24-byte x64 and
        // 16-byte x86 PROPVARIANT storage, including GetValue's output buffer.
        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] internal ushort Kind;
            [FieldOffset(8)] internal IntPtr Pointer;
            [FieldOffset(8)] internal CountedPointer Counted;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RecentLinkInfo { internal IntPtr Link; internal IntPtr ApplicationId; }

        [DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void SHAddToRecentDocs(uint flags, ref RecentLinkInfo info);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkComObject { }
        [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
        private class DestinationListComObject { }
        [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
        private class EnumerableObjectCollectionComObject { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
            [PreserveSig] int GetIDList(out IntPtr idList);
            [PreserveSig] int SetIDList(IntPtr idList);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
            [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
            [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
            [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            [PreserveSig] int GetHotkey(out short hotkey);
            [PreserveSig] int SetHotkey(short hotkey);
            [PreserveSig] int GetShowCmd(out int showCommand);
            [PreserveSig] int SetShowCmd(int showCommand);
            [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int count, out int iconIndex);
            [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            [PreserveSig] int Resolve(IntPtr window, uint flags);
            [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectArray
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
        }

        [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IObjectCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
            [PreserveSig] int AddObject([MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int AddFromArray(IObjectArray array);
            [PreserveSig] int RemoveObjectAt(uint index);
            [PreserveSig] int Clear();
        }

        [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICustomDestinationList
        {
            [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int BeginList(out uint minimumSlots, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object removed);
            [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
            [PreserveSig] int AppendKnownCategory(uint category);
            [PreserveSig] int AddUserTasks(IObjectArray tasks);
            [PreserveSig] int CommitList();
            [PreserveSig] int GetRemovedDestinations(ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object removed);
            [PreserveSig] int DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int AbortList();
        }
    }
}
