using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

internal static class OcrProbe
{
    private static int Main()
    {
        try
        {
            Console.WriteLine("Maximum: " + OcrEngine.MaxImageDimension);
            OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
            Console.WriteLine("Languages: " + OcrEngine.AvailableRecognizerLanguages.Count);
            if (engine == null) { Console.WriteLine("No engine"); return 2; }
            using (Bitmap bitmap = new Bitmap(1000, 200, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (Font font = new Font("Segoe UI", 28))
                { g.Clear(Color.White); g.DrawString("Snap highlight to these words", font, Brushes.Black, 30, 40); }
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] bytes = new byte[bitmap.Width * bitmap.Height * 4];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                bitmap.UnlockBits(data);
                using (DataWriter writer = new DataWriter())
                {
                    writer.WriteBytes(bytes);
                    using (SoftwareBitmap source = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Ignore))
                    {
                        IAsyncOperation<OcrResult> operation = engine.RecognizeAsync(source);
                        while (operation.Status == AsyncStatus.Started) Thread.Sleep(20);
                        OcrResult result = operation.GetResults();
                        Console.WriteLine(result.Text);
                        foreach (OcrLine line in result.Lines)
                            foreach (OcrWord word in line.Words) Console.WriteLine(word.Text + " " + word.BoundingRect);
                        operation.Close();
                    }
                }
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
