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
        public List<TextWordRegion> Words { get; private set; }

        public TextLineRegion() { Words = new List<TextWordRegion>(); }
    }

    public sealed class TextRecognitionResult
    {
        public bool Available { get; set; }
        public string Message { get; set; }
        public string Text { get; set; }
        public string Language { get; set; }
        public double? TextAngle { get; set; }
        public bool WasResized { get; set; }
        public List<TextLineRegion> Lines { get; private set; }

        public TextRecognitionResult()
        {
            Text = "";
            Message = "";
            Language = "";
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
            double scale = Math.Min(1.0, Math.Min((double)maxDimension / snapshot.Width, (double)maxDimension / snapshot.Height));
            int targetWidth = Math.Max(1, (int)Math.Floor(snapshot.Width * scale));
            int targetHeight = Math.Max(1, (int)Math.Floor(snapshot.Height * scale));
            double scaleX = (double)snapshot.Width / targetWidth;
            double scaleY = (double)snapshot.Height / targetHeight;

            using (Bitmap prepared = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(prepared))
                {
                    // Transparent freeform areas become white for recognition only;
                    // no OCR preprocessing is written back to the screenshot.
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(snapshot, new Rectangle(0, 0, targetWidth, targetHeight),
                        new Rectangle(0, 0, snapshot.Width, snapshot.Height), GraphicsUnit.Pixel);
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
                        OcrResult result;
                        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(30));
                            try { result = await AwaitRecognition(operation, timeout.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                return Unavailable("Local text recognition took too long. Try a smaller snip, or use freehand highlighting.");
                            }
                        }

                        TextRecognitionResult recognition = new TextRecognitionResult();
                        recognition.Available = true;
                        recognition.Language = engine.RecognizerLanguage.LanguageTag;
                        recognition.TextAngle = result.TextAngle;
                        recognition.WasResized = targetWidth != snapshot.Width || targetHeight != snapshot.Height;
                        // Preserve recognized text verbatim; OCR is not a rewrite or
                        // verification of business identifiers or numeric values.
                        recognition.Text = result.Text ?? "";
                        foreach (OcrLine sourceLine in result.Lines)
                        {
                            TextLineRegion line = new TextLineRegion();
                            line.Text = sourceLine.Text ?? "";
                            bool haveBounds = false;
                            foreach (OcrWord sourceWord in sourceLine.Words)
                            {
                                Windows.Foundation.Rect rect = sourceWord.BoundingRect;
                                RectangleF bounds = new RectangleF((float)(rect.X * scaleX), (float)(rect.Y * scaleY),
                                    (float)(rect.Width * scaleX), (float)(rect.Height * scaleY));
                                bounds = RectangleF.Intersect(bounds, new RectangleF(0, 0, snapshot.Width, snapshot.Height));
                                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                                line.Words.Add(new TextWordRegion { Text = sourceWord.Text ?? "", Bounds = bounds });
                                line.Bounds = haveBounds ? RectangleF.Union(line.Bounds, bounds) : bounds;
                                haveBounds = true;
                            }
                            if (line.Words.Count > 0) recognition.Lines.Add(line);
                        }
                        recognition.Message = recognition.Lines.Count == 0 ? "No text was detected. Freehand highlighting is available."
                            : "Text ready — " + recognition.Language + ".";
                        if (recognition.TextAngle.HasValue && Math.Abs(recognition.TextAngle.Value) > 2)
                            recognition.Message += " Angled text may need freehand highlighting.";
                        return recognition;
                    }
                }
            }
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
