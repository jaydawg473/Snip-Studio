using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        internal ReadOnlyCollection<SourceTextCandidate> Candidates { get; private set; }
        internal ReadOnlyCollection<SourceTextCandidate> Models { get; private set; }
        internal SourceTextFamilyContext FamilyContext { get; private set; }

        internal SourceTextStyle(string family, float size, FontStyle style, double confidence, string description)
            : this(family, size, style, confidence, description, null) { }

        internal SourceTextStyle(string family, float size, FontStyle style, double confidence, string description,
            IEnumerable<SourceTextCandidate> candidates)
            : this(family, size, style, confidence, description, candidates, null, null) { }

        internal SourceTextStyle(string family, float size, FontStyle style, double confidence, string description,
            IEnumerable<SourceTextCandidate> candidates, IEnumerable<SourceTextCandidate> models, SourceTextFamilyContext context)
        {
            FontFamily = family;
            FontSizePixels = size;
            Style = style;
            Confidence = confidence;
            Description = description;
            Candidates = new List<SourceTextCandidate>(candidates ?? new SourceTextCandidate[0]).AsReadOnly();
            Models = new List<SourceTextCandidate>(models ?? new SourceTextCandidate[0]).AsReadOnly();
            FamilyContext = context;
        }
    }

    internal sealed class SourceTextCandidate
    {
        internal readonly string Family;
        internal readonly float Size;
        internal readonly FontStyle Style;
        internal readonly double Score;
        internal readonly bool Grows, SlantMismatch, StrokeMismatch;
        internal SourceTextCandidate(string family, float size, FontStyle style)
            : this(family, size, style, 0, false, false, false) { }
        internal SourceTextCandidate(string family, float size, FontStyle style, double score, bool grows, bool mismatch, bool strokeMismatch)
        { Family = family; Size = size; Style = style; Score = score; Grows = grows; SlantMismatch = mismatch; StrokeMismatch = strokeMismatch; }
    }

    public sealed class SourceTextFamilySample
    {
        public Rectangle Bounds { get; private set; }
        public string Text { get; private set; }
        public Color Foreground { get; private set; }
        public Color Background { get; private set; }
        public SourceTextFamilySample(Rectangle bounds, string text, Color foreground, Color background)
        { Bounds = bounds; Text = text; Foreground = foreground; Background = background; }
    }

    // A family-only raster prior; it contains no bitmap or editable line state.
    // EvidenceGap is a score difference, never a probability of exact recovery.
    public sealed class SourceTextFamilyContext
    {
        public string PreferredFamily { get; private set; }
        public int SampleCount { get; private set; }
        public double EvidenceGap { get; private set; }
        internal SourceTextFamilyContext(string family, int count, double gap)
        { PreferredFamily = family; SampleCount = count; EvidenceGap = gap; }
        internal double PriorFor(string family)
        {
            return String.Equals(family, PreferredFamily, StringComparison.OrdinalIgnoreCase) ?
                Math.Min(.06, .02 + Math.Max(0, EvidenceGap)) * Math.Min(1, SampleCount / 2.0) : 0;
        }
    }

    // This work is deliberately on-demand. Do not run it during startup,
    // screenshot capture, painting, zooming, or pointer movement.
    public static class SourceTextMatcher
    {
        private const int MaximumCachedStyles = 128;
        private const int MaximumTextLength = 160;
        private const float TemplateFontSize = 32;
        private static readonly string[] Families = { "Segoe UI", "Segoe UI Variable Small", "Segoe UI Variable Text", "Segoe UI Variable Display",
            "Arial", "Calibri", "Aptos", "Tahoma", "Verdana", "Times New Roman", "Cambria", "Consolas", "Courier New" };
        private static readonly FontStyle[] Styles = { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic };
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, SourceTextStyle> Cache = new Dictionary<string, SourceTextStyle>(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new Queue<string>();

        public static SourceTextStyle Estimate(Bitmap source, Rectangle bounds, string text, Color foreground, Color background, CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException("source");
            cancellationToken.ThrowIfCancellationRequested();
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
            List<Match> matches = new List<Match>();
            double runnerUp = Double.NegativeInfinity;
            foreach (string familyName in Families)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                        if (!family.IsStyleAvailable(style)) continue;
                        Match candidate = Compare(family, style, text, original, observed, normalizedWidth, normalizedHeight, cancellationToken);
                        if (candidate == null) continue;
                        matches.Add(candidate);
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
            // A 32px normalized template can rank a small-font style well while
            // extrapolating the wrong physical size. Validate every available
            // model at three nearby actual pixel sizes before the native
            // shortlist is formed. This remains background-only GDI work:
            // at most 52 templates plus 156 actual-size comparisons.
            Match physicalBest = null;
            foreach (Match candidate in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompareActualSizes(candidate, text, original, foreground, background, cancellationToken);
                if (Double.IsNegativeInfinity(candidate.ActualScore)) continue;
                if (BetterActualFit(candidate, physicalBest)) physicalBest = candidate;
            }
            if (physicalBest != null) best = physicalBest;
            double margin = Double.IsNegativeInfinity(runnerUp) ? 0 : Math.Max(0, best.Score - runnerUp);
            // Similar families can have almost identical glyphs. Deliberately
            // conservative confidence keeps this an adjustable starting point.
            double confidence = Math.Max(.1, Math.Min(.9, best.Score * .65 + margin * 1.5));
            SourceTextStyle result = new SourceTextStyle(best.Family, best.Size, best.Style, confidence,
                "Estimated source style: " + best.Family + StyleLabel(best.Style) + ", " + best.Size.ToString("0.#", CultureInfo.InvariantCulture) + " px. Adjust if needed.",
                Shortlist(best, matches), AllModels(matches), null);
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

        public static SourceTextFamilyContext EstimateFamilyContext(Bitmap source, IEnumerable<SourceTextFamilySample> samples, CancellationToken token)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (samples == null) throw new ArgumentNullException("samples");
            token.ThrowIfCancellationRequested();
            Dictionary<string, double> totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int attempted = 0, accepted = 0;
            foreach (SourceTextFamilySample sample in samples)
            {
                if (attempted++ >= 3) break;
                token.ThrowIfCancellationRequested();
                if (sample == null) continue;
                SourceTextStyle estimate = Estimate(source, sample.Bounds, sample.Text, sample.Foreground, sample.Background, token);
                if (estimate.Models.Count == 0) continue;
                Dictionary<string, SourceTextCandidate> familyBest = new Dictionary<string, SourceTextCandidate>(StringComparer.OrdinalIgnoreCase);
                foreach (SourceTextCandidate model in estimate.Models)
                {
                    SourceTextCandidate current;
                    if (!familyBest.TryGetValue(model.Family, out current) || BetterModel(model, current)) familyBest[model.Family] = model;
                }
                accepted++;
                foreach (KeyValuePair<string, SourceTextCandidate> item in familyBest)
                {
                    double total; int count;
                    totals.TryGetValue(item.Key, out total); counts.TryGetValue(item.Key, out count);
                    // Unsupported slant/growth must not gain a consensus vote
                    // merely by covering a long or misrecognized string.
                    totals[item.Key] = total + item.Value.Score - (item.Value.Grows ? .15 : 0) - (item.Value.SlantMismatch ? .15 : 0);
                    counts[item.Key] = count + 1;
                }
            }
            token.ThrowIfCancellationRequested();
            if (accepted == 0) return null;
            string bestFamily = null;
            double bestScore = Double.NegativeInfinity, second = Double.NegativeInfinity;
            foreach (string family in Families)
            {
                double total;
                if (!totals.TryGetValue(family, out total) || counts[family] != accepted) continue;
                double score = total / accepted;
                if (score > bestScore) { second = bestScore; bestScore = score; bestFamily = family; }
                else second = Math.Max(second, score);
            }
            return bestFamily == null ? null : new SourceTextFamilyContext(bestFamily, accepted,
                Double.IsNegativeInfinity(second) ? 0 : Math.Max(0, bestScore - second));
        }

        public static SourceTextStyle EstimateWithFamilyContext(Bitmap source, Rectangle bounds, string text,
            Color foreground, Color background, CancellationToken token, SourceTextFamilyContext context)
        {
            SourceTextStyle line = Estimate(source, bounds, text, foreground, background, token);
            if (context == null || line.Models.Count == 0) return line;
            // Retain independently supported alternatives. A document family
            // hint must not evict a better third-family line model.
            List<SourceTextCandidate> candidates = new List<SourceTextCandidate>(line.Candidates);
            foreach (FontStyle style in Styles)
                foreach (SourceTextCandidate model in line.Models)
                    if (model.Style == style && String.Equals(model.Family, context.PreferredFamily, StringComparison.OrdinalIgnoreCase))
                    {
                        bool present = false;
                        foreach (SourceTextCandidate existing in candidates)
                            if (existing.Family == model.Family && existing.Style == model.Style) { present = true; break; }
                        if (!present && candidates.Count < 12) candidates.Add(model);
                        break;
                    }
            return new SourceTextStyle(line.FontFamily, line.FontSizePixels, line.Style, line.Confidence, line.Description,
                candidates, line.Models, context);
        }

        private static bool BetterModel(SourceTextCandidate candidate, SourceTextCandidate current)
        {
            if (candidate.Grows != current.Grows) return !candidate.Grows;
            if (candidate.SlantMismatch != current.SlantMismatch) return !candidate.SlantMismatch;
            return candidate.Score > current.Score;
        }

        private static IEnumerable<SourceTextCandidate> AllModels(List<Match> matches)
        {
            List<SourceTextCandidate> models = new List<SourceTextCandidate>();
            foreach (Match match in matches)
                if (!Double.IsNegativeInfinity(match.ActualScore)) models.Add(new SourceTextCandidate(match.Family, match.Size,
                    match.Style, match.ActualScore, match.ActualGrows, match.ActualSlantMismatch, match.ActualStrokeMismatch));
            return models;
        }

        private static void CompareActualSizes(Match match, string text, Coverage original, Color foreground, Color background, CancellationToken token)
        {
            try
            {
                using (FontFamily family = new FontFamily(match.Family))
                using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
                using (SolidBrush brush = new SolidBrush(foreground))
                {
                    format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                    format.Trimming = StringTrimming.None;
                    float center = (float)Math.Round(match.Size);
                    float[] sizes = { center, center - 1, center + 1 };
                    SourceTextCandidate model = new SourceTextCandidate(match.Family, match.Size, match.Style);
                    NativeMatch best = null;
                    foreach (float size in sizes)
                    {
                        token.ThrowIfCancellationRequested();
                        if (size < 8 || size > 160) continue;
                        using (Font font = new Font(family, size, match.Style, GraphicsUnit.Pixel))
                        {
                            SizeF measured;
                            using (Bitmap measure = new Bitmap(1, 1))
                            using (Graphics graphics = Graphics.FromImage(measure))
                                measured = graphics.MeasureString(text, font, Int32.MaxValue, format);
                            int width = (int)Math.Ceiling(measured.Width) + 20;
                            int height = (int)Math.Ceiling(font.GetHeight()) + 20;
                            if (width < 4 || width > 8192 || height < 4 || height > 512) continue;
                            using (Bitmap rendered = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                            {
                                using (Graphics graphics = Graphics.FromImage(rendered))
                                {
                                    graphics.Clear(background);
                                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                                    graphics.DrawString(text, font, brush, new PointF(8, 6), format);
                                }
                                Coverage predicted = ReadCoverage(rendered, foreground, background, token);
                                if (predicted.Ink.Width < 2 || predicted.Ink.Height < 2) continue;
                                // GDI antialiasing can have a different stroke
                                // thickness from final RichEdit output. Keep it
                                // out of the shortlist's hard stroke constraint.
                                NativeMatch comparison = PhysicalFitCore(original, predicted, size, token, false);
                                comparison.Candidate = model;
                                if (BetterNativeFit(comparison, best, original.Ink.Width)) best = comparison;
                            }
                        }
                    }
                    if (best == null) return;
                    match.Size = best.Size;
                    match.ActualScore = best.PhysicalScore;
                    match.ActualGrows = best.Grows;
                    match.ActualSlantMismatch = best.SlantMismatch;
                    match.ActualStrokeMismatch = best.StrokeMismatch;
                }
            }
            catch (ArgumentException) { }
            catch (ExternalException) { }
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

        private static IEnumerable<SourceTextCandidate> Shortlist(Match best, List<Match> matches)
        {
            List<SourceTextCandidate> result = new List<SourceTextCandidate>();
            result.Add(new SourceTextCandidate(best.Family, best.Size, best.Style));
            foreach (FontStyle style in Styles)
            {
                foreach (Match candidate in matches)
                    if (candidate.Style == style && String.Equals(candidate.Family, best.Family, StringComparison.OrdinalIgnoreCase))
                    { AddCandidate(result, candidate); break; }
            }
            // Reserve every style, including Regular when the coarse winner is
            // Bold Italic. Also retain the best other-family model per style;
            // a coarse rasterizer must not decide the final weight or slant.
            foreach (FontStyle style in Styles)
            {
                Match other = null;
                foreach (Match candidate in matches)
                    if (!Double.IsNegativeInfinity(candidate.ActualScore) && candidate.Style == style &&
                        !String.Equals(candidate.Family, best.Family, StringComparison.OrdinalIgnoreCase) &&
                        BetterActualFit(candidate, other)) other = candidate;
                AddCandidate(result, other);
            }
            return result;
        }

        private static void AddCandidate(List<SourceTextCandidate> candidates, Match match)
        {
            if (match == null || candidates.Count >= 8) return;
            foreach (SourceTextCandidate candidate in candidates)
                if (candidate.Family == match.Family && candidate.Style == match.Style) return;
            candidates.Add(new SourceTextCandidate(match.Family, match.Size, match.Style));
        }

        // The background search remains GDI-only and MTA-safe. Validate at most
        // twelve shortlisted appearances with the final RichEdit renderer on STA,
        // once per edit. Never run this from paint, typing, or pointer movement.
        internal static SourceTextStyle RefineSizeOnUi(Bitmap source, Rectangle bounds, string text,
            Color foreground, Color background, SourceTextStyle initial, CancellationToken token)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (initial == null) throw new ArgumentNullException("initial");
            token.ThrowIfCancellationRequested();
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return initial;
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), bounds);
            if (clipped.Width < 3 || clipped.Height < 3 || clipped.Width > 8192 || clipped.Height > 512 ||
                String.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength || initial.Confidence <= .1) return initial;
            try
            {
                Coverage original;
                using (Bitmap sample = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(sample))
                    { graphics.Clear(background); graphics.DrawImage(source, new Rectangle(Point.Empty, sample.Size), clipped, GraphicsUnit.Pixel); }
                    original = ReadCoverage(sample, foreground, background, token);
                }
                if (original.Ink.Width < 2 || original.Ink.Height < 3 || original.Energy < 12) return initial;
                int glyphCount = SimpleGlyphCount(text);
                Rectangle[] sourceGlyphs = glyphCount == 0 ? null : VerifiedGlyphRuns(original, glyphCount, token);
                Coverage sourceLeading = sourceGlyphs == null ? null : CropCoverage(original, sourceGlyphs[0]);
                string leadingText = null;
                if (sourceLeading != null)
                    foreach (char character in text) if (character != ' ') { leadingText = character.ToString(); break; }
                Dictionary<string, Coverage> leadingCache = new Dictionary<string, Coverage>(StringComparer.Ordinal);
                int renderWidth = Math.Min(8192, Math.Max(32, checked(clipped.Width * 2 + 32)));
                List<SourceTextCandidate> candidates = new List<SourceTextCandidate>();
                candidates.Add(new SourceTextCandidate(initial.FontFamily, initial.FontSizePixels, initial.Style));
                foreach (SourceTextCandidate candidate in initial.Candidates)
                {
                    if (candidates.Count >= 12) break;
                    if (candidate.Family == initial.FontFamily && candidate.Style == initial.Style) continue;
                    candidates.Add(candidate);
                }
                NativeMatch best = null;
                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    SourceTextCandidate candidate = candidates[candidateIndex];
                    HashSet<string> renderedRtf = new HashSet<string>(StringComparer.Ordinal);
                    float center = (float)Math.Round(candidate.Size * 2) / 2;
                    float[] sizes = { center, center - .5f, center + .5f, center - 1, center + 1 };
                    foreach (float size in sizes)
                    {
                        token.ThrowIfCancellationRequested();
                        // Complete the bounded evidence set regardless of CPU
                        // scheduling. Timed partial searches can choose different
                        // styles for identical pixels. At most sixty full-run
                        // appearances plus sixty verified leading glyphs are
                        // rendered; cancellation stays active.
                        if (size < 8 || size > 160) continue;
                    string rtf = RichTextLayout.CreatePlainRtf(text, candidate.Family, size, candidate.Style, foreground);
                    // Center is tested first and is nearest the coarse size.
                    // Canonically identical neighboring sizes add no evidence.
                    if (!renderedRtf.Add(rtf)) continue;
                    RichTextMetrics metrics = RichTextLayout.Measure(rtf, renderWidth);
                    int height = (int)Math.Ceiling(metrics.Size.Height);
                    if (height < 1 || height > 512) continue;
                    using (Bitmap rendered = new Bitmap(renderWidth, height, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics graphics = Graphics.FromImage(rendered))
                        { graphics.Clear(background); RichTextLayout.Draw(graphics, rtf, renderWidth, PointF.Empty); }
                        Coverage predicted = ReadCoverage(rendered, foreground, background, token);
                        if (predicted.Ink.Width < 2 || predicted.Ink.Height < 2) continue;
                        NativeMatch measured = PhysicalFit(original, predicted, size, token);
                        measured.Candidate = candidate;
                        if (sourceGlyphs != null)
                        {
                            Rectangle[] predictedGlyphs = VerifiedGlyphRuns(predicted, glyphCount, token);
                            if (predictedGlyphs != null)
                            {
                                measured.GlyphAgreement = GlyphAgreement(original, sourceGlyphs, predicted, predictedGlyphs, token);
                                measured.PhysicalScore = .5 * measured.PhysicalScore + .5 * measured.GlyphAgreement;
                            }
                            else
                            {
                                // A backend can join neighboring characters even
                                // when the source glyph identities are clear.
                                // Abstain from full-run correspondence, then test
                                // the same verified first character independently.
                                string glyphRtf = RichTextLayout.CreatePlainRtf(leadingText, candidate.Family, size, candidate.Style, foreground);
                                Coverage leading;
                                if (!leadingCache.TryGetValue(glyphRtf, out leading))
                                {
                                    token.ThrowIfCancellationRequested();
                                    RichTextMetrics glyphMetrics = RichTextLayout.Measure(glyphRtf, 256);
                                    int glyphHeight = (int)Math.Ceiling(glyphMetrics.Size.Height);
                                    leading = null;
                                    if (glyphHeight >= 1 && glyphHeight <= 512)
                                        using (Bitmap glyphBitmap = new Bitmap(256, glyphHeight, PixelFormat.Format24bppRgb))
                                        {
                                            using (Graphics graphics = Graphics.FromImage(glyphBitmap))
                                            { graphics.Clear(background); RichTextLayout.Draw(graphics, glyphRtf, 256, PointF.Empty); }
                                            leading = ReadCoverage(glyphBitmap, foreground, background, token);
                                        }
                                    leadingCache[glyphRtf] = leading;
                                }
                                if (leading != null && !leading.Ink.IsEmpty)
                                {
                                    measured.GlyphAgreement = PhysicalFit(sourceLeading, leading, size, token).PhysicalScore;
                                    measured.PhysicalScore = .5 * measured.PhysicalScore + .5 * measured.GlyphAgreement;
                                }
                            }
                        }
                        if (initial.FamilyContext != null) measured.PhysicalScore += initial.FamilyContext.PriorFor(candidate.Family);
                        // Score size and appearance together. Selecting a size
                        // by bounding-box geometry first can discard the model
                        // whose unstretched source-pixel glyphs actually agree.
                        if (BetterNativeFit(measured, best, original.Ink.Width)) best = measured;
                    }
                    }
                }
                token.ThrowIfCancellationRequested();
                if (best == null || best.Grows) return initial;
                return new SourceTextStyle(best.Candidate.Family, best.Size, best.Candidate.Style, initial.Confidence,
                    "Estimated source style: " + best.Candidate.Family + StyleLabel(best.Candidate.Style) + ", " +
                    best.Size.ToString("0.#", CultureInfo.InvariantCulture) + " px. Appearance compared with editor rendering; adjust if needed.");
            }
            catch (OperationCanceledException) { throw; }
            catch (ArgumentException) { return initial; }
            catch (InvalidOperationException) { return initial; }
            catch (ExternalException) { return initial; }
        }

        private static bool BetterActualFit(Match candidate, Match current)
        {
            if (current == null) return true;
            if (current.ActualGrows != candidate.ActualGrows) return !candidate.ActualGrows;
            if (current.ActualSlantMismatch != candidate.ActualSlantMismatch) return !candidate.ActualSlantMismatch;
            return candidate.ActualScore > current.ActualScore;
        }

        private static NativeMatch PhysicalFit(Coverage original, Coverage predicted, float size, CancellationToken token)
        {
            return PhysicalFitCore(original, predicted, size, token, true);
        }

        private static NativeMatch PhysicalFitCore(Coverage original, Coverage predicted, float size, CancellationToken token, bool validateStroke)
        {
            double sourceMass = PrefixInkMass(original, Math.Min(128, original.Ink.Width));
            double massRatio = PrefixInkMass(predicted, Math.Min(128, original.Ink.Width)) / Math.Max(1, sourceMass);
            int sourceSlant = SlantState(original, token), predictedSlant = SlantState(predicted, token);
            NativeMatch result = new NativeMatch
            {
                Size = size, Width = predicted.Ink.Width, Height = predicted.Ink.Height,
                Score = PrefixAgreement(original, predicted, Int32.MaxValue),
                Prefix32 = PrefixAgreement(original, predicted, 32), Prefix64 = PrefixAgreement(original, predicted, 64),
                MassError = Math.Abs(Math.Log(Math.Max(.001, massRatio))),
                // Allow one pixel of ordinary antialiasing height variation.
                Geometry = Math.Abs(Math.Log(predicted.Ink.Width / (double)original.Ink.Width)) +
                    Math.Max(0, Math.Abs(predicted.Ink.Height - original.Ink.Height) - 1) / (double)original.Ink.Height,
                Grows = predicted.Ink.Width > original.Ink.Width * 1.10 + 2 || predicted.Ink.Height > original.Ink.Height + 1
            };
            result.SlantMismatch = sourceSlant >= 0 && predictedSlant >= 0 && sourceSlant != predictedSlant;
            result.StrokeMismatch = validateStroke && StrokeMismatch(original, predicted, token);
            result.PhysicalScore = .40 * result.Score + .35 * result.Prefix32 + .25 * result.Prefix64 -
                .15 * result.Geometry - .10 * result.MassError;
            return result;
        }

        private static bool BetterNativeFit(NativeMatch candidate, NativeMatch current, int sourceWidth)
        {
            if (current == null) return true;
            if (current.Grows != candidate.Grows) return !candidate.Grows;
            if (current.SlantMismatch != candidate.SlantMismatch) return !candidate.SlantMismatch;
            if (current.StrokeMismatch != candidate.StrokeMismatch) return !candidate.StrokeMismatch;
            return candidate.PhysicalScore > current.PhysicalScore + .0001 ||
                (Math.Abs(candidate.PhysicalScore - current.PhysicalScore) <= .0001 &&
                 (candidate.Geometry < current.Geometry - .0001 ||
                  (Math.Abs(candidate.Geometry - current.Geometry) <= .0001 &&
                   Math.Abs(candidate.Size - candidate.Candidate.Size) < Math.Abs(current.Size - current.Candidate.Size))));
        }

        private sealed class NativeMatch
        {
            internal SourceTextCandidate Candidate;
            internal float Size;
            internal double Score, Geometry, MassError, Prefix32, Prefix64, PhysicalScore, GlyphAgreement;
            internal int Width, Height;
            internal bool Grows, SlantMismatch, StrokeMismatch;
        }

        private static int SimpleGlyphCount(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == ' ') continue;
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return 0;
                count++;
            }
            return count >= 4 && count <= 16 ? count : 0;
        }

        private static Coverage CropCoverage(Coverage coverage, Rectangle bounds)
        {
            byte[] values = new byte[bounds.Width * bounds.Height];
            double energy = 0;
            for (int y = 0; y < bounds.Height; y++)
            {
                Buffer.BlockCopy(coverage.Values, (bounds.Top + y) * coverage.Width + bounds.Left, values, y * bounds.Width, bounds.Width);
                for (int x = 0; x < bounds.Width; x++) energy += values[y * bounds.Width + x] / 255.0;
            }
            return new Coverage { Width = bounds.Width, Height = bounds.Height, Values = values,
                Ink = new Rectangle(0, 0, bounds.Width, bounds.Height), Energy = energy };
        }

        // Only a complete, independently separable simple run supplies glyph
        // identities. Joined/fragmented source ink abstains for the entire run.
        // Fixed-size analysis; no scaling, font-style labels or word rules.
        private static Rectangle[] VerifiedGlyphRuns(Coverage coverage, int expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Rectangle ink = coverage.Ink;
            if (expected < 4 || expected > 16 || ink.Width < 4 || ink.Width > 512 || ink.Height < 4 || ink.Height > 128) return null;
            List<Rectangle> groups = new List<Rectangle>();
            int start = -1, top = ink.Bottom, bottom = -1;
            for (int x = ink.Left; x <= ink.Right; x++)
            {
                if ((x & 31) == 0) token.ThrowIfCancellationRequested();
                bool present = false;
                if (x < ink.Right)
                    for (int y = ink.Top; y < ink.Bottom; y++)
                        if (coverage.Values[y * coverage.Width + x] >= 64)
                        { present = true; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                if (present) { if (start < 0) start = x; }
                else if (start >= 0)
                { groups.Add(Rectangle.FromLTRB(start, top, x, bottom + 1)); start = -1; top = ink.Bottom; bottom = -1; }
            }
            if (groups.Count == expected) return groups.ToArray();
            int width = ink.Width, height = ink.Height;
            bool[] seen = new bool[width * height]; int[] queue = new int[seen.Length];
            groups.Clear();
            for (int first = 0; first < seen.Length; first++)
            {
                if ((first & 1023) == 0) token.ThrowIfCancellationRequested();
                int firstY = first / width, firstX = first % width;
                if (seen[first] || coverage.Values[(ink.Top + firstY) * coverage.Width + ink.Left + firstX] < 64) continue;
                int head = 0, tail = 1, left = width, right = -1, componentTop = height, componentBottom = -1;
                queue[0] = first; seen[first] = true;
                while (head < tail)
                {
                    int index = queue[head++], y = index / width, x = index % width;
                    left = Math.Min(left, x); right = Math.Max(right, x); componentTop = Math.Min(componentTop, y); componentBottom = Math.Max(componentBottom, y);
                    for (int direction = 0; direction < 4; direction++)
                    {
                        int nx = x + (direction == 0 ? -1 : direction == 1 ? 1 : 0);
                        int ny = y + (direction == 2 ? -1 : direction == 3 ? 1 : 0);
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        int next = ny * width + nx;
                        if (seen[next] || coverage.Values[(ink.Top + ny) * coverage.Width + ink.Left + nx] < 64) continue;
                        seen[next] = true; queue[tail++] = next;
                    }
                }
                if (componentBottom - componentTop + 1 >= height * .55 && tail >= 3)
                    groups.Add(Rectangle.FromLTRB(ink.Left + left, ink.Top + componentTop, ink.Left + right + 1, ink.Top + componentBottom + 1));
            }
            if (groups.Count != expected) return null;
            groups.Sort(delegate(Rectangle a, Rectangle b) { return a.Left.CompareTo(b.Left); });
            return groups.ToArray();
        }

        private static double GlyphAgreement(Coverage original, Rectangle[] sourceGlyphs, Coverage predicted, Rectangle[] predictedGlyphs, CancellationToken token)
        {
            double intersection = 0, union = 0;
            for (int i = 0; i < sourceGlyphs.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                Rectangle a = sourceGlyphs[i], b = predictedGlyphs[i];
                for (int y = 0; y < Math.Max(a.Height, b.Height); y++)
                    for (int x = 0; x < Math.Max(a.Width, b.Width); x++)
                    {
                        int av = x < a.Width && y < a.Height ? original.Values[(a.Top + y) * original.Width + a.Left + x] : 0;
                        int bv = x < b.Width && y < b.Height ? predicted.Values[(b.Top + y) * predicted.Width + b.Left + x] : 0;
                        intersection += Math.Min(av, bv); union += Math.Max(av, bv);
                    }
            }
            return intersection / Math.Max(1, union);
        }

        // Area divided by horizontal/vertical edge variation estimates stroke
        // thickness without assigning Regular/Bold from a family name. Require
        // disagreement in both axes before this independent evidence is used.
        // Null means unknown; each bounded coverage prefix is analyzed once.
        private static bool StrokeMismatch(Coverage source, Coverage predicted, CancellationToken token)
        {
            double[] original = StrokeWidths(source, token), candidate = StrokeWidths(predicted, token);
            if (original == null || candidate == null) return false;
            double horizontal = candidate[0] / original[0], vertical = candidate[1] / original[1];
            return (horizontal < .75 && vertical < .75) || (horizontal > 4.0 / 3 && vertical > 4.0 / 3);
        }

        private static double[] StrokeWidths(Coverage coverage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (coverage.StrokeAnalyzed) return coverage.Stroke;
            int width = Math.Min(512, coverage.Ink.Width), height = coverage.Ink.Height;
            if (width < 8 || height < 6 || height > 128)
            { coverage.StrokeAnalyzed = true; return null; }
            double area = 0, horizontal = 0, vertical = 0;
            for (int y = 0; y < height; y++)
            {
                if ((y & 15) == 0) token.ThrowIfCancellationRequested();
                int offset = (coverage.Ink.Top + y) * coverage.Width + coverage.Ink.Left;
                int previous = 0;
                for (int x = 0; x < width; x++)
                {
                    int value = coverage.Values[offset + x];
                    int above = y == 0 ? 0 : coverage.Values[offset - coverage.Width + x];
                    area += value; horizontal += Math.Abs(value - previous); vertical += Math.Abs(value - above);
                    previous = value;
                }
                horizontal += previous;
            }
            int last = (coverage.Ink.Bottom - 1) * coverage.Width + coverage.Ink.Left;
            for (int x = 0; x < width; x++) vertical += coverage.Values[last + x];
            if (area >= 12 * 255 && horizontal > 1 && vertical > 1)
            {
                double x = area / horizontal, y = area / vertical;
                if (!Double.IsNaN(x) && !Double.IsInfinity(x) && !Double.IsNaN(y) && !Double.IsInfinity(y) && x > 0 && y > 0)
                    coverage.Stroke = new double[] { x, y };
            }
            coverage.StrokeAnalyzed = true;
            return coverage.Stroke;
        }

        // An image descriptor, independent of OCR spelling and FontStyle.
        // Compare signed horizontal edges between separated rows. Vertical
        // stems agree at zero shift; slanted stems agree at a nonzero shift.
        // Ambiguous evidence does not impose a slant preference. Each coverage
        // is analyzed once; two lags, <=32 row pairs, <=512 columns, <=17 shifts.
        private static int SlantState(Coverage coverage, CancellationToken token)
        {
            if (coverage.SlantAnalyzed) return coverage.Slant;
            int width = Math.Min(512, coverage.Ink.Width), height = coverage.Ink.Height;
            if (width < 8 || height < 6 || height > 128)
            { coverage.SlantAnalyzed = true; coverage.Slant = -1; return -1; }
            int agreed = -1;
            int[] lags = { Math.Max(2, height / 3), Math.Max(2, height / 2) };
            int maximumShift = Math.Min(8, Math.Max(3, height / 6));
            foreach (int lag in lags)
            {
                token.ThrowIfCancellationRequested();
                int availableRows = height - lag, rows = Math.Min(32, availableRows);
                double[] products = new double[maximumShift * 2 + 1];
                double upperEnergy = 0, lowerEnergy = 0;
                for (int row = 0; row < rows; row++)
                {
                    int y = rows < 2 ? 0 : row * (availableRows - 1) / (rows - 1);
                    int upper = (coverage.Ink.Top + y) * coverage.Width + coverage.Ink.Left;
                    int lower = (coverage.Ink.Top + y + lag) * coverage.Width + coverage.Ink.Left;
                    for (int x = 0; x < width - 1; x++)
                    {
                        int a = coverage.Values[upper + x + 1] - coverage.Values[upper + x];
                        int b = coverage.Values[lower + x + 1] - coverage.Values[lower + x];
                        upperEnergy += a * a; lowerEnergy += b * b;
                        for (int shift = -maximumShift; shift <= maximumShift; shift++)
                        {
                            int shifted = x + shift;
                            if (shifted < 0 || shifted >= width - 1) continue;
                            int other = coverage.Values[lower + shifted + 1] - coverage.Values[lower + shifted];
                            products[shift + maximumShift] += a * other;
                        }
                    }
                }
                double denominator = Math.Sqrt(upperEnergy * lowerEnergy);
                if (denominator < 1) continue;
                double zero = products[maximumShift] / denominator;
                double bestNonzero = Double.NegativeInfinity;
                for (int shift = 0; shift < products.Length; shift++)
                    if (shift != maximumShift) bestNonzero = Math.Max(bestNonzero, products[shift] / denominator);
                int state = zero >= .1 && zero - bestNonzero >= .05 ? 0 :
                    bestNonzero >= .1 && bestNonzero - zero >= .05 ? 1 : -1;
                if (state < 0) continue;
                if (agreed >= 0 && agreed != state) { agreed = -1; break; }
                agreed = state;
            }
            coverage.Slant = agreed; coverage.SlantAnalyzed = true;
            return agreed;
        }

        private static double PrefixInkMass(Coverage coverage, int width)
        {
            double mass = 0;
            int columns = Math.Min(width, coverage.Ink.Width);
            for (int y = coverage.Ink.Top; y < coverage.Ink.Bottom; y++)
                for (int x = 0; x < columns; x++) mass += coverage.Values[y * coverage.Width + coverage.Ink.Left + x];
            return mass;
        }

        private static double PrefixAgreement(Coverage original, Coverage predicted, int maximumWidth)
        {
            int width = Math.Min(maximumWidth, Math.Max(original.Ink.Width, predicted.Ink.Width));
            int height = Math.Max(original.Ink.Height, predicted.Ink.Height);
            double intersection = 0, union = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int observed = y < original.Ink.Height && x < original.Ink.Width ? original.Values[(original.Ink.Top + y) * original.Width + original.Ink.Left + x] : 0;
                    int candidate = y < predicted.Ink.Height && x < predicted.Ink.Width ?
                        predicted.Values[(predicted.Ink.Top + y) * predicted.Width + predicted.Ink.Left + x] : 0;
                    intersection += Math.Min(observed, candidate); union += Math.Max(observed, candidate);
                }
            return intersection / Math.Max(1, union);
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
            internal bool SlantAnalyzed;
            internal int Slant;
            internal bool StrokeAnalyzed;
            internal double[] Stroke;
        }
        private sealed class Match
        {
            internal string Family;
            internal FontStyle Style;
            internal float Size;
            internal double Score;
            internal double ActualScore = Double.NegativeInfinity;
            internal bool ActualGrows;
            internal bool ActualSlantMismatch;
            internal bool ActualStrokeMismatch;
        }
    }
}
