using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    // Modeless: an error in one editor must not disable every open snip.
    internal sealed class CaptureFailureNotice : Form
    {
        internal CaptureFailureNotice(string title, string details)
        {
            Text = "Snip Studio — Capture notice";
            Font = new Font("Segoe UI", 10f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(530, 240);
            MinimumSize = new Size(440, 220);
            BackColor = StudioTheme.Background; ForeColor = StudioTheme.Text;
            MainForm.TrySetAppIcon(this);
            CaptureVisibilityScope.DisableTransientAnimations(this);

            Label heading = new Label { Text = title, Dock = DockStyle.Top, Height = 54,
                Padding = new Padding(20, 14, 20, 4), ForeColor = StudioTheme.Text,
                Font = new Font(Font.FontFamily, 13f, FontStyle.Bold) };
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(16), BackColor = StudioTheme.Surface };
            StudioButton dismiss = new StudioButton { Text = "Dismiss", Size = new Size(104, 34), Dock = DockStyle.Right, Primary = true };
            dismiss.Click += delegate { Close(); };
            footer.Controls.Add(dismiss);
            TextBox message = new TextBox { Text = details + "\r\n\r\nYou can take another snip in this or any other Snip Studio window.",
                Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = StudioTheme.Background, ForeColor = StudioTheme.Text, Tag = "PreserveColor" };
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
            body.Controls.Add(message);
            Controls.Add(body); Controls.Add(footer); Controls.Add(heading);
            AcceptButton = dismiss; CancelButton = dismiss;
            Shown += delegate { message.Select(0, 0); dismiss.Focus(); };
        }
    }
}
