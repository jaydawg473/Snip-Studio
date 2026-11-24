#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using SnipStudio;

namespace SnipStudio.WinUI.Backend;

internal static partial class TerminalOcrRecovery
{
    private static bool RecoverRepeatedPrompt(TextRecognitionResult primary, OfflineOcrEngine.Response response,
        ReadOnlySpan<byte> gray, Size size, RectangleF panel, float pitch, CancellationToken token)
    {
        int origin = (int)Math.Round(panel.Left);
        foreach (var band in response.Rows.Where(row => row?.Bounds is not null)
                     .GroupBy(row => row.Bounds))
        {
            token.ThrowIfCancellationRequested();
            RectangleF candidate = band.Key.ToRectangle();
            if (!Valid(candidate, panel) || candidate.Bottom < panel.Bottom - pitch * 2 ||
                Math.Abs(candidate.Left - origin) > 1.5f || candidate.Height is < 7 or > 20 ||
                band.Any(row => row.Agreement is < 1 or > 3 || !double.IsFinite(row.Confidence) ||
                    row.Confidence is < .95 or > 1) ||
                band.Sum(row => row.Agreement) != 3)
                continue;

            string? prompt = null;
            foreach (var row in band)
            {
                string? read = ReadPromptVariant(row, panel, pitch);
                if (read is null || (prompt is not null && prompt != read))
                {
                    prompt = null;
                    break;
                }
                prompt = read;
            }
            if (prompt is null) continue;
            int right = origin + (int)Math.Floor(prompt.Length * pitch);
            int top = (int)Math.Ceiling(candidate.Top), bottom = (int)Math.Floor(candidate.Bottom);
            var bounds = Rectangle.FromLTRB(origin, top, right, bottom);
            if (!Valid(bounds, panel) || candidate.Right < right + 4 ||
                primary.ProtectedGraphics.Any(graphic => graphic.IntersectsWith(bounds)))
                continue;

            bool repeated = false;
            foreach (TextLineRegion earlier in primary.Lines)
            {
                if (earlier.Bounds.Bottom >= top - 2 || !Valid(earlier.Bounds, panel) ||
                    Math.Abs(earlier.Bounds.Left - origin) > 1.5f || earlier.Bounds.Right < right + pitch * 2 ||
                    earlier.Text.Length <= prompt.Length || !earlier.Text.StartsWith(prompt, StringComparison.Ordinal) ||
                    !char.IsAsciiLetterOrDigit(earlier.Text[prompt.Length]))
                    continue;
                int previousTop = (int)Math.Ceiling(earlier.Bounds.Top);
                if (previousTop + bounds.Height > size.Height) continue;
                if (!SamePromptPixels(gray, size.Width, bounds, previousTop, pitch, prompt.Length, out byte peak))
                    continue;
                if (!HasPromptCursor(gray, size.Width, candidate, right, top, bottom, pitch, peak)) continue;
                repeated = true;
                break;
            }
            if (!repeated) continue;

            var fragments = new List<int>();
            // Native word boxes can extend fractionally past the last cell.
            // Validate that overhang without enlarging the editable region.
            RectangleF nativeFit = RectangleF.FromLTRB(bounds.Left, bounds.Top, bounds.Right + 1, bounds.Bottom);
            bool occupied = false;
            for (int index = 0; index < primary.Lines.Count; index++)
            {
                TextLineRegion line = primary.Lines[index];
                RectangleF overlap = RectangleF.Intersect(line.Bounds, candidate);
                if (overlap.IsEmpty || overlap.Width <= 0 ||
                    overlap.Height < Math.Min(line.Bounds.Height, candidate.Height) * .5f)
                    continue;
                if (line.Text == prompt || !nativeFit.Contains(line.Bounds) || !line.ExcludedGraphicBounds.IsEmpty ||
                    line.Words.Count == 0 || !MatchesPromptFragment(line.Text, line.Bounds, prompt, origin, pitch) ||
                    line.Words.Any(word => !MatchesPromptFragment(word.Text, word.Bounds, prompt, origin, pitch)))
                {
                    occupied = true;
                    break;
                }
                fragments.Add(index);
            }
            if (occupied || fragments.Count == 0) continue;
            token.ThrowIfCancellationRequested();
            var recovered = new TextLineRegion
            {
                Text = prompt,
                Bounds = bounds,
                RecognitionClipBounds = RectangleF.FromLTRB(panel.Left, panel.Top, right, panel.Bottom)
            };
            recovered.Words.Add(new TextWordRegion { Text = prompt, Bounds = bounds });
            for (int index = fragments.Count - 1; index >= 0; index--)
                primary.Lines.RemoveAt(fragments[index]);
            primary.Lines.Insert(fragments[0], recovered);
            return true;
        }
        return false;
    }

    private static string? ReadPromptVariant(OfflineOcrEngine.Row row, RectangleF panel, float pitch)
    {
        string text = row.Text;
        if (text is null || text.Length is < 8 or > 96 || !char.IsAsciiLetter(text[0]) || text[1] != ':')
            return null;
        if (text[2] == ' ')
        {
            if (row.Words is not { Length: 2 }) return null;
            var drive = row.Words[0];
            var path = row.Words[1];
            if (drive?.Bounds is null || path?.Bounds is null || drive.Text != text[..2] || path.Text != text[3..] ||
                !Valid(drive.Bounds.ToRectangle(), panel) || !Valid(path.Bounds.ToRectangle(), panel) ||
                Math.Abs(path.Bounds.X - drive.Bounds.X - 2 * pitch) > pitch * .28f)
                return null;
            text = text.Remove(2, 1);
        }
        if (text.EndsWith('_')) text = text[..^1];
        if (text[2] != '\\' || text[^1] != '>') return null;
        foreach (char c in text.AsSpan(3, text.Length - 4))
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '-' or '\\'))
                return null;
        return text;
    }

    private static bool SamePromptPixels(ReadOnlySpan<byte> gray, int stride, Rectangle bounds, int previousTop,
        float pitch, int cells, out byte peak)
    {
        peak = 0;
        int totalDifference = 0, totalSignal = 0;
        for (int cell = 0; cell < cells; cell++)
        {
            int left = bounds.Left + (int)Math.Floor(cell * pitch);
            int right = bounds.Left + (int)Math.Floor((cell + 1) * pitch);
            int difference = 0, signal = 0;
            for (int y = 0; y < bounds.Height; y++)
            {
                for (int x = left; x < right; x++)
                {
                    byte current = gray[(bounds.Top + y) * stride + x];
                    byte earlier = gray[(previousTop + y) * stride + x];
                    peak = Math.Max(peak, current);
                    int amount = Math.Max(current, earlier);
                    if (amount < 14) continue;
                    difference += Math.Abs(current - earlier);
                    signal += amount;
                }
            }
            if (signal < 180 || difference > signal * .37) return false;
            totalDifference += difference;
            totalSignal += signal;
        }
        return totalDifference <= totalSignal * .27;
    }

    private static bool HasPromptCursor(ReadOnlySpan<byte> gray, int stride, RectangleF bounds, int textRight,
        int top, int bottom, float pitch, byte peak)
    {
        int cursorLeft = textRight + 1;
        int cursorRight = cursorLeft + (int)Math.Floor(pitch * .8);
        int cursorTop = bottom - 3;
        if (cursorRight - cursorLeft < 3 || cursorRight > Math.Floor(bounds.Right)) return false;
        int total = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = cursorLeft; x < cursorRight; x++)
            {
                int value = gray[y * stride + x];
                if (y < cursorTop)
                {
                    if (value > Math.Max(24, peak / 5)) return false;
                }
                else
                {
                    if (value < Math.Max(116, (int)peak)) return false;
                    total += value;
                }
            }
        }
        return total >= (cursorRight - cursorLeft) * 3 * peak * 1.06;
    }

    private static bool MatchesPromptFragment(string text, RectangleF bounds, string prompt, int origin, float pitch)
    {
        if (string.IsNullOrEmpty(text) || text.Length > prompt.Length) return false;
        int cell = (int)Math.Round((bounds.Left - origin) / pitch);
        return cell >= 0 && cell + text.Length <= prompt.Length &&
            Math.Abs(bounds.Left - origin - cell * pitch) <= pitch * .4f &&
            bounds.Right <= origin + (cell + text.Length + .4f) * pitch &&
            prompt.AsSpan(cell, text.Length).SequenceEqual(text.AsSpan());
    }
}
