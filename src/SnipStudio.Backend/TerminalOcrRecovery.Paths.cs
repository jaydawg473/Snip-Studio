#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using SnipStudio;

namespace SnipStudio.WinUI.Backend;

internal static partial class TerminalOcrRecovery
{
    private static bool RecoverTerminalPaths(TextRecognitionResult primary, OfflineOcrEngine.Response response,
        ReadOnlySpan<byte> gray, Size size, RectangleF panel, float pitch, CancellationToken token)
    {
        bool changed = false;
        foreach (var band in response.Rows.Where(row => PathCandidate(row, panel)).GroupBy(row => row.Bounds).Take(4))
        {
            token.ThrowIfCancellationRequested();
            var variants = band.ToArray();
            if (variants.Length is < 2 or > 3 || variants.Sum(row => row.Agreement) != 3) continue;
            var first = variants[0];
            var inkBounds = first.Bounds.ToRectangle();
            if (primary.ProtectedGraphics.Any(inkBounds.IntersectsWith) ||
                !variants.All(row => SamePathAnchors(first, row, pitch))) continue;

            var existing = primary.Lines.Where(line => inkBounds.IntersectsWith(line.Bounds)).Take(2).ToArray();
            if (existing.Length != 1 || !existing[0].ExcludedGraphicBounds.IsEmpty ||
                !PrefixMatches(existing[0], first)) continue;

            string[] paths = variants.Select(row => row.Words[^1].Text).Distinct(StringComparer.Ordinal).ToArray();
            if (paths.Length < 2 || paths.Any(word => word.Length != paths[0].Length || word.Length > 128)) continue;
            if (!ResolvePathGlyphs(paths, first, response, gray, size, panel, pitch, out string winner)) continue;

            var selected = variants.First(row => row.Words[^1].Text == winner);
            var replacement = new TextLineRegion { Text = selected.Text, Bounds = inkBounds, RecognitionClipBounds = panel };
            if (!existing[0].RecognitionClipBounds.IsEmpty)
                replacement.RecognitionClipBounds = RectangleF.Intersect(panel, existing[0].RecognitionClipBounds);
            if (!replacement.RecognitionClipBounds.Contains(inkBounds)) continue;
            foreach (var word in selected.Words)
                replacement.Words.Add(new TextWordRegion { Text = word.Text, Bounds = word.Bounds.ToRectangle() });
            JoinDrivePath(replacement, pitch);
            primary.Lines[primary.Lines.IndexOf(existing[0])] = replacement;
            changed = true;
        }
        return changed;
    }

    private static bool SamePathAnchors(OfflineOcrEngine.Row first, OfflineOcrEngine.Row alternate, float pitch)
    {
        if (alternate.Words.Length != first.Words.Length ||
            !alternate.Words[..^2].SequenceEqual(first.Words[..^2]) ||
            alternate.Words[^2].Text != first.Words[^2].Text) return false;

        // OCR may allocate a few of the same boundary pixels to the drive or
        // the backslash. The rest of the row and the path's outer edge must agree.
        var drive = first.Words[^2].Bounds;
        var otherDrive = alternate.Words[^2].Bounds;
        var path = first.Words[^1].Bounds;
        var otherPath = alternate.Words[^1].Bounds;
        return drive.X == otherDrive.X && drive.Y == otherDrive.Y && drive.Height == otherDrive.Height &&
            Math.Abs(drive.Width - otherDrive.Width) <= pitch * .5f &&
            path.Y == otherPath.Y && path.Height == otherPath.Height &&
            path.X + path.Width == otherPath.X + otherPath.Width && Math.Abs(path.X - otherPath.X) <= 1;
    }

    private static bool PathCandidate(OfflineOcrEngine.Row? row, RectangleF panel)
    {
        if (row?.Bounds is null || row.Agreement is < 1 or > 3 || row.Confidence is < .95 or > 1 ||
            !double.IsFinite(row.Confidence) || row.Words is not { Length: >= 6 and <= 32 } ||
            !Valid(row.Bounds.ToRectangle(), panel) || row.Bounds.X > panel.Left + 2 ||
            row.Words.Any(word => word?.Bounds is null || string.IsNullOrEmpty(word.Text) ||
                !Valid(word.Bounds.ToRectangle(), row.Bounds.ToRectangle()))) return false;
        string drive = row.Words[^2].Text, path = row.Words[^1].Text;
        if (drive.Length != 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':' ||
            path.Length < 8 || path[0] != '\\' || path.IndexOf('\\', 1) < 0 ||
            path.LastIndexOf('.') <= path.LastIndexOf('\\')) return false;
        return row.Text == string.Join(' ', row.Words.Select(word => word.Text));
    }

    private static bool PrefixMatches(TextLineRegion existing, OfflineOcrEngine.Row alternate)
    {
        int count = existing.Words.Count;
        if (count < 4 || count >= alternate.Words.Length ||
            (existing.Text != string.Join(' ', existing.Words.Select(word => word.Text)))) return false;
        for (int index = 0; index < count; index++)
        {
            var native = existing.Words[index];
            var word = alternate.Words[index];
            if (Math.Abs(native.Bounds.Top - word.Bounds.Y) > 2 ||
                Math.Abs(native.Bounds.Right - word.Bounds.ToRectangle().Right) > 3) return false;
            if (native.Text == word.Text && Math.Abs(native.Bounds.Left - word.Bounds.X) <= 3) continue;
            if (index != 0 || word.Text.Length != native.Text.Length + 1 ||
                !word.Text.EndsWith(native.Text, StringComparison.Ordinal) || native.Bounds.Left <= word.Bounds.X) return false;
        }
        return true;
    }

    private static bool ResolvePathGlyphs(string[] paths, OfflineOcrEngine.Row anchor, OfflineOcrEngine.Response response,
        ReadOnlySpan<byte> gray, Size size, RectangleF panel, float pitch, out string winner)
    {
        winner = "";
        int differences = 0;
        string[] surviving = paths;
        for (int i = 0; i < paths[0].Length; i++)
        {
            char first = paths[0][i];
            if (paths.All(path => path[i] == first)) continue;
            if (++differences > 2) return false;
            if (paths.All(path => path[i] is 'W' or 'w'))
            {
                int knownUpper = Enumerable.Range(0, paths[0].Length).FirstOrDefault(
                    position => paths.All(path => path[position] == 'W'), -1);
                if (knownUpper < 0 || !HasMatchingCapShape(gray, size, panel, pitch, anchor, response, knownUpper, i)) return false;
                surviving = surviving.Where(path => path[i] == 'W').ToArray();
            }
            else if (paths.All(path => path[i] is 'l' or '1'))
            {
                if (!HasMatchingAscender(gray, size, panel, pitch, anchor, response, i)) return false;
                surviving = surviving.Where(path => path[i] == 'l').ToArray();
            }
            else return false;
        }
        // A surviving literal must also be consistent against the variants
        // discarded by the first glyph check.
        if (surviving.Length != 1 || differences == 0) return false;
        winner = surviving[0];
        return true;
    }

    private static bool HasMatchingCapShape(ReadOnlySpan<byte> gray, Size size, RectangleF panel, float pitch,
        OfflineOcrEngine.Row anchor, OfflineOcrEngine.Response response, int referenceIndex, int targetIndex)
    {
        Point reference = PathGlyphOrigin(anchor, anchor.Words[^1], referenceIndex, panel, pitch);
        Point target = PathGlyphOrigin(anchor, anchor.Words[^1], targetIndex, panel, pitch);
        int width = (int)Math.Ceiling(pitch);
        int top = StemPairTop(gray, size, target, width);
        if (top is < 0 or > 2 || StemPairTop(gray, size, reference, width) != top) return false;
        foreach (var row in TrustedTerminalRows(response, panel))
        {
            foreach (var word in row.Words)
            {
                int lower = word.Text.IndexOf('w');
                if (lower < 0) continue;
                int xHeight = StemPairTop(gray, size, PathGlyphOrigin(row, word, lower, panel, pitch), width);
                if (xHeight > top && xHeight <= top + 3) return true;
            }
        }
        return false;
    }

    private static int StemPairTop(ReadOnlySpan<byte> gray, Size size, Point glyph, int width)
    {
        if (glyph.X < 0 || glyph.X + width > size.Width || glyph.Y < 0 || glyph.Y + 6 > size.Height) return -1;
        for (int y = 0; y < 5; y++)
        {
            ReadOnlySpan<byte> scan = gray.Slice((glyph.Y + y) * size.Width + glyph.X, width);
            int first = -1;
            for (int x = 0; x < scan.Length; x++)
            {
                if (scan[x] < 40) continue;
                if (first < 0) first = x;
                else if (x >= first + 3 && scan[first + (x - first) / 2] < 25) return y;
            }
        }
        return -1;
    }

    private static bool HasMatchingAscender(ReadOnlySpan<byte> gray, Size size, RectangleF panel, float pitch,
        OfflineOcrEngine.Row anchor, OfflineOcrEngine.Response response, int targetIndex)
    {
        Point target = PathGlyphOrigin(anchor, anchor.Words[^1], targetIndex, panel, pitch);
        int width = (int)Math.Round(pitch);
        double bestDigit = 1, bestLetter = 1, secondLetter = 1;
        int confirmedRows = 0, digitRows = 0;
        foreach (var row in TrustedTerminalRows(response, panel))
        {
            double bestLowercase = 1;
            bool hadDigit = false;
            foreach (var word in row.Words)
            {
                for (int position = 0; position < word.Text.Length; position++)
                {
                    char letter = word.Text[position];
                    if (letter is not ('l' or '1')) continue;
                    Point sample = PathGlyphOrigin(row, word, position, panel, pitch);
                    double score = GlyphDistance(gray, size, target, sample, width);
                    if (letter == 'l') bestLowercase = Math.Min(score, bestLowercase);
                    else if (word.Text.All(character => char.IsAsciiDigit(character) || character is '.' or ':' or '-'))
                    {
                        bestDigit = Math.Min(score, bestDigit);
                        hadDigit |= score < 1;
                    }
                }
            }
            if (bestLowercase <= .34)
            {
                confirmedRows++;
                if (bestLowercase < bestLetter) (bestLetter, secondLetter) = (bestLowercase, bestLetter);
                else if (bestLowercase < secondLetter) secondLetter = bestLowercase;
            }
            if (hadDigit) digitRows++;
        }
        return confirmedRows >= 2 && digitRows >= 2 && bestDigit - secondLetter >= .06;
    }

    private static Point PathGlyphOrigin(OfflineOcrEngine.Row row, OfflineOcrEngine.Word word,
        int offset, RectangleF panel, float pitch)
    {
        float column = MathF.Round((word.Bounds.X - panel.Left) / pitch) + offset;
        return new Point((int)MathF.Floor(panel.Left + column * pitch), (int)MathF.Floor(row.Bounds.Y));
    }

    private static OfflineOcrEngine.Row[] TrustedTerminalRows(OfflineOcrEngine.Response response, RectangleF panel) =>
        response.Rows.Where(row => row is { Agreement: 3, Confidence: >= .98 and <= 1, Bounds: not null } &&
            row.Words is { Length: > 1 and <= 64 } && Valid(row.Bounds.ToRectangle(), panel) &&
            row.Words.All(word => word?.Bounds is not null && word.Text is { Length: > 0 and <= 128 } &&
                Valid(word.Bounds.ToRectangle(), row.Bounds.ToRectangle()))).Take(24).ToArray();

    private static double GlyphDistance(ReadOnlySpan<byte> gray, Size size, Point target, Point sample, int width)
    {
        const int height = 8;
        if (target.X < 0 || target.X + width > size.Width || target.Y < 0 || target.Y + height > size.Height) return 1;
        double best = 1;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            Point shifted = new Point(sample.X + dx, sample.Y + dy);
            if (shifted.X < 0 || shifted.X + width > size.Width || shifted.Y < 0 || shifted.Y + height > size.Height) continue;
            int difference = 0, combined = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int a = gray[(target.Y + y) * size.Width + target.X + x];
                int b = gray[(shifted.Y + y) * size.Width + shifted.X + x];
                if (a < 14) a = 0;
                if (b < 14) b = 0;
                difference += Math.Abs(a - b);
                combined += Math.Max(a, b);
            }
            if (combined > 300) best = Math.Min(best, (double)difference / combined);
        }
        return best;
    }
}
