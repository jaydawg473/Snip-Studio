using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio;

internal static class OcrTimestampRecovery
{
    private const int MaximumRows = 32;
    private const int MaximumCellRereads = 6;
    private static readonly Regex Timestamp = new(
        @"\A(?<date>[0-9]{1,2}/[0-9]{1,2}/[0-9]{4}) ?(?<hour>[0-9]{1,2}):?(?<minute>[0-9]{2})(?: ?(?<period>[AP]M))?\z",
        RegexOptions.CultureInvariant);

    private sealed record Cell(int Index, TextLineRegion Original, Match Observed);

    internal static async Task<int> RecoverAsync(Bitmap source, TextRecognitionResult primary, int maximumDimension,
        Func<Rectangle, Size, CancellationToken, Task<TextRecognitionResult>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!primary.Available || !Horizontal(primary.TextAngle) || primary.Lines.Count is < 8 or > 256 ||
            source.Height > 900 || (long)source.Width * source.Height > 1_600_000) return 0;

        List<Cell> cells = [];
        Rectangle frame = new(Point.Empty, source.Size);
        for (int index = 0; index < primary.Lines.Count; index++)
        {
            TextLineRegion line = primary.Lines[index];
            if (line?.Text is not { Length: >= 11 and <= 24 } || !Fits(line.Bounds, frame) ||
                !line.ExcludedGraphicBounds.IsEmpty || !line.RecognitionClipBounds.IsEmpty) continue;
            Match observed = Timestamp.Match(line.Text);
            if (observed.Success) cells.Add(new Cell(index, line, observed));
        }
        if (cells.Count is < 6 or > MaximumRows || !cells.Any(NeedsRecovery)) return 0;
        cells.Sort((first, second) => Center(first.Original.Bounds).CompareTo(Center(second.Original.Bounds)));

        // Only an isolated, regularly spaced column with no competing text
        // or graphics can use these views. A nearby unrelated AM/PM stays put.
        float start = cells.Min(cell => cell.Original.Bounds.Left);
        float end = cells.Max(cell => cell.Original.Bounds.Right);
        float height = cells.Select(cell => cell.Original.Bounds.Height).OrderBy(value => value).ElementAt(cells.Count / 2);
        float spacing = (Center(cells[^1].Original.Bounds) - Center(cells[0].Original.Bounds)) / (cells.Count - 1);
        if (height is < 6 or > 24 || spacing < height * 1.35f || spacing > height * 5 || end - start is < 45 or > 256) return 0;
        for (int index = 0; index < cells.Count; index++)
        {
            RectangleF bounds = cells[index].Original.Bounds;
            if (bounds.Left - start > 2 || Math.Abs(bounds.Height - height) > Math.Max(2, height * .25f) ||
                (index > 0 && Math.Abs(Center(bounds) - Center(cells[index - 1].Original.Bounds) - spacing) > 2)) return 0;
        }

        HashSet<int> cellIndices = cells.Select(cell => cell.Index).ToHashSet();
        int gridTop = Math.Max(0, (int)Math.Ceiling(Center(cells[0].Original.Bounds) - spacing / 2));
        int gridBottom = Math.Min(source.Height, (int)Math.Floor(Center(cells[^1].Original.Bounds) + spacing));
        int left = (int)Math.Floor(start), right = (int)Math.Ceiling(end), wideRight = source.Width;
        for (int index = 0; index < primary.Lines.Count; index++)
        {
            RectangleF other = primary.Lines[index].Bounds;
            if (!cellIndices.Contains(index) && other.Top < gridBottom && other.Bottom > gridTop && other.Left > end)
                wideRight = Math.Min(wideRight, (int)Math.Floor(other.Left) - 1);
        }
        var narrow = Rectangle.FromLTRB(Math.Max(0, left - (int)Math.Ceiling(height * .55)), gridTop,
            Math.Min(wideRight, right + (int)Math.Ceiling(height * .7)), gridBottom);
        var wide = Rectangle.FromLTRB(Math.Max(0, left - (int)Math.Ceiling(height * 1.8)), gridTop, wideRight, gridBottom);
        if (narrow == wide || primary.ProtectedGraphics.Any(bounds => bounds.IntersectsWith(wide))) return 0;
        for (int index = 0; index < primary.Lines.Count; index++)
            if (!cellIndices.Contains(index) && primary.Lines[index].Bounds.IntersectsWith(wide)) return 0;

        TextRecognitionResult contextA = await Read(narrow, 2).ConfigureAwait(false);
        TextRecognitionResult contextB = await Read(wide, 2).ConfigureAwait(false);
        var replacements = new List<(int Index, TextLineRegion Line)>();
        int cellRereads = 0;
        for (int index = 0; index < cells.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            Cell cell = cells[index];
            if (!NeedsRecovery(cell)) continue;
            float middle = Center(cell.Original.Bounds);
            int top = index == 0 ? gridTop : (int)Math.Ceiling((Center(cells[index - 1].Original.Bounds) + middle) / 2);
            // Leave a blank boundary row before the next cell. The source
            // word geometries still have to fit entirely inside this crop.
            int bottom = index + 1 == cells.Count ? gridBottom :
                (int)Math.Floor((middle + Center(cells[index + 1].Original.Bounds)) / 2 - .5);
            Rectangle band = Rectangle.FromLTRB(wide.Left, top, wide.Right, bottom);
            TextLineRegion first = FindCandidate(contextA, cell, band), second = FindCandidate(contextB, cell, band);
            if (first != null && second != null && first.Text != second.Text) continue;
            Rectangle clip = Rectangle.Intersect(band, narrow);
            if (!Agree(first, second) && cellRereads < MaximumCellRereads)
            {
                cellRereads++;
                Rectangle tight = Rectangle.FromLTRB(Math.Max(0, (int)Math.Floor(cell.Original.Bounds.Left) - 1), top, wide.Right, bottom);
                if (!Fits(cell.Original.Bounds, tight) || HitsInk(source, Rectangle.FromLTRB(wide.Left, top, tight.Left, bottom)))
                    continue;
                TextLineRegion context = first ?? second;
                // With one complete column read in hand, an isolated view at
                // its padded scale can corroborate the same visible glyphs
                // without magnifying a small cell far enough to skew its boxes.
                Rectangle preferred = context == null ? tight : Rectangle.FromLTRB(narrow.Left, top, wide.Right, bottom);
                TextLineRegion closeA = FindCandidate(await Read(preferred, context == null ? 1.5 : 2).ConfigureAwait(false), cell, preferred);
                if (Conflict(context, closeA)) continue;
                TextLineRegion closeB = Agree(context, closeA) ? null :
                    FindCandidate(await Read(tight, 4).ConfigureAwait(false), cell, tight);
                if (Conflict(closeA, closeB) || Conflict(context, closeB)) continue;
                if (Agree(closeA, closeB))
                {
                    first = closeA;
                    second = closeB;
                    clip = tight;
                }
                else if (Agree(context, closeA) || Agree(context, closeB))
                {
                    clip = first != null ? Rectangle.Intersect(tight, narrow) : tight;
                    first = context;
                    second = Agree(context, closeA) ? closeA : closeB;
                }
            }
            if (!Agree(first, second) || !Fits(first.Bounds, clip) || !ConfirmOriginalInk(source, cell, first)) continue;
            var recovered = new TextLineRegion { Bounds = first.Bounds, Text = first.Text, RecognitionClipBounds = clip };
            foreach (TextWordRegion word in first.Words)
                recovered.Words.Add(new TextWordRegion { Bounds = word.Bounds, Text = word.Text });
            replacements.Add((cell.Index, recovered));
        }

        token.ThrowIfCancellationRequested();
        if (replacements.Count == 0) return 0;
        foreach (var replacement in replacements) primary.Lines[replacement.Index] = replacement.Line;
        primary.Text = string.Join(Environment.NewLine, primary.Lines.Select(line => line.Text));
        return replacements.Count;

        async Task<TextRecognitionResult> Read(Rectangle crop, double scale)
        {
            token.ThrowIfCancellationRequested();
            var size = new Size((int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale));
            if (size.Width > maximumDimension || size.Height > maximumDimension) return null;
            TextRecognitionResult result = await read(crop, size, token).ConfigureAwait(false);
            primary.RecoveryPasses++;
            if (!string.IsNullOrEmpty(result?.RawText))
                primary.RecoveryRawText += (primary.RecoveryRawText.Length == 0 ? "" : Environment.NewLine) + result.RawText;
            return result;
        }
    }

    private static bool NeedsRecovery(Cell cell) => !cell.Observed.Groups["period"].Success ||
        cell.Original.Text != Canonical(cell.Observed);

    // Used only as an eligibility comparison. The accepted text always comes
    // unchanged from both reads and contains the original digits and case.
    private static string Canonical(Match value) => value.Groups["date"].Value + " " + value.Groups["hour"].Value + ":" +
        value.Groups["minute"].Value + " " + value.Groups["period"].Value;

    private static TextLineRegion FindCandidate(TextRecognitionResult view, Cell cell, Rectangle band)
    {
        if (view?.Available != true || !Horizontal(view.TextAngle) || view.ProtectedGraphics.Any(bounds => bounds.IntersectsWith(band)))
            return null;
        TextLineRegion candidate = null;
        foreach (TextLineRegion line in view.Lines)
        {
            if (!line.Bounds.IntersectsWith(band)) continue;
            if (candidate != null || !Fits(line.Bounds, band) || !line.ExcludedGraphicBounds.IsEmpty) return null;
            candidate = line;
        }
        if (candidate?.Text is not { Length: >= 16 and <= 24 } || candidate.Words.Count != 3 ||
            Math.Abs(candidate.Bounds.Left - cell.Original.Bounds.Left) > 2 ||
            Math.Abs(Center(candidate.Bounds) - Center(cell.Original.Bounds)) > 2) return null;

        Match observed = Timestamp.Match(candidate.Text);
        if (!observed.Success || !observed.Groups["period"].Success || candidate.Text != Canonical(observed)) return null;
        if (new[] { "date", "hour", "minute" }.Any(name => observed.Groups[name].Value != cell.Observed.Groups[name].Value) ||
            (cell.Observed.Groups["period"].Success && observed.Groups["period"].Value != cell.Observed.Groups["period"].Value)) return null;
        for (int index = 0; index < 3; index++)
            if (!Fits(candidate.Words[index].Bounds, candidate.Bounds)) return null;
        if (string.Join(" ", candidate.Words.Select(word => word.Text)) != candidate.Text) return null;
        if (candidate.Words[0].Bounds.Right >= candidate.Words[1].Bounds.Left ||
            candidate.Words[1].Bounds.Right >= candidate.Words[2].Bounds.Left) return null;
        return candidate;
    }

    private static bool Agree(TextLineRegion first, TextLineRegion second)
    {
        if (first == null || second == null || first.Text != second.Text) return false;
        for (int index = 0; index < 3; index++)
        {
            RectangleF a = first.Words[index].Bounds, b = second.Words[index].Bounds;
            if (Math.Abs(a.Left - b.Left) > 2 || Math.Abs(a.Right - b.Right) > 2 ||
                Math.Abs(a.Top - b.Top) > 2 || Math.Abs(a.Bottom - b.Bottom) > 2) return false;
        }
        return true;
    }

    private static bool Conflict(TextLineRegion first, TextLineRegion second) =>
        first != null && second != null && first.Text != second.Text;

    private static bool ConfirmOriginalInk(Bitmap source, Cell cell, TextLineRegion candidate)
    {
        RectangleF date = candidate.Words[0].Bounds, time = candidate.Words[1].Bounds, suffix = candidate.Words[2].Bounds;
        if (!BlankWordGap(source, date, time) || !BlankWordGap(source, time, suffix) || !HasColon(source, time)) return false;
        if (cell.Observed.Groups["period"].Success) return true;
        if (suffix.Left - cell.Original.Bounds.Right < 1 || suffix.Left <= time.Right + 1) return false;
        // A suffix can be restored only from its own previously unread ink,
        // with two separate visible letter bodies to the right of the old row.
        Rectangle box = PixelBounds(suffix);
        if (box.Width is < 8 or > 36 || box.Height is < 5 or > 24) return false;
        List<(Rectangle Box, int Count)> letters = ReadInkComponents(source, box);
        return letters?.Count == 2 && letters.All(letter => letter.Box.Width >= 3 && letter.Box.Height >= 5 && letter.Count >= 10) &&
            letters[0].Box.Right < letters[1].Box.Left && Math.Abs(letters[0].Box.Top - letters[1].Box.Top) <= 2;
    }

    private static bool BlankWordGap(Bitmap source, RectangleF first, RectangleF second)
    {
        Rectangle gap = Rectangle.FromLTRB((int)Math.Ceiling(first.Right),
            (int)Math.Floor(Math.Min(first.Top, second.Top)), (int)Math.Floor(second.Left),
            (int)Math.Ceiling(Math.Max(first.Bottom, second.Bottom)));
        return gap.Width >= 1 && !HitsInk(source, gap);
    }

    private static bool HasColon(Bitmap source, RectangleF bounds)
    {
        Rectangle box = PixelBounds(bounds);
        if (box.Width is < 10 or > 42 || box.Height is < 5 or > 24) return false;
        List<(Rectangle Box, int Count)> components = ReadInkComponents(source, box);
        if (components == null) return false;
        int pairs = 0;
        foreach (var top in components)
            foreach (var bottom in components)
            {
                if (top.Box.Width > 2 || top.Box.Height > 3 || top.Count > 4 || bottom.Box.Width > 2 ||
                    bottom.Box.Height > 3 || bottom.Count > 4 || bottom.Box.Top - top.Box.Bottom is < 1 or > 4 ||
                    Math.Min(top.Box.Right, bottom.Box.Right) <= Math.Max(top.Box.Left, bottom.Box.Left)) continue;
                Rectangle dots = Rectangle.Union(top.Box, bottom.Box);
                if (components.Any(item => item.Box.Width >= 3 && item.Box.Height >= 4 && item.Box.Right < dots.Left) &&
                    components.Any(item => item.Box.Width >= 3 && item.Box.Height >= 4 && item.Box.Left > dots.Right) &&
                    !components.Any(item => item.Box.Left <= dots.Left - 1 && item.Box.Right > dots.Left - 1 ||
                        item.Box.Left <= dots.Right && item.Box.Right > dots.Right)) pairs++;
            }
        return pairs == 1;
    }

    private static List<(Rectangle Box, int Count)> ReadInkComponents(Bitmap source, Rectangle box)
    {
        bool[,] ink = new bool[box.Width, box.Height];
        for (int x = 0; x < box.Width; x++)
            for (int y = 0; y < box.Height; y++)
            {
                Color color = source.GetPixel(box.Left + x, box.Top + y);
                if (color.A != 255) return null;
                ink[x, y] = Dark(color);
            }
        var components = new List<(Rectangle Box, int Count)>();
        for (int x = 0; x < box.Width; x++)
            for (int y = 0; y < box.Height; y++)
            {
                if (!ink[x, y]) continue;
                Rectangle extent = new(x, y, 1, 1);
                int pixels = 0;
                var pending = new Queue<Point>();
                Visit(x, y);
                while (pending.Count > 0)
                {
                    Point point = pending.Dequeue();
                    pixels++;
                    extent = Rectangle.Union(extent, new Rectangle(point, new Size(1, 1)));
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++) Visit(point.X + dx, point.Y + dy);
                }
                components.Add((extent, pixels));

                void Visit(int atX, int atY)
                {
                    if (atX < 0 || atY < 0 || atX >= box.Width || atY >= box.Height || !ink[atX, atY]) return;
                    ink[atX, atY] = false;
                    pending.Enqueue(new Point(atX, atY));
                }
            }
        return components;
    }

    private static Rectangle PixelBounds(RectangleF box) => Rectangle.FromLTRB((int)Math.Floor(box.Left), (int)Math.Floor(box.Top),
        (int)Math.Ceiling(box.Right), (int)Math.Ceiling(box.Bottom));

    private static bool HitsInk(Bitmap source, Rectangle bounds)
    {
        for (int x = bounds.Left; x < bounds.Right; x++)
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                Color color = source.GetPixel(x, y);
                if (color.A != 255 || Dark(color)) return true;
            }
        return false;
    }

    private static bool Dark(Color color) => 21 * color.R + 72 * color.G + 7 * color.B <= 18500;
    private static float Center(RectangleF bounds) => bounds.Top + bounds.Height / 2;
    private static bool Horizontal(double? angle) => !angle.HasValue || Math.Abs(angle.Value) <= 2;
    private static bool Fits(RectangleF box, RectangleF frame) => box.Width > 0 && box.Height > 0 && frame.Contains(box);
}
