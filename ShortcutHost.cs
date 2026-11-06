using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio
{
    internal enum ShortcutForwardResult { NotForwarded, Forwarded, IncompatibleHost }

    // Legacy listeners have no runtime-version handshake. This policy compares
    // the opened assembly with the existing process's executable file metadata;
    // it does not claim to inspect another process's loaded managed assembly.
    internal static class ShortcutHostCompatibility
    {
        internal static string Check(string launchingPath, Version launchingVersion,
            string runningPath, Version runningFileVersion, string verificationError)
        {
            string opened = NormalizePath(launchingPath), existing = NormalizePath(runningPath);
            bool verified = String.IsNullOrEmpty(verificationError) && opened != null && existing != null &&
                launchingVersion != null && runningFileVersion != null;
            if (verified && String.Equals(opened, existing, StringComparison.OrdinalIgnoreCase) &&
                launchingVersion.Equals(runningFileVersion)) return null;

            string reason = !verified ? "Windows could not verify that the existing copy matches this one." :
                !launchingVersion.Equals(runningFileVersion) ? "The existing copy's executable file has a different version." :
                "The existing copy is running from a different folder.";
            return "Another Snip Studio copy is already running. This launch has not been sent to it.\n\n" +
                "Copy you opened:\n" + (opened ?? launchingPath ?? "Location unavailable") + "\nVersion " +
                (launchingVersion == null ? "unavailable" : launchingVersion.ToString()) + "\n\n" +
                "Existing copy:\n" + (existing ?? runningPath ?? "Location unavailable") + "\nExecutable file version " +
                (runningFileVersion == null ? "unavailable" : runningFileVersion.ToString()) + "\n\n" + reason + "\n\n" +
                "Save your snips and stop any recording in the existing copy. Choose More > Exit Snip Studio, " +
                "or Exit from its notification-area icon. Then open the copy you want again. " +
                "Closing an editor window alone may leave its shortcut listener running.\n\n" +
                "Your existing session has not been closed.";
        }

        private static string NormalizePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            bool drive = value.Length >= 3 && Char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
            bool unc = value.StartsWith(@"\\", StringComparison.Ordinal);
            if (!drive && !unc) return null;
            try { return Path.GetFullPath(value); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }
    }

    // This window belongs to the application context, never to an editor. Keep it
    // alive while editors exist so a second launch always reaches the same process.
    // All instance members must be used on the creating UI thread.
    internal sealed class ShortcutHost : IDisposable
    {
        private const int WmCopyData = 0x004A, WmHotkey = 0x0312, WmDispatch = 0x8000 + 0x53;
        private const int PrintScreenId = 0x5351, CaptureId = 0x5352;
        private const int CopyDataMagic = 0x53534831, Acknowledged = 0x5353;
        private const int MaxRequestBytes = 65536, MaxQueuedRequests = 16;
        private static readonly IntPtr MessageOnly = new IntPtr(-3);
        private static readonly object PrimaryLock = new object();
        private static ShortcutHost processPrimary;
        private static readonly object ForwardLock = new object();
        private static readonly Dictionary<string, string> ForwardIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;
        private readonly Queue<ShortcutStartupRequest> pending = new Queue<ShortcutStartupRequest>();
        private readonly Dictionary<string, string> received = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> receivedOrder = new Queue<string>();
        private Mutex instanceMutex;
        private bool ownsMutex, disposed, idleEnabled, dispatchPosted, idleDispatchPending, printScreenRegistered, captureRegistered;
        private BrokerWindow window;
        private NotifyIcon trayIcon;
        private Icon trayImage;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem printScreenState, captureState;
        private string printScreenStatus = "Print Screen: off", captureStatus = "Ctrl+Alt+S: off";

        internal event Action<string> OpenRequested;
        internal event Action CaptureRequested;
        internal event Action ExitRequested;

        internal bool IsPrimary { get { return ownsMutex && !disposed; } }
        internal bool PrintScreenRegistered { get { return printScreenRegistered; } }
        internal bool CaptureShortcutRegistered { get { return captureRegistered; } }
        internal string ShortcutStatus { get { return printScreenStatus + ". " + captureStatus + "."; } }

        internal ShortcutHost()
        {
            lock (PrimaryLock)
            {
                if (processPrimary != null && processPrimary.IsPrimary) return;
                instanceMutex = new Mutex(false, MutexName);
                try
                {
                    try { ownsMutex = instanceMutex.WaitOne(0, false); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex) { instanceMutex.Dispose(); instanceMutex = null; return; }
                    window = new BrokerWindow(this);
                    window.CreateBroker(BrokerCaption);
                    processPrimary = this;
                }
                catch
                {
                    if (window != null) { window.DestroyBroker(); window = null; }
                    ReleaseMutex();
                    throw;
                }
            }
        }

        private static string UserIdentity
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity.User == null) throw new InvalidOperationException("Windows did not provide the current user identity.");
                    return identity.User.Value;
                }
            }
        }
        private static string MutexName { get { return @"Local\AutomationForge.SnipStudio.ShortcutHost.v1." + UserIdentity; } }
        private static string BrokerCaption { get { return "AutomationForge.SnipStudio.ShortcutHost.v1." + UserIdentity; } }

        // Only an explicit ACK means the existing process accepted the request.
        // Discovery retries are limited to a startup race where a mutex already
        // exists. A normal first launch does not wait for a nonexistent process.
        internal static ShortcutForwardResult TryForward(string[] args, out string startupProblem)
        {
            startupProblem = null;
            ShortcutStartupRequest request;
            if (!ShortcutStartupRequest.TryFromArguments(args, out request)) return ShortcutForwardResult.NotForwarded;
            string body = request.ToBody(), id;
            lock (ForwardLock)
            {
                // A retry after an uncertain timeout uses the same ID. The receiver
                // acknowledges it again without opening a second editor/capture.
                if (!ForwardIds.TryGetValue(body, out id))
                {
                    id = Guid.NewGuid().ToString("N");
                    if (ForwardIds.Count >= MaxQueuedRequests) ForwardIds.Clear();
                    ForwardIds.Add(body, id);
                }
            }
            byte[] bytes = Encoding.Unicode.GetBytes(id + "\n" + body + "\0");
            if (bytes.Length > MaxRequestBytes) return ShortcutForwardResult.NotForwarded;
            string caption = BrokerCaption;
            IntPtr target = FindWindowEx(MessageOnly, IntPtr.Zero, null, caption);
            if (target == IntPtr.Zero)
            {
                Mutex existing;
                try { if (!Mutex.TryOpenExisting(MutexName, out existing)) return ShortcutForwardResult.NotForwarded; }
                catch (UnauthorizedAccessException) { return ShortcutForwardResult.NotForwarded; }
                using (existing)
                {
                    Stopwatch wait = Stopwatch.StartNew();
                    while (target == IntPtr.Zero && wait.ElapsedMilliseconds < 300)
                    {
                        Thread.Sleep(25);
                        target = FindWindowEx(MessageOnly, IntPtr.Zero, null, caption);
                    }
                }
                if (target == IntPtr.Zero) return ShortcutForwardResult.NotForwarded;
            }
            uint recipientProcess;
            if (GetWindowThreadProcessId(target, out recipientProcess) == 0 || recipientProcess == 0)
                return ShortcutForwardResult.NotForwarded;
            Assembly launchingAssembly = Assembly.GetExecutingAssembly();
            string runningPath = null, verificationError = null;
            Version runningFileVersion = null;
            try
            {
                using (Process running = Process.GetProcessById(checked((int)recipientProcess)))
                {
                    runningPath = running.MainModule.FileName;
                    string versionText = FileVersionInfo.GetVersionInfo(runningPath).FileVersion;
                    if (!Version.TryParse(versionText, out runningFileVersion))
                        verificationError = "The existing executable's file version is unavailable.";
                    if (running.HasExited) return ShortcutForwardResult.NotForwarded;
                }
            }
            catch (System.ComponentModel.Win32Exception) { verificationError = "Windows could not read the existing executable."; }
            catch (InvalidOperationException) { return ShortcutForwardResult.NotForwarded; }
            catch (ArgumentException) { return ShortcutForwardResult.NotForwarded; }
            catch (UnauthorizedAccessException) { verificationError = "Windows denied access to the existing executable."; }
            catch (System.Security.SecurityException) { verificationError = "Windows denied access to the existing executable."; }
            catch (IOException) { verificationError = "The existing executable could not be read."; }
            catch (NotSupportedException) { verificationError = "Windows could not inspect the existing executable."; }
            catch (OverflowException) { verificationError = "Windows could not inspect the existing process."; }
            startupProblem = ShortcutHostCompatibility.Check(launchingAssembly.Location, launchingAssembly.GetName().Version,
                runningPath, runningFileVersion, verificationError);
            if (startupProblem != null) return ShortcutForwardResult.IncompatibleHost;
            IntPtr data = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, data, bytes.Length);
                CopyData packet = new CopyData { Magic = new IntPtr(CopyDataMagic), ByteCount = bytes.Length, Data = data };
                // Pin the recipient across metadata inspection; a destroyed and
                // reused broker HWND must not receive this launch request.
                uint currentRecipient;
                if (GetWindowThreadProcessId(target, out currentRecipient) == 0 || currentRecipient != recipientProcess)
                    return ShortcutForwardResult.NotForwarded;
                if (request.Kind != ShortcutStartupRequest.RequestKind.Background) AllowSetForegroundWindow(recipientProcess);
                UIntPtr result;
                IntPtr delivered = SendMessageTimeout(target, WmCopyData, IntPtr.Zero, ref packet,
                    0x0001 | 0x0002 | 0x0020, 700, out result); // BLOCK | ABORTIFHUNG | ERRORONEXIT
                return delivered != IntPtr.Zero && result.ToUInt64() == Acknowledged ?
                    ShortcutForwardResult.Forwarded : ShortcutForwardResult.NotForwarded;
            }
            finally { Marshal.FreeHGlobal(data); }
        }

        // Release these before constructing/showing an editor, whose existing
        // hotkey code then owns the keys. Enabling is only for zero editor windows.
        internal void SetIdleShortcuts(bool enabled, bool usePrintScreen)
        {
            VerifyThread();
            if (disposed) throw new ObjectDisposedException("ShortcutHost");
            if (!IsPrimary) return;
            ReleaseHotkeys();
            idleEnabled = enabled;
            printScreenStatus = "Print Screen: off";
            captureStatus = "Ctrl+Alt+S: off";
            if (!enabled) { RemoveTray(); return; }
            captureRegistered = RegisterHotKey(window.Handle, CaptureId, 0x4000 | 0x0001 | 0x0002, (uint)Keys.S);
            int captureError = captureRegistered ? 0 : Marshal.GetLastWin32Error();
            captureStatus = captureRegistered ? "Ctrl+Alt+S: registered" : "Ctrl+Alt+S: unavailable (Windows error " + captureError + ")";
            if (usePrintScreen)
            {
                printScreenRegistered = RegisterHotKey(window.Handle, PrintScreenId, 0x4000, 0x2C);
                int printError = printScreenRegistered ? 0 : Marshal.GetLastWin32Error();
                printScreenStatus = printScreenRegistered ? "Print Screen: registered" : "Print Screen: unavailable (Windows error " + printError + ")";
            }
            EnsureTray();
            printScreenState.Text = printScreenStatus;
            printScreenState.ToolTipText = printScreenRegistered ?
                "Windows can still assign Print Screen to Snipping Tool. Review Windows keyboard settings if needed." :
                "Use New snip, Ctrl+Alt+S, or review Windows keyboard settings. Snip Studio does not change Windows key assignments.";
            captureState.Text = captureStatus;
            trayIcon.Text = usePrintScreen && !printScreenRegistered ? "Snip Studio - Print Screen unavailable" :
                !captureRegistered ? "Snip Studio - Ctrl+Alt+S unavailable" : "Snip Studio - shortcuts ready";
            trayIcon.Visible = true;
        }

        private void EnsureTray()
        {
            if (trayIcon != null) return;
            trayMenu = new ContextMenuStrip();
            trayMenu.ShowImageMargin = false;
            trayMenu.Items.Add("New snip", null, delegate { QueueRequest(ShortcutStartupRequest.Capture()); });
            trayMenu.Items.Add("Open Snip Studio", null, delegate { QueueRequest(ShortcutStartupRequest.Open(null)); });
            trayMenu.Items.Add(new ToolStripSeparator());
            printScreenState = new ToolStripMenuItem();
            printScreenState.Enabled = false;
            captureState = new ToolStripMenuItem();
            captureState.Enabled = false;
            trayMenu.Items.Add(printScreenState);
            trayMenu.Items.Add(captureState);
            trayMenu.Items.Add("Windows keyboard settings...", null, delegate { OpenKeyboardSettings(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit Snip Studio", null, delegate { if (ExitRequested != null) ExitRequested(); });
            trayImage = LoadTrayIcon();
            trayIcon = new NotifyIcon { Icon = trayImage, Text = "Snip Studio", ContextMenuStrip = trayMenu };
            trayIcon.DoubleClick += delegate { QueueRequest(ShortcutStartupRequest.Open(null)); };
        }

        private static Icon LoadTrayIcon()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SnipStudio.AppIcon"))
            {
                if (stream != null)
                {
                    using (Icon icon = new Icon(stream)) return (Icon)icon.Clone();
                }
            }
            return (Icon)SystemIcons.Application.Clone();
        }

        private static void OpenKeyboardSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show("Windows keyboard settings could not be opened.\n\n" + ex.Message,
                    "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private bool QueueRequest(ShortcutStartupRequest request)
        {
            if (disposed || window == null || pending.Count >= MaxQueuedRequests) return false;
            pending.Enqueue(request);
            ScheduleDispatch();
            return true;
        }

        private void ScheduleDispatch()
        {
            if (dispatchPosted || idleDispatchPending || disposed) return;
            dispatchPosted = PostMessage(window.Handle, WmDispatch, IntPtr.Zero, IntPtr.Zero);
            if (dispatchPosted) return;
            // A full Windows message queue must not strand requests already
            // acknowledged. Idle dispatch is still deferred beyond WM_COPYDATA.
            idleDispatchPending = true;
            Application.Idle += DispatchWhenIdle;
        }

        private void DispatchWhenIdle(object sender, EventArgs args)
        {
            Application.Idle -= DispatchWhenIdle;
            idleDispatchPending = false;
            DispatchOne();
        }

        private bool AcceptCopyData(IntPtr pointer)
        {
            if (disposed || pointer == IntPtr.Zero) return false;
            CopyData packet = (CopyData)Marshal.PtrToStructure(pointer, typeof(CopyData));
            if (packet.Magic != new IntPtr(CopyDataMagic) || packet.Data == IntPtr.Zero ||
                packet.ByteCount < 2 || packet.ByteCount > MaxRequestBytes || (packet.ByteCount & 1) != 0) return false;
            byte[] bytes = new byte[packet.ByteCount];
            Marshal.Copy(packet.Data, bytes, 0, bytes.Length);
            string id, body;
            ShortcutStartupRequest request;
            if (!TryDecodeRequest(bytes, out id, out body, out request)) return false;
            string acceptedBody;
            if (received.TryGetValue(id, out acceptedBody)) return String.Equals(acceptedBody, body, StringComparison.Ordinal);
            // Background/autostart forwarding is deliberately a no-op, including
            // while a visible editor is open.
            if (request.Kind != ShortcutStartupRequest.RequestKind.Background && !QueueRequest(request)) return false;
            received.Add(id, body);
            receivedOrder.Enqueue(id);
            if (receivedOrder.Count > 64) received.Remove(receivedOrder.Dequeue());
            return true;
        }

        // Pure envelope validation for checks; the native reader validates magic
        // and pointer/length before copying into this bounded managed buffer.
        internal static bool TryDecodeRequest(byte[] bytes, out string id, out string body, out ShortcutStartupRequest request)
        {
            id = body = null;
            request = null;
            if (bytes == null || bytes.Length < 2 || bytes.Length > MaxRequestBytes || (bytes.Length & 1) != 0) return false;
            if (bytes[bytes.Length - 2] != 0 || bytes[bytes.Length - 1] != 0) return false;
            string text;
            try { text = new UnicodeEncoding(false, false, true).GetString(bytes, 0, bytes.Length - 2); }
            catch (DecoderFallbackException) { return false; }
            if (text.IndexOf('\0') >= 0 || text.Length < 34 || text[32] != '\n') return false;
            string candidateId = text.Substring(0, 32), candidateBody = text.Substring(33);
            Guid requestId;
            if (!Guid.TryParseExact(candidateId, "N", out requestId) || !ShortcutStartupRequest.TryParseBody(candidateBody, out request)) return false;
            id = candidateId;
            body = candidateBody;
            return true;
        }

        private void DispatchOne()
        {
            dispatchPosted = false;
            if (disposed || pending.Count == 0) return;
            ShortcutStartupRequest request = pending.Dequeue();
            // Post the next request before invoking application code, which can
            // enter a modal loop. No construction occurs during WM_COPYDATA.
            if (pending.Count != 0) ScheduleDispatch();
            if (request.Kind == ShortcutStartupRequest.RequestKind.Capture)
            {
                if (CaptureRequested != null) CaptureRequested();
            }
            else if (request.Kind == ShortcutStartupRequest.RequestKind.Open && OpenRequested != null) OpenRequested(request.ImagePath);
        }

        private void ReleaseHotkeys()
        {
            if (window != null)
            {
                if (printScreenRegistered) UnregisterHotKey(window.Handle, PrintScreenId);
                if (captureRegistered) UnregisterHotKey(window.Handle, CaptureId);
            }
            printScreenRegistered = captureRegistered = false;
        }

        private void RemoveTray()
        {
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
            if (trayImage != null) { trayImage.Dispose(); trayImage = null; }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
            printScreenState = captureState = null;
        }

        private void VerifyThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != uiThreadId)
                throw new InvalidOperationException("The shortcut listener must be used on its application UI thread.");
        }

        private void ReleaseMutex()
        {
            if (instanceMutex == null) return;
            if (ownsMutex) { instanceMutex.ReleaseMutex(); ownsMutex = false; }
            instanceMutex.Dispose();
            instanceMutex = null;
        }

        public void Dispose()
        {
            VerifyThread();
            if (disposed) return;
            disposed = true;
            idleEnabled = false;
            if (idleDispatchPending) { Application.Idle -= DispatchWhenIdle; idleDispatchPending = false; }
            ReleaseHotkeys();
            RemoveTray();
            pending.Clear();
            if (window != null) { window.DestroyBroker(); window = null; }
            lock (PrimaryLock)
            {
                if (ReferenceEquals(processPrimary, this)) processPrimary = null;
                ReleaseMutex();
            }
        }

        private sealed class BrokerWindow : NativeWindow
        {
            private readonly ShortcutHost owner;
            internal BrokerWindow(ShortcutHost owner) { this.owner = owner; }
            internal void CreateBroker(string caption)
            {
                CreateHandle(new CreateParams { Caption = caption, Parent = MessageOnly });
            }
            internal void DestroyBroker() { if (Handle != IntPtr.Zero) DestroyHandle(); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == WmCopyData)
                {
                    message.Result = owner.AcceptCopyData(message.LParam) ? new IntPtr(Acknowledged) : IntPtr.Zero;
                    return;
                }
                if (message.Msg == WmDispatch) { owner.DispatchOne(); return; }
                if (message.Msg == WmHotkey)
                {
                    int id = message.WParam.ToInt32();
                    if (owner.idleEnabled && ((id == PrintScreenId && owner.printScreenRegistered) ||
                        (id == CaptureId && owner.captureRegistered))) owner.QueueRequest(ShortcutStartupRequest.Capture());
                    return;
                }
                base.WndProc(ref message);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyData { internal IntPtr Magic; internal int ByteCount; internal IntPtr Data; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string caption);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, ref CopyData lParam,
            uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }

    // Pure command/payload parsing, also usable by focused checks without creating
    // a window or registering a key. IPC only ever opens an image; never a command.
    internal sealed class ShortcutStartupRequest
    {
        internal enum RequestKind { Open, Capture, Background }
        internal RequestKind Kind { get; private set; }
        internal string ImagePath { get; private set; }
        private ShortcutStartupRequest(RequestKind kind, string imagePath) { Kind = kind; ImagePath = imagePath; }
        internal static ShortcutStartupRequest Open(string imagePath) { return new ShortcutStartupRequest(RequestKind.Open, imagePath); }
        internal static ShortcutStartupRequest Capture() { return new ShortcutStartupRequest(RequestKind.Capture, null); }

        internal static bool TryFromArguments(string[] args, out ShortcutStartupRequest request)
        {
            request = null;
            if (args == null || args.Length == 0) { request = Open(null); return true; }
            if (args.Length != 1 || args[0] == null) return false;
            string value = args[0];
            if (String.Equals(value, "--background", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(value, "--shortcut-listener", StringComparison.OrdinalIgnoreCase))
                request = new ShortcutStartupRequest(RequestKind.Background, null);
            else if (String.Equals(value, "--capture", StringComparison.OrdinalIgnoreCase)) request = Capture();
            else
            {
                string path;
                if (!TryImagePath(value, out path)) return false;
                request = Open(path);
            }
            return true;
        }

        internal string ToBody()
        {
            return Kind == RequestKind.Background ? "BACKGROUND" : Kind == RequestKind.Capture ? "CAPTURE" :
                ImagePath == null ? "OPEN" : "IMAGE\n" + ImagePath;
        }

        internal static bool TryParseBody(string body, out ShortcutStartupRequest request)
        {
            request = null;
            if (body == "BACKGROUND") request = new ShortcutStartupRequest(RequestKind.Background, null);
            else if (body == "CAPTURE") request = Capture();
            else if (body == "OPEN") request = Open(null);
            else if (body != null && body.StartsWith("IMAGE\n", StringComparison.Ordinal))
            {
                string path;
                if (!TryImagePath(body.Substring(6), out path)) return false;
                request = Open(path);
            }
            return request != null;
        }

        private static bool TryImagePath(string value, out string path)
        {
            path = null;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 32700 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                value.IndexOfAny(new char[] { '*', '?', '"' }) >= 0 ||
                value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0 || value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                value.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            // Path.IsPathRooted also accepts drive-relative/root-relative strings.
            // Require a drive with slash, or a complete UNC server/share instead.
            bool drive = value.Length >= 3 && Char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
            bool unc = value.StartsWith(@"\\", StringComparison.Ordinal);
            if (!drive && !unc) return false;
            if (unc)
            {
                string[] segments = value.Substring(2).Split(new char[] { '\\', '/' });
                if (segments.Length < 3 || String.IsNullOrWhiteSpace(segments[0]) || String.IsNullOrWhiteSpace(segments[1])) return false;
            }
            try
            {
                string fullPath = Path.GetFullPath(value);
                if (fullPath.IndexOf(':', drive ? 2 : 0) >= 0) return false; // Reject alternate data streams.
                string extension = Path.GetExtension(fullPath).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp" &&
                    extension != ".gif" && extension != ".tif" && extension != ".tiff") return false;
                path = fullPath;
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
    }
}
