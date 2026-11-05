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
        private bool highlighterColor;
        private readonly string initialPath;
        private readonly bool layoutTestMode;
        private const int HotkeyId = 0x534E;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
            Resize += delegate { UpdateResponsiveChrome(); };
            mode.SelectedIndex = Math.Max(0, Math.Min(3, preferences.CaptureModeIndex));
            delay.SelectedIndex = preferences.DelaySeconds == 3 ? 1 : preferences.DelaySeconds == 5 ? 2 : preferences.DelaySeconds == 10 ? 3 : 0;
            int stroke = Array.IndexOf(new int[] { 2, 4, 8, 12, 20 }, preferences.StrokeWidth);
            width.SelectedIndex = stroke >= 0 ? stroke : 1;
            canvas.InkColor = Color.FromArgb(preferences.InkArgb);
            if (canvas.InkColor.A == 0) canvas.InkColor = Color.FromArgb(220, 38, 38);
            UpdateColorButton();
            SelectTool(EditorTool.Pen);
            canvas.DocumentChanged += delegate { UpdateDocumentState(); OnDocumentEdited(); };
            if (!layoutTestMode) InitializeSettingsIntegration();
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
            canvas.Tool = tool;
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
            UpdateContextHelp();
            LayoutChrome();
            if (canvas.HasImage)
            {
                if (tool == EditorTool.Crop) SetStatus("Crop: drag across the area to keep. Undo restores the full image.");
                else if (tool == EditorTool.Text) SetStatus("Text: click the image where you want to add a note.");
                else if (tool == EditorTool.Eraser) SetStatus("Eraser: click or drag over an annotation to remove it.");
                else if (tool == EditorTool.Select) SetStatus("Move: select an annotation and drag it. Drag blank image space to pan.");
                else SetStatus(tool == EditorTool.Highlighter ? "Highlight: drag over text to highlight it. Hold Shift for freehand." : tool.ToString() + ": drag over the image.");
            }
        }

        private void UpdateColorButton()
        {
            if (colorButton == null) return;
            StudioButton swatch = colorButton as StudioButton;
            if (swatch != null) { swatch.SwatchColor = Color.FromArgb(255, canvas.InkColor); swatch.Invalidate(); }
            tips.SetToolTip(colorButton, "Choose ink color. Current color: " + canvas.InkColor.Name);
        }

        private void UpdateContextHelp()
        {
            bool highlighting = canvas.Tool == EditorTool.Highlighter;
            toolGuidance.Visible = !highlighting;
            snapToText.Visible = highlighting;
            recognitionStatus.Visible = highlighting;
            switch (canvas.Tool)
            {
                case EditorTool.Select: toolGuidance.Text = "Select any annotation to move it. Arrow keys nudge. Drag blank space to pan."; break;
                case EditorTool.Pen: toolGuidance.Text = "Draw naturally. Switch to Move to reposition your marks."; break;
                case EditorTool.Arrow: toolGuidance.Text = "Draw an arrow. In Move, drag its tip or tail handle to rotate or change its length."; break;
                case EditorTool.Rectangle: toolGuidance.Text = "Drag to frame an area. Switch to Move to reposition the box."; break;
                case EditorTool.Text: toolGuidance.Text = "Click to add text. Double-click a note to edit it; drag its side handles to wrap onto more lines."; break;
                case EditorTool.Eraser: toolGuidance.Text = "Click or drag across an annotation to remove it. Captured pixels stay intact."; break;
                case EditorTool.Crop: toolGuidance.Text = "Drag over the area to keep. Undo restores the previous image."; break;
            }
            UpdateRecognitionStatus();
        }

        private void UpdateRecognitionStatus()
        {
            if (IsDisposed || recognitionStatus.IsDisposed) return;
            string message = !canvas.HasImage ? "Capture or open a snip to recognize text." :
                canvas.TextActionsMode && !String.IsNullOrWhiteSpace(canvas.AllRecognizedText) ? "Select words to copy, edit a line, or delete. F2 edits; Delete removes the selection." :
                !snapToText.Checked && !canvas.TextActionsMode ? "Freehand highlight mode" : canvas.TextRecognitionStatus;
            if (String.IsNullOrEmpty(message)) message = "Preparing text recognition…";
            recognitionStatus.Text = message + (canvas.HasImage && !canvas.TextActionsMode ? "   •   Shift-drag for freehand" : "");
            tips.SetToolTip(recognitionStatus, message + "\nText recognition runs on this computer. Review any recognized text before using it.");
        }

        private void ChooseColor()
        {
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

        private async void StartCapture()
        {
            if (capturePending || closing) return;
            if (recordingModeSelected) SetCaptureKind(false);
            if (!preferences.MultipleWindows && !ConfirmReplace("starting a new capture")) return;
            capturePending = true;
            int seconds = new int[] { 0, 3, 5, 10 }[Math.Max(0, delay.SelectedIndex)];
            CaptureMode captureMode = (CaptureMode)mode.SelectedIndex;
            newButton.Enabled = false;
            FormWindowState previousState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            MainForm capturedWindow = null;
            Hide();
            try
            {
                // Let the compositor remove our window before freezing the desktop.
                if (seconds > 0 && !CaptureCountdown.WaitForCapture(seconds))
                {
                    SetStatus("Capture canceled. Your previous snip is unchanged.");
                    return;
                }
                await Task.Delay(220);
                if (closing || IsDisposed) return;
                using (Bitmap image = CaptureService.Capture(captureMode))
                {
                    if (image != null)
                    {
                        if (preferences.MultipleWindows && canvas.HasImage)
                        {
                            capturedWindow = new MainForm(null);
                            capturedWindow.mode.SelectedIndex = mode.SelectedIndex;
                            capturedWindow.currentPath = null;
                            capturedWindow.canvas.SetImage(image);
                            capturedWindow.ApplyCapturePolicies(image);
                            Program.OpenWindow(capturedWindow);
                            capturedWindow.SetStatus(capturedWindow.capturePolicyMessage ?? "Captured. Annotate your snip, then Copy or Save.");
                        }
                        else
                        {
                            currentPath = null;
                            canvas.SetImage(image);
                            ApplyCapturePolicies(image);
                            SetStatus(capturePolicyMessage ?? "Captured. Annotate your snip, then Copy or Save.");
                        }
                    }
                    else SetStatus("Capture canceled. Your previous snip is unchanged.");
                }
            }
            catch (Exception ex) { Show(); Activate(); ShowFailure("Capture could not finish", ex); }
            finally
            {
                capturePending = false;
                if (!closing && !IsDisposed)
                {
                    WindowState = previousState;
                    Show();
                    Activate();
                    canvas.Focus();
                    newButton.Enabled = true;
                    UpdateDocumentState();
                    if (capturedWindow != null && !capturedWindow.IsDisposed) capturedWindow.Activate();
                }
            }
        }

        private bool ConfirmReplace(string action)
        {
            if (!canvas.HasImage || !canvas.IsDirty || !ShouldAskToSaveEdits) return true;
            DialogResult answer = MessageBox.Show(this, "Save your current snip before " + action + "?", "Unsaved snip",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
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
            if (saveAs || String.IsNullOrEmpty(path) || (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp" && extension != ".pdf"))
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
                    if (Directory.Exists(preferences.SaveFolder)) picker.InitialDirectory = preferences.SaveFolder;
                    if (picker.ShowDialog(this) != DialogResult.OK) return false;
                    path = picker.FileName;
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
                return true;
            }
            catch (Exception ex) { ShowFailure("The snip could not be saved. Your snip is still open", ex); return false; }
            finally { if (temporary != null && File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } } }
        }

        private void CopyImage()
        {
            if (!canvas.HasImage) return;
            try
            {
                CopySnapshotToClipboard();
                SetStatus("Copied. Paste the image into another app with Ctrl+V.");
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
            editorContextBar.Visible = canvas.HasImage && (canvas.TextActionsMode || canvas.Tool == EditorTool.Highlighter);
            UpdateResponsiveChrome();
            foreach (Control control in documentControls) control.Enabled = canvas.HasImage && !capturePending;
            undoButton.Enabled = canvas.CanUndo && !capturePending;
            redoButton.Enabled = canvas.CanRedo && !capturePending;
            newButton.Enabled = !capturePending;
            UpdateRecordingControls();
            dimensions.Text = canvas.HasImage ? canvas.ImageSize.Width + " × " + canvas.ImageSize.Height + " px" : "";
            Text = (canvas.HasImage && canvas.IsDirty ? "• " : "") + "Snip Studio" + (String.IsNullOrEmpty(currentPath) ? "" : " — " + Path.GetFileName(currentPath));
            UpdateZoomLabel();
            UpdateRecognitionStatus();
            UpdateTextStyleControls();
            foreach (KeyValuePair<EditorTool, Button> item in toolButtons)
            {
                StudioButton button = item.Value as StudioButton;
                if (button != null) button.Selected = item.Key == canvas.Tool;
            }
        }

        private void UpdateZoomLabel() { zoomLabel.Text = canvas.HasImage ? Math.Round(canvas.Zoom * 100) + "%" : "—"; }

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
                "CAPTURE\r\nChoose Rectangle, Freeform, Window, or Full screen, then New snip. Drag a region across connected monitors when needed. Full screen includes every connected monitor. Use a delay to prepare menus or tooltips. Escape or right-click cancels the selection.\r\n\r\nWindow mode captures the visible screen area inside the selected window, including anything covering it. Protected content may appear black.",
                "ANNOTATE AND MOVE\r\nUse Pen, Highlight, Arrow, Box, or Text to make your point. Color and stroke width apply to new annotations. Choose Move, click any annotation, then drag to reposition it. Arrow keys nudge a selected annotation; Delete removes it. Drag blank image space or use the middle mouse button to pan.\r\n\r\nEraser removes annotations without changing the captured pixels. Crop keeps the area you drag across. Undo and Redo let you recover edits and crops. Save and Copy image export at the image's actual resolution, regardless of zoom.",
                "SMART HIGHLIGHT\r\nWith Highlight selected, leave Snap to text checked to highlight detected words and lines. Text recognition runs locally. The readiness message shows when recognition is pending, ready, or unavailable. Hold Shift while dragging to draw a freehand highlight, or turn Snap to text off.",
                "TEXT NOTES AND ARROWS\r\nText has its own size selector. Double-click an existing note in Move/Text mode, or select it and press F2, to edit its words, size, wrapping width, and color. Drag the side handles to make a note narrower; its height grows as words wrap. Keep your note inside the screenshot edges. In Move, select an arrow and drag its tip or tail handle to rotate it and change its length.",
                "COPY TEXT\r\nCopy text recognizes the current snip and adds a selectable text overlay directly on the image. Drag over words to select them, then copy the selection or all recognized text. Recognition can make mistakes, especially with small text, unusual fonts, or low contrast. Check important values before using them. Screenshots are not uploaded for recognition.",
                "EDIT RECOGNIZED TEXT\r\nSelect words in one recognized line, then Edit line or F2. The words start selected so you can delete or replace them. Delete selection removes selected words and reflows the line. Fonts are estimated; the old screenshot line is covered with the chosen solid background color. Undo restores the original. Existing editable notes are changed through their original text, and ambiguous overlapping text is left unchanged.",
                "RECORD SCREEN\r\nChoose Video mode. Use the audio button to choose microphone and system-audio sources, then Record. Choose the recording's save location when prompted, drag over the screen area to record, and prepare during the three-second countdown. Your current screenshot stays open. The recording panel shows elapsed time; Stop saves and finalizes the video. Closing the panel also stops and saves.\r\n\r\nVideos use MP4 at 15 frames per second, scaled proportionally to fit up to 1920 × 1080 pixels. Both audio sources are enabled initially. If a requested audio source cannot be used, the app reports the problem; it does not silently switch to a recording without that source. Keep the recording panel outside the selected area if Windows cannot exclude it from capture.",
                "PRINT\r\nPrint opens a preview of the current snip and annotations. Review the printer and page options before sending the image to print.",
                "PDF AND SMALLER RECORDINGS\r\nMore > Save as PDF exports a lossless image of the finished snip. This PDF is an image document, not searchable or editable text. In recording review, Make smaller offers 720p, 480p, and target-size options for a separate MP4 copy. The original stays intact. Target sizes are estimates; check the actual result before attaching it to email.",
                "TASKBARS AND TOOLTIPS\r\nSettings > Hide taskbars during capture applies to screenshots and video. Keep main monitor taskbar visible is enabled by default when hiding is on. Hidden taskbars are restored after the screenshot snapshot or when recording ends. Show tooltips is on by default; turn it off in Settings for fewer hints.",
                "KEYBOARD\r\nCtrl+N — New capture in the selected Photo/Video mode\r\nCtrl+Alt+S — Capture from another app while Snip Studio is open\r\nCtrl+O — Open an image\r\nCtrl+V — Paste an image\r\nCtrl+C — Copy image, or selected words in text-overlay mode\r\nCtrl+Shift+C — Toggle text overlay\r\nCtrl+S — Save\r\nCtrl+Shift+S — Save as\r\nCtrl+P — Print\r\nCtrl+Z — Undo\r\nCtrl+Y / Ctrl+Shift+Z — Redo\r\nCtrl+mouse wheel — Zoom\r\nF1 — Help",
                hotkeyRegistered ? "The global capture shortcut is ready." : "The global shortcut could not be registered. Another app may already use it. Use New snip or Ctrl+N instead.",
                "SETTINGS AND YOUR FILES\r\nUse Settings to choose automatic copies and saves, edit prompts, save folders, capture borders, appearance, and recording audio defaults. Closing a window closes that snip; closing the last window exits Snip Studio. Keep running in the notification area is an optional Settings choice that makes Close hide the window instead. Recognition and editing run on this computer. Your screenshots and recordings are not uploaded by Snip Studio.",
                "SNIP STUDIO 0.3.0\r\nCreated by Automation Forge. Open About for app information and automation-forge.com. Snip Studio is an independent app and is not affiliated with Microsoft. Scrolling capture is not included."
            });
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
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
            if (HandleAdditionalHotkey(message)) { base.WndProc(ref message); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == HotkeyId)
            {
                if (!capturePending && !closing && Enabled && IsWindowEnabled(Handle) && ModalOpenCount() == 0)
                {
                    Show();
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Activate();
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
            if (recordingSession)
            {
                e.Cancel = true;
                closeAfterRecording = true;
                StopRecording();
                return;
            }
            if (TryHideToTray(e)) return;
            if (!ConfirmReplace("closing")) { e.Cancel = true; return; }
            closing = true;
            preferences.CaptureModeIndex = mode.SelectedIndex;
            preferences.DelaySeconds = new int[] { 0, 3, 5, 10 }[Math.Max(0, delay.SelectedIndex)];
            preferences.StrokeWidth = (int)canvas.StrokeWidth;
            if (!highlighterColor) preferences.InkArgb = canvas.InkColor.ToArgb();
            try { preferences.Save(); } catch (Exception) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeSettingsIntegration();
                DisposeRecordingTaskbars();
                DisposeHotkeysAndTray();
                tips.Dispose();
                if (brandMark.Image != null) { brandMark.Image.Dispose(); brandMark.Image = null; }
            }
            base.Dispose(disposing);
        }
    }
}
