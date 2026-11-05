using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private readonly CheckBox recordMicrophone = new CheckBox();
        private readonly CheckBox recordSystemAudio = new CheckBox();
        private StudioButton recordButton;
        private ScreenRecorder screenRecorder;
        private RecordingControlForm recordingControl;
        private bool recordingSession, recordingStarting, stoppingRecording, closeAfterRecording;
        private string recordingPath;
        private FormWindowState recordingRestoreState;
        private Exception recordingFailure;
        private TaskbarVisibilityScope recordingTaskbars;
        private Timer recordingTaskbarTimer;
        private Exception recordingTaskbarFailure;

        private FlowLayoutPanel BuildRecordingBar()
        {
            FlowLayoutPanel bar = Bar(new Padding(22, 0, 22, 13));
            bar.BackColor = StudioTheme.Surface;
            recordButton = ActionButton("Record screen", "record", 150, delegate { StartRecording(); });
            recordButton.Margin = new Padding(0, 8, 12, 0);
            tips.SetToolTip(recordButton, "Choose a screen area, then record video with the audio sources selected here. Your current snip stays open.");
            bar.Controls.Add(recordButton);
            recordButton.Visible = false;
            ConfigureAudioChoice(recordMicrophone, "Microphone");
            ConfigureAudioChoice(recordSystemAudio, "System audio");
            bar.Controls.Add(recordMicrophone);
            bar.Controls.Add(recordSystemAudio);
            AddAudioSourceSelectors(bar);
            Label specification = BarLabel("MP4 • up to 1080p • 15 fps");
            specification.Font = new Font(Font.FontFamily, 8.5f);
            specification.Margin = new Padding(8, 12, 0, 0);
            tips.SetToolTip(bar, "MP4 • up to 1080p • 15 fps");
            return bar;
        }

        private void ConfigureAudioChoice(CheckBox choice, string label)
        {
            choice.Text = label;
            choice.Checked = true;
            choice.AutoSize = true;
            choice.FlatStyle = FlatStyle.Flat;
            choice.ForeColor = StudioTheme.Text;
            choice.Font = new Font(Font.FontFamily, 9f);
            choice.Margin = new Padding(0, 11, 8, 0);
            choice.AccessibleName = "Record " + label.ToLowerInvariant();
            tips.SetToolTip(choice, label == "Microphone" ?
                "Include audio from the default microphone. Recording will report an error if access or the device is unavailable." :
                "Include sound playing through the default output device. Recording will report an error if that source is unavailable.");
        }

        private void UpdateRecordingControls()
        {
            if (recordButton != null) recordButton.Enabled = !capturePending;
            recordMicrophone.Enabled = !capturePending;
            recordSystemAudio.Enabled = !capturePending;
            mode.Enabled = !capturePending;
            delay.Enabled = !capturePending;
            UpdateAudioSourceControlState();
        }

        private async void StartRecording()
        {
            if (capturePending || recordingSession || closing || IsDisposed || Disposing) return;
            string destination;
            if (!ChooseRecordingDestination(out destination)) return;

            if (!String.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Choose an .mp4 filename for the recording.");
                return;
            }
            bool microphone = recordMicrophone.Checked;
            bool systemAudio = recordSystemAudio.Checked;
            string microphoneDeviceId = SelectedMicrophoneDeviceId;
            string systemAudioDeviceId = SelectedSystemAudioDeviceId;
            bool hideTaskbars = preferences.HideTaskbarsDuringCapture;
            bool keepPrimaryVisible = preferences.KeepPrimaryTaskbarVisible;
            capturePending = true;
            recordingSession = true;
            recordingStarting = true;
            recordingFailure = null;
            recordingTaskbarFailure = null;
            recordingPath = destination;
            recordingRestoreState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            UpdateDocumentState();
            Hide();
            bool started = false;
            Exception startupFailure = null;
            try { started = await BeginRecordingAsync(destination, microphone, systemAudio, microphoneDeviceId, systemAudioDeviceId, hideTaskbars, keepPrimaryVisible); }
            catch (Exception ex) { recordingFailure = ex; startupFailure = ex; }
            if (!started)
            {
                recordingStarting = false;
                Exception restoreFailure = null;
                try
                {
                    ScreenRecorder recorder = screenRecorder;
                    if (recorder != null)
                    {
                        recorder.StatusChanged -= OnRecordingStatus;
                        try { await recorder.StopAsync(); } catch (Exception) { }
                        recorder.Dispose();
                        if (Object.ReferenceEquals(screenRecorder, recorder)) screenRecorder = null;
                    }
                }
                finally
                {
                    try { CloseRecordingController(); }
                    finally
                    {
                        restoreFailure = ReleaseRecordingTaskbars();
                        recordingSession = false;
                        capturePending = false;
                        RestoreAfterRecording();
                    }
                }
                if (IsDisposed || Disposing) return;
                if (startupFailure != null) ShowFailure("Recording could not start. Your snip is unchanged", startupFailure);
                if (restoreFailure != null) ShowFailure("Windows could not confirm taskbar restoration", restoreFailure);
                if (closeAfterRecording && !IsDisposed) { closeAfterRecording = false; Close(); }
            }
        }

        private async Task<bool> BeginRecordingAsync(string destination, bool microphone, bool systemAudio, string microphoneDeviceId, string systemAudioDeviceId, bool hideTaskbars, bool keepPrimaryVisible)
        {
            await Task.Delay(220);
            if (closing || IsDisposed || Disposing) return false;
            Rectangle? region = CaptureService.SelectRecordingRegion(hideTaskbars, keepPrimaryVisible);
            if (closing || IsDisposed || Disposing || closeAfterRecording) return false;
            if (!region.HasValue)
            {
                SetStatus("Recording canceled. Your snip is unchanged.");
                return false;
            }
            if (hideTaskbars) BeginRecordingTaskbars(keepPrimaryVisible);
            if (!CaptureCountdown.WaitForCapture(3))
            {
                if (!IsDisposed && !Disposing) SetStatus("Recording canceled. Your snip is unchanged.");
                return false;
            }
            await Task.Delay(220);
            if (closeAfterRecording || closing || IsDisposed || Disposing)
            {
                if (!IsDisposed && !Disposing) SetStatus("Recording canceled. Your snip is unchanged.");
                return false;
            }
            if (recordingTaskbarFailure != null) throw recordingTaskbarFailure;
            if (recordingTaskbars != null) recordingTaskbars.VerifyHidden();

            ScreenRecordingOptions options = new ScreenRecordingOptions();
            options.RecordMicrophone = microphone;
            options.RecordSystemAudio = systemAudio;
            options.FramesPerSecond = 15;
            options.IncludeCursor = true;
            options.MicrophoneDeviceId = microphoneDeviceId;
            options.SystemAudioDeviceId = systemAudioDeviceId;
            StopRecordingTaskbarTimer();
            TaskbarVisibilityScope taskbars = recordingTaskbars;
            if (taskbars != null) options.BeforeFrameCapture = taskbars.PrepareFrame;
            ScreenRecorder recorder = new ScreenRecorder();
            screenRecorder = recorder;
            recorder.StatusChanged += OnRecordingStatus;
            await recorder.StartAsync(region.Value, destination, options);
            if (IsDisposed || Disposing || closing || !recordingSession)
            {
                try { await recorder.StopAsync(); } catch (Exception) { }
                return false;
            }
            if (recordingFailure != null) throw recordingFailure;
            if (recordingTaskbarFailure != null) throw recordingTaskbarFailure;
            recordingStarting = false;
            recordingControl = new RecordingControlForm(microphone, systemAudio);
            recordingControl.StopRequested += delegate { StopRecording(); };
            recordingControl.Show();
            recordingControl.UpdateRecording(recorder.Duration, "Recording your selected area. Stop saves the video.");
            if (closeAfterRecording) StopRecording();
            return true;
        }

        private void OnRecordingStatus(object sender, RecordingStatusEventArgs e)
        {
            if (IsDisposed || Disposing || !recordingSession) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((MethodInvoker)delegate { OnRecordingStatus(sender, e); }); }
                catch (InvalidOperationException) { }
                return;
            }
            if (recordingControl != null && !recordingControl.IsDisposed)
                recordingControl.UpdateRecording(e.Duration, e.Message);
            if (e.Error != null)
            {
                recordingFailure = e.Error;
                if (recordingControl != null) StopRecording();
            }
        }

        private async void StopRecording()
        {
            if (!recordingSession || stoppingRecording) return;
            // Startup, selection, and the countdown retain control until a recorder exists.
            if (screenRecorder == null || recordingStarting) return;
            ScreenRecorder recorder = screenRecorder;
            stoppingRecording = true;
            if (recordingControl != null)
                recordingControl.SetStopping("Saving your recording. Please wait…");
            bool saved = false;
            Exception failure = recordingFailure;
            Exception taskbarFailure = recordingTaskbarFailure;
            Exception restoreFailure = null;
            try
            {
                await recorder.StopAsync();
                if (recordingFailure != null) throw recordingFailure;
                saved = true;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    recorder.StatusChanged -= OnRecordingStatus;
                    recorder.Dispose();
                    if (Object.ReferenceEquals(screenRecorder, recorder)) screenRecorder = null;
                    CloseRecordingController();
                }
                finally
                {
                    restoreFailure = ReleaseRecordingTaskbars();
                    recordingSession = false;
                    stoppingRecording = false;
                    capturePending = false;
                    RestoreAfterRecording();
                }
            }

            if (IsDisposed || Disposing) return;
            taskbarFailure = recordingTaskbarFailure ?? taskbarFailure;
            if (saved)
            {
                preferences.SaveFolder = Path.GetDirectoryName(recordingPath);
                SetStatus("Recording saved: " + recordingPath);
                try { OnRecordingSaved(recordingPath); }
                catch (Exception ex) { ShowFailure("The recording was saved, but an automatic action could not finish", ex); }
                if (taskbarFailure != null) ShowFailure("The recording was saved, but ended early because Windows could not keep a taskbar hidden", taskbarFailure);
                if (restoreFailure != null) ShowFailure("The recording was saved, but Windows could not confirm taskbar restoration", restoreFailure);
                using (VideoReviewForm review = new VideoReviewForm(recordingPath, preferences)) review.ShowDialog(this);
            }
            else
            {
                ShowFailure("Recording could not be saved. Your snip is unchanged",
                    failure ?? new InvalidOperationException("The recording did not finish successfully."));
                if (restoreFailure != null) ShowFailure("Windows could not confirm taskbar restoration", restoreFailure);
            }
            if (closeAfterRecording && !IsDisposed) { closeAfterRecording = false; Close(); }
        }

        private void RestoreAfterRecording()
        {
            if (closing || IsDisposed || Disposing) return;
            WindowState = recordingRestoreState;
            Show();
            Activate();
            canvas.Focus();
            UpdateDocumentState();
        }

        private void CloseRecordingController()
        {
            if (recordingControl == null) return;
            if (recordingControl.IsDisposed) { recordingControl = null; return; }
            recordingControl.AllowClose = true;
            recordingControl.Close();
            recordingControl.Dispose();
            recordingControl = null;
        }

        private void BeginRecordingTaskbars(bool keepPrimaryVisible)
        {
            if (recordingTaskbars != null) throw new InvalidOperationException("Recording taskbars are already prepared.");
            recordingTaskbars = TaskbarVisibilityScope.Hide(keepPrimaryVisible);
            recordingTaskbarTimer = new Timer();
            recordingTaskbarTimer.Interval = 200;
            recordingTaskbarTimer.Tick += delegate
            {
                if (recordingTaskbars == null || IsDisposed || Disposing) return;
                try { recordingTaskbars.MaintainHidden(); }
                catch (Exception error)
                {
                    recordingTaskbarFailure = error;
                    recordingTaskbarTimer.Stop();
                    if (screenRecorder != null && !recordingStarting) StopRecording();
                }
            };
            recordingTaskbarTimer.Start();
        }

        private Exception ReleaseRecordingTaskbars()
        {
            StopRecordingTaskbarTimer();
            TaskbarVisibilityScope taskbars = recordingTaskbars;
            recordingTaskbars = null;
            if (taskbars == null) return null;
            try { taskbars.Dispose(); return null; }
            catch (Exception error) { return error; }
        }

        private void StopRecordingTaskbarTimer()
        {
            if (recordingTaskbarTimer != null)
            {
                recordingTaskbarTimer.Stop();
                recordingTaskbarTimer.Dispose();
                recordingTaskbarTimer = null;
            }
        }

        // Called by MainForm.Dispose on the UI thread. Normal close waits through
        // StopRecording first. Direct disposal prioritizes restoring the desktop
        // even if a native device or encoder is taking time to stop.
        private void DisposeRecordingTaskbars()
        {
            StopRecordingTaskbarTimer();
            try
            {
                ScreenRecorder recorder = screenRecorder;
                if (recorder != null)
                {
                    recorder.StatusChanged -= OnRecordingStatus;
                    try
                    {
                        recorder.StopAsync().ContinueWith(delegate(Task completed)
                        {
                            if (completed.IsFaulted && completed.Exception != null)
                                Trace.WriteLine("Recording shutdown: " + completed.Exception.GetBaseException().Message);
                        }, TaskScheduler.Default);
                    }
                    catch (Exception error) { Trace.WriteLine("Recording shutdown: " + error.Message); }
                    recorder.Dispose();
                    if (Object.ReferenceEquals(screenRecorder, recorder)) screenRecorder = null;
                }
            }
            finally
            {
                Exception error = ReleaseRecordingTaskbars();
                if (error != null) Trace.WriteLine("Taskbar restoration: " + error.Message);
                CloseRecordingController();
            }
        }

        private void ShowRecordingSaved(string path)
        {
            using (Form saved = new Form())
            {
                saved.Text = "Recording saved — Snip Studio";
                saved.StartPosition = FormStartPosition.CenterParent;
                saved.Font = Font;
                saved.AutoScaleMode = AutoScaleMode.Dpi;
                saved.AutoScaleDimensions = new SizeF(96, 96);
                saved.ClientSize = new Size(620, 222);
                saved.FormBorderStyle = FormBorderStyle.FixedDialog;
                saved.MinimizeBox = saved.MaximizeBox = false;
                saved.BackColor = StudioTheme.Background;
                saved.ForeColor = StudioTheme.Text;
                TrySetAppIcon(saved);
                saved.HandleCreated += delegate { ApplyDarkCaption(saved); };
                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(26, 22, 26, 20);
                layout.ColumnCount = 1;
                layout.RowCount = 3;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Label title = new Label();
                title.Text = "Your recording is ready";
                title.Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
                title.AutoSize = true;
                title.Margin = new Padding(0, 0, 0, 14);
                layout.Controls.Add(title, 0, 0);
                Label filename = new Label();
                filename.Text = path;
                filename.Dock = DockStyle.Fill;
                filename.ForeColor = StudioTheme.Muted;
                filename.AutoEllipsis = true;
                filename.Margin = new Padding(0, 0, 0, 16);
                tips.SetToolTip(filename, path);
                layout.Controls.Add(filename, 0, 1);
                FlowLayoutPanel actions = Bar(new Padding(0));
                actions.WrapContents = false;
                actions.Controls.Add(ActionButton("Open video", "open", 126, delegate { OpenRecordingPath(path); }));
                actions.Controls.Add(ActionButton("Open folder", "open", 130, delegate { OpenRecordingPath(Path.GetDirectoryName(path)); }));
                StudioButton done = ActionButton("Done", "", 90, delegate { saved.Close(); });
                done.Primary = true;
                done.DialogResult = DialogResult.Cancel;
                actions.Controls.Add(done);
                saved.CancelButton = done;
                layout.Controls.Add(actions, 0, 2);
                saved.Controls.Add(layout);
                saved.ShowDialog(this);
            }
        }

        private void OpenRecordingPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { ShowFailure("The saved recording could not be opened", ex); }
        }
    }
}
