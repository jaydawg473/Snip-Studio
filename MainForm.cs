using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm : Form
    {
        private readonly EditorCanvas canvas = new EditorCanvas();
        private readonly Preferences preferences;
        private readonly ComboBox mode = new StudioComboBox();
        private readonly ComboBox delay = new StudioComboBox();
        private readonly ComboBox width = new StudioComboBox();
        private readonly Label status = new Label();
        private readonly Label dimensions = new Label();
        private readonly Label zoomLabel = new Label();
        private readonly Label toolGuidance = new Label();
        private readonly Label recognitionStatus = new Label();
        private readonly CheckBox snapToText = new CheckBox();
        private readonly PictureBox brandMark = new PictureBox();
        private Control editorToolShelf;
        private Control editorContextBar;
        private readonly ToolTip tips = new ToolTip();
        private readonly Dictionary<EditorTool, Button> toolButtons = new Dictionary<EditorTool, Button>();
        private readonly List<Control> documentControls = new List<Control>();
        private Button undoButton, redoButton, colorButton, newButton;
        private Button externalEditorButton;
        private string currentPath;
        private bool capturePending, hotkeyRegistered, closing;
        private bool closeDecisionInProgress;
        private bool closeEmptyShortcutCapture;
        private bool captureOpenSnipsRequested;
        private bool highlighterColor;
        private bool selectingCanvasTool;
        private readonly string initialPath;
        private readonly bool layoutTestMode;
        private const int HotkeyId = 0x534E;
        private static int? sessionCaptureDelay;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        public MainForm(string imagePath) : this(imagePath, false) { }

        internal MainForm(string imagePath, bool layoutTest)
        {
            layoutTestMode = layoutTest;
            initialPath = imagePath;
            preferences = layoutTest ? new Preferences { AutoCopyScreenshotChanges = false, AutoSaveOriginalScreenshots = false, KeepRunningInTray = false, UsePrintScreen = false } : Preferences.Load();
            TooltipService.RegisterProvider(tips);
            Text = "Snip Studio";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = layoutTest ? Size.Empty : new Size(500, 230);
            Size = new Size(540, 260);
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            TrySetAppIcon(this);
            KeyPreview = true;
            BuildLayout();
            canvas.ToolChanged += delegate { if (!selectingCanvasTool) SelectTool(canvas.Tool); };
            canvas.CopySelectedRecognizedTextRequested += delegate { CopyRecognizedText(false); };
            Resize += delegate { UpdateResponsiveChrome(); };
            mode.SelectedIndex = Math.Max(0, Math.Min(mode.Items.Count - 1, preferences.CaptureModeIndex));
            int initialDelay = !layoutTestMode && sessionCaptureDelay.HasValue ? sessionCaptureDelay.Value : preferences.RememberCaptureDelay ? preferences.DelaySeconds : 0;
            delay.SelectedIndex = initialDelay == 3 ? 1 : initialDelay == 5 ? 2 : initialDelay == 10 ? 3 : 0;
            int stroke = Array.IndexOf(new int[] { 2, 4, 8, 12, 20 }, preferences.StrokeWidth);
            width.SelectedIndex = stroke >= 0 ? stroke : 1;
            canvas.InkColor = Color.FromArgb(preferences.InkArgb);
            if (canvas.InkColor.A == 0) canvas.InkColor = Color.FromArgb(220, 38, 38);
            UpdateColorButton();
            SelectTool(EditorTool.Pen);
            canvas.DocumentChanged += delegate { UpdateDocumentState(); OnDocumentEdited(); };
            if (!layoutTestMode) InitializeSettingsIntegration();
            if (!layoutTestMode) InitializeUpdateIntegration();
            FormClosing += OnClosing;
            Shown += delegate
            {
                Rectangle working = Screen.FromControl(this).WorkingArea;
                if (Width > working.Width || Height > working.Height)
                    Bounds = new Rectangle(working.X + 20, working.Y + 20, Math.Min(Width, working.Width - 40), Math.Min(Height, working.Height - 40));
                if (!layoutTestMode && !String.IsNullOrEmpty(initialPath)) OpenImage(initialPath);
                UpdateDocumentState();
                if (!canvas.HasImage) SetStatus(hotkeyRegistered ? "Ready. New / Ctrl+N  •  Capture from any app: Ctrl+Alt+S" : "Ready. Use New or Ctrl+N. Global shortcut Ctrl+Alt+S is unavailable.");
            };
        }


        private static FlowLayoutPanel Bar(Padding padding)
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.WrapContents = true;
            panel.Padding = padding;
            panel.Margin = new Padding(0);
            panel.BackColor = StudioTheme.Background;
            return panel;
        }

        private FlowLayoutPanel ToolGroup(FlowLayoutPanel shelf, string title)
        {
            TableLayoutPanel group = new TableLayoutPanel();
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.RowCount = 2;
            group.ColumnCount = 1;
            group.Margin = new Padding(0, 0, title == "HISTORY" ? 0 : 18, 0);
            group.BackColor = StudioTheme.Surface;
            Label caption = new Label();
            caption.Text = title;
            caption.AutoSize = true;
            caption.Font = new Font(Font.FontFamily, 7.6f, FontStyle.Bold);
            caption.ForeColor = StudioTheme.Muted;
            caption.Margin = new Padding(4, 0, 0, 7);
            group.Controls.Add(caption, 0, 0);
            FlowLayoutPanel content = Bar(new Padding(0));
            content.Dock = DockStyle.None;
            content.WrapContents = false;
            content.BackColor = StudioTheme.Surface;
            group.Controls.Add(content, 0, 1);
            shelf.Controls.Add(group);
            return content;
        }

        private Label BarLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = new Padding(4, 11, 8, 0);
            label.ForeColor = StudioTheme.Muted;
            return label;
        }

        private void ConfigureCombo(ComboBox box, int size, object[] items)
        {
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.Width = size;
            box.Margin = new Padding(0, 4, 16, 3);
            box.Items.AddRange(items);
            box.AccessibleName = box == mode ? "Capture mode" : box == delay ? "Capture delay" : "Stroke width";
        }

        private StudioButton ActionButton(string text, string glyph, int size, EventHandler action)
        {
            StudioButton button = new StudioButton();
            button.Text = text;
            button.Glyph = glyph;
            button.Width = size;
            button.Height = 40;
            button.Margin = new Padding(2, 0, 2, 0);
            button.Font = new Font(Font.FontFamily, 9f);
            button.Cursor = Cursors.Hand;
            button.AccessibleName = text;
            button.Click += action;
            return button;
        }

        private void AddTool(FlowLayoutPanel panel, string label, EditorTool tool, string glyph, string help)
        {
            StudioButton button = ActionButton(label, glyph, tool == EditorTool.Highlighter ? 64 : 54, delegate { SelectTool(tool); });
            button.ToolLayout = true;
            button.Height = 64;
            button.Margin = new Padding(1, 0, 1, 0);
            toolButtons[tool] = button;
            panel.Controls.Add(button);
            documentControls.Add(button);
            tips.SetToolTip(button, help);
        }

        private void SelectTool(EditorTool tool)
        {
            canvas.TextActionsMode = false;
            selectingCanvasTool = true;
            try { canvas.Tool = tool; }
            finally { selectingCanvasTool = false; }
            // A stale/invalid inline draft is kept for correction rather than
            // changing the selected toolbar button without changing the tool.
            if (canvas.Tool != tool) return;
            foreach (KeyValuePair<EditorTool, Button> item in toolButtons)
            {
                bool selected = item.Key == tool;
                StudioButton button = item.Value as StudioButton;
                if (button != null) button.Selected = selected;
                item.Value.AccessibleDescription = selected ? "Selected tool" : "";
            }
            if (tool == EditorTool.Highlighter && !highlighterColor)
            {
                canvas.InkColor = Color.FromArgb(255, 211, 40);
                highlighterColor = true;
            }
            else if (tool != EditorTool.Highlighter && highlighterColor)
            {
                canvas.InkColor = Color.FromArgb(preferences.InkArgb);
                highlighterColor = false;
            }
            UpdateColorButton();
            UpdateAnnotationContextControls();
            UpdateContextHelp();
            LayoutChrome();
            if (canvas.HasImage)
            {
                if (tool == EditorTool.Crop) SetStatus("Crop: drag an edge or corner, then Apply crop or Enter. Escape cancels.");
                else if (tool == EditorTool.Text) SetStatus("Text: click the snip and type. Enter adds a line; Ctrl+Enter finishes. Advanced text opens full formatting.");
                else if (IsShapeToolKind(tool)) SetStatus("Shapes: drag to draw a " + tool.ToString().ToLowerInvariant() + ". Fill and Outline control its appearance.");
                else if (tool == EditorTool.Eraser) SetStatus("Eraser: click or drag over an annotation to remove it.");
                else if (tool == EditorTool.RemoveArea) SetStatus("Remove area: select captured content, drag the selection to adjust it, then press Delete. You can also reposition the selection with Move.");
                else if (tool == EditorTool.Select) SetStatus(canvas.HasSelectedRemovalArea
                    ? "Move: drag the removal selection to reposition it. Delete removes the new area; Escape cancels."
                    : "Move: select an annotation and drag it. Middle-drag pans the image.");
                else SetStatus(tool == EditorTool.Highlighter ? "Highlight: drag over text to highlight it. Hold Shift for freehand." : tool.ToString() + ": drag over the image.");
            }
        }

        private bool IsSourceRemovalContext
        {
            get { return canvas.Tool == EditorTool.RemoveArea || (canvas.Tool == EditorTool.Select && canvas.HasSelectedRemovalArea); }
        }

        private void UpdateColorButton()
        {
            if (colorButton == null) return;
            StudioButton swatch = colorButton as StudioButton;
            Color current = IsSourceRemovalContext ? canvas.SourceRemovalFillColor : canvas.InkColor;
            if (swatch != null) { swatch.SwatchColor = Color.FromArgb(255, current); swatch.Invalidate(); }
            tips.SetToolTip(colorButton, IsSourceRemovalContext ? "Choose the background fill for the selected removal area. Current color: " + current.Name : "Choose ink color. Current color: " + current.Name);
        }

        private void UpdateContextHelp()
        {
            bool highlighting = canvas.Tool == EditorTool.Highlighter;
            toolGuidance.Visible = !highlighting;
            snapToText.Visible = highlighting;
            recognitionStatus.Visible = highlighting;
            switch (canvas.Tool)
            {
                case EditorTool.Select: toolGuidance.Text = canvas.HasSelectedRemovalArea
                    ? "Drag the removal selection to reposition it. Delete applies the fill at its new position; Escape cancels."
                    : "Select an annotation to move it. Arrow keys nudge. Middle-drag pans the image."; break;
                case EditorTool.Pen: toolGuidance.Text = "Draw naturally. Switch to Move to reposition your marks."; break;
                case EditorTool.Arrow: toolGuidance.Text = "Draw an arrow. In Move, drag its tip or tail handle to rotate or change its length."; break;
                case EditorTool.Rectangle: toolGuidance.Text = "Draw a rectangle. Fill and Outline set its appearance; Move repositions it."; break;
                case EditorTool.Ellipse: toolGuidance.Text = "Draw an ellipse. Fill and Outline set its appearance; Move repositions it."; break;
                case EditorTool.Line: toolGuidance.Text = "Draw a line. Outline controls its color, opacity and size."; break;
                case EditorTool.Text: toolGuidance.Text = "Click the snip and type. Enter starts a new line; Ctrl+Enter finishes. Advanced text opens full formatting."; break;
                case EditorTool.Eraser: toolGuidance.Text = "Click or drag across an annotation to remove it. Captured pixels stay intact."; break;
                case EditorTool.Crop: toolGuidance.Text = "Drag the snip's edges or corners, then Apply crop or Enter. Escape cancels; Undo restores an applied crop."; break;
                case EditorTool.RemoveArea: toolGuidance.Text = "Click a flat-color object or drag any area. Drag the selected box, or switch to Move, to adjust its position before Delete. Right-click or Color changes the fill."; break;
            }
            UpdateRecognitionStatus();
        }

        private void UpdateRecognitionStatus()
        {
            if (IsDisposed || recognitionStatus.IsDisposed) return;
            string message = canvas.IsMatchingSourceText ? "Matching the source font and style… You can continue using the preview." :
                !canvas.HasImage ? "Capture or open a snip to recognize text." :
                canvas.TextActionsMode && !String.IsNullOrWhiteSpace(canvas.AllRecognizedText) ? "Select words to copy, edit a line, or delete. F2 edits; Delete removes the selection." :
                !snapToText.Checked && !canvas.TextActionsMode ? "Freehand highlight mode" : canvas.TextRecognitionStatus;
            if (String.IsNullOrEmpty(message)) message = "Preparing text recognition…";
            recognitionStatus.Text = message + (canvas.HasImage && !canvas.TextActionsMode ? "   •   Shift-drag for freehand" : "");
            tips.SetToolTip(recognitionStatus, message + "\nText recognition runs on this computer. Review any recognized text before using it.");
        }

        private void ChooseColor()
        {
            if (IsSourceRemovalContext) { canvas.ChooseSourceRemovalFillColor(this); return; }
            List<Color> recent = new List<Color>();
            if (preferences.RecentInkColors != null) foreach (int argb in preferences.RecentInkColors) recent.Add(Color.FromArgb(argb));
            using (ColorPaletteDialog picker = new ColorPaletteDialog(canvas.InkColor, recent))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                canvas.InkColor = picker.SelectedColor;
                if (!highlighterColor) preferences.InkArgb = picker.SelectedColor.ToArgb();
                recent.RemoveAll(delegate(Color color) { return color.ToArgb() == picker.SelectedColor.ToArgb(); });
                recent.Insert(0, picker.SelectedColor);
                if (recent.Count > 8) recent.RemoveRange(8, recent.Count - 8);
                preferences.RecentInkColors = recent.ConvertAll<int>(delegate(Color color) { return color.ToArgb(); }).ToArray();
                UpdateColorButton();
            }
        }

        private void StartCapture()
        {
            StartCapture(null);
        }

        private void CaptureOpenSnips()
        {
            if (!CanStartBackgroundCapture()) return;
            captureOpenSnipsRequested = true;
            try { StartCapture(null); }
            finally { captureOpenSnipsRequested = false; }
        }

        private async void StartCapture(int? captureDelaySeconds)
        {
            // Consume before every early return or await. This applies to one
            // still capture only, without changing saved mode or timer choices.
            bool includeOpenSnips = captureOpenSnipsRequested;
            captureOpenSnipsRequested = false;
            if (!includeOpenSnips && mode.SelectedIndex == 4) { StartScrollingCapture(); return; }
            if (!CanStartBackgroundCapture()) return;
            if (IsCaptureSavePending) { SetStatus("Finishing the current capture. Please wait briefly."); return; }
            if (!includeOpenSnips && recordingModeSelected) SetCaptureKind(false);
            if (!preferences.MultipleWindows && !ConfirmReplace("starting a new capture")) return;
            capturePending = true;
            // The taskbar timer is a one-shot override, independent of the saved
            // timer and its Remember setting. Do not assign the delay control.
            int seconds = captureDelaySeconds ?? new int[] { 0, 3, 5, 10 }[Math.Max(0, delay.SelectedIndex)];
            CaptureMode captureMode = includeOpenSnips && mode.SelectedIndex == 4 ?
                CaptureMode.Rectangle : (CaptureMode)mode.SelectedIndex;
            newButton.Enabled = false;
            FormWindowState previousState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            MainForm capturedWindow = null;
            bool captureFailed = false;
            CaptureVisibilityScope visibility = null;
            try
            {
                visibility = CaptureVisibilityScope.HideApplicationWindows(this, includeOpenSnips);
                if (seconds > 0 && !CaptureCountdown.WaitForCapture(seconds))
                {
                    SetStatus("Capture canceled. Your previous snip is unchanged.");
                    return;
                }
                await visibility.WaitUntilHiddenAsync();
                if (closing || IsDisposed) return;
                using (Bitmap image = CaptureService.Capture(captureMode))
                {
                    if (image != null)
                    {
                        if (preferences.MultipleWindows && canvas.HasImage)
                        {
                            capturedWindow = new MainForm(null);
                            capturedWindow.mode.SelectedIndex = mode.SelectedIndex;
                            capturedWindow.delay.SelectedIndex = delay.SelectedIndex;
                            capturedWindow.currentPath = null;
                            capturedWindow.PrepareCaptureImage(image);
                            capturedWindow.canvas.SetImage(image);
                            Program.OpenWindow(capturedWindow);
                            capturedWindow.BeginCapturePolicies(image);
                            capturedWindow.SetStatus(capturedWindow.capturePolicyMessage ?? "Captured. Annotate your snip, then Copy or Save.");
                        }
                        else
                        {
                            currentPath = null;
                            PrepareCaptureImage(image);
                            canvas.SetImage(image);
                            Show();
                            BeginCapturePolicies(image);
                            SetStatus(capturePolicyMessage ?? "Captured. Annotate your snip, then Copy or Save.");
                        }
                    }
                    else SetStatus("Capture canceled. Your previous snip is unchanged.");
                }
            }
            catch (Exception ex) { captureFailed = true; Show(); Activate(); ShowFailure("Capture could not finish", ex); }
            finally
            {
                Exception restoreFailure = null;
                if (visibility != null)
                {
                    try { visibility.Dispose(); }
                    catch (Exception error) { restoreFailure = error; }
                }
                capturePending = false;
                bool releaseEmptyEditor = closeEmptyShortcutCapture && !canvas.HasImage && !captureFailed && restoreFailure == null;
                closeEmptyShortcutCapture = false;
                if (!closing && !IsDisposed)
                {
                    if (releaseEmptyEditor) Close();
                    else
                    {
                    WindowState = previousState;
                    Show();
                    Activate();
                    canvas.Focus();
                    newButton.Enabled = true;
                    UpdateDocumentState();
                    if (capturedWindow != null && !capturedWindow.IsDisposed) capturedWindow.Activate();
                    if (restoreFailure != null) ShowFailure("The capture finished, but some Snip Studio windows could not be restored", restoreFailure);
                    }
                }
            }
        }

        private bool ConfirmReplace(string action)
        {
            if (IsCaptureSavePending) { SetStatus("Finishing the current capture before " + action + ". Please wait briefly."); return false; }
            if (!canvas.HasImage || !canvas.IsDirty || !ShouldAskToSaveEdits) return true;
            DialogResult answer = String.Equals(action, "closing", StringComparison.Ordinal)
                ? EditorClosePrompt.AskToSave(this)
                : MessageBox.Show(this, "Save your current snip before " + action + "?", "Unsaved snip",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            CaptureVisibilityScope.NotifyNativeDialogClosed();
            if (answer == DialogResult.Cancel) return false;
            return answer != DialogResult.Yes || SaveImage(false);
        }

        private void ChooseImage()
        {
            if (capturePending || !ConfirmReplace("opening another image")) return;
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Open an image";
                picker.Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";
                picker.CheckFileExists = true;
                if (picker.ShowDialog(this) == DialogResult.OK) OpenImage(picker.FileName);
            }
        }

        private void OpenImage(string path)
        {
            try
            {
                using (Image source = Image.FromFile(path))
                {
                    if ((long)source.Width * source.Height > 100000000)
                        throw new InvalidOperationException("This image is too large to edit safely. Please open an image under 100 megapixels.");
                    using (Bitmap bitmap = new Bitmap(source)) canvas.SetImage(bitmap);
                }
                currentPath = Path.GetFullPath(path);
                canvas.MarkSaved();
                UpdateDocumentState();
                SetStatus("Opened " + Path.GetFileName(path) + ". Edits are kept until you save.");
                TaskbarJumpList.RecordRecentFile(currentPath);
            }
            catch (Exception ex) { ShowFailure("The image could not be opened", ex); }
        }

        private void PasteImage()
        {
            if (capturePending) return;
            try
            {
                if (!Clipboard.ContainsImage()) { SetStatus("There is no image on the clipboard. Copy an image, then try Paste."); return; }
                using (Image image = Clipboard.GetImage())
                {
                    if (image == null) { SetStatus("The clipboard image is unavailable. Please copy it again."); return; }
                    if ((long)image.Width * image.Height > 100000000) throw new InvalidOperationException("This clipboard image is too large to edit safely.");
                    if (!ConfirmReplace("pasting another image")) return;
                    using (Bitmap bitmap = new Bitmap(image)) canvas.SetImage(bitmap);
                }
                currentPath = null;
                UpdateDocumentState();
                SetStatus("Pasted. Annotate your image, then Copy or Save.");
            }
            catch (Exception ex) { ShowFailure("The clipboard image could not be opened", ex); }
        }

        private bool SaveImage(bool saveAs) { return SaveImage(saveAs, false); }

        private bool SaveImage(bool saveAs, bool pdfOnly)
        {
            if (!canvas.HasImage) return false;
            string path = currentPath;
            string extension = path == null ? "" : Path.GetExtension(path).ToLowerInvariant();
            // Every explicit save is reviewable: choose name, folder, and format.
            {
                using (SaveFileDialog picker = new SaveFileDialog())
                {
                    picker.Title = "Save your snip";
                    picker.Filter = pdfOnly ? "PDF document (*.pdf)|*.pdf" : "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap image (*.bmp)|*.bmp|PDF document (*.pdf)|*.pdf";
                    picker.FilterIndex = pdfOnly ? 1 : extension == ".pdf" ? 4 : extension == ".jpg" || extension == ".jpeg" ? 2 : extension == ".bmp" ? 3 : 1;
                    picker.DefaultExt = pdfOnly || extension == ".pdf" ? "pdf" : "png";
                    picker.AddExtension = true;
                    picker.OverwritePrompt = true;
                    picker.FileName = String.IsNullOrEmpty(path) ? "Snip " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") : Path.GetFileNameWithoutExtension(path);
                    string currentFolder = String.IsNullOrEmpty(path) ? "" : Path.GetDirectoryName(path);
                    if (Directory.Exists(currentFolder)) picker.InitialDirectory = currentFolder;
                    else if (Directory.Exists(preferences.SaveFolder)) picker.InitialDirectory = preferences.SaveFolder;
                    else if (Directory.Exists(preferences.ScreenshotFolder)) picker.InitialDirectory = preferences.ScreenshotFolder;
                    if (picker.ShowDialog(this) != DialogResult.OK) return false;
                    string chosenExtension = pdfOnly || picker.FilterIndex == 4 ? ".pdf" : picker.FilterIndex == 2 ? ".jpg" : picker.FilterIndex == 3 ? ".bmp" : ".png";
                    path = Path.ChangeExtension(picker.FileName, chosenExtension);
                    if (!String.Equals(path, picker.FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
                        MessageBox.Show(this, "Replace the existing file " + Path.GetFileName(path) + "?", "Save your snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
                }
            }
            string temporary = null;
            try
            {
                extension = Path.GetExtension(path).ToLowerInvariant();
                ImageFormat format = null;
                if (extension == ".png") format = ImageFormat.Png;
                else if (extension == ".jpg" || extension == ".jpeg") format = ImageFormat.Jpeg;
                else if (extension == ".bmp") format = ImageFormat.Bmp;
                else if (extension != ".pdf") throw new InvalidOperationException("Choose a .png, .jpg, .bmp, or .pdf filename.");
                path = Path.GetFullPath(path);
                if (extension == ".pdf")
                {
                    using (Bitmap image = canvas.RenderImage()) PdfExportService.Save(image, path);
                }
                else
                {
                    temporary = Path.Combine(Path.GetDirectoryName(path), ".snip-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (Bitmap image = canvas.RenderImage())
                    {
                        if (format.Guid == ImageFormat.Jpeg.Guid)
                        {
                            using (Bitmap flattened = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                            {
                                using (Graphics graphics = Graphics.FromImage(flattened)) { graphics.Clear(Color.White); graphics.DrawImageUnscaled(image, 0, 0); }
                                flattened.Save(temporary, format);
                            }
                        }
                        else image.Save(temporary, format);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    temporary = null;
                }
                currentPath = path;
                preferences.SaveFolder = Path.GetDirectoryName(path);
                canvas.MarkSaved();
                UpdateDocumentState();
                SetStatus("Saved " + Path.GetFileName(path));
                // PDF exports are not editable image documents on a later launch.
                if (extension != ".pdf") TaskbarJumpList.RecordRecentFile(currentPath);
                return true;
            }
            catch (Exception ex) { ShowFailure("The snip could not be saved. Your snip is still open", ex); return false; }
            finally { if (temporary != null && File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
        }

        private async void CopyImage()
        {
            if (!canvas.HasImage) return;
            try
            {
                if (await CopySnapshotToClipboardAsync(false)) SetStatus("Copied. Paste the image into another app with Ctrl+V.");
            }
            catch (Exception ex) { ShowFailure("The clipboard is busy. Please try Copy again", ex); }
        }

        private void CopyText()
        {
            if (!canvas.HasImage || capturePending) return;
            canvas.TextActionsMode = !canvas.TextActionsMode;
            UpdateTextActionControls();
            LayoutChrome();
            canvas.Focus();
            SetStatus(canvas.TextActionsMode ? "Select recognized words on the image, then copy the selection or all text." : "Text actions closed.");
        }

        private void PrintImage()
        {
            if (!canvas.HasImage || capturePending) return;
            try
            {
                using (Bitmap image = canvas.RenderImage())
                using (PrintActionsForm print = new PrintActionsForm(image)) print.ShowDialog(this);
            }
            catch (Exception ex) { ShowFailure("Printing could not open", ex); }
        }

        private void ShowAbout()
        {
            using (AboutForm about = new AboutForm()) about.ShowDialog(this);
        }

        private void SetStatus(string message)
        {
            status.Text = message;
            tips.SetToolTip(status, message + (hotkeyRegistered ? "\nCapture shortcut: Ctrl+Alt+S" : "\nGlobal shortcut unavailable. Use New or Ctrl+N."));
        }

        private void UpdateDocumentState()
        {
            if (IsDisposed) return;
            editorToolShelf.Visible = canvas.HasImage;
            editorContextBar.Visible = canvas.HasImage && (canvas.TextActionsMode || canvas.Tool == EditorTool.Highlighter || IsCropContext || IsShapeContext || IsInlineTextContext);
            UpdateResponsiveChrome();
            foreach (Control control in documentControls) control.Enabled = canvas.HasImage && !capturePending;
            undoButton.Enabled = canvas.CanUndo && !capturePending;
            redoButton.Enabled = canvas.CanRedo && !capturePending;
            newButton.Enabled = !capturePending;
            UpdateRecordingControls();
            dimensions.Text = canvas.HasImage ? canvas.ImageSize.Width + " × " + canvas.ImageSize.Height + " px" : "";
            UpdateDocumentTitle();
            UpdateZoomLabel();
            UpdateRecognitionStatus();
            UpdateTextStyleControls();
            UpdateCropActionControls();
            UpdateAnnotationContextControls();
            foreach (KeyValuePair<EditorTool, Button> item in toolButtons)
            {
                StudioButton button = item.Value as StudioButton;
                if (button != null) button.Selected = item.Key == canvas.Tool;
            }
        }

        private void UpdateDocumentTitle()
        {
            if (!IsDisposed && !closing)
                Text = (canvas.HasImage && canvas.IsDirty ? "• " : "") + "Snip Studio" + (String.IsNullOrEmpty(currentPath) ? "" : " — " + Path.GetFileName(currentPath));
        }

        private void UpdateZoomLabel() { zoomLabel.Text = canvas.HasImage ? (canvas.Zoom * 100).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "%" : "—"; }

        private void UpdateResponsiveChrome()
        {
            LayoutChrome();
        }

        private void ShowFailure(string title, Exception error)
        {
            SetStatus(title + ".");
            MessageBox.Show(this, title + ".\n\n" + error.Message, "Snip Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowHelp()
        {
            using (Form help = new Form())
            {
                help.Text = "Snip Studio — Help";
                help.Font = Font;
                help.StartPosition = FormStartPosition.CenterParent;
                help.AutoScaleMode = AutoScaleMode.Dpi;
                help.AutoScaleDimensions = new SizeF(96, 96);
                help.Size = new Size(760, 740);
                help.MinimumSize = new Size(560, 480);
                help.MinimizeBox = false;
                help.BackColor = StudioTheme.Background;
                help.ForeColor = StudioTheme.Text;
                TrySetAppIcon(help);
                help.HandleCreated += delegate { ApplyDarkCaption(help); };
                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.RowCount = 3;
                layout.ColumnCount = 1;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
                layout.Padding = new Padding(26, 22, 26, 20);
                Label heading = new Label();
                heading.Text = "Your screenshot workspace";
                heading.AutoSize = true;
                heading.Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
                heading.Margin = new Padding(0, 0, 0, 18);
                layout.Controls.Add(heading, 0, 0);
                TextBox text = new TextBox();
                text.Multiline = true;
                text.ReadOnly = true;
                text.ScrollBars = ScrollBars.Vertical;
                text.Dock = DockStyle.Fill;
                text.BackColor = StudioTheme.Background;
                text.ForeColor = StudioTheme.Text;
                text.BorderStyle = BorderStyle.None;
                text.Font = new Font("Segoe UI", 10.5f);
                text.Margin = new Padding(0);
                text.Text = BuildHelpText();
                text.SelectionStart = 0;
                text.SelectionLength = 0;
                layout.Controls.Add(text, 0, 1);
                FlowLayoutPanel footer = Bar(new Padding(0, 16, 0, 0));
                footer.AutoSize = false;
                footer.Height = 56;
                footer.FlowDirection = FlowDirection.RightToLeft;
                StudioButton close = ActionButton("Done", "", 96, delegate { help.Close(); });
                close.Primary = true;
                close.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(close);
                footer.Controls.Add(ActionButton("About", "help", 96, delegate { using (AboutForm about = new AboutForm()) about.ShowDialog(help); }));
                help.CancelButton = close;
                layout.Controls.Add(footer, 0, 2);
                help.Controls.Add(layout);
                help.Shown += delegate { text.Select(0, 0); text.ScrollToCaret(); close.Focus(); };
                help.ShowDialog(this);
            }
        }

        private string BuildHelpText()
        {
            return String.Join("\r\n\r\n", new string[]
            {
                "CAPTURE\r\nChoose Rectangle, Freeform, Window, Full screen, Scrolling, or All monitors, then New snip. Drag a region across connected monitors when needed. Full screen lets you click the monitor to capture. All monitors captures the complete connected desktop. Use a delay to prepare menus or tooltips. Escape or right-click cancels the selection.\r\n\r\nNew hides only the window starting the capture and Settings. Other open snips stay visible. To include the window you are using, choose More > Capture open snips. This takes one screenshot with all open snips visible, keeps your selected timer and mode, and uses Rectangle for this capture if Scrolling is selected.\r\n\r\nWindow mode captures the visible screen area inside the selected window, including anything covering it. Protected content may appear black.",
                "TASKBAR RIGHT-CLICK MENU\r\nRight-click Snip Studio on the Windows taskbar for Take a new screenshot or a new screenshot in 3, 5, or 10 seconds. These actions also work when Snip Studio is closed. The selected taskbar delay applies to that capture only; your saved timer choice is kept. Recent saved or opened snips reopen in Snip Studio. Windows manages this list and its privacy setting: Settings > Personalization > Start > Show recently opened items in Start, Jump Lists, and File Explorer. Unsaved snips are not added. Choose More > Refresh taskbar menu, or Refresh taskbar menu from the notification-area icon, to refresh capture tasks and the existing shortcut without unpinning. Windows supplies Pin to taskbar and Close window; use More > Exit Snip Studio to stop the background shortcut listener.",
                "ANNOTATE AND MOVE\r\nUse Pen, Highlight, Shapes, or Text to make your point. Selecting a tool keeps the normal pointer. Resize handles and Move still indicate the available action. Choose Move, click any annotation, then drag to reposition it, or right-click an annotation to select it and switch to Move. Move selects annotations and removal selection boxes; clicking blank space clears the selection. Arrow keys nudge a selected annotation; Delete removes it. Use the middle mouse button or scrollbars to pan the image.\r\n\r\nShapes includes Rectangle, Ellipse, Line, and Arrow. Fill and Outline offer colors, None, opacity, and outline size. Fill applies to rectangles and ellipses. Select a shape with Move to change its appearance; Undo restores its previous style. Choose Emoji, pick a symbol, then click the snip to place it. Use Move or Text to adjust it afterward.\r\n\r\nClick the Exact color square in the palette for a honeycomb picker with RGB, hex, and brightness controls. Cancel keeps the previous color. Eraser removes annotations without changing the captured pixels. Crop shows handles around the whole snip. Drag an edge or corner to adjust the area to keep; Apply crop or Enter commits it, and Cancel or Escape leaves the snip unchanged. Undo and Redo let you recover applied edits and crops. Save and Copy image export at the image's actual resolution, regardless of zoom.",
                "SMART HIGHLIGHT\r\nWith Highlight selected, leave Snap to text checked to highlight detected words and lines. Text recognition runs locally. The readiness message shows when recognition is pending, ready, or unavailable. Hold Shift while dragging to draw a freehand highlight, or turn Snap to text off.",
                "TEXT NOTES AND ARROWS\r\nChoose Text, click the snip, and type directly on the image. Enter adds a new line; Ctrl+Enter or Done finishes; Escape or Cancel discards the current text edit. Double-click an existing note in Move/Text mode, or select it and press F2, to edit it in place. Text has its own size selector. Advanced text retains the full popup editor for font family and size, bold, italic, underline, strikeout, colors, highlighting, alignment, lists, indentation, spacing, and scripts. Formatting applies to selected words or the insertion point. Drag the side handles to make a note narrower; its height grows as words wrap. Keep your note inside the screenshot edges. In Move, select an arrow and drag its tip or tail handle to rotate it and change its length.",
                "COPY TEXT\r\nCopy text recognizes the current snip and adds a selectable text overlay directly on the image. Drag over words to select them, then copy the selection or all recognized text. Right-click selected words for Copy selection; the selection stays in place. Recognition can make mistakes, especially with small text, unusual fonts, or low contrast. Check important values before using them. Screenshots are not uploaded for recognition.",
                "TRANSLATE TEXT\r\nIn Text actions, choose Translate to translate selected words or all recognized text inside Snip Studio. The complete installer includes a free, open-source offline engine and English, German, Spanish, and French language models. Choose the original language and target language, then Translate. No server, paid account, or internet connection is needed for the included languages. Copy translation keeps the screenshot unchanged. Replace text in snip replaces each original recognized line in its existing position, using estimated source typography and a sampled solid background. Review the result; one Undo restores all original lines. Photos and gradients can show patches. Keep each translated line paired with its original line when editing the result. Other pairs among these languages can translate through English. Check the result before using it. An optional LibreTranslate server remains available for advanced use; only that mode sends text to the server you choose.",
                "EDIT RECOGNIZED TEXT\r\nSelect words in one recognized line, then Edit line or F2. The words start selected so you can delete or replace them. Source font family, style, and size are estimated from the pixels; review and adjust the formatting. Applying a line edit covers the old screenshot line with the chosen solid background color. Delete selection on screenshot text covers only the selected words, leaving gaps and preserving surrounding text. Editable notes keep their formatting and reflow after deletion. Undo restores the original; ambiguous overlapping text is left unchanged.",
                "RECORD SCREEN\r\nChoose Video mode. Use the audio button to choose microphone and system-audio sources, then Record. Choose the recording's save location when prompted, drag over the screen area to record, and prepare during the three-second countdown. Your current screenshot stays open. The recording panel shows elapsed time; Stop saves and finalizes the video. Closing the panel also stops and saves.\r\n\r\nVideos use MP4 at 15 frames per second, scaled proportionally to fit up to 1920 × 1080 pixels. Both audio sources are enabled initially. If a requested audio source cannot be used, the app reports the problem; it does not silently switch to a recording without that source. Keep the recording panel outside the selected area if Windows cannot exclude it from capture.",
                "PRINT\r\nPrint opens a preview of the current snip and annotations. Review the printer and page options before sending the image to print.",
                "PDF AND SMALLER RECORDINGS\r\nMore > Save as PDF exports a lossless image of the finished snip. This PDF is an image document, not searchable or editable text. In recording review, Make smaller offers 720p, 480p, and target-size options for a separate MP4 copy. The original stays intact. Target sizes are estimates; check the actual result before attaching it to email.",
                "TASKBARS AND TOOLTIPS\r\nSettings > Hide taskbars during capture applies to screenshots and video and defaults to On. Keep main monitor taskbar visible also defaults to On. Hidden taskbars are restored after the screenshot snapshot or when recording ends. Show tooltips is on by default; turn it off in Settings for fewer hints.",
                "DELAY AND ZOOM\r\nRemember capture delay keeps your last timer choice for the next session. Turn it off to start each session with No delay while retaining timer choices during that session. Ctrl+mouse wheel zooms toward the pointer. In Settings, turn off Hold Ctrl to zoom with the mouse wheel to use the wheel by itself. Without Ctrl, the default wheel scrolls smoothly; Shift+wheel scrolls horizontally. Use the middle button or scrollbars to pan.",
                "KEYBOARD\r\nF11 — Toggle full-screen editor on this monitor; Escape returns when no canvas action needs it\r\nCtrl+N — New capture in the selected Photo/Video mode\r\nCtrl+Alt+S — Capture from another app while Snip Studio is open\r\nCtrl+O — Open an image\r\nCtrl+V — Paste an image\r\nCtrl+C — Copy image, or selected words in text-overlay mode\r\nCtrl+Shift+C — Toggle text overlay\r\nCtrl+S — Save\r\nCtrl+Shift+S — Save as\r\nCtrl+P — Print\r\nCtrl+Z — Undo\r\nCtrl+Y / Ctrl+Shift+Z — Redo\r\nCtrl+mouse wheel — Zoom\r\nF1 — Help",
                hotkeyRegistered ? "The global capture shortcut is ready." : "The global shortcut could not be registered. Another app may already use it. Use New snip or Ctrl+N instead.",
                "SETTINGS AND YOUR FILES\r\nUse Settings to choose automatic copies and saves, edit prompts, save folders, capture borders, appearance, and recording audio defaults. Closing a window closes that snip; the editor releases its snip when closed. Keep capture shortcuts ready after closing is on by default: a small tray listener keeps Print Screen and Ctrl+Alt+S available. Exit Snip Studio stops it. Start with Windows is optional and keeps capture available after sign-in. Windows can reserve Print Screen; use the Windows keyboard settings link to review that assignment. Keep running in the notification area is an optional Settings choice that makes Close hide the window instead. Annotation toolbar position in Settings chooses Top, Right, Bottom or Left. Settings stays independent so other snips remain usable while it is open. Recognition and editing run on this computer. Your screenshots and recordings are not uploaded by Snip Studio.",
                "SNIP STUDIO 0.6.0\r\nCreated by Automation Forge. Open About for app information and automation-forge.com. Snip Studio is an independent app and is not affiliated with Microsoft. Choose Scrolling mode for long vertical content. Select only the scrolling content, leaving out sidebars and fixed message boxes. Let capture finish, then review the joins and bottom before choosing Use capture. Pause streaming text or videos when possible. Partial captures may not reach the bottom. Finish opens the accepted portion; Cancel or Escape discards it. Unsupported, animated, covered or unalignable content stops capture for review."
            });
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (canvas.InlineTextEditing && canvas.ContainsFocus && IsInlineTextInputCommand(keyData))
                return base.ProcessCmdKey(ref msg, keyData);
            if (HandleCropCommand(keyData)) return true;
            if (keyData == Keys.F11) { ToggleFullScreenEditor(); return true; }
            if (keyData == Keys.Escape && TryExitFullScreenOnEscape()) return true;
            if (capturePending) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == (Keys.Control | Keys.N)) { if (recordingModeSelected) StartRecording(); else StartCapture(); return true; }
            if (keyData == (Keys.Control | Keys.O)) { ChooseImage(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { SaveImage(false); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { SaveImage(true); return true; }
            if (keyData == (Keys.Control | Keys.C)) { if (canvas.TextActionsMode && canvas.HasSelectedRecognizedText) CopyRecognizedText(false); else CopyImage(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.C)) { CopyText(); return true; }
            if (keyData == (Keys.Control | Keys.P)) { PrintImage(); return true; }
            if (keyData == (Keys.Control | Keys.V)) { PasteImage(); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { canvas.Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { canvas.Redo(); return true; }
            if (keyData == Keys.F1) { ShowHelp(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (layoutTestMode) return;
            ApplyDarkCaption(this);
            hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, (uint)Keys.S);
            ConfigurePrintScreenShortcut();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ReleaseAdditionalHotkeys();
            if (hotkeyRegistered) { UnregisterHotKey(Handle, HotkeyId); hotkeyRegistered = false; }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (EditorClosePrompt.ActivatePendingForCloseMessage(this, message.Msg, message.WParam))
            {
                message.Result = IntPtr.Zero;
                return;
            }
            if (HandleAdditionalHotkey(message)) { base.WndProc(ref message); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == HotkeyId)
            {
                if (CanStartBackgroundCapture())
                {
                    StartCapture();
                }
            }
            base.WndProc(ref message);
        }

        private int ModalOpenCount()
        {
            int count = 0;
            foreach (Form form in Application.OpenForms) if (form.Modal) count++;
            return count;
        }

        internal static void ApplyDarkCaption(Form form)
        {
            StudioTheme.ApplyCaption(form);
        }

        internal static void TrySetAppIcon(Form form)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SnipStudio.AppIcon"))
                    if (stream != null)
                    using (Icon embedded = new Icon(stream)) form.Icon = (Icon)embedded.Clone();
            }
            catch (ArgumentException) { }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (EditorClosePrompt.DeferCloseIfPending(this)) { e.Cancel = true; return; }
            if (closeDecisionInProgress) { e.Cancel = true; return; }
            if (TryDelayCloseForScrollingCapture(e)) return;
            if (TryDelayCloseForCaptureSave(e)) return;
            if (recordingSession)
            {
                e.Cancel = true;
                closeAfterRecording = true;
                StopRecording();
                return;
            }
            if (TryHideToTray(e)) { EditorClosePrompt.CompleteHiddenClose(this); return; }
            closeDecisionInProgress = true;
            try
            {
                if (!ConfirmReplace("closing")) { e.Cancel = true; forceExit = false; Program.CancelApplicationExit(); return; }
            }
            catch { Program.CancelApplicationExit(); throw; }
            finally { closeDecisionInProgress = false; }
            closing = true;
            preferences.CaptureModeIndex = mode.SelectedIndex;
            if (preferences.RememberCaptureDelay) preferences.DelaySeconds = sessionCaptureDelay ?? new int[] { 0, 3, 5, 10 }[Math.Max(0, delay.SelectedIndex)];
            preferences.StrokeWidth = (int)canvas.StrokeWidth;
            if (!highlighterColor) preferences.InkArgb = canvas.InkColor.ToArgb();
            try { preferences.Save(); } catch (Exception) { }
            Program.RememberShortcutPreferences(preferences);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelScrollingCaptureOnDispose();
                DisposeSettingsIntegration();
                DisposeUpdateIntegration();
                DisposeRecordingTaskbars();
                DisposeHotkeysAndTray();
                DisposeShapeActions();
                tips.Dispose();
                if (brandMark.Image != null) { brandMark.Image.Dispose(); brandMark.Image = null; }
            }
            base.Dispose(disposing);
        }
    }
}
