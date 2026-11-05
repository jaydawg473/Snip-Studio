using System;
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
        private bool applyingCapturePolicy;
        private string capturePolicyMessage;
        private string externalWorkingCopy;
        private string lastRecordingPath;
        private ContextMenuStrip moreMenu;
        private ToolStripMenuItem reloadExternalItem;
        private bool preparingShare;

        private bool ShouldAskToSaveEdits
        {
            get { return String.IsNullOrEmpty(currentPath) || preferences.AskSaveEditedScreenshots; }
        }

        private Control BuildSettingsButton()
        {
            StudioButton more = ActionButton("More", "more", 34, delegate { moreMenu.Show(Cursor.Position); });
            more.IconOnly = true;
            more.AccessibleName = "More actions";
            tips.SetToolTip(more, "More actions, Print, Settings, Help and About");
            moreMenu = new ContextMenuStrip();
            TooltipService.RegisterMenu(moreMenu);
            moreMenu.Renderer = new StudioMenuRenderer();
            StudioTheme.StyleMenu(moreMenu);
            moreMenu.BackColor = StudioTheme.Surface;
            moreMenu.ForeColor = StudioTheme.Text;
            ToolStripMenuItem zoom = new ToolStripMenuItem("Zoom");
            zoom.DropDownItems.Add("Zoom in", null, delegate { canvas.ZoomIn(); UpdateDocumentState(); });
            zoom.DropDownItems.Add("Zoom out", null, delegate { canvas.ZoomOut(); UpdateDocumentState(); });
            zoom.DropDownItems.Add("Fit to window", null, delegate { canvas.FitToWindow(); UpdateDocumentState(); });
            zoom.DropDownItems.Add("Actual size (100%)", null, delegate { canvas.ActualSize(); UpdateDocumentState(); });
            moreMenu.Items.Add(zoom);
            ToolStripMenuItem shareItem = AddMenuItem("Share…", "copy", delegate { ShareCurrentImage(); });
            AddMenuItem("Open file…", "open", delegate { ChooseImage(); });
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
                shareItem.Enabled = canvas.HasImage && !capturePending && !preparingShare;
                foreach (ToolStripItem item in moreMenu.Items) item.ForeColor = StudioTheme.Text;
            };
            StudioTheme.StyleMenu(moreMenu);
            return more;
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
            automaticCopyTimer.Tick += delegate
            {
                automaticCopyTimer.Stop();
                if (preferences.AutoCopyScreenshotChanges && canvas.HasImage && !IsDisposed)
                {
                    try { CopySnapshotToClipboard(); }
                    catch (Exception) { SetStatus("Your edit is kept. Automatic copy could not access the clipboard; use Copy image to retry."); }
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
            recordMicrophone.Checked = preferences.DefaultMicrophone;
            recordSystemAudio.Checked = preferences.DefaultSystemAudio;
            if (!layoutTestMode) ApplyAudioSourcePreferences();
            UpdateExternalEditorButton();
            if (!canvas.HasImage)
            {
                MinimumSize = new Size(500, 230);
                if (preferences.StartCompact) Size = new Size(540, 260);
                else Size = new Size(1220, 820);
            }
        }

        private void ShowSettings()
        {
            bool wasStartup = preferences.StartupWithWindows;
            using (SettingsForm settings = new SettingsForm(preferences))
                if (settings.ShowDialog(this) != DialogResult.OK) return;
            if (wasStartup != preferences.StartupWithWindows)
            {
                try { StartupIntegration.SetEnabled(preferences.StartupWithWindows); }
                catch (Exception error)
                {
                    preferences.StartupWithWindows = wasStartup;
                    try { preferences.Save(); } catch (Exception) { }
                    ShowFailure("Windows startup could not be changed. The previous startup choice was kept", error);
                }
            }
            ApplyCurrentPreferences();
            ConfigureTrayIcon();
            ConfigurePrintScreenShortcut();
            foreach (Form form in Application.OpenForms)
            {
                MainForm other = form as MainForm;
                if (other != null && other != this)
                {
                    int captureMode = other.preferences.CaptureModeIndex;
                    int captureDelay = other.preferences.DelaySeconds;
                    int ink = other.preferences.InkArgb;
                    int stroke = other.preferences.StrokeWidth;
                    other.preferences.CopyFrom(preferences);
                    other.preferences.CaptureModeIndex = captureMode;
                    other.preferences.DelaySeconds = captureDelay;
                    other.preferences.InkArgb = ink;
                    other.preferences.StrokeWidth = stroke;
                    other.ApplyCurrentPreferences();
                    other.ConfigureTrayIcon();
                    other.ConfigurePrintScreenShortcut();
                }
            }
            SetStatus(preferences.UsePrintScreen ? "Settings saved. " + PrintScreenShortcutStatus : "Settings saved.");
        }

        private void OnDocumentEdited()
        {
            if (layoutTestMode || !canvas.HasImage || applyingCapturePolicy) return;
            if (lastDocumentRevision == canvas.DocumentRevision) return;
            lastDocumentRevision = canvas.DocumentRevision;
            if (lastSnipSize != canvas.ImageSize)
            {
                lastSnipSize = canvas.ImageSize;
                ResizeForSnip();
            }
            if (preferences.AutoCopyScreenshotChanges && automaticCopyTimer != null)
            {
                automaticCopyTimer.Stop();
                automaticCopyTimer.Start();
            }
        }

        private void ResizeForSnip()
        {
            if (!preferences.ResizeToSnip || !canvas.HasImage || WindowState == FormWindowState.Maximized) return;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int maximumWidth = Math.Max(640, work.Width - 48);
            int maximumHeight = Math.Max(400, work.Height - 48);
            LayoutChrome();
            int chromeHeight = CompactChromeHeight + (Height - ClientSize.Height) + Ui(24);
            double fit = Math.Min(1.0, Math.Min((double)(maximumWidth - 64) / canvas.ImageSize.Width,
                (double)Math.Max(100, maximumHeight - chromeHeight) / canvas.ImageSize.Height));
            MinimumSize = new Size(Math.Min(Ui(780), maximumWidth), Math.Min(Ui(380), maximumHeight));
            Size = new Size(Math.Max(MinimumSize.Width, Math.Min(maximumWidth, (int)Math.Ceiling(canvas.ImageSize.Width * fit) + 64)),
                Math.Max(MinimumSize.Height, Math.Min(maximumHeight, (int)Math.Ceiling(canvas.ImageSize.Height * fit) + chromeHeight)));
            Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
            canvas.FitToWindow();
        }

        private void ApplyCapturePolicies(Bitmap image)
        {
            capturePolicyMessage = null;
            applyingCapturePolicy = true;
            try
            {
                if (preferences.AddScreenshotBorder)
                {
                    using (Bitmap bordered = new Bitmap(image))
                    using (Graphics graphics = Graphics.FromImage(bordered))
                    using (Pen pen = new Pen(Color.FromArgb(preferences.ScreenshotBorderArgb), Math.Max(1, preferences.ScreenshotBorderWidth)))
                    {
                        pen.Alignment = PenAlignment.Inset;
                        graphics.DrawRectangle(pen, 0, 0, bordered.Width - 1, bordered.Height - 1);
                        canvas.SetImage(bordered);
                    }
                }
                if (preferences.AutoSaveOriginalScreenshots)
                {
                    Directory.CreateDirectory(preferences.ScreenshotFolder);
                    string filename = Path.Combine(preferences.ScreenshotFolder, "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                    using (Bitmap original = canvas.RenderImage()) WritePngAtomic(original, filename);
                    currentPath = filename;
                    canvas.MarkSaved();
                    capturePolicyMessage = "Captured and original saved. Your edits stay separate until you save.";
                }
            }
            catch (Exception error)
            {
                capturePolicyMessage = "Captured. Automatic save failed; your snip is still open. Use Save to choose another folder.";
                MessageBox.Show(this, capturePolicyMessage + "\n\n" + error.Message, "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                applyingCapturePolicy = false;
                lastDocumentRevision = -1;
                OnDocumentEdited();
                UpdateDocumentState();
            }
        }

        private static void WritePngAtomic(Bitmap image, string filename)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(filename), ".snip-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                image.Save(temporary, ImageFormat.Png);
                if (File.Exists(filename)) File.Replace(temporary, filename, null);
                else File.Move(temporary, filename);
            }
            finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
        }

        private void CopySnapshotToClipboard()
        {
            using (Bitmap image = canvas.RenderImage())
            using (MemoryStream png = new MemoryStream())
            {
                image.Save(png, ImageFormat.Png);
                png.Position = 0;
                DataObject data = new DataObject();
                data.SetData(DataFormats.Bitmap, true, image);
                data.SetData("PNG", false, png);
                Clipboard.SetDataObject(data, true, 4, 100);
            }
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
                ShowSettings();
                if (String.IsNullOrWhiteSpace(preferences.EditorApplicationPath) || !File.Exists(preferences.EditorApplicationPath)) return;
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
