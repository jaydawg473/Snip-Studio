#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

public sealed record DesktopRecentHistoryResult(IReadOnlyList<string> Paths, bool WindowsHistoryAvailable);

/// <summary>Reads only the Windows document list belonging to this WinUI app.</summary>
public static class DesktopRecentHistory
{
    private const int MaximumWindowsItems = 32;
    private const int MaximumPathLength = 32700;
    private const uint FileSystemPath = 0x80058000;
    private static readonly SemaphoreSlim windowsReader = new(1, 1);
    private static readonly DesktopRecentHistoryResult unavailable = new(Array.Empty<string>(), false);

    public static async Task<DesktopRecentHistoryResult> ReadWindowsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!windowsReader.Wait(0)) return unavailable;
        var result = new TaskCompletionSource<DesktopRecentHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                try { result.TrySetResult(ReadWindowsCore(Environment.ProcessPath, token)); }
                catch (OperationCanceledException) { result.TrySetCanceled(token); }
                catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
                { result.TrySetResult(unavailable); }
                finally { windowsReader.Release(); }
            }) { IsBackground = true, Name = "Snip Studio Windows Recent" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            windowsReader.Release();
            throw;
        }

        // A shell call itself cannot be canceled. Hold the one-worker lease
        // through completion even if the gallery closes or stops waiting.
        try { return await result.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (TimeoutException) { return unavailable; }
    }

    internal static string? GetOwnedLinkPath(string target, string arguments, string executable)
    {
        string? ownedExecutable = NormalizeAbsolutePath(executable);
        if (ownedExecutable is null || !string.Equals(Path.GetExtension(ownedExecutable), ".exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeAbsolutePath(target), ownedExecutable, StringComparison.OrdinalIgnoreCase)
            || arguments.Length < 3 || arguments.Length > MaximumPathLength + 2
            || arguments[0] != '"' || arguments[^1] != '"')
            return null;

        return NormalizeDocumentPath(arguments[1..^1]);
    }

    public static IReadOnlyList<string> Merge(IEnumerable<string> appPaths, IEnumerable<string> windowsPaths)
    {
        // The app's ordered history includes projects and recordings even
        // when Windows disables Recent. Shell history adds missing files.
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Append(appPaths);
        Append(windowsPaths);
        return paths.AsReadOnly();

        void Append(IEnumerable<string> candidates)
        {
            int inspected = 0;
            foreach (string candidate in candidates)
            {
                if (++inspected > MaximumWindowsItems) break;
                if (NormalizeDocumentPath(candidate) is { } path && seen.Add(path))
                    paths.Add(path);
            }
        }
    }

    internal static string? NormalizeDocumentPath(string? path)
    {
        string? normalized = NormalizeAbsolutePath(path);
        if (normalized is null) return null;
        return Path.GetExtension(normalized).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".pdf"
                or ".mp4" or ".wmv" or ".mov" or ".avi" or ".m4v" or ".webm" or ".mkv" or ".snipstudio"
                => normalized,
            _ => null
        };
    }

    private static string? NormalizeAbsolutePath(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaximumPathLength
            || candidate.IndexOfAny(['\0', '\r', '\n', '"', '*', '?']) >= 0
            || !Path.IsPathFullyQualified(candidate)
            || candidate.StartsWith(@"\\?\", StringComparison.Ordinal)
            || candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;
        try
        {
            string path = Path.GetFullPath(candidate);
            string? root = Path.GetPathRoot(path);
            if (root is null || path.IndexOf(':', root.Length) >= 0) return null;
            return path;
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static DesktopRecentHistoryResult ReadWindowsCore(string? executable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (executable is null || CoInitializeEx(IntPtr.Zero, 2) < 0) return unavailable;
        object? listObject = null;
        object? arrayObject = null;
        try
        {
            listObject = new ApplicationDocumentLists();
            var list = (IApplicationDocumentLists)listObject;
            Marshal.ThrowExceptionForHR(list.SetAppID(DesktopJumpList.ApplicationId));
            token.ThrowIfCancellationRequested();
            Guid objectArrayId = typeof(DesktopJumpList.IObjectArray).GUID;
            Marshal.ThrowExceptionForHR(list.GetList(0, MaximumWindowsItems, ref objectArrayId, out arrayObject));
            var entries = (DesktopJumpList.IObjectArray)arrayObject;
            Marshal.ThrowExceptionForHR(entries.GetCount(out uint count));
            Guid unknownId = new("00000000-0000-0000-C000-000000000046");
            var paths = new List<string>();
            for (uint index = 0; index < Math.Min(count, (uint)MaximumWindowsItems); index++)
            {
                token.ThrowIfCancellationRequested();
                object? entry = null;
                try
                {
                    if (entries.GetAt(index, ref unknownId, out entry) >= 0 && ReadEntryPath(entry, executable) is { } path)
                        paths.Add(path);
                }
                catch (COMException) { /* A stale shell entry does not hide the rest of Recent. */ }
                finally { Release(entry); }
            }
            return new(paths.AsReadOnly(), true);
        }
        finally
        {
            Release(arrayObject);
            Release(listObject);
            CoUninitialize();
        }
    }

    private static string? ReadEntryPath(object? entry, string executable)
    {
        if (entry is DesktopJumpList.IShellLinkW link)
        {
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            if (link.GetPath(target, target.Capacity, IntPtr.Zero, 4) < 0
                || link.GetArguments(arguments, arguments.Capacity) < 0
                || target.Length >= target.Capacity - 1 || arguments.Length >= arguments.Capacity - 1)
                return null;
            return GetOwnedLinkPath(target.ToString(), arguments.ToString(), executable);
        }
        if (entry is not IShellItem item) return null;
        nint name = 0;
        try
        {
            return item.GetDisplayName(FileSystemPath, out name) >= 0 && name != 0
                ? NormalizeDocumentPath(Marshal.PtrToStringUni(name))
                : null;
        }
        finally { if (name != 0) Marshal.FreeCoTaskMem(name); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    // IApplicationDocumentLists and the IShellItem prefix match the Windows
    // SDK definitions used by the original Snip Studio Recent service.
    [ComImport, Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")]
    private class ApplicationDocumentLists { }
    [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(uint type, uint desired, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object items);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out object parent);
        [PreserveSig] int GetDisplayName(uint kind, out nint value);
    }
}
