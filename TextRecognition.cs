using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SnipStudio
{
    public sealed class TextWordRegion
    {
        public string Text { get; set; }
        public RectangleF Bounds { get; set; }
        internal TextWordFootprint Footprint { get; set; }
    }

    public sealed class TextLineRegion
    {
        public string Text { get; set; }
        public RectangleF Bounds { get; set; }
        // A proven neighboring graphic stays protected even when erase/color
        // sampling padding would otherwise extend beyond the text bounds.
        public RectangleF ExcludedGraphicBounds { get; set; }
        // Empty means the whole source image. A cropped OCR pass cannot prove
        // that adjacent pixels outside its recognition copy are safe to erase.
        public RectangleF RecognitionClipBounds { get; set; }
        public List<TextWordRegion> Words { get; private set; }

        public TextLineRegion() { Words = new List<TextWordRegion>(); }
    }

    public sealed class TextRecognitionResult
    {
        public bool Available { get; set; }
        public string Message { get; set; }
        public string Text { get; set; }
        // Native OCR output is retained even when a proven graphical icon is
        // excluded from editable text regions. This is not corrected spelling.
        public string RawText { get; set; }
        // Verbatim native output of bounded recovery passes. The initial raw
        // output is never replaced by a merged or corrected representation.
        public string RecoveryRawText { get; internal set; }
        public int RecoveryPasses { get; internal set; }
        internal bool HasUncertainClippedText { get; set; }
        public string Language { get; set; }
        public double? TextAngle { get; set; }
        public bool WasResized { get; set; }
        public List<TextLineRegion> Lines { get; private set; }
        // Standalone icon lines can be dropped from editable text. Keep their
        // positions regardless, including when OCR recovery merges other rows.
        internal List<RectangleF> ProtectedGraphics { get; private set; }

        public TextRecognitionResult()
        {
            Text = "";
            Message = "";
            Language = "";
            RecoveryRawText = "";
            Lines = new List<TextLineRegion>();
            ProtectedGraphics = new List<RectangleF>();
        }
    }

    public static class TextRecognition
    {
        // OCR is local. Serial execution avoids competing Windows recognizers and
        // limits the number of native bitmap buffers being processed at once.
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static Task<TextRecognitionResult> RecognizeAsync(Bitmap image)
        {
            return RecognizeAsync(image, CancellationToken.None);
        }

        public static Task<TextRecognitionResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken)
        {
            if (image == null) throw new ArgumentNullException("image");
            // Snapshot before returning: the caller retains ownership of its input
            // and may dispose or replace it while this request runs.
            Bitmap snapshot;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
            }
            catch (OperationCanceledException)
            {
                TaskCompletionSource<TextRecognitionResult> canceled = new TaskCompletionSource<TextRecognitionResult>();
                canceled.SetCanceled();
                return canceled.Task;
            }
            catch (Exception ex)
            {
                return Task.FromResult(Unavailable("Text recognition could not prepare this image. " + ex.Message));
            }

            return Task.Run<TextRecognitionResult>(async delegate
            {
                using (snapshot)
                {
                    bool entered = false;
                    try
                    {
                        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        entered = true;
                        return await RecognizeCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return Unavailable("Local text recognition is unavailable for this image. " + ex.Message);
                    }
                    finally { if (entered) Gate.Release(); }
                }
            });
        }

        private static async Task<TextRecognitionResult> RecognizeCoreAsync(Bitmap snapshot, CancellationToken cancellationToken)
        {
            OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                foreach (Windows.Globalization.Language language in OcrEngine.AvailableRecognizerLanguages)
                {
                    engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine != null) break;
                }
            }
            if (engine == null)
                return Unavailable("No Windows OCR language is available. Install an OCR-capable Windows language pack to use text recognition. Freehand highlighting remains available.");

            int maxDimension = (int)OcrEngine.MaxImageDimension;
            if (maxDimension < 1) return Unavailable("Windows reported no supported OCR image size. Freehand highlighting remains available.");
            System.Drawing.Size target = PreparedSize(snapshot.Size, maxDimension);
            Rectangle sourceBounds = new Rectangle(System.Drawing.Point.Empty, snapshot.Size);
            TextRecognitionResult recognition = null;
            bool recoveryTimedOut = false;
            int recoveredLines = 0;
            // The initial pass and both optional recovery passes share one
            // budget. Cancellation never turns into a successful stale result.
            using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    recognition = await RecognizePassAsync(engine, snapshot, sourceBounds, target, budget.Token).ConfigureAwait(false);
                    RecognizedTextRegions.Refine(recognition, snapshot);
                    bool enlarged = target.Width > snapshot.Width && target.Height > snapshot.Height;
                    if (!recognition.TextAngle.HasValue || Math.Abs(recognition.TextAngle.Value) <= 2)
                    {
                        TextRecognitionResult native = recognition;
                        if (enlarged)
                        {
                            budget.Token.ThrowIfCancellationRequested();
                            native = await RecognizePassAsync(engine, snapshot, sourceBounds, snapshot.Size, budget.Token).ConfigureAwait(false);
                            recognition.RecoveryPasses++;
                            recognition.RecoveryRawText = native.RawText ?? "";
                            RecognizedTextRegions.Refine(native, snapshot);
                            KeepProtectedGraphics(recognition, native);
                            recoveredLines += RecognizedTextRecovery.MergeMissing(recognition, native, sourceBounds, budget.Token);
                        }
                        // Repeated proven icons can make Windows omit an entire
                        // neighboring row even on a large capture. Repeat a
                        // proven column at original resolution within budget.
                        Rectangle column = RecognizedTextRecovery.IconFreeColumnCrop(native, snapshot.Size);
                        if (!column.IsEmpty && column.Width <= maxDimension && column.Height <= maxDimension)
                        {
                            budget.Token.ThrowIfCancellationRequested();
                            TextRecognitionResult pane = await RecognizePassAsync(engine, snapshot, column, column.Size, budget.Token).ConfigureAwait(false);
                            recognition.RecoveryPasses++;
                            if (recognition.RecoveryRawText.Length > 0) recognition.RecoveryRawText += Environment.NewLine;
                            recognition.RecoveryRawText += pane.RawText ?? "";
                            RecognizedTextRegions.Refine(pane, snapshot);
                            KeepProtectedGraphics(recognition, pane);
                            recoveredLines += RecognizedTextRecovery.MergeMissing(recognition, pane, sourceBounds, budget.Token);
                        }
                        // Recovery may supply an icon guess in an already
                        // protected location. Reconcile the final editable rows
                        // with the same raster checks, leaving both raw passes intact.
                        if (recognition.RecoveryPasses > 0) RecognizedTextRegions.Refine(recognition, snapshot);
                        foreach (RecognizedTextRegions.JoinedGraphicPrefix prefix in
                            RecognizedTextRegions.FindJoinedGraphicPrefixes(recognition, snapshot))
                        {
                            budget.Token.ThrowIfCancellationRequested();
                            System.Drawing.Size cropSize = PreparedSize(prefix.CropBounds.Size, maxDimension);
                            TextRecognitionResult reread = await RecognizePassAsync(engine, snapshot,
                                prefix.CropBounds, cropSize, budget.Token).ConfigureAwait(false);
                            recognition.RecoveryPasses++;
                            if (recognition.RecoveryRawText.Length > 0) recognition.RecoveryRawText += Environment.NewLine;
                            recognition.RecoveryRawText += reread.RawText ?? "";
                            if (RecognizedTextRegions.ApplyJoinedGraphicPrefix(recognition, prefix, reread))
                                recoveredLines++;
                        }
                        var sparse = RecognizedTextRecovery.FindSparseColumnRecovery(recognition, snapshot.Size, budget.Token,
                            reviewExisting: true);
                        if (sparse != null)
                        {
                            recoveredLines += await RecoverSparseLabelsAsync(engine, snapshot, recognition, sparse,
                                sourceBounds, maxDimension, budget.Token).ConfigureAwait(false);
                            recoveredLines += await ReviewSparseLabelsAsync(engine, snapshot, recognition, sparse,
                                sourceBounds, maxDimension, budget.Token).ConfigureAwait(false);
                        }
                        else if (target == snapshot.Size)
                        {
                            recoveredLines += await ReviewInteriorRowAsync(engine, snapshot, recognition,
                                maxDimension, budget.Token).ConfigureAwait(false);
                        }
#if NET10_0_OR_GREATER
                        recoveredLines += await OcrTimestampRecovery.RecoverAsync(snapshot, recognition, maxDimension,
                            (crop, size, token) => RecognizePassAsync(engine, snapshot, crop, size, token), budget.Token).ConfigureAwait(false);
                        recoveredLines += await OcrSplitColonRecovery.RecoverAsync(snapshot, recognition, maxDimension,
                            (crop, size, token) => RecognizePassAsync(engine, snapshot, crop, size, token), budget.Token).ConfigureAwait(false);
#endif
                        recoveredLines += await RecoverColoredCaretAsync(snapshot, recognition,
                            (image, token) => RecognizePassAsync(engine, image, sourceBounds, target, token), budget.Token).ConfigureAwait(false);
                        recoveredLines += RecognizedTextRecovery.RecoverSpacedFilenameExtensions(recognition, snapshot, budget.Token);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (recognition == null)
                        return Unavailable("Local text recognition took too long. Try a smaller snip, or use freehand highlighting.");
                    recoveryTimedOut = true;
                }
            }
            recognition.Message = recognition.Lines.Count == 0 ? "No text was detected. Freehand highlighting is available."
                : "Text ready — " + recognition.Language + ".";
            if (recoveredLines > 0) recognition.Message += " An additional recognition pass recovered text. Check identifiers against the image.";
            if (recoveryTimedOut) recognition.Message += " Additional recognition timed out; some text may be missing.";
            if (recognition.TextAngle.HasValue && Math.Abs(recognition.TextAngle.Value) > 2)
                recognition.Message += " Angled text may need freehand highlighting.";
            return recognition;
        }

        private static void KeepProtectedGraphics(TextRecognitionResult target, TextRecognitionResult source)
        {
            foreach (RectangleF bounds in source.ProtectedGraphics)
            {
                if (target.ProtectedGraphics.Count >= 512) break;
                if (!target.ProtectedGraphics.Contains(bounds)) target.ProtectedGraphics.Add(bounds);
            }
        }

        // A one-pixel colored insertion caret can be reported as a trailing
        // digit. Require both the source caret geometry and an exact reread of
        // the otherwise identical line before changing the editable words.
        internal static async Task<int> RecoverColoredCaretAsync(Bitmap source, TextRecognitionResult primary,
            Func<Bitmap, CancellationToken, Task<TextRecognitionResult>> reread, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!primary.Available || primary.Lines.Count > 512 || primary.ProtectedGraphics.Count >= 512 ||
                !(Math.Abs(primary.TextAngle.GetValueOrDefault()) <= .1)) return 0;

            TextLineRegion line = null;
            Rectangle caret = Rectangle.Empty;
            Color background = Color.Empty;
            foreach (TextLineRegion candidate in primary.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (!TryFindColoredCaret(source, candidate, out Rectangle candidateCaret, out Color candidateBackground)) continue;
                if (line != null || primary.ProtectedGraphics.Exists(bounds => bounds.IntersectsWith(candidateCaret)))
                    return 0;
                line = candidate;
                caret = candidateCaret;
                background = candidateBackground;
            }
            if (line == null) return 0;

            TextRecognitionResult alternative;
            using (Bitmap clean = source.Clone(new Rectangle(System.Drawing.Point.Empty, source.Size), PixelFormat.Format32bppArgb))
            {
                for (int y = caret.Top; y < caret.Bottom; y++) clean.SetPixel(caret.Left, y, background);
                alternative = await reread(clean, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            RecordRecoveryRead(primary, alternative);
            if (!alternative.Available || !(Math.Abs(alternative.TextAngle.GetValueOrDefault()) <= .1)) return 0;

            int last = line.Words.Count - 1;
            TextWordRegion original = line.Words[last];
            string expectedText = line.Text.Substring(0, line.Text.Length - 1);
            string expectedNumber = original.Text.Substring(0, original.Text.Length - 1);
            TextWordRegion corrected = null;
            foreach (TextLineRegion candidate in alternative.Lines)
            {
                if (candidate.Text != expectedText || candidate.Words.Count != line.Words.Count) continue;
                TextWordRegion number = candidate.Words[last];
                if (number.Text != expectedNumber || number.Bounds.Right > caret.Left ||
                    Math.Abs(number.Bounds.Left - original.Bounds.Left) > 2 ||
                    !number.Bounds.IntersectsWith(original.Bounds)) continue;
                bool matching = true;
                for (int index = 0; index < last; index++)
                {
                    TextWordRegion before = line.Words[index];
                    TextWordRegion after = candidate.Words[index];
                    if (before.Text != after.Text || !before.Bounds.IntersectsWith(after.Bounds))
                    {
                        matching = false;
                        break;
                    }
                }
                if (!matching) continue;
                if (corrected != null) return 0;
                corrected = number;
            }
            if (corrected == null) return 0;

            line.Words[last] = corrected;
            line.Bounds = line.Words[0].Bounds;
            for (int index = 1; index < line.Words.Count; index++)
                line.Bounds = RectangleF.Union(line.Bounds, line.Words[index].Bounds);
            line.Text = expectedText;
            primary.ProtectedGraphics.Add(caret);
            var rows = new List<string>(primary.Lines.Count);
            foreach (TextLineRegion row in primary.Lines) rows.Add(row.Text);
            primary.Text = String.Join(Environment.NewLine, rows);
            return 1;
        }

        private static bool TryFindColoredCaret(Bitmap source, TextLineRegion line, out Rectangle caret, out Color background)
        {
            caret = Rectangle.Empty;
            background = Color.Empty;
            if (line.Words.Count < 2 || String.IsNullOrEmpty(line.Text)) return false;
            TextWordRegion number = line.Words[line.Words.Count - 1];
            RectangleF left = line.Words[line.Words.Count - 2].Bounds;
            RectangleF digits = number.Bounds;
            if (String.IsNullOrEmpty(number.Text) || number.Text.Length < 3 || number.Text.Length > 32 ||
                number.Text[number.Text.Length - 1] != '1' || !line.Text.EndsWith(number.Text, StringComparison.Ordinal) ||
                !(left.Height >= 6 && left.Height <= 48 && left.Right <= digits.Left &&
                    digits.Height >= 1.3f * left.Height && digits.Height <= 2.5f * left.Height) ||
                digits.Top > left.Top - 1 || digits.Bottom < left.Bottom + 1) return false;
            foreach (char character in number.Text)
                if (character < '0' || character > '9') return false;

            int middle = (int)Math.Floor(left.Top + left.Height / 2);
            int firstY = Math.Max(1, (int)Math.Floor(digits.Top) - 1);
            int lastY = Math.Min(source.Height - 2, (int)Math.Ceiling(digits.Bottom));
            if (middle < firstY || middle > lastY) return false;
            int firstX = Math.Max(1, (int)Math.Floor(digits.Right) - 3);
            int lastX = Math.Min(source.Width - 3, (int)Math.Floor(digits.Right));
            for (int x = firstX; x <= lastX; x++)
            {
                Color mark = source.GetPixel(x, middle);
                Color blank = source.GetPixel(x + 1, middle);
                if (mark.A != 255 || blank.A != 255 || ColorSpread(mark) < 140 || ColorSpread(blank) > 48) continue;
                int markArgb = mark.ToArgb(), blankArgb = blank.ToArgb();
                int top = middle, bottom = middle + 1;
                while (top > firstY && source.GetPixel(x, top - 1).ToArgb() == markArgb) top--;
                while (bottom <= lastY && source.GetPixel(x, bottom).ToArgb() == markArgb) bottom++;
                // The bar extends above and below the glyphs. Its right side is
                // two flat background columns, and its two ends have no ink to
                // the left. This excludes colored strokes confined to glyphs.
                if (bottom - top < Math.Max(12, 1.3f * left.Height) || bottom - top > 2.5f * left.Height ||
                    top > left.Top - 1 || bottom < left.Bottom + 1 ||
                    source.GetPixel(x, top - 1).ToArgb() == markArgb || source.GetPixel(x, bottom).ToArgb() == markArgb)
                    continue;
                bool isolated = true;
                for (int y = top; y < bottom; y++)
                {
                    if (source.GetPixel(x + 1, y).ToArgb() != blankArgb ||
                        source.GetPixel(x + 2, y).ToArgb() != blankArgb ||
                        ((y < left.Top - 1 || y > left.Bottom + 1) && source.GetPixel(x - 1, y).ToArgb() != blankArgb))
                    {
                        isolated = false;
                        break;
                    }
                }
                if (!isolated) continue;
                if (!caret.IsEmpty) return false;
                caret = new Rectangle(x, top, 1, bottom - top);
                background = blank;
            }
            return !caret.IsEmpty;
        }

        private static int ColorSpread(Color color) =>
            Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));

        private static async Task<int> ReviewInteriorRowAsync(OcrEngine engine, Bitmap snapshot,
            TextRecognitionResult primary, int maximumDimension, CancellationToken token)
        {
            var plan = RecognizedTextRecovery.FindInteriorRowRecovery(primary, snapshot.Size, token);
            if (plan == null || plan.WideCrop.Width > maximumDimension || plan.WideCrop.Height > maximumDimension) return 0;
            if (!TryReadOpaqueRightMargin(snapshot, plan.NarrowCrop.Right, token, out int background))
            {
                plan = ExpandInteriorRowCrops(snapshot, plan, maximumDimension, token, out background);
                if (plan == null) return 0;
            }

            // These two views keep the original pixel scale and differ only in
            // how much proven blank right margin is retained for the provider.
            TextRecognitionResult narrow = await RecognizePassAsync(engine, snapshot, plan.NarrowCrop,
                plan.NarrowCrop.Size, token).ConfigureAwait(false);
            RecordRecoveryRead(primary, narrow);
            RecognizedTextRegions.Refine(narrow, snapshot);
            TextRecognitionResult wide = await RecognizePassAsync(engine, snapshot, plan.WideCrop,
                plan.WideCrop.Size, token).ConfigureAwait(false);
            RecordRecoveryRead(primary, wide);
            RecognizedTextRegions.Refine(wide, snapshot);
            Func<RectangleF, bool> hasSourceInk = bounds => HasInteriorWordInk(snapshot, bounds, background, token);
            int merged = RecognizedTextRecovery.MergeInteriorRowRecovery(primary, narrow, wide, plan, hasSourceInk, token);
            if (merged != 0 || !RecognizedTextRecovery.TryChooseInteriorRetry(primary, narrow, wide, plan,
                hasSourceInk, token, out bool keepWide)) return merged;

            // Use at most one additional width. Its removed pixels are already
            // covered by the exact blank-margin proof for the narrow view.
            Rectangle middle = plan.NarrowCrop;
            middle.Width += (plan.WideCrop.Width - middle.Width) / 2;
            TextRecognitionResult retry = await RecognizePassAsync(engine, snapshot, middle, middle.Size, token).ConfigureAwait(false);
            RecordRecoveryRead(primary, retry);
            RecognizedTextRegions.Refine(retry, snapshot);
            var retryPlan = new RecognizedTextRecovery.InteriorRowRecovery(plan.FirstRunIndex, plan.RunCount, plan.WordSlot,
                keepWide ? middle : plan.NarrowCrop, keepWide ? plan.WideCrop : middle);
            return RecognizedTextRecovery.MergeInteriorRowRecovery(primary, keepWide ? retry : narrow,
                keepWide ? wide : retry, retryPlan, hasSourceInk, token);
        }

        internal static RecognizedTextRecovery.InteriorRowRecovery ExpandInteriorRowCrops(Bitmap snapshot,
            RecognizedTextRecovery.InteriorRowRecovery plan, int maximumDimension, CancellationToken token, out int background)
        {
            token.ThrowIfCancellationRequested();
            background = 0;
            Rectangle narrow = plan.NarrowCrop;
            Rectangle wide = plan.WideCrop;
            int separation = wide.Width - narrow.Width;
            if (narrow.X != 0 || narrow.Y != 0 || narrow.Height != snapshot.Height || narrow.Width < 1 ||
                wide.X != 0 || wide.Y != 0 || wide.Height != snapshot.Height || wide.Width >= snapshot.Width ||
                wide.Width > maximumDimension || wide.Height > maximumDimension || separation < 1 ||
                (long)snapshot.Width * snapshot.Height > 2_000_000) return null;

            // Leave the same width spacing between the views and reserve a
            // blank suffix beyond both. A nearby graphic can stay in both
            // views if it leaves enough space for two distinct crops.
            int latestNarrow = Math.Min(narrow.Width + 2 * separation,
                Math.Min(snapshot.Width - 2 * separation, maximumDimension - separation));
            if (latestNarrow <= narrow.Width) return null;

            Rectangle margin = Rectangle.FromLTRB(narrow.Right, 0, snapshot.Width, snapshot.Height);
            byte[] bytes = ReadPixels(snapshot, margin);
            int edge = (margin.Width - 1) * 4;
            if (bytes[edge + 3] != 255) return null;
            int opaque = BitConverter.ToInt32(bytes, edge);
            int suffix = 0;
            for (int y = 0; y < margin.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                int row = y * margin.Width * 4;
                for (int x = margin.Width - 1; x >= suffix; x--)
                {
                    if (BitConverter.ToInt32(bytes, row + x * 4) == opaque) continue;
                    suffix = x + 1;
                    if (margin.Left + suffix > latestNarrow) return null;
                    break;
                }
            }

            const int widthStep = 32;
            int extra = (suffix + widthStep - 1) / widthStep * widthStep;
            if (narrow.Width + extra > latestNarrow) return null;
            narrow.Width += extra;
            wide.Width += extra;
            background = opaque;
            return new RecognizedTextRecovery.InteriorRowRecovery(plan.FirstRunIndex, plan.RunCount, plan.WordSlot, narrow, wide);
        }

        internal static bool TryReadOpaqueRightMargin(Bitmap snapshot, int left, CancellationToken token, out int background)
        {
            token.ThrowIfCancellationRequested();
            background = 0;
            if (left < 0 || left >= snapshot.Width || (long)snapshot.Width * snapshot.Height > 2_000_000) return false;
            Rectangle margin = Rectangle.FromLTRB(left, 0, snapshot.Width, snapshot.Height);
            byte[] bytes = ReadPixels(snapshot, margin);
            if (bytes[3] != 255) return false;
            background = BitConverter.ToInt32(bytes, 0);
            for (int offset = 4; offset < bytes.Length; offset += 4)
            {
                if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
                if (BitConverter.ToInt32(bytes, offset) != background) return false;
            }
            return true;
        }

        internal static bool HasInteriorWordInk(Bitmap snapshot, RectangleF bounds, int background, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Rectangle area = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
            if (!new Rectangle(System.Drawing.Point.Empty, snapshot.Size).Contains(area) || area.Width < 12 || area.Width > 256 ||
                area.Height < 4 || area.Height > 64) return false;
            byte[] bytes = ReadPixels(snapshot, area);
            var inkColumns = new bool[area.Width];
            int inkPixels = 0;
            for (int y = 0; y < area.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < area.Width; x++)
                {
                    int offset = (y * area.Width + x) * 4;
                    if (bytes[offset + 3] != 255) return false;
                    int b = bytes[offset], g = bytes[offset + 1], r = bytes[offset + 2];
                    if (Math.Abs(b - (background & 255)) + Math.Abs(g - ((background >> 8) & 255)) +
                        Math.Abs(r - ((background >> 16) & 255)) < 120) continue;
                    // Restrict this optional recovery to ordinary neutral text
                    // silhouettes. Do not turn colored artwork into a label.
                    if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) return false;
                    inkColumns[x] = true;
                    inkPixels++;
                }
            }
            int runs = 0;
            for (int x = 0; x < inkColumns.Length; x++)
                if (inkColumns[x] && (x == 0 || !inkColumns[x - 1])) runs++;
            return runs >= 3 && inkPixels >= area.Width && inkPixels < area.Width * area.Height / 2;
        }

        private static async Task<int> RecoverSparseLabelsAsync(OcrEngine engine, Bitmap snapshot,
            TextRecognitionResult primary, RecognizedTextRecovery.SparseColumnRecovery plan,
            Rectangle sourceBounds, int maximumDimension, CancellationToken token)
        {
            var candidate = new TextRecognitionResult { Available = true, TextAngle = primary.TextAngle };
            int count = Math.Min(24, plan.MissingRows.Count);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                Rectangle slot = Rectangle.Intersect(sourceBounds, Rectangle.Truncate(plan.MissingRows[index]));
                int left = Math.Max(slot.Left, (int)Math.Floor(plan.LabelLeft - plan.LeftTolerance));
                Rectangle area = Rectangle.FromLTRB(left, slot.Top, slot.Right, slot.Bottom);
                Rectangle ink;
                if (!TryReadSparseLabelInk(snapshot, area, out ink) ||
                    Math.Abs(ink.Left - plan.LabelLeft) > plan.LeftTolerance) continue;
                Rectangle crop = Rectangle.FromLTRB(Math.Max(area.Left, ink.Left - 2), area.Top,
                    Math.Min(area.Right, ink.Right + 2), area.Bottom);
                string first = await ReadRepeatedLabelAsync(engine, snapshot, crop, 2, 5, maximumDimension,
                    primary, token).ConfigureAwait(false);
                if (first == null) continue;
                string second = await ReadRepeatedLabelAsync(engine, snapshot, crop, 3, 8, maximumDimension,
                    primary, token).ConfigureAwait(false);
                if (!String.Equals(first, second, StringComparison.Ordinal)) continue;

                var line = new TextLineRegion { Text = first, Bounds = ink, RecognitionClipBounds = slot };
                line.Words.Add(new TextWordRegion { Text = first, Bounds = ink });
                candidate.Lines.Add(line);
            }

            return RecognizedTextRecovery.MergeSparseColumnRecovery(primary, candidate, plan, sourceBounds, token);
        }

        private static async Task<int> ReviewSparseLabelsAsync(OcrEngine engine, Bitmap snapshot,
            TextRecognitionResult primary, RecognizedTextRecovery.SparseColumnRecovery plan,
            Rectangle sourceBounds, int maximumDimension, CancellationToken token)
        {
            var candidate = new TextRecognitionResult { Available = true, TextAngle = primary.TextAngle };
            int count = Math.Min(24, plan.ReviewCells.Count);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var cell = plan.ReviewCells[index];
                Rectangle slot = Rectangle.Intersect(sourceBounds, Rectangle.Truncate(cell.Bounds));
                Rectangle ink;
                if (!TryReadSparseLabelInk(snapshot, slot, out ink, 240) ||
                    Math.Abs(ink.Left - plan.LabelLeft) > plan.LeftTolerance) continue;
                // Some ClearType filenames are stable only with enough
                // whitespace for the recognizer to establish the line. The
                // wider read may include blank margin beyond the slot, but
                // its ink must be exactly the same isolated label.
                Rectangle context = Rectangle.Intersect(sourceBounds, Rectangle.FromLTRB(slot.Left - 3,
                    slot.Top - 4, slot.Right, slot.Bottom + 4));
                int left = ink.Left - 5;
                Rectangle crop = Rectangle.Intersect(context, Rectangle.FromLTRB(left, ink.Top - 5,
                    Math.Max(ink.Right + 16, left + 160), ink.Bottom + 7));
                Rectangle contextInk;
                if (!TryReadSparseLabelInk(snapshot, crop, out contextInk, 240) || contextInk != ink)
                    crop = Rectangle.FromLTRB(Math.Max(slot.Left, ink.Left - 4), slot.Top,
                        Math.Min(slot.Right, ink.Right + Math.Max(12, ink.Height)), slot.Bottom);
                byte[] pixels = ReadPixels(snapshot, crop);
                string first = await ReadContrastLabelAsync(engine, crop, ink, pixels, false, maximumDimension,
                    primary, token).ConfigureAwait(false);
                if (first == null || (cell.Runs.Count == 1 && String.Equals(first, cell.Runs[0].Text, StringComparison.Ordinal)))
                    continue;
                string second = await ReadContrastLabelAsync(engine, crop, ink, pixels, true, maximumDimension,
                    primary, token).ConfigureAwait(false);
                if (!String.Equals(first, second, StringComparison.Ordinal)) continue;

                var line = new TextLineRegion { Text = first, Bounds = ink, RecognitionClipBounds = slot };
                line.Words.Add(new TextWordRegion { Text = first, Bounds = ink });
                candidate.Lines.Add(line);
            }

            return RecognizedTextRecovery.MergeSparseColumnReview(primary, candidate, plan, sourceBounds, token);
        }

        private static async Task<string> ReadContrastLabelAsync(OcrEngine engine, Rectangle crop, Rectangle ink,
            byte[] pixels, bool minimumChannel, int maximumDimension, TextRecognitionResult primary, CancellationToken token)
        {
            const int padding = 6;
            const int scale = 2;
            var frame = new Rectangle(0, 0, (crop.Width + 2 * padding) * scale, (crop.Height + 2 * padding) * scale);
            if (frame.Width > maximumDimension || frame.Height > maximumDimension) return null;
            using (var gray = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb))
            using (var prepared = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb))
            {
                // ClearType can turn adjacent strokes into another character at
                // OCR scale. Render two monochrome readings of the same pixels.
                int stride = crop.Width * 4;
                var row = new byte[stride];
                var data = gray.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < crop.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = 0; x < stride; x += 4)
                        {
                            int offset = y * stride + x;
                            int blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
                            int grayLevel = minimumChannel ? Math.Min(red, Math.Min(green, blue)) :
                                (21 * red + 72 * green + 7 * blue + 50) / 100;
                            byte level = (byte)((grayLevel * pixels[offset + 3] + 255 * (255 - pixels[offset + 3]) + 127) / 255);
                            row[x] = row[x + 1] = row[x + 2] = level;
                            row[x + 3] = 255;
                        }
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                    }
                }
                finally { gray.UnlockBits(data); }

                using (Graphics graphics = Graphics.FromImage(prepared))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(gray, new Rectangle(padding * scale, padding * scale, crop.Width * scale,
                        crop.Height * scale), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
                }

                TextRecognitionResult read = await RecognizePassAsync(engine, prepared, frame,
                    PreparedSize(frame.Size, maximumDimension), token).ConfigureAwait(false);
                RecordRecoveryRead(primary, read);
                if (read.Lines.Count != 1 || read.Lines[0].Words.Count != 1) return null;
                TextLineRegion line = read.Lines[0];
                TextWordRegion word = line.Words[0];
                if (!String.Equals(line.Text, word.Text, StringComparison.Ordinal) || !IsSparseLabel(word.Text, 96)) return null;
                var observed = new RectangleF(crop.Left + word.Bounds.Left / scale - padding,
                    crop.Top + word.Bounds.Top / scale - padding, word.Bounds.Width / scale, word.Bounds.Height / scale);
                var allowed = (RectangleF)crop;
                allowed.Inflate(1, 1);
                if (!allowed.Contains(observed)) return null;
                observed.Inflate(3, 2);
                return observed.Contains(ink) ? word.Text : null;
            }
        }

        private static bool TryReadSparseLabelInk(Bitmap snapshot, Rectangle area, out Rectangle ink, int maximumInkWidth = 96)
        {
            ink = Rectangle.Empty;
            if (area.Width < 4 || area.Width > 768 || area.Height < 5 || area.Height > 72) return false;
            byte[] pixels = ReadPixels(snapshot, area);
            int left = area.Width, right = 0, top = area.Height, bottom = 0, light = 0, marks = 0;
            for (int y = 0; y < area.Height; y++)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    int offset = (y * area.Width + x) * 4;
                    if (pixels[offset + 3] < 250) continue;
                    int level = (21 * pixels[offset + 2] + 72 * pixels[offset + 1] + 7 * pixels[offset]) / 100;
                    if (level >= 232) { light++; continue; }
                    if (level >= 175) continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x + 1);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y + 1);
                    marks++;
                }
            }

            // A light, short label with clear space at the boundaries can be
            // read independently. Do not infer a word from a grid, edge or icon.
            if (light * 4 < area.Width * area.Height * 3 || marks < 8 ||
                left == 0 || top == 0 || right == area.Width || bottom == area.Height ||
                right - left < 4 || right - left > maximumInkWidth || bottom - top < 4 || bottom - top > 24)
                return false;
            ink = Rectangle.FromLTRB(area.Left + left, area.Top + top, area.Left + right, area.Top + bottom);
            return true;
        }

        private static async Task<string> ReadRepeatedLabelAsync(OcrEngine engine, Bitmap snapshot, Rectangle crop,
            int scale, int gap, int maximumDimension, TextRecognitionResult primary, CancellationToken token)
        {
            const int copies = 3;
            int stride = crop.Width + gap;
            Rectangle frame = new Rectangle(0, 0, (stride * copies + 16) * scale, (crop.Height + 12) * scale);
            if (frame.Width > maximumDimension || frame.Height > maximumDimension) return null;
            var placements = new RectangleF[copies];
            using (var repeated = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb))
            {
                // Windows sometimes drops a short word on its own. Repeating
                // only the same source pixels lets the recognizer read the run
                // without introducing example characters or a dictionary guess.
                using (Graphics graphics = Graphics.FromImage(repeated))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    for (int index = 0; index < copies; index++)
                    {
                        Rectangle placement = new Rectangle((8 + stride * index) * scale, 6 * scale,
                            crop.Width * scale, crop.Height * scale);
                        graphics.DrawImage(snapshot, placement, crop, GraphicsUnit.Pixel);
                        placements[index] = placement;
                        placements[index].Inflate(scale * 1.5f, scale * 1.5f);
                    }
                }

                TextRecognitionResult read = await RecognizePassAsync(engine, repeated, frame,
                    PreparedSize(frame.Size, maximumDimension), token).ConfigureAwait(false);
                RecordRecoveryRead(primary, read);

                int covered = 0;
                string value = null;
                foreach (TextLineRegion line in read.Lines)
                {
                    foreach (TextWordRegion word in line.Words)
                    {
                        int copy = Array.FindIndex(placements, placement => placement.Contains(word.Bounds));
                        if (copy < 0 || (covered & (1 << copy)) != 0 || !IsSparseLabel(word.Text) ||
                            (value != null && !String.Equals(value, word.Text, StringComparison.Ordinal))) return null;
                        covered |= 1 << copy;
                        value = word.Text;
                    }
                }
                return covered == (1 << copies) - 1 ? value : null;
            }
        }

        private static void RecordRecoveryRead(TextRecognitionResult primary, TextRecognitionResult read)
        {
            primary.RecoveryPasses++;
            if (String.IsNullOrEmpty(read.RawText)) return;
            if (primary.RecoveryRawText.Length > 0) primary.RecoveryRawText += Environment.NewLine;
            primary.RecoveryRawText += read.RawText;
        }

        private static bool IsSparseLabel(string text, int maximumLength = 24)
        {
            if (String.IsNullOrEmpty(text) || text.Length < 2 || text.Length > maximumLength) return false;
            bool lettersOrDigits = false;
            foreach (char character in text)
            {
                if (Char.IsControl(character) || Char.IsWhiteSpace(character)) return false;
                lettersOrDigits |= Char.IsLetterOrDigit(character);
            }
            return lettersOrDigits;
        }

        private static async Task<TextRecognitionResult> RecognizePassAsync(OcrEngine engine, Bitmap snapshot,
            Rectangle sourceBounds, System.Drawing.Size target, CancellationToken cancellationToken)
        {
            int targetWidth = target.Width;
            int targetHeight = target.Height;
            bool resized = target != sourceBounds.Size;

            using (Bitmap prepared = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(prepared))
                {
                    // Transparent freeform areas become white for recognition only;
                    // no OCR preprocessing is written back to the screenshot.
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = resized ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(snapshot, new Rectangle(0, 0, targetWidth, targetHeight),
                        sourceBounds, GraphicsUnit.Pixel);
                }
                cancellationToken.ThrowIfCancellationRequested();
                byte[] pixels = ReadPixels(prepared);
                using (DataWriter writer = new DataWriter())
                {
                    writer.WriteBytes(pixels);
                    using (SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
                        targetWidth, targetHeight, BitmapAlphaMode.Ignore))
                    {
                        IAsyncOperation<OcrResult> operation = engine.RecognizeAsync(bitmap);
                        OcrResult result = await AwaitRecognition(operation, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        TextRecognitionResult recognition = new TextRecognitionResult();
                        recognition.Available = true;
                        recognition.Language = engine.RecognizerLanguage.LanguageTag;
                        recognition.TextAngle = result.TextAngle;
                        // This describes the initial pass on the returned result;
                        // recovery reads retain original source coordinates.
                        recognition.WasResized = resized;
                        // Preserve recognized text verbatim; OCR is not a rewrite or
                        // verification of business identifiers or numeric values.
                        recognition.Text = result.Text ?? "";
                        recognition.RawText = recognition.Text;
                        foreach (OcrLine sourceLine in result.Lines)
                        {
                            TextLineRegion line = new TextLineRegion();
                            if (sourceBounds != new Rectangle(System.Drawing.Point.Empty, snapshot.Size))
                                line.RecognitionClipBounds = sourceBounds;
                            line.Text = sourceLine.Text ?? "";
                            bool haveBounds = false;
                            foreach (OcrWord sourceWord in sourceLine.Words)
                            {
                                Windows.Foundation.Rect rect = sourceWord.BoundingRect;
                                TextWordFootprint footprint;
                                RectangleF bounds = SourceWordGeometry(rect.X, rect.Y, rect.Width, rect.Height,
                                    target, sourceBounds, recognition.TextAngle, true, out footprint);
                                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                                line.Words.Add(new TextWordRegion { Text = sourceWord.Text ?? "", Bounds = bounds, Footprint = footprint });
                                line.Bounds = haveBounds ? RectangleF.Union(line.Bounds, bounds) : bounds;
                                haveBounds = true;
                            }
                            if (line.Words.Count > 0) recognition.Lines.Add(line);
                        }
                        return recognition;
                    }
                }
            }
        }

        internal static RectangleF SourceWordBounds(double x, double y, double width, double height,
            System.Drawing.Size prepared, Rectangle source, double? angle)
        {
            TextWordFootprint unused;
            return SourceWordGeometry(x, y, width, height, prepared, source, angle, false, out unused);
        }

        internal static RectangleF SourceWordGeometry(double x, double y, double width, double height,
            System.Drawing.Size prepared, Rectangle source, double? angle, bool retainFootprint,
            out TextWordFootprint footprint)
        {
            footprint = null;
            if (width <= 0 || height <= 0) return RectangleF.Empty;
            double scaleX = (double)source.Width / prepared.Width, scaleY = (double)source.Height / prepared.Height;
            double degrees = angle.GetValueOrDefault();
            if (Math.Abs(degrees) > .001 && Math.Abs(degrees) <= 180)
            {
                // WinRT bounding rectangles are in the deskewed frame. Rotate
                // about the recognition input center before mapping the crop
                // back to source pixels. Enclose all four rotated corners.
                double radians = degrees * Math.PI / 180, cosine = Math.Cos(radians), sine = Math.Sin(radians);
                double centerX = prepared.Width / 2.0, centerY = prepared.Height / 2.0;
                double localX = x + width / 2 - centerX, localY = y + height / 2 - centerY;
                double mappedX = centerX + localX * cosine - localY * sine;
                double mappedY = centerY + localX * sine + localY * cosine;
                // Keep the actual projected word for tilted hit testing. The
                // public box stays unchanged for display and horizontal editing.
                if (retainFootprint && Math.Abs(degrees) > 2)
                {
                    double halfWidthX = width * cosine / 2, halfWidthY = width * sine / 2;
                    double halfHeightX = -height * sine / 2, halfHeightY = height * cosine / 2;
                    footprint = TextWordFootprint.Create(
                        MapSourcePoint(mappedX - halfWidthX - halfHeightX, mappedY - halfWidthY - halfHeightY, source, scaleX, scaleY),
                        MapSourcePoint(mappedX + halfWidthX - halfHeightX, mappedY + halfWidthY - halfHeightY, source, scaleX, scaleY),
                        MapSourcePoint(mappedX + halfWidthX + halfHeightX, mappedY + halfWidthY + halfHeightY, source, scaleX, scaleY),
                        MapSourcePoint(mappedX - halfWidthX + halfHeightX, mappedY - halfWidthY + halfHeightY, source, scaleX, scaleY), source);
                }
                double enclosingWidth = Math.Abs(cosine * width) + Math.Abs(sine * height);
                double enclosingHeight = Math.Abs(sine * width) + Math.Abs(cosine * height);
                x = mappedX - enclosingWidth / 2;
                y = mappedY - enclosingHeight / 2;
                width = enclosingWidth;
                height = enclosingHeight;
            }
            var mapped = new RectangleF((float)(source.Left + x * scaleX), (float)(source.Top + y * scaleY),
                (float)(width * scaleX), (float)(height * scaleY));
            return RectangleF.Intersect(mapped, source);
        }

        private static PointF MapSourcePoint(double x, double y, Rectangle source, double scaleX, double scaleY)
        {
            return new PointF((float)(source.Left + x * scaleX), (float)(source.Top + y * scaleY));
        }

        internal static System.Drawing.Size PreparedSize(System.Drawing.Size source, int maximumDimension)
        {
            if (source.Width < 1 || source.Height < 1) throw new ArgumentOutOfRangeException("source");
            if (maximumDimension < 1) throw new ArgumentOutOfRangeException("maximumDimension");
            // Small screenshots often contain glyphs too short for Windows OCR.
            // Enlarge only the recognition copy, in one bounded pass; never
            // rewrite a guessed identifier or resize captured/exported pixels.
            bool small = source.Width <= 1024 && source.Height <= 512 &&
                (long)source.Width * source.Height <= 524288;
            double scale = Math.Min(small ? 2.0 : 1.0,
                Math.Min((double)maximumDimension / source.Width, (double)maximumDimension / source.Height));
            return new System.Drawing.Size(Math.Max(1, (int)Math.Floor(source.Width * scale)),
                Math.Max(1, (int)Math.Floor(source.Height * scale)));
        }

        private static byte[] ReadPixels(Bitmap bitmap)
        {
            return ReadPixels(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        private static byte[] ReadPixels(Bitmap bitmap, Rectangle area)
        {
            int rowBytes = checked(area.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * area.Height)];
            BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < area.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }
            finally { bitmap.UnlockBits(data); }
            return pixels;
        }

        private static async Task<OcrResult> AwaitRecognition(IAsyncOperation<OcrResult> operation, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                operation.Completed = delegate(IAsyncOperation<OcrResult> completed, AsyncStatus status)
                {
                    try
                    {
                        if (status == AsyncStatus.Completed) completion.TrySetResult(completed.GetResults());
                        else if (status == AsyncStatus.Canceled) completion.TrySetCanceled();
                        else completion.TrySetException(completed.ErrorCode ?? new InvalidOperationException("Windows OCR failed."));
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                };

                // Register after installing Completed because registration
                // can invoke Cancel immediately for an already canceled token.
                // Dispose outside Completed: a provider can wait for that
                // callback to return before its Cancel method returns.
                using (cancellationToken.Register(delegate
                {
                    try { operation.Cancel(); } catch (Exception) { }
                }))
                    return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                try { operation.Close(); } catch (Exception) { }
            }
        }

        private static TextRecognitionResult Unavailable(string message)
        {
            return new TextRecognitionResult { Available = false, Message = message };
        }
    }
}
