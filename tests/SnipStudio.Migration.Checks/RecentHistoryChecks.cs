using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckRecentHistoryPolicy()
    {
        const string screenshot = @"C:\Snip Studio checks\0007-Exact.png";
        const string webm = @"C:\Snip Studio checks\recording.webm";
        const string mkv = @"C:\Snip Studio checks\recording.mkv";
        const string project = @"C:\Snip Studio checks\Editable.snipstudio";
        const string shellOnly = @"C:\Snip Studio checks\From Windows.jpg";
        IReadOnlyList<string> merged = DesktopRecentHistory.Merge(
            [screenshot, webm, mkv, project],
            [screenshot.ToUpperInvariant(), "relative.png", @"C:\Snip Studio checks\payload.exe", shellOnly]);
        Check(merged.SequenceEqual(new[] { screenshot, webm, mkv, project, shellOnly }),
            "Recent merges Windows documents after app-native history without replacing captured spelling, order, recordings, or projects");

        const string current = @"C:\Snip Studio checks\Snip Studio.exe";
        string arguments = '"' + screenshot + '"';
        Check(DesktopRecentHistory.GetOwnedLinkPath(current.ToUpperInvariant(), arguments, current) == screenshot,
            "Windows recent links must target the current executable and decode one quoted document path");
        Check(DesktopRecentHistory.GetOwnedLinkPath(@"C:\other\Snip Studio.exe", arguments, current) is null &&
            DesktopRecentHistory.GetOwnedLinkPath("Bad.exe", arguments, "Bad.exe") is null,
            "Other installations and malformed launch targets cannot contribute Windows document links");
        Check(DesktopRecentHistory.GetOwnedLinkPath(current, arguments + " --capture", current) is null &&
            DesktopRecentHistory.GetOwnedLinkPath(current, screenshot, current) is null &&
            DesktopRecentHistory.GetOwnedLinkPath(current, arguments + " " + arguments, current) is null,
            "Windows recent links cannot supply additional arguments, multiple documents, or unquoted input");
        Check(DesktopRecentHistory.NormalizeDocumentPath(@"\\?\C:\Snips\path.png") is null &&
            DesktopRecentHistory.NormalizeDocumentPath(@"C:\Snips\path.png:alternate.jpg") is null &&
            DesktopRecentHistory.NormalizeDocumentPath(@"C:\Snips\*.png") is null,
            "Recent paths reject device namespaces, alternate streams, and wildcards");

        string[] manyStore = Enumerable.Range(0, 45).Select(index => $@"C:\app\{index:D3}.png").ToArray();
        string[] manyShell = Enumerable.Range(0, 45).Select(index => $@"C:\shell\{index:D3}.png").ToArray();
        IReadOnlyList<string> bounded = DesktopRecentHistory.Merge(manyStore, manyShell);
        Check(bounded.Count == 64 && bounded[31] == manyStore[31] && bounded[32] == manyShell[0] && bounded[63] == manyShell[31],
            "Recent reads cap each source at 32 items while preserving store and shell order");

        Check(DesktopJumpList.ApplicationId == "AutomationForge.SnipStudio",
            "The stable desktop app retains the identity used by original Snip Studio shortcuts and recent documents");
        Check(DesktopJumpList.TryGetCaptureDelay([current, "--capture"], out int defaultDelay) && defaultDelay == 0,
            "Current capture links may omit a delay");
        foreach ((string argument, int seconds) in new[] { ("0", 0), ("3", 3), ("5", 5), ("10", 10) })
        {
            Check(DesktopJumpList.TryGetCaptureDelay([current, "--capture", "--delay", argument], out int legacyDelay) &&
                  DesktopJumpList.TryGetCaptureDelay([current, "--capture", argument], out int previewDelay) &&
                  legacyDelay == seconds && previewDelay == seconds,
                $"Original and current taskbar links request the same {seconds}-second delay");
        }
        Check(!DesktopJumpList.TryGetCaptureDelay([current, "--capture", "--delay", "6"], out _) &&
              !DesktopJumpList.TryGetCaptureDelay([current, "--capture", "--unknown", "3"], out _) &&
              !DesktopJumpList.TryGetCaptureDelay([current, "--capture", "--delay", "3", "--extra"], out _),
            "Taskbar capture rejects unsupported delays and unexpected options");
    }
}
