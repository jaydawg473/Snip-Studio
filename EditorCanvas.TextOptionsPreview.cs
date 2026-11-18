using System.Drawing;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        // Contextual UI color is separate from the default drawing ink. Reading
        // an OCR edit must not turn the next pen stroke into its source color.
        internal bool TryReadTextColor(out Color color, out bool mixed)
        {
            color = inkColor; mixed = false;
            if (InlineTextEditing)
            {
                color = inlineTextEditor.SelectionColor;
                mixed = color.IsEmpty;
                if (mixed) color = RichTextLayout.GetFirstColor(inlineTextEditor.Rtf);
                return true;
            }
            TextAnnotationInfo text = HasImage && !textActionsMode ? document.GetTextAnnotation(selectedIndex) : null;
            if (text == null) return false;
            color = text.Color;
            return true;
        }

        // A read-only snapshot for the options card. Asking for a sample must
        // never select text, apply formatting, or commit an inline draft.
        internal void ReadTextPreviewStyle(out string family, out FontStyle style, out Color color)
        {
            family = "Segoe UI"; style = FontStyle.Regular; color = inkColor;
            if (InlineTextEditing)
            {
                using (Font selected = inlineTextEditor.SelectionFont)
                {
                    if (selected != null) { family = selected.FontFamily.Name; style = selected.Style; }
                    else
                    {
                        family = RichTextLayout.GetFirstFontFamily(inlineTextEditor.Rtf);
                        style = RichTextLayout.GetFirstFontStyle(inlineTextEditor.Rtf);
                    }
                }
                Color selectionColor = inlineTextEditor.SelectionColor;
                color = selectionColor.IsEmpty ? RichTextLayout.GetFirstColor(inlineTextEditor.Rtf) : selectionColor;
                return;
            }
            TextAnnotationInfo text = HasImage && !textActionsMode ? document.GetTextAnnotation(selectedIndex) : null;
            if (text != null) { family = text.FontFamily; style = text.FontStyle; color = text.Color; }
        }
    }
}
