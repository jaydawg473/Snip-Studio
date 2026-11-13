using System;
using System.Drawing;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private string pendingEmoji;
        public bool EmojiPlacementPending { get { return pendingEmoji != null; } }
        public string PendingEmoji { get { return pendingEmoji; } }

        public void BeginEmojiPlacement(string emoji)
        {
            if (String.IsNullOrWhiteSpace(emoji) || emoji.Length > 32) throw new ArgumentException("Choose an emoji.", "emoji");
            if (!HasImage || !CommitInlineText()) return;
            TextActionsMode = false;
            Tool = EditorTool.Text;
            pendingEmoji = emoji;
            selectedIndex = -1;
            RaiseTextStyleChanged(); NotifyShapeSelectionChanged(); UpdateCursor(); Invalidate();
        }

        private bool TryPlacePendingEmoji(PointF location)
        {
            if (pendingEmoji == null) return false;
            if (!HasImage || ImageSize.Width < 12 || ImageSize.Height < 12) return true;
            float size = Math.Min(48, Math.Min(ImageSize.Width, ImageSize.Height) * 0.65f);
            float boxWidth = Math.Min(ImageSize.Width, Math.Max(12, size * 2));
            PointF position = new PointF(Math.Max(0, Math.Min(location.X, ImageSize.Width - boxWidth)),
                Math.Max(0, Math.Min(location.Y, ImageSize.Height - size * 1.5f)));
            string emoji = pendingEmoji;
            string rtf = RichTextLayout.CreatePlainRtf(emoji, "Segoe UI Emoji", size, FontStyle.Regular, inkColor);
            document.AddRichText(emoji, rtf, position, inkColor, size, boxWidth);
            int inserted = document.AnnotationCount - 1;
            pendingEmoji = null;
            Tool = EditorTool.Select;
            selectedIndex = inserted;
            RaiseChanged(); UpdateCursor();
            return true;
        }
    }
}
