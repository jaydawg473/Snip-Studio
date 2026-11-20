using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipStudio
{
    // Native OCR can read a folded-paper icon as "Z]" and include it in the
    // text that would be erased/re-rendered. Require either a distinct folded
    // document outline or a proven graphical column. Ambiguous prefixes stay text.
    internal static class RecognizedTextRegions
    {
        private const int MaximumAnchors = 256;
        private const int MaximumRasterChecks = 512;

        internal static void Refine(TextRecognitionResult result, Bitmap source)
        {
            if (result == null) throw new ArgumentNullException("result");
            if (source == null) throw new ArgumentNullException("source");
            if (result.RawText == null) result.RawText = result.Text ?? "";
            if (!result.Available || result.Lines.Count == 0 || result.Lines.Count > 4096 ||
                (result.TextAngle.HasValue && (Double.IsNaN(result.TextAngle.Value) ||
                    Double.IsInfinity(result.TextAngle.Value) || Math.Abs(result.TextAngle.Value) > 2))) return;

            List<TextLineRegion> regions = SplitColumns(result.Lines);
            bool changed = regions.Count != result.Lines.Count;
            List<Rectangle> anchors = new List<Rectangle>();
            using (Pixels pixels = new Pixels(source))
            {
                int examined = 0;
                foreach (TextLineRegion line in regions)
                {
                    if (anchors.Count >= MaximumAnchors || examined >= MaximumRasterChecks) break;
                    if (line == null || line.Words.Count == 0) continue;
                    RectangleF first = line.Words[0].Bounds;
                    float height = MedianHeight(line.Words, 0);
                    if (height < 5 || height > 96 || first.Width < height * 2.5f) continue;
                    int pad = Math.Max(2, (int)Math.Ceiling(height * .4f));
                    Rectangle probe = Rectangle.FromLTRB((int)Math.Floor(first.Left - height * 3.5f),
                        (int)Math.Floor(first.Top) - pad, (int)Math.Floor(first.Left),
                        (int)Math.Ceiling(first.Bottom) + pad);
                    examined++;
                    Evidence evidence = pixels.Inspect(probe);
                    Rectangle graphic = evidence.ColorBounds;
                    if ((evidence.Multicolor || evidence.ChromaticPaperContour) && graphic.Width >= height * .6f && graphic.Width <= height * 2 &&
                        graphic.Height >= height * .75f && graphic.Height <= height * 2 &&
                        graphic.Right < first.Left && first.Left - graphic.Right <= height * 1.8f)
                    {
                        anchors.Add(graphic);
                        ProtectGraphic(result, line, graphic);
                    }
                }

                // A PDF-only list may have no multicolor application icon, and
                // Windows OCR may read every red paper icon as "@". Those rows
                // cannot establish the old omitted-icon anchors. A compact
                // chromatic paper contour supplies independent raster evidence;
                // two separate rows must still prove the graphics column.
                examined = 0;
                foreach (TextLineRegion line in regions)
                {
                    if (line == null || line.Words.Count == 0 || examined >= MaximumRasterChecks ||
                        anchors.Count >= MaximumAnchors) continue;
                    TextWordRegion word = line.Words[0];
                    if (String.IsNullOrEmpty(word.Text) || word.Text.Length > 3) continue;
                    RectangleF bounds = word.Bounds;
                    float height = line.Words.Count > 1 ? MedianHeight(line.Words, 1) : bounds.Height / 1.4f;
                    if (!CompactPrefix(line, height, false)) continue;
                    examined++;
                    Evidence evidence = pixels.Inspect(Rectangle.Ceiling(bounds));
                    if (evidence.ChromaticPaperContour) anchors.Add(Rectangle.Ceiling(bounds));
                }

                examined = 0;
                foreach (TextLineRegion line in regions)
                {
                    if (line == null || line.Words.Count == 0 || examined >= MaximumRasterChecks) continue;
                    TextWordRegion word = line.Words[0];
                    float height = line.Words.Count > 1 ? MedianHeight(line.Words, 1) : word.Bounds.Height / 1.4f;
                    RectangleF bounds = word.Bounds;
                    Evidence evidence = null;
                    bool document = false;
                    if (PotentialDocumentPrefix(line))
                    {
                        examined++;
                        Rectangle probe = Rectangle.FromLTRB((int)Math.Floor(bounds.Left) - 2, (int)Math.Floor(bounds.Top) - 2,
                            (int)Math.Ceiling(bounds.Right) + 2, (int)Math.Ceiling(bounds.Bottom) + 2);
                        if (line.Words.Count > 1) probe.Width = Math.Min(probe.Right, (int)Math.Floor(line.Words[1].Bounds.Left)) - probe.Left;
                        Evidence expanded = pixels.Inspect(probe);
                        if (expanded.FoldedDocument && expanded.ColorBounds.IntersectsWith(Rectangle.Ceiling(bounds)))
                        { document = true; evidence = expanded; }
                    }
                    // Keep the existing proven-column route even when this
                    // token is ineligible for the independent outline check.
                    if (!document)
                    {
                        if (!CompactPrefix(line, height, true) || !ProvenColumn(bounds, anchors, height)) continue;
                        examined++;
                        evidence = pixels.Inspect(Rectangle.Ceiling(bounds));
                    }
                    bool symbols = false;
                    foreach (char character in word.Text)
                        if (!Char.IsLetterOrDigit(character) && !Char.IsWhiteSpace(character)) symbols = true;
                    // A monochrome N, part code or CJK character is not removed.
                    // Neutral folded-paper geometry also needs a punctuation-like
                    // OCR token; chromatic artwork supplies its own raster proof.
                    if (!document && !evidence.Multicolor && !evidence.ChromaticBoxContour && (!symbols || !evidence.PaperContour)) continue;
                    RectangleF graphic = document ? RectangleF.Union(bounds, evidence.ColorBounds) : bounds;
                    if (line.Words.Count == 1)
                    {
                        if (!document && !evidence.ChromaticBoxContour) continue;
                        ProtectGraphic(result, line, graphic);
                        line.Words.Clear();
                        line.Text = "";
                        line.Bounds = RectangleF.Empty;
                        changed = true;
                        continue;
                    }
                    string retained;
                    if (!TrySlice(line, 1, line.Words.Count, out retained)) continue;
                    ProtectGraphic(result, line, graphic);
                    line.Words.RemoveAt(0);
                    line.Text = retained;
                    RecomputeBounds(line);
                    changed = true;
                }
            }
            if (!changed) return;
            regions.RemoveAll(delegate(TextLineRegion line) { return line != null && line.Words.Count == 0; });
            result.Lines.Clear();
            result.Lines.AddRange(regions);
            List<string> text = new List<string>();
            foreach (TextLineRegion line in regions) text.Add(line.Text ?? "");
            result.Text = String.Join(Environment.NewLine, text.ToArray());
        }

        private static bool CompactPrefix(TextLineRegion line, float height, bool requireTall)
        {
            TextWordRegion word = line.Words[0];
            RectangleF bounds = word.Bounds;
            if (String.IsNullOrEmpty(word.Text) || word.Text.Length > 3 || height < 5 || height > 96 ||
                bounds.Height < height * (requireTall ? 1.15f : 1.05f) || bounds.Height > height * 2.3f ||
                bounds.Width > bounds.Height * 1.8f) return false;
            // A compact non-Latin identifier can itself have a rectangular
            // contour. Keep it ambiguous rather than treating it as artwork.
            foreach (char character in word.Text) if (character > 127) return false;
            if (line.Words.Count == 1) return true;
            RectangleF next = line.Words[1].Bounds;
            return next.Left > bounds.Right && next.Left - bounds.Right <= height * 2 && bounds.Top <= next.Top;
        }

        private static bool PotentialDocumentPrefix(TextLineRegion line)
        {
            TextWordRegion first = line.Words[0];
            RectangleF bounds = first.Bounds;
            if (String.IsNullOrEmpty(first.Text) || first.Text.Length > 3 ||
                bounds.Height < 7 || bounds.Height > 96 || bounds.Width < 4 || bounds.Width > bounds.Height * 1.8f ||
                Single.IsNaN(bounds.Left) || Single.IsInfinity(bounds.Left) ||
                Single.IsNaN(bounds.Top) || Single.IsInfinity(bounds.Top) ||
                Single.IsNaN(bounds.Width) || Single.IsNaN(bounds.Height)) return false;
            foreach (char character in first.Text) if (character > 127) return false;
            return line.Words.Count == 1 || line.Words[1].Bounds.Left >= bounds.Right;
        }

        private static void ProtectGraphic(TextRecognitionResult result, TextLineRegion line, RectangleF graphic)
        {
            line.ExcludedGraphicBounds = line.ExcludedGraphicBounds.IsEmpty ? graphic :
                RectangleF.Union(line.ExcludedGraphicBounds, graphic);
            if (result.ProtectedGraphics.Count < MaximumRasterChecks && !result.ProtectedGraphics.Contains(graphic))
                result.ProtectedGraphics.Add(graphic);
        }

        private static bool ProvenColumn(RectangleF candidate, List<Rectangle> anchors, float textHeight)
        {
            float tolerance = Math.Max(3, textHeight * .4f);
            for (int first = 0; first < anchors.Count; first++)
            {
                Rectangle anchor = anchors[first];
                if (Math.Abs(candidate.Left - anchor.Left) > tolerance ||
                    Math.Abs(candidate.Right - anchor.Right) > tolerance ||
                    Math.Abs(candidate.Height - anchor.Height) > Math.Max(4, textHeight * .5f)) continue;
                for (int second = first + 1; second < anchors.Count; second++)
                {
                    Rectangle other = anchors[second];
                    if (Math.Abs(anchor.Left - other.Left) <= tolerance && Math.Abs(anchor.Right - other.Right) <= tolerance &&
                        Math.Abs(anchor.Top - other.Top) >= Math.Min(anchor.Height, other.Height)) return true;
                }
            }
            return false;
        }

        private static List<TextLineRegion> SplitColumns(IList<TextLineRegion> lines)
        {
            List<TextLineRegion> result = new List<TextLineRegion>();
            foreach (TextLineRegion line in lines)
            {
                if (line == null || line.Words.Count < 2) { result.Add(line); continue; }
                float threshold = Math.Max(48, MedianHeight(line.Words, 0) * 6);
                List<int> breaks = new List<int>();
                for (int index = 1; index < line.Words.Count; index++)
                {
                    RectangleF left = line.Words[index - 1].Bounds, right = line.Words[index].Bounds;
                    // Clear horizontal gutters only. Preserve ordinary spaces,
                    // source order, mixed-script spacing and overlapping words.
                    if (right.Left - left.Right >= threshold &&
                        Math.Min(left.Bottom, right.Bottom) > Math.Max(left.Top, right.Top)) breaks.Add(index);
                }
                if (breaks.Count == 0) { result.Add(line); continue; }
                breaks.Add(line.Words.Count);
                List<TextLineRegion> fragments = new List<TextLineRegion>();
                int start = 0;
                foreach (int end in breaks)
                {
                    string text;
                    if (!TrySlice(line, start, end, out text)) { fragments.Clear(); break; }
                    TextLineRegion fragment = new TextLineRegion();
                    fragment.Text = text;
                    fragment.RecognitionClipBounds = line.RecognitionClipBounds;
                    if (start == 0) fragment.ExcludedGraphicBounds = line.ExcludedGraphicBounds;
                    for (int index = start; index < end; index++) fragment.Words.Add(line.Words[index]);
                    RecomputeBounds(fragment);
                    fragments.Add(fragment);
                    start = end;
                }
                if (fragments.Count == 0) result.Add(line); else result.AddRange(fragments);
            }
            return result;
        }

        private static bool TrySlice(TextLineRegion line, int start, int end, out string text)
        {
            text = null;
            string original = line.Text ?? "";
            int cursor = 0, first = -1, last = -1;
            for (int index = 0; index < end; index++)
            {
                string token = line.Words[index].Text;
                if (String.IsNullOrEmpty(token)) return false;
                int found = original.IndexOf(token, cursor, StringComparison.Ordinal);
                if (found < 0) return false;
                if (index == start) first = found;
                cursor = found + token.Length;
                if (index == end - 1) last = cursor;
            }
            if (first < 0 || last < first) return false;
            text = original.Substring(first, last - first);
            return true;
        }

        private static float MedianHeight(IList<TextWordRegion> words, int start)
        {
            List<float> heights = new List<float>();
            for (int index = start; index < words.Count; index++)
                if (words[index] != null && words[index].Bounds.Height > 0) heights.Add(words[index].Bounds.Height);
            if (heights.Count == 0) return 0;
            heights.Sort();
            return heights[heights.Count / 2];
        }

        private static void RecomputeBounds(TextLineRegion line)
        {
            RectangleF bounds = RectangleF.Empty;
            foreach (TextWordRegion word in line.Words)
                bounds = bounds.IsEmpty ? word.Bounds : RectangleF.Union(bounds, word.Bounds);
            line.Bounds = bounds;
        }

        private sealed class Evidence
        {
            internal Rectangle ColorBounds;
            internal bool Multicolor, PaperContour, ChromaticPaperContour, ChromaticBoxContour, FoldedDocument;
        }

        private sealed class Pixels : IDisposable
        {
            private readonly Bitmap bitmap;
            private readonly BitmapData data;
            private readonly byte[] row = new byte[1024];
            internal Pixels(Bitmap source)
            {
                bitmap = source;
                data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            }
            internal Evidence Inspect(Rectangle requested)
            {
                Evidence result = new Evidence();
                Rectangle area = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), requested);
                if (area.Width < 2 || area.Height < 2 || area.Width > 256 || area.Height > 256) return result;
                int[] hues = new int[12], columns = new int[area.Width], rows = new int[area.Height];
                int[] colorColumns = new int[area.Width], colorRows = new int[area.Height], colorRowRight = new int[area.Height];
                int firstX = area.Right, firstY = area.Bottom, lastX = area.Left - 1, lastY = area.Top - 1, saturated = 0;
                int background = Background(area);
                int br = (background >> 16) & 255, bg = (background >> 8) & 255, bb = background & 255;
                for (int y = 0; y < area.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked((area.Top + y) * data.Stride + area.Left * 4)), row, 0, area.Width * 4);
                    for (int x = 0; x < area.Width; x++)
                    {
                        int at = x * 4, r = row[at + 2], g = row[at + 1], b = row[at];
                        if (row[at + 3] < 128) continue;
                        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        // Dark teal/red app artwork is chromatic even when its
                        // absolute RGB range is small. A relative saturation
                        // requirement rejects the pale selected-row background.
                        if (max >= 48 && max - min >= 32 && (max - min) * 100 >= max * 40)
                        {
                            int hue = Math.Min(11, (int)(Color.FromArgb(r, g, b).GetHue() / 30));
                            hues[hue]++; saturated++;
                            colorColumns[x]++; colorRows[y]++; colorRowRight[y] = x;
                            firstX = Math.Min(firstX, area.Left + x); lastX = Math.Max(lastX, area.Left + x);
                            firstY = Math.Min(firstY, area.Top + y); lastY = Math.Max(lastY, area.Top + y);
                        }
                        int distance = Math.Abs(r - br) + Math.Abs(g - bg) + Math.Abs(b - bb);
                        if (max - min <= 48 && distance >= 90) { columns[x]++; rows[y]++; }
                    }
                }
                if (lastX >= firstX && lastY >= firstY)
                {
                    result.ColorBounds = Rectangle.FromLTRB(firstX, firstY, lastX + 1, lastY + 1);
                    int minimum = Math.Max(4, (int)Math.Ceiling(saturated * .12));
                    for (int first = 0; first < hues.Length; first++)
                        for (int second = first + 1; second < hues.Length; second++)
                        {
                            int separation = Math.Min(second - first, 12 - second + first);
                            if (separation >= 3 && hues[first] >= minimum && hues[second] >= minimum &&
                                saturated >= 12 && saturated >= result.ColorBounds.Width * result.ColorBounds.Height * .3)
                                result.Multicolor = true;
                        }
                }
                int left = -1, right = -1, top = -1, bottom = -1;
                for (int x = 0; x < columns.Length; x++)
                    if (columns[x] >= area.Height * .6) { if (left < 0) left = x; right = x; }
                for (int y = 0; y < rows.Length; y++)
                    if (rows[y] >= area.Width * .55) { if (top < 0) top = y; bottom = y; }
                result.PaperContour = right - left >= area.Width * .55 && bottom - top >= area.Height * .55;
                if (!result.ColorBounds.IsEmpty)
                {
                    Rectangle ink = result.ColorBounds;
                    int inkLeft = ink.Left - area.Left, inkRight = ink.Right - area.Left - 1;
                    int inkTop = ink.Top - area.Top, inkBottom = ink.Bottom - area.Top - 1;
                    // Long outer edges distinguish folded-paper artwork from
                    // a colored glyph or ClearType fringe. Require all four
                    // edges; density/color alone must not delete a real @.
                    result.ChromaticBoxContour = ink.Width >= 7 && ink.Height >= 9 &&
                        ink.Height >= ink.Width * .65 && ink.Height <= ink.Width * 2 &&
                        colorColumns[inkLeft] >= ink.Height * .7 && colorColumns[inkRight] >= ink.Height * .6 &&
                        colorRows[inkTop] >= ink.Width * .6 && colorRows[inkBottom] >= ink.Width * .6;
                    result.ChromaticPaperContour = result.ChromaticBoxContour && ink.Height >= ink.Width * 1.05;
                    // A one-off colored token is only suppressed when it has
                    // the full rectangular sheet edges, the cut-away upper
                    // corner and colored marks well inside the sheet. A red @,
                    // an outline letter and ordinary punctuation do not qualify.
                    int foldDepth = Math.Max(2, Math.Min(5, ink.Width / 3));
                    int foldRow = Math.Min(inkBottom, inkTop + foldDepth);
                    result.FoldedDocument = result.ChromaticPaperContour && ink.Width >= 9 && ink.Height >= 12 &&
                        colorRowRight[inkTop] <= inkRight - 2 && colorRowRight[inkBottom] >= inkRight - 1 &&
                        colorRowRight[foldRow] >= inkRight - 1 && HasInteriorMark(ink);
                }
                return result;
            }

            private bool HasInteriorMark(Rectangle ink)
            {
                int coloredRows = 0;
                for (int y = ink.Top + 3; y < ink.Bottom - 3; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride + ink.Left * 4)), row, 0, ink.Width * 4);
                    for (int x = 3; x < ink.Width - 3; x++)
                    {
                        int at = x * 4, r = row[at + 2], g = row[at + 1], b = row[at];
                        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        if (row[at + 3] < 128 || max < 48 || max - min < 32 || (max - min) * 100 < max * 40) continue;
                        if (++coloredRows >= 3) return true;
                        break;
                    }
                }
                return false;
            }
            private int Background(Rectangle area)
            {
                Dictionary<int, int> votes = new Dictionary<int, int>();
                int winner = 0, best = 0;
                foreach (Point point in new Point[] { new Point(area.Left - 1, area.Top - 1), new Point(area.Right, area.Top - 1),
                    new Point(area.Left - 1, area.Bottom), new Point(area.Right, area.Bottom) })
                {
                    int x = Math.Max(0, Math.Min(bitmap.Width - 1, point.X)), y = Math.Max(0, Math.Min(bitmap.Height - 1, point.Y));
                    int color = Marshal.ReadInt32(IntPtr.Add(data.Scan0, checked(y * data.Stride + x * 4))) & 0xffffff;
                    int count; votes.TryGetValue(color, out count); votes[color] = ++count;
                    if (count > best) { best = count; winner = color; }
                }
                return winner;
            }
            public void Dispose() { bitmap.UnlockBits(data); }
        }
    }
}
