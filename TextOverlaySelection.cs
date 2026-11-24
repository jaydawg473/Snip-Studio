using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace SnipStudio
{
    // A snapshot of OCR word geometry. Selection changes never modify the image.
    public sealed class TextOverlaySelection
    {
        private sealed class Word
        {
            internal readonly string Text;
            internal readonly RectangleF Bounds;
            internal readonly int Line;
            internal readonly string SeparatorBefore;
            internal readonly TextWordFootprint Footprint;
            internal Word(TextWordRegion word, int line, string separatorBefore)
            {
                Text = word.Text;
                Bounds = word.Bounds;
                Footprint = word.Footprint;
                Line = line;
                SeparatorBefore = separatorBefore;
            }
        }

        private readonly List<Word> words = new List<Word>();
        private readonly Dictionary<int, RectangleF> lineBounds = new Dictionary<int, RectangleF>();
        private readonly HashSet<int> areaWords = new HashSet<int>();
        private readonly string allText;
        private readonly bool hasFootprints;
        private int first = -1, last = -1;
        public int Count { get { return words.Count; } }
        public bool HasSelection { get { return first >= 0 && last >= first && last < words.Count; } }
        public int SelectedLineIndex { get { return HasSelection && words[first].Line == words[last].Line ? words[first].Line : -1; } }
        public string AllText { get { return allText; } }
        public string SelectedText
        {
            get
            {
                if (!HasSelection) return "";
                StringBuilder value = new StringBuilder();
                int previous = -1;
                for (int i = first; i <= last; i++)
                {
                    if (!IsSelected(i)) continue;
                    Word word = words[i];
                    if (previous >= 0)
                    {
                        if (word.Line != words[previous].Line) value.Append(Environment.NewLine);
                        // Adjacent words retain their native separator. A space
                        // keeps disjoint words from becoming one identifier.
                        else value.Append(i == previous + 1 ? word.SeparatorBefore : " ");
                    }
                    value.Append(word.Text);
                    previous = i;
                }
                return value.ToString();
            }
        }

        public TextOverlaySelection(IList<TextLineRegion> lines)
        {
            List<string> textLines = new List<string>();
            if (lines != null)
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    TextLineRegion line = lines[lineIndex];
                    if (line == null) continue;
                    List<string> fallback = new List<string>();
                    string nativeText = line.Text;
                    bool haveNativePositions = !String.IsNullOrWhiteSpace(nativeText);
                    int nativeCursor = 0;
                    if (line.Words != null)
                        foreach (TextWordRegion word in line.Words)
                        {
                            if (word == null || String.IsNullOrWhiteSpace(word.Text) || !ValidBounds(word.Bounds)) continue;
                            string separator = fallback.Count == 0 ? "" : " ";
                            int nativeStart = haveNativePositions ? nativeText.IndexOf(word.Text, nativeCursor, StringComparison.Ordinal) : -1;
                            if (nativeStart >= 0)
                            {
                                // Copy selection must preserve the same native
                                // separators as Copy all. Inserting a guessed
                                // space changes adjacent codes, underscores,
                                // non-Latin words and meaningful whitespace.
                                if (fallback.Count > 0) separator = nativeText.Substring(nativeCursor, nativeStart - nativeCursor);
                                nativeCursor = nativeStart + word.Text.Length;
                            }
                            else haveNativePositions = false;
                            words.Add(new Word(word, lineIndex, separator));
                            hasFootprints |= word.Footprint != null;
                            RectangleF existing;
                            lineBounds[lineIndex] = lineBounds.TryGetValue(lineIndex, out existing) ? RectangleF.Union(existing, word.Bounds) : word.Bounds;
                            fallback.Add(word.Text);
                        }
                    string text = !String.IsNullOrWhiteSpace(line.Text) ? line.Text : String.Join(" ", fallback.ToArray());
                    if (!String.IsNullOrWhiteSpace(text)) textLines.Add(text);
                }
            allText = String.Join(Environment.NewLine, textLines.ToArray());
        }

        public void Select(int anchor, int end)
        {
            Clear();
            if (anchor < 0 || end < 0 || anchor >= words.Count || end >= words.Count) return;
            first = Math.Min(anchor, end);
            last = Math.Max(anchor, end);
        }

        // A drag begun on blank pixels selects only words touching its area.
        internal void SelectArea(PointF start, PointF end)
        {
            Clear();
            if (!Finite(start.X) || !Finite(start.Y) || !Finite(end.X) || !Finite(end.Y) || start == end) return;
            RectangleF area = RectangleF.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
            if (area.Width == 0) area.Inflate(.5f, 0);
            if (area.Height == 0) area.Inflate(0, .5f);
            for (int i = 0; i < words.Count; i++)
            {
                Word word = words[i];
                if (!area.IntersectsWith(word.Bounds) ||
                    (word.Footprint != null && !word.Footprint.Intersects(area))) continue;
                areaWords.Add(i);
                if (first < 0) first = i;
                last = i;
            }
        }

        public void Clear() { first = last = -1; areaWords.Clear(); }
        public bool TryGetSelectedCharacterRange(string lineText, out int start, out int length)
        {
            start = 0; length = 0;
            int line = SelectedLineIndex;
            if (line < 0 || lineText == null) return false;
            if (areaWords.Count > 0 && areaWords.Count != last - first + 1) return false;
            int cursor = 0;
            for (int i = 0; i <= last; i++)
            {
                if (words[i].Line != line) continue;
                int found = lineText.IndexOf(words[i].Text, cursor, StringComparison.Ordinal);
                if (found < 0) return false;
                if (i == first) start = found;
                cursor = found + words[i].Text.Length;
            }
            length = cursor - start;
            return length > 0;
        }
        public bool IsSelected(int index)
        {
            if (!HasSelection || index < first || index > last) return false;
            return areaWords.Count == 0 || areaWords.Contains(index);
        }
        public RectangleF WordBounds(int index) { return index < 0 || index >= words.Count ? RectangleF.Empty : words[index].Bounds; }

        public IList<RectangleF> SelectedLineBounds()
        {
            List<RectangleF> bands = new List<RectangleF>();
            if (!HasSelection) return bands;
            int currentLine = words[first].Line;
            RectangleF currentBounds = words[first].Bounds;
            for (int i = first + 1; i <= last; i++)
            {
                if (!IsSelected(i)) continue;
                if (words[i].Line == currentLine)
                    currentBounds = RectangleF.Union(currentBounds, words[i].Bounds);
                else
                {
                    bands.Add(currentBounds);
                    currentLine = words[i].Line;
                    currentBounds = words[i].Bounds;
                }
            }
            bands.Add(currentBounds);
            return bands;
        }

        internal IList<RectangleF> SelectedWordBounds()
        {
            var bands = new List<RectangleF>();
            if (!HasSelection) return bands;
            for (int index = first; index <= last; index++)
                if (IsSelected(index)) bands.Add(words[index].Bounds);
            return bands;
        }

        internal int HitTestNear(PointF point, float tolerance)
        {
            int nearest = HitTest(point, tolerance);
            return nearest >= 0 && WordDistanceSquared(point, words[nearest]) <= tolerance * tolerance ? nearest : -1;
        }

        public int HitTest(PointF point, float tolerance)
        {
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(tolerance)) return -1;
            tolerance = Math.Max(0, Math.Min(64, tolerance));
            for (int i = words.Count - 1; i >= 0; i--)
            {
                Word word = words[i];
                if (word.Bounds.Contains(point) && (word.Footprint == null || word.Footprint.Contains(point))) return i;
            }
            int nearest = -1;
            double best = Double.MaxValue;
            for (int i = 0; i < words.Count; i++)
            {
                Word word = words[i];
                RectangleF expanded = word.Bounds;
                expanded.Inflate(tolerance, tolerance);
                if (!expanded.Contains(point)) continue;
                double distance = WordDistanceSquared(point, word);
                if (word.Footprint != null && distance > tolerance * tolerance) continue;
                if (distance < best) { best = distance; nearest = i; }
            }
            return nearest;
        }

        public int Nearest(PointF point)
        {
            if (!Finite(point.X) || !Finite(point.Y)) return -1;
            if (hasFootprints) return NearestTiltedWord(point);
            int nearest = -1;
            double best = Double.MaxValue;
            double nearestLineDistance = Double.MaxValue;
            double nearestLineHorizontalDistance = Double.MaxValue;
            RectangleF nearestLine = RectangleF.Empty;
            foreach (RectangleF line in lineBounds.Values)
            {
                double vertical = VerticalDistance(point.Y, line);
                double horizontal = Math.Max(line.Left - point.X, Math.Max(0, point.X - line.Right));
                if (vertical < nearestLineDistance || (vertical == nearestLineDistance && horizontal < nearestLineHorizontalDistance))
                { nearestLineDistance = vertical; nearestLineHorizontalDistance = horizontal; nearestLine = line; }
            }
            for (int i = 0; i < words.Count; i++)
            {
                RectangleF line = lineBounds[words[i].Line];
                // Descenders can make a neighboring date cell a few pixels
                // taller than the filename on the same row. Keep nearby row
                // glyphs eligible before choosing the closest actual word;
                // otherwise a drag below a short glyph jumps to that column.
                double rowTolerance = Math.Max(2, Math.Min(8, line.Height * .35));
                double vertical = VerticalDistance(point.Y, line);
                if (vertical > nearestLineDistance)
                {
                    float overlap = Math.Min(line.Bottom, nearestLine.Bottom) - Math.Max(line.Top, nearestLine.Top);
                    if (vertical > nearestLineDistance + rowTolerance || overlap < Math.Min(line.Height, nearestLine.Height) * .5f) continue;
                }
                double distance = DistanceSquared(point, words[i].Bounds);
                if (distance < best) { best = distance; nearest = i; }
            }
            return nearest;
        }

        private int NearestTiltedWord(PointF point)
        {
            int nearest = -1;
            double best = Double.PositiveInfinity;
            for (int i = 0; i < words.Count; i++)
            {
                Word word = words[i];
                if (DistanceSquared(point, word.Bounds) >= best) continue;
                double distance = WordDistanceSquared(point, word);
                if (distance < best) { best = distance; nearest = i; }
            }
            return nearest;
        }

        private static double WordDistanceSquared(PointF point, Word word)
        {
            return word.Footprint == null ? DistanceSquared(point, word.Bounds) : word.Footprint.DistanceSquared(point);
        }

        private static double VerticalDistance(float y, RectangleF bounds)
        {
            return Math.Max(bounds.Top - y, Math.Max(0, y - bounds.Bottom));
        }

        private static double DistanceSquared(PointF point, RectangleF bounds)
        {
            double dx = Math.Max(bounds.Left - point.X, Math.Max(0, point.X - bounds.Right));
            double dy = Math.Max(bounds.Top - point.Y, Math.Max(0, point.Y - bounds.Bottom));
            return dx * dx + dy * dy;
        }

        private static bool ValidBounds(RectangleF bounds)
        {
            return bounds.Width > 0 && bounds.Height > 0 && Finite(bounds.Left) && Finite(bounds.Top) &&
                Finite(bounds.Right) && Finite(bounds.Bottom);
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
    }

    // The visible part of a deskewed word, projected back into source pixels.
    // A convex footprint avoids selecting a different tilted word through an
    // empty corner of its enclosing rectangle.
    internal sealed class TextWordFootprint
    {
        private static readonly TextWordFootprint Empty = new TextWordFootprint(Array.Empty<PointF>());
        private readonly PointF[] vertices;

        private TextWordFootprint(PointF[] vertices) { this.vertices = vertices; }

        internal static TextWordFootprint Create(PointF first, PointF second, PointF third, PointF fourth, RectangleF source)
        {
            if (!(Cross(first, second, fourth) > 0)) return Empty;
            var corners = new[] { first, second, third, fourth };
            if (Array.TrueForAll(corners, point =>
                point.X >= source.Left && point.X <= source.Right && point.Y >= source.Top && point.Y <= source.Bottom))
                return new TextWordFootprint(corners);

            // Clip once so the same footprint governs clicks, proximity and
            // drag selection. A clipped-out word has no selectable geometry.
            List<PointF> clipped = Clip(corners, source.Left, horizontal: true, keepGreater: true);
            clipped = Clip(clipped, source.Right, horizontal: true, keepGreater: false);
            clipped = Clip(clipped, source.Top, horizontal: false, keepGreater: true);
            clipped = Clip(clipped, source.Bottom, horizontal: false, keepGreater: false);
            if (clipped.Count < 3) return Empty;
            double area = 0;
            for (int i = 2; i < clipped.Count; i++) area += Cross(clipped[0], clipped[i - 1], clipped[i]);
            return area > 0 ? new TextWordFootprint(clipped.ToArray()) : Empty;
        }

        private static List<PointF> Clip(IList<PointF> polygon, float edge, bool horizontal, bool keepGreater)
        {
            var result = new List<PointF>(polygon.Count + 1);
            if (polygon.Count == 0) return result;
            PointF previous = polygon[polygon.Count - 1];
            double before = (horizontal ? previous.X : previous.Y) - edge;
            foreach (PointF point in polygon)
            {
                double after = (horizontal ? point.X : point.Y) - edge;
                bool beforeInside = keepGreater ? before >= 0 : before <= 0;
                bool afterInside = keepGreater ? after >= 0 : after <= 0;
                if (beforeInside != afterInside)
                {
                    double fraction = before / (before - after);
                    var boundary = horizontal
                        ? new PointF(edge, (float)(previous.Y + (point.Y - previous.Y) * fraction))
                        : new PointF((float)(previous.X + (point.X - previous.X) * fraction), edge);
                    result.Add(boundary);
                }
                if (afterInside) result.Add(point);
                previous = point;
                before = after;
            }
            return result;
        }

        internal bool Contains(PointF point)
        {
            if (vertices.Length < 3) return false;
            for (int i = 0; i < vertices.Length; i++)
                if (Cross(vertices[i], Next(i), point) < 0) return false;
            return true;
        }

        internal bool Intersects(RectangleF area)
        {
            // The caller has already checked the horizontal and vertical
            // axes with the bounding boxes. Check the polygon's own edges.
            if (vertices.Length < 3) return false;
            PointF topLeft = area.Location, topRight = new PointF(area.Right, area.Top);
            PointF bottomLeft = new PointF(area.Left, area.Bottom), bottomRight = new PointF(area.Right, area.Bottom);
            for (int i = 0; i < vertices.Length; i++)
            {
                PointF a = vertices[i], b = Next(i);
                if (a != b && Cross(a, b, topLeft) <= 0 && Cross(a, b, topRight) <= 0 &&
                    Cross(a, b, bottomLeft) <= 0 && Cross(a, b, bottomRight) <= 0) return false;
            }
            return true;
        }

        internal double DistanceSquared(PointF point)
        {
            if (Contains(point)) return 0;
            double nearest = Double.PositiveInfinity;
            for (int i = 0; i < vertices.Length; i++)
            {
                PointF a = vertices[i], b = Next(i);
                double dx = b.X - a.X, dy = b.Y - a.Y, x = point.X - a.X, y = point.Y - a.Y;
                double lengthSquared = dx * dx + dy * dy;
                double fraction = lengthSquared == 0 ? 0 :
                    Math.Max(0, Math.Min(1, (x * dx + y * dy) / lengthSquared));
                x -= dx * fraction;
                y -= dy * fraction;
                nearest = Math.Min(nearest, x * x + y * y);
            }
            return nearest;
        }

        private PointF Next(int index)
        {
            return vertices[(index + 1) % vertices.Length];
        }

        private static double Cross(PointF a, PointF b, PointF point)
        {
            return ((double)b.X - a.X) * ((double)point.Y - a.Y) - ((double)b.Y - a.Y) * ((double)point.X - a.X);
        }
    }
}
