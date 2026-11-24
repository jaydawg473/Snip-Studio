#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

/// <summary>Publishes the WinUI capture tasks and lets Windows manage recent documents.</summary>
public static class DesktopJumpList
{
    public const string ApplicationId = "AutomationForge.SnipStudio";
    private const string CloseAllWithoutSavingArgument = "--close-all-without-saving";
    private const string ShortcutName = "Snip Studio";
    private const int RecentQueueLimit = 16;
    private static readonly (int Delay, string Title)[] CaptureTasks =
    [
        (0, "Take a new screenshot"), (3, "New screenshot in 3 seconds"),
        (5, "New screenshot in 5 seconds"), (10, "New screenshot in 10 seconds")
    ];
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static TaskCompletionSource<string>? pendingRefresh;
    private static bool identityReady;
    private static bool workerRunning;
    private static readonly PropertyKey TitleKey = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    private static readonly PropertyKey AppIdKey = new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    // Assign the AppUserModelID before creating the first WinUI window.
    public static void Initialize()
    {
        try
        {
            lock (Gate)
                identityReady = SetCurrentProcessExplicitAppUserModelID(ApplicationId) >= 0;
            _ = RefreshAsync();
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            // A taskbar identity failure must leave the editor available.
        }
    }

    public static bool TryGetCaptureDelay(string[] arguments, out int delaySeconds)
    {
        delaySeconds = 0;
        if (arguments.Length is < 2 or > 4 || !arguments[1].Equals("--capture", StringComparison.OrdinalIgnoreCase))
            return false;
        if (arguments.Length == 2) return true;
        if (arguments.Length == 4 && !arguments[2].Equals("--delay", StringComparison.OrdinalIgnoreCase))
            return false;
        switch (arguments[^1])
        {
            case "0": return true;
            case "3": delaySeconds = 3; return true;
            case "5": delaySeconds = 5; return true;
            case "10": delaySeconds = 10; return true;
            default: return false;
        }
    }

    public static bool IsCloseAllWithoutSavingRequest(string[] arguments) =>
        arguments.Length == 2 && arguments[1].Equals(CloseAllWithoutSavingArgument, StringComparison.OrdinalIgnoreCase);

    public static Task<string> RefreshAsync()
    {
        lock (Gate)
        {
            if (!identityReady) return Task.FromResult("Windows could not assign the Snip Studio taskbar identity.");
            if (pendingRefresh is not null) return pendingRefresh.Task;
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingRefresh = completion;
            StartWorker();
            return completion.Task;
        }
    }

    public static void AddRecent(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 32700 || path.IndexOfAny(['"', '\0', '\r', '\n']) >= 0 ||
            string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            return;
        lock (Gate)
        {
            if (!identityReady) return;
            if (Recent.Count >= RecentQueueLimit) Recent.Dequeue();
            Recent.Enqueue(path);
            StartWorker();
        }
    }

    // Called under Gate. Explorer, signature checks and offline file paths may
    // block, so neither startup nor a recent-file update waits for this STA.
    private static void StartWorker()
    {
        if (workerRunning) return;
        var worker = new Thread(Drain) { IsBackground = true, Name = "Snip Studio taskbar" };
        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            workerRunning = true;
            worker.Start();
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            workerRunning = false;
            pendingRefresh?.TrySetResult("Windows could not start the taskbar update.");
            pendingRefresh = null;
            Recent.Clear();
        }
    }

    private static void Drain()
    {
        string? executable = Environment.ProcessPath;
        bool ownExecutableVerified = false;
        for (;;)
        {
            TaskCompletionSource<string>? refresh;
            string? path;
            lock (Gate)
            {
                refresh = pendingRefresh;
                pendingRefresh = null;
                path = refresh is null && Recent.Count != 0 ? Recent.Dequeue() : null;
                if (refresh is null && path is null) { workerRunning = false; return; }
            }
            try
            {
                if (executable is null) throw new InvalidOperationException("The application path is unavailable.");
                if (!ownExecutableVerified)
                {
                    DesktopExecutableTrust.VerifySignedTimestampedExecutable(executable, requireAutomationForge: true);
                    ownExecutableVerified = true;
                }
                if (refresh is not null)
                    refresh.TrySetResult(Publish(executable));
                else if (File.Exists(path))
                    AddRecentCore(path!, executable);
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                refresh?.TrySetResult("Windows could not update the taskbar menu: " + exception.Message);
                // The app's own recent-file history remains available.
            }
        }
    }

    private static string Publish(string executable)
    {
        bool useRecent = PublishTasks(executable);
        bool shortcutsUpdated = true;
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(programs))
            shortcutsUpdated &= RefreshShortcut(Path.Combine(programs, "Automation Forge", ShortcutName, ShortcutName + ".lnk"), executable);
        if (!string.IsNullOrEmpty(desktop))
            shortcutsUpdated &= RefreshShortcut(Path.Combine(desktop, ShortcutName + ".lnk"), executable);
        string message = useRecent ? "Taskbar capture shortcuts refreshed." :
            "Taskbar capture shortcuts refreshed. The Windows recent category is unavailable.";
        if (!shortcutsUpdated) message += " Windows could not refresh one or more Snip Studio shortcuts.";
        return message;
    }

    private static bool PublishTasks(string executable)
    {
        object destination = new DestinationListComObject();
        object? removed = null;
        object? collection = null;
        var list = (ICustomDestinationList)destination;
        bool staging = false;
        try
        {
            Check(list.SetAppID(ApplicationId));
            Guid array = typeof(IObjectArray).GUID;
            Check(list.BeginList(out _, ref array, out removed));
            staging = true;
            bool useRecent = list.AppendKnownCategory(2) >= 0; // KDC_RECENT
            collection = new EnumerableObjectCollectionComObject();
            foreach ((int delay, string title) in CaptureTasks)
            {
                object link = CreateLink(executable, "--capture " + delay.ToString(CultureInfo.InvariantCulture), title);
                try { Check(((IObjectCollection)collection).AddObject(link)); }
                finally { Release(link); }
            }
            object closeAll = CreateLink(executable, CloseAllWithoutSavingArgument, "Close all without saving");
            try { Check(((IObjectCollection)collection).AddObject(closeAll)); }
            finally { Release(closeAll); }
            Check(list.AddUserTasks((IObjectArray)collection));
            Check(list.CommitList());
            staging = false;
            return useRecent;
        }
        finally
        {
            if (staging) list.AbortList();
            Release(collection);
            Release(removed);
            Release(destination);
        }
    }

    private static object CreateLink(string executable, string arguments, string title)
    {
        object linkObject = new ShellLinkComObject();
        try
        {
            var link = (IShellLinkW)linkObject;
            Check(link.SetPath(executable));
            Check(link.SetWorkingDirectory(Path.GetDirectoryName(executable)!));
            Check(link.SetArguments(arguments));
            Check(link.SetDescription(title));
            Check(link.SetIconLocation(executable, 0));
            var properties = (IPropertyStore)linkObject;
            SetStringProperty(properties, TitleKey, title);
            SetStringProperty(properties, AppIdKey, ApplicationId);
            Check(properties.Commit());
            return linkObject;
        }
        catch { Release(linkObject); throw; }
    }

    private static void AddRecentCore(string path, string executable)
    {
        object link = CreateLink(executable, "\"" + path + "\"", Path.GetFileName(path));
        nint linkPointer = 0;
        nint applicationPointer = 0;
        try
        {
            linkPointer = Marshal.GetComInterfaceForObject(link, typeof(IShellLinkW));
            applicationPointer = Marshal.StringToCoTaskMemUni(ApplicationId);
            var info = new RecentLinkInfo { Link = linkPointer, ApplicationId = applicationPointer };
            SHAddToRecentDocs(7, ref info); // SHARD_APPIDINFOLINK
        }
        finally
        {
            if (applicationPointer != 0) Marshal.FreeCoTaskMem(applicationPointer);
            if (linkPointer != 0) Marshal.Release(linkPointer);
            Release(link);
        }
    }

    // Upgrade only the installer-created shortcut when it still targets this
    // executable, uses no arguments, and doesn't carry a different explicit ID.
    private static bool RefreshShortcut(string path, string executable)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
            object shortcut = new ShellLinkComObject();
            try
            {
                var file = (IPersistFile)shortcut;
                file.Load(path, 2); // STGM_READWRITE
                var link = (IShellLinkW)shortcut;
                var target = new StringBuilder(32768);
                var args = new StringBuilder(1024);
                if (link.GetPath(target, target.Capacity, 0, 4) != 0 ||
                    link.GetArguments(args, args.Capacity) != 0 || args.Length != 0 ||
                    !string.Equals(Environment.ExpandEnvironmentVariables(target.ToString()), executable, StringComparison.OrdinalIgnoreCase))
                    return true;
                var properties = (IPropertyStore)shortcut;
                PropertyKey key = AppIdKey;
                Check(properties.GetValue(ref key, out PropVariant identity));
                bool empty;
                try { empty = identity.Kind == 0; } // VT_EMPTY
                finally { PropVariantClear(ref identity); }
                if (!empty) return true;
                SetStringProperty(properties, AppIdKey, ApplicationId);
                Check(properties.Commit());
                file.Save(path, true);
                SHChangeNotify(0x00002000, 0x0005, path, 0); // UPDATEITEM, PATHW
                return true;
            }
            finally { Release(shortcut); }
        }
        catch (Exception exception) when (Recoverable(exception)) { return false; }
    }

    private static void SetStringProperty(IPropertyStore store, PropertyKey key, string value)
    {
        var variant = new PropVariant { Kind = 31, Pointer = Marshal.StringToCoTaskMemUni(value) }; // VT_LPWSTR
        try { Check(store.SetValue(ref key, ref variant)); }
        finally { PropVariantClear(ref variant); }
    }

    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or AccessViolationException);
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CountedPointer { public uint Count; public nint Pointer; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Kind;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(8)] public CountedPointer FullUnion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecentLinkInfo { public nint Link; public nint ApplicationId; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHAddToRecentDocs(uint flags, ref RecentLinkInfo info);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string first, nint second);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkComObject { }
    [ComImport, Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private class DestinationListComObject { }
    [ComImport, Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private class EnumerableObjectCollectionComObject { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint findData, uint flags);
        [PreserveSig] int GetIDList(out nint idList);
        [PreserveSig] int SetIDList(nint idList);
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
        [PreserveSig] int Resolve(nint window, uint flags);
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
    internal interface IObjectArray
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
