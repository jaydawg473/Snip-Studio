using System;
using System.Collections.Generic;
using System.Drawing;

namespace SnipStudio.WinUI.Backend;

public sealed partial class EditorSession
{
    private static readonly Lazy<IReadOnlyList<string>> installedTextFonts = new(() =>
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (FontFamily family in FontFamily.Families)
        {
            using (family)
                names.Add(family.Name);
        }
        return Array.AsReadOnly(new List<string>(names).ToArray());
    });

    public static IReadOnlyList<string> GetInstalledTextFonts() => installedTextFonts.Value;

    /// <summary>Validates formatted text without opening an external object, image, or data source.</summary>
    public static void ValidateFormattedText(string rtf) => global::SnipStudio.RichTextLayout.ValidateRtf(rtf);

    /// <summary>Reads the characters from a bounded, text-only rich document.</summary>
    public static string ReadFormattedText(string rtf) => global::SnipStudio.RichTextLayout.GetPlainText(rtf);
}
