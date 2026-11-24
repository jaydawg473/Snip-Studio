using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio;

internal static class OcrSplitColonRecovery
{
    private const int MinimumMatchingRows = 3;
    private const int MaximumMatchingRows = 24;

    private sealed record Fragment(int LabelIndex, int TailIndex, TextLineRegion Label, TextLineRegion Tail, Rectangle Crop)
    {
        internal RectangleF Dot => Tail.Words[0].Bounds;
        internal string[] Words => [Label.Text[..^1] + ':', .. Tail.Words.Skip(1).Select(word => word.Text)];
        internal string Text => Label.Text[..^1] + ':' + Tail.Text[1..];
    }

    internal static async Task<int> RecoverAsync(Bitmap image, TextRecognitionResult primary, int maximumDimension,
        Func<Rectangle, Size, CancellationToken, Task<TextRecognitionResult>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!primary.Available || !Horizontal(primary.TextAngle) || primary.Lines.Count is < 6 or > 256 ||
            image.Height > 1100 || (long)image.Width * image.Height > 2_500_000) return 0;

        var fragments = FindFragments(image, primary, token);
        if (fragments.Count is < MinimumMatchingRows or > MaximumMatchingRows) return 0;
        // Require a distinct column of repeated, equally spaced splits. A
        // bullet, abbreviation, or isolated label elsewhere is not evidence.
        fragments.Sort((first, second) => first.Dot.Top.CompareTo(second.Dot.Top));
        Fragment first = fragments[0];
        float pitch = fragments[1].Dot.Top - first.Dot.Top;
        if (pitch is < 15 or > 80 || fragments.Any(fragment =>
            Math.Abs(fragment.Dot.Left - first.Dot.Left) > 2 ||
            Math.Abs(fragment.Label.Bounds.Left - first.Label.Bounds.Left) > 3) ||
            fragments.Zip(fragments.Skip(1)).Any(pair =>
                Math.Abs(pair.Second.Dot.Top - pair.First.Dot.Top - pitch) > 2)) return 0;

        var replacements = new Dictionary<int, TextLineRegion>();
        var removed = new HashSet<int>();
        foreach (Fragment fragment in fragments)
        {
            token.ThrowIfCancellationRequested();
            if (!HasOriginalColon(image, fragment)) continue;
            Rectangle crop = fragment.Crop;
            if (crop.Width > maximumDimension / 2 || crop.Height > maximumDimension / 2) continue;
            string[] words = fragment.Words;
            TextLineRegion firstView = await Read(1.5).ConfigureAwait(false);
            if (!Matches(firstView, fragment, words)) continue;
            TextLineRegion secondView = await Read(2).ConfigureAwait(false);
            if (!Matches(secondView, fragment, words) ||
                firstView.Words.Zip(secondView.Words).Any(pair => !Near(pair.First.Bounds, pair.Second.Bounds))) continue;

            secondView.RecognitionClipBounds = crop;
            int start = Math.Min(fragment.LabelIndex, fragment.TailIndex);
            int end = Math.Max(fragment.LabelIndex, fragment.TailIndex);
            replacements.Add(start, secondView);
            removed.Add(end);

            async Task<TextLineRegion> Read(double scale)
            {
                var size = new Size((int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale));
                TextRecognitionResult result = await read(crop, size, token).ConfigureAwait(false);
                primary.RecoveryPasses++;
                if (!string.IsNullOrEmpty(result?.RawText))
                    primary.RecoveryRawText += (primary.RecoveryRawText.Length == 0 ? "" : Environment.NewLine) + result.RawText;
                return result?.Available == true && Horizontal(result.TextAngle) && result.Lines.Count == 1
                    ? result.Lines[0] : null;
            }
        }

        token.ThrowIfCancellationRequested();
        if (replacements.Count == 0) return 0;
        for (int index = primary.Lines.Count - 1; index >= 0; index--)
        {
            if (removed.Contains(index)) primary.Lines.RemoveAt(index);
            else if (replacements.TryGetValue(index, out TextLineRegion line)) primary.Lines[index] = line;
        }
        primary.Text = string.Join(Environment.NewLine, primary.Lines.Select(line => line.Text));
        return replacements.Count;
    }

    private static List<Fragment> FindFragments(Bitmap image, TextRecognitionResult primary, CancellationToken token)
    {
        var frame = new Rectangle(Point.Empty, image.Size);
        var fragments = new List<Fragment>();
        for (int tailIndex = 0; tailIndex < primary.Lines.Count; tailIndex++)
        {
            token.ThrowIfCancellationRequested();
            TextLineRegion tail = primary.Lines[tailIndex];
            if (tail.Words.Count is < 3 or > 12 || tail.Text.Length > 160 || !IsUnrestricted(tail)) continue;
            TextWordRegion bullet = tail.Words[0];
            if (bullet.Text != "•" || !tail.Text.StartsWith("• ", StringComparison.Ordinal) ||
                !Fits(bullet.Bounds, tail.Bounds) ||
                bullet.Bounds.Width > 3 || bullet.Bounds.Height > 3 ||
                Math.Abs(bullet.Bounds.Left - tail.Bounds.Left) > 1 ||
                tail.Words[1].Bounds.Left - bullet.Bounds.Right < 3) continue;

            Fragment match = null;
            for (int labelIndex = 0; labelIndex < primary.Lines.Count; labelIndex++)
            {
                if (labelIndex == tailIndex) continue;
                TextLineRegion label = primary.Lines[labelIndex];
                if (label.Words.Count != 1 || label.Text != label.Words[0].Text || label.Text.Length is < 3 or > 25 ||
                    label.Text[^1] != '.' || !label.Text[..^1].All(char.IsLetterOrDigit) || !IsUnrestricted(label) ||
                    label.Bounds.Left >= bullet.Bounds.Left - 6 || Math.Abs(label.Bounds.Right - bullet.Bounds.Right) > 2 ||
                    !RectangleF.Inflate(label.Bounds, 1, 0).Contains(bullet.Bounds) ||
                    label.Bounds.Bottom - bullet.Bounds.Bottom is < 3 or > 10) continue;

                RectangleF ink = RectangleF.Union(label.Bounds, tail.Bounds);
                var crop = Rectangle.FromLTRB((int)Math.Floor(ink.Left) - 17, (int)Math.Floor(ink.Top) - 6,
                    (int)Math.Ceiling(ink.Right) + 21, (int)Math.Ceiling(ink.Bottom) + 6);
                if (!frame.Contains(crop) || ink.Height is < 8 or > 24 || crop.Width > 460 ||
                    primary.ProtectedGraphics.Any(((RectangleF)crop).IntersectsWith)) continue;
                if (primary.Lines.Where((_, index) => index != labelIndex && index != tailIndex)
                    .Any(other => other.Bounds.IntersectsWith(crop))) continue;
                if (match != null) { match = null; break; }
                match = new Fragment(labelIndex, tailIndex, label, tail, crop);
            }
            if (match == null) continue;
            if (fragments.Any(fragment => fragment.LabelIndex == match.LabelIndex)) return [];
            fragments.Add(match);
        }
        return fragments;
    }

    private static bool HasOriginalColon(Bitmap image, Fragment fragment)
    {
        RectangleF dot = fragment.Dot;
        int left = (int)Math.Floor(dot.Left);
        int right = (int)Math.Ceiling(dot.Right);
        int top = (int)Math.Floor(dot.Top);
        int lower = (int)Math.Ceiling(fragment.Label.Bounds.Bottom);
        if (right - left is < 1 or > 4) return false;

        var marks = new List<(int Start, int Height, int Mask)>();
        for (int y = top - 2; y <= lower + 1; y++)
        {
            int mask = 0;
            for (int x = left; x < right; x++)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.A != 255) return false;
                if (21 * pixel.R + 72 * pixel.G + 7 * pixel.B <= 18500) mask |= 1 << (x - left);
            }
            if (mask == 0) continue;
            if (marks.Count > 0 && marks[^1].Start + marks[^1].Height == y)
            {
                var mark = marks[^1];
                marks[^1] = (mark.Start, mark.Height + 1, mark.Mask | mask);
            }
            else marks.Add((y, 1, mask));
        }
        return marks.Count == 2 && marks.All(mark => mark.Height <= 3) &&
            marks[0].Start <= top + 1 && marks[0].Start + marks[0].Height >= top + 1 &&
            marks[1].Start - (marks[0].Start + marks[0].Height) is >= 1 and <= 7 &&
            marks[0].Mask == marks[1].Mask;
    }

    private static bool Matches(TextLineRegion line, Fragment fragment, string[] words)
    {
        if (line == null || !Fits(line.Bounds, fragment.Crop) || !line.ExcludedGraphicBounds.IsEmpty ||
            (!line.RecognitionClipBounds.IsEmpty && line.RecognitionClipBounds != (RectangleF)fragment.Crop) ||
            line.Text != fragment.Text || line.Words.Count != words.Length) return false;

        for (int index = 0; index < words.Length; index++)
        {
            TextWordRegion word = line.Words[index];
            RectangleF original = index == 0 ? fragment.Label.Bounds : fragment.Tail.Words[index].Bounds;
            if (word.Text != words[index] || !Fits(word.Bounds, line.Bounds) || !Near(word.Bounds, original)) return false;
        }
        return true;
    }

    private static bool Near(RectangleF a, RectangleF b) =>
        Math.Abs(a.Left - b.Left) <= 3 && Math.Abs(a.Right - b.Right) <= 3 &&
        Math.Abs(a.Top - b.Top) <= 3 && Math.Abs(a.Bottom - b.Bottom) <= 3;

    private static bool Horizontal(double? angle) => !angle.HasValue || Math.Abs(angle.Value) <= 2.000001;

    private static bool IsUnrestricted(TextLineRegion line) =>
        line.RecognitionClipBounds.IsEmpty && line.ExcludedGraphicBounds.IsEmpty;

    private static bool Fits(RectangleF bounds, RectangleF frame) =>
        bounds.Width > 0 && bounds.Height > 0 && frame.Contains(bounds);
}

