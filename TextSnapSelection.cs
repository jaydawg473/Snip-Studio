using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace SnipStudio
{
    // Pure geometry: the recognizer supplies reading order and image-pixel boxes.
    public static class TextSnapSelection
    {
        private static readonly object SnapshotGate = new object();
        private static readonly ConditionalWeakTable<IList<TextLineRegion>, WordSnapshot> Snapshots =
            new ConditionalWeakTable<IList<TextLineRegion>, WordSnapshot>();
        private static readonly List<WordPosition> EmptyWords = new List<WordPosition>();
        public static bool CanStart(IList<TextLineRegion> lines, PointF start, float tolerance)
        {
            if (!Finite(start)) return false;
            List<WordPosition> words = Flatten(lines);
            return FindStart(words, start, CleanTolerance(tolerance)) >= 0;
        }

        public static List<RectangleF> BuildBands(IList<TextLineRegion> lines, PointF start, PointF end, float tolerance)
        {
            if (!Finite(start) || !Finite(end)) return new List<RectangleF>();
            return BuildEndpointBands(Flatten(lines), start, end, tolerance, splitWideGaps: false);
        }

        public static List<RectangleF> BuildStrokeBands(IList<TextLineRegion> lines, PointF start, PointF end, float tolerance)
        {
            if (!Finite(start) || !Finite(end)) return new List<RectangleF>();
            List<WordPosition> words = Flatten(lines);
            List<RectangleF> bands = BuildEndpointBands(words, start, end, tolerance, splitWideGaps: true);
            if (bands.Count != 0 || start == end) return bands;

            WordPosition previous = null;
            RectangleF union = RectangleF.Empty;
            foreach (WordPosition word in words)
            {
                if (!IntersectsStroke(Pad(word.Bounds), start, end))
                {
                    if (previous != null) bands.Add(Pad(union));
                    previous = null;
                    continue;
                }

                if (previous != null && IsSameLineRun(previous, word)) union = RectangleF.Union(union, word.Bounds);
                else
                {
                    if (previous != null) bands.Add(Pad(union));
                    union = word.Bounds;
                }
                previous = word;
            }
            if (previous != null) bands.Add(Pad(union));
            return bands;
        }

        private static List<RectangleF> BuildEndpointBands(List<WordPosition> words, PointF start, PointF end,
            float tolerance, bool splitWideGaps)
        {
            List<RectangleF> bands = new List<RectangleF>();
            if (words.Count == 0) return bands;
            tolerance = CleanTolerance(tolerance);
            int first = FindStart(words, start, tolerance);
            if (first < 0) return bands;
            int last = FindEnd(words, end, tolerance, words[first].Bounds.Height);
            if (last < 0) return bands;
            int lower = Math.Min(first, last), upper = Math.Max(first, last);

            // Reading order is meaningful within a block. Do not bridge a large
            // blank gap or jump between disjoint side-by-side text columns.
            for (int index = lower + 1; index <= upper; index++)
            {
                WordPosition previous = words[index - 1], next = words[index];
                if (previous.Line != next.Line && !SameTextBlock(previous.LineBounds, next.LineBounds))
                    return bands;
            }

            int currentLine = -1;
            RectangleF union = RectangleF.Empty;
            for (int index = lower; index <= upper; index++)
            {
                WordPosition word = words[index];
                if (word.Line != currentLine || (splitWideGaps && !IsSameLineRun(words[index - 1], word)))
                {
                    if (currentLine >= 0) bands.Add(Pad(union));
                    union = word.Bounds;
                    currentLine = word.Line;
                }
                else union = RectangleF.Union(union, word.Bounds);
            }
            if (currentLine >= 0) bands.Add(Pad(union));
            return bands;
        }

        private static bool IsSameLineRun(WordPosition previous, WordPosition next)
        {
            if (previous.Line != next.Line) return false;
            RectangleF first = previous.Bounds, second = next.Bounds;
            float gap = Math.Max(first.Left, second.Left) - Math.Min(first.Right, second.Right);
            return gap <= Math.Max(6, Math.Min(first.Height, second.Height));
        }

        private static bool IntersectsStroke(RectangleF bounds, PointF start, PointF end)
        {
            double first = 0, last = 1;
            return ClipAxis(bounds.Left, bounds.Right, start.X, end.X, ref first, ref last) &&
                ClipAxis(bounds.Top, bounds.Bottom, start.Y, end.Y, ref first, ref last);
        }

        private static bool ClipAxis(double minimum, double maximum, double start, double end, ref double first, ref double last)
        {
            double length = end - start;
            if (length == 0) return start >= minimum && start <= maximum;
            double enter = (minimum - start) / length, leave = (maximum - start) / length;
            first = Math.Max(first, Math.Min(enter, leave));
            last = Math.Min(last, Math.Max(enter, leave));
            return first <= last;
        }

        private static List<WordPosition> Flatten(IList<TextLineRegion> lines)
        {
            if (lines == null) return EmptyWords;
            lock (SnapshotGate)
            {
                WordSnapshot snapshot;
                if (!Snapshots.TryGetValue(lines, out snapshot) || !snapshot.Matches(lines))
                {
                    snapshot = new WordSnapshot(lines.Count, CopyWords(lines));
                    Snapshots.Remove(lines);
                    Snapshots.Add(lines, snapshot);
                }
                return snapshot.Words;
            }
        }

        private static List<WordPosition> CopyWords(IList<TextLineRegion> lines)
        {
            List<WordPosition> words = new List<WordPosition>();
            if (lines == null) return words;
            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                TextLineRegion line = lines[lineIndex];
                if (line == null || line.Words == null) continue;
                RectangleF lineBounds = RectangleF.Empty;
                bool haveBounds = false;
                foreach (TextWordRegion word in line.Words)
                {
                    if (word == null || !Valid(word.Bounds)) continue;
                    lineBounds = haveBounds ? RectangleF.Union(lineBounds, word.Bounds) : word.Bounds;
                    haveBounds = true;
                }
                if (!haveBounds) continue;
                foreach (TextWordRegion word in line.Words)
                    if (word != null && Valid(word.Bounds))
                        words.Add(new WordPosition { Line = lineIndex, Bounds = word.Bounds, LineBounds = lineBounds });
            }
            return words;
        }

        private sealed class WordSnapshot
        {
            internal readonly List<WordPosition> Words;
            private readonly int lineCount;
            internal WordSnapshot(int count, List<WordPosition> words) { lineCount = count; Words = words; }
            internal bool Matches(IList<TextLineRegion> lines)
            {
                if (lines.Count != lineCount) return false;
                int index = 0;
                for (int line = 0; line < lines.Count; line++)
                {
                    TextLineRegion source = lines[line];
                    if (source == null || source.Words == null) continue;
                    foreach (TextWordRegion word in source.Words)
                    {
                        if (word == null || !Valid(word.Bounds)) continue;
                        if (index >= Words.Count || Words[index].Line != line || Words[index].Bounds != word.Bounds) return false;
                        index++;
                    }
                }
                return index == Words.Count;
            }
        }

        private static int FindStart(List<WordPosition> words, PointF point, float tolerance)
        {
            int best = -1;
            double bestDistance = Double.MaxValue, bestCenterDistance = Double.MaxValue;
            for (int index = 0; index < words.Count; index++)
            {
                double distance = Distance(words[index].Bounds, point);
                double center = CenterDistance(words[index].Bounds, point);
                if (distance > tolerance) continue;
                if (distance < bestDistance || (Math.Abs(distance - bestDistance) < .001 && center < bestCenterDistance))
                { best = index; bestDistance = distance; bestCenterDistance = center; }
            }
            return best;
        }

        private static int FindEnd(List<WordPosition> words, PointF point, float tolerance, float anchorHeight)
        {
            int best = -1;
            double bestDistance = Double.MaxValue, bestCenterDistance = Double.MaxValue;
            for (int index = 0; index < words.Count; index++)
            {
                double distance = Distance(words[index].Bounds, point);
                double center = CenterDistance(words[index].Bounds, point);
                float allowance = Math.Max(tolerance, Math.Min(32, Math.Max(anchorHeight, words[index].Bounds.Height) * 1.25f));
                if (distance > allowance) continue;
                if (distance < bestDistance || (Math.Abs(distance - bestDistance) < .001 && center < bestCenterDistance))
                { best = index; bestDistance = distance; bestCenterDistance = center; }
            }
            return best;
        }

        private static bool SameTextBlock(RectangleF first, RectangleF second)
        {
            float height = Math.Max(first.Height, second.Height);
            float verticalGap = Math.Max(0, Math.Max(first.Top, second.Top) - Math.Min(first.Bottom, second.Bottom));
            if (verticalGap > Math.Max(32, height * 2.5f)) return false;
            float horizontalGap = Math.Max(0, Math.Max(first.Left, second.Left) - Math.Min(first.Right, second.Right));
            // Nearby indents are fine; a separate column at the same vertical
            // position is not a continuation of the current highlight.
            if (horizontalGap > Math.Max(24, height * 1.5f)) return false;
            return true;
        }

        private static RectangleF Pad(RectangleF rect)
        {
            const float horizontal = 2f;
            const float vertical = 2f;
            return RectangleF.FromLTRB(Math.Max(0, rect.Left - horizontal), Math.Max(0, rect.Top - vertical),
                rect.Right + horizontal, rect.Bottom + vertical);
        }

        private static double Distance(RectangleF box, PointF point)
        {
            double x = Math.Max(box.Left - point.X, Math.Max(0, point.X - box.Right));
            double y = Math.Max(box.Top - point.Y, Math.Max(0, point.Y - box.Bottom));
            return Math.Sqrt(x * x + y * y);
        }

        private static double CenterDistance(RectangleF box, PointF point)
        {
            double x = box.Left + box.Width / 2 - point.X, y = box.Top + box.Height / 2 - point.Y;
            return x * x + y * y;
        }

        private static float CleanTolerance(float value) { return Single.IsNaN(value) || Single.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(32, value)); }
        private static bool Finite(PointF point) { return !Single.IsNaN(point.X) && !Single.IsInfinity(point.X) && !Single.IsNaN(point.Y) && !Single.IsInfinity(point.Y); }
        private static bool Valid(RectangleF box) { return Finite(box.Location) && Finite(new PointF(box.Right, box.Bottom)) && box.Width > 0 && box.Height > 0; }

        private sealed class WordPosition
        {
            internal int Line;
            internal RectangleF Bounds;
            internal RectangleF LineBounds;
        }
    }
}
