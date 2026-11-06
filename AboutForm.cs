using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed class AboutForm : Form
    {
        private Bitmap artwork;
        public AboutForm()
        {
            Text = "About Snip Studio";
            Font = new Font("Segoe UI", 10f);
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(590, 470);
            MinimumSize = new Size(590, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(32);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            FlowLayoutPanel hero = new FlowLayoutPanel();
            hero.Dock = DockStyle.Fill;
            hero.WrapContents = false;
            hero.Margin = new Padding(0);
            artwork = IconArtwork.CreateAppIconBitmap(80);
            PictureBox mark = new PictureBox();
            mark.Size = new Size(80, 80);
            mark.SizeMode = PictureBoxSizeMode.Zoom;
            mark.Image = artwork;
            mark.Margin = new Padding(0, 0, 20, 0);
            hero.Controls.Add(mark);
            TableLayoutPanel titleBlock = new TableLayoutPanel();
            titleBlock.ColumnCount = 1;
            titleBlock.AutoSize = true;
            titleBlock.Padding = new Padding(0, 4, 0, 0);
            Label title = LabelFor("Snip Studio", 25f, FontStyle.Bold, StudioTheme.Text);
            titleBlock.Controls.Add(title);
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            titleBlock.Controls.Add(LabelFor("Version " + version.Major + "." + version.Minor + "." + version.Build, 10f, FontStyle.Regular, StudioTheme.Muted));
            hero.Controls.Add(titleBlock);
            layout.Controls.Add(hero, 0, 0);
            Label description = LabelFor("Capture clearly. Make your point.", 16f, FontStyle.Bold, StudioTheme.Text);
            description.Margin = new Padding(0, 2, 0, 12);
            layout.Controls.Add(description, 0, 1);
            Label body = LabelFor("Screenshots, screen recording, movable annotations,\r\ntext recognition, and print in one focused workspace.", 11f, FontStyle.Regular, StudioTheme.Muted);
            body.Margin = new Padding(0, 0, 0, 18);
            layout.Controls.Add(body, 0, 2);
            Label publisher = LabelFor("Built by Automation Forge", 11f, FontStyle.Bold, StudioTheme.Text);
            publisher.Margin = new Padding(0, 0, 0, 7);
            layout.Controls.Add(publisher, 0, 3);
            LinkLabel website = new LinkLabel();
            website.Text = "automation-forge.com";
            website.AutoSize = true;
            website.Font = new Font("Segoe UI", 11f);
            website.LinkColor = StudioTheme.Accent;
            website.ActiveLinkColor = Color.FromArgb(120, 205, 255);
            website.VisitedLinkColor = StudioTheme.Accent;
            website.Margin = new Padding(0, 0, 0, 16);
            website.LinkClicked += delegate { OpenWebsite(); };
            website.AccessibleName = "Open the Automation Forge website";
            FlowLayoutPanel links = new FlowLayoutPanel();
            links.FlowDirection = FlowDirection.TopDown;
            links.WrapContents = false;
            links.AutoSize = true;
            links.Dock = DockStyle.Fill;
            links.Margin = Padding.Empty;
            links.Controls.Add(website);
            LinkLabel licenses = new LinkLabel();
            licenses.Text = "Open-source licenses and translation credits";
            licenses.AutoSize = true;
            licenses.LinkColor = licenses.ActiveLinkColor = licenses.VisitedLinkColor = StudioTheme.Accent;
            licenses.Margin = new Padding(0, 0, 0, 8);
            licenses.AccessibleName = "Open source licenses and translation credits";
            licenses.LinkClicked += delegate
            {
                string notices = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translation", "licenses", "NOTICES.txt");
                try
                {
                    if (!File.Exists(notices)) throw new FileNotFoundException("The bundled translation notices were not found. Reinstall the complete package.");
                    Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Open-source licenses", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            links.Controls.Add(licenses);
            layout.Controls.Add(links, 0, 4);
            Label privacy = LabelFor("Captures stay local until you choose to share them.\r\nText recognition runs on this PC.  © " + DateTime.Now.Year + " Automation Forge", 9f, FontStyle.Regular, StudioTheme.Muted);
            privacy.Margin = new Padding(0, 0, 0, 20);
            layout.Controls.Add(privacy, 0, 5);
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.AutoSize = true;
            actions.Dock = DockStyle.Fill;
            actions.Margin = new Padding(0);
            StudioButton visit = new StudioButton();
            visit.Text = "Visit Automation Forge";
            visit.Glyph = "open";
            visit.Primary = true;
            visit.Size = new Size(226, 42);
            visit.Margin = new Padding(0, 0, 12, 0);
            visit.Click += delegate { OpenWebsite(); };
            actions.Controls.Add(visit);
            StudioButton close = new StudioButton();
            close.Text = "Close";
            close.Size = new Size(90, 42);
            close.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(close);
            CancelButton = close;
            layout.Controls.Add(actions, 0, 6);
        }

        private static Label LabelFor(string value, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = value;
            label.AutoSize = true;
            label.ForeColor = color;
            label.Font = new Font("Segoe UI", size, style);
            label.Margin = new Padding(0);
            return label;
        }

        private void OpenWebsite()
        {
            try { Process.Start(new ProcessStartInfo("https://automation-forge.com/") { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, "The website could not be opened.\n\nhttps://automation-forge.com/\n\n" + error.Message, "Automation Forge", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && artwork != null) { artwork.Dispose(); artwork = null; }
            base.Dispose(disposing);
        }
    }
}
