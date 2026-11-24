#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SnipStudio;

namespace SnipStudio.WinUI.Backend;

internal static partial class TerminalOcrRecovery
{
    private static readonly Regex Progress = new(
        @"^(\[=+)([0-9]{1,3}(?:\.[0-9]{1,3})?%)(\])$", RegexOptions.CultureInvariant);

    internal static bool Apply(TextRecognitionResult primary, OfflineOcrEngine.Response response,
        ReadOnlySpan<byte> gray, Size size, CancellationToken token)
    {
        if (response.Kind != "dark" || response.Panel is null ||
            response.Rows is not { Length: > 0 and <= 64 } ||
            size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height != gray.Length)
            return false;
        RectangleF panel = response.Panel.ToRectangle();
        if (!Valid(panel, new RectangleF(PointF.Empty, size))) return false;
        float pitch = MeasurePitch(response.Rows, panel);
        if (pitch <= 0) return false;

        bool changed = false;
        foreach (TextLineRegion line in primary.Lines)
        {
            token.ThrowIfCancellationRequested();
            if (!panel.Contains(line.Bounds))
                changed |= TrimCrossingPrefix(line, response.Rows, panel, pitch, primary.ProtectedGraphics);
            if (panel.Contains(line.Bounds)) changed |= JoinDrivePath(line, pitch);
        }

        foreach (OfflineOcrEngine.Row row in response.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (row?.Bounds is null || string.IsNullOrWhiteSpace(row.Text)) continue;
            RectangleF bounds = row.Bounds.ToRectangle();
            if (row.Agreement < 3 || !double.IsFinite(row.Confidence) ||
                row.Confidence is < .70 or > 1 || !Valid(bounds, panel) ||
                bounds.Left - panel.Left > 2 || row.Text.Length > 40) continue;
            Match match = Progress.Match(row.Text.Replace(" ", "", StringComparison.Ordinal));
            if (!match.Success || primary.ProtectedGraphics.Any(bounds.IntersectsWith)) continue;
            if (response.Rows.Any(other => other is not null && !ReferenceEquals(other, row) &&
                other.Bounds == row.Bounds && other.Text != row.Text)) continue;

            // Only consider complete raster pixels inside the reported bounds.
            // Rounding origin and size separately could read past the source edge.
            Rectangle raster = Rectangle.FromLTRB((int)Math.Ceiling(bounds.Left), (int)Math.Ceiling(bounds.Top),
                (int)Math.Floor(bounds.Right), (int)Math.Floor(bounds.Bottom));
            if (raster.Width <= 0 || raster.Height <= 0) continue;
            Rectangle[] runs = ReadInkRuns(gray, size.Width, raster, pitch);
            if (runs.Length != 3) continue;
            var progress = new TextLineRegion { Bounds = bounds, RecognitionClipBounds = panel };
            var text = new StringBuilder();
            int previousEnd = 0;
            for (int index = 0; index < runs.Length; index++)
            {
                string glyphs = match.Groups[index + 1].Value;
                int start = FindFirstCell(runs[index], glyphs.Length, panel.Left, pitch);
                if (start < previousEnd || (index == 0 ? start != 0 : start - previousEnd < 2)) break;
                text.Append(' ', start - previousEnd).Append(glyphs);
                progress.Words.Add(new TextWordRegion { Text = glyphs, Bounds = runs[index] });
                previousEnd = start + glyphs.Length;
            }
            if (progress.Words.Count != runs.Length) continue;
            progress.Text = text.ToString();
            changed |= ReplaceProgress(primary, progress);
        }
        changed |= RecoverTerminalPaths(primary, response, gray, size, panel, pitch, token);
        changed |= RecoverRepeatedPrompt(primary, response, gray, size, panel, pitch, token);
        return changed;
    }

    private static float MeasurePitch(IEnumerable<OfflineOcrEngine.Row> rows, RectangleF panel)
    {
        var measurements = new List<float>();
        foreach (OfflineOcrEngine.Row row in rows)
        {
            if (row?.Bounds is null || string.IsNullOrWhiteSpace(row.Text) ||
                row.Agreement < 3 || !double.IsFinite(row.Confidence) || row.Confidence is < .98 or > 1 ||
                !Valid(row.Bounds.ToRectangle(), panel) || row.Bounds.X - panel.Left > 2 ||
                row.Words is not { Length: >= 4 and <= 128 }) continue;
            var starts = new List<(int Offset, float X)>();
            int offset = 0;
            foreach (OfflineOcrEngine.Word word in row.Words)
            {
                if (word?.Bounds is null || string.IsNullOrEmpty(word.Text) ||
                    !Valid(word.Bounds.ToRectangle(), panel)) break;
                int next = row.Text.IndexOf(word.Text, offset, StringComparison.Ordinal);
                if (next < 0 || !string.IsNullOrWhiteSpace(row.Text[offset..next])) break;
                starts.Add((next, word.Bounds.X));
                offset = next + word.Text.Length;
            }
            if (starts.Count != row.Words.Length || starts[0].Offset != 0 || starts[^1].Offset < 12) continue;
            float pitch = (starts[^1].X - starts[0].X) / starts[^1].Offset;
            if (pitch is < 4 or > 20 || !float.IsFinite(pitch)) continue;
            if (starts.Any(start => Math.Abs(start.X - starts[0].X - start.Offset * pitch) > pitch * .28f)) continue;
            measurements.Add(pitch);
        }
        if (measurements.Count < 2) return 0;
        measurements.Sort();
        float median = measurements[measurements.Count / 2];
        return measurements.Count(pitch => Math.Abs(pitch - median) <= median * .025f) >= 2 ? median : 0;
    }

    private static bool JoinDrivePath(TextLineRegion line, float pitch)
    {
        string original = line.Text;
        int offset = 0;
        int separator = -1;
        for (int index = 0; index + 1 < line.Words.Count; index++)
        {
            TextWordRegion drive = line.Words[index];
            TextWordRegion path = line.Words[index + 1];
            int start = original.IndexOf(drive.Text, offset, StringComparison.Ordinal);
            if (start < 0) return false;
            offset = start + drive.Text.Length;
            if (drive.Text.Length != 2 || !char.IsAsciiLetter(drive.Text[0]) || drive.Text[1] != ':' ||
                path.Text.Length < 3 || path.Text[0] != '\\' || !char.IsAsciiLetterOrDigit(path.Text[1]) ||
                path.Text.IndexOf('\\', 1) < 0) continue;
            if (offset >= original.Length || original[offset] != ' ' ||
                !original.AsSpan(offset + 1).StartsWith(path.Text, StringComparison.Ordinal)) continue;
            float gap = path.Bounds.Left - drive.Bounds.Right;
            float advance = path.Bounds.Left - drive.Bounds.Left;
            if (gap < -1 || gap > pitch * .5f || Math.Abs(advance - 2 * pitch) > pitch * .25f ||
                Math.Abs(path.Bounds.Top - drive.Bounds.Top) > 2) continue;
            if (separator >= 0) return false;
            separator = offset;
        }
        if (separator < 0) return false;
        line.Text = original.Remove(separator, 1);
        return true;
    }

    private static bool TrimCrossingPrefix(TextLineRegion line, IEnumerable<OfflineOcrEngine.Row> rows,
        RectangleF panel, float pitch, IReadOnlyList<RectangleF> protectedGraphics)
    {
        if (line.Words.Count < 3 || line.Bounds.Left > panel.Left - pitch ||
            line.Bounds.Right > panel.Right || line.Bounds.Top < panel.Top || line.Bounds.Bottom > panel.Bottom ||
            !line.ExcludedGraphicBounds.IsEmpty || protectedGraphics.Any(line.Bounds.IntersectsWith))
            return false;
        TextWordRegion first = line.Words[0];
        if (first.Bounds.Left > panel.Left - pitch || first.Bounds.Right < panel.Left + 2 * pitch ||
            !line.Text.StartsWith(first.Text + " ", StringComparison.Ordinal) ||
            line.Words.Skip(1).Any(word => !Valid(word.Bounds, panel)))
            return false;

        OfflineOcrEngine.Row[] candidates = rows.Where(row =>
        {
            if (row?.Bounds is null || row.Agreement is < 1 or > 3 || !double.IsFinite(row.Confidence) ||
                row.Confidence is < .95 or > 1 ||
                !Valid(row.Bounds.ToRectangle(), panel) || row.Bounds.X > panel.Left + 2) return false;
            RectangleF overlap = RectangleF.Intersect(row.Bounds.ToRectangle(), line.Bounds);
            return overlap.Height >= Math.Min(line.Bounds.Height, row.Bounds.Height) * .65f;
        }).ToArray();
        if (candidates.Length == 0 || candidates.Sum(row => row.Agreement) != 3 ||
            candidates.Any(row => row.Bounds != candidates[0].Bounds || row.Words is null ||
                row.Words.Length < line.Words.Count || row.Words.Take(line.Words.Count).Any(word =>
                    word?.Bounds is null || string.IsNullOrEmpty(word.Text) || !Valid(word.Bounds.ToRectangle(), panel))))
            return false;
        OfflineOcrEngine.Word clean = candidates[0].Words[0];
        RectangleF cleanInk = clean.Bounds.ToRectangle();
        if (clean.Text.Length < 3 || clean.Text.Length >= first.Text.Length ||
            !first.Text.EndsWith(clean.Text, StringComparison.Ordinal) || !Valid(cleanInk, panel) ||
            cleanInk.Left > panel.Left + 2 || Math.Abs(cleanInk.Right - first.Bounds.Right) > 2)
            return false;

        // Each alternate must corroborate every already-extracted word. Their
        // unread tail can disagree and remains outside the replacement.
        foreach (OfflineOcrEngine.Row candidate in candidates)
        {
            if (candidate.Words[0] != clean) return false;
            for (int index = 1; index < line.Words.Count; index++)
            {
                TextWordRegion native = line.Words[index];
                OfflineOcrEngine.Word alternate = candidate.Words[index];
                if (native.Text != alternate.Text || Math.Abs(native.Bounds.Left - alternate.Bounds.X) > 3 ||
                    Math.Abs(native.Bounds.Right - alternate.Bounds.ToRectangle().Right) > 3)
                    return false;
            }
        }

        RectangleF retained = cleanInk;
        for (int index = 1; index < line.Words.Count; index++)
            retained = RectangleF.Union(retained, line.Words[index].Bounds);
        RectangleF clip = line.RecognitionClipBounds.IsEmpty ? panel :
            RectangleF.Intersect(panel, line.RecognitionClipBounds);
        if (!Valid(retained, clip)) return false;
        line.Text = clean.Text + line.Text[first.Text.Length..];
        first.Text = clean.Text;
        first.Bounds = cleanInk;
        line.Bounds = retained;
        line.RecognitionClipBounds = clip;
        return true;
    }

    private static Rectangle[] ReadInkRuns(ReadOnlySpan<byte> gray, int stride, Rectangle bounds, float pitch)
    {
        // Ignore antialiasing noise. The ink at both edges of each run must
        // fit its recognized number of terminal cells before spaces are added.
        var runs = new List<Rectangle>(3);
        Rectangle current = Rectangle.Empty;
        for (int x = bounds.Left; x < bounds.Right; x++)
        {
            Rectangle ink = Rectangle.Empty;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                if (gray[y * stride + x] < 40) continue;
                Rectangle pixel = new(x, y, 1, 1);
                ink = ink.IsEmpty ? pixel : Rectangle.Union(ink, pixel);
            }
            if (ink.IsEmpty) continue;
            if (!current.IsEmpty && ink.Left - current.Right >= Math.Max(12, pitch * 2))
            {
                runs.Add(current);
                if (runs.Count > 2) return [];
                current = Rectangle.Empty;
            }
            current = current.IsEmpty ? ink : Rectangle.Union(current, ink);
        }
        if (!current.IsEmpty) runs.Add(current);
        return runs.ToArray();
    }

    private static int FindFirstCell(Rectangle ink, int characters, float origin, float pitch)
    {
        int nearest = (int)Math.Round((ink.Left - origin) / pitch);
        int match = -1;
        for (int cell = Math.Max(0, nearest - 1); cell <= nearest + 1; cell++)
        {
            float left = origin + cell * pitch;
            float right = left + characters * pitch;
            if (ink.Left < left - 1.4f || ink.Left > left + pitch * .65f ||
                ink.Right > right + 1.4f || ink.Right < right - pitch * .65f) continue;
            if (match >= 0) return -1;
            match = cell;
        }
        return match;
    }

    private static bool ReplaceProgress(TextRecognitionResult primary, TextLineRegion progress)
    {
        int matched = -1;
        for (int index = 0; index < primary.Lines.Count; index++)
        {
            TextLineRegion prior = primary.Lines[index];
            RectangleF overlap = RectangleF.Intersect(progress.Bounds, prior.Bounds);
            if (overlap.Width < 1 || overlap.Height < Math.Min(prior.Bounds.Height, progress.Bounds.Height) * .5f)
                continue;
            if (matched >= 0 || !prior.ExcludedGraphicBounds.IsEmpty || !progress.Words.Any(word =>
            {
                RectangleF ink = word.Bounds;
                ink.Inflate(2, 2);
                return ink.Contains(prior.Bounds);
            })) return false;
            matched = index;
        }
        if (matched >= 0) primary.Lines[matched] = progress;
        else
        {
            int next = primary.Lines.FindIndex(line =>
                line.Bounds.Top > progress.Bounds.Top && progress.RecognitionClipBounds.Contains(line.Bounds));
            primary.Lines.Insert(next < 0 ? primary.Lines.Count : next, progress);
        }
        return true;
    }

    private static bool Valid(RectangleF value, RectangleF container) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Width) && float.IsFinite(value.Height) &&
        value.Width >= 1 && value.Height >= 1 && container.Contains(value);
}
