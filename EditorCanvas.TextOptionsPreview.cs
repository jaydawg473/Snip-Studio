using System.Drawing;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
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
