using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

internal static class TaskbarEditorCloseRequest
{
    internal const nuint Acknowledged = 0x5353;
    internal static readonly uint Message = RegisterWindowMessageW("AutomationForge.SnipStudio.ConfirmCloseAll.v1");
    private const uint SendBlockedAbortIfHung = 0x0003;
    private const uint TokenQuery = 0x0008;
    private const uint SetForeground = 0x00010000;
    private const uint WarningYesNoDefaultNo = 0x00000134;
    private const int AnswerYes = 6;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(6);

    internal static void NotifyRunningEditors()
    {
        if (Message == 0 || Environment.ProcessPath is not string executable) return;
        DesktopExecutableTrust.VerifySignedTimestampedExecutable(executable, requireAutomationForge: true);
        using Process sender = Process.GetCurrentProcess();
        using WindowsIdentity senderIdentity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? owner = senderIdentity.User;
        if (owner is null) return;

        // Only the taskbar-launched broker owns the confirmation. Keep simultaneous
        // requests from presenting several prompts for the same installation.
        string installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Local\AutomationForge.SnipStudio.GroupClose." + installation);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        try
        {
            CloseRunningEditorsAsync(sender.Id, sender.SessionId, owner, executable).GetAwaiter().GetResult();
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static async Task CloseRunningEditorsAsync(int senderId, int session, SecurityIdentifier owner, string executable)
    {
        if (FindRunningEditors(senderId, session, owner, executable) is not uint[] targets)
        {
            ShowBlocked();
            return;
        }
        var connections = new List<(NamedPipeClientStream Pipe, Process Target)>();
        bool discardSent = false;
        try
        {
            using (var prepare = new CancellationTokenSource(ResponseTimeout))
            {
                foreach (uint pid in targets)
                {
                    Process target = Process.GetProcessById(checked((int)pid));
                    var connection = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
                    connections.Add((connection, target));
                    // Keep a real handle through commit, so exit can be distinguished
                    // from a still-running target that refused the discard.
                    _ = target.Handle;
                    await connection.ConnectAsync(prepare.Token).ConfigureAwait(false);
                    if (!GetNamedPipeServerProcessId(connection.SafePipeHandle, out uint server) || server != pid ||
                        !MatchesRunningEditor(server, session, owner, executable))
                        throw new IOException("The Snip Studio close listener changed.");
                    TaskbarDiscardStatus result = await TaskbarDiscardProtocol.ReadStatusAsync(connection, prepare.Token).ConfigureAwait(false);
                    if (result == TaskbarDiscardStatus.Ready) continue;
                    if (result != TaskbarDiscardStatus.NoEditors)
                        throw new IOException("A Snip Studio window is busy.");
                    connection.Dispose();
                    target.Dispose();
                    connections.RemoveAt(connections.Count - 1);
                }
            }
            if (connections.Count == 0 ||
                MessageBoxW(0, "Close the currently open snips and discard their unsaved edits? Recordings will stay open.",
                    "Close all without saving?", SetForeground | WarningYesNoDefaultNo) != AnswerYes)
                return;

            using var commit = new CancellationTokenSource(ResponseTimeout);
            TaskbarDiscardStatus[] replies = await Task.WhenAll(connections.Select(async connection =>
            {
                await TaskbarDiscardProtocol.RequestRecheckAsync(connection.Pipe, commit.Token).ConfigureAwait(false);
                return await TaskbarDiscardProtocol.ReadStatusAsync(connection.Pipe, commit.Token).ConfigureAwait(false);
            })).ConfigureAwait(false);
            if (replies.Any(reply => reply != TaskbarDiscardStatus.Ready))
                throw new IOException("A snip changed while you reviewed the close request.");
            discardSent = true;
            bool[] outcomes = await Task.WhenAll(connections.Select(async connection =>
            {
                // Never treat a failed commit write as acknowledgement.
                await TaskbarDiscardProtocol.ConfirmDiscardAsync(connection.Pipe, commit.Token).ConfigureAwait(false);
                try
                {
                    TaskbarDiscardStatus result = await TaskbarDiscardProtocol.ReadStatusAsync(connection.Pipe, commit.Token).ConfigureAwait(false);
                    return result == TaskbarDiscardStatus.Ready ||
                        result == TaskbarDiscardStatus.Disconnected && connection.Target.HasExited;
                }
                catch (IOException) when (connection.Target.HasExited)
                {
                    // WinUI can end the process when its last window closes.
                    return true;
                }
            })).ConfigureAwait(false);
            if (outcomes.Any(closed => !closed)) throw new IOException("An editor did not finish closing.");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or
                Win32Exception or ArgumentException or InvalidOperationException)
        {
            // Release any editors waiting for the commit before displaying feedback.
            foreach (var connection in connections) connection.Pipe.Dispose();
            if (discardSent)
                MessageBoxW(0, "Some snips could not be closed. Check the remaining Snip Studio windows.",
                    "Snip Studio", SetForeground | 0x30);
            else
                ShowBlocked();
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Pipe.Dispose();
                connection.Target.Dispose();
            }
        }
    }

    internal static async Task ReceiveAsync(Func<Task<TaskbarDiscardStatus>> prepare, Func<Task<bool>> recheck, Func<Task<bool>> discard)
    {
        if (Environment.ProcessPath is not string executable) return;
        using Process receiver = Process.GetCurrentProcess();
        using WindowsIdentity receiverIdentity = WindowsIdentity.GetCurrent();
        if (receiverIdentity.User is not SecurityIdentifier owner) return;

        using var server = new NamedPipeServerStream(PipeName((uint)receiver.Id), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var startup = new CancellationTokenSource(ResponseTimeout);
        await server.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint client) ||
            !MatchesRunningEditor(client, receiver.SessionId, owner, executable))
            return;
        DesktopExecutableTrust.VerifySignedTimestampedExecutable(executable, requireAutomationForge: true);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await TaskbarDiscardProtocol.ReceiveAsync(server, prepare, recheck, discard, lifetime.Token).ConfigureAwait(false);
    }

    private static uint[]? FindRunningEditors(int senderId, int session, SecurityIdentifier owner, string executable)
    {
        var sent = new HashSet<uint>();
        var inspected = new Dictionary<uint, bool>();
        var attempts = new Dictionary<uint, int>();
        long started = Environment.TickCount64;
        bool finished = EnumWindows((hwnd, _) =>
        {
            if (Environment.TickCount64 - started >= 4000) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == senderId || sent.Contains(pid)) return true;
            if (!inspected.TryGetValue(pid, out bool eligible))
            {
                eligible = MatchesRunningEditor(pid, session, owner, executable);
                inspected.Add(pid, eligible);
            }
            if (!eligible) return true;
            attempts.TryGetValue(pid, out int priorAttempts);
            if (priorAttempts == 4) return true;
            attempts[pid] = priorAttempts + 1;
            GetWindowThreadProcessId(hwnd, out uint currentOwner);
            if (currentOwner == pid && SendMessageTimeoutW(hwnd, Message, Acknowledged, 0, SendBlockedAbortIfHung, 450, out nuint result) != 0 &&
                result == Acknowledged)
                sent.Add(pid);
            return true;
        }, 0);
        return finished && inspected.All(entry => !entry.Value || sent.Contains(entry.Key)) ? sent.ToArray() : null;
    }

    private static string PipeName(uint pid) => "AutomationForge.SnipStudio.GroupClose." + pid;

    private static void ShowBlocked() =>
        MessageBoxW(0, "The open snips were kept. Finish any active action, close unfinished Settings or text reviews, then retry.",
            "Could not close all snips", SetForeground | 0x30);

    private static bool MatchesRunningEditor(uint pid, int session, SecurityIdentifier owner, string executable)
    {
        try
        {
            using Process target = Process.GetProcessById(checked((int)pid));
            if (target.SessionId != session ||
                !string.Equals(target.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase) ||
                !OpenProcessToken(target.Handle, TokenQuery, out SafeAccessTokenHandle token))
                return false;
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                return identity.User is SecurityIdentifier user && owner.Equals(user);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Processes can exit or belong to an inaccessible account.
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumerateWindow(nint hwnd, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EnumWindows(EnumerateWindow callback, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern bool OpenProcessToken(nint handle, uint desiredAccess, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint client);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint server);
}
