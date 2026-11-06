using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    // The recording and every completed trim remain independent files. Clipboard
    // file references stay usable after this window closes.
    public sealed class VideoReviewForm : Form
    {
        private readonly Preferences preferences;
        private readonly string originalPath;
        private string currentPath;
        private string lastSaveFolder;
        private VideoMetadata metadata;
        private bool loading = true;
        private bool working;
        private bool dirty;
        private bool allowClose;
        private bool closePromptPending;
        private CancellationTokenSource exportCancellation;

        private readonly Label heading = new Label();
        private readonly Label description = new Label();
        private readonly Label fileName = new Label();
        private readonly Label details = new Label();
        private readonly Label fileState = new Label();
        private readonly Label rangeInfo = new Label();
        private readonly Label trimExplanation = new Label();
        private readonly Label status = new Label();
        private readonly TextBox pathBox = new TextBox();
        private readonly NumericUpDown startSeconds = new NumericUpDown();
        private readonly NumericUpDown endSeconds = new NumericUpDown();
        private readonly StudioButton applyTrim = new StudioButton();
        private readonly StudioButton play = new StudioButton();
        private readonly StudioButton copy = new StudioButton();
        private readonly StudioButton saveAs = new StudioButton();
        private readonly StudioButton smaller = new StudioButton();
        private readonly StudioButton cancelExport = new StudioButton();
        private readonly StudioButton close = new StudioButton();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());

        public VideoReviewForm(string path, Preferences prefs)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException("path");
            if (prefs == null) throw new ArgumentNullException("prefs");
            originalPath = Path.GetFullPath(path);
            if (!IsVideoFile(originalPath))
                throw new ArgumentException("Choose a video recording such as an .mp4, .mov, .avi, .wmv, .webm or .mkv file.", "path");
            if (!File.Exists(originalPath)) throw new FileNotFoundException("The recording is unavailable.", originalPath);
            currentPath = originalPath;
            preferences = prefs;
            lastSaveFolder = Directory.Exists(prefs.RecordingFolder) ? prefs.RecordingFolder : Path.GetDirectoryName(originalPath);

            Text = "Recording — Snip Studio";
            Font = new Font("Segoe UI", 10f);
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(890, 740);
            MinimumSize = new Size(720, 610);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MainForm.TrySetAppIcon(this);
            BuildLayout();
            UpdateSummary();
            UpdateControls();
            Shown += FitWorkingArea;
            Shown += ReadMetadata;
        }

        private void FitWorkingArea(object sender, EventArgs e)
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int availableWidth = Math.Max(320, area.Width - 32);
            int availableHeight = Math.Max(300, area.Height - 32);
            if (Width <= availableWidth && Height <= availableHeight) return;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, availableWidth), Math.Min(MinimumSize.Height, availableHeight));
            Size = new Size(Math.Min(Width, availableWidth), Math.Min(Height, availableHeight));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MainForm.ApplyDarkCaption(this);
        }

        private void BuildLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(26, 22, 26, 22);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.AutoScroll = true;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            heading.Text = "Your recording, ready.";
            heading.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 0, 0, 7);
            layout.Controls.Add(heading, 0, 0);
            description.Text = "Play, copy, trim, or make a smaller MP4 for email and chat. Your original stays unchanged.";
            description.ForeColor = StudioTheme.Muted;
            description.AutoSize = true;
            description.Margin = new Padding(0, 0, 0, 18);
            layout.Controls.Add(description, 0, 1);

            TableLayoutPanel summary = new TableLayoutPanel();
            summary.Dock = DockStyle.Fill;
            summary.MinimumSize = new Size(0, 155);
            summary.ColumnCount = 2;
            summary.RowCount = 1;
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summary.BackColor = StudioTheme.Surface;
            summary.Padding = new Padding(18, 20, 22, 18);
            summary.Margin = new Padding(0, 0, 0, 18);
            VideoMark mark = new VideoMark();
            mark.Dock = DockStyle.Fill;
            mark.Margin = new Padding(0, 0, 18, 0);
            summary.Controls.Add(mark, 0, 0);
            TableLayoutPanel summaryText = new TableLayoutPanel();
            summaryText.Dock = DockStyle.Fill;
            summaryText.ColumnCount = 1;
            summaryText.RowCount = 3;
            summaryText.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            summaryText.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            summaryText.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            summaryText.Margin = new Padding(0);
            summaryText.BackColor = StudioTheme.Surface;
            fileName.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            fileName.Dock = DockStyle.Fill;
            fileName.AutoEllipsis = true;
            fileName.TextAlign = ContentAlignment.MiddleLeft;
            fileName.Margin = new Padding(0);
            summaryText.Controls.Add(fileName, 0, 0);
            details.Dock = DockStyle.Fill;
            details.TextAlign = ContentAlignment.MiddleLeft;
            details.ForeColor = StudioTheme.Muted;
            details.Margin = new Padding(0);
            summaryText.Controls.Add(details, 0, 1);
            fileState.Dock = DockStyle.Fill;
            fileState.ForeColor = StudioTheme.Accent;
            fileState.TextAlign = ContentAlignment.MiddleLeft;
            fileState.Margin = new Padding(0);
            summaryText.Controls.Add(fileState, 0, 2);
            summary.Controls.Add(summaryText, 1, 0);
            layout.Controls.Add(summary, 0, 2);

            TableLayoutPanel trim = new TableLayoutPanel();
            trim.Dock = DockStyle.Fill;
            trim.AutoSize = true;
            trim.ColumnCount = 1;
            trim.RowCount = 4;
            trim.Padding = new Padding(18, 16, 18, 16);
            trim.BackColor = StudioTheme.Surface;
            trim.Margin = new Padding(0, 0, 0, 16);
            Label trimTitle = new Label();
            trimTitle.Text = "Trim a copy";
            trimTitle.Font = new Font(Font, FontStyle.Bold);
            trimTitle.AutoSize = true;
            trimTitle.Margin = new Padding(0, 0, 0, 6);
            trim.Controls.Add(trimTitle, 0, 0);
            trimExplanation.Text = "Set the start and end in seconds. Apply trim creates a new H.264/AAC MP4; the source recording stays unchanged.";
            trimExplanation.AutoSize = true;
            trimExplanation.ForeColor = StudioTheme.Muted;
            trimExplanation.Margin = new Padding(0, 0, 0, 13);
            trim.Controls.Add(trimExplanation, 0, 1);
            FlowLayoutPanel range = new FlowLayoutPanel();
            range.AutoSize = true;
            range.Dock = DockStyle.Fill;
            range.WrapContents = true;
            range.Margin = new Padding(0);
            range.BackColor = StudioTheme.Surface;
            range.Controls.Add(RangeLabel("Start (s)"));
            ConfigureSeconds(startSeconds, "Trim start in seconds");
            range.Controls.Add(startSeconds);
            range.Controls.Add(RangeLabel("End (s)"));
            ConfigureSeconds(endSeconds, "Trim end in seconds");
            range.Controls.Add(endSeconds);
            ConfigureButton(applyTrim, "Apply trim", "crop", 130);
            applyTrim.Primary = true;
            applyTrim.Margin = new Padding(4, 0, 0, 5);
            applyTrim.Click += ApplyTrim;
            range.Controls.Add(applyTrim);
            trim.Controls.Add(range, 0, 2);
            rangeInfo.AutoSize = true;
            rangeInfo.ForeColor = StudioTheme.Muted;
            rangeInfo.Margin = new Padding(0, 7, 0, 0);
            trim.Controls.Add(rangeInfo, 0, 3);
            layout.Controls.Add(trim, 0, 3);

            pathBox.ReadOnly = true;
            pathBox.Dock = DockStyle.Fill;
            pathBox.BackColor = StudioTheme.Surface;
            pathBox.ForeColor = StudioTheme.Muted;
            pathBox.BorderStyle = BorderStyle.FixedSingle;
            pathBox.Margin = new Padding(0, 0, 0, 12);
            pathBox.AccessibleName = "Current recording file path";
            layout.Controls.Add(pathBox, 0, 4);

            TableLayoutPanel activity = new TableLayoutPanel();
            activity.AutoSize = true;
            activity.Dock = DockStyle.Fill;
            activity.ColumnCount = 1;
            activity.RowCount = 2;
            activity.Margin = new Padding(0, 0, 0, 14);
            status.AutoSize = true;
            status.ForeColor = StudioTheme.Muted;
            status.Text = "Reading video details…";
            status.Margin = new Padding(0, 0, 0, 7);
            status.AccessibleName = "Recording action status";
            activity.Controls.Add(status, 0, 0);
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 35;
            progress.Height = 4;
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0);
            activity.Controls.Add(progress, 0, 1);
            layout.Controls.Add(activity, 0, 5);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.WrapContents = true;
            actions.Margin = new Padding(0);
            ConfigureButton(play, "Play video", "record", 130);
            play.Click += delegate { PlayCurrentFile(); };
            tips.SetToolTip(play, "Open this file in your default video application.");
            actions.Controls.Add(play);
            ConfigureButton(copy, "Copy video", "copy", 138);
            copy.Click += delegate { CopyCurrentFile(false); };
            tips.SetToolTip(copy, "Copy the video file. Paste it into a folder, email, or chat that accepts files.");
            actions.Controls.Add(copy);
            ConfigureButton(saveAs, "Save as…", "save", 126);
            saveAs.Primary = true;
            saveAs.Click += async delegate { await SaveAsAsync(); };
            actions.Controls.Add(saveAs);
            ConfigureButton(smaller, "Make smaller…", "video", 165);
            smaller.Click += MakeSmaller;
            tips.SetToolTip(smaller, "Choose a smaller video quality or target file size, then save a separate MP4 to attach or copy.");
            actions.Controls.Add(smaller);
            ConfigureButton(cancelExport, "Cancel export", "", 145);
            cancelExport.Click += delegate {
                if (exportCancellation == null || exportCancellation.IsCancellationRequested) return;
                exportCancellation.Cancel();
                cancelExport.Enabled = false;
                SetStatus("Cancel requested. Waiting for the current Windows media operation to finish…", false);
            };
            cancelExport.Visible = false;
            actions.Controls.Add(cancelExport);
            ConfigureButton(close, "Close", "", 84);
            close.Click += delegate { Close(); };
            actions.Controls.Add(close);
            layout.Controls.Add(actions, 0, 6);
            CancelButton = close;
            Resize += delegate { UpdateWrapping(); };
            UpdateWrapping();
        }

        private static Label RangeLabel(string value)
        {
            Label label = new Label();
            label.Text = value;
            label.AutoSize = true;
            label.ForeColor = StudioTheme.Muted;
            label.Margin = new Padding(0, 10, 8, 7);
            return label;
        }

        private void ConfigureSeconds(NumericUpDown value, string accessibleName)
        {
            value.DecimalPlaces = 3;
            value.Increment = .100m;
            value.Minimum = 0;
            value.Maximum = 999999999999m;
            value.Width = 118;
            value.BackColor = StudioTheme.Raised;
            value.ForeColor = StudioTheme.Text;
            value.BorderStyle = BorderStyle.FixedSingle;
            value.Margin = new Padding(0, 5, 16, 5);
            value.AccessibleName = accessibleName;
            value.ValueChanged += delegate { UpdateRange(); };
        }

        private static void ConfigureButton(StudioButton button, string label, string glyph, int width)
        {
            button.Text = label;
            button.Glyph = glyph;
            button.Size = new Size(width, 40);
            button.Margin = new Padding(0, 0, 8, 5);
            button.Cursor = Cursors.Hand;
            button.AccessibleName = label;
        }

        private void UpdateWrapping()
        {
            int width = Math.Max(280, ClientSize.Width - 72);
            description.MaximumSize = new Size(width, 0);
            status.MaximumSize = new Size(width, 0);
            trimExplanation.MaximumSize = new Size(Math.Max(250, width - 36), 0);
        }

        private async void ReadMetadata(object sender, EventArgs e)
        {
            string inspectedPath = currentPath;
            try
            {
                VideoMetadata result = await VideoTrimService.InspectAsync(inspectedPath);
                if (IsDisposed || Disposing || currentPath != inspectedPath) return;
                metadata = result;
                ResetRange();
                SetStatus("Ready. Play opens your default video app. Save as makes a separate file.", false);
            }
            catch (Exception error)
            {
                if (IsDisposed || Disposing) return;
                SetStatus("Video details could not be read. You can still play, copy or save the file. " + error.Message, true);
            }
            finally
            {
                if (!IsDisposed && !Disposing)
                {
                    loading = false;
                    UpdateSummary();
                    UpdateControls();
                }
            }
        }

        private void ResetRange()
        {
            if (IsDisposed || Disposing) return;
            if (metadata == null || metadata.Duration <= TimeSpan.Zero) return;
            decimal maximum = Decimal.Ceiling((decimal)metadata.Duration.TotalSeconds * 1000m) / 1000m;
            maximum = Math.Max(.001m, Math.Min(999999999999m, maximum));
            startSeconds.Value = 0;
            endSeconds.Value = 0;
            startSeconds.Maximum = maximum;
            endSeconds.Maximum = maximum;
            endSeconds.Value = maximum;
            UpdateRange();
        }

        private bool ValidRange()
        {
            return metadata != null && metadata.Duration > TimeSpan.Zero && endSeconds.Value > startSeconds.Value &&
                startSeconds.Value < (decimal)metadata.Duration.TotalSeconds;
        }

        private void UpdateRange()
        {
            if (metadata == null) rangeInfo.Text = "Trim controls become available when video details have been read.";
            else if (!ValidRange()) rangeInfo.Text = "Choose an end time after the start time, within the video duration.";
            else rangeInfo.Text = "Selected length: " + (endSeconds.Value - startSeconds.Value).ToString("0.000") + " seconds. Trimming re-encodes the selected range.";
            applyTrim.Enabled = !loading && !working && ValidRange();
        }

        private async void ApplyTrim(object sender, EventArgs e)
        {
            if (loading || working || !ValidRange()) return;
            TimeSpan start = TimeSpan.FromTicks(Decimal.ToInt64(startSeconds.Value * TimeSpan.TicksPerSecond));
            TimeSpan end = TimeSpan.FromTicks(Decimal.ToInt64(endSeconds.Value * TimeSpan.TicksPerSecond));
            if (end > metadata.Duration) end = metadata.Duration;
            if (start == TimeSpan.Zero && end == metadata.Duration)
            {
                SetStatus("Choose a shorter range to trim. Use Save as to copy the complete video.", false);
                return;
            }
            VideoMetadata previousMetadata = metadata;
            string source = currentPath;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipStudio", "VideoEdits");
            string destination = Path.Combine(folder, "Trim " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".mp4");
            SetWorking(true, "Trimming and encoding your recording… Please keep this window open.");
            try
            {
                Directory.CreateDirectory(folder);
                await VideoTrimService.TrimAsync(source, destination, start, end);
                if (IsDisposed || Disposing) return;
                currentPath = destination;
                dirty = true;
                bool verified = true;
                try { metadata = await VideoTrimService.InspectAsync(destination); }
                catch (Exception)
                {
                    verified = false;
                    metadata = new VideoMetadata { Duration = end - start, Width = previousMetadata.Width, Height = previousMetadata.Height,
                        FramesPerSecond = previousMetadata.FramesPerSecond, HasAudio = previousMetadata.HasAudio };
                }
                if (IsDisposed || Disposing) return;
                ResetRange();
                UpdateSummary();
                string message = verified ? "Trimmed copy ready. Save as puts it in the folder you choose. The original recording is unchanged." :
                    "Trimmed file created; its updated details could not be confirmed. Play it to review, then Save as. The original recording is unchanged.";
                if (preferences.AutoCopyRecordingChanges)
                    message += CopyCurrentFile(true) ? " Video file copied." : " Automatic copy was unavailable; try Copy video.";
                SetStatus(message, !verified);
            }
            catch (Exception error)
            {
                SetStatus("The trim could not finish. Your current recording is unchanged. " + error.Message, true);
            }
            finally
            {
                SetWorking(false, null);
            }
        }

        private void PlayCurrentFile()
        {
            if (loading || working) return;
            try
            {
                if (!File.Exists(currentPath)) throw new FileNotFoundException("This recording file is no longer available.");
                Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = true });
                SetStatus("Opened in your default video app.", false);
            }
            catch (Exception error) { SetStatus("The video could not be opened. " + error.Message, true); }
        }

        private async void MakeSmaller(object sender, EventArgs e)
        {
            if (loading || working || metadata == null) return;
            string source = currentPath;
            VideoExportPlan plan;
            try
            {
                using (VideoExportOptionsForm options = new VideoExportOptionsForm(metadata, new FileInfo(source).Length))
                {
                    if (options.ShowDialog(this) != DialogResult.OK) return;
                    plan = options.Plan;
                }
            }
            catch (Exception error) { SetStatus("Export options could not be prepared. " + error.Message, true); return; }
            string destination;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Save a smaller recording copy";
                dialog.Filter = "MP4 video (*.mp4)|*.mp4";
                dialog.DefaultExt = "mp4"; dialog.AddExtension = true; dialog.OverwritePrompt = true;
                dialog.FileName = Path.GetFileNameWithoutExtension(source) + " - email";
                if (Directory.Exists(lastSaveFolder)) dialog.InitialDirectory = lastSaveFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                destination = Path.GetFullPath(dialog.FileName);
            }
            if (String.Equals(destination, source, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(destination, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Choose a different filename. Make smaller preserves the original and current recording.", true);
                return;
            }
            CancellationTokenSource cancellation = new CancellationTokenSource();
            exportCancellation = cancellation;
            SetWorking(true, "Encoding a smaller copy… You can cancel between Windows media operations.");
            progress.Style = ProgressBarStyle.Continuous; progress.Value = 0;
            IProgress<double> updates = new Progress<double>(delegate(double value) {
                if (IsDisposed || Disposing || exportCancellation != cancellation || cancellation.IsCancellationRequested) return;
                int percent = Math.Max(0, Math.Min(100, (int)Math.Round(value * 100)));
                progress.Value = percent;
                SetStatus(percent >= 98 ? "Finishing the MP4 file…" : "Encoding smaller copy… " + percent + "%", false);
            });
            try
            {
                VideoExportResult result = await VideoTrimService.ExportAsync(source, destination, plan, cancellation.Token, updates);
                if (IsDisposed || Disposing) return;
                // Publishing has completed. Late progress or Cancel cannot change
                // that outcome while Windows re-reads the resulting metadata.
                exportCancellation = null;
                cancelExport.Visible = false;
                currentPath = destination;
                dirty = false;
                lastSaveFolder = Path.GetDirectoryName(destination);
                bool inspected = true;
                try { metadata = await VideoTrimService.InspectAsync(destination); }
                catch (Exception) {
                    inspected = false;
                    metadata = new VideoMetadata { Duration = plan.Duration, Width = plan.Width, Height = plan.Height,
                        FramesPerSecond = plan.FramesPerSecond, HasAudio = plan.HasAudio };
                }
                ResetRange(); UpdateSummary();
                bool copied = preferences.AutoCopyRecordingChanges && CopyCurrentFile(true);
                string message = "Saved a separate smaller MP4: " + (result.Bytes / 1000000.0).ToString("0.0") + " MB (was " +
                    (result.SourceBytes / 1000000.0).ToString("0.0") + " MB). The original is unchanged.";
                if (!result.TargetMet) message += " This exceeds the selected " + (result.TargetBytes / 1000000.0).ToString("0") + " MB target; try Small quality or trim its duration.";
                else message += copied ? " The file is copied; paste it into an email or chat that accepts files." : " Use Copy video or attach this file in your email or chat app.";
                if (!inspected) message += " Windows could not re-read the saved video details; the completed file remains available.";
                SetStatus(message, !result.TargetMet || !inspected);
            }
            catch (OperationCanceledException) { SetStatus("Export canceled. Your current recording and any existing destination file are unchanged.", false); }
            catch (Exception error) { SetStatus("The smaller copy could not be saved. Your current recording is unchanged. " + error.Message, true); }
            finally
            {
                if (exportCancellation == cancellation) exportCancellation = null;
                cancellation.Dispose();
                if (!IsDisposed && !Disposing) { progress.Style = ProgressBarStyle.Marquee; SetWorking(false, null); }
            }
        }

        private bool CopyCurrentFile(bool automatic)
        {
            if (!automatic && (loading || working)) return false;
            try
            {
                if (!File.Exists(currentPath)) throw new FileNotFoundException("This recording file is no longer available.");
                StringCollection files = new StringCollection();
                files.Add(currentPath);
                DataObject data = new DataObject();
                data.SetFileDropList(files);
                MainForm.RegisterClipboardIntent();
                Clipboard.SetDataObject(data, true, 4, 100);
                if (!automatic) SetStatus("Video file copied. Paste it into a folder or an app that accepts files.", false);
                return true;
            }
            catch (Exception error)
            {
                if (!automatic) SetStatus("The file could not be copied. Please try Copy video again. " + error.Message, true);
                return false;
            }
        }

        private async Task<bool> SaveAsAsync()
        {
            if (loading || working) return false;
            string source = currentPath;
            string extension = Path.GetExtension(source);
            if (String.IsNullOrEmpty(extension)) extension = ".mp4";
            string destination;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = dirty ? "Save trimmed recording as" : "Save a recording copy";
                dialog.Filter = "Video file (*" + extension + ")|*" + extension;
                dialog.DefaultExt = extension.TrimStart('.');
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = Path.GetFileNameWithoutExtension(originalPath) + (dirty ? " - trimmed" : " - copy");
                if (Directory.Exists(lastSaveFolder)) dialog.InitialDirectory = lastSaveFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                destination = Path.GetFullPath(dialog.FileName);
            }
            if (String.Equals(destination, originalPath, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(destination, source, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Choose a different filename. Save as preserves the original recording and the current file.", true);
                return false;
            }
            if (!String.Equals(Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Use a " + extension + " filename for this recording copy. Save as copies the existing video format.", true);
                return false;
            }
            SetWorking(true, "Saving your recording copy…");
            try
            {
                await Task.Run(delegate { CopyAtomically(source, destination); });
                currentPath = destination;
                dirty = false;
                lastSaveFolder = Path.GetDirectoryName(destination);
                UpdateSummary();
                SetStatus("Saved a separate recording copy. The original recording is unchanged.", false);
                return true;
            }
            catch (Exception error)
            {
                SetStatus("The copy could not be saved. Your current video is still available. " + error.Message, true);
                return false;
            }
            finally { SetWorking(false, null); }
        }

        private static void CopyAtomically(string source, string destination)
        {
            string folder = Path.GetDirectoryName(destination);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
            string temporary = Path.Combine(folder, ".snip-video-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output, 81920);
                    output.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception) { } }
            }
        }

        private void SetWorking(bool value, string message)
        {
            working = value;
            if (message != null) SetStatus(message, false);
            UpdateControls();
        }

        private void UpdateControls()
        {
            if (IsDisposed || Disposing) return;
            bool ready = !loading && !working;
            play.Enabled = ready;
            copy.Enabled = ready;
            saveAs.Enabled = ready;
            smaller.Enabled = ready && metadata != null;
            cancelExport.Visible = exportCancellation != null;
            cancelExport.Enabled = exportCancellation != null && !exportCancellation.IsCancellationRequested;
            startSeconds.Enabled = ready && metadata != null;
            endSeconds.Enabled = ready && metadata != null;
            close.Enabled = !working;
            progress.Visible = loading || working;
            UseWaitCursor = loading || working;
            UpdateRange();
        }

        private void UpdateSummary()
        {
            if (IsDisposed || Disposing) return;
            heading.Text = dirty ? "Your trimmed recording." : "Your recording, ready.";
            fileName.Text = Path.GetFileName(currentPath);
            tips.SetToolTip(fileName, currentPath);
            pathBox.Text = currentPath;
            pathBox.SelectionStart = 0;
            pathBox.SelectionLength = 0;
            tips.SetToolTip(pathBox, currentPath);
            fileState.Text = dirty ? "Working copy — Save as to choose its final location" : "Recording file — original preserved";
            if (metadata == null) details.Text = loading ? "Reading duration, size and audio…" : "Video details unavailable";
            else
            {
                string frames = metadata.FramesPerSecond > 0 ? metadata.FramesPerSecond + " fps" : "Frame rate unavailable";
                details.Text = FormatDuration(metadata.Duration) + "  ·  " + metadata.Width + " × " + metadata.Height + " px\r\n" +
                    frames + "  ·  " + (metadata.HasAudio ? "Audio included" : "No audio") + "  ·  " + FileSize(currentPath);
            }
            Text = (dirty ? "• " : "") + "Recording — Snip Studio";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return ((int)duration.TotalHours).ToString("00") + ":" + duration.Minutes.ToString("00") + ":" +
                duration.Seconds.ToString("00") + "." + duration.Milliseconds.ToString("000");
        }

        private static bool IsVideoFile(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".mp4" || extension == ".m4v" || extension == ".mov" || extension == ".avi" ||
                extension == ".wmv" || extension == ".webm" || extension == ".mkv";
        }

        private static string FileSize(string path)
        {
            try
            {
                long bytes = new FileInfo(path).Length;
                if (bytes >= 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
                if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
                return bytes + " bytes";
            }
            catch (Exception) { return "File size unavailable"; }
        }

        private void SetStatus(string message, bool failure)
        {
            if (IsDisposed || Disposing) return;
            status.Text = message;
            status.ForeColor = failure ? (StudioTheme.IsDark ? Color.FromArgb(255, 169, 151) : Color.FromArgb(150, 42, 25)) : StudioTheme.Muted;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (working)
            {
                e.Cancel = true;
                SetStatus(exportCancellation != null ? "Cancel export to stop, or wait for the copy to finish before closing this window. Windows may need a moment to finish its current media operation." :
                    "Please wait for the current video operation to finish before closing this window.", false);
            }
            else if (!allowClose && dirty && preferences.AskSaveEditedRecordings)
            {
                e.Cancel = true;
                if (!closePromptPending)
                {
                    closePromptPending = true;
                    BeginInvoke((MethodInvoker)ConfirmClose);
                }
            }
            base.OnFormClosing(e);
        }

        private async void ConfirmClose()
        {
            try
            {
                if (IsDisposed || Disposing) return;
                DialogResult answer = MessageBox.Show(this,
                    "Save your trimmed recording to a folder before closing?\n\nThe original recording is unchanged. The working copy remains available at:\n" + currentPath,
                    "Save edited recording", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes && !await SaveAsAsync()) return;
                allowClose = true;
                if (!IsDisposed && !Disposing) BeginInvoke((MethodInvoker)delegate { Close(); });
            }
            catch (Exception error) { SetStatus("The recording window could not close. " + error.Message, true); }
            finally { closePromptPending = false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            // Do not delete working video files: the clipboard or another video app
            // may still reference them after this form has closed.
            base.Dispose(disposing);
        }

        private sealed class VideoMark : Panel
        {
            internal VideoMark()
            {
                DoubleBuffered = true;
                BackColor = StudioTheme.Surface;
                AccessibleName = "Recording file workspace. Use Play video to open playback.";
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int width = Math.Min(86, Math.Max(25, ClientSize.Width - 8));
                int height = width * 3 / 4;
                Rectangle box = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
                using (GraphicsPath outline = StudioTheme.Round(box, 12))
                using (Brush fill = new SolidBrush(StudioTheme.Raised))
                using (Pen border = new Pen(StudioTheme.Border, 2))
                using (Brush ink = new SolidBrush(StudioTheme.Accent))
                {
                    graphics.FillPath(fill, outline);
                    graphics.DrawPath(border, outline);
                    int middleX = box.Left + box.Width / 2;
                    int middleY = box.Top + box.Height / 2;
                    graphics.FillPolygon(ink, new Point[] { new Point(middleX - 8, middleY - 12), new Point(middleX - 8, middleY + 12), new Point(middleX + 12, middleY) });
                }
            }
        }
    }
}
