using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    private void OnInlineCopyingToClipboard(RichEditBox sender, TextControlCopyingToClipboardEventArgs args)
    {
        args.Handled = true;
        CopyInlineSelection(sender, cut: false);
    }

    private void OnInlineCuttingToClipboard(RichEditBox sender, TextControlCuttingToClipboardEventArgs args)
    {
        args.Handled = true;
        CopyInlineSelection(sender, cut: true);
    }

    private void CopyInlineSelection(RichEditBox editor, bool cut)
    {
        if (!IsTextEditing || cut && editor.IsReadOnly) return;
        try
        {
            var selection = editor.Document.Selection;
            if (selection.StartPosition == selection.EndPosition) return;
            selection.GetText(TextGetOptions.UseCrlf, out string text);
            selection.GetText(TextGetOptions.FormatRtf, out string rtf);
            if (DesktopTextClipboard.Copy(text, rtf: rtf) && cut)
                selection.SetText(TextSetOptions.None, "");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            GestureFeedback?.Invoke(this, "Could not copy selected text. " + error.Message);
        }
    }
}
