#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SnipStudio.WinUI.Backend;

public sealed record OcrRedactionSuggestions(IReadOnlyList<ImageRectangle> EmailAddresses,
    IReadOnlyList<ImageRectangle> PhoneNumbers);

public sealed partial class EditorSession
{
    private static readonly Regex EmailPattern = new(
        @"(?<![\w.%+@-])[a-zA-Z0-9._%+-]{1,64}@[a-zA-Z0-9-]{1,63}(?:\.[a-zA-Z0-9-]{1,63})*\.[a-zA-Z]{2,24}(?![\w@-])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex PhonePattern = new(
        @"(?<![\w+])(?:\+1[ .-]?)?(?:\([2-9][0-9]{2}\)[ .-]?|[2-9][0-9]{2}[ .-])[2-9][0-9]{2}[ .-][0-9]{4}(?![\w-])|(?<![\w+])\+[1-9][0-9]{0,2}(?:[ .-][0-9]{2,4}){2,5}(?![\w-])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>Finds common email and phone formats whose recognized characters have source bounds.</summary>
    public OcrRedactionSuggestions SuggestOcrRedactions(OcrSnapshot snapshot)
    {
        VerifyAccess();
        RequireCurrentOcr(snapshot);
        var emails = new List<ImageRectangle>();
        var phones = new List<ImageRectangle>();
        if (snapshot.Available)
            foreach (OcrLineSnapshot line in snapshot.Lines)
            {
                if (emails.Count + phones.Count >= MaximumRedactionAreas) break;
                if (line.Text.Length > MaximumTextCharacters) continue;
                List<(int Start, int End, OcrBounds Bounds)> located = LocateRedactionWords(line);
                if (located.Count == 0) continue;
                AddMatches(EmailPattern, emails);
                AddMatches(PhonePattern, phones);

                void AddMatches(Regex pattern, List<ImageRectangle> target)
                {
                    foreach (Match match in pattern.Matches(line.Text))
                    {
                        if (emails.Count + phones.Count >= MaximumRedactionAreas) break;
                        if (RedactionMatchArea(line.Text, match.Index, match.Index + match.Length, located) is ImageRectangle area &&
                            !target.Contains(area))
                            target.Add(area);
                    }
                }
            }
        return new(emails.AsReadOnly(), phones.AsReadOnly());
    }

    private static List<(int Start, int End, OcrBounds Bounds)> LocateRedactionWords(OcrLineSnapshot line)
    {
        var words = new List<(int Start, int End, OcrBounds Bounds)>();
        int cursor = 0;
        foreach (OcrWordSnapshot word in line.Words)
        {
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            int start = line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (start < 0) return [];
            cursor = start + word.Text.Length;
            words.Add((start, cursor, word.Bounds));
        }
        return words;
    }

    private ImageRectangle? RedactionMatchArea(string line, int start, int end,
        List<(int Start, int End, OcrBounds Bounds)> words)
    {
        int coveredEnd = start;
        double left = ImageWidth, top = ImageHeight, right = 0, bottom = 0;
        foreach (var word in words)
        {
            if (word.End <= start) continue;
            if (word.Start >= end) break;
            // Every matched non-space character needs a real word rectangle.
            for (int i = coveredEnd; i < word.Start; i++)
                if (!char.IsWhiteSpace(line[i])) return null;
            OcrBounds bounds = word.Bounds;
            double wordRight = bounds.X + bounds.Width, wordBottom = bounds.Y + bounds.Height;
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(wordRight) ||
                !double.IsFinite(wordBottom) || bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.X < 0 || bounds.Y < 0 || wordRight > ImageWidth || wordBottom > ImageHeight)
                return null;
            left = Math.Min(left, bounds.X);
            top = Math.Min(top, bounds.Y);
            right = Math.Max(right, wordRight);
            bottom = Math.Max(bottom, wordBottom);
            coveredEnd = word.End;
        }
        if (coveredEnd < end) return null;
        left = Math.Max(0, left - 3);
        top = Math.Max(0, top - 3);
        right = Math.Min(ImageWidth, right + 3);
        bottom = Math.Min(ImageHeight, bottom + 3);
        return new(left, top, right - left, bottom - top);
    }
}
