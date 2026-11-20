using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace SnipStudio.WinUI;

/// <summary>Lets Setup request the same protected exit as the notification-area menu.</summary>
internal sealed class InstallerExitHook
{
    private const string MessageName = "AutomationForge.SnipStudio.WinUI3.RequestInstallerExit.v1";
    private const int Acknowledged = 0x5355;
    private const uint WmNcDestroy = 0x0082;
    private const nuint SubclassId = 0x53534955;
    private static readonly uint ExitMessage = RegisterWindowMessageW(MessageName);
    private readonly SubclassProcedure procedure;
    private readonly Window window;
    private nint attachedWindow;

    private InstallerExitHook(Window window)
    {
        this.window = window;
        procedure = WindowProcedure;
    }

    internal static void Attach(Window window)
    {
        if (ExitMessage == 0) return;
        var hook = new InstallerExitHook(window);
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (SetWindowSubclass(hwnd, hook.procedure, SubclassId, 0))
        {
            hook.attachedWindow = hwnd;
            window.Closed += hook.OnClosed;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args) => Detach();

    private void Detach()
    {
        if (attachedWindow == 0) return;
        RemoveWindowSubclass(attachedWindow, procedure, SubclassId);
        attachedWindow = 0;
        window.Closed -= OnClosed;
    }

    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == ExitMessage && wParam == Acknowledged)
        {
            if (lParam == 1) return Acknowledged;
            if (lParam == 0 || lParam == 2)
            {
                try { return MainWindow.QueueInstallerExit(window.DispatcherQueue, silent: lParam == 2) ? Acknowledged : 0; }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
                {
                    return 0;
                }
            }
            return 0;
        }
        if (message == WmNcDestroy) Detach();
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
