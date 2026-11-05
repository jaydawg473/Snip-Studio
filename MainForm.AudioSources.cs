using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private readonly ComboBox microphoneDevices = new StudioComboBox();
        private readonly ComboBox systemAudioDevices = new StudioComboBox();
        private StudioButton refreshAudioSources;
        private readonly Label audioSourceHint = new Label();
        private bool loadingAudioSources, applyingAudioSources, audioLoadQueued;
        private int audioLoadVersion;
        private string desiredMicrophoneId = "", desiredSystemAudioId = "";

        private string SelectedMicrophoneDeviceId { get { return desiredMicrophoneId ?? ""; } }
        private string SelectedSystemAudioDeviceId { get { return desiredSystemAudioId ?? ""; } }

        private void AddAudioSourceSelectors(FlowLayoutPanel bar)
        {
            bar.Controls.Remove(recordMicrophone);
            bar.Controls.Remove(recordSystemAudio);
            AddAudioGroup(bar, recordMicrophone, microphoneDevices, "Microphone source");
            AddAudioGroup(bar, recordSystemAudio, systemAudioDevices, "System audio source");
            tips.SetToolTip(recordMicrophone, "Include audio from the microphone selected below. An unavailable selected source causes a visible recording error.");
            tips.SetToolTip(recordSystemAudio, "Include sound from the playback device selected below. An unavailable selected source causes a visible recording error.");
            refreshAudioSources = ActionButton("", "redo", 48, delegate { LoadAudioSources(); });
            refreshAudioSources.AccessibleName = "Refresh audio sources";
            refreshAudioSources.Margin = new Padding(0, 18, 0, 0);
            tips.SetToolTip(refreshAudioSources, "Refresh connected microphones and playback devices");
            bar.Controls.Add(refreshAudioSources);
            audioSourceHint.AutoSize = true;
            audioSourceHint.MaximumSize = new Size(750, 0);
            audioSourceHint.ForeColor = StudioTheme.Muted;
            audioSourceHint.Font = new Font(Font.FontFamily, 8.5f);
            audioSourceHint.Margin = new Padding(12, 12, 0, 0);
            audioSourceHint.Visible = false;
            bar.Controls.Add(audioSourceHint);
            microphoneDevices.SelectedIndexChanged += delegate
            {
                if (applyingAudioSources) return;
                desiredMicrophoneId = SelectedDeviceId(microphoneDevices);
                preferences.DefaultMicrophoneDeviceId = desiredMicrophoneId;
                UpdateAudioDeviceTooltips();
            };
            systemAudioDevices.SelectedIndexChanged += delegate
            {
                if (applyingAudioSources) return;
                desiredSystemAudioId = SelectedDeviceId(systemAudioDevices);
                preferences.DefaultSystemAudioDeviceId = desiredSystemAudioId;
                UpdateAudioDeviceTooltips();
            };
            recordMicrophone.CheckedChanged += delegate { UpdateAudioSourceControlState(); };
            recordSystemAudio.CheckedChanged += delegate { UpdateAudioSourceControlState(); };
        }

        private void AddAudioGroup(FlowLayoutPanel bar, CheckBox enabled, ComboBox devices, string accessibleName)
        {
            TableLayoutPanel group = new TableLayoutPanel();
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Size = Size.Empty;
            group.RowCount = 2;
            group.ColumnCount = 1;
            group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            group.Margin = new Padding(0, 0, 12, 0);
            enabled.Margin = new Padding(1, 0, 0, 5);
            group.Controls.Add(enabled, 0, 0);
            devices.Width = 190;
            devices.DropDownWidth = 480;
            devices.DropDownStyle = ComboBoxStyle.DropDownList;
            devices.AccessibleName = accessibleName;
            devices.Margin = new Padding(0);
            devices.Items.Add(new AudioDeviceInfo { Id = "", Name = "Windows default" });
            devices.SelectedIndex = 0;
            group.Controls.Add(devices, 0, 1);
            bar.Controls.Add(group);
        }

        private void ApplyAudioSourcePreferences()
        {
            desiredMicrophoneId = preferences.DefaultMicrophoneDeviceId ?? "";
            desiredSystemAudioId = preferences.DefaultSystemAudioDeviceId ?? "";
            LoadAudioSources();
        }

        private void LoadAudioSources()
        {
            if (IsDisposed || capturePending) return;
            if (!IsHandleCreated)
            {
                if (!audioLoadQueued)
                {
                    audioLoadQueued = true;
                    HandleCreated += OnAudioSourcesHandleCreated;
                }
                return;
            }
            int version = ++audioLoadVersion;
            loadingAudioSources = true;
            audioSourceHint.Text = "Finding audio sources…";
            audioSourceHint.Visible = true;
            UpdateAudioSourceControlState();
            Task.Run(delegate
            {
                AudioSourceLists lists = new AudioSourceLists();
                try { lists.Microphones = AudioCaptureMixer.GetMicrophoneDevices(); }
                catch (Exception ex) { lists.MicrophoneError = ex.Message; }
                try { lists.Outputs = AudioCaptureMixer.GetSystemAudioDevices(); }
                catch (Exception ex) { lists.OutputError = ex.Message; }
                return lists;
            }).ContinueWith(delegate(Task<AudioSourceLists> task)
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed || version != audioLoadVersion) return;
                        loadingAudioSources = false;
                        if (task.IsFaulted)
                        {
                            GC.KeepAlive(task.Exception);
                            audioSourceHint.Text = "Audio sources could not be listed. Refresh to try again.";
                            audioSourceHint.Visible = true;
                            UpdateAudioSourceControlState();
                            return;
                        }
                        AudioSourceLists lists = task.Result;
                        applyingAudioSources = true;
                        try
                        {
                            PopulateAudioChoices(microphoneDevices, lists.Microphones, desiredMicrophoneId);
                            PopulateAudioChoices(systemAudioDevices, lists.Outputs, desiredSystemAudioId);
                        }
                        finally { applyingAudioSources = false; }
                        bool unavailable = !String.IsNullOrEmpty(lists.MicrophoneError) || !String.IsNullOrEmpty(lists.OutputError);
                        audioSourceHint.Text = unavailable ? "Some audio sources could not be listed. Refresh to try again." : "";
                        audioSourceHint.Visible = unavailable;
                        tips.SetToolTip(audioSourceHint, String.Join("\n", new string[] { lists.MicrophoneError ?? "", lists.OutputError ?? "" }).Trim());
                        UpdateAudioDeviceTooltips();
                        UpdateAudioSourceControlState();
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void OnAudioSourcesHandleCreated(object sender, EventArgs e)
        {
            HandleCreated -= OnAudioSourcesHandleCreated;
            audioLoadQueued = false;
            LoadAudioSources();
        }

        private static void PopulateAudioChoices(ComboBox box, IList<AudioDeviceInfo> devices, string selectedId)
        {
            box.Items.Clear();
            box.Items.Add(new AudioDeviceInfo { Id = "", Name = "Windows default" });
            int selection = 0;
            if (devices != null)
                foreach (AudioDeviceInfo device in devices)
                {
                    box.Items.Add(new AudioDeviceInfo { Id = device.Id, Name = device.Name });
                    if (String.Equals(device.Id, selectedId, StringComparison.Ordinal)) selection = box.Items.Count - 1;
                }
            if (selection == 0 && !String.IsNullOrEmpty(selectedId))
            {
                box.Items.Add(new AudioDeviceInfo { Id = selectedId, Name = "Unavailable source — choose another" });
                selection = box.Items.Count - 1;
            }
            box.SelectedIndex = selection;
        }

        private static string SelectedDeviceId(ComboBox box)
        {
            AudioDeviceInfo device = box.SelectedItem as AudioDeviceInfo;
            return device == null ? "" : device.Id ?? "";
        }

        private void UpdateAudioDeviceTooltips()
        {
            tips.SetToolTip(microphoneDevices, DeviceDescription(microphoneDevices));
            tips.SetToolTip(systemAudioDevices, DeviceDescription(systemAudioDevices));
        }

        private static string DeviceDescription(ComboBox box)
        {
            AudioDeviceInfo device = box.SelectedItem as AudioDeviceInfo;
            if (device == null || String.IsNullOrEmpty(device.Id)) return "Use the Windows default device when recording starts.";
            return device.ToString() + "\nThis exact source is used. If it disconnects, recording reports an error instead of switching sources.";
        }

        private void UpdateAudioSourceControlState()
        {
            microphoneDevices.Enabled = !capturePending && !loadingAudioSources && recordMicrophone.Checked;
            systemAudioDevices.Enabled = !capturePending && !loadingAudioSources && recordSystemAudio.Checked;
            if (refreshAudioSources != null) refreshAudioSources.Enabled = !capturePending && !loadingAudioSources;
        }

        private sealed class AudioSourceLists
        {
            internal IList<AudioDeviceInfo> Microphones, Outputs;
            internal string MicrophoneError, OutputError;
        }
    }
}
