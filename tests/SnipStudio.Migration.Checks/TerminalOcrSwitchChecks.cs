using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckTerminalOcrSwitchReview()
    {
        Check(TerminalOcrRecovery.TrySelectSwitchCorrection("/On1ine", "/Online", "/Online", out string corrected) &&
            corrected == "/Online", "Two focused native reads corroborate the exact terminal switch glyph");
        Check(!TerminalOcrRecovery.TrySelectSwitchCorrection("/On1ine", "/Online", "/On1ine", out corrected) &&
            corrected == "/On1ine", "A disputed terminal glyph preserves the whole-image text");
        Check(!TerminalOcrRecovery.TrySelectSwitchCorrection("/On1ine", "/online", "/online", out corrected) &&
            corrected == "/On1ine", "Terminal switch review preserves every other character and its case");
        Check(!TerminalOcrRecovery.TrySelectSwitchCorrection("\\A1Demo", "\\AlDemo", "\\AlDemo", out corrected) &&
            corrected == "\\A1Demo", "Terminal switch review cannot edit a path identifier");
        Check(!TerminalOcrRecovery.TrySelectSwitchCorrection("/A11Demo", "/AllDemo", "/AllDemo", out corrected) &&
            corrected == "/A11Demo", "Terminal switch review cannot change digits in a numeric run");
        Check(!TerminalOcrRecovery.TrySelectSwitchCorrection("/A1Demo", "/AlDemo\n", "/AlDemo\n", out corrected) &&
            corrected == "/A1Demo", "Terminal switch review refuses additional native text");
        completedGroups.Add("Terminal switch reread preserves uncorroborated and out-of-scope text");
    }
}
