using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class EmojiPickerPopup : ToolStripDropDown
    {
        private readonly Panel content;
        private readonly ToolStripControlHost host;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly Font textFont, emojiFont;
        private readonly Size fullSize;
        private readonly float scale;
        private bool selected;

        internal EmojiPickerPopup(Action<string> selectedEmoji, float scale)
        {
            if (selectedEmoji == null) throw new ArgumentNullException("selectedEmoji");
            this.scale = Math.Max(.75f, Math.Min(4f, scale));
            AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty;
            BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text; AccessibleName = "Choose an emoji to place";
            textFont = new Font("Segoe UI", 9.5f); emojiFont = new Font("Segoe UI Emoji", 19f);
            content = new Panel { BackColor = StudioTheme.Surface, Font = textFont, AutoScroll = true };
            Label title = new Label { Text = "Choose an emoji, then click your snip", AutoSize = false,
                ForeColor = StudioTheme.Text, Bounds = new Rectangle(S(14), S(10), S(270), S(30)) };
            content.Controls.Add(title);
            string[] emojis = { "😀", "😄", "🙂", "😉", "😍", "🤔", "👍", "👎", "👏", "🙏", "💪", "👀",
                "✅", "❌", "⚠️", "❗", "❓", "💡", "⭐", "❤️", "🔥", "🎉", "📌", "➡️" };
            string[] names = { "Smile", "Laugh", "Happy", "Wink", "Love", "Thinking", "Thumbs up", "Thumbs down", "Applause", "Thank you", "Strong", "Look",
                "Check mark", "Cross", "Warning", "Important", "Question", "Idea", "Star", "Heart", "Fire", "Celebrate", "Pin", "Right arrow" };
            for (int i = 0; i < emojis.Length; i++)
            {
                string emoji = emojis[i];
                Button button = new Button { Text = emoji, Font = emojiFont, FlatStyle = FlatStyle.Flat,
                    BackColor = StudioTheme.Surface, ForeColor = StudioTheme.Text, UseVisualStyleBackColor = false,
                    Bounds = new Rectangle(S(12 + i % 6 * 44), S(44 + i / 6 * 44), S(40), S(40)), AccessibleName = names[i] };
                button.FlatAppearance.BorderSize = 0;
                button.Click += delegate {
                    if (selected) return;
                    selected = true; Close(ToolStripDropDownCloseReason.ItemClicked); selectedEmoji(emoji);
                };
                tips.SetToolTip(button, names[i] + ": select, then click the snip to place it.");
                content.Controls.Add(button);
            }
            Label note = new Label { Text = "Emojis remain editable text. Appearance follows Windows fonts.",
                AutoSize = false, ForeColor = StudioTheme.Muted, Bounds = new Rectangle(S(14), S(226), S(270), S(42)) };
            content.Controls.Add(note);
            StudioButton cancel = new StudioButton { Text = "Cancel", Bounds = new Rectangle(S(14), S(274), S(104), S(32)) };
            cancel.Click += delegate { Close(ToolStripDropDownCloseReason.CloseCalled); };
            content.Controls.Add(cancel);
            fullSize = new Size(S(302), S(318));
            content.Size = fullSize; content.AutoScrollMinSize = new Size(S(284), fullSize.Height);
            host = new ToolStripControlHost(content) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = fullSize };
            Items.Add(host); Size = fullSize;
        }

        internal void FitToWorkingArea(Rectangle work)
        {
            content.Size = host.Size = Size = new Size(Math.Min(fullSize.Width, Math.Max(1, work.Width - S(8))),
                Math.Min(fullSize.Height, Math.Max(1, work.Height - S(8))));
        }
        private int S(int value) { return Math.Max(1, (int)Math.Round(value * scale)); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(ToolStripDropDownCloseReason.Keyboard); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { tips.Dispose(); textFont.Dispose(); emojiFont.Dispose(); }
        }
    }
}
