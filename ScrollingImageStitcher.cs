using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipStudio
{
    internal enum ScrollingAppendStatus { Accepted, Unchanged, NoReliableOverlap, Limit }

    internal sealed class ScrollingAppendResult
    {
        internal ScrollingAppendStatus Status { get; private set; }
        internal int AddedHeight { get; private set; }
        internal int Offset { get; private set; }
        internal double Confidence { get; private set; }
        internal string Message { get; private set; }

        internal ScrollingAppendResult(ScrollingAppendStatus status, int addedHeight, int offset, double confidence, string message)
        {
            Status = status; AddedHeight = addedHeight; Offset = offset;
            Confidence = confidence; Message = message;
        }
    }

    // Pure image processing: this class neither captures a screen nor scrolls a window.
    // Inputs remain caller-owned. Only a uniquely supported downward translation is
    // accepted. Repeating rows, blank overlaps, reflow, horizontal motion, large fixed
    // overlays and substantial animation deliberately stop rather than guess.
    internal sealed class ScrollingStitchSession : IDisposable
    {
        internal const long MaximumPixels = 64L * 1000 * 1000;
        internal const int MaximumWidth = 16384;
        internal const int MaximumHeight = 30000;
        internal const int MaximumFrames = 100;
        private const int MaximumSampleColumns = 128;
        private static readonly ScrollingAppendResult MemoryLimitResult = new ScrollingAppendResult(
            ScrollingAppendStatus.Limit, 0, 0, 0,
            "There is not enough image memory to add another section. The accepted image has been kept.");
        private List<Strip> strips = new List<Strip>();
        private SampleFrame previous;
        private bool disposed;

        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int FrameCount { get; private set; }
        // The last uniquely aligned moving body excludes fixed toolbars and
        // composers. The scroll driver uses this conservative height for its
        // next step instead of treating all captured chrome as scrolling space.
        internal int ScrollableHeight { get; private set; }

        internal ScrollingStitchSession(Bitmap firstFrame)
        {
            if (firstFrame == null) throw new ArgumentNullException("firstFrame");
            ValidateInitialSize(firstFrame.Width, firstFrame.Height);
            Bitmap owned = Normalize(firstFrame);
            try
            {
                previous = SampleFrame.Read(owned);
                strips.Add(new Strip(owned, owned.Height));
                Width = owned.Width; Height = owned.Height; FrameCount = 1;
                ScrollableHeight = owned.Height;
            }
            catch { owned.Dispose(); throw; }
        }

        internal ScrollingAppendResult Append(Bitmap nextFrame)
        {
            ThrowIfDisposed();
            if (nextFrame == null) throw new ArgumentNullException("nextFrame");
            if (nextFrame.Width != Width || nextFrame.Height != previous.Height)
                return Result(ScrollingAppendStatus.NoReliableOverlap, 0, 0, 0, "The captured area changed size. The accepted image has been kept.");
            if (FrameCount >= MaximumFrames)
                return Result(ScrollingAppendStatus.Limit, 0, 0, 0, "The 100-frame capture limit has been reached.");

            Bitmap normalized = null;
            Bitmap addition = null;
            Bitmap retainedTail = null;
            try
            {
                normalized = Normalize(nextFrame);
                SampleFrame next = SampleFrame.Read(normalized);
                if (IsUnchanged(previous, next))
                    return Result(ScrollingAppendStatus.Unchanged, 0, 0, 1, "No further movement was detected.");

                int top = StableEdge(previous, next, true);
                int bottom = StableEdge(previous, next, false);
                Match best = FindMatch(previous, next, top, bottom);
                if (best == null)
                    return Result(ScrollingAppendStatus.NoReliableOverlap, 0, 0, 0, "A unique overlap could not be confirmed. The accepted image has been kept.");

                long proposedHeight = (long)Height + best.Offset;
                if (proposedHeight > MaximumHeight || proposedHeight * Width > MaximumPixels)
                    return Result(ScrollingAppendStatus.Limit, 0, best.Offset, best.Confidence, "The image size limit has been reached (30,000 pixels high or 64 million pixels).");

                // Replace the previous fixed footer and append the newly revealed body.
                // A conservative overestimate of a blank edge merely moves the seam
                // into the already verified overlap; it does not discard body pixels.
                int start = next.Height - bottom - best.Offset;
                addition = normalized.Clone(new Rectangle(0, start, Width, bottom + best.Offset), PixelFormat.Format32bppArgb);
                List<Strip> replacement = new List<Strip>(strips.Count + 1);
                int remaining = Height - bottom;
                int retainedWholeStrips = 0;
                foreach (Strip strip in strips)
                {
                    if (remaining == 0) break;
                    if (remaining >= strip.Height)
                    {
                        replacement.Add(strip);
                        retainedWholeStrips++;
                        remaining -= strip.Height;
                    }
                    else
                    {
                        // Physically crop the tail; retaining a large bitmap behind
                        // a short logical strip would defeat the pixel memory cap.
                        retainedTail = strip.Image.Clone(new Rectangle(0, 0, Width, remaining), PixelFormat.Format32bppArgb);
                        replacement.Add(new Strip(retainedTail, remaining));
                        remaining = 0;
                    }
                }
                replacement.Add(new Strip(addition, addition.Height));
                ScrollingAppendResult accepted = Result(ScrollingAppendStatus.Accepted, best.Offset, best.Offset, best.Confidence, "The next section was added.");

                // Everything that can allocate has finished. Commit atomically from
                // the caller's perspective, leaving accepted pixels intact on failure.
                List<Strip> oldStrips = strips;
                strips = replacement;
                addition = null;
                retainedTail = null;
                previous = next;
                Height = (int)proposedHeight;
                FrameCount++;
                ScrollableHeight = next.Height - top - bottom;
                for (int i = retainedWholeStrips; i < oldStrips.Count; i++) oldStrips[i].Image.Dispose();
                return accepted;
            }
            catch (OutOfMemoryException)
            {
                return MemoryLimitResult;
            }
            finally
            {
                if (addition != null) addition.Dispose();
                if (retainedTail != null) retainedTail.Dispose();
                if (normalized != null) normalized.Dispose();
            }
        }

        // The returned bitmap is independent of this session and belongs to the caller.
        // Retained strips use at most MaximumPixels * 4 bytes, plus one sampled frame;
        // rendering a result temporarily needs another output-sized bitmap.
        internal Bitmap BuildImage()
        {
            ThrowIfDisposed();
            Bitmap result = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    int y = 0;
                    foreach (Strip strip in strips)
                    {
                        graphics.DrawImage(strip.Image, new Rectangle(0, y, Width, strip.Height),
                            new Rectangle(0, 0, Width, strip.Height), GraphicsUnit.Pixel);
                        y += strip.Height;
                    }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (Strip strip in strips) strip.Image.Dispose();
            strips.Clear(); previous = null;
        }

        private static void ValidateInitialSize(int width, int height)
        {
            if (width < 32 || height < 64)
                throw new ArgumentException("Choose a scrolling area at least 32 pixels wide and 64 pixels high.");
            if (width > MaximumWidth || height > MaximumHeight || (long)width * height > MaximumPixels)
                throw new ArgumentException("The selected image exceeds the scrolling capture size limit.");
        }

        private static Bitmap Normalize(Bitmap source)
        {
            Bitmap result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImageUnscaled(source, 0, 0);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static bool IsUnchanged(SampleFrame a, SampleFrame b)
        {
            long total = 0;
            int changed = 0;
            for (int i = 0; i < a.Pixels.Length; i += 3)
            {
                int error = Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
                total += error;
                if (error > 18) changed++;
            }
            return total <= a.Pixels.Length * 0.15 && changed <= a.Height * a.Columns * 0.002;
        }

        private static int StableEdge(SampleFrame a, SampleFrame b, bool fromTop)
        {
            // A chat composer can occupy more than a quarter of the viewport.
            // Trim only rows that stayed visually fixed; FindMatch still
            // requires a substantial feature-rich moving body and unique fit.
            int limit = a.Height * 45 / 100;
            int length = 0;
            for (int i = 0; i < limit; i++)
            {
                int row = fromTop ? i : a.Height - 1 - i;
                int changed;
                double error = RowError(a, row, b, row, 1, out changed);
                // A few changed pixels (for example a caret) must not make a
                // static toolbar repeat in the middle of the resulting image.
                if (error > 0.8 || changed > Math.Max(1, a.Columns / 50)) break;
                length++;
            }
            return length;
        }

        private static Match FindMatch(SampleFrame a, SampleFrame b, int top, int bottom)
        {
            Match strict = FindMatch(a, b, top, bottom, false);
            return strict ?? FindMatch(a, b, top, bottom, true);
        }

        private static Match FindMatch(SampleFrame a, SampleFrame b, int top, int bottom, bool tolerateLocalizedAnimation)
        {
            int bodyHeight = a.Height - top - bottom;
            if (bodyHeight < Math.Max(64, a.Height / 5)) return null;
            int minimumOverlap = Math.Max(24, a.Height / 10);
            int maximumOffset = Math.Min(a.Height * 4 / 5, a.Height - top - bottom - minimumOverlap);
            if (maximumOffset < 1) return null;
            List<Match> candidates = new List<Match>();
            for (int offset = 1; offset <= maximumOffset; offset++)
            {
                double score = Score(a, b, offset, top, bottom, false, tolerateLocalizedAnimation);
                if (!double.IsPositiveInfinity(score)) candidates.Add(new Match(offset, score));
            }
            candidates.Sort(delegate(Match x, Match y) { return x.Score.CompareTo(y.Score); });
            if (candidates.Count == 0 || candidates[0].Score > 4.0) return null;

            // Verify several distinct basins, so a repeating pattern cannot be
            // accepted merely because one occurrence happened to sort first.
            List<Match> verified = new List<Match>();
            foreach (Match candidate in candidates)
            {
                bool close = false;
                foreach (Match existing in verified)
                    if (Math.Abs(existing.Offset - candidate.Offset) <= 2) { close = true; break; }
                if (close) continue;
                candidate.Score = Score(a, b, candidate.Offset, top, bottom, true, tolerateLocalizedAnimation);
                if (!double.IsPositiveInfinity(candidate.Score) &&
                    !OverlapBoundariesMatch(a, b, candidate.Offset, top, bottom))
                    candidate.Score = double.PositiveInfinity;
                verified.Add(candidate);
                if (verified.Count == 16) break;
            }
            verified.Sort(delegate(Match x, Match y) { return x.Score.CompareTo(y.Score); });
            Match best = verified[0];
            if (double.IsPositiveInfinity(best.Score) || best.Score > 2.0) return null;
            double second = verified.Count > 1 ? verified[1].Score : double.PositiveInfinity;
            if (second < best.Score + Math.Max(0.65, best.Score * 0.65)) return null;
            best.Confidence = Math.Min(1.0, Math.Max(0.0, 1.0 - best.Score / 4.0));
            if (tolerateLocalizedAnimation) best.Confidence = Math.Min(0.85, best.Confidence);
            return best;
        }

        // Tolerating a few interior changed rows is different from appending
        // through an unverified seam. Apply this to strict matches as well:
        // even a short late paragraph reflow can otherwise omit source rows.
        private static bool OverlapBoundariesMatch(SampleFrame a, SampleFrame b, int offset, int top, int bottom)
        {
            int length = a.Height - bottom - top - offset;
            int band = Math.Max(8, Math.Min(24, length / 16));
            for (int edge = 0; edge < 2; edge++)
                for (int index = 0; index < band; index++)
                {
                    int row = top + (edge == 0 ? index : length - band + index);
                    int changed;
                    double error = RowError(a, row + offset, b, row, 1, out changed);
                    if (error > 0.8 || changed > Math.Max(1, a.Columns / 50)) return false;
                }
            return true;
        }

        private static double Score(SampleFrame a, SampleFrame b, int offset, int top, int bottom, bool full, bool tolerateLocalizedAnimation)
        {
            if (tolerateLocalizedAnimation) return ScoreStableAnchors(a, b, offset, top, bottom, full);
            int length = a.Height - bottom - top - offset;
            int rows = full ? length : Math.Min(72, length);
            int stride = full ? 1 : 4;
            double[] errors = new double[rows];
            int features = 0, firstFeature = -1, lastFeature = -1, badRows = 0;
            long badPixels = 0;
            double total = 0;
            for (int i = 0; i < rows; i++)
            {
                int y = top + (rows == 1 ? 0 : i * (length - 1) / (rows - 1));
                int changed;
                double error = RowError(a, y + offset, b, y, stride, out changed);
                total += error; badPixels += changed;
                if (error > 5) badRows++;
                if (Math.Max(a.Energy[y + offset], b.Energy[y]) >= 0.6)
                {
                    errors[features++] = error;
                    if (firstFeature < 0) firstFeature = y;
                    lastFeature = y;
                }
            }
            if (features < Math.Min(8, Math.Max(4, rows / 4)) || lastFeature - firstFeature < Math.Min(48, length / 3))
                return double.PositiveInfinity;
            Array.Sort(errors, 0, features);
            int keep = Math.Max(1, features - (full ? features / 50 : features / 10));
            double featureError = 0;
            for (int i = 0; i < keep; i++) featureError += errors[i];
            featureError /= keep;
            if (full && (badRows > Math.Max(2, rows / 25) || badPixels > (long)rows * a.Columns / 40))
                return double.PositiveInfinity;
            return Math.Max(featureError, total / rows);
        }

        // Only used after strict matching found no safe offset. A small animated
        // part must not discard an otherwise independently proven translation.
        // Blank rows cannot dilute the outlier fraction: informative rows have
        // their own bound, and anchors must cover every quarter and both ends.
        private static double ScoreStableAnchors(SampleFrame a, SampleFrame b, int offset, int top, int bottom, bool full)
        {
            int length = a.Height - bottom - top - offset;
            int rows = full ? length : Math.Min(72, length);
            int stride = full ? 1 : 4;
            int columns = (a.Columns + stride - 1) / stride;
            int informative = 0, informativeOutliers = 0, outliers = 0;
            int anchors = 0, firstAnchor = -1, lastAnchor = -1;
            int endpointBand = Math.Max(8, Math.Min(24, length / 16));
            int firstBandAnchors = 0, lastBandAnchors = 0;
            int[] quarterAnchors = new int[4];
            long changedPixels = 0;
            double anchorError = 0;
            for (int index = 0; index < rows; index++)
            {
                int local = rows == 1 ? 0 : index * (length - 1) / (rows - 1);
                int y = top + local, changed;
                double error = RowError(a, y + offset, b, y, stride, out changed);
                bool endpoint = local < endpointBand || local >= length - endpointBand;
                // A mismatch at either overlap boundary may be a missed fixed
                // footer or a reflow, rather than animation. Appending through
                // it could skip rows, so no outlier allowance applies there.
                if (endpoint && (error > 0.8 || changed > Math.Max(1, columns / 50)))
                    return double.PositiveInfinity;
                changedPixels += changed;
                bool feature = Math.Max(a.Energy[y + offset], b.Energy[y]) >= 0.6;
                bool outlier = error > 1.2 || changed > Math.Max(1, columns / 50);
                if (outlier) outliers++;
                if (feature || outlier)
                {
                    informative++;
                    if (outlier) informativeOutliers++;
                }
                if (!feature || outlier) continue;
                anchors++;
                anchorError += error;
                if (firstAnchor < 0) firstAnchor = local;
                lastAnchor = local;
                if (local < endpointBand) firstBandAnchors++;
                if (local >= length - endpointBand) lastBandAnchors++;
                quarterAnchors[Math.Min(3, local * 4 / length)]++;
            }
            if (anchors < (full ? 12 : 8) || informative == 0 ||
                outliers > rows * 0.15 || informativeOutliers > informative * 0.15 ||
                changedPixels > (long)rows * columns * 0.15 ||
                firstAnchor >= endpointBand || lastAnchor < length - endpointBand ||
                firstBandAnchors < 2 || lastBandAnchors < 2)
                return double.PositiveInfinity;
            for (int quarter = 0; quarter < quarterAnchors.Length; quarter++)
                if (quarterAnchors[quarter] < 2) return double.PositiveInfinity;
            return anchorError / anchors + 2.0 * informativeOutliers / informative;
        }

        private static double RowError(SampleFrame a, int rowA, SampleFrame b, int rowB, int stride, out int changed)
        {
            int startA = rowA * a.Columns * 3, startB = rowB * b.Columns * 3;
            long sum = 0; int count = 0; changed = 0;
            for (int x = 0; x < a.Columns; x += stride)
            {
                int p = startA + x * 3, q = startB + x * 3;
                int error = Math.Abs(a.Pixels[p] - b.Pixels[q]) + Math.Abs(a.Pixels[p + 1] - b.Pixels[q + 1]) + Math.Abs(a.Pixels[p + 2] - b.Pixels[q + 2]);
                sum += error; count++;
                if (error > 18) changed++;
            }
            return (double)sum / (count * 3);
        }

        private static ScrollingAppendResult Result(ScrollingAppendStatus status, int addedHeight, int offset, double confidence, string message)
        {
            return new ScrollingAppendResult(status, addedHeight, offset, confidence, message);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("ScrollingStitchSession");
        }

        private sealed class Strip
        {
            internal readonly Bitmap Image;
            internal int Height;
            internal Strip(Bitmap image, int height) { Image = image; Height = height; }
        }

        private sealed class Match
        {
            internal readonly int Offset;
            internal double Score;
            internal double Confidence;
            internal Match(int offset, double score) { Offset = offset; Score = score; }
        }

        private sealed class SampleFrame
        {
            internal int Height;
            internal int Columns;
            internal byte[] Pixels;
            internal double[] Energy;

            internal static SampleFrame Read(Bitmap bitmap)
            {
                int left = Math.Max(1, bitmap.Width / 100);
                int right = bitmap.Width - 1 - Math.Max(1, bitmap.Width / 50);
                int columns = Math.Min(MaximumSampleColumns, right - left + 1);
                SampleFrame result = new SampleFrame();
                result.Height = bitmap.Height; result.Columns = columns;
                result.Pixels = new byte[bitmap.Height * columns * 3];
                result.Energy = new double[bitmap.Height];
                byte[] row = new byte[bitmap.Width * 4];
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        int destination = y * columns * 3;
                        for (int i = 0; i < columns; i++)
                        {
                            int x = left + i * (right - left) / Math.Max(1, columns - 1);
                            result.Pixels[destination++] = row[x * 4];
                            result.Pixels[destination++] = row[x * 4 + 1];
                            result.Pixels[destination++] = row[x * 4 + 2];
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                for (int y = 0; y < bitmap.Height; y++)
                {
                    long energy = 0;
                    int start = y * columns * 3;
                    for (int x = 1; x < columns; x++)
                        for (int c = 0; c < 3; c++)
                        {
                            energy += Math.Abs(result.Pixels[start + x * 3 + c] - result.Pixels[start + (x - 1) * 3 + c]);
                            if (y > 0) energy += Math.Abs(result.Pixels[start + x * 3 + c] - result.Pixels[start + x * 3 + c - columns * 3]);
                        }
                    result.Energy[y] = (double)energy / (Math.Max(1, columns - 1) * 6);
                }
                return result;
            }
        }
    }
}
