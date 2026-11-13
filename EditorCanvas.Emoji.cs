using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private string activeEmojiKey = "happy";
        private bool emojiPlacementArmed;
        public bool EmojiPlacementPending { get { return emojiPlacementArmed && HasImage && tool == EditorTool.Emoji && !textActionsMode; } }
        public string PendingEmoji { get { return EmojiPlacementPending ? ActiveEmojiGlyph : null; } }
        public string ActiveEmojiKey { get { return activeEmojiKey; } }
        public string ActiveEmojiName { get { return EmojiArtwork.GetDisplayName(activeEmojiKey); } }
        public string ActiveEmojiGlyph { get { return EmojiArtwork.GetGlyph(activeEmojiKey); } }
        public bool HasSelectedEmoji { get { return SelectedEmojiInfo != null; } }
        public EmojiAnnotationInfo SelectedEmojiInfo
        {
            get { return HasImage && !textActionsMode ? document.GetEmojiAnnotation(selectedIndex) : null; }
        }

        public void BeginEmojiPlacement(string glyphOrKey)
        {
            string key = EmojiArtwork.ResolveKey(glyphOrKey);
            if (!HasImage || !CommitInlineText()) return;
            TextActionsMode = false;
            Tool = EditorTool.Emoji;
            if (tool != EditorTool.Emoji || textActionsMode) return;
            CancelGesture();
            activeEmojiKey = key;
            emojiPlacementArmed = true;
            selectedIndex = -1;
            RaiseTextStyleChanged(); NotifyShapeSelectionChanged(); UpdateCursor(); Invalidate();
        }

        public void CancelEmojiPlacement()
        {
            bool selected = HasSelectedEmoji;
            bool changed = emojiPlacementArmed || selected || EmojiHandleDragging || emojiMoveActive;
            emojiPlacementArmed = false;
            CancelEmojiHandleDrag();
            if (selected) selectedIndex = -1;
            if (changed) { RaiseTextStyleChanged(); NotifyShapeSelectionChanged(); UpdateCursor(); Invalidate(); }
        }

        // Existing stickers use the normal move transaction in either tool.
        // Empty clicks stamp only while the dedicated Emoji tool is selected.
        internal bool HandleEmojiMouseDown(MouseEventArgs e)
        {
            if (!HasImage || textActionsMode || e.Button != MouseButtons.Left ||
                (tool != EditorTool.Emoji && tool != EditorTool.Select) || !IsOnImage(e.Location)) return false;
            PointF point = ToImage(e.Location, false);
            int hit = document.HitTestAnnotation(point, EmojiHandleRadius);
            if (document.GetEmojiAnnotation(hit) != null)
            {
                CancelGesture(); selectedIndex = hit; emojiPlacementArmed = false;
                BeginEmojiBodyMove(point);
                RaiseTextStyleChanged(); NotifyShapeSelectionChanged(); Invalidate();
                return true;
            }
            if (tool != EditorTool.Emoji) return false;
            CancelGesture(); PlaceActiveEmoji(point); return true;
        }

        // Separate from mouse capture so placement can be checked on synthetic
        // documents without changing desktop input state.
        internal bool PlaceActiveEmoji(PointF point)
        {
            if (!HasImage || textActionsMode || tool != EditorTool.Emoji) return false;
            if (Single.IsNaN(point.X) || Single.IsInfinity(point.X) || Single.IsNaN(point.Y) || Single.IsInfinity(point.Y))
                throw new ArgumentOutOfRangeException("point");
            float aspect = EmojiArtwork.GetAspectRatio(activeEmojiKey);
            float width = aspect >= 1 ? 96 : 96 * aspect;
            float height = aspect >= 1 ? 96 / aspect : 96;
            float fit = Math.Min(1, Math.Min(ImageSize.Width / width, ImageSize.Height / height));
            width = Math.Max(1, Math.Min(ImageSize.Width, width * fit));
            height = Math.Max(1, Math.Min(ImageSize.Height, height * fit));
            RectangleF frame = new RectangleF(
                Math.Max(0, Math.Min(ImageSize.Width - width, point.X - width / 2)),
                Math.Max(0, Math.Min(ImageSize.Height - height, point.Y - height / 2)), width, height);
            if (!document.AddEmoji(activeEmojiKey, frame)) return false;
            selectedIndex = document.AnnotationCount - 1; emojiPlacementArmed = false;
            // Keep the tool and remembered artwork ready for another stamp.
            RaiseChanged(); RaiseTextStyleChanged(); NotifyShapeSelectionChanged(); UpdateCursor(); return true;
        }
    }
}
