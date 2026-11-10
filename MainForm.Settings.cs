using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private Timer automaticCopyTimer;
        private long lastDocumentRevision = -1;
        private Size lastSnipSize = Size.Empty;
        private string capturePolicyMessage;
        private string externalWorkingCopy;
        private string lastRecordingPath;
        private ContextMenuStrip moreMenu;
        private bool positioningMoreMenu;
        private ToolStripMenuItem reloadExternalItem;
        private bool preparingShare;
        private bool refreshingTaskbarMenu;
        private static SettingsForm sharedSettings;

        private bool ShouldAskToSaveEdits
        {
            get { return String.IsNullOrEmpty(currentPath) || preferences.AskSaveEditedScreenshots; }
        }

        private Control BuildSettingsButton()
        {
            StudioButton more = ActionButton("More", "more", 34, ShowMoreMenu);
            more.IconOnly = true;
            more.AccessibleName = "More actions";
            tips.SetToolTip(more, "More actions, Print, Settings, Help and About");
            moreMenu = new ContextMenuStrip();
            TooltipService.RegisterMenu(moreMenu);
            moreMenu.Renderer = new StudioMenuRenderer();
            StudioTheme.StyleMenu(moreMenu);
            moreMenu.BackColor = StudioTheme.Surface;
            moreMenu.ForeColor = StudioTheme.Text;
            moreMenu.Opened += delegate { RepositionMoreMenu(); };
            moreMenu.SizeChanged += delegate { RepositionMoreMenu(); };
            ToolStripMenuItem zoom = new ToolStripMenuItem("Zoom");
            zoom.DropDownItems.Add("Zoom in", null, delegate { canvas.ZoomIn(); });
            zoom.DropDownItems.Add("Zoom out", null, delegate { canvas.ZoomOut(); });
            zoom.DropDownItems.Add("Fit to window", null, delegate { canvas.FitToWindow(); });
            zoom.DropDownItems.Add("Actual size (100%)", null, delegate { canvas.ActualSize(); });
            moreMenu.Items.Add(zoom);
            ToolStripMenuItem shareItem = AddMenuItem("Share…", "copy", delegate { ShareCurrentImage(); });
            AddMenuItem("Open file…", "open", delegate { ChooseImage(); });
            AddMenuItem("Full-screen capture (choose monitor)", "camera", delegate { SetCaptureKind(false); mode.SelectedIndex = 3; StartCapture(); });
            AddMenuItem("Capture all monitors", "fullscreen", delegate { SetCaptureKind(false); mode.SelectedIndex = 5; StartCapture(); });
            AddMenuItem("Full-screen view                 F11", "fit", delegate { ToggleFullScreenEditor(); });
            AddMenuItem("Paste image", "paste", delegate { PasteImage(); });
            AddMenuItem("Open screenshots folder", "open", delegate { OpenScreenshotFolder(); });
            AddMenuItem("Open recordings folder", "open", delegate { OpenFolder(preferences.RecordingFolder); });
            AddMenuItem("Review a recording…", "record", delegate { ChooseRecordingForReview(); });
            AddMenuItem("Edit in application…", "pen", delegate { EditInApplication(); });
            reloadExternalItem = AddMenuItem("Reload external edit", "pen", delegate
            {
                if (File.Exists(externalWorkingCopy) && ConfirmReplace("loading the edited copy")) OpenImage(externalWorkingCopy);
            });
            moreMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Print…                       Ctrl+P", "print", delegate { PrintImage(); });
            AddMenuItem("Save as PDF…", "save", delegate { SaveImage(true, true); });
            ToolStripMenuItem refreshTaskbarItem = AddMenuItem("Refresh taskbar menu", "settings", delegate { RefreshTaskbarMenu(); });
            refreshTaskbarItem.ToolTipText = "Refresh Snip Studio's capture tasks, recent snips and existing Windows shortcuts.";
            checkUpdatesItem = AddMenuItem("Check for updates", "help", delegate { CheckForUpdates(false); });
            checkUpdatesItem.ToolTipText = "Check for a newer Snip Studio installer on the official GitHub release page.";
            AddMenuItem("Settings", "settings", delegate { ShowSettings(); });
            AddMenuItem("Help                           F1", "help", delegate { ShowHelp(); });
            AddMenuItem("About Snip Studio", "help", delegate { using (AboutForm about = new AboutForm()) about.ShowDialog(this); });
            moreMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Exit Snip Studio", "", delegate { ExitFromTray(); });
            moreMenu.Opening += delegate
            {
                StudioTheme.StyleMenu(moreMenu);
                moreMenu.BackColor = StudioTheme.Surface;
                moreMenu.ForeColor = StudioTheme.Text;
                reloadExternalItem.Enabled = !String.IsNullOrEmpty(externalWorkingCopy) && File.Exists(externalWorkingCopy);
                refreshTaskbarItem.Enabled = !refreshingTaskbarMenu;
                checkUpdatesItem.Enabled = !checkingForUpdates;
                shareItem.Enabled = canvas.HasImage && !capturePending && !preparingShare;
                foreach (ToolStripItem item in moreMenu.Items) item.ForeColor = StudioTheme.Text;
            };
            StudioTheme.StyleMenu(moreMenu);
            return more;
        }

        private async void RefreshTaskbarMenu()
        {
            if (refreshingTaskbarMenu || IsDisposed || closing) return;
            refreshingTaskbarMenu = true;
            SetStatus("Refreshing the Windows taskbar menu…");
            try
            {
                TaskbarJumpList.TaskbarRefreshResult result = await TaskbarJumpList.RefreshAsync();
                if (IsDisposed || closing) return;
                SetStatus(result.Message);
                if (!Visible && trayIcon != null && !hotkeysTrayDisposed)
                    trayIcon.ShowBalloonTip(4000, "Snip Studio taskbar menu", result.Message,
                        result.HasWarnings ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !closing) ShowFailure("The taskbar menu could not be refreshed", error);
            }
            finally { refreshingTaskbarMenu = false; }
        }

        private void ShowMoreMenu(object sender, EventArgs e)
        {
            Control anchor = sender as Control;
            if (anchor == null || anchor.IsDisposed || moreMenu == null || moreMenu.IsDisposed) return;
            Rectangle buttonBounds = anchor.RectangleToScreen(anchor.ClientRectangle);
            Rectangle work = Screen.FromControl(anchor).WorkingArea;
            Size popup = moreMenu.GetPreferredSize(Size.Empty);
            Point location = MenuPopupPlacement.Calculate(buttonBounds, popup, work);
            // The control-relative overload also supplies SourceControl so
            // WinForms uses this button's DPI and owner, never the global cursor.
            moreMenu.Show(anchor, anchor.PointToClient(location), ToolStripDropDownDirection.BelowRight);
        }

        private void RepositionMoreMenu()
        {
            if (positioningMoreMenu || moreMenu == null || moreMenu.IsDisposed || !moreMenu.Visible) return;
            if (moreMenu.Width < 1 || moreMenu.Height < 1) return;
            Control anchor = moreMenu.SourceControl;
            if (anchor == null || anchor.IsDisposed) return;
            positioningMoreMenu = true;
            try
            {
                // The first Show can resize for a different DPI or menu font.
                // Clamp that actual size when opened and after any later menu
                // sizing, using the current owner monitor.
                Point location = MenuPopupPlacement.Calculate(anchor.RectangleToScreen(anchor.ClientRectangle),
                    moreMenu.Size, Screen.FromControl(anchor).WorkingArea);
                if (moreMenu.Location != location) moreMenu.Location = location;
            }
            finally { positioningMoreMenu = false; }
        }

        private ToolStripMenuItem AddMenuItem(string label, string glyph, EventHandler action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label, String.IsNullOrEmpty(glyph) ? null : IconArtwork.CreateToolIcon(glyph, 18, StudioTheme.Text), action);
            if (!String.IsNullOrEmpty(glyph)) item.Tag = "glyph:" + glyph;
            moreMenu.Items.Add(item);
            return item;
        }

        private void InitializeSettingsIntegration()
        {
            automaticCopyTimer = new Timer();
            automaticCopyTimer.Interval = 180;
            automaticCopyTimer.Tick += async delegate
            {
                automaticCopyTimer.Stop();
                if (preferences.AutoCopyScreenshotChanges && canvas.HasImage && !IsDisposed)
                {
                    try { await CopySnapshotToClipboardAsync(true); }
                    catch (Exception) { if (!IsDisposed && !Disposing) SetStatus("Your edit is kept. Automatic copy could not access the clipboard; use Copy image to retry."); }
                }
            };
            ApplyCurrentPreferences();
            InitializeHotkeysAndTray();
        }

        private void ApplyCurrentPreferences()
        {
            StudioTheme.SetTheme(preferences.Theme);
            TooltipService.SetEnabled(preferences.ShowTooltips);
            TooltipService.RegisterProvider(tips);
            StudioTheme.ApplyTo(this);
            if (IsHandleCreated) ApplyDarkCaption(this);
            CaptureService.UseHdrColorCorrection = preferences.HdrColorCorrection;
            CaptureService.HideTaskbarsDuringCapture = preferences.HideTaskbarsDuringCapture;
            CaptureService.KeepPrimaryTaskbarVisible = preferences.KeepPrimaryTaskbarVisible;
            canvas.RequireCtrlForWheelZoom = preferences.RequireCtrlForWheelZoom;
            recordMicrophone.Checked = preferences.DefaultMicrophone;
            recordSystemAudio.Checked = preferences.DefaultSystemAudio;
            if (!layoutTestMode) ApplyAudioSourcePreferences();
            UpdateExternalEditorButton();
            if (!canvas.HasImage && !IsFullScreenEditor)
            {
                MinimumSize = new Size(500, 230);
                if (preferences.StartCompact) Size = new Size(540, 260);
                else Size = new Size(1220, 820);
            }
            LayoutChrome();
        }

        private void ShowSettings()
        {
            if (sharedSettings != null && !sharedSettings.IsDisposed && !sharedSettings.Disposing)
            {
                if (sharedSettings.WindowState == FormWindowState.Minimized) sharedSettings.WindowState = FormWindowState.Normal;
                if (!sharedSettings.Visible) sharedSettings.Show();
                sharedSettings.BringToFront();
                sharedSettings.Activate();
                return;
            }

            // Keep the dialog's saved result separate from every editor window's
            // current capture and drawing choices while the modeless draft is open.
            Preferences saved = preferences.Clone();
            bool wasStartup = saved.StartupWithWindows;
            // The optional installer component can create this app's Run entry.
            // Read its current value so Settings reflects that explicit choice.
            try { wasStartup = saved.StartupWithWindows = StartupIntegration.GetEnabled(); }
            catch (InvalidOperationException) { }

            SettingsForm settings = new SettingsForm(saved);
            sharedSettings = settings;
            settings.FormClosed += delegate
            {
                if (ReferenceEquals(sharedSettings, settings)) sharedSettings = null;
                try
                {
                    if (settings.DialogResult == DialogResult.OK) ApplySavedSettings(saved, wasStartup);
                }
                finally
                {
                    Program.UntrackAuxiliaryWindow(settings);
                    settings.Dispose();
                }
            };
            settings.Disposed += delegate
            {
                if (ReferenceEquals(sharedSettings, settings)) sharedSettings = null;
            };
            try
            {
                // Settings has no editor owner: closing the editor that opened it
                // must keep both the draft and the application's UI loop alive.
                Program.TrackAuxiliaryWindow(settings);
                settings.Show();
            }
            catch (Exception error)
            {
                if (ReferenceEquals(sharedSettings, settings)) sharedSettings = null;
                Program.UntrackAuxiliaryWindow(settings);
                settings.Dispose();
                ShowSettingsFailure("Settings could not be opened", error);
            }
        }

        private static void ApplySavedSettings(Preferences saved, bool wasStartup)
        {
            Exception startupFailure = null;
            if (wasStartup != saved.StartupWithWindows)
            {
                try { StartupIntegration.SetEnabled(saved.StartupWithWindows); }
                catch (Exception error)
                {
                    saved.StartupWithWindows = wasStartup;
                    try { saved.Save(); } catch (Exception) { }
                    startupFailure = error;
                }
            }

            // Take a snapshot because theme and tray changes can dispatch window
            // events. No action below depends on the original editor surviving.
            List<MainForm> editors = new List<MainForm>();
            foreach (Form form in Application.OpenForms)
            {
                MainForm editor = form as MainForm;
                if (editor != null && !editor.IsDisposed && !editor.Disposing && !editor.closing) editors.Add(editor);
            }
            Program.RememberShortcutPreferences(saved);
            foreach (MainForm editor in editors)
            {
                if (editor.IsDisposed || editor.Disposing || editor.closing) continue;
                int captureMode = editor.preferences.CaptureModeIndex;
                int captureDelay = editor.preferences.DelaySeconds;
                int ink = editor.preferences.InkArgb;
                int stroke = editor.preferences.StrokeWidth;
                editor.preferences.CopyFrom(saved);
                editor.preferences.CaptureModeIndex = captureMode;
                editor.preferences.DelaySeconds = captureDelay;
                editor.preferences.InkArgb = ink;
                editor.preferences.StrokeWidth = stroke;
                editor.ApplyCurrentPreferences();
                editor.ConfigureTrayIcon();
                editor.ConfigurePrintScreenShortcut();
                editor.SetStatus(saved.UsePrintScreen ? "Settings saved. " + editor.PrintScreenShortcutStatus : "Settings saved.");
            }
            if (startupFailure != null)
                ShowSettingsFailure("Windows startup could not be changed. The previous startup choice was kept", startupFailure);
        }

        private static void ShowSettingsFailure(string title, Exception error)
        {
            foreach (Form form in Application.OpenForms)
            {
                MainForm editor = form as MainForm;
                if (editor == null || editor.IsDisposed || editor.Disposing || editor.closing || !editor.Visible) continue;
                editor.ShowFailure(title, error);
                return;
            }
            MessageBox.Show(title + ".\n\n" + error.Message, "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnDocumentEdited()
        {
            if (layoutTestMode || !canvas.HasImage) return;
            if (lastDocumentRevision == canvas.DocumentRevision) return;
            lastDocumentRevision = canvas.DocumentRevision;
            if (lastSnipSize != canvas.ImageSize)
            {
                lastSnipSize = canvas.ImageSize;
                ResizeForSnip();
            }
            if (preferences.AutoCopyScreenshotChanges && automaticCopyTimer != null)
            {
                RequestAutomaticClipboardCopy();
                if (captureProcessingPending || clipboardCopyBusy) return;
                automaticCopyRequested = false;
                automaticCopyTimer.Stop();
                automaticCopyTimer.Start();
            }
        }

        private void ResizeForSnip()
        {
            if (!preferences.ResizeToSnip || !canvas.HasImage || WindowState == FormWindowState.Maximized || IsFullScreenEditor) return;
            Rectangle work = EditorWorkingArea();
            int maximumWidth = Math.Max(640, work.Width - 48);
            int maximumHeight = Math.Max(400, work.Height - 48);
            LayoutChrome();
            int chromeHeight = CompactChromeHeight + (Height - ClientSize.Height) + Ui(24);
            double fit = Math.Min(1.0, Math.Min((double)(maximumWidth - 64) / canvas.ImageSize.Width,
                (double)Math.Max(100, maximumHeight - chromeHeight) / canvas.ImageSize.Height));
            int minimumHeight = Math.Min(work.Height, Math.Max(Math.Min(Ui(380), maximumHeight), MinimumSize.Height));
            MinimumSize = new Size(Math.Min(Ui(780), maximumWidth), minimumHeight);
            Size = new Size(Math.Max(MinimumSize.Width, Math.Min(maximumWidth, (int)Math.Ceiling(canvas.ImageSize.Width * fit) + 64)),
                Math.Max(MinimumSize.Height, Math.Min(maximumHeight, (int)Math.Ceiling(canvas.ImageSize.Height * fit) + chromeHeight)));
            Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
            canvas.FitToWindow();
        }

        private void ApplyCapturePolicies(Bitmap image)
        {
            // Compatibility for callers that have not adopted the prepare/show/
            // begin sequence yet. New captures prepare their owned bitmap once.
            PrepareCaptureImage(image);
            canvas.SetImage(image);
            BeginCapturePolicies(image);
        }

        private bool ChooseRecordingDestination(out string destination)
        {
            destination = null;
            try
            {
                if (preferences.AutoSaveOriginalRecordings)
                {
                    Directory.CreateDirectory(preferences.RecordingFolder);
                    destination = Path.Combine(preferences.RecordingFolder, "Recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4");
                    return true;
                }
                using (SaveFileDialog picker = new SaveFileDialog())
                {
                    picker.Title = "Save screen recording";
                    picker.Filter = "MP4 video (*.mp4)|*.mp4";
                    picker.DefaultExt = "mp4";
                    picker.AddExtension = true;
                    picker.OverwritePrompt = true;
                    picker.FileName = "Recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                    if (Directory.Exists(preferences.RecordingFolder)) picker.InitialDirectory = preferences.RecordingFolder;
                    if (picker.ShowDialog(this) != DialogResult.OK) return false;
                    CaptureVisibilityScope.NotifyNativeDialogClosed();
                    destination = picker.FileName;
                    return true;
                }
            }
            catch (Exception error) { ShowFailure("The recording folder is unavailable. Choose another folder in Settings", error); return false; }
        }

        private void OnRecordingSaved(string path)
        {
            lastRecordingPath = path;
            if (preferences.AutoCopyRecordingChanges) CopyVideoToClipboard(path);
        }

        private static void CopyVideoToClipboard(string path)
        {
            RegisterClipboardIntent();
            StringCollection paths = new StringCollection();
            paths.Add(path);
            Clipboard.SetFileDropList(paths);
        }

        private void ChooseRecordingForReview()
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Open a recording";
                picker.Filter = "MP4 video|*.mp4";
                if (Directory.Exists(preferences.RecordingFolder)) picker.InitialDirectory = preferences.RecordingFolder;
                if (picker.ShowDialog(this) == DialogResult.OK)
                    using (VideoReviewForm review = new VideoReviewForm(picker.FileName, preferences)) review.ShowDialog(this);
            }
        }

        private void UpdateExternalEditorButton()
        {
            if (externalEditorButton == null) return;
            string name = String.IsNullOrWhiteSpace(preferences.EditorApplicationName) ? "app" : preferences.EditorApplicationName;
            externalEditorButton.Text = "Edit in " + name;
            tips.SetToolTip(externalEditorButton, "Open a working copy in " + name + ". Choose your editor in Settings.");
        }

        private void EditInApplication()
        {
            if (!canvas.HasImage || capturePending) return;
            if (String.IsNullOrWhiteSpace(preferences.EditorApplicationPath) || !File.Exists(preferences.EditorApplicationPath))
            {
                SetStatus("Choose an editing app in Settings, save, then choose Edit in application again.");
                ShowSettings();
                return;
            }
            try
            {
                using (Bitmap image = canvas.RenderImage()) externalWorkingCopy = ExternalEditorService.Open(image, preferences.EditorApplicationPath);
                SetStatus("Opened a working copy in " + preferences.EditorApplicationName + ". Use More → Reload external edit to bring saved changes back.");
            }
            catch (Exception error) { ShowFailure("The external editor could not be opened. Your snip is unchanged", error); }
        }

        private async void ShareCurrentImage()
        {
            if (!canvas.HasImage) { SetStatus("Capture or open a snip before sharing."); return; }
            if (capturePending || preparingShare) return;
            preparingShare = true;
            SetStatus("Preparing Windows Share…");
            try { using (Bitmap image = canvas.RenderImage()) await ShareService.ShowImageAsync(this, image); SetStatus("Windows Share opened. Choose a destination or close the panel."); }
            catch (Exception error) { ShowFailure("Windows sharing could not be opened. Your snip is unchanged", error); }
            finally { preparingShare = false; }
        }

        private void OpenScreenshotFolder() { OpenFolder(preferences.ScreenshotFolder); }

        private void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { ShowFailure("The folder could not be opened", error); }
        }

        private void DisposeSettingsIntegration()
        {
            DisposeCaptureProcessing();
            if (delayPopup != null) { delayPopup.Dispose(); delayPopup = null; }
            if (automaticCopyTimer != null) { automaticCopyTimer.Stop(); automaticCopyTimer.Dispose(); automaticCopyTimer = null; }
            if (moreMenu != null)
            {
                foreach (ToolStripItem item in moreMenu.Items) if (item.Image != null) item.Image.Dispose();
                moreMenu.Dispose();
                moreMenu = null;
            }
        }
    }

    internal sealed class StudioMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (Brush brush = new SolidBrush(StudioTheme.Surface)) e.Graphics.FillRectangle(brush, e.AffectedBounds); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using (Brush brush = new SolidBrush(e.Item.Selected ? StudioTheme.Raised : StudioTheme.Surface)) e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? StudioTheme.Text : StudioTheme.Muted; base.OnRenderItemText(e); }
    }
}
