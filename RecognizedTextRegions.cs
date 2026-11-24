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
        private const int MaximumJoinedPrefixes = 8;

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
                changed |= SplitInteriorGraphics(result, regions, pixels);
                changed |= ExcludeUiIcons(result, regions, pixels);
            }
            if (!changed) return;
            regions.RemoveAll(delegate(TextLineRegion line) { return line != null && line.Words.Count == 0; });
            result.Lines.Clear();
            result.Lines.AddRange(regions);
            List<string> text = new List<string>();
            foreach (TextLineRegion line in regions) text.Add(line.Text ?? "");
            result.Text = String.Join(Environment.NewLine, text.ToArray());
        }

        internal sealed class JoinedGraphicPrefix
        {
            internal readonly TextLineRegion Line;
            internal readonly TextWordRegion Word;
            internal readonly Rectangle CropBounds;
            internal readonly int TextLeft;

            internal JoinedGraphicPrefix(TextLineRegion line, TextWordRegion word, Rectangle cropBounds, int textLeft)
            {
                Line = line;
                Word = word;
                CropBounds = cropBounds;
                TextLeft = textLeft;
            }
        }

        // A native word can cover both the icon and the neighboring filename.
        // Isolate the filename pixels for a second read. A guessed split based
        // on character width could strip real punctuation from the filename.
        internal static List<JoinedGraphicPrefix> FindJoinedGraphicPrefixes(TextRecognitionResult result, Bitmap source)
        {
            var prefixes = new List<JoinedGraphicPrefix>();
            if (!result.Available || result.Lines.Count > 4096 ||
                (result.TextAngle.HasValue && (Double.IsNaN(result.TextAngle.Value) ||
                    Double.IsInfinity(result.TextAngle.Value) || Math.Abs(result.TextAngle.Value) > 2))) return prefixes;

            Rectangle sourceBounds = new Rectangle(Point.Empty, source.Size);
            using (var pixels = new Pixels(source))
            {
                int examined = 0;
                foreach (TextLineRegion line in result.Lines)
                {
                    if (prefixes.Count == MaximumJoinedPrefixes || examined == MaximumRasterChecks) break;
                    if (line == null || line.Words.Count == 0) continue;
                    TextWordRegion word = line.Words[0];
                    RectangleF bounds = word.Bounds;
                    if (String.IsNullOrEmpty(word.Text) || word.Text.Length < 4 || word.Text.Length > 256 ||
                        !((RectangleF)sourceBounds).Contains(bounds) || bounds.Height < 12 || bounds.Height > 72 ||
                        bounds.Width < bounds.Height * 1.6f) continue;

                    Rectangle wordPixels = Rectangle.Intersect(sourceBounds, Enclose(bounds));
                    int probeRight = Math.Min(wordPixels.Right, wordPixels.Left + (int)Math.Ceiling(bounds.Height * 1.65));
                    Rectangle probe = Rectangle.Intersect(sourceBounds, Rectangle.FromLTRB(
                        wordPixels.Left - 2, wordPixels.Top - 2, probeRight, wordPixels.Bottom + 2));
                    Rectangle clip = line.RecognitionClipBounds.IsEmpty ? sourceBounds : Enclose(line.RecognitionClipBounds);
                    probe = Rectangle.Intersect(probe, clip);
                    examined++;

                    Rectangle graphic;
                    int nextInk;
                    if (!pixels.TrySeparateLeadingGraphic(probe, out graphic, out nextInk) ||
                        graphic.Height < bounds.Height * .65f || graphic.Left < bounds.Left - 2 ||
                        graphic.Left > bounds.Left + 4 || nextInk >= wordPixels.Right - 3) continue;
                    int right = Math.Min(clip.Right, wordPixels.Right + 2);
                    if (line.Words.Count > 1) right = Math.Min(right, (int)Math.Floor(line.Words[1].Bounds.Left));
                    Rectangle crop = Rectangle.Intersect(sourceBounds, Rectangle.FromLTRB(
                        Math.Max(clip.Left, nextInk - 1), probe.Top, right, probe.Bottom));
                    if (crop.Width < 4 || crop.Height < 8 || crop.IntersectsWith(graphic)) continue;

                    ProtectGraphic(result, line, graphic);
                    prefixes.Add(new JoinedGraphicPrefix(line, word, crop, nextInk));
                }
            }
            return prefixes;
        }

        internal static bool ApplyJoinedGraphicPrefix(TextRecognitionResult result, JoinedGraphicPrefix prefix,
            TextRecognitionResult reread)
        {
            TextLineRegion line = prefix.Line;
            TextWordRegion original = prefix.Word;
            if (!result.Lines.Contains(line) || line.Words.Count == 0 || line.Words[0] != original ||
                !reread.Available || reread.Lines.Count != 1 || reread.Lines[0].Words.Count != 1) return false;
            TextWordRegion recovered = reread.Lines[0].Words[0];
            int removed = String.IsNullOrEmpty(recovered.Text) ? 0 : original.Text.Length - recovered.Text.Length;
            float tolerance = Math.Max(3, original.Bounds.Height * .28f);
            if (removed < 1 || removed > 3 || recovered.Text != reread.Lines[0].Text ||
                !original.Text.EndsWith(recovered.Text, StringComparison.Ordinal) ||
                recovered.Bounds.Width <= 0 || recovered.Bounds.Height <= 0 ||
                !((RectangleF)prefix.CropBounds).Contains(recovered.Bounds) ||
                Math.Abs(recovered.Bounds.Left - prefix.TextLeft) > tolerance ||
                Math.Abs(recovered.Bounds.Right - original.Bounds.Right) > tolerance) return false;
            for (int index = 0; index < removed; index++)
                if (original.Text[index] > 127 || Char.IsWhiteSpace(original.Text[index])) return false;

            int start = (line.Text ?? "").IndexOf(original.Text, StringComparison.Ordinal);
            if (start < 0 || !String.IsNullOrWhiteSpace(line.Text.Substring(0, start))) return false;
            line.Text = line.Text.Remove(start, removed);
            line.Words[0] = new TextWordRegion { Bounds = recovered.Bounds, Text = recovered.Text };
            RecomputeBounds(line);
            var text = new List<string>();
            foreach (TextLineRegion row in result.Lines) text.Add(row.Text ?? "");
            result.Text = String.Join(Environment.NewLine, text.ToArray());
            return true;
        }

        private static Rectangle Enclose(RectangleF bounds)
        {
            return Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
        }

        private static bool SplitInteriorGraphics(TextRecognitionResult result, List<TextLineRegion> regions, Pixels pixels)
        {
            int examined = 0;
            bool changed = false;
            for (int lineIndex = 0; lineIndex < regions.Count && examined < MaximumRasterChecks; lineIndex++)
            {
                TextLineRegion line = regions[lineIndex];
                if (line == null || line.Words.Count < 2) continue;
                var graphics = new List<RectangleF>();
                var fragments = new List<TextLineRegion>();
                int start = 0;
                for (int index = 1; index < line.Words.Count && examined < MaximumRasterChecks; index++)
                {
                    RectangleF graphic;
                    if (!TryReadInteriorGraphic(line, index, pixels, ref examined, out graphic)) continue;
                    // Split around an icon so the later rectangular erase
                    // mask never spans it. Other source tokens stay verbatim.
                    if (start < index)
                    {
                        TextLineRegion fragment = CreateFragment(line, start, index);
                        if (fragment == null) { graphics.Clear(); break; }
                        fragments.Add(fragment);
                    }
                    graphics.Add(graphic);
                    start = index + 1;
                }
                if (graphics.Count == 0) continue;
                if (start < line.Words.Count)
                {
                    TextLineRegion fragment = CreateFragment(line, start, line.Words.Count);
                    if (fragment == null) continue;
                    fragments.Add(fragment);
                }
                if (graphics.Count > MaximumRasterChecks - result.ProtectedGraphics.Count) break;
                foreach (RectangleF graphic in graphics) RememberGraphic(result, graphic);
                regions.RemoveAt(lineIndex);
                regions.InsertRange(lineIndex, fragments);
                lineIndex += fragments.Count - 1;
                changed = true;
            }
            return changed;
        }

        private static bool TryReadInteriorGraphic(TextLineRegion line, int index, Pixels pixels,
            ref int examined, out RectangleF graphic)
        {
            graphic = RectangleF.Empty;
            TextWordRegion word = line.Words[index];
            if (!PotentialDocumentToken(word)) return false;
            RectangleF bounds = word.Bounds;
            RectangleF previous = line.Words[index - 1].Bounds;
            // Leave tightly attached tokens intact. The two-pixel gutter also
            // keeps the raster query from including either neighboring word.
            if (bounds.Left - previous.Right < 2) return false;
            int left = Math.Max((int)Math.Floor(bounds.Left) - 2, (int)Math.Ceiling(previous.Right));
            int right = (int)Math.Ceiling(bounds.Right) + 2;
            if (index + 1 < line.Words.Count)
            {
                RectangleF next = line.Words[index + 1].Bounds;
                if (next.Left - bounds.Right < 2) return false;
                right = Math.Min(right, (int)Math.Floor(next.Left));
            }
            examined++;
            Evidence evidence = pixels.Inspect(Rectangle.FromLTRB(left, (int)Math.Floor(bounds.Top) - 2,
                right, (int)Math.Ceiling(bounds.Bottom) + 2));
            if (!evidence.FoldedDocument || !evidence.ColorBounds.IntersectsWith(Rectangle.Ceiling(bounds))) return false;
            graphic = RectangleF.Union(bounds, evidence.ColorBounds);
            return true;
        }

        private static bool ExcludeUiIcons(TextRecognitionResult result, List<TextLineRegion> regions, Pixels pixels)
        {
            // A short OCR token can coincide with a UI icon. Require independent
            // raster evidence before removing it, even for tokens like "+".
            var pairedBadges = new List<Rectangle>();
            int probes = 0;
            foreach (TextLineRegion line in regions)
            {
                if (probes >= MaximumRasterChecks || pairedBadges.Count >= MaximumAnchors) break;
                if (line == null || line.Words.Count < 2 || line.Words[0].Text != "+") continue;
                RectangleF token = line.Words[0].Bounds;
                RectangleF next = line.Words[1].Bounds;
                if (!PotentialDocumentToken(line.Words[0]) || next.Left - token.Right < 4 ||
                    next.Left - token.Left > 60) continue;
                probes++;
                Rectangle area = Rectangle.FromLTRB((int)Math.Floor(token.Left) - 26,
                    (int)Math.Floor(token.Top) - 5, (int)Math.Floor(next.Left),
                    (int)Math.Ceiling(token.Bottom) + 5);
                Rectangle left, right;
                if (!pixels.TryReadPairedBadges(area, out left, out right) ||
                    (!OverlapsIcon(token, left) && !OverlapsIcon(token, right))) continue;
                pairedBadges.Add(left);
                pairedBadges.Add(right);
            }

            bool changed = false;
            probes = 0;
            for (int lineIndex = 0; lineIndex < regions.Count && probes < MaximumRasterChecks; lineIndex++)
            {
                TextLineRegion line = regions[lineIndex];
                if (line == null) continue;
                int start = 0;
                var icons = new List<RectangleF>();
                var fragments = new List<TextLineRegion>();
                for (int index = 0; index < line.Words.Count && probes < MaximumRasterChecks; index++)
                {
                    TextWordRegion word = line.Words[index];
                    bool roundToken = word.Text == "O" || word.Text == "o" || word.Text == "0";
                    if ((!roundToken && word.Text != "+") || !PotentialDocumentToken(word)) continue;
                    probes++;
                    RectangleF box = word.Bounds;
                    Rectangle ink = pairedBadges.Find(candidate => OverlapsIcon(box, candidate));
                    if (ink.IsEmpty && roundToken)
                    {
                        Rectangle crop = Enclose(box);
                        crop.Inflate(3, 3);
                        if (index > 0)
                            crop = Rectangle.Intersect(crop, Rectangle.FromLTRB(
                                (int)Math.Ceiling(line.Words[index - 1].Bounds.Right), crop.Top, crop.Right, crop.Bottom));
                        if (index + 1 < line.Words.Count)
                            crop = Rectangle.Intersect(crop, Rectangle.FromLTRB(crop.Left, crop.Top,
                                (int)Math.Floor(line.Words[index + 1].Bounds.Left), crop.Bottom));
                        bool mayBeEditIcon = line.Words.Count == 1 && HasNeighboringTextRow(box, regions);
                        if (!pixels.TryReadNeutralIcon(crop, mayBeEditIcon, out ink) || !OverlapsIcon(box, ink)) continue;
                    }
                    if (ink.IsEmpty) continue;
                    if (start < index)
                    {
                        TextLineRegion before = CreateFragment(line, start, index);
                        if (before == null) { icons.Clear(); break; }
                        fragments.Add(before);
                    }
                    icons.Add(RectangleF.Union(box, ink));
                    start = index + 1;
                }
                if (icons.Count == 0 || icons.Count > MaximumRasterChecks - result.ProtectedGraphics.Count) continue;
                if (start < line.Words.Count)
                {
                    TextLineRegion after = CreateFragment(line, start, line.Words.Count);
                    if (after == null) continue;
                    fragments.Add(after);
                }
                foreach (RectangleF icon in icons) RememberGraphic(result, icon);
                regions.RemoveAt(lineIndex);
                regions.InsertRange(lineIndex, fragments);
                lineIndex += fragments.Count - 1;
                changed = true;
            }
            return changed;
        }

        private static bool OverlapsIcon(RectangleF token, Rectangle icon)
        {
            RectangleF overlap = RectangleF.Intersect(token, icon);
            return overlap.Width * overlap.Height >= token.Width * token.Height * .55f &&
                icon.Contains((int)(token.Left + token.Width / 2), (int)(token.Top + token.Height / 2));
        }

        private static bool HasNeighboringTextRow(RectangleF icon, List<TextLineRegion> regions)
        {
            foreach (TextLineRegion row in regions)
            {
                if (row == null || row.Words.Count < 2) continue;
                RectangleF first = row.Words[0].Bounds;
                float gap = first.Left - icon.Right;
                if (gap >= 4 && gap <= 24 &&
                    Math.Abs(first.Top + first.Height / 2 - icon.Top - icon.Height / 2) <= 4) return true;
            }
            return false;
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
            return PotentialDocumentToken(first) &&
                (line.Words.Count == 1 || line.Words[1].Bounds.Left >= first.Bounds.Right);
        }

        private static bool PotentialDocumentToken(TextWordRegion word)
        {
            RectangleF bounds = word.Bounds;
            if (String.IsNullOrEmpty(word.Text) || word.Text.Length > 3 ||
                bounds.Height < 7 || bounds.Height > 96 || bounds.Width < 4 || bounds.Width > bounds.Height * 1.8f ||
                Single.IsNaN(bounds.Left) || Single.IsInfinity(bounds.Left) ||
                Single.IsNaN(bounds.Top) || Single.IsInfinity(bounds.Top) ||
                Single.IsNaN(bounds.Width) || Single.IsNaN(bounds.Height)) return false;
            foreach (char character in word.Text) if (character > 127) return false;
            return true;
        }

        private static void ProtectGraphic(TextRecognitionResult result, TextLineRegion line, RectangleF graphic)
        {
            line.ExcludedGraphicBounds = line.ExcludedGraphicBounds.IsEmpty ? graphic :
                RectangleF.Union(line.ExcludedGraphicBounds, graphic);
            RememberGraphic(result, graphic);
        }

        private static void RememberGraphic(TextRecognitionResult result, RectangleF graphic)
        {
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
                    TextLineRegion fragment = CreateFragment(line, start, end);
                    if (fragment == null) { fragments.Clear(); break; }
                    fragments.Add(fragment);
                    start = end;
                }
                if (fragments.Count == 0) result.Add(line); else result.AddRange(fragments);
            }
            return result;
        }

        private static TextLineRegion CreateFragment(TextLineRegion line, int start, int end)
        {
            string text;
            if (!TrySlice(line, start, end, out text)) return null;
            TextLineRegion fragment = new TextLineRegion { Text = text, RecognitionClipBounds = line.RecognitionClipBounds };
            if (start == 0) fragment.ExcludedGraphicBounds = line.ExcludedGraphicBounds;
            for (int index = start; index < end; index++) fragment.Words.Add(line.Words[index]);
            RecomputeBounds(fragment);
            return fragment;
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

            internal bool TryReadNeutralIcon(Rectangle requested, bool allowEditIcon, out Rectangle graphic)
            {
                graphic = Rectangle.Empty;
                Rectangle area = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), requested);
                if (area.Width < 8 || area.Height < 8 || area.Width > 38 || area.Height > 38) return false;
                int background = Background(area);
                int br = (background >> 16) & 255, bg = (background >> 8) & 255, bb = background & 255;
                var ink = new bool[area.Width * area.Height];
                Rectangle outline = Rectangle.Empty;
                for (int y = 0; y < area.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked((area.Top + y) * data.Stride + area.Left * 4)), row, 0, area.Width * 4);
                    for (int x = 0; x < area.Width; x++)
                    {
                        int i = x * 4, r = row[i + 2], g = row[i + 1], b = row[i];
                        if (row[i + 3] < 128) continue;
                        int distance = Math.Abs(r - br) + Math.Abs(g - bg) + Math.Abs(b - bb);
                        if (distance < 90) continue;
                        if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) return false;
                        ink[y * area.Width + x] = true;
                        outline = IncludePixel(outline, x, y);
                    }
                }
                if (outline.Height < 8 || outline.Width < 8 || outline.Width < outline.Height * .75 ||
                    outline.Width > outline.Height * 1.35) return false;

                // A copy outline has a rectangular front opening and an
                // offset, L-shaped rear opening. O/0 have a single opening.
                // Two openings stacked vertically (8/B) are also insufficient.
                List<Opening> holes = EnclosedOpenings(ink, area.Width, area.Height);
                if (holes.Count != 2) return false;
                if (!IsCopyOutline(holes[0], holes[1], outline) &&
                    (!allowEditIcon || !IsEditOutline(holes[0], holes[1], outline))) return false;
                graphic = new Rectangle(area.Left + outline.Left, area.Top + outline.Top, outline.Width, outline.Height);
                return true;
            }

            private static bool IsCopyOutline(Opening rear, Opening front, Rectangle outline)
            {
                Rectangle a = rear.Bounds, b2 = front.Bounds;
                if (a.Width < 3 || a.Height < 3 || b2.Width < 3 || b2.Height < 3 ||
                    b2.Width < b2.Height * .65 || b2.Width > b2.Height * 1.55 ||
                    a.Width < b2.Width * .65 || a.Width > b2.Width * 1.5 ||
                    a.Height < b2.Height * .65 || a.Height > b2.Height * 1.5) return false;
                int dx = a.Left - b2.Left, dy = b2.Top - a.Top;
                return dx >= 2 && dy >= 2 && dx <= outline.Width * .5 && dy <= outline.Height * .5 &&
                    a.IntersectsWith(b2) && rear.Fill >= .28 && rear.Fill <= .7 && front.Fill >= .85;
            }

            private static bool IsEditOutline(Opening cap, Opening barrel, Rectangle outline)
            {
                // The slanted pencil has a short enclosed cap at the upper
                // right and a larger, elongated diagonal opening in its stem.
                Rectangle tip = cap.Bounds, stem = barrel.Bounds;
                return tip.Width >= 2 && tip.Height >= 2 && tip.Width <= outline.Width * .5 &&
                    tip.Height <= outline.Height * .5 && stem.Width >= 4 && stem.Height >= 4 &&
                    cap.Area <= barrel.Area * .32 && tip.Left >= stem.Right - 2 &&
                    tip.Bottom <= stem.Top + 2 && barrel.Fill >= .36 && barrel.Fill <= .78 &&
                    barrel.Correlation <= -.55;
            }

            private readonly struct Opening
            {
                internal readonly Rectangle Bounds;
                internal readonly double Fill;
                internal readonly int Area;
                internal readonly double Correlation;
                internal Opening(Rectangle bounds, int area, double correlation)
                {
                    Bounds = bounds;
                    Area = area;
                    Fill = (double)area / (bounds.Width * bounds.Height);
                    Correlation = correlation;
                }
            }

            private static List<Opening> EnclosedOpenings(bool[] ink, int width, int height)
            {
                var result = new List<Opening>();
                var visited = new bool[ink.Length];
                var queue = new int[ink.Length];
                for (int start = 0; start < ink.Length; start++)
                {
                    if (ink[start] || visited[start]) continue;
                    int count = 1;
                    queue[0] = start;
                    visited[start] = true;
                    bool outside = false;
                    Rectangle bounds = Rectangle.Empty;
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                    for (int at = 0; at < count; at++)
                    {
                        int p = queue[at], x = p % width, y = p / width;
                        outside |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                        bounds = IncludePixel(bounds, x, y);
                        sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                        if (x > 0) Visit(p - 1);
                        if (x + 1 < width) Visit(p + 1);
                        if (y > 0) Visit(p - width);
                        if (y + 1 < height) Visit(p + width);
                    }
                    if (!outside && count >= 3)
                    {
                        double variance = (sxx - sx * sx / count) * (syy - sy * sy / count);
                        double correlation = variance > 0 ? (sxy - sx * sy / count) / Math.Sqrt(variance) : 0;
                        result.Add(new Opening(bounds, count, correlation));
                        if (result.Count > 2) break;
                    }

                    void Visit(int neighbor)
                    {
                        if (ink[neighbor] || visited[neighbor]) return;
                        visited[neighbor] = true;
                        queue[count++] = neighbor;
                    }
                }
                return result;
            }

            internal bool TryReadPairedBadges(Rectangle requested, out Rectangle first, out Rectangle second)
            {
                first = second = Rectangle.Empty;
                Rectangle area = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), requested);
                if (area.Width < 22 || area.Width > 68 || area.Height < 9 || area.Height > 40) return false;
                int background = Background(area);
                int br = (background >> 16) & 255, bg = (background >> 8) & 255, bb = background & 255;
                var colored = new int[area.Width * area.Height];
                var columns = new bool[area.Width];
                for (int y = 0; y < area.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked((area.Top + y) * data.Stride + area.Left * 4)), row, 0, area.Width * 4);
                    for (int x = 0; x < area.Width; x++)
                    {
                        int i = x * 4, r = row[i + 2], g = row[i + 1], b = row[i];
                        int high = Math.Max(r, Math.Max(g, b)), low = Math.Min(r, Math.Min(g, b));
                        if (row[i + 3] < 128 || high - low < 42 ||
                            Math.Abs(r - br) + Math.Abs(g - bg) + Math.Abs(b - bb) < 105) continue;
                        colored[y * area.Width + x] = (int)Color.FromArgb(r, g, b).GetHue() + 1;
                        columns[x] = true;
                    }
                }
                int left = Array.IndexOf(columns, true);
                if (left < 0) return false;
                int leftEnd = Array.IndexOf(columns, false, left);
                if (leftEnd < 0) return false;
                int right = Array.IndexOf(columns, true, leftEnd);
                if (right < 0 || right - leftEnd < 2 || right - leftEnd > 14) return false;
                int rightEnd = Array.IndexOf(columns, false, right);
                if (rightEnd < 0) rightEnd = columns.Length;
                if (rightEnd < columns.Length && Array.IndexOf(columns, true, rightEnd) >= 0) return false;
                Rectangle a, b2;
                int hueA, hueB;
                if (!TryBadgeShape(colored, area.Width, area.Height, left, leftEnd, out a, out hueA) ||
                    !TryBadgeShape(colored, area.Width, area.Height, right, rightEnd, out b2, out hueB)) return false;
                int hueDistance = Math.Abs(hueA - hueB);
                if (Math.Min(hueDistance, 360 - hueDistance) < 85 || Math.Abs(a.Top - b2.Top) > 4 ||
                    Math.Abs(a.Height - b2.Height) > 4) return false;
                a.Offset(area.Location);
                b2.Offset(area.Location);
                first = a;
                second = b2;
                return true;
            }

            private static bool TryBadgeShape(int[] colored, int stride, int height, int left, int right,
                out Rectangle bounds, out int hue)
            {
                bounds = Rectangle.Empty;
                hue = 0;
                if (right - left < 8 || right - left > 25) return false;
                int count = 0;
                var hues = new int[12];
                for (int y = 0; y < height; y++)
                    for (int x = left; x < right; x++)
                    {
                        int color = colored[y * stride + x];
                        if (color == 0) continue;
                        count++;
                        hues[Math.Min(11, (color - 1) / 30)]++;
                        bounds = IncludePixel(bounds, x, y);
                    }
                if (bounds.Height < 8 || bounds.Height > 25 || bounds.Width < bounds.Height * .65 ||
                    bounds.Width > bounds.Height * 1.55 || count < bounds.Width * bounds.Height * .37) return false;
                int best = 0;
                for (int at = 1; at < hues.Length; at++)
                    if (hues[at] > hues[best]) best = at;
                hue = best * 30 + 15;

                // Real + glyphs contain the horizontal and vertical bars alone.
                // Both independently detected badges need dense diagonal arms.
                int xEdge = (int)Math.Ceiling(bounds.Width * .3), yEdge = (int)Math.Ceiling(bounds.Height * .3);
                int[] corners = new int[4];
                for (int y = bounds.Top; y < bounds.Bottom; y++)
                    for (int x = bounds.Left; x < bounds.Right; x++)
                    {
                        if (colored[y * stride + x] == 0) continue;
                        int cx = x < bounds.Left + xEdge ? 0 : x >= bounds.Right - xEdge ? 1 : -1;
                        int cy = y < bounds.Top + yEdge ? 0 : y >= bounds.Bottom - yEdge ? 2 : -1;
                        if (cx >= 0 && cy >= 0) corners[cx + cy]++;
                    }
                return Array.TrueForAll(corners, value => value >= 2);
            }

            private static Rectangle IncludePixel(Rectangle bounds, int x, int y)
            {
                return bounds.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.FromLTRB(
                    Math.Min(bounds.Left, x), Math.Min(bounds.Top, y),
                    Math.Max(bounds.Right, x + 1), Math.Max(bounds.Bottom, y + 1));
            }

            internal bool TrySeparateLeadingGraphic(Rectangle area, out Rectangle graphic, out int nextInk)
            {
                graphic = Rectangle.Empty;
                nextInk = 0;
                if (area.Width < 12 || area.Width > 128 || area.Height < 12 || area.Height > 80) return false;
                int background = Background(area);
                int br = (background >> 16) & 255, bg = (background >> 8) & 255, bb = background & 255;
                bool[] ink = new bool[area.Width];
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride + area.Left * 4)), row, 0, area.Width * 4);
                    for (int x = 0; x < ink.Length; x++)
                    {
                        if (ink[x]) continue;
                        int at = x * 4;
                        int distance = Math.Abs(row[at + 2] - br) + Math.Abs(row[at + 1] - bg) + Math.Abs(row[at] - bb);
                        if (row[at + 3] >= 128 && distance >= 65) ink[x] = true;
                    }
                }
                int first = Array.IndexOf(ink, true);
                if (first < 0 || first > 6) return false;
                int end = first;
                while (end < ink.Length && ink[end]) end++;
                if (end - first < 9 || end + 2 >= ink.Length || ink[end + 1]) return false;
                int next = Array.IndexOf(ink, true, end + 2);
                if (next < 0 || next - end > area.Height * .85f) return false;
                Evidence evidence = Inspect(Rectangle.FromLTRB(area.Left + first, area.Top,
                    area.Left + end, area.Bottom));
                if (!evidence.FoldedDocument) return false;
                graphic = evidence.ColorBounds;
                nextInk = area.Left + next;
                return true;
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
