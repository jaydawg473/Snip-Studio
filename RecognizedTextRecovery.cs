using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace SnipStudio
{
    // Recovery is geometric. It can restore a missed native OCR run, but must
    // not vote between two different readings of an already complete code.
    internal static class RecognizedTextRecovery
    {
        internal static int MergeMissing(TextRecognitionResult primary, TextRecognitionResult candidate,
            Rectangle sourceBounds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (primary == null || candidate == null || !primary.Available || !candidate.Available ||
                !Horizontal(primary.TextAngle) || !Horizontal(candidate.TextAngle) ||
                primary.Lines.Count > 4096 || candidate.Lines.Count > 512) return 0;
            foreach (TextLineRegion existing in primary.Lines)
                if (existing == null || !ValidLine(existing, sourceBounds)) return 0;
            List<TextLineRegion> planned = new List<TextLineRegion>(primary.Lines);
            List<TextLineRegion> missing = new List<TextLineRegion>();
            int accepted = 0;
            foreach (TextLineRegion line in candidate.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (!ValidLine(line, sourceBounds)) continue;
                int overlap = -1, overlaps = 0;
                for (int index = 0; index < planned.Count; index++)
                {
                    TextLineRegion previous = planned[index];
                    if (previous == null || !previous.Bounds.IntersectsWith(line.Bounds)) continue;
                    overlap = index;
                    if (++overlaps > 1) break;
                }
                if (overlaps > 1) continue;
                if (overlaps == 1)
                {
                    RectangleF previous = planned[overlap].Bounds;
                    RectangleF tolerance = line.Bounds;
                    tolerance.Inflate(2, 2);
                    double oldArea = previous.Width * previous.Height;
                    double newArea = line.Bounds.Width * line.Bounds.Height;
                    float height = Math.Max(previous.Height, line.Bounds.Height);
                    float extension = Math.Max(previous.Left - line.Bounds.Left, line.Bounds.Right - previous.Right);
                    // Only a short contained fragment can yield to a complete
                    // run. Equal-size conflicting identifiers stay as read by
                    // the initial pass, regardless of candidate text length.
                    if (!tolerance.Contains(previous) || newArea < oldArea * 2 ||
                        extension < height * 3 || previous.Width > line.Bounds.Width * .55f || previous.Width > height * 4 ||
                        Math.Abs(previous.Top + previous.Height / 2 - line.Bounds.Top - line.Bounds.Height / 2) > height * .45f)
                        continue;
                    TextLineRegion restored = Clone(line);
                    RectangleF priorGraphic = planned[overlap].ExcludedGraphicBounds;
                    if (!priorGraphic.IsEmpty) restored.ExcludedGraphicBounds = restored.ExcludedGraphicBounds.IsEmpty ?
                        priorGraphic : RectangleF.Union(priorGraphic, restored.ExcludedGraphicBounds);
                    RectangleF priorClip = planned[overlap].RecognitionClipBounds;
                    if (!priorClip.IsEmpty) restored.RecognitionClipBounds = restored.RecognitionClipBounds.IsEmpty ?
                        priorClip : RectangleF.Intersect(priorClip, restored.RecognitionClipBounds);
                    if ((!priorClip.IsEmpty || !line.RecognitionClipBounds.IsEmpty) &&
                        (restored.RecognitionClipBounds.IsEmpty || !restored.RecognitionClipBounds.Contains(restored.Bounds))) continue;
                    planned[overlap] = restored;
                }
                else
                {
                    missing.Add(Clone(line));
                    continue;
                }
                accepted++;
            }
            // Fill contained fragments first. A short first filename may not
            // horizontally overlap the suffix of a longer later row, but it
            // does align with that row's recovered complete run. Deferring new
            // rows until those anchors exist preserves column reading order
            // without widening column matching into neighboring fields.
            foreach (TextLineRegion line in missing)
            {
                token.ThrowIfCancellationRequested();
                bool overlaps = false;
                foreach (TextLineRegion existing in planned)
                    if (existing.Bounds.IntersectsWith(line.Bounds)) { overlaps = true; break; }
                if (overlaps) continue;
                planned.Insert(InsertionIndex(planned, line), line);
                accepted++;
            }
            token.ThrowIfCancellationRequested();
            if (accepted == 0) return 0;
            // Preserve the provider's existing reading order. Interleaving a
            // filename column with adjacent date/type cells changes drag-text
            // selection, even though every individual rectangle is correct.
            List<string> text = new List<string>();
            primary.Lines.Clear();
            foreach (TextLineRegion line in planned)
            { primary.Lines.Add(line); text.Add(line.Text ?? ""); }
            primary.Text = String.Join(Environment.NewLine, text.ToArray());
            return accepted;
        }

        private static int InsertionIndex(List<TextLineRegion> existing, TextLineRegion added)
        {
            int next = -1, previous = -1;
            float nextDistance = Single.MaxValue, previousDistance = Single.MaxValue;
            RectangleF bounds = added.Bounds;
            for (int index = 0; index < existing.Count; index++)
            {
                RectangleF other = existing[index].Bounds;
                float overlap = Math.Min(bounds.Right, other.Right) - Math.Max(bounds.Left, other.Left);
                if (overlap < Math.Min(bounds.Width, other.Width) * .5f) continue;
                float distance = other.Top - bounds.Top;
                if (distance > 0 && distance < nextDistance) { next = index; nextDistance = distance; }
                else if (distance < 0 && -distance < previousDistance) { previous = index; previousDistance = -distance; }
            }
            if (next >= 0) return next;
            return previous >= 0 ? previous + 1 : existing.Count;
        }

        internal static Rectangle IconFreeColumnCrop(TextRecognitionResult native, Size size)
        {
            if (native == null || !native.Available || !Horizontal(native.TextAngle)) return Rectangle.Empty;
            foreach (TextLineRegion anchor in native.Lines)
            {
                RectangleF graphic = anchor.ExcludedGraphicBounds;
                if (graphic.Width < 5 || graphic.Height < 5 || graphic.Right >= size.Width - 64) continue;
                List<float> rows = new List<float>();
                foreach (TextLineRegion line in native.Lines)
                {
                    RectangleF other = line.ExcludedGraphicBounds;
                    if (other.IsEmpty || Math.Abs(other.Left - graphic.Left) > 3 || Math.Abs(other.Right - graphic.Right) > 3) continue;
                    bool distinct = true;
                    foreach (float row in rows) if (Math.Abs(row - other.Top) < Math.Min(graphic.Height, other.Height) * .7f) { distinct = false; break; }
                    if (distinct) rows.Add(other.Top);
                }
                if (rows.Count < 3) continue;
                int left = Math.Max(0, (int)Math.Ceiling(graphic.Right));
                return new Rectangle(left, 0, size.Width - left, size.Height);
            }
            return Rectangle.Empty;
        }

        private static bool Horizontal(double? angle)
        { return !angle.HasValue || (!Double.IsNaN(angle.Value) && !Double.IsInfinity(angle.Value) && Math.Abs(angle.Value) <= 2); }

        private static bool ValidLine(TextLineRegion line, Rectangle source)
        {
            if (line == null || String.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0 || line.Words.Count > 256 ||
                !Finite(line.Bounds) || line.Bounds.Height < 3 || !((RectangleF)source).Contains(line.Bounds)) return false;
            if (!line.RecognitionClipBounds.IsEmpty && (!Finite(line.RecognitionClipBounds) ||
                !((RectangleF)source).Contains(line.RecognitionClipBounds) || !line.RecognitionClipBounds.Contains(line.Bounds))) return false;
            foreach (TextWordRegion word in line.Words)
                if (word == null || String.IsNullOrWhiteSpace(word.Text) || !Finite(word.Bounds) ||
                    !((RectangleF)source).Contains(word.Bounds) || !line.Bounds.Contains(word.Bounds)) return false;
            return true;
        }
        private static bool Finite(RectangleF bounds)
        { return bounds.Width > 0 && bounds.Height > 0 && !Single.IsNaN(bounds.Left) && !Single.IsNaN(bounds.Top) &&
            !Single.IsInfinity(bounds.Left) && !Single.IsInfinity(bounds.Top) && !Single.IsInfinity(bounds.Right) && !Single.IsInfinity(bounds.Bottom); }

        private static TextLineRegion Clone(TextLineRegion source)
        {
            TextLineRegion copy = new TextLineRegion { Text = source.Text, Bounds = source.Bounds,
                ExcludedGraphicBounds = source.ExcludedGraphicBounds, RecognitionClipBounds = source.RecognitionClipBounds };
            foreach (TextWordRegion word in source.Words) copy.Words.Add(new TextWordRegion { Text = word.Text, Bounds = word.Bounds });
            return copy;
        }
    }
}
