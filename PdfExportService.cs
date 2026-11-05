using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace SnipStudio
{
    // A local, lossless image-backed PDF. The caller supplies the rendered snip,
    // so annotation appearance is identical to the image save/copy paths.
    public static class PdfExportService
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static void Save(Bitmap image, string destination)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (String.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose a PDF filename.", "destination");
            string path = Path.GetFullPath(destination);
            if (!String.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a .pdf filename.", "destination");

            string temporary = Path.Combine(Path.GetDirectoryName(path), ".snip-pdf-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (Bitmap flattened = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(flattened))
                    {
                        graphics.Clear(Color.White);
                        graphics.DrawImageUnscaled(image, 0, 0);
                    }
                    using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        WritePdf(output, flattened);
                        output.Flush(true);
                    }
                }
                // Publish only after the complete PDF has been flushed and closed.
                // If a destination is locked or replacement fails, retain it intact.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                temporary = null;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void WritePdf(FileStream output, Bitmap image)
        {
            // Screenshots use a predictable 96-DPI physical page size instead of
            // trusting imported image metadata. Large images retain every pixel;
            // only their PDF page dimensions are reduced to the standard limit.
            double pointsPerPixel = Math.Min(72.0 / 96.0, 14400.0 / Math.Max(image.Width, image.Height));
            string pageWidth = (image.Width * pointsPerPixel).ToString("0.########", Invariant);
            string pageHeight = (image.Height * pointsPerPixel).ToString("0.########", Invariant);
            long[] offsets = new long[8];
            WriteAscii(output, "%PDF-1.4\n");
            byte[] binaryMarker = { 37, 226, 227, 207, 211, 10 };
            output.Write(binaryMarker, 0, binaryMarker.Length);

            BeginObject(output, offsets, 1);
            WriteAscii(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            BeginObject(output, offsets, 2);
            WriteAscii(output, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            BeginObject(output, offsets, 3);
            WriteAscii(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + pageWidth + " " + pageHeight + "] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

            BeginObject(output, offsets, 4);
            WriteAscii(output, "<< /Type /XObject /Subtype /Image /Width " + image.Width.ToString(Invariant) + " /Height " + image.Height.ToString(Invariant) + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length 6 0 R >>\nstream\n");
            long imageStart = output.Position;
            WriteImageData(output, image);
            long imageLength = output.Position - imageStart;
            WriteAscii(output, "\nendstream\nendobj\n");

            string commands = "q\n" + pageWidth + " 0 0 " + pageHeight + " 0 0 cm\n/Im0 Do\nQ\n";
            BeginObject(output, offsets, 5);
            WriteAscii(output, "<< /Length " + Encoding.ASCII.GetByteCount(commands).ToString(Invariant) + " >>\nstream\n" + commands + "endstream\nendobj\n");
            BeginObject(output, offsets, 6);
            WriteAscii(output, imageLength.ToString(Invariant) + "\nendobj\n");
            BeginObject(output, offsets, 7);
            WriteAscii(output, "<< /Producer (Snip Studio - Automation Forge) /CreationDate (D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", Invariant) + "Z) >>\nendobj\n");

            long xref = output.Position;
            if (xref > 9999999999L) throw new IOException("This PDF is too large for the supported file format.");
            WriteAscii(output, "xref\n0 8\n0000000000 65535 f \n");
            for (int i = 1; i < offsets.Length; i++)
                WriteAscii(output, offsets[i].ToString("D10", Invariant) + " 00000 n \n");
            WriteAscii(output, "trailer\n<< /Size 8 /Root 1 0 R /Info 7 0 R >>\nstartxref\n" + xref.ToString(Invariant) + "\n%%EOF\n");
        }

        private static void WriteImageData(Stream output, Bitmap image)
        {
            // .NET Framework DeflateStream emits raw RFC1951 data. PDF FlateDecode
            // expects the RFC1950 zlib wrapper: a header, deflate, and Adler-32.
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            uint adlerOne = 1, adlerTwo = 0;
            int rowBytes = checked(image.Width * 3);
            byte[] bgr = new byte[rowBytes];
            byte[] rgb = new byte[rowBytes];
            BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                using (DeflateStream compressed = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    for (int y = 0; y < image.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), bgr, 0, rowBytes);
                        for (int x = 0; x < rowBytes; x += 3)
                        {
                            rgb[x] = bgr[x + 2];
                            rgb[x + 1] = bgr[x + 1];
                            rgb[x + 2] = bgr[x];
                        }
                        compressed.Write(rgb, 0, rgb.Length);
                        UpdateAdler(rgb, ref adlerOne, ref adlerTwo);
                    }
                }
            }
            finally { image.UnlockBits(data); }
            uint checksum = (adlerTwo << 16) | adlerOne;
            output.WriteByte((byte)(checksum >> 24));
            output.WriteByte((byte)(checksum >> 16));
            output.WriteByte((byte)(checksum >> 8));
            output.WriteByte((byte)checksum);
        }

        private static void UpdateAdler(byte[] bytes, ref uint one, ref uint two)
        {
            // Bound intermediate sums so even very wide screenshot rows cannot
            // overflow before the modulus is applied.
            int start = 0;
            while (start < bytes.Length)
            {
                int end = Math.Min(bytes.Length, start + 4096);
                for (int i = start; i < end; i++) { one += bytes[i]; two += one; }
                one %= 65521;
                two %= 65521;
                start = end;
            }
        }

        private static void BeginObject(Stream output, long[] offsets, int number)
        {
            offsets[number] = output.Position;
            WriteAscii(output, number.ToString(Invariant) + " 0 obj\n");
        }

        private static void WriteAscii(Stream output, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            output.Write(bytes, 0, bytes.Length);
        }
    }
}
