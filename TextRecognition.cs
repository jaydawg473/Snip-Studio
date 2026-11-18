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
        public string Language { get; set; }
        public double? TextAngle { get; set; }
        public bool WasResized { get; set; }
        public List<TextLineRegion> Lines { get; private set; }

        public TextRecognitionResult()
        {
            Text = "";
            Message = "";
            Language = "";
            RecoveryRawText = "";
            Lines = new List<TextLineRegion>();
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
                    if (enlarged && (!recognition.TextAngle.HasValue || Math.Abs(recognition.TextAngle.Value) <= 2))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        TextRecognitionResult native = await RecognizePassAsync(engine, snapshot, sourceBounds, snapshot.Size, budget.Token).ConfigureAwait(false);
                        recognition.RecoveryPasses++;
                        recognition.RecoveryRawText = native.RawText ?? "";
                        RecognizedTextRegions.Refine(native, snapshot);
                        RecognizedTextRecovery.MergeMissing(recognition, native, sourceBounds, budget.Token);
                        // Repeated proven icons can make Windows omit an entire
                        // neighboring row. One measured original-size retry
                        // excludes that icon column, while safe geometric merge
                        // keeps all already-read text elsewhere in the image.
                        Rectangle column = RecognizedTextRecovery.IconFreeColumnCrop(native, snapshot.Size);
                        if (!column.IsEmpty)
                        {
                            budget.Token.ThrowIfCancellationRequested();
                            TextRecognitionResult pane = await RecognizePassAsync(engine, snapshot, column, column.Size, budget.Token).ConfigureAwait(false);
                            recognition.RecoveryPasses++;
                            recognition.RecoveryRawText += Environment.NewLine + (pane.RawText ?? "");
                            RecognizedTextRegions.Refine(pane, snapshot);
                            RecognizedTextRecovery.MergeMissing(recognition, pane, sourceBounds, budget.Token);
                        }
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
            if (recoveryTimedOut) recognition.Message += " Additional recognition timed out; some text may be missing.";
            if (recognition.TextAngle.HasValue && Math.Abs(recognition.TextAngle.Value) > 2)
                recognition.Message += " Angled text may need freehand highlighting.";
            return recognition;
        }

        private static async Task<TextRecognitionResult> RecognizePassAsync(OcrEngine engine, Bitmap snapshot,
            Rectangle sourceBounds, System.Drawing.Size target, CancellationToken cancellationToken)
        {
            int targetWidth = target.Width;
            int targetHeight = target.Height;
            bool resized = target != sourceBounds.Size;
            double scaleX = (double)sourceBounds.Width / targetWidth;
            double scaleY = (double)sourceBounds.Height / targetHeight;

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
                                RectangleF bounds = new RectangleF((float)(sourceBounds.Left + rect.X * scaleX), (float)(sourceBounds.Top + rect.Y * scaleY),
                                    (float)(rect.Width * scaleX), (float)(rect.Height * scaleY));
                                bounds = RectangleF.Intersect(bounds, new RectangleF(0, 0, snapshot.Width, snapshot.Height));
                                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                                line.Words.Add(new TextWordRegion { Text = sourceWord.Text ?? "", Bounds = bounds });
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
            int rowBytes = checked(bitmap.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * bitmap.Height)];
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }
            finally { bitmap.UnlockBits(data); }
            return pixels;
        }

        private static Task<OcrResult> AwaitRecognition(IAsyncOperation<OcrResult> operation, CancellationToken cancellationToken)
        {
            TaskCompletionSource<OcrResult> completion = new TaskCompletionSource<OcrResult>();
            CancellationTokenRegistration registration = cancellationToken.Register(delegate
            {
                try { operation.Cancel(); } catch (Exception) { }
            });
            operation.Completed = delegate(IAsyncOperation<OcrResult> completed, AsyncStatus status)
            {
                try
                {
                    if (status == AsyncStatus.Completed) completion.TrySetResult(completed.GetResults());
                    else if (status == AsyncStatus.Canceled) completion.TrySetCanceled();
                    else completion.TrySetException(completed.ErrorCode ?? new InvalidOperationException("Windows OCR failed."));
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    // Cleanup must not escape a native async callback and bring
                    // down the editor after a result has already been delivered.
                    try { registration.Dispose(); } catch (Exception) { }
                    try { completed.Close(); } catch (Exception) { }
                }
            };
            return completion.Task;
        }

        private static TextRecognitionResult Unavailable(string message)
        {
            return new TextRecognitionResult { Available = false, Message = message };
        }
    }
}
