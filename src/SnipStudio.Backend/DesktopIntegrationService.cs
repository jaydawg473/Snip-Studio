#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SnipStudio.WinUI.Backend;

/// <summary>
/// Optional integrations for the WinUI preview only. Construct and dispose on
/// the HWND's UI thread. Construction does not register shortcuts, startup or a
/// tray icon; every persistent change is an explicit settings operation.
/// </summary>
public sealed class DesktopIntegrationService : IDisposable
{
    public const string CaptureShortcutLabel = "Ctrl+Alt+Shift+S";
    public const string RecordingShortcutLabel = "Ctrl+Alt+Shift+R";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValue = "Snip Studio WinUI3 Preview - Automation Forge";
    private const int CaptureHotkey = 0x5351, RecordingHotkey = 0x5352;
    private const uint TrayMessage = 0x8000 + 105;
    private const nuint SubclassId = 0x53535733;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly nint hwnd;
    private readonly SubclassProc subclassProc;
    private readonly uint taskbarCreated;
    private nint icon;
    private bool disposed, hotkeysEnabled, trayEnabled, trayAdded;

    public DesktopIntegrationService(nint windowHandle)
    {
        if (windowHandle == 0 || !IsWindow(windowHandle))
            throw new ArgumentException("A live Snip Studio window is required.", nameof(windowHandle));
        if (GetWindowThreadProcessId(windowHandle, out _) != GetCurrentThreadId())
            throw new InvalidOperationException("Initialize desktop integrations on the window's UI thread.");
        hwnd = windowHandle;
        subclassProc = WindowProc;
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        if (!SetWindowSubclass(hwnd, subclassProc, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not initialize optional desktop integrations.");
    }

    public event EventHandler? CaptureRequested;
    public event EventHandler? RecordingRequested;
    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<DesktopIntegrationErrorEventArgs>? IntegrationFailed;
    public bool HotkeysEnabled { get { VerifyAccess(); return hotkeysEnabled; } }
    public bool TrayEnabled { get { VerifyAccess(); return trayEnabled; } }

    public void SetHotkeysEnabled(bool enabled)
    {
        VerifyAccess();
        if (enabled == hotkeysEnabled) return;
        if (!enabled)
        {
            UnregisterHotKey(hwnd, CaptureHotkey);
            UnregisterHotKey(hwnd, RecordingHotkey);
            hotkeysEnabled = false;
            return;
        }
        // Extra Alt avoids claiming local Save as (Ctrl+Shift+S), PrintScreen,
        // or the stable app's capture shortcut.
        const uint modifiers = 0x0001 | 0x0002 | 0x0004 | 0x4000; // ALT | CONTROL | SHIFT | NOREPEAT
        if (!RegisterHotKey(hwnd, CaptureHotkey, modifiers, 0x53))
            throw new Win32Exception(Marshal.GetLastWin32Error(), CaptureShortcutLabel + " is already in use. Global shortcuts were not enabled.");
        if (!RegisterHotKey(hwnd, RecordingHotkey, modifiers, 0x52))
        {
            int error = Marshal.GetLastWin32Error();
            UnregisterHotKey(hwnd, CaptureHotkey);
            throw new Win32Exception(error, RecordingShortcutLabel + " is already in use. Global shortcuts were not enabled.");
        }
        hotkeysEnabled = true;
    }

    public void SetTrayEnabled(bool enabled)
    {
        VerifyAccess();
        if (enabled == trayEnabled && (!enabled || trayAdded)) return;
        if (!enabled)
        {
            RemoveTrayIcon();
            trayEnabled = false;
            // Turning off the only route back to a hidden window reveals it.
            RestoreWindow();
            return;
        }
        AddTrayIcon();
        trayEnabled = true;
    }

    public bool HideToTray()
    {
        VerifyAccess();
        if (!trayEnabled) return false;
        if (!trayAdded) AddTrayIcon();
        ShowWindow(hwnd, 0); // SW_HIDE; only after Explorer accepted the icon.
        return true;
    }

    public void RestoreWindow()
    {
        VerifyAccess();
        ShowWindow(hwnd, IsIconic(hwnd) ? 9 : 5); // RESTORE or SHOW
        SetForegroundWindow(hwnd);
    }

    public bool GetStartupEnabled()
    {
        VerifyAccess();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
        return string.Equals(key?.GetValue(StartupValue) as string, BuildStartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetStartupEnabled(bool enabled)
    {
        VerifyAccess();
        string command = BuildStartupCommand();
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
            DesktopExecutableTrust.VerifySignedTimestampedExecutable(executable, requireAutomationForge: true);
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupKey, true)
                ?? throw new InvalidOperationException("Windows did not allow the startup entry to be opened.");
            key.SetValue(StartupValue, command, RegistryValueKind.String);
            if (!string.Equals(key.GetValue(StartupValue) as string, command, StringComparison.Ordinal))
                throw new IOException("Windows did not confirm the new startup entry.");
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
            if (key is null) return;
            string? existing = key.GetValue(StartupValue) as string;
            if (existing is null) return;
            if (!string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The preview startup entry belongs to another installation. Change it in Windows Startup Apps or from that installation.");
            key.DeleteValue(StartupValue, false);
            if (key.GetValue(StartupValue) is not null)
                throw new IOException("Windows did not confirm removal of the preview startup entry.");
        }
    }

    private static string BuildStartupCommand()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
        if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
            throw new InvalidOperationException("This application path cannot be registered for startup.");
        string command = "\"" + Path.GetFullPath(path) + "\" --background";
        if (command.Length > 260) throw new InvalidOperationException("Move the preview to a shorter path before enabling Windows startup.");
        return command;
    }

    private void AddTrayIcon()
    {
        if (icon == 0)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "SnipStudio.ico");
            icon = LoadImageW(0, path, 1, 0, 0, 0x0010 | 0x0040); // ICON, LOADFROMFILE, DEFAULTSIZE
            if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The Snip Studio notification icon could not be loaded.");
        }
        NotifyIconData data = CreateTrayData();
        if (!Shell_NotifyIconW(0, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the notification icon. The window has stayed open.");
        trayAdded = true;
        data.VersionOrTimeout = 4;
        Shell_NotifyIconW(4, ref data); // NIM_SETVERSION; legacy callbacks remain handled if unavailable.
    }

    private NotifyIconData CreateTrayData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1,
        Flags = 0x0001 | 0x0002 | 0x0004, CallbackMessage = TrayMessage,
        Icon = icon, Tip = "Snip Studio — WinUI 3 preview", Info = "", InfoTitle = ""
    };

    private void RemoveTrayIcon()
    {
        if (!trayAdded) return;
        NotifyIconData data = CreateTrayData();
        Shell_NotifyIconW(2, ref data); // NIM_DELETE
        trayAdded = false;
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference)
    {
        try
        {
            if (!disposed && message == 0x0312 && hotkeysEnabled) // WM_HOTKEY
            {
                if ((int)wParam == CaptureHotkey) { Raise(CaptureRequested); return 0; }
                if ((int)wParam == RecordingHotkey) { Raise(RecordingRequested); return 0; }
            }
            if (!disposed && taskbarCreated != 0 && message == taskbarCreated && trayEnabled)
            {
                trayAdded = false;
                try { AddTrayIcon(); }
                catch { ShowWindow(hwnd, 5); throw; }
            }
            if (!disposed && message == TrayMessage)
            {
                uint notification = unchecked((uint)(long)lParam) & 0xFFFF;
                if (notification is 0x0203 or 0x0400 or 0x0401) // double-click, select, keyboard select
                {
                    RestoreWindow();
                    Raise(RestoreRequested);
                }
                else if (notification is 0x0205 or 0x007B) ShowTrayMenu();
                return 0;
            }
            if (message == 0x0082) // WM_NCDESTROY
            {
                ReleaseNativeResources();
                RemoveWindowSubclass(hwnd, subclassProc, SubclassId);
                disposed = true;
            }
        }
        catch (Exception exception)
        {
            // No managed exception may unwind through a native window callback.
            ReportFailure(exception);
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            AppendMenuW(menu, 0, 1, "Open Snip Studio");
            AppendMenuW(menu, 0, 2, "New screenshot");
            AppendMenuW(menu, 0x0800, 0, null);
            AppendMenuW(menu, 0, 3, "Exit");
            if (!GetCursorPos(out NativePoint point)) return;
            SetForegroundWindow(hwnd);
            uint selected = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, hwnd, 0); // RETURNCMD | RIGHTBUTTON
            if (selected == 1) { RestoreWindow(); Raise(RestoreRequested); }
            else if (selected == 2) { RestoreWindow(); Raise(CaptureRequested); }
            else if (selected == 3) Raise(ExitRequested);
            PostMessageW(hwnd, 0, 0, 0); // documented foreground-menu dismissal handshake
        }
        finally { DestroyMenu(menu); }
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null) return;
        foreach (EventHandler callback in handler.GetInvocationList())
        {
            try { callback(this, EventArgs.Empty); }
            catch (Exception exception) { ReportFailure(exception); }
        }
    }

    private void ReportFailure(Exception exception)
    {
        EventHandler<DesktopIntegrationErrorEventArgs>? handlers = IntegrationFailed;
        if (handlers is null) return;
        foreach (EventHandler<DesktopIntegrationErrorEventArgs> callback in handlers.GetInvocationList())
        {
            try { callback(this, new DesktopIntegrationErrorEventArgs(exception)); }
            catch { /* A failure observer must not escape the native window proc. */ }
        }
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Use desktop integrations on the window's UI thread.");
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyAccess();
        ReleaseNativeResources();
        RemoveWindowSubclass(hwnd, subclassProc, SubclassId);
        disposed = true;
        GC.KeepAlive(subclassProc);
    }

    private void ReleaseNativeResources()
    {
        UnregisterHotKey(hwnd, CaptureHotkey);
        UnregisterHotKey(hwnd, RecordingHotkey);
        hotkeysEnabled = false;
        RemoveTrayIcon();
        trayEnabled = false;
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public nint BalloonIcon;
    }
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}

public sealed class DesktopIntegrationErrorEventArgs : EventArgs
{
    public Exception Exception { get; }
    public DesktopIntegrationErrorEventArgs(Exception exception) => Exception = exception;
}
