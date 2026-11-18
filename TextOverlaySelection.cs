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
            internal Word(string text, RectangleF bounds, int line, string separatorBefore)
            { Text = text; Bounds = bounds; Line = line; SeparatorBefore = separatorBefore; }
        }

        private readonly List<Word> words = new List<Word>();
        private readonly Dictionary<int, RectangleF> lineBounds = new Dictionary<int, RectangleF>();
        private readonly string allText;
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
                for (int i = first; i <= last; i++)
                {
                    if (i > first) value.Append(words[i].Line == words[i - 1].Line ? words[i].SeparatorBefore : Environment.NewLine);
                    value.Append(words[i].Text);
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
                            words.Add(new Word(word.Text, word.Bounds, lineIndex, separator));
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
            if (anchor < 0 || end < 0 || anchor >= words.Count || end >= words.Count) { Clear(); return; }
            first = Math.Min(anchor, end);
            last = Math.Max(anchor, end);
        }

        public void Clear() { first = last = -1; }
        public bool TryGetSelectedCharacterRange(string lineText, out int start, out int length)
        {
            start = 0; length = 0;
            int line = SelectedLineIndex;
            if (line < 0 || lineText == null) return false;
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
        public bool IsSelected(int index) { return HasSelection && index >= first && index <= last; }
        public RectangleF WordBounds(int index) { return index < 0 || index >= words.Count ? RectangleF.Empty : words[index].Bounds; }

        public IList<RectangleF> SelectedLineBounds()
        {
            List<RectangleF> bands = new List<RectangleF>();
            if (!HasSelection) return bands;
            int currentLine = words[first].Line;
            RectangleF currentBounds = words[first].Bounds;
            for (int i = first + 1; i <= last; i++)
            {
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

        public int HitTest(PointF point, float tolerance)
        {
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(tolerance)) return -1;
            tolerance = Math.Max(0, Math.Min(64, tolerance));
            for (int i = words.Count - 1; i >= 0; i--)
                if (words[i].Bounds.Contains(point)) return i;
            int nearest = -1;
            double best = Double.MaxValue;
            for (int i = 0; i < words.Count; i++)
            {
                RectangleF expanded = words[i].Bounds;
                expanded.Inflate(tolerance, tolerance);
                if (!expanded.Contains(point)) continue;
                double distance = DistanceSquared(point, words[i].Bounds);
                if (distance < best) { best = distance; nearest = i; }
            }
            return nearest;
        }

        public int Nearest(PointF point)
        {
            if (!Finite(point.X) || !Finite(point.Y)) return -1;
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
}
