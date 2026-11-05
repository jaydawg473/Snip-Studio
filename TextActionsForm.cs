using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed class TextActionsForm : Form
    {
        private Bitmap snapshot;
        private readonly RichTextBox text = new RichTextBox();
        private readonly Label status = new Label();
        private readonly StudioButton copyAll = new StudioButton();
        private readonly StudioButton copySelection = new StudioButton();

        public TextActionsForm(Bitmap image)
        {
            if (image == null) throw new ArgumentNullException("image");
            snapshot = new Bitmap(image);
            Text = "Text from your snip — Snip Studio";
            Font = new Font("Segoe UI", 10f);
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(820, 650);
            MinimumSize = new Size(650, 450);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(26, 24, 26, 20);
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            Label heading = new Label();
            heading.Text = "Text, ready to use.";
            heading.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            heading.ForeColor = StudioTheme.Text;
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(heading, 0, 0);
            Label description = new Label();
            description.Text = "Select the words you need, or copy everything. Recognition stays on this PC.";
            description.ForeColor = StudioTheme.Muted;
            description.AutoSize = true;
            description.MaximumSize = new Size(700, 0);
            description.Margin = new Padding(0, 0, 0, 14);
            layout.Controls.Add(description, 0, 1);
            status.Text = "Reading text on this PC…";
            status.AutoSize = true;
            status.ForeColor = StudioTheme.Accent;
            status.MaximumSize = new Size(700, 0);
            status.Margin = new Padding(0, 0, 0, 12);
            layout.Controls.Add(status, 0, 2);
            Panel border = new Panel();
            border.Dock = DockStyle.Fill;
            border.Padding = new Padding(1);
            border.BackColor = StudioTheme.Border;
            border.Margin = new Padding(0, 0, 0, 16);
            layout.Controls.Add(border, 0, 3);
            text.Dock = DockStyle.Fill;
            text.BorderStyle = BorderStyle.None;
            text.BackColor = StudioTheme.Input;
            text.ForeColor = StudioTheme.Text;
            text.Font = new Font("Segoe UI", 12f);
            text.ReadOnly = true;
            text.HideSelection = false;
            text.DetectUrls = false;
            text.WordWrap = true;
            text.ScrollBars = RichTextBoxScrollBars.Vertical;
            text.AccessibleName = "Recognized text. Select words here to copy.";
            text.SelectionChanged += delegate { copySelection.Enabled = text.SelectionLength > 0; };
            border.Controls.Add(text);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.Margin = new Padding(0);
            actions.WrapContents = true;
            layout.Controls.Add(actions, 0, 4);
            ConfigureButton(copyAll, "Copy all text", "copy", 148);
            copyAll.Primary = true;
            copyAll.Enabled = false;
            copyAll.Click += delegate { CopyText(false); };
            actions.Controls.Add(copyAll);
            ConfigureButton(copySelection, "Copy selection", "text-scan", 162);
            copySelection.Enabled = false;
            copySelection.Click += delegate { CopyText(true); };
            actions.Controls.Add(copySelection);
            StudioButton close = new StudioButton();
            ConfigureButton(close, "Close", "", 86);
            close.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(close);
            CancelButton = close;
            Resize += delegate
            {
                int available = Math.Max(200, ClientSize.Width - 52);
                description.MaximumSize = new Size(available, 0);
                status.MaximumSize = new Size(available, 0);
            };
            Shown += ReadText;
        }

        private static void ConfigureButton(StudioButton button, string label, string glyph, int width)
        {
            button.Text = label;
            button.Glyph = glyph;
            button.Size = new Size(width, 40);
            button.Margin = new Padding(0, 0, 8, 0);
            button.AccessibleName = label;
            button.Cursor = Cursors.Hand;
        }

        private async void ReadText(object sender, EventArgs e)
        {
            try
            {
                TextRecognitionResult result = await TextRecognition.RecognizeAsync(snapshot);
                if (IsDisposed || Disposing) return;
                text.Text = result.Text ?? "";
                copyAll.Enabled = !String.IsNullOrWhiteSpace(text.Text);
                if (!result.Available) status.Text = result.Message;
                else if (!copyAll.Enabled) status.Text = "No text was detected. Try a closer, sharper snip.";
                else status.Text = "Text recognized. Review for possible misreads before copying.";
                status.ForeColor = copyAll.Enabled ? StudioTheme.Muted : StudioTheme.Accent;
                text.Select(0, 0);
                text.Focus();
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) status.Text = "Text recognition is unavailable: " + error.Message;
            }
        }

        private void CopyText(bool selectedOnly)
        {
            string value = selectedOnly ? text.SelectedText : text.Text;
            if (String.IsNullOrEmpty(value)) return;
            try
            {
                Clipboard.SetDataObject(value, true, 4, 100);
                status.Text = selectedOnly ? "Selected text copied." : "All recognized text copied.";
                status.ForeColor = StudioTheme.Accent;
            }
            catch (Exception)
            {
                status.Text = "The clipboard is busy. Please try Copy again.";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && snapshot != null) { snapshot.Dispose(); snapshot = null; }
            base.Dispose(disposing);
        }
    }
}
