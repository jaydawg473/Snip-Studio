using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    /// <summary>A recording controller only; the owner manages the recorder and final file.</summary>
    public sealed class RecordingControlForm : Form
    {
        private readonly Label elapsed = new Label();
        private readonly Label detail = new Label();
        private readonly StudioButton stop = new StudioButton();
        private bool stopRequested;
        private bool allowClose;

        public event EventHandler StopRequested;
        public bool CaptureExclusionSupported { get; private set; }
        public bool AllowClose { get { return allowClose; } set { allowClose = value; } }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WindowsVersion
        {
            internal int Size = Marshal.SizeOf(typeof(WindowsVersion));
            internal int Major;
            internal int Minor;
            internal int Build;
            internal int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion([In, Out] WindowsVersion version);

        public RecordingControlForm(bool microphone, bool systemAudio)
        {
            Text = "Recording — Snip Studio";
            Font = new Font("Segoe UI", 9.5f);
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(456, 152);
            MinimumSize = new Size(400, 182);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            MainForm.TrySetAppIcon(this);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20, 15, 20, 16);
            layout.ColumnCount = 2;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            FlowLayoutPanel clock = new FlowLayoutPanel();
            clock.AutoSize = true;
            clock.WrapContents = false;
            clock.Margin = new Padding(0);
            Label dot = new Label();
            dot.Text = "●";
            dot.ForeColor = Color.FromArgb(255, 102, 115);
            dot.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            dot.AutoSize = true;
            dot.Margin = new Padding(0, 8, 7, 0);
            dot.AccessibleName = "Recording indicator";
            clock.Controls.Add(dot);
            elapsed.Text = "00:00";
            elapsed.Font = new Font("Segoe UI Semibold", 24f, FontStyle.Bold);
            elapsed.ForeColor = StudioTheme.Text;
            elapsed.AutoSize = true;
            elapsed.Margin = new Padding(0);
            elapsed.AccessibleName = "Recording elapsed time";
            clock.Controls.Add(elapsed);
            layout.Controls.Add(clock, 0, 0);

            stop.Text = "Stop";
            stop.Glyph = "stop";
            stop.Primary = true;
            stop.Size = new Size(100, 42);
            stop.Margin = new Padding(14, 4, 0, 0);
            stop.Cursor = Cursors.Hand;
            stop.AccessibleName = "Stop recording and save";
            stop.Click += delegate { RequestStop(); };
            layout.Controls.Add(stop, 1, 0);

            Label audio = new Label();
            audio.AutoSize = true;
            audio.Margin = new Padding(1, 7, 0, 0);
            audio.ForeColor = StudioTheme.Muted;
            audio.Text = microphone && systemAudio ? "Microphone + system audio" : microphone ? "Microphone audio" : systemAudio ? "System audio" : "No audio";
            layout.Controls.Add(audio, 0, 1);
            layout.SetColumnSpan(audio, 2);

            detail.Dock = DockStyle.Fill;
            detail.Margin = new Padding(1, 9, 0, 0);
            detail.ForeColor = StudioTheme.Muted;
            detail.Text = "Recording your selected area. Stop saves the video.";
            detail.AutoEllipsis = true;
            detail.AccessibleName = "Recording status";
            layout.Controls.Add(detail, 0, 2);
            layout.SetColumnSpan(detail, 2);
            Shown += delegate
            {
                Rectangle working = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(working.Right - Width - 24, working.Top + 24);
                if (!CaptureExclusionSupported)
                    detail.Text = "Keep this panel outside your recorded area.";
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MainForm.ApplyDarkCaption(this);
            CaptureExclusionSupported = false;
            try
            {
                WindowsVersion version = new WindowsVersion();
                // Earlier Windows releases treat 0x11 as a blacked-out window rather than
                // excluding it, so do not advertise successful exclusion on those releases.
                bool supportsExclusion = RtlGetVersion(version) == 0 &&
                    (version.Major > 10 || (version.Major == 10 && version.Build >= 19041));
                if (supportsExclusion)
                    CaptureExclusionSupported = SetWindowDisplayAffinity(Handle, 0x00000011);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public void UpdateRecording(TimeSpan duration, string message)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    try { BeginInvoke((MethodInvoker)delegate { UpdateRecording(duration, message); }); }
                    catch (InvalidOperationException) { }
                }
                return;
            }
            elapsed.Text = duration.TotalHours >= 1 ?
                ((int)duration.TotalHours).ToString("00") + ":" + duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00") :
                ((int)duration.TotalMinutes).ToString("00") + ":" + duration.Seconds.ToString("00");
            if (!stopRequested && !String.IsNullOrEmpty(message)) detail.Text = message;
        }

        public void SetStopping(string message)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    try { BeginInvoke((MethodInvoker)delegate { SetStopping(message); }); }
                    catch (InvalidOperationException) { }
                }
                return;
            }
            stopRequested = true;
            stop.Enabled = false;
            stop.Glyph = "";
            stop.Text = "Saving…";
            detail.Text = String.IsNullOrEmpty(message) ? "Saving your recording. Please wait…" : message;
        }

        private void RequestStop()
        {
            if (stopRequested || allowClose) return;
            SetStopping("Saving your recording. Please wait…");
            EventHandler handler = StopRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true;
                RequestStop();
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape)
            {
                RequestStop();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }
    }
}
