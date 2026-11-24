using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace SnipStudio.WinUI;

internal static class DesktopTextClipboard
{
    internal static bool Copy(string? text, long? intent = null, string? rtf = null, string? html = null)
    {
        if (!DesktopImageActions.IsCurrentClipboardWrite(intent ?? DesktopImageActions.BeginClipboardWrite()))
            return false;

        DataPackage package = CreatePackage(text, rtf, html);
        if (!Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            { IsAllowedInHistory = false, IsRoamable = false }))
            throw new InvalidOperationException("Windows did not accept the clipboard text. Try copying again.");
        Clipboard.Flush();
        return true;
    }

    internal static DataPackage CreatePackage(string? text, string? rtf, string? html)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (text != null) package.SetText(text);
        if (!string.IsNullOrEmpty(rtf)) package.SetRtf(rtf);
        if (!string.IsNullOrEmpty(html)) package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        return package;
    }

    internal static void KeepSelectionLocal(TextBox box, Action<string> reportFailure)
    {
        box.CopyingToClipboard += (sender, args) =>
        {
            args.Handled = true;
            CopySelection(sender, cut: false, reportFailure);
        };
        box.CuttingToClipboard += (sender, args) =>
        {
            args.Handled = true;
            CopySelection(sender, cut: true, reportFailure);
        };
    }

    private static void CopySelection(TextBox box, bool cut, Action<string> reportFailure)
    {
        if (box.SelectionLength == 0 || cut && box.IsReadOnly) return;
        try
        {
            if (Copy(box.SelectedText) && cut) box.SelectedText = "";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            reportFailure("Could not copy selected text. " + error.Message);
        }
    }
}
