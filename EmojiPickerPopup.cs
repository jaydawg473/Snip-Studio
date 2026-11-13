using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class EmojiPickerPopup : ToolStripDropDown
    {
        private readonly Panel content;
        private readonly ToolStripControlHost host;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly List<Bitmap> thumbnails = new List<Bitmap>();
        private readonly Font textFont;
        private readonly Size fullSize;
        private readonly float scale;
        private bool selected, finished, resourcesDisposed;

        // A containing tool card may temporarily own this panel. It must detach
        // the panel before disposing the popup, which still owns its artwork.
        internal Panel EmbeddedContent { get { return content; } }
        internal Size PreferredEmbeddedSize { get { return fullSize; } }
        internal event EventHandler SelectionFinished;

        internal EmojiPickerPopup(Action<string> selectedEmoji, float scale)
        {
            if (selectedEmoji == null) throw new ArgumentNullException("selectedEmoji");
            this.scale = Math.Max(.75f, Math.Min(4f, scale));
            AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty;
            BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text; AccessibleName = "Choose an emoji sticker";
            try
            {
                textFont = new Font("Segoe UI", 9.5f);
                int rows = (EmojiArtwork.Catalog.Count + 5) / 6;
                int noteTop = 50 + rows * 44 + 8;
                int cancelTop = noteTop + 48;
                fullSize = new Size(S(302), S(cancelTop + 44));
                content = new Panel { BackColor = StudioTheme.Surface, Font = textFont, AutoScroll = true,
                    Size = fullSize, AutoScrollMinSize = new Size(S(284), fullSize.Height) };
                host = new ToolStripControlHost(content) { AutoSize = false, Margin = Padding.Empty,
                    Padding = Padding.Empty, Size = fullSize };
                Items.Add(host); Size = fullSize;
                Label title = new Label { Text = "Choose a sticker, then click your snip to place it", AutoSize = false,
                    ForeColor = StudioTheme.Text, Bounds = new Rectangle(S(14), S(10), S(270), S(36)) };
                content.Controls.Add(title);
                for (int i = 0; i < EmojiArtwork.Catalog.Count; i++)
                {
                    EmojiStickerDefinition definition = EmojiArtwork.Catalog[i];
                    string key = definition.Key;
                    Bitmap thumbnail = EmojiArtwork.CreateThumbnail(key, S(32));
                    thumbnails.Add(thumbnail);
                    StickerButton button = new StickerButton(thumbnail, this.scale) {
                        Bounds = new Rectangle(S(12 + i % 6 * 44), S(50 + i / 6 * 44), S(40), S(40)),
                        AccessibleName = definition.DisplayName,
                        AccessibleDescription = "Choose, then click the snip to place it. Drag corner handles to resize and the top handle to rotate."
                    };
                    button.Click += delegate {
                        if (selected || finished) return;
                        selected = true; FinishChoice(ToolStripDropDownCloseReason.ItemClicked); selectedEmoji(key);
                    };
                    tips.SetToolTip(button, definition.DisplayName + ": choose, then click the snip to place it. Drag corner handles to resize; use the top handle to rotate.");
                    content.Controls.Add(button);
                }
                Label note = new Label { Text = "Resize with corner handles. Rotate with the top handle.",
                    AutoSize = false, ForeColor = StudioTheme.Muted, Bounds = new Rectangle(S(14), S(noteTop), S(270), S(42)) };
                content.Controls.Add(note);
                StudioButton cancel = new StudioButton { Text = "Cancel", Bounds = new Rectangle(S(14), S(cancelTop), S(104), S(32)) };
                cancel.Click += delegate { FinishChoice(ToolStripDropDownCloseReason.CloseCalled); };
                content.Controls.Add(cancel);
            }
            catch { Dispose(); throw; }
        }

        internal void FitToWorkingArea(Rectangle work)
        {
            content.Size = host.Size = Size = new Size(Math.Min(fullSize.Width, Math.Max(1, work.Width - S(8))),
                Math.Min(fullSize.Height, Math.Max(1, work.Height - S(8))));
        }
        private int S(int value) { return Math.Max(1, (int)Math.Round(value * scale)); }
        private void FinishChoice(ToolStripDropDownCloseReason reason)
        {
            if (finished) return;
            finished = true;
            Close(reason);
            EventHandler handler = SelectionFinished;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { FinishChoice(ToolStripDropDownCloseReason.Keyboard); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing && !resourcesDisposed)
                {
                    resourcesDisposed = true;
                    tips.Dispose();
                    foreach (Bitmap thumbnail in thumbnails) thumbnail.Dispose();
                    thumbnails.Clear();
                    if (textFont != null) textFont.Dispose();
                }
            }
        }

        // The popup owns the artwork; these buttons only paint their supplied thumbnail.
        private sealed class StickerButton : Button
        {
            private readonly Bitmap thumbnail;
            private readonly float scale;
            private bool hovered, pressed;

            internal StickerButton(Bitmap thumbnail, float scale)
            {
                this.thumbnail = thumbnail; this.scale = scale;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
                BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text;
            }

            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
            protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
            protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.Clear(Parent == null ? StudioTheme.Surface : Parent.BackColor);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = pressed ? StudioTheme.Pressed : hovered || Focused ? StudioTheme.Raised : StudioTheme.Surface;
                using (GraphicsPath path = StudioTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 6 * scale))
                {
                    using (Brush brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
                    if (hovered || Focused)
                        using (Pen pen = new Pen(Focused ? StudioTheme.Accent : StudioTheme.Border, Math.Max(1f, scale)))
                            graphics.DrawPath(pen, path);
                }
                // Explicit source and destination pixel rectangles avoid a second DPI scale.
                Rectangle destination = new Rectangle((Width - thumbnail.Width) / 2, (Height - thumbnail.Height) / 2,
                    thumbnail.Width, thumbnail.Height);
                graphics.DrawImage(thumbnail, destination, 0, 0, thumbnail.Width, thumbnail.Height, GraphicsUnit.Pixel);
                if (Focused && ShowFocusCues)
                {
                    Rectangle focus = ClientRectangle; focus.Inflate(-4, -4);
                    ControlPaint.DrawFocusRectangle(graphics, focus, StudioTheme.Text, fill);
                }
            }
        }
    }
}
