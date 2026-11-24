using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace SnipStudio
{
    // Physical row and column evidence decides where a separate corroborated
    // read may be used. The provider's original output remains available.
    internal static class RecognizedTextRecovery
    {
        private const int MaximumSparseColumnRuns = 160;
        private const int MaximumSparseReviewCells = 12;
        private const int MaximumFilenameExtensionChecks = 16;

        internal sealed class InteriorRowRecovery
        {
            internal readonly int FirstRunIndex;
            internal readonly int RunCount;
            internal readonly int WordSlot;
            internal readonly Rectangle NarrowCrop;
            internal readonly Rectangle WideCrop;

            internal InteriorRowRecovery(int firstRunIndex, int runCount, int wordSlot, Rectangle narrowCrop, Rectangle wideCrop)
            {
                FirstRunIndex = firstRunIndex;
                RunCount = runCount;
                WordSlot = wordSlot;
                NarrowCrop = narrowCrop;
                WideCrop = wideCrop;
            }
        }

        internal static InteriorRowRecovery FindInteriorRowRecovery(TextRecognitionResult primary, Size size,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (primary == null || !primary.Available || !Horizontal(primary.TextAngle) ||
                primary.WasResized || primary.ProtectedGraphics.Count != 0 || primary.Lines.Count < 8 ||
                primary.Lines.Count > MaximumSparseColumnRuns || (long)size.Width * size.Height > 2_000_000) return null;

            var source = new Rectangle(Point.Empty, size);
            foreach (TextLineRegion row in primary.Lines)
                if (!ValidLine(row, source) || !row.ExcludedGraphicBounds.IsEmpty || !row.RecognitionClipBounds.IsEmpty)
                    return null;

            int split = -1, runCount = 2, wordSlot = -1;
            for (int index = 1; index < primary.Lines.Count; index++)
            {
                RectangleF first = primary.Lines[index - 1].Bounds, second = primary.Lines[index].Bounds;
                float height = Math.Min(first.Height, second.Height);
                if (Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top) < height * .5f) continue;
                if (split >= 0 || second.Left - first.Right < Math.Max(48, height * 4)) return null;
                split = index - 1;
                wordSlot = primary.Lines[split].Words.Count;
            }
            if (split < 0)
            {
                // A wide hole can remain inside one line after deskewed boxes
                // are mapped back to the visible source. The intact rows still
                // have to corroborate exactly one missing interior position.
                runCount = 1;
                int maximumWords = 0;
                foreach (TextLineRegion row in primary.Lines) maximumWords = Math.Max(maximumWords, row.Words.Count);
                for (int index = 0; index < primary.Lines.Count; index++)
                {
                    TextLineRegion row = primary.Lines[index];
                    if (row.Words.Count != maximumWords - 1) continue;
                    for (int word = 1; word < row.Words.Count; word++)
                    {
                        RectangleF before = row.Words[word - 1].Bounds, after = row.Words[word].Bounds;
                        if (after.Left - before.Right < Math.Max(48, Math.Min(before.Height, after.Height) * 4)) continue;
                        if (split >= 0) return null;
                        split = index;
                        wordSlot = word;
                    }
                }
                if (split < 0) return null;
            }

            TextLineRegion left = primary.Lines[split], right = primary.Lines[split + runCount - 1];
            int fullWords = left.Words.Count + (runCount == 2 ? right.Words.Count : 0) + 1;
            if (fullWords > 16) return null;
            float gapStart = left.Words[wordSlot - 1].Bounds.Right;
            float gapEnd = right.Words[runCount == 2 ? 0 : wordSlot].Bounds.Left;
            RectangleF combined = RectangleF.Union(left.Bounds, right.Bounds);
            float tolerance = Math.Max(8, combined.Height * .6f), extent = combined.Right;
            var body = new List<TextLineRegion>();
            var heights = new List<float>();
            for (int index = 0; index < primary.Lines.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (index == split) { body.Add(new TextLineRegion { Bounds = combined }); continue; }
                if (runCount == 2 && index == split + 1) continue;
                TextLineRegion row = primary.Lines[index];
                if (row.Words.Count != fullWords || Math.Abs(row.Bounds.Left - combined.Left) > tolerance ||
                    Math.Abs(row.Bounds.Right - combined.Right) > tolerance) return null;
                TextWordRegion anchor = row.Words[wordSlot];
                float middle = anchor.Bounds.Left + anchor.Bounds.Width / 2;
                if (middle <= gapStart || middle >= gapEnd) return null;
                heights.Add(row.Words[0].Bounds.Height);
                extent = Math.Max(extent, row.Bounds.Right);
                body.Add(row);
            }
            heights.Sort();
            float cadence = RegularCadence(body, heights[heights.Count / 2]);
            if (cadence <= 0) return null;
            for (int index = 1; index < body.Count; index++)
                if (Math.Abs(Middle(body[index].Bounds) - Middle(body[index - 1].Bounds) - cadence) >
                    Math.Max(6, cadence * .3f)) return null;

            // Keep generous blank padding in both rereads. Every removed pixel
            // is checked before recognition, including pixels without OCR bounds.
            const int widthStep = 32;
            int padding = Math.Max(24, (int)Math.Ceiling(combined.Height * 2));
            int narrow = ((int)Math.Ceiling(extent) + padding + widthStep - 1) / widthStep * widthStep;
            int wide = narrow + Math.Max(widthStep * 2, (padding + widthStep - 1) / widthStep * widthStep);
            if (size.Width - wide < padding) return null;
            return new InteriorRowRecovery(split, runCount, wordSlot, new Rectangle(0, 0, narrow, size.Height),
                new Rectangle(0, 0, wide, size.Height));
        }

        internal static int MergeInteriorRowRecovery(TextRecognitionResult primary, TextRecognitionResult narrow,
            TextRecognitionResult wide, InteriorRowRecovery plan, Func<RectangleF, bool> hasSourceInk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (plan == null || plan.NarrowCrop.Width >= plan.WideCrop.Width) return 0;
            TextLineRegion replacement = ValidateInteriorRead(primary, narrow, plan, plan.NarrowCrop, hasSourceInk, token);
            if (replacement == null || ValidateInteriorRead(primary, wide, plan, plan.WideCrop, hasSourceInk, token) == null) return 0;
            for (int index = 0; index < narrow.Lines.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!SameInteriorRow(narrow.Lines[index], wide.Lines[index], nearExact: true)) return 0;
            }

            token.ThrowIfCancellationRequested();
            int split = plan.FirstRunIndex;
            TextLineRegion reviewed = Clone(replacement);
            reviewed.RecognitionClipBounds = plan.NarrowCrop;
            primary.Lines[split] = reviewed;
            if (plan.RunCount == 2) primary.Lines.RemoveAt(split + 1);
            var text = new List<string>(primary.Lines.Count);
            foreach (TextLineRegion row in primary.Lines) text.Add(row.Text);
            primary.Text = String.Join(Environment.NewLine, text);
            return 1;
        }

        internal static bool TryChooseInteriorRetry(TextRecognitionResult primary, TextRecognitionResult narrow,
            TextRecognitionResult wide, InteriorRowRecovery plan, Func<RectangleF, bool> hasSourceInk,
            CancellationToken token, out bool keepWide)
        {
            token.ThrowIfCancellationRequested();
            keepWide = false;
            if (plan == null) return false;
            bool narrowValid = ValidateInteriorRead(primary, narrow, plan, plan.NarrowCrop, hasSourceInk, token) != null;
            bool wideValid = ValidateInteriorRead(primary, wide, plan, plan.WideCrop, hasSourceInk, token) != null;
            if (narrowValid == wideValid) return false;
            TextRecognitionResult incomplete = wideValid ? narrow : wide, complete = wideValid ? wide : narrow;
            Rectangle incompleteCrop = wideValid ? plan.NarrowCrop : plan.WideCrop;
            if (incomplete == null || !incomplete.Available || !Horizontal(incomplete.TextAngle) ||
                incomplete.ProtectedGraphics.Count != 0 || incomplete.Lines.Count != complete.Lines.Count) return false;

            // Treat the incomplete read as an abstention only when it omits
            // the target and at most one other anchored word. It cannot vote
            // against a value, move an anchor, or lose a whole source row.
            const int maximumOmissions = 2;
            bool targetOmitted = false;
            int omissions = 0;
            for (int index = 0; index < incomplete.Lines.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                TextLineRegion observed = incomplete.Lines[index], reference = complete.Lines[index];
                if (!ValidLine(observed, incompleteCrop) || !observed.ExcludedGraphicBounds.IsEmpty) return false;
                if (SameInteriorRow(reference, observed, nearExact: false)) continue;
                if (++omissions > maximumOmissions) return false;
                int omitted = UniqueOmittedInteriorWord(reference, observed);
                if (omitted < 0) return false;
                if (index == plan.FirstRunIndex)
                {
                    if (omitted != plan.WordSlot) return false;
                    targetOmitted = true;
                }
            }
            keepWide = targetOmitted && wideValid;
            return targetOmitted;
        }

        private static int UniqueOmittedInteriorWord(TextLineRegion full, TextLineRegion partial)
        {
            if (full.Words.Count != partial.Words.Count + 1) return -1;
            var parts = new string[full.Words.Count];
            for (int index = 0; index < parts.Length; index++) parts[index] = full.Words[index].Text;
            if (full.Text != String.Join(" ", parts)) return -1;
            var remaining = new string[partial.Words.Count];
            for (int index = 0; index < remaining.Length; index++) remaining[index] = partial.Words[index].Text;
            if (partial.Text != String.Join(" ", remaining)) return -1;

            int unique = -1;
            for (int omitted = 0; omitted < parts.Length; omitted++)
            {
                bool matches = true;
                for (int index = 0; index < partial.Words.Count; index++)
                {
                    if (SameInteriorWord(full.Words[index < omitted ? index : index + 1], partial.Words[index], nearExact: false))
                        continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                if (unique >= 0) return -1;
                unique = omitted;
            }
            return unique;
        }

        private static TextLineRegion ValidateInteriorRead(TextRecognitionResult primary, TextRecognitionResult read,
            InteriorRowRecovery plan, Rectangle crop, Func<RectangleF, bool> hasSourceInk, CancellationToken token)
        {
            if (hasSourceInk == null || primary == null || read == null || !primary.Available || !read.Available ||
                !Horizontal(read.TextAngle) || primary.ProtectedGraphics.Count != 0 || read.ProtectedGraphics.Count != 0 ||
                read.Lines.Count != primary.Lines.Count - plan.RunCount + 1 ||
                plan.FirstRunIndex < 0 || plan.FirstRunIndex >= read.Lines.Count) return null;

            int split = plan.FirstRunIndex, last = split + plan.RunCount - 1;
            TextLineRegion left = primary.Lines[split], right = primary.Lines[last];
            for (int index = 0; index < read.Lines.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                TextLineRegion item = read.Lines[index];
                if (!ValidLine(item, crop) || !item.ExcludedGraphicBounds.IsEmpty) return null;
                if (index != split && !SameInteriorRow(primary.Lines[index < split ? index : index + plan.RunCount - 1], item, nearExact: false))
                    return null;
            }

            TextLineRegion replacement = read.Lines[split];
            int slot = plan.WordSlot, firstRight = plan.RunCount == 2 ? 0 : slot;
            if (replacement.Words.Count != slot + right.Words.Count - firstRight + 1) return null;

            for (int index = 0; index < slot; index++)
                if (!SameInteriorWord(left.Words[index], replacement.Words[index], nearExact: false)) return null;
            for (int index = firstRight; index < right.Words.Count; index++)
                if (!SameInteriorWord(right.Words[index], replacement.Words[slot + 1 + index - firstRight], nearExact: false)) return null;

            TextWordRegion added = replacement.Words[slot];
            if (!PreservesInteriorText(left, right, slot, added.Text, replacement.Text, plan.RunCount) ||
                added.Bounds.Left + added.Bounds.Width / 2 <= left.Words[slot - 1].Bounds.Right ||
                added.Bounds.Left + added.Bounds.Width / 2 >= right.Words[firstRight].Bounds.Left ||
                added.Bounds.Left < replacement.Words[slot - 1].Bounds.Right ||
                added.Bounds.Right > replacement.Words[slot + 1].Bounds.Left) return null;
            for (int index = 0; index < primary.Lines.Count; index++)
                if ((index < split || index > last) && primary.Lines[index].Bounds.IntersectsWith(replacement.Bounds)) return null;
            return hasSourceInk(added.Bounds) ? replacement : null;
        }

        private static bool PreservesInteriorText(TextLineRegion left, TextLineRegion right, int slot,
            string added, string candidate, int runCount)
        {
            if (runCount == 2) return candidate == left.Text + " " + added + " " + right.Text;
            int cursor = 0;
            for (int index = 0; index < slot; index++)
            {
                string word = left.Words[index].Text;
                int start = left.Text.IndexOf(word, cursor, StringComparison.Ordinal);
                if (start < 0) return false;
                cursor = start + word.Length;
            }
            int next = left.Text.IndexOf(left.Words[slot].Text, cursor, StringComparison.Ordinal);
            return next == cursor + 1 && left.Text[cursor] == ' ' && candidate == left.Text.Insert(next, added + " ");
        }

        private static bool SameInteriorRow(TextLineRegion first, TextLineRegion second, bool nearExact)
        {
            if (!String.Equals(first.Text, second.Text, StringComparison.Ordinal) || first.Words.Count != second.Words.Count)
                return false;
            for (int index = 0; index < first.Words.Count; index++)
                if (!SameInteriorWord(first.Words[index], second.Words[index], nearExact)) return false;
            return true;
        }

        private static bool SameInteriorWord(TextWordRegion first, TextWordRegion second, bool nearExact)
        {
            if (!String.Equals(first.Text, second.Text, StringComparison.Ordinal)) return false;
            if (nearExact) return NearSameBounds(first.Bounds, second.Bounds);
            // Native recognition can widen or shift word boxes with canvas
            // shape. Match the same ink spatially without adopting other rows.
            RectangleF overlap = RectangleF.Intersect(first.Bounds, second.Bounds);
            return overlap.Width >= Math.Min(first.Bounds.Width, second.Bounds.Width) * .75f &&
                overlap.Height >= Math.Min(first.Bounds.Height, second.Bounds.Height) * .75f;
        }

        internal sealed class SparseColumnReviewCell
        {
            internal readonly RectangleF Bounds;
            internal readonly IReadOnlyList<TextLineRegion> Runs;

            internal SparseColumnReviewCell(RectangleF bounds, List<TextLineRegion> runs)
            {
                Bounds = bounds;
                Runs = runs.AsReadOnly();
            }
        }

        internal sealed class SparseColumnRecovery
        {
            internal readonly Rectangle Column;
            internal readonly IReadOnlyList<RectangleF> MissingRows;
            internal readonly IReadOnlyList<SparseColumnReviewCell> ReviewCells;
            internal readonly float LabelLeft;
            internal readonly float LeftTolerance;

            internal SparseColumnRecovery(Rectangle column, List<RectangleF> missingRows,
                List<SparseColumnReviewCell> reviewCells, float labelLeft, float leftTolerance)
            {
                Column = column;
                MissingRows = missingRows.AsReadOnly();
                ReviewCells = reviewCells.AsReadOnly();
                LabelLeft = labelLeft;
                LeftTolerance = leftTolerance;
            }
        }

        internal static SparseColumnRecovery FindSparseColumnRecovery(TextRecognitionResult refined, Size size,
            CancellationToken token, bool reviewExisting = false)
        {
            token.ThrowIfCancellationRequested();
            if (refined == null || !refined.Available || !Horizontal(refined.TextAngle) ||
                size.Width < 96 || size.Height < 80 || refined.Lines.Count < 9 ||
                refined.Lines.Count > MaximumSparseColumnRuns) return null;

            var source = new Rectangle(Point.Empty, size);
            var runs = new List<TextLineRegion>();
            var heights = new List<float>();
            foreach (TextLineRegion line in refined.Lines)
                if (ValidLine(line, source)) { runs.Add(line); heights.Add(line.Bounds.Height); }
            if (runs.Count < 9) return null;
            heights.Sort();
            float height = heights[heights.Count / 2];
            float tolerance = Math.Max(5, height * .75f);
            runs.Sort((first, second) => first.Bounds.Left.CompareTo(second.Bounds.Left));

            var columns = new List<List<TextLineRegion>>();
            for (int start = 0; start < runs.Count;)
            {
                int end = start + 1;
                while (end < runs.Count && runs[end].Bounds.Left - runs[start].Bounds.Left <= tolerance) end++;
                if (end - start >= 3) columns.Add(runs.GetRange(start, end - start));
                start = end;
            }

            SparseColumnRecovery best = null;
            int bestAligned = 0;
            for (int index = 0; index + 1 < columns.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                List<TextLineRegion> labels = columns[index];
                float labelLeft = labels[labels.Count / 2].Bounds.Left;
                float labelRight = 0;
                foreach (TextLineRegion label in labels) labelRight = Math.Max(labelRight, label.Bounds.Right);
                // A detached run from a few filenames can sit between labels
                // and the actual neighboring column. Use the nearest column
                // with enough observations and a stable vertical cadence.
                List<TextLineRegion> neighbors = null;
                float neighborLeft = 0, cadence = 0;
                for (int next = index + 1; next < columns.Count; next++)
                {
                    List<TextLineRegion> candidate = columns[next];
                    if (candidate.Count < 6 || (!reviewExisting && candidate.Count <= labels.Count) ||
                        candidate[0].Bounds.Left - labelRight < Math.Max(20, height * 2)) continue;
                    var ordered = new List<TextLineRegion>(candidate);
                    ordered.Sort((first, second) => Middle(first.Bounds).CompareTo(Middle(second.Bounds)));
                    cadence = RegularCadence(ordered, height);
                    if (cadence <= 0) continue;
                    neighbors = ordered;
                    neighborLeft = candidate[0].Bounds.Left;
                    break;
                }
                if (neighbors == null) continue;
                int left = Math.Max(0, (int)Math.Floor(labelLeft - Math.Min(3, height * .3f)));
                int right = (int)Math.Floor(neighborLeft - Math.Max(3, height * .35f));
                var column = Rectangle.FromLTRB(left, 0, right, size.Height);
                if (column.Width < 24) continue;

                var missingRows = new List<RectangleF>();
                var reviewCells = new List<SparseColumnReviewCell>();
                int aligned = 0;
                for (int rowIndex = 0; rowIndex < neighbors.Count; rowIndex++)
                {
                    TextLineRegion neighbor = neighbors[rowIndex];
                    float middle = Middle(neighbor.Bounds);
                    float gap = cadence;
                    if (rowIndex > 0) gap = Math.Min(gap, middle - Middle(neighbors[rowIndex - 1].Bounds));
                    if (rowIndex + 1 < neighbors.Count) gap = Math.Min(gap, Middle(neighbors[rowIndex + 1].Bounds) - middle);
                    // Neighbor gaps can have isolated outliers even with a stable cadence.
                    // Use each row's smaller gap so pixel-aligned clips cannot overlap.
                    float halfHeight = Math.Min(48, Math.Min(gap * .45f, Math.Max(height, neighbor.Bounds.Height) * .8f));
                    var row = RectangleF.FromLTRB(left, Math.Max(0, (float)Math.Ceiling(middle - halfHeight)),
                        right, Math.Min(size.Height, (float)Math.Floor(middle + halfHeight)));
                    if (row.Height < Math.Max(4, height * .8f)) continue;
                    if (HasRunInRow(labels, row))
                    {
                        aligned++;
                        if (reviewExisting)
                        {
                            SparseColumnReviewCell review = PlanSparseReviewCell(runs, labels, row);
                            if (review != null) reviewCells.Add(review);
                        }
                    }
                    else if (!HasRunInRow(runs, row)) missingRows.Add(row);
                }
                if (aligned < 3 || aligned * 4 < labels.Count * 3 || missingRows.Count > 64 ||
                    (missingRows.Count == 0 && reviewCells.Count == 0)) continue;
                if (reviewCells.Count > MaximumSparseReviewCells)
                {
                    // Check proven split runs before single-glyph ambiguities.
                    var limited = new List<SparseColumnReviewCell>(MaximumSparseReviewCells);
                    foreach (SparseColumnReviewCell cell in reviewCells)
                        if (cell.Runs.Count > 1 && limited.Count < MaximumSparseReviewCells) limited.Add(cell);
                    foreach (SparseColumnReviewCell cell in reviewCells)
                        if (cell.Runs.Count == 1 && limited.Count < MaximumSparseReviewCells) limited.Add(cell);
                    reviewCells = limited;
                }
                // Detached fragments can look sparse because they occur on
                // only a few rows. Prefer the lane backed by more intact row
                // matches before comparing the number of apparent vacancies.
                bool better = best == null || aligned > bestAligned ||
                    (aligned == bestAligned && missingRows.Count > best.MissingRows.Count);
                if (better)
                {
                    best = new SparseColumnRecovery(column, missingRows, reviewCells, labelLeft, tolerance);
                    bestAligned = aligned;
                }
            }
            return best;
        }

        private static SparseColumnReviewCell PlanSparseReviewCell(List<TextLineRegion> runs,
            List<TextLineRegion> labels, RectangleF row)
        {
            var occupied = new List<TextLineRegion>(3);
            foreach (TextLineRegion run in runs)
            {
                if (!RunInRow(run, row)) continue;
                if (occupied.Count == 3 || !row.Contains(run.Bounds) || !IsIdentifier(run.Text, allowSpace: true)) return null;
                occupied.Add(run);
            }
            if (occupied.Count == 0 || !labels.Contains(occupied[0])) return null;
            if (occupied.Count == 1)
            {
                string value = occupied[0].Text;
                if (value.Length < 4 || !IsIdentifier(value, allowSpace: false) ||
                    value.IndexOfAny(new[] { 'I', 'l', '1', 'B', '8' }) < 0) return null;
            }
            else
            {
                for (int index = 1; index < occupied.Count; index++)
                {
                    TextLineRegion previous = occupied[index - 1];
                    if (!CanJoinAdjacentRuns(previous.Text, previous.Bounds, occupied[index].Bounds)) return null;
                }
            }
            return new SparseColumnReviewCell(row, occupied);
        }

        internal static int MergeSparseColumnRecovery(TextRecognitionResult primary, TextRecognitionResult candidate,
            SparseColumnRecovery plan, Rectangle sourceBounds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (primary == null || candidate == null || plan == null || !primary.Available || !candidate.Available ||
                !Horizontal(candidate.TextAngle) || primary.Lines.Count > MaximumSparseColumnRuns ||
                candidate.Lines.Count > MaximumSparseColumnRuns) return 0;

            var found = new TextLineRegion[plan.MissingRows.Count];
            var ambiguous = new bool[found.Length];
            foreach (TextLineRegion line in candidate.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (!ValidLine(line, sourceBounds) || !((RectangleF)plan.Column).Contains(line.Bounds) ||
                    Math.Abs(line.Bounds.Left - plan.LabelLeft) > plan.LeftTolerance) continue;
                bool touchesGraphic = false;
                foreach (RectangleF graphic in primary.ProtectedGraphics)
                    if (graphic.IntersectsWith(line.Bounds)) { touchesGraphic = true; break; }
                if (touchesGraphic) continue;

                float middle = Middle(line.Bounds);
                for (int slot = 0; slot < found.Length; slot++)
                {
                    RectangleF row = plan.MissingRows[slot];
                    if (middle < row.Top || middle >= row.Bottom || !row.Contains(line.Bounds)) continue;
                    if (found[slot] == null) found[slot] = line;
                    else ambiguous[slot] = true;
                    break;
                }
            }

            var eligible = new TextRecognitionResult { Available = true, TextAngle = candidate.TextAngle };
            for (int slot = 0; slot < found.Length; slot++)
            {
                TextLineRegion line = found[slot];
                if (!ambiguous[slot] && line != null && !HasRunInRow(primary.Lines, plan.MissingRows[slot]))
                {
                    bool overlaps = false;
                    foreach (TextLineRegion existing in primary.Lines)
                        if (existing != null && existing.Bounds.IntersectsWith(line.Bounds)) { overlaps = true; break; }
                    if (overlaps) continue;
                    RectangleF clip = line.RecognitionClipBounds.IsEmpty ? plan.MissingRows[slot] :
                        RectangleF.Intersect(line.RecognitionClipBounds, plan.MissingRows[slot]);
                    if (clip.IsEmpty || !clip.Contains(line.Bounds)) continue;
                    TextLineRegion clipped = Clone(line);
                    clipped.RecognitionClipBounds = clip;
                    eligible.Lines.Add(clipped);
                }
            }
            return eligible.Lines.Count == 0 ? 0 : MergeMissing(primary, eligible, sourceBounds, token);
        }

        // The caller supplies only cells corroborated by two independent
        // reads of the same visible source ink. Keep the geometric and literal
        // checks here so unrelated, stale or conflicting runs cannot be merged.
        internal static int MergeSparseColumnReview(TextRecognitionResult primary, TextRecognitionResult candidate,
            SparseColumnRecovery plan, Rectangle sourceBounds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (primary == null || candidate == null || plan == null || !primary.Available || !candidate.Available ||
                !Horizontal(primary.TextAngle) || !Horizontal(candidate.TextAngle) ||
                primary.Lines.Count > MaximumSparseColumnRuns || candidate.Lines.Count > MaximumSparseReviewCells) return 0;

            var planned = new List<TextLineRegion>(primary.Lines);
            int accepted = 0;
            foreach (SparseColumnReviewCell cell in plan.ReviewCells)
            {
                token.ThrowIfCancellationRequested();
                TextLineRegion verified = null;
                foreach (TextLineRegion read in candidate.Lines)
                {
                    if (!ValidLine(read, sourceBounds) || !cell.Bounds.Contains(read.Bounds)) continue;
                    if (verified != null) { verified = null; break; }
                    verified = read;
                }
                if (verified == null || !CanReviewText(cell.Runs, verified.Text)) continue;

                int first = planned.Count, inRow = 0;
                RectangleF oldBounds = RectangleF.Empty, priorGraphics = RectangleF.Empty;
                RectangleF clip = verified.RecognitionClipBounds.IsEmpty ? cell.Bounds :
                    RectangleF.Intersect(cell.Bounds, verified.RecognitionClipBounds);
                bool stale = false;
                foreach (TextLineRegion original in cell.Runs)
                {
                    int index = planned.IndexOf(original);
                    if (index < 0) { stale = true; break; }
                    first = Math.Min(first, index);
                    oldBounds = Union(oldBounds, original.Bounds);
                    priorGraphics = Union(priorGraphics, original.ExcludedGraphicBounds);
                    if (!original.RecognitionClipBounds.IsEmpty)
                        clip = RectangleF.Intersect(clip, original.RecognitionClipBounds);
                }
                foreach (TextLineRegion existing in planned)
                    if (RunInRow(existing, cell.Bounds)) inRow++;
                if (stale || inRow != cell.Runs.Count || clip.IsEmpty || !clip.Contains(verified.Bounds) ||
                    !NearSameBounds(oldBounds, verified.Bounds) || priorGraphics.IntersectsWith(verified.Bounds)) continue;

                bool touchesGraphic = false;
                foreach (RectangleF graphic in primary.ProtectedGraphics)
                    if (graphic.IntersectsWith(verified.Bounds)) { touchesGraphic = true; break; }
                if (touchesGraphic) continue;

                TextLineRegion reviewed = Clone(verified);
                reviewed.RecognitionClipBounds = clip;
                reviewed.ExcludedGraphicBounds = Union(priorGraphics, reviewed.ExcludedGraphicBounds);
                foreach (TextLineRegion original in cell.Runs) planned.Remove(original);
                planned.Insert(first, reviewed);
                accepted++;
            }
            token.ThrowIfCancellationRequested();
            if (accepted == 0) return 0;
            var text = new List<string>(planned.Count);
            foreach (TextLineRegion line in planned) text.Add(line.Text ?? "");
            primary.Lines.Clear();
            primary.Lines.AddRange(planned);
            primary.Text = String.Join(Environment.NewLine, text.ToArray());
            return accepted;
        }

        private static bool CanReviewText(IReadOnlyList<TextLineRegion> original, string verified)
        {
            if (!IsIdentifier(verified, allowSpace: true)) return false;
            if (original.Count == 1)
            {
                string prior = original[0].Text;
                if (prior.Length != verified.Length) return false;
                bool corrected = false;
                for (int index = 0; index < prior.Length; index++)
                {
                    if (prior[index] == verified[index]) continue;
                    int shape = ConfusableClass(prior[index]);
                    if (corrected || shape == 0 || shape != ConfusableClass(verified[index])) return false;
                    corrected = true;
                }
                return corrected;
            }

            string joined = "", withoutStraySpace = "";
            foreach (TextLineRegion run in original)
            {
                joined += run.Text;
                withoutStraySpace += WithoutAdjacentSpuriousSpace(run);
            }
            return String.Equals(joined, verified, StringComparison.Ordinal) ||
                (joined.Length - withoutStraySpace.Length == 1 &&
                 String.Equals(withoutStraySpace, verified, StringComparison.Ordinal));
        }

        internal static int RecoverSpacedFilenameExtensions(TextRecognitionResult result, Bitmap source,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (result == null || !result.Available || !(Math.Abs(result.TextAngle.GetValueOrDefault()) <= .25) ||
                result.Lines.Count > 1024) return 0;

            var frame = new Rectangle(Point.Empty, source.Size);
            var verified = new List<(TextLineRegion Line, TextWordRegion Word)>();
            int checkedRows = 0;
            foreach (TextLineRegion line in result.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (line?.Words.Count != 2 || !ValidLine(line, frame)) continue;
                TextWordRegion stem = line.Words[0], extension = line.Words[1];
                if (stem.Footprint != null || extension.Footprint != null ||
                    !IsFilenameStemWithTrailingDigits(stem.Text) || !IsFilenameExtension(extension.Text) ||
                    !String.Equals(line.Text, stem.Text + " " + extension.Text, StringComparison.Ordinal)) continue;

                float gap = extension.Bounds.Left - stem.Bounds.Right;
                if (gap < 0 || gap > 32) continue;
                RectangleF bounds = RectangleF.Union(stem.Bounds, extension.Bounds);
                var crop = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                    (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
                if (!frame.Contains(crop) || crop.Height < 7 || crop.Height > 48 ||
                    (long)crop.Width * crop.Height > 10_000 ||
                    (!line.RecognitionClipBounds.IsEmpty && !line.RecognitionClipBounds.Contains(crop)) ||
                    (!line.ExcludedGraphicBounds.IsEmpty && line.ExcludedGraphicBounds.IntersectsWith(crop)) ||
                    result.ProtectedGraphics.Exists(graphic => graphic.IntersectsWith(crop))) continue;

                if (++checkedRows > MaximumFilenameExtensionChecks) break;
                if (HasContinuousFilenameInk(source, crop, stem, extension, token))
                    verified.Add((line, new TextWordRegion { Text = stem.Text + extension.Text, Bounds = bounds }));
            }

            token.ThrowIfCancellationRequested();
            if (verified.Count == 0) return 0;
            foreach (var item in verified)
            {
                item.Line.Text = item.Word.Text;
                item.Line.Words.Clear();
                item.Line.Words.Add(item.Word);
            }
            var text = new List<string>(result.Lines.Count);
            foreach (TextLineRegion line in result.Lines) text.Add(line.Text);
            result.Text = String.Join(Environment.NewLine, text);
            return verified.Count;
        }

        private static bool IsFilenameStemWithTrailingDigits(string text)
        {
            if (text.Length < 6 || text.Length > 72) return false;
            int letters = 0;
            foreach (char value in text)
            {
                if (AsciiLetter(value)) letters++;
                else if ((value < '0' || value > '9') && !JoinDelimiter(value)) return false;
            }
            for (int index = text.Length - 4; index < text.Length; index++)
                if (text[index] < '0' || text[index] > '9') return false;
            return letters >= 2;
        }

        private static bool IsFilenameExtension(string text)
        {
            if (text.Length < 3 || text.Length > 6 || text[0] != '.') return false;
            for (int index = 1; index < text.Length; index++)
                if (!AsciiLetter(text[index])) return false;
            return true;
        }

        private static bool AsciiLetter(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        private struct FilenameInkRun
        {
            internal int Left, Right, Top, Bottom;
            internal int Width => Right - Left + 1;
            internal int Height => Bottom - Top + 1;
            internal double Center => (Left + Right) * .5;
        }

        private static bool HasContinuousFilenameInk(Bitmap source, Rectangle crop, TextWordRegion stem,
            TextWordRegion extension, CancellationToken token)
        {
            Color background = source.GetPixel(crop.Left, crop.Top);
            int luminance = FilenameLuminance(background);
            if (background.A != 255 || (luminance > 85 && luminance < 170) ||
                source.GetPixel(crop.Right - 1, crop.Top).ToArgb() != background.ToArgb() ||
                source.GetPixel(crop.Left, crop.Bottom - 1).ToArgb() != background.ToArgb() ||
                source.GetPixel(crop.Right - 1, crop.Bottom - 1).ToArgb() != background.ToArgb()) return false;

            // An inserted separator never changes the letters. Require the
            // source to show one isolated ink run per character at two
            // contrast cutoffs. The period must occupy the very next cell
            // of a measured glyph grid, with no cell for a real space.
            bool dark = luminance <= 85;
            return FitsFilenameGlyphGrid(ReadFilenameInkRuns(source, crop, luminance, dark, 90, token), stem, extension) &&
                FitsFilenameGlyphGrid(ReadFilenameInkRuns(source, crop, luminance, dark, 110, token), stem, extension);
        }

        private static List<FilenameInkRun> ReadFilenameInkRuns(Bitmap source, Rectangle crop, int background,
            bool dark, int contrast, CancellationToken token)
        {
            var runs = new List<FilenameInkRun>();
            for (int x = crop.Left; x < crop.Right; x++)
            {
                token.ThrowIfCancellationRequested();
                int top = crop.Bottom, bottom = crop.Top;
                for (int y = crop.Top; y < crop.Bottom; y++)
                {
                    Color pixel = source.GetPixel(x, y);
                    if (pixel.A != 255) return null;
                    int luminance = FilenameLuminance(pixel);
                    if ((dark ? luminance - background : background - luminance) < contrast) continue;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
                if (top > bottom) continue;
                int last = runs.Count - 1;
                if (last >= 0 && runs[last].Right == x - 1)
                {
                    FilenameInkRun run = runs[last];
                    run.Right = x;
                    run.Top = Math.Min(run.Top, top);
                    run.Bottom = Math.Max(run.Bottom, bottom);
                    runs[last] = run;
                }
                else runs.Add(new FilenameInkRun { Left = x, Right = x, Top = top, Bottom = bottom });
            }
            return runs;
        }

        private static bool FitsFilenameGlyphGrid(List<FilenameInkRun> ink, TextWordRegion stem, TextWordRegion extension)
        {
            int split = stem.Text.Length;
            if (ink == null || ink.Count != split + extension.Text.Length) return false;
            FilenameInkRun lastDigit = ink[split - 1], period = ink[split];
            double pitch = (lastDigit.Center - ink[split - 4].Center) / 3;
            if (pitch < 5 || pitch > 32 || lastDigit.Height < 6 ||
                lastDigit.Right > Math.Ceiling(stem.Bounds.Right) || period.Left < Math.Floor(extension.Bounds.Left) ||
                extension.Bounds.Left - stem.Bounds.Right > pitch * .7) return false;

            double tolerance = Math.Max(1, pitch * .16);
            for (int index = 1; index < ink.Count; index++)
                if (Math.Abs(ink[index].Center - ink[index - 1].Center - pitch) > tolerance) return false;
            for (int index = split - 4; index < split; index++)
            {
                FilenameInkRun digit = ink[index];
                if (digit.Width < pitch * .35 || Math.Abs(digit.Height - lastDigit.Height) > 2 ||
                    Math.Abs(digit.Bottom - lastDigit.Bottom) > 1) return false;
            }
            if (period.Width > pitch * .42 || period.Height > lastDigit.Height * .42 ||
                Math.Abs(period.Bottom - lastDigit.Bottom) > 1) return false;
            for (int index = split + 1; index < ink.Count; index++)
                if (ink[index].Height < lastDigit.Height * .6) return false;
            return true;
        }

        private static int FilenameLuminance(Color pixel) => (21 * pixel.R + 72 * pixel.G + 7 * pixel.B + 50) / 100;

        private static string WithoutAdjacentSpuriousSpace(TextLineRegion line)
        {
            string text = line.Text;
            TextWordRegion previous = null;
            int cursor = 0, remove = -1;
            foreach (TextWordRegion word in line.Words)
            {
                int found = text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
                if (found < 0) return text;
                if (previous != null && found == cursor + 1 && text[cursor] == ' ' && text[cursor - 1] == '.' &&
                    Char.IsLetterOrDigit(word.Text[0]) && NearAdjacent(previous.Bounds, word.Bounds))
                {
                    if (remove >= 0) return text;
                    remove = cursor;
                }
                cursor = found + word.Text.Length;
                previous = word;
            }
            return remove < 0 ? text : text.Remove(remove, 1);
        }

        private static int ConfusableClass(char value)
        {
            if (value == 'I' || value == 'l' || value == '1') return 1;
            if (value == 'B' || value == '8') return 2;
            return 0;
        }

        private static bool IsIdentifier(string text, bool allowSpace)
        {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 256) return false;
            bool hasLetterOrDigit = false;
            foreach (char value in text)
            {
                if (Char.IsLetterOrDigit(value)) hasLetterOrDigit = true;
                else if (!JoinDelimiter(value) && !(allowSpace && value == ' ')) return false;
            }
            return hasLetterOrDigit;
        }

        private static bool JoinDelimiter(char value) => value == '-' || value == '_' || value == '.';

        internal static bool CanJoinAdjacentRuns(string firstText, RectangleF first, RectangleF second) =>
            !String.IsNullOrEmpty(firstText) && JoinDelimiter(firstText[firstText.Length - 1]) && NearAdjacent(first, second);

        private static bool NearAdjacent(RectangleF first, RectangleF second)
        {
            float gap = second.Left - first.Right;
            float overlap = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
            return gap >= -1 && gap <= 2 && overlap >= Math.Min(first.Height, second.Height) * .65f;
        }

        private static bool NearSameBounds(RectangleF first, RectangleF second)
        {
            RectangleF firstMargin = first, secondMargin = second;
            firstMargin.Inflate(2, 2);
            secondMargin.Inflate(2, 2);
            return firstMargin.Contains(second) && secondMargin.Contains(first);
        }

        private static RectangleF Union(RectangleF first, RectangleF second) => first.IsEmpty ? second :
            second.IsEmpty ? first : RectangleF.Union(first, second);

        private static float RegularCadence(List<TextLineRegion> rows, float height)
        {
            var gaps = new List<float>(rows.Count - 1);
            for (int index = 1; index < rows.Count; index++)
                gaps.Add(Middle(rows[index].Bounds) - Middle(rows[index - 1].Bounds));
            gaps.Sort();
            float cadence = gaps[gaps.Count / 2];
            if (cadence < height * 1.15f || cadence > height * 5) return 0;
            int matching = 0;
            foreach (float gap in gaps) if (Math.Abs(gap - cadence) <= Math.Max(2, cadence * .12f)) matching++;
            return matching * 4 >= gaps.Count * 3 ? cadence : 0;
        }

        private static float Middle(RectangleF bounds) => bounds.Top + bounds.Height / 2;

        private static bool HasRunInRow(List<TextLineRegion> runs, RectangleF row)
        {
            foreach (TextLineRegion line in runs)
                if (RunInRow(line, row)) return true;
            return false;
        }

        private static bool RunInRow(TextLineRegion line, RectangleF row) => line != null &&
            line.Bounds.Left < row.Right && line.Bounds.Right > row.Left &&
            Middle(line.Bounds) >= row.Top && Middle(line.Bounds) < row.Bottom;

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
