using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed class SettingsForm : Form
    {
        private readonly Preferences original;
        private readonly Preferences edited;
        private readonly Panel scroll = new Panel();
        private readonly TableLayoutPanel cards = new TableLayoutPanel();
        private readonly Label status = new Label();
        private readonly TextBox screenshotFolder = new TextBox();
        private readonly TextBox recordingFolder = new TextBox();
        private readonly NumericUpDown borderWidth = new SettingsNumericUpDown();
        private readonly Panel borderSwatch = new Panel();
        private readonly Label borderColorLabel = new Label();
        private readonly StudioComboBox theme = new StudioComboBox();
        private readonly TextBox editorPath = new TextBox();
        private readonly TextBox editorName = new TextBox();
        private readonly StudioComboBox detectedEditors = new StudioComboBox();
        private readonly PictureBox detectedEditorIcon = new PictureBox();
        private StudioButton refreshEditors;
        private Label editorDiscoveryStatus;
        private bool populatingEditors;
        private int editorDiscoveryVersion;
        private readonly StudioComboBox microphoneDevices = new StudioComboBox();
        private readonly StudioComboBox systemAudioDevices = new StudioComboBox();
        private Label audioStatus;
        private string audioEnumerationProblem = "";
        private bool refreshingAudio;
        private readonly System.Collections.Generic.List<Control> borderControls = new System.Collections.Generic.List<Control>();
        private readonly System.Collections.Generic.List<Label> wrappingLabels = new System.Collections.Generic.List<Label>();
        private SettingsToggle screenshotCopy, screenshotAutoSave, screenshotAskSave, multipleWindows, screenshotBorder, hdrCorrection;
        private SettingsToggle hideTaskbars, keepPrimaryTaskbar;
        private SettingsToggle recordingCopy, recordingAutoSave, recordingAskSave, defaultMic, defaultSystem;
        private SettingsToggle compactStart, resizeToSnip, showTooltips;
        private SettingsToggle usePrintScreen, keepRunningInTray, startupWithWindows;

        public SettingsForm(Preferences preferences)
        {
            if (preferences == null) throw new ArgumentNullException("preferences");
            original = preferences;
            edited = preferences.Clone();
            Text = "Settings — Snip Studio";
            Font = new Font("Segoe UI", 10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            Size = new Size(860, 850);
            MinimumSize = new Size(700, 580);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            BuildLayout();
            Populate();
            StudioTheme.ApplyTo(this);
            StudioTheme.ThemeChanged += OnThemeChanged;
            Shown += delegate
            {
                Rectangle working = Screen.FromControl(this).WorkingArea;
                if (Width > working.Width - 30 || Height > working.Height - 30)
                {
                    Size = new Size(Math.Min(Width, Math.Max(500, working.Width - 30)), Math.Min(Height, Math.Max(400, working.Height - 30)));
                    Location = new Point(working.Left + (working.Width - Width) / 2, working.Top + (working.Height - Height) / 2);
                }
                Reflow();
                scroll.AutoScrollPosition = Point.Empty;
                RefreshInstalledEditors();
            };
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Padding = new Padding(0);
            Controls.Add(root);

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.Height = 105;
            header.Margin = new Padding(0);
            header.Padding = new Padding(28, 22, 28, 14);
            header.BackColor = StudioTheme.Surface;
            root.Controls.Add(header, 0, 0);
            Label heading = new Label();
            heading.Text = "Make it yours.";
            heading.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            heading.AutoSize = true;
            heading.Location = new Point(26, 18);
            heading.ForeColor = StudioTheme.Text;
            header.Controls.Add(heading);
            Label subtitle = new Label();
            subtitle.Text = "Capture, save, and share the way you work.";
            subtitle.AutoSize = true;
            subtitle.Location = new Point(28, 63);
            subtitle.ForeColor = StudioTheme.Muted;
            header.Controls.Add(subtitle);

            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = StudioTheme.Background;
            scroll.Margin = new Padding(0);
            scroll.Padding = new Padding(24, 20, 24, 20);
            root.Controls.Add(scroll, 0, 1);
            cards.AutoSize = true;
            cards.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            cards.ColumnCount = 1;
            cards.RowCount = 0;
            cards.Location = new Point(24, 20);
            cards.Margin = new Padding(0);
            cards.Padding = new Padding(0, 0, 0, 20);
            scroll.Controls.Add(cards);
            scroll.Resize += delegate { Reflow(); };

            TableLayoutPanel screenshot = Card("Screen captures", "A clear path from your screen to your next task.");
            screenshotCopy = AddToggle(screenshot, "Automatically copy changes", "Copy the image to the clipboard after capture and after edits.");
            screenshotAutoSave = AddToggle(screenshot, "Automatically save original screenshots", "Keep an original image in the screenshot folder after each capture.");
            AddFolder(screenshot, "Screenshot folder", screenshotFolder);
            screenshotAskSave = AddToggle(screenshot, "Ask to save edited screenshots", "Prompt before closing or replacing a snip with unsaved edits.");
            multipleWindows = AddToggle(screenshot, "Open snips in separate windows", "Keep earlier snips open when starting a new capture.");
            screenshotBorder = AddToggle(screenshot, "Add a border to screenshots", "Place an outline around new captures using the color and width below.");
            AddBorderOptions(screenshot);
            screenshotBorder.CheckedChanged += delegate { UpdateBorderOptions(); };
            hdrCorrection = AddToggle(screenshot, "HDR screenshot color correction", "Use color correction when capturing screenshots from HDR displays.");
            hideTaskbars = AddToggle(screenshot, "Hide taskbars during capture", "Hide taskbars for screenshots and while recording video. Screenshot taskbars return before selection; video taskbars return when recording stops or is canceled.");
            keepPrimaryTaskbar = AddToggle(screenshot, "Keep main monitor taskbar visible", "Keep the main monitor taskbar visible for directions or context in screenshots and videos. Turn this off to hide taskbars on every monitor.");
            hideTaskbars.CheckedChanged += delegate { keepPrimaryTaskbar.Enabled = hideTaskbars.Checked; };

            TableLayoutPanel recording = Card("Screen recordings", "Set the defaults for your next recording.");
            recordingCopy = AddToggle(recording, "Automatically copy recording changes", "Copy the current recording to the clipboard after capture or edits.");
            recordingAutoSave = AddToggle(recording, "Automatically save original recordings", "Keep an original recording in the recording folder.");
            AddFolder(recording, "Recording folder", recordingFolder);
            recordingAskSave = AddToggle(recording, "Ask to save edited recordings", "Prompt before closing an edited recording that has not been saved.");
            defaultMic = AddToggle(recording, "Microphone on by default", "Start new recording setups with microphone audio selected. You can change it before recording.");
            AddAudioDevicePicker(recording, "Microphone source", microphoneDevices);
            defaultSystem = AddToggle(recording, "System audio on by default", "Start new recording setups with computer audio selected. You can change it before recording.");
            AddAudioDevicePicker(recording, "System audio playback device", systemAudioDevices);
            AddBody(recording, "Microphone audio comes from an input device. System audio records what plays through the selected output device.", true);
            StudioButton refreshAudio = Button("Refresh audio devices", "redo", 222);
            refreshAudio.Margin = new Padding(0, 7, 0, 3);
            refreshAudio.Click += delegate { RefreshAudioDevices(); };
            AddRow(recording, refreshAudio);
            audioStatus = AddBody(recording, "Audio devices have not been loaded yet.", true);
            audioStatus.Font = new Font("Segoe UI", 9.2f);
            defaultMic.CheckedChanged += delegate { microphoneDevices.Enabled = defaultMic.Checked; UpdateAudioChoiceStatus(); };
            defaultSystem.CheckedChanged += delegate { systemAudioDevices.Enabled = defaultSystem.Checked; UpdateAudioChoiceStatus(); };
            microphoneDevices.SelectedIndexChanged += delegate { UpdateAudioChoiceStatus(); };
            systemAudioDevices.SelectedIndexChanged += delegate { UpdateAudioChoiceStatus(); };

            TableLayoutPanel appearance = Card("Workspace", "A comfortable fit for your screen and your snips.");
            AddTheme(appearance);
            showTooltips = AddToggle(appearance, "Show tooltips", "Explain buttons and controls when you pause over them. On by default; turn this off for fewer pop-up hints.");
            compactStart = AddToggle(appearance, "Start in a compact window", "Open with a small capture workspace instead of a large empty canvas.");
            resizeToSnip = AddToggle(appearance, "Resize the window to each snip", "Adjust the preview window to the captured image while keeping it within the screen.");

            TableLayoutPanel editor = Card("External editor", "Send a copy of your snip to the editing app you prefer.");
            AddExternalEditor(editor);

            TableLayoutPanel shortcuts = Card("Shortcuts", "Capture without losing your place.");
            Label shortcut = AddBody(shortcuts, "Ctrl + Alt + S", false);
            shortcut.Font = new Font(Font, FontStyle.Bold);
            shortcut.ForeColor = StudioTheme.Text;
            AddBody(shortcuts, "Start a capture from another app while Snip Studio is open. If another app uses this shortcut, use New or Ctrl + N in Snip Studio.", true);
            usePrintScreen = AddToggle(shortcuts, "Use Print Screen to capture", "Capture with Print Screen while Snip Studio is running. If Windows or another app reserves that key, try Ctrl + Alt + S or New.");
            keepRunningInTray = AddToggle(shortcuts, "Keep running in the notification area", "Optional: Close hides the main window and keeps shortcuts active. Otherwise, closing the last snip exits. Use Exit Snip Studio to quit.");
            startupWithWindows = AddToggle(shortcuts, "Start with Windows", "Start when you sign in. Starts quietly if notification-area mode is enabled; otherwise opens the main window. Off until you choose it.");
            AddBody(shortcuts, "Print Screen behavior is controlled by Windows. Review its assignment in Windows keyboard settings; Snip Studio does not change that assignment.", true);
            StudioButton keyboard = Button("Windows keyboard settings", "settings", 252);
            keyboard.Margin = new Padding(0, 8, 0, 0);
            keyboard.Click += delegate { OpenKeyboardSettings(); };
            AddRow(shortcuts, keyboard);

            TableLayoutPanel privacy = Card("Your files", "Preferences are stored on this PC.");
            AddBody(privacy, "Screenshots and recordings are saved to the folders shown above. Those folders’ existing backup or sync settings still apply.", true);
            AddBody(privacy, "Changes take effect after Save. Cancel leaves your current settings unchanged.", true);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.Height = 114;
            footer.Margin = new Padding(0);
            footer.BackColor = StudioTheme.Surface;
            footer.Padding = new Padding(26, 12, 26, 16);
            root.Controls.Add(footer, 0, 2);
            status.Text = "Your changes are applied when you select Save.";
            status.ForeColor = StudioTheme.Muted;
            status.AutoEllipsis = true;
            status.Location = new Point(28, 12);
            status.Size = new Size(780, 24);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            footer.Controls.Add(status);
            StudioButton save = Button("Save", "save", 112);
            save.Primary = true;
            save.Location = new Point(28, 50);
            save.Click += delegate { SaveChanges(); };
            footer.Controls.Add(save);
            StudioButton cancel = Button("Cancel", "", 100);
            cancel.Location = new Point(150, 50);
            cancel.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private TableLayoutPanel Card(string title, string description)
        {
            TableLayoutPanel card = new TableLayoutPanel();
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.ColumnCount = 1;
            card.Padding = new Padding(20, 18, 20, 20);
            card.BackColor = StudioTheme.Surface;
            card.Margin = new Padding(0, 0, 0, 16);
            card.Dock = DockStyle.Top;
            AddRow(cards, card);
            Label heading = AddBody(card, title, false);
            heading.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            heading.ForeColor = StudioTheme.Text;
            heading.Margin = new Padding(0, 0, 0, 4);
            Label sub = AddBody(card, description, true);
            sub.Margin = new Padding(0, 0, 0, 10);
            return card;
        }

        private static void AddRow(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, row);
        }

        private Label AddBody(TableLayoutPanel card, string text, bool muted)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.ForeColor = muted ? StudioTheme.Muted : StudioTheme.Text;
            label.Margin = new Padding(0, 4, 0, 5);
            label.MaximumSize = new Size(680, 0);
            wrappingLabels.Add(label);
            AddRow(card, label);
            return label;
        }

        private SettingsToggle AddToggle(TableLayoutPanel card, string title, string description)
        {
            SettingsToggle toggle = new SettingsToggle();
            toggle.Text = title;
            toggle.AccessibleName = title;
            toggle.AccessibleDescription = description;
            toggle.Dock = DockStyle.Top;
            toggle.Width = 680;
            toggle.Height = 42;
            toggle.Margin = new Padding(0, 8, 0, 0);
            toggle.BackColor = StudioTheme.Surface;
            toggle.ForeColor = StudioTheme.Text;
            AddRow(card, toggle);
            Label details = AddBody(card, description, true);
            details.Font = new Font("Segoe UI", 9.2f);
            details.Margin = new Padding(0, 0, 104, 10);
            return toggle;
        }

        private void AddFolder(TableLayoutPanel card, string title, TextBox path)
        {
            Label label = AddBody(card, title, false);
            label.Font = new Font("Segoe UI", 9.3f, FontStyle.Bold);
            label.Margin = new Padding(0, 5, 0, 6);
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.ColumnCount = 3;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Margin = new Padding(0, 0, 0, 12);
            path.Dock = DockStyle.Fill;
            path.BackColor = StudioTheme.Input;
            path.ForeColor = StudioTheme.Text;
            path.BorderStyle = BorderStyle.FixedSingle;
            path.Margin = new Padding(0, 7, 10, 0);
            path.AccessibleName = title;
            row.Controls.Add(path, 0, 0);
            StudioButton browse = Button("Browse…", "open", 110);
            browse.Margin = new Padding(0, 0, 6, 0);
            browse.Click += delegate { ChooseFolder(path, title); };
            row.Controls.Add(browse, 1, 0);
            StudioButton open = Button("Open", "open", 88);
            open.Margin = new Padding(0);
            open.Click += delegate { OpenFolder(path); };
            row.Controls.Add(open, 2, 0);
            AddRow(card, row);
        }

        private void AddBorderOptions(TableLayoutPanel card)
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.AutoSize = true;
            row.Dock = DockStyle.Top;
            row.WrapContents = true;
            row.Margin = new Padding(0, 1, 0, 8);
            StudioButton color = Button("Color…", "color", 112);
            color.Click += delegate
            {
                using (ColorPaletteDialog dialog = new ColorPaletteDialog(Color.FromArgb(edited.ScreenshotBorderArgb), null))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    edited.ScreenshotBorderArgb = Color.FromArgb(255, dialog.SelectedColor).ToArgb();
                    UpdateBorderSwatch();
                }
            };
            row.Controls.Add(color);
            borderControls.Add(color);
            borderSwatch.Size = new Size(28, 28);
            borderSwatch.Margin = new Padding(4, 6, 6, 0);
            borderSwatch.BorderStyle = BorderStyle.FixedSingle;
            borderSwatch.Tag = "PreserveColor";
            row.Controls.Add(borderSwatch);
            borderColorLabel.AutoSize = true;
            borderColorLabel.ForeColor = StudioTheme.Muted;
            borderColorLabel.Margin = new Padding(0, 11, 18, 0);
            row.Controls.Add(borderColorLabel);
            Label widthLabel = new Label();
            widthLabel.Text = "Width";
            widthLabel.AutoSize = true;
            widthLabel.ForeColor = StudioTheme.Muted;
            widthLabel.Margin = new Padding(0, 11, 7, 0);
            row.Controls.Add(widthLabel);
            borderWidth.Minimum = 1;
            borderWidth.Maximum = 20;
            borderWidth.Width = 66;
            borderWidth.BackColor = StudioTheme.Input;
            borderWidth.ForeColor = StudioTheme.Text;
            borderWidth.Margin = new Padding(0, 7, 5, 0);
            borderWidth.AccessibleName = "Screenshot border width in pixels";
            row.Controls.Add(borderWidth);
            borderControls.Add(borderWidth);
            Label pixels = new Label();
            pixels.Text = "px";
            pixels.AutoSize = true;
            pixels.ForeColor = StudioTheme.Muted;
            pixels.Margin = new Padding(0, 11, 0, 0);
            row.Controls.Add(pixels);
            AddRow(card, row);
        }

        private void AddTheme(TableLayoutPanel card)
        {
            Label label = AddBody(card, "App theme", false);
            label.Font = new Font(Font, FontStyle.Bold);
            theme.Items.AddRange(new object[] { "Dark", "Light", "Use Windows setting" });
            theme.Width = 240;
            theme.Margin = new Padding(0, 3, 0, 12);
            theme.AccessibleName = "App theme";
            AddRow(card, theme);
        }

        private void AddAudioDevicePicker(TableLayoutPanel card, string label, StudioComboBox choices)
        {
            Label heading = AddBody(card, label, false);
            heading.Font = new Font("Segoe UI", 9.3f, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 5);
            choices.Dock = DockStyle.Top;
            choices.Width = 600;
            choices.Margin = new Padding(0, 0, 0, 10);
            choices.AccessibleName = label;
            choices.DropDownWidth = 640;
            AddRow(card, choices);
        }

        private void RefreshAudioDevices()
        {
            AudioChoice oldMic = microphoneDevices.SelectedItem as AudioChoice;
            AudioChoice oldSystem = systemAudioDevices.SelectedItem as AudioChoice;
            string micId = oldMic == null ? (edited.DefaultMicrophoneDeviceId ?? "") : oldMic.Id;
            string systemId = oldSystem == null ? (edited.DefaultSystemAudioDeviceId ?? "") : oldSystem.Id;
            string problem = "";
            refreshingAudio = true;
            try { FillAudioChoices(microphoneDevices, AudioCaptureMixer.GetMicrophoneDevices(), micId, oldMic); }
            catch (Exception error)
            {
                FillAudioChoices(microphoneDevices, null, micId, oldMic);
                problem = "Microphones could not be listed: " + error.Message;
            }
            try { FillAudioChoices(systemAudioDevices, AudioCaptureMixer.GetSystemAudioDevices(), systemId, oldSystem); }
            catch (Exception error)
            {
                FillAudioChoices(systemAudioDevices, null, systemId, oldSystem);
                problem += (problem.Length > 0 ? " " : "") + "Playback devices could not be listed: " + error.Message;
            }
            microphoneDevices.Enabled = defaultMic.Checked;
            systemAudioDevices.Enabled = defaultSystem.Checked;
            audioEnumerationProblem = problem;
            refreshingAudio = false;
            UpdateAudioChoiceStatus();
        }

        private void UpdateAudioChoiceStatus()
        {
            if (audioStatus == null || refreshingAudio) return;
            bool missingMic = microphoneDevices.SelectedItem is AudioChoice && !((AudioChoice)microphoneDevices.SelectedItem).Available;
            bool missingSystem = systemAudioDevices.SelectedItem is AudioChoice && !((AudioChoice)systemAudioDevices.SelectedItem).Available;
            int micCount = 0, systemCount = 0;
            foreach (AudioChoice choice in microphoneDevices.Items) if (choice.Available && choice.Id.Length > 0) micCount++;
            foreach (AudioChoice choice in systemAudioDevices.Items) if (choice.Available && choice.Id.Length > 0) systemCount++;
            bool noActiveDevice = (defaultMic.Checked && micCount == 0) || (defaultSystem.Checked && systemCount == 0);
            if (!String.IsNullOrEmpty(audioEnumerationProblem)) audioStatus.Text = audioEnumerationProblem + " Saved choices are kept. Choose Windows default or reconnect the device before recording.";
            else if (missingMic || missingSystem) audioStatus.Text = "A saved audio device is unavailable. Its choice is kept; reconnect it or select another source before recording.";
            else if (noActiveDevice) audioStatus.Text = "No active " + (defaultMic.Checked && micCount == 0 ? "microphone" : "playback device") + " was found. Connect a device and Refresh, or turn off that audio source before recording.";
            else audioStatus.Text = "Choose Windows default to follow Windows, or select a particular device. Refresh after connecting or disconnecting audio hardware.";
            audioStatus.ForeColor = String.IsNullOrEmpty(audioEnumerationProblem) && !missingMic && !missingSystem && !noActiveDevice ? StudioTheme.Muted : StudioTheme.Accent;
        }

        private static void FillAudioChoices(StudioComboBox target, System.Collections.Generic.IList<AudioDeviceInfo> devices, string selectedId, AudioChoice previous)
        {
            target.Items.Clear();
            target.Items.Add(new AudioChoice("", "Windows default", true));
            int selectedIndex = String.IsNullOrEmpty(selectedId) ? 0 : -1;
            if (devices != null)
            {
                foreach (AudioDeviceInfo device in devices)
                {
                    if (device == null || String.IsNullOrEmpty(device.Id)) continue;
                    int index = target.Items.Add(new AudioChoice(device.Id, String.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name, true));
                    if (String.Equals(device.Id, selectedId, StringComparison.Ordinal)) selectedIndex = index;
                }
            }
            if (selectedIndex < 0)
            {
                string name = previous == null ? "Saved device" : previous.Name;
                selectedIndex = target.Items.Add(new AudioChoice(selectedId, name, false));
            }
            target.SelectedIndex = selectedIndex;
        }

        private static string SelectedAudioDeviceId(StudioComboBox choices)
        {
            AudioChoice selected = choices.SelectedItem as AudioChoice;
            return selected == null ? "" : selected.Id;
        }

        private sealed class AudioChoice
        {
            internal readonly string Id;
            internal readonly string Name;
            internal readonly bool Available;
            internal AudioChoice(string id, string name, bool available)
            {
                Id = id ?? "";
                Name = name;
                Available = available;
            }
            public override string ToString() { return Available ? Name : "Unavailable — " + Name; }
        }

        private void AddExternalEditor(TableLayoutPanel card)
        {
            AddBody(card, "Choose a Windows application, such as Photoshop. The Edit in action opens a copy of the current image in that app.", true);
            Label detectedLabel = AddBody(card, "Detected image editors", false);
            detectedLabel.Font = new Font(Font, FontStyle.Bold);
            TableLayoutPanel detectedRow = new TableLayoutPanel();
            detectedRow.AutoSize = true;
            detectedRow.Dock = DockStyle.Top;
            detectedRow.ColumnCount = 3;
            detectedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            detectedRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detectedRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            detectedRow.Margin = new Padding(0, 0, 0, 4);
            detectedEditorIcon.Size = new Size(28, 28);
            detectedEditorIcon.SizeMode = PictureBoxSizeMode.Zoom;
            detectedEditorIcon.Margin = new Padding(0, 6, 10, 0);
            detectedEditorIcon.BackColor = Color.Transparent;
            detectedEditorIcon.Tag = "PreserveColor";
            detectedEditorIcon.AccessibleName = "Selected editing application icon";
            detectedRow.Controls.Add(detectedEditorIcon, 0, 0);
            detectedEditors.Dock = DockStyle.Fill;
            detectedEditors.DropDownWidth = 520;
            detectedEditors.AccessibleName = "Detected image editing applications";
            detectedEditors.Margin = new Padding(0, 7, 10, 0);
            detectedEditors.SelectedIndexChanged += delegate { ChooseDetectedEditor(); };
            detectedRow.Controls.Add(detectedEditors, 1, 0);
            refreshEditors = Button("Refresh", "redo", 112);
            refreshEditors.Margin = new Padding(0);
            refreshEditors.Click += delegate { RefreshInstalledEditors(); };
            detectedRow.Controls.Add(refreshEditors, 2, 0);
            AddRow(card, detectedRow);
            editorDiscoveryStatus = AddBody(card, "Installed image editors will be listed here. Choose app also supports portable or unlisted editors.", true);
            editorDiscoveryStatus.Font = new Font("Segoe UI", 9.2f);
            Label label = AddBody(card, "Application", false);
            label.Font = new Font(Font, FontStyle.Bold);
            TableLayoutPanel pathRow = new TableLayoutPanel();
            pathRow.AutoSize = true;
            pathRow.Dock = DockStyle.Top;
            pathRow.ColumnCount = 2;
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Margin = new Padding(0, 0, 0, 8);
            ConfigureTextBox(editorPath, "External editor application path");
            editorPath.ReadOnly = true;
            editorPath.Dock = DockStyle.Fill;
            editorPath.Margin = new Padding(0, 7, 10, 0);
            pathRow.Controls.Add(editorPath, 0, 0);
            StudioButton choose = Button("Choose app…", "open", 152);
            choose.Margin = new Padding(0);
            choose.Click += delegate { ChooseExternalEditor(); };
            pathRow.Controls.Add(choose, 1, 0);
            AddRow(card, pathRow);
            Label nameLabel = AddBody(card, "Name shown on the Edit in button", false);
            nameLabel.Font = new Font("Segoe UI", 9.3f, FontStyle.Bold);
            ConfigureTextBox(editorName, "External editor button name");
            editorName.Width = 280;
            editorName.Margin = new Padding(0, 3, 0, 8);
            editorName.MaxLength = 60;
            AddRow(card, editorName);
            StudioButton reset = Button("Use Paint", "pen", 136);
            reset.Margin = new Padding(0, 4, 0, 0);
            reset.Click += delegate
            {
                Preferences defaults = new Preferences();
                editorPath.Text = defaults.EditorApplicationPath;
                editorName.Text = defaults.EditorApplicationName;
                SelectEditorFromFields();
            };
            AddRow(card, reset);
        }

        private async void RefreshInstalledEditors()
        {
            int version = ++editorDiscoveryVersion;
            detectedEditors.Enabled = false;
            refreshEditors.Enabled = false;
            editorDiscoveryStatus.Text = "Looking for installed image editors…";
            editorDiscoveryStatus.ForeColor = StudioTheme.Muted;
            try
            {
                System.Collections.Generic.IList<InstalledEditor> found = await InstalledEditorFinder.DiscoverAsync();
                if (IsDisposed || Disposing || version != editorDiscoveryVersion) return;
                ShowDetectedEditors(found);
            }
            catch (Exception)
            {
                if (IsDisposed || Disposing || version != editorDiscoveryVersion) return;
                editorDiscoveryStatus.Text = "Installed apps could not be listed. Your current choice is kept; use Choose app or try Refresh.";
                editorDiscoveryStatus.ForeColor = StudioTheme.Accent;
                SelectEditorFromFields();
            }
            finally
            {
                if (!IsDisposed && !Disposing && version == editorDiscoveryVersion)
                {
                    detectedEditors.Enabled = true;
                    refreshEditors.Enabled = true;
                }
            }
        }

        private void ShowDetectedEditors(System.Collections.Generic.IList<InstalledEditor> found)
        {
            bool previous = populatingEditors;
            populatingEditors = true;
            try
            {
                // Read current fields after discovery: a manual choice made
                // while the search was running must not be overwritten.
                detectedEditors.Items.Clear();
                detectedEditors.Items.Add(new InstalledEditor { Name = "Choose an installed editor…", ExecutablePath = "" });
                int detected = 0;
                if (found != null)
                    foreach (InstalledEditor app in found)
                        if (app != null && File.Exists(app.ExecutablePath)) { detectedEditors.Items.Add(app); detected++; }
                SelectEditorFromFields();
                editorDiscoveryStatus.Text = detected > 0
                    ? "Choose a detected app, or use Choose app for a portable or unlisted editor."
                    : "No image editors were detected. Use Choose app to select an installed or portable editor.";
            }
            finally { populatingEditors = previous; }
        }

        private void ChooseDetectedEditor()
        {
            if (populatingEditors) return;
            InstalledEditor app = detectedEditors.SelectedItem as InstalledEditor;
            if (app == null || String.IsNullOrEmpty(app.ExecutablePath)) return;
            if (!File.Exists(app.ExecutablePath))
            {
                editorDiscoveryStatus.Text = "That app is no longer available at the detected path. Refresh or use Choose app.";
                editorDiscoveryStatus.ForeColor = StudioTheme.Accent;
                return;
            }
            editorPath.Text = app.ExecutablePath;
            editorName.Text = app.Name;
            UpdateEditorIcon(app.ExecutablePath);
        }

        private void SelectEditorFromFields()
        {
            bool previous = populatingEditors;
            populatingEditors = true;
            try
            {
                if (detectedEditors.Items.Count == 0)
                    detectedEditors.Items.Add(new InstalledEditor { Name = "Choose an installed editor…", ExecutablePath = "" });
                int selected = 0;
                if (!String.IsNullOrEmpty(editorPath.Text))
                {
                    for (int i = 1; i < detectedEditors.Items.Count; i++)
                    {
                        InstalledEditor app = detectedEditors.Items[i] as InstalledEditor;
                        if (app != null && String.Equals(app.ExecutablePath, editorPath.Text, StringComparison.OrdinalIgnoreCase)) { selected = i; break; }
                    }
                    if (selected == 0)
                        selected = detectedEditors.Items.Add(new InstalledEditor {
                            Name = String.IsNullOrWhiteSpace(editorName.Text) ? "Current editing application" : editorName.Text,
                            ExecutablePath = editorPath.Text
                        });
                }
                detectedEditors.SelectedIndex = selected;
                UpdateEditorIcon(editorPath.Text);
            }
            finally { populatingEditors = previous; }
        }

        private void UpdateEditorIcon(string path)
        {
            Image replacement = null;
            try
            {
                if (File.Exists(path))
                    using (Icon icon = Icon.ExtractAssociatedIcon(path))
                        if (icon != null) replacement = icon.ToBitmap();
            }
            catch (Exception) { }
            Image previous = detectedEditorIcon.Image;
            detectedEditorIcon.Image = replacement;
            if (previous != null) previous.Dispose();
        }

        private static void ConfigureTextBox(TextBox box, string accessibleName)
        {
            box.BackColor = StudioTheme.Input;
            box.ForeColor = StudioTheme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.AccessibleName = accessibleName;
        }

        private void ChooseExternalEditor()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Choose an image editing application";
                dialog.Filter = "Windows applications (*.exe)|*.exe";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (File.Exists(editorPath.Text))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(editorPath.Text);
                    dialog.FileName = Path.GetFileName(editorPath.Text);
                }
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string path = ValidateEditorPath(dialog.FileName);
                    editorPath.Text = path;
                    FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                    string name = String.IsNullOrWhiteSpace(version.ProductName) ? version.FileDescription : version.ProductName;
                    if (String.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
                    editorName.Text = name.Trim();
                    SelectEditorFromFields();
                }
                catch (Exception error) { ShowProblem("That application could not be selected. " + error.Message); }
            }
        }

        private static StudioButton Button(string text, string glyph, int width)
        {
            StudioButton button = new StudioButton();
            button.Text = text;
            button.Glyph = glyph;
            button.Size = new Size(width, 40);
            button.Margin = new Padding(0, 0, 6, 0);
            button.Cursor = Cursors.Hand;
            button.AccessibleName = text;
            return button;
        }

        private void Populate()
        {
            screenshotCopy.Checked = edited.AutoCopyScreenshotChanges;
            screenshotAutoSave.Checked = edited.AutoSaveOriginalScreenshots;
            screenshotFolder.Text = edited.ScreenshotFolder ?? "";
            screenshotAskSave.Checked = edited.AskSaveEditedScreenshots;
            multipleWindows.Checked = edited.MultipleWindows;
            screenshotBorder.Checked = edited.AddScreenshotBorder;
            borderWidth.Value = Math.Max(borderWidth.Minimum, Math.Min(borderWidth.Maximum, edited.ScreenshotBorderWidth));
            hdrCorrection.Checked = edited.HdrColorCorrection;
            hideTaskbars.Checked = edited.HideTaskbarsDuringCapture;
            keepPrimaryTaskbar.Checked = edited.KeepPrimaryTaskbarVisible;
            keepPrimaryTaskbar.Enabled = hideTaskbars.Checked;
            recordingCopy.Checked = edited.AutoCopyRecordingChanges;
            recordingAutoSave.Checked = edited.AutoSaveOriginalRecordings;
            recordingFolder.Text = edited.RecordingFolder ?? "";
            recordingAskSave.Checked = edited.AskSaveEditedRecordings;
            defaultMic.Checked = edited.DefaultMicrophone;
            defaultSystem.Checked = edited.DefaultSystemAudio;
            RefreshAudioDevices();
            compactStart.Checked = edited.StartCompact;
            showTooltips.Checked = edited.ShowTooltips;
            resizeToSnip.Checked = edited.ResizeToSnip;
            usePrintScreen.Checked = edited.UsePrintScreen;
            keepRunningInTray.Checked = edited.KeepRunningInTray;
            startupWithWindows.Checked = edited.StartupWithWindows;
            editorPath.Text = edited.EditorApplicationPath ?? "";
            editorName.Text = edited.EditorApplicationName ?? "Paint";
            theme.SelectedIndex = edited.Theme == "Light" ? 1 : edited.Theme == "System" ? 2 : 0;
            UpdateBorderSwatch();
            UpdateBorderOptions();
            SelectEditorFromFields();
        }

        private void UpdateBorderSwatch()
        {
            Color color = Color.FromArgb(edited.ScreenshotBorderArgb);
            borderSwatch.BackColor = Color.FromArgb(255, color);
            borderColorLabel.Text = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private void UpdateBorderOptions()
        {
            if (screenshotBorder == null) return;
            foreach (Control control in borderControls) control.Enabled = screenshotBorder.Checked;
            // Labels and the chosen swatch are information, not disabled actions.
            // Native disabled Label rendering ignores ForeColor on some themes.
            borderColorLabel.Enabled = true;
            borderColorLabel.ForeColor = StudioTheme.Muted;
            borderSwatch.Enabled = true;
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { OnThemeChanged(sender, e); }); }
                catch (InvalidOperationException) { }
                return;
            }
            StudioTheme.ApplyTo(this);
            UpdateBorderOptions();
            borderWidth.Invalidate(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                editorDiscoveryVersion++;
                StudioTheme.ThemeChanged -= OnThemeChanged;
                if (detectedEditorIcon.Image != null) { detectedEditorIcon.Image.Dispose(); detectedEditorIcon.Image = null; }
            }
            base.Dispose(disposing);
        }

        private void Reflow()
        {
            if (scroll.ClientSize.Width <= 0) return;
            int available = Math.Max(420, scroll.ClientSize.Width - scroll.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            cards.Width = available;
            foreach (Control control in cards.Controls)
            {
                TableLayoutPanel card = control as TableLayoutPanel;
                if (card == null) continue;
                card.MinimumSize = new Size(available, 0);
                card.MaximumSize = new Size(available, 0);
                foreach (Control row in card.Controls)
                {
                    SettingsToggle toggle = row as SettingsToggle;
                    if (toggle != null) toggle.Width = Math.Max(300, available - card.Padding.Horizontal);
                }
            }
            foreach (Label label in wrappingLabels)
                label.MaximumSize = new Size(Math.Max(200, available - 40 - label.Margin.Horizontal), 0);
        }

        private void ChooseFolder(TextBox target, string title)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the " + title.ToLowerInvariant() + ".";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
            }
        }

        private void OpenFolder(TextBox target)
        {
            try
            {
                string folder = ValidateFolder(target.Text);
                if (!Directory.Exists(folder))
                {
                    status.Text = "That folder does not exist yet. It will be created when a file is saved there.";
                    status.ForeColor = StudioTheme.Accent;
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception error) { ShowProblem("The folder could not be opened. " + error.Message); }
        }

        private void OpenKeyboardSettings()
        {
            try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:easeofaccess-keyboard", UseShellExecute = true }); }
            catch (Exception error) { ShowProblem("Windows keyboard settings could not be opened. " + error.Message); }
        }

        private static string ValidateFolder(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Choose a folder for screenshots and a folder for recordings.");
            string path = value.Trim();
            if (!Path.IsPathRooted(path)) throw new InvalidOperationException("Enter a complete folder path or use Browse.");
            path = Path.GetFullPath(path);
            if (File.Exists(path)) throw new InvalidOperationException("Choose a folder, not an existing file.");
            return path;
        }

        private static string ValidateEditorPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            string path = value.Trim();
            if (!Path.IsPathRooted(path)) throw new InvalidOperationException("Choose the editing app with Choose app.");
            path = Path.GetFullPath(path);
            if (!String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("Choose an existing Windows .exe application.");
            return path;
        }

        private void SaveChanges()
        {
            try
            {
                edited.AutoCopyScreenshotChanges = screenshotCopy.Checked;
                edited.AutoSaveOriginalScreenshots = screenshotAutoSave.Checked;
                edited.ScreenshotFolder = ValidateFolder(screenshotFolder.Text);
                edited.AskSaveEditedScreenshots = screenshotAskSave.Checked;
                edited.MultipleWindows = multipleWindows.Checked;
                edited.AddScreenshotBorder = screenshotBorder.Checked;
                edited.ScreenshotBorderWidth = (int)borderWidth.Value;
                edited.HdrColorCorrection = hdrCorrection.Checked;
                edited.HideTaskbarsDuringCapture = hideTaskbars.Checked;
                edited.KeepPrimaryTaskbarVisible = keepPrimaryTaskbar.Checked;
                edited.AutoCopyRecordingChanges = recordingCopy.Checked;
                edited.AutoSaveOriginalRecordings = recordingAutoSave.Checked;
                edited.RecordingFolder = ValidateFolder(recordingFolder.Text);
                edited.AskSaveEditedRecordings = recordingAskSave.Checked;
                edited.DefaultMicrophone = defaultMic.Checked;
                edited.DefaultSystemAudio = defaultSystem.Checked;
                edited.DefaultMicrophoneDeviceId = SelectedAudioDeviceId(microphoneDevices);
                edited.DefaultSystemAudioDeviceId = SelectedAudioDeviceId(systemAudioDevices);
                edited.Theme = theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "System" : "Dark";
                edited.StartCompact = compactStart.Checked;
                edited.ShowTooltips = showTooltips.Checked;
                edited.ResizeToSnip = resizeToSnip.Checked;
                edited.UsePrintScreen = usePrintScreen.Checked;
                edited.KeepRunningInTray = keepRunningInTray.Checked;
                edited.TrayBehaviorConfirmed = true;
                edited.StartupWithWindows = startupWithWindows.Checked;
                edited.EditorApplicationPath = ValidateEditorPath(editorPath.Text);
                edited.EditorApplicationName = editorName.Text.Trim();
                if (!String.IsNullOrEmpty(edited.EditorApplicationPath) && String.IsNullOrWhiteSpace(edited.EditorApplicationName))
                    throw new InvalidOperationException("Enter a name for the Edit in button.");
                edited.Save();
                original.CopyFrom(edited);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error) { ShowProblem("Settings could not be saved. " + error.Message); }
        }

        private void ShowProblem(string message)
        {
            status.Text = message;
            status.ForeColor = StudioTheme.Accent;
            MessageBox.Show(this, message, "Snip Studio settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private sealed class SettingsToggle : CheckBox
        {
            private bool hovered;

            internal SettingsToggle()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AutoSize = false;
                Cursor = Cursors.Hand;
            }

            protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(BackColor);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = graphics.DpiX / 96f;
                int inset = (int)(3 * scale);
                Rectangle textBounds = new Rectangle(0, 0, Math.Max(10, Width - (int)(116 * scale)), Height);
                Color ink = Enabled ? StudioTheme.Text : StudioTheme.Muted;
                TextRenderer.DrawText(graphics, Text, Font, textBounds, ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                int switchWidth = (int)(42 * scale), switchHeight = (int)(24 * scale);
                RectangleF toggle = new RectangleF(Width - switchWidth - inset, (Height - switchHeight) / 2, switchWidth, switchHeight);
                Color fill = Checked ? StudioTheme.Accent : StudioTheme.Raised;
                using (GraphicsPath shape = StudioTheme.Round(toggle, switchHeight / 2))
                {
                    using (Brush brush = new SolidBrush(fill)) graphics.FillPath(brush, shape);
                    using (Pen border = new Pen(Checked ? StudioTheme.Accent : hovered ? StudioTheme.Muted : StudioTheme.Border, Math.Max(1, scale))) graphics.DrawPath(border, shape);
                }
                float diameter = switchHeight - 6 * scale;
                float knobX = Checked ? toggle.Right - diameter - 3 * scale : toggle.Left + 3 * scale;
                using (Brush brush = new SolidBrush(Checked ? StudioTheme.PrimaryText : StudioTheme.Text))
                    graphics.FillEllipse(brush, knobX, toggle.Top + 3 * scale, diameter, diameter);
                Rectangle state = new Rectangle(Width - (int)(91 * scale), 0, (int)(40 * scale), Height);
                TextRenderer.DrawText(graphics, Checked ? "On" : "Off", Font, state, StudioTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = ClientRectangle;
                    focus.Inflate(-2, -2);
                    ControlPaint.DrawFocusRectangle(graphics, focus, StudioTheme.Accent, BackColor);
                }
            }
        }

        // The native disabled UpDownEdit can paint system black on a dark input,
        // even when NumericUpDown.ForeColor is set. Keep native spin/input behavior
        // but replace only its disabled value drawing with a theme-aware readout.
        private sealed class SettingsNumericUpDown : NumericUpDown
        {
            [DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
            private DisabledValueReadout readout;

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x020A)
                {
                    ForwardWheel(message.WParam, message.LParam);
                    message.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref message);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                // The native edit and spin-button children can dispatch directly
                // to this event instead of the parent's window procedure.
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
                Point screen = PointToScreen(e.Location);
                int coordinates = (screen.X & 0xffff) | ((screen.Y & 0xffff) << 16);
                ForwardWheel(new IntPtr(e.Delta << 16), new IntPtr(coordinates));
            }

            private void ForwardWheel(IntPtr keysAndDelta, IntPtr coordinates)
            {
                for (Control parent = Parent; parent != null; parent = parent.Parent)
                {
                    ScrollableControl target = parent as ScrollableControl;
                    if (target == null || !target.AutoScroll) continue;
                    SendMessage(target.Handle, 0x020A, keysAndDelta, coordinates);
                    break;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                EnsureReadout();
                RefreshReadout();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                EnsureReadout();
                RefreshReadout();
            }

            protected override void OnValueChanged(EventArgs e)
            {
                base.OnValueChanged(e);
                RefreshReadout();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                RefreshReadout();
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                RefreshReadout();
            }

            private void EnsureReadout()
            {
                if (readout != null || IsDisposed) return;
                foreach (Control child in Controls)
                {
                    if (!(child is TextBoxBase)) continue;
                    readout = new DisabledValueReadout();
                    readout.Dock = DockStyle.Fill;
                    readout.TabStop = false;
                    readout.AccessibleRole = AccessibleRole.StaticText;
                    child.Controls.Add(readout);
                    break;
                }
            }

            private void RefreshReadout()
            {
                if (readout == null || readout.IsDisposed) return;
                readout.Text = Text;
                readout.Font = Font;
                readout.Visible = !Enabled;
                readout.Invalidate();
            }
        }

        private sealed class DisabledValueReadout : Control
        {
            internal DisabledValueReadout()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(StudioTheme.Input);
                Rectangle textBounds = ClientRectangle;
                textBounds.Inflate(-1, 0);
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, StudioTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
