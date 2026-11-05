using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class CaptureCountdown : Form
    {
        private readonly Timer timer = new Timer();
        private readonly Label message = new Label();
        private readonly Label countdownNumber = new Label();
        private DateTime deadline;

        private CaptureCountdown(int seconds)
        {
            Text = "Preparing capture — Snip Studio";
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 206);
            BackColor = StudioTheme.Background;
            ForeColor = StudioTheme.Text;
            MainForm.TrySetAppIcon(this);
            HandleCreated += delegate { MainForm.ApplyDarkCaption(this); };
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(26, 20, 26, 18);
            layout.ColumnCount = 2;
            layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            countdownNumber.Dock = DockStyle.Fill;
            countdownNumber.TextAlign = ContentAlignment.TopLeft;
            countdownNumber.Font = new Font("Segoe UI Semibold", 38f, FontStyle.Bold);
            countdownNumber.ForeColor = StudioTheme.Accent;
            countdownNumber.Margin = new Padding(0, 2, 0, 0);
            layout.Controls.Add(countdownNumber, 0, 0);
            message.Dock = DockStyle.Fill;
            message.Margin = new Padding(3, 10, 0, 0);
            message.ForeColor = StudioTheme.Text;
            message.Text = "Prepare your screen\r\n\r\nThis window closes before the capture.";
            layout.Controls.Add(message, 1, 0);
            StudioButton cancel = new StudioButton();
            cancel.Text = "Cancel (Esc)";
            cancel.Size = new Size(132, 40);
            cancel.Margin = new Padding(0);
            cancel.Anchor = AnchorStyles.Right;
            cancel.DialogResult = DialogResult.Cancel;
            layout.Controls.Add(cancel, 1, 1);
            Controls.Add(layout);
            CancelButton = cancel;
            deadline = DateTime.UtcNow.AddSeconds(seconds);
            timer.Interval = 70;
            timer.Tick += delegate
            {
                double remaining = (deadline - DateTime.UtcNow).TotalSeconds;
                if (remaining <= 0) { timer.Stop(); DialogResult = DialogResult.OK; Close(); }
                else countdownNumber.Text = Math.Ceiling(remaining).ToString();
            };
            countdownNumber.Text = seconds.ToString();
            Shown += delegate { timer.Start(); };
        }

        internal static bool WaitForCapture(int seconds)
        {
            using (CaptureCountdown countdown = new CaptureCountdown(seconds))
                return countdown.ShowDialog() == DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
