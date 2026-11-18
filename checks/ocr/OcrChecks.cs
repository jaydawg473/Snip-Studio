using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio;

internal static class OcrChecks
{
    private static int checks;
    private static void Assert(bool pass, string message)
    {
        if (!pass) throw new Exception(message);
        checks++;
    }

    private static int Main()
    {
        try
        {
            Bitmap source = Card(1000, 200);
            Task<TextRecognitionResult> pending = TextRecognition.RecognizeAsync(source);
            source.Dispose();
            TextRecognitionResult recognized = pending.GetAwaiter().GetResult();
            Console.WriteLine("OCR status: " + recognized.Message);
            Console.WriteLine("OCR text: " + recognized.Text);
            Assert(recognized.Available, "Local OCR must be available on this host");
            Assert(recognized.Text.Contains("Snap") && recognized.Text.Contains("words"), "Known text should be recognized after caller input disposal");
            Assert(recognized.Lines.Count > 0 && recognized.Lines[0].Words.Count >= 5, "Word boxes must be returned");
            Assert(recognized.Lines[0].Bounds.Left >= 20 && recognized.Lines[0].Bounds.Top >= 30, "Word/line boxes must match original location");
            Assert(recognized.WasResized, "Small input is enlarged for OCR while boxes remain in original coordinates");
            using (Bitmap blank = new Bitmap(120, 80))
            {
                using (Graphics g = Graphics.FromImage(blank)) g.Clear(Color.White);
                TextRecognitionResult empty = TextRecognition.RecognizeAsync(blank).GetAwaiter().GetResult();
                Assert(empty.Available && empty.Lines.Count == 0 && empty.Text.Length == 0, "No text must remain distinct from unavailable OCR");
            }
            using (Bitmap wide = Card(10002, 220))
            {
                TextRecognitionResult resized = TextRecognition.RecognizeAsync(wide).GetAwaiter().GetResult();
                Assert(resized.Available && resized.WasResized, "Input beyond engine max must resize");
                Assert(resized.Lines.Count > 0 && resized.Lines[0].Bounds.Left >= 20 && resized.Lines[0].Bounds.Right < wide.Width, "Resized word boxes must map into original image coordinates");
            }
            using (Bitmap canceledImage = Card(1000, 200))
            using (CancellationTokenSource canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Task<TextRecognitionResult> task = TextRecognition.RecognizeAsync(canceledImage, canceled.Token);
                Assert(task.IsCanceled, "Canceled request must not start recognition");
            }
            Console.WriteLine("PASS: " + checks + " OCR checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static Bitmap Card(int width, int height)
    {
        Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(image))
        using (Font font = new Font("Segoe UI", 28))
        { g.Clear(Color.White); g.DrawString("Snap highlight to these words", font, Brushes.Black, 30, 40); }
        return image;
    }
}
