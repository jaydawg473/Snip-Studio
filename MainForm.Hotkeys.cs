using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private const int PrintScreenHotkeyId = 0x5350;
        private static MainForm printScreenOwner;
        private static MainForm trayOwner;
        private bool printScreenRegistered, forceExit, hotkeysTrayDisposed;
        private bool printScreenWarningQueued, trayHintShown;
        private string printScreenShortcutStatus = "Print Screen has not been registered.";
        private NotifyIcon trayIcon;
        private Icon trayIconImage;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayShortcutState;

        private string PrintScreenShortcutStatus { get { return printScreenShortcutStatus; } }

        private bool CaptureShortcutRegisteredInApp
        {
            get
            {
                foreach (Form form in Application.OpenForms)
                {
                    MainForm main = form as MainForm;
                    if (main != null && !main.IsDisposed && (main.hotkeyRegistered || main.printScreenRegistered)) return true;
                }
                return hotkeyRegistered || printScreenRegistered;
            }
        }

        private void InitializeHotkeysAndTray()
        {
            if (IsDisposed || hotkeysTrayDisposed) return;
            ConfigureTrayIcon();
            ConfigurePrintScreenShortcut();
        }

        private void ConfigurePrintScreenShortcut()
        {
            if (IsDisposed || Disposing || hotkeysTrayDisposed) return;
            if (!preferences.UsePrintScreen)
            {
                if (printScreenOwner != null) printScreenOwner.ReleaseAdditionalHotkeys();
                ReleaseAdditionalHotkeys();
                printScreenShortcutStatus = "Print Screen is off. Use New snip or Ctrl+Alt+S.";
                RefreshTrayShortcutState();
                return;
            }
            if (!IsHandleCreated) return;
            if (printScreenOwner != null && !ReferenceEquals(printScreenOwner, this) &&
                !printScreenOwner.IsDisposed && printScreenOwner.printScreenRegistered)
            {
                printScreenShortcutStatus = "Print Screen is handled by another Snip Studio window.";
                RefreshTrayShortcutState();
                return;
            }
            if (printScreenRegistered) return;
            printScreenRegistered = RegisterHotKey(Handle, PrintScreenHotkeyId, 0x4000, 0x2C);
            if (printScreenRegistered)
            {
                printScreenOwner = this;
                printScreenWarningQueued = false;
                printScreenShortcutStatus = "Print Screen is registered. If Windows opens Snipping Tool, review Windows keyboard settings.";
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                printScreenShortcutStatus = "Print Screen could not be registered (Windows error " + error + "). Use New snip or Ctrl+Alt+S, or review Windows keyboard settings.";
                QueuePrintScreenWarning();
            }
            RefreshTrayShortcutState();
        }

        private void ReleaseAdditionalHotkeys()
        {
            if (printScreenRegistered && IsHandleCreated)
                UnregisterHotKey(Handle, PrintScreenHotkeyId);
            printScreenRegistered = false;
            if (ReferenceEquals(printScreenOwner, this)) printScreenOwner = null;
        }

        private bool HandleAdditionalHotkey(Message message)
        {
            if (message.Msg != 0x0312 || message.WParam.ToInt32() != PrintScreenHotkeyId) return false;
            if (printScreenRegistered && CanStartBackgroundCapture())
            {
                StartCapture();
            }
            return true;
        }

        private bool CanStartBackgroundCapture()
        {
            if (IsDisposed || closing || capturePending || recordingSession || !Enabled ||
                !IsHandleCreated || !IsWindowEnabled(Handle) || ModalOpenCount() != 0) return false;
            foreach (Form form in Application.OpenForms)
            {
                MainForm main = form as MainForm;
                if (main != null && (main.capturePending || main.recordingSession || main.closing)) return false;
            }
            return true;
        }

        private void ConfigureTrayIcon()
        {
            if (IsDisposed || hotkeysTrayDisposed) return;
            if (!preferences.KeepRunningInTray)
            {
                RemoveTrayIcon();
                return;
            }
            if (trayOwner != null && !ReferenceEquals(trayOwner, this) && !trayOwner.IsDisposed && trayOwner.trayIcon != null) return;
            trayOwner = this;
            if (trayIcon != null) { trayIcon.Visible = true; RefreshTrayAppearance(); RefreshTrayShortcutState(); return; }

            trayMenu = new ContextMenuStrip();
            TooltipService.RegisterMenu(trayMenu);
            trayMenu.BackColor = StudioTheme.Surface;
            trayMenu.ForeColor = StudioTheme.Text;
            trayMenu.ShowImageMargin = false;
            trayMenu.Renderer = new TrayMenuRenderer();
            trayMenu.Items.Add("New snip", null, delegate
            {
                if (!CanStartBackgroundCapture()) return;
                StartCapture();
            });
            trayMenu.Items.Add("Open Snip Studio", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("Settings…", null, delegate
            {
                if (capturePending || recordingSession || ModalOpenCount() != 0) return;
                ShowFromTray();
                ShowSettings();
            });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayShortcutState = new ToolStripMenuItem("Print Screen");
            trayShortcutState.Enabled = false;
            trayMenu.Items.Add(trayShortcutState);
            trayMenu.Items.Add("Windows keyboard settings…", null, delegate { OpenWindowsKeyboardSettings(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit Snip Studio", null, delegate { ExitFromTray(); });
            trayIcon = new NotifyIcon();
            trayIconImage = Icon != null ? (Icon)Icon.Clone() : (Icon)SystemIcons.Application.Clone();
            trayIcon.Icon = trayIconImage;
            trayIcon.Text = "Snip Studio";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
            trayIcon.BalloonTipClicked += delegate { ShowFromTray(); };
            trayIcon.Visible = true;
            RefreshTrayAppearance();
            RefreshTrayShortcutState();
        }

        private void ShowFromTray()
        {
            if (IsDisposed || closing) return;
            ShowInTaskbar = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        internal void ShowFromShortcut()
        {
            foreach (Form form in Application.OpenForms)
                if (form.Modal && form.Visible) { form.Activate(); return; }
            ShowFromTray();
        }

        internal void CaptureFromShortcut()
        {
            CaptureFromShortcut(false);
        }

        internal void CaptureFromShortcut(bool closeEmptyOnCancel)
        {
            if (!CanStartBackgroundCapture()) return;
            closeEmptyShortcutCapture = closeEmptyOnCancel && !Visible && !canvas.HasImage;
            StartCapture();
        }

        private bool TryHideToTray(FormClosingEventArgs e)
        {
            if (forceExit || !preferences.KeepRunningInTray || e.CloseReason != CloseReason.UserClosing ||
                IsDisposed || closing) return false;
            ConfigureTrayIcon();
            // A secondary snip closes normally, keeping its unsaved-image prompt. One
            // primary window and one notification icon own the background lifetime.
            if (!ReferenceEquals(trayOwner, this) || trayIcon == null) return false;
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            if (!trayHintShown)
            {
                trayHintShown = true;
                trayIcon.ShowBalloonTip(4500, "Snip Studio is still running",
                    "Your open snip is kept in memory. Use the tray icon to open it or exit.", ToolTipIcon.Info);
            }
            return true;
        }

        internal void ExitFromTray()
        {
            ExitFromTray(null);
        }

        internal void ExitFromTray(IEnumerable<MainForm> trackedEditors)
        {
            if (ModalOpenCount() != 0)
            {
                foreach (Form form in Application.OpenForms) if (form.Modal && form.Visible) { form.Activate(); break; }
                return;
            }
            Program.RequestApplicationExit();
            List<MainForm> windows = new List<MainForm>();
            if (trackedEditors != null)
                foreach (MainForm main in trackedEditors)
                    if (main != null && !main.IsDisposed && !windows.Contains(main)) windows.Add(main);
            // The context can own never-shown capture editors that have a
            // native handle but are not in Application.OpenForms yet.
            if (!IsDisposed && !windows.Contains(this)) windows.Add(this);
            foreach (Form form in Application.OpenForms)
            {
                MainForm main = form as MainForm;
                if (main != null && !main.IsDisposed && !windows.Contains(main)) windows.Add(main);
            }
            foreach (MainForm main in windows) main.forceExit = true;
            foreach (MainForm main in windows)
            {
                if (main.IsDisposed) continue;
                // Empty never-shown listener/capture editors need no visual
                // round-trip. Show any real work before its normal prompts.
                if (main.Visible || main.canvas.HasImage || main.recordingSession ||
                    main.scrollingCaptureActive || main.IsCaptureProcessingPending)
                    main.ShowFromTray();
                main.Close();
                // Close may remain pending while a video finalizes. A non-recording,
                // non-closing window means the user canceled an unsaved-work prompt.
                if (!main.IsDisposed && !main.recordingSession && !main.scrollingCaptureActive && !main.IsCaptureProcessingPending && !main.closing)
                {
                    foreach (MainForm remaining in windows) if (!remaining.IsDisposed) remaining.forceExit = false;
                    Program.CancelApplicationExit();
                    return;
                }
            }
        }

        private void QueuePrintScreenWarning()
        {
            if (printScreenWarningQueued || !IsHandleCreated) return;
            printScreenWarningQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || printScreenRegistered || !preferences.UsePrintScreen) return;
                    SetStatus(printScreenShortcutStatus);
                    if (!Visible && trayIcon != null)
                        trayIcon.ShowBalloonTip(6000, "Print Screen is unavailable",
                            "Use Ctrl+Alt+S or open Windows keyboard settings from the tray menu.", ToolTipIcon.Warning);
                });
            }
            catch (InvalidOperationException) { }
        }

        private void RefreshTrayShortcutState()
        {
            if (trayShortcutState != null)
            {
                trayShortcutState.Text = !preferences.UsePrintScreen ? "Print Screen: off" :
                    printScreenRegistered || (printScreenOwner != null && printScreenOwner.printScreenRegistered) ?
                    "Print Screen: registered" : "Print Screen: unavailable";
                trayShortcutState.ToolTipText = printScreenShortcutStatus;
            }
            if (trayIcon != null)
                trayIcon.Text = preferences.UsePrintScreen && !printScreenRegistered && printScreenOwner == null ?
                    "Snip Studio — Print Screen unavailable" : "Snip Studio";
        }

        private void RefreshTrayAppearance()
        {
            if (trayMenu == null) return;
            trayMenu.BackColor = StudioTheme.Surface;
            trayMenu.ForeColor = StudioTheme.Text;
            foreach (ToolStripItem item in trayMenu.Items) item.ForeColor = StudioTheme.Text;
        }

        private void OpenWindowsKeyboardSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
            catch (Exception ex) { ShowFailure("Windows keyboard settings could not be opened", ex); }
        }

        private void RemoveTrayIcon()
        {
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
            if (trayIconImage != null) { trayIconImage.Dispose(); trayIconImage = null; }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; trayShortcutState = null; }
            if (ReferenceEquals(trayOwner, this)) trayOwner = null;
        }

        private void DisposeHotkeysAndTray()
        {
            if (hotkeysTrayDisposed) return;
            hotkeysTrayDisposed = true;
            ReleaseAdditionalHotkeys();
            // Release the existing capture shortcut before giving a surviving window
            // a chance to own it. The base destroy hook then sees it already released.
            if (hotkeyRegistered && IsHandleCreated) { UnregisterHotKey(Handle, HotkeyId); hotkeyRegistered = false; }
            RemoveTrayIcon();
            foreach (Form form in Application.OpenForms)
            {
                MainForm main = form as MainForm;
                if (main == null || ReferenceEquals(main, this) || main.IsDisposed || main.Disposing || main.closing || !main.IsHandleCreated) continue;
                if (!main.hotkeyRegistered) main.hotkeyRegistered = RegisterHotKey(main.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, (uint)Keys.S);
                main.ConfigurePrintScreenShortcut();
                main.ConfigureTrayIcon();
            }
        }

        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                using (Brush brush = new SolidBrush(e.Item.Selected ? StudioTheme.Raised : StudioTheme.Surface))
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (Pen pen = new Pen(StudioTheme.Border))
                    e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2);
            }
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (Pen pen = new Pen(StudioTheme.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }
    }
}
