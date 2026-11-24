using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    internal (int Start, int End) GetTextDraftSelection()
    {
        var selection = InlineEditor.Document.Selection;
        return (selection.StartPosition, selection.EndPosition);
    }

    internal void ApplyAdvancedTextDraft(TextEditDraft draft, int selectionStart, int selectionEnd)
    {
        if (textDraft is null) return;
        textDraft = draft;
        loadingText = true;
        try
        {
            InlineEditor.Document.GetText(TextGetOptions.FormatRtf, out string currentRtf);
            if (draft.Rtf != currentRtf)
                InlineEditor.Document.SetText(TextSetOptions.FormatRtf | TextSetOptions.ApplyRtfDocumentDefaults, draft.Rtf);
            if (draft.LayoutWidth > 0)
            {
                InlineEditor.Width = draft.LayoutWidth;
                TextEditorFrame.Width = draft.LayoutWidth + 2;
            }
            if (draft.ReplacementBackground is uint fill)
                RecognizedDraftMask.Fill = new SolidColorBrush(ToColor(fill));
            int length = ReadInlineText().Length;
            InlineEditor.Document.Selection.SetRange(Math.Clamp(selectionStart, 0, length), Math.Clamp(selectionEnd, 0, length));
        }
        finally { loadingText = false; }
        UpdateTextDraftState();
        UpdateScene();
    }
}
