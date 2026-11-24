using System.Drawing;
using SnipStudio;

internal static partial class Program
{
    private static void CheckOcrRichTextStability()
    {
        const string timestamp = "11/23/2026 4:38 PM";
        const float sourceFontSize = 12.35f;
        const float sourceWidth = 114;
        RichTextLayout.ClearCache();

        string rtf = RichTextLayout.CreatePlainRtf(timestamp, "Segoe UI", sourceFontSize, FontStyle.Regular,
            Color.FromArgb(54, 68, 84));
        RichTextMetrics prepared = RichTextLayout.Measure(rtf, sourceWidth);

        // The document and renderer must get the same pixels after eviction.
        RichTextLayout.ClearCache();
        RichTextMetrics reloaded = RichTextLayout.Measure(rtf, sourceWidth);
        Check(!prepared.InkBounds.IsEmpty && prepared.Size == reloaded.Size &&
            prepared.InkBounds == reloaded.InkBounds && RichTextLayout.GetPlainText(rtf) == timestamp,
            "OCR conversion preflight and cold rendering measure identical saved RTF and preserve its literal text");
        completedGroups.Add("OCR text layout remains stable after cache eviction");
    }
}
