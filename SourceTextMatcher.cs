using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace SnipStudio
{
    // A raster estimate, not recovered font metadata. Callers should keep all
    // fields editable and must not describe Confidence as an exact-font result.
    public sealed class SourceTextStyle
    {
        public string FontFamily { get; private set; }
        public float FontSizePixels { get; private set; }
        public FontStyle Style { get; private set; }
        public double Confidence { get; private set; }
        public string Description { get; private set; }

        internal SourceTextStyle(string family, float size, FontStyle style, double confidence, string description)
        {
            FontFamily = family;
            FontSizePixels = size;
            Style = style;
            Confidence = confidence;
            Description = description;
        }
    }

    // This work is deliberately on-demand. Do not run it during startup,
    // screenshot capture, painting, zooming, or pointer movement.
    public static class SourceTextMatcher
    {
        private const int BudgetMilliseconds = 550;
        private const int MaximumCachedStyles = 128;
        private const int MaximumTextLength = 160;
        private const float TemplateFontSize = 32;
        private static readonly string[] Families = { "Segoe UI", "Arial", "Calibri", "Aptos", "Tahoma", "Verdana", "Times New Roman", "Cambria", "Consolas", "Courier New" };
        private static readonly FontStyle[] Styles = { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic };
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, SourceTextStyle> Cache = new Dictionary<string, SourceTextStyle>(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new Queue<string>();

        public static SourceTextStyle Estimate(Bitmap source, Rectangle bounds, string text, Color foreground, Color background, CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException("source");
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch elapsed = Stopwatch.StartNew();
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), bounds);
            SourceTextStyle fallback = Fallback(clipped.Height, "Source font estimated from line dimensions; choose a font if needed.");
            if (clipped.Width < 3 || clipped.Height < 3 || String.IsNullOrWhiteSpace(text)) return fallback;
            // Very short samples cannot distinguish font families reliably. Very
            // large/long lines are kept out of the shape-comparison work budget.
            if (LetterOrDigitCount(text) < 4 || text.Length > MaximumTextLength || clipped.Width > 8192 || clipped.Height > 512 || (long)clipped.Width * clipped.Height > 2000000)
                return fallback;

            double contrast = Square(foreground.R - background.R) + Square(foreground.G - background.G) + Square(foreground.B - background.B);
            if (contrast < 900) return Fallback(clipped.Height, "Source contrast is too low for font matching; font is estimated.");

            Coverage original;
            using (Bitmap sample = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(sample))
                {
                    graphics.Clear(Color.FromArgb(255, background));
                    graphics.DrawImage(source, new Rectangle(Point.Empty, sample.Size), clipped, GraphicsUnit.Pixel);
                }
                original = ReadCoverage(sample, foreground, background, cancellationToken);
            }
            if (original.Ink.Width < 2 || original.Ink.Height < 3 || original.Energy < 12) return fallback;
            int normalizedWidth = Math.Min(448, Math.Max(24, original.Ink.Width));
            int normalizedHeight = Math.Min(36, Math.Max(12, original.Ink.Height));
            byte[] observed = Normalize(original, normalizedWidth, normalizedHeight);
            string cacheKey = CacheKey(text, original.Ink.Size, normalizedWidth, normalizedHeight, observed);
            lock (CacheGate)
            {
                SourceTextStyle cached;
                if (Cache.TryGetValue(cacheKey, out cached)) return cached;
            }

            Match best = null;
            double runnerUp = Double.NegativeInfinity;
            foreach (string familyName in Families)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= BudgetMilliseconds) break;
                FontFamily family = null;
                try
                {
                    // Named FontFamily construction either resolves the installed
                    // family or fails. Do not mistake Font's silent fallback for
                    // evidence that an unavailable family matched the source.
                    family = new FontFamily(familyName);
                    if (!String.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (FontStyle style in Styles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (elapsed.ElapsedMilliseconds >= BudgetMilliseconds) break;
                        if (!family.IsStyleAvailable(style)) continue;
                        Match candidate = Compare(family, style, text, original, observed, normalizedWidth, normalizedHeight, cancellationToken);
                        if (candidate == null) continue;
                        if (best == null || candidate.Score > best.Score)
                        {
                            if (best != null) runnerUp = Math.Max(runnerUp, best.Score);
                            best = candidate;
                        }
                        else runnerUp = Math.Max(runnerUp, candidate.Score);
                    }
                }
                catch (ArgumentException) { /* A common family is not installed. */ }
                catch (ExternalException) { /* A font could not render this sample. */ }
                finally { if (family != null) family.Dispose(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (best == null || best.Score < .35) return fallback;
            double margin = Double.IsNegativeInfinity(runnerUp) ? 0 : Math.Max(0, best.Score - runnerUp);
            // Similar families can have almost identical glyphs. Deliberately
            // conservative confidence keeps this an adjustable starting point.
            double confidence = Math.Max(.1, Math.Min(.9, best.Score * .65 + margin * 1.5));
            SourceTextStyle result = new SourceTextStyle(best.Family, best.Size, best.Style, confidence,
                "Estimated source style: " + best.Family + StyleLabel(best.Style) + ", " + best.Size.ToString("0.#", CultureInfo.InvariantCulture) + " px. Adjust if needed.");
            lock (CacheGate)
            {
                if (!Cache.ContainsKey(cacheKey))
                {
                    while (Cache.Count >= MaximumCachedStyles && CacheOrder.Count > 0) Cache.Remove(CacheOrder.Dequeue());
                    Cache[cacheKey] = result;
                    CacheOrder.Enqueue(cacheKey);
                }
            }
            return result;
        }

        private static Match Compare(FontFamily family, FontStyle style, string text, Coverage original, byte[] observed, int width, int height, CancellationToken token)
        {
            using (Font font = new Font(family, TemplateFontSize, style, GraphicsUnit.Pixel))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                format.Trimming = StringTrimming.None;
                SizeF measured;
                using (Bitmap measure = new Bitmap(1, 1))
                using (Graphics graphics = Graphics.FromImage(measure)) measured = graphics.MeasureString(text, font, Int32.MaxValue, format);
                int bitmapWidth = (int)Math.Ceiling(measured.Width) + 20;
                int bitmapHeight = (int)Math.Ceiling(font.GetHeight()) + 20;
                if (bitmapWidth < 4 || bitmapWidth > 8192 || bitmapHeight > 256) return null;
                Coverage template;
                using (Bitmap bitmap = new Bitmap(bitmapWidth, bitmapHeight, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.White);
                        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        graphics.DrawString(text, font, Brushes.Black, new PointF(8, 6), format);
                    }
                    template = ReadCoverage(bitmap, Color.Black, Color.White, token);
                }
                if (template.Ink.Width < 2 || template.Ink.Height < 2) return null;
                byte[] predicted = Normalize(template, width, height);
                double overlap = SoftOverlap(observed, predicted);
                double observedRatio = original.Ink.Width / (double)original.Ink.Height;
                double predictedRatio = template.Ink.Width / (double)template.Ink.Height;
                double geometryDifference = Math.Abs(Math.Log(predictedRatio / observedRatio));
                double score = .84 * overlap + .16 * (1 - Math.Min(1, geometryDifference * 2));
                float sizeFromHeight = TemplateFontSize * original.Ink.Height / template.Ink.Height;
                float sizeFromWidth = TemplateFontSize * original.Ink.Width / template.Ink.Width;
                float size = Math.Max(8, Math.Min(160, (float)Math.Round((sizeFromHeight * .7f + sizeFromWidth * .3f) * 10) / 10f));
                return new Match { Family = family.Name, Style = style, Size = size, Score = score };
            }
        }

        private static Coverage ReadCoverage(Bitmap bitmap, Color foreground, Color background, CancellationToken token)
        {
            int width = bitmap.Width, height = bitmap.Height;
            byte[] values = new byte[checked(width * height)];
            byte[] row = new byte[checked(width * 3)];
            int left = width, top = height, right = -1, bottom = -1;
            double energy = 0;
            double red = foreground.R - background.R, green = foreground.G - background.G, blue = foreground.B - background.B;
            double denominator = Math.Max(1, red * red + green * green + blue * blue);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    if ((y & 15) == 0) token.ThrowIfCancellationRequested();
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), row, 0, row.Length);
                    int destination = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int position = x * 3;
                        double ink = ((row[position + 2] - background.R) * red + (row[position + 1] - background.G) * green + (row[position] - background.B) * blue) / denominator;
                        byte coverage = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(ink * 255)));
                        values[destination + x] = coverage;
                        energy += coverage / 255.0;
                        if (coverage < 42) continue;
                        left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            Rectangle inkBounds = right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            return new Coverage { Width = width, Height = height, Values = values, Ink = inkBounds, Energy = energy };
        }

        private static byte[] Normalize(Coverage coverage, int width, int height)
        {
            byte[] normalized = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                double sourceY = Math.Max(coverage.Ink.Top, Math.Min(coverage.Ink.Bottom - 1, coverage.Ink.Top + (y + .5) * coverage.Ink.Height / height - .5));
                int y0 = (int)Math.Floor(sourceY);
                int y1 = Math.Min(coverage.Ink.Bottom - 1, y0 + 1);
                double fy = sourceY - y0;
                for (int x = 0; x < width; x++)
                {
                    double sourceX = Math.Max(coverage.Ink.Left, Math.Min(coverage.Ink.Right - 1, coverage.Ink.Left + (x + .5) * coverage.Ink.Width / width - .5));
                    int x0 = (int)Math.Floor(sourceX);
                    int x1 = Math.Min(coverage.Ink.Right - 1, x0 + 1);
                    double fx = sourceX - x0;
                    double upper = coverage.Values[y0 * coverage.Width + x0] * (1 - fx) + coverage.Values[y0 * coverage.Width + x1] * fx;
                    double lower = coverage.Values[y1 * coverage.Width + x0] * (1 - fx) + coverage.Values[y1 * coverage.Width + x1] * fx;
                    normalized[y * width + x] = (byte)Math.Round(upper * (1 - fy) + lower * fy);
                }
            }
            return normalized;
        }

        private static double SoftOverlap(byte[] observed, byte[] predicted)
        {
            double intersection = 0, total = 0;
            for (int i = 0; i < observed.Length; i++)
            {
                intersection += Math.Min(observed[i], predicted[i]);
                total += Math.Max(observed[i], predicted[i]);
            }
            return total == 0 ? 0 : intersection / total;
        }

        private static string CacheKey(string text, Size originalSize, int width, int height, byte[] coverage)
        {
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (byte value in coverage) { hash ^= value; hash *= 1099511628211UL; } }
            return text + "\n" + originalSize.Width.ToString(CultureInfo.InvariantCulture) + ":" + originalSize.Height.ToString(CultureInfo.InvariantCulture) + ":" + width + ":" + height + ":" + hash.ToString("X16", CultureInfo.InvariantCulture);
        }
        private static SourceTextStyle Fallback(int height, string description)
        {
            return new SourceTextStyle("Segoe UI", Math.Max(8, Math.Min(160, (float)Math.Round(height * 1.2))), FontStyle.Regular, .1, description);
        }
        private static int LetterOrDigitCount(string text)
        {
            int count = 0;
            foreach (char value in text) if (Char.IsLetterOrDigit(value)) count++;
            return count;
        }
        private static double Square(double value) { return value * value; }
        private static string StyleLabel(FontStyle style)
        {
            return style == (FontStyle.Bold | FontStyle.Italic) ? " Bold Italic" : style == FontStyle.Bold ? " Bold" : style == FontStyle.Italic ? " Italic" : "";
        }
        private sealed class Coverage
        {
            internal int Width, Height;
            internal byte[] Values;
            internal Rectangle Ink;
            internal double Energy;
        }
        private sealed class Match
        {
            internal string Family;
            internal FontStyle Style;
            internal float Size;
            internal double Score;
        }
    }
}
