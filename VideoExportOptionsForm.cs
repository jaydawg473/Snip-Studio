using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class VideoExportOptionsForm : Form
    {
        private readonly VideoMetadata metadata;
        private readonly long sourceBytes;
        private readonly StudioComboBox preset = new StudioComboBox();
        private readonly NumericUpDown targetSize = new NoWheelNumber();
        private readonly Label estimate = new Label();
        private readonly Label explanation = new Label();
        private readonly StudioButton export = new StudioButton();
        private readonly List<Label> wrappingLabels = new List<Label>();
        public VideoExportPlan Plan { get; private set; }

        internal VideoExportOptionsForm(VideoMetadata sourceMetadata, long bytes)
        {
            metadata = sourceMetadata;
            sourceBytes = bytes;
            Text = "Make a smaller recording";
            Font = new Font("Segoe UI", 10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false; MaximizeBox = false;
            BackColor = StudioTheme.Background; ForeColor = StudioTheme.Text;
            ClientSize = new Size(610, 490);
            MinimumSize = new Size(530, 420);
            MainForm.TrySetAppIcon(this);

            FlowLayoutPanel footer = new FlowLayoutPanel();
            footer.Dock = DockStyle.Bottom; footer.Height = 68;
            footer.Padding = new Padding(20, 12, 20, 12);
            footer.FlowDirection = FlowDirection.RightToLeft; footer.WrapContents = false;
            footer.BackColor = StudioTheme.Surface;
            StudioButton cancel = Action("Cancel", 94);
            cancel.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(cancel);
            export.Text = "Create copy"; export.Primary = true;
            export.Size = new Size(145, 40); export.Margin = new Padding(8, 0, 0, 0);
            export.Click += delegate { if (Plan != null) { DialogResult = DialogResult.OK; Close(); } };
            footer.Controls.Add(export);
            Controls.Add(footer);
            CancelButton = cancel; AcceptButton = export;

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true;
            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Top; body.AutoSize = true;
            body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            body.ColumnCount = 1; body.Padding = new Padding(24, 22, 24, 22);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(body); Controls.Add(scroll); scroll.BringToFront();
            Label heading = Paragraph("A smaller copy, ready to share.", false);
            heading.Font = new Font(Font.FontFamily, 18f, FontStyle.Bold);
            body.Controls.Add(heading);
            body.Controls.Add(Paragraph("The original stays unchanged. After exporting, use Copy video or attach the new MP4 in your email or chat app.", true));
            body.Controls.Add(Paragraph("Quality / size", false));
            preset.Items.AddRange(new object[] { "Balanced — up to 720p", "Small — up to 480p", "Target attachment file size" });
            preset.Dock = DockStyle.Top; preset.Margin = new Padding(0, 0, 0, 12);
            preset.Width = 400; preset.DropDownStyle = ComboBoxStyle.DropDownList;
            preset.AccessibleName = "Recording export quality";
            body.Controls.Add(preset);
            FlowLayoutPanel targets = new FlowLayoutPanel();
            targets.AutoSize = true; targets.Dock = DockStyle.Top;
            targets.WrapContents = true; targets.Margin = new Padding(0, 0, 0, 8);
            Label sizeLabel = new Label(); sizeLabel.AutoSize = true;
            sizeLabel.Text = "Target file size (MB)"; sizeLabel.Margin = new Padding(0, 9, 10, 6);
            targets.Controls.Add(sizeLabel);
            targetSize.Minimum = 1; targetSize.Maximum = 1000; targetSize.Value = 20;
            targetSize.Width = 85; targetSize.Height = 30;
            targetSize.Margin = new Padding(0, 4, 12, 6);
            targetSize.AccessibleName = "Target video file size in megabytes";
            targets.Controls.Add(targetSize);
            foreach (int megabytes in new int[] { 10, 20, 25 })
            {
                int choice = megabytes;
                StudioButton quick = Action(choice + " MB", 76);
                quick.Margin = new Padding(0, 0, 6, 6);
                quick.Click += delegate { targetSize.Value = choice; preset.SelectedIndex = 2; RefreshPlan(); };
                targets.Controls.Add(quick);
            }
            body.Controls.Add(targets);
            estimate.AutoSize = true; estimate.ForeColor = StudioTheme.Text;
            estimate.Font = new Font(Font, FontStyle.Bold);
            estimate.Margin = new Padding(0, 5, 0, 12);
            wrappingLabels.Add(estimate); body.Controls.Add(estimate);
            explanation.Text = "Sizes are estimates. Email systems have different attachment limits, and an email can be larger than its video file. Choose a file target below your email limit; the actual exported size is checked afterward. Smaller copies can soften text and fine details.";
            explanation.AutoSize = true; explanation.ForeColor = StudioTheme.Muted;
            explanation.Margin = new Padding(0, 0, 0, 0);
            wrappingLabels.Add(explanation); body.Controls.Add(explanation);
            preset.SelectedIndexChanged += delegate { RefreshPlan(); };
            targetSize.ValueChanged += delegate { if (preset.SelectedIndex == 2) RefreshPlan(); };
            Resize += delegate { Reflow(); };
            Shown += delegate {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                if (Width > work.Width - 32 || Height > work.Height - 32)
                {
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width - 32), Math.Min(MinimumSize.Height, work.Height - 32));
                    Size = new Size(Math.Min(Width, work.Width - 32), Math.Min(Height, work.Height - 32));
                }
                Reflow();
            };
            preset.SelectedIndex = 0;
            StudioTheme.ApplyTo(this);
            Reflow(); RefreshPlan();
        }

        private Label Paragraph(string text, bool muted)
        {
            Label label = new Label(); label.Text = text;
            label.AutoSize = true; label.ForeColor = muted ? StudioTheme.Muted : StudioTheme.Text;
            label.Margin = new Padding(0, 0, 0, 12);
            wrappingLabels.Add(label); return label;
        }

        private static StudioButton Action(string text, int width)
        {
            StudioButton button = new StudioButton(); button.Text = text;
            button.Size = new Size(width, 40); button.Cursor = Cursors.Hand;
            button.AccessibleName = text; return button;
        }

        private void Reflow()
        {
            int width = Math.Max(100, ClientSize.Width - 66);
            foreach (Label label in wrappingLabels) label.MaximumSize = new Size(width, 0);
        }

        private void RefreshPlan()
        {
            if (preset.SelectedIndex < 0) return;
            targetSize.Enabled = preset.SelectedIndex == 2;
            try
            {
                Plan = VideoTrimService.CreateExportPlan(metadata, sourceBytes, (VideoExportPreset)preset.SelectedIndex, (int)targetSize.Value);
                estimate.ForeColor = StudioTheme.Text;
                estimate.Text = Plan.Width + " × " + Plan.Height + " px  ·  " + Plan.FramesPerSecond + " fps\r\n" +
                    "Estimated video file: " + (Plan.EstimatedBytes / 1000000.0).ToString("0.0") + " MB  ·  Source: " + (sourceBytes / 1000000.0).ToString("0.0") + " MB\r\n" +
                    (Plan.HasAudio ? "Audio is kept in the copy." : "This recording has no audio.");
                export.Enabled = true;
            }
            catch (Exception error)
            {
                Plan = null; export.Enabled = false;
                estimate.ForeColor = StudioTheme.IsDark ? Color.FromArgb(255, 179, 157) : Color.FromArgb(150, 42, 25);
                estimate.Text = error.Message;
            }
        }

        private sealed class NoWheelNumber : NumericUpDown
        {
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x020A) { message.Result = IntPtr.Zero; return; }
                base.WndProc(ref message);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }
        }
    }
}
