using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio
{
    internal sealed class RichTextMetrics
    {
        internal SizeF Size { get; private set; }
        internal RectangleF InkBounds { get; private set; }
        internal RichTextMetrics(SizeF size, RectangleF inkBounds) { Size = size; InkBounds = inkBounds; }
    }

    // RichEdit does the paragraph/character layout once, at source-image scale.
    // Paint, zoom, and movement reuse a transparent bitmap, never a live editor.
    internal static class RichTextLayout
    {
        private const int MaximumRtfLength = 1024 * 1024;
        private const int MaximumTextLength = 16000;
        private const int MaximumDimension = 32768;
        private const long MaximumPixels = 8L * 1024 * 1024;
        private const long MaximumNativePixels = 16L * 1024 * 1024;
        private const long MaximumCacheBytes = 64L * 1024 * 1024;
        private const int EmFormatRange = 0x0439;
        private const int LogPixelsX = 88, LogPixelsY = 90;
        private static readonly object gate = new object();
        private static readonly Dictionary<LayoutKey, LayoutEntry> layouts = new Dictionary<LayoutKey, LayoutEntry>();
        private static readonly Dictionary<string, TextMetadata> metadata = new Dictionary<string, TextMetadata>(StringComparer.Ordinal);
        private static long clock, cacheBytes, metadataBytes;
        [ThreadStatic] private static RichTextBox editor;
        [ThreadStatic] private static Font editorFont;
        private static bool exitHooked;

        private sealed class LayoutEntry : IDisposable
        {
            internal Bitmap Bitmap;
            internal RichTextMetrics Metrics;
            internal long Used, Bytes;
            public void Dispose() { if (Bitmap != null) { Bitmap.Dispose(); Bitmap = null; } }
        }
        private struct LayoutKey : IEquatable<LayoutKey>
        {
            internal readonly string Rtf;
            internal readonly int Width;
            internal LayoutKey(string rtf, int width) { Rtf = rtf; Width = width; }
            public bool Equals(LayoutKey other) { return Width == other.Width && Object.ReferenceEquals(Rtf, other.Rtf); }
            public override bool Equals(object value) { return value is LayoutKey && Equals((LayoutKey)value); }
            public override int GetHashCode() { return (Rtf == null ? 0 : RuntimeHelpers.GetHashCode(Rtf)) ^ Width; }
        }
        private sealed class TextMetadata
        {
            internal string Text, Family;
            internal float Size;
            internal FontStyle Style;
            internal Color Color;
            internal long Used;
        }

        internal static string CreatePlainRtf(string text, string fontFamily, float fontSizePixels, FontStyle style, Color color)
        {
            if (text == null) text = "";
            if (text.Length > MaximumTextLength) throw new ArgumentException("Use at most 16,000 characters in one text note.", "text");
            ValidateFontSize(fontSizePixels);
            RichTextBox box = Editor();
            box.Clear();
            Font oldFont = editorFont;
            editorFont = new Font(String.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily,
                fontSizePixels, style, GraphicsUnit.Pixel);
            box.Font = editorFont;
            if (oldFont != null) oldFont.Dispose();
            box.Text = text;
            box.SelectAll();
            RichEditFormatting.ClearFormatting(box, String.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily, fontSizePixels, Color.FromArgb(255, color));
            RichEditFormatting.SetFontFamily(box, String.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily);
            RichEditFormatting.SetFontSizePixels(box, fontSizePixels);
            RichEditFormatting.SetTextColor(box, Color.FromArgb(255, color));
            RichEditFormatting.SetStyle(box, FontStyle.Bold, (style & FontStyle.Bold) != 0);
            RichEditFormatting.SetStyle(box, FontStyle.Italic, (style & FontStyle.Italic) != 0);
            RichEditFormatting.SetStyle(box, FontStyle.Underline, (style & FontStyle.Underline) != 0);
            RichEditFormatting.SetStyle(box, FontStyle.Strikeout, (style & FontStyle.Strikeout) != 0);
            string result = box.Rtf;
            RememberMetadata(result, box);
            box.ClearUndo();
            return result;
        }

        internal static string SetFontSize(string rtf, float fontSizePixels)
        {
            ValidateFontSize(fontSizePixels);
            RichTextBox box = Load(rtf);
            box.SelectAll(); RichEditFormatting.SetFontSizePixels(box, fontSizePixels);
            string result = box.Rtf; RememberMetadata(result, box); return result;
        }
        internal static string SetColor(string rtf, Color color)
        {
            RichTextBox box = Load(rtf);
            box.SelectAll(); RichEditFormatting.SetTextColor(box, Color.FromArgb(255, color));
            string result = box.Rtf; RememberMetadata(result, box); return result;
        }
        internal static string RemoveTextRange(string rtf, int start, int length)
        {
            RichTextBox box = Load(rtf);
            if (start < 0 || length < 0 || start > box.TextLength || length > box.TextLength - start)
                throw new ArgumentOutOfRangeException("start", "Choose a valid range of text.");
            box.Select(start, length); box.SelectedText = "";
            string result = box.Rtf; RememberMetadata(result, box); return result;
        }
        internal static string GetPlainText(string rtf) { return Metadata(rtf).Text; }
        internal static string GetFirstFontFamily(string rtf) { return Metadata(rtf).Family; }
        internal static FontStyle GetFirstFontStyle(string rtf) { return Metadata(rtf).Style; }
        internal static float GetFirstFontSize(string rtf) { return Metadata(rtf).Size; }
        internal static Color GetFirstColor(string rtf) { return Metadata(rtf).Color; }

        internal static RichTextMetrics Measure(string rtf, float width)
        {
            lock (gate) return Layout(rtf, width).Metrics;
        }
        internal static void Draw(Graphics graphics, string rtf, float width, PointF location)
        {
            if (graphics == null) throw new ArgumentNullException("graphics");
            lock (gate)
            {
                LayoutEntry entry = Layout(rtf, width);
                graphics.DrawImage(entry.Bitmap, new RectangleF(location, entry.Bitmap.Size),
                    new RectangleF(0, 0, entry.Bitmap.Width, entry.Bitmap.Height), GraphicsUnit.Pixel);
            }
        }

        private static LayoutEntry Layout(string rtf, float width)
        {
            if (Single.IsNaN(width) || Single.IsInfinity(width) || width < 0 || width > MaximumDimension)
                throw new ArgumentOutOfRangeException("width", "Choose a text width between 1 and 32,768 pixels.");
            int layoutWidth = width <= 0 ? MaximumDimension : Math.Max(1, (int)Math.Round(width));
            LayoutKey key = new LayoutKey(rtf, width <= 0 ? 0 : layoutWidth);
            LayoutEntry entry;
            if (layouts.TryGetValue(key, out entry)) { entry.Used = ++clock; return entry; }
            ValidateRtf(rtf);

            RichTextBox box = Load(rtf);
            int height;
            float dpiX, dpiY;
            using (Bitmap probe = new Bitmap(1, 1, PixelFormat.Format32bppRgb))
            using (Graphics graphics = Graphics.FromImage(probe))
            {
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    dpiX = Math.Max(1, GetDeviceCaps(hdc, LogPixelsX));
                    dpiY = Math.Max(1, GetDeviceCaps(hdc, LogPixelsY));
                    height = MeasureHeight(box, hdc, layoutWidth);
                }
                finally { SendMessage(box.Handle, EmFormatRange, IntPtr.Zero, IntPtr.Zero); graphics.ReleaseHdc(hdc); }
            }

            // Legacy no-wrap notes may use the large measuring width, but retain
            // only occupied pixels. Positive widths keep the user's resize box.
            int renderWidth = layoutWidth;
            if ((long)renderWidth * height > MaximumPixels)
            {
                if (width <= 0) throw new InvalidOperationException("This unwrapped note is too large. Choose a wrapping width before applying it.");
                throw new InvalidOperationException("This text note is too large to render. Make the box wider, reduce its size, or split it into separate notes.");
            }
            int nativeWidth = Math.Max(1, (int)Math.Ceiling(renderWidth * dpiX / 96f));
            int nativeHeight = Math.Max(1, (int)Math.Ceiling(height * dpiY / 96f));
            if ((long)nativeWidth * nativeHeight > MaximumNativePixels)
                throw new InvalidOperationException("This text note is too large at the current display scale. Reduce its size or split it into separate notes.");

            Bitmap transparent = null;
            try
            {
                using (Bitmap white = RenderMatte(box, layoutWidth, height, nativeWidth, nativeHeight, Color.White))
                using (Bitmap black = RenderMatte(box, layoutWidth, height, nativeWidth, nativeHeight, Color.Black))
                using (Bitmap nativeAlpha = RecoverAlpha(white, black))
                {
                    transparent = new Bitmap(renderWidth, height, PixelFormat.Format32bppArgb);
                    transparent.SetResolution(96, 96);
                    using (Graphics graphics = Graphics.FromImage(transparent))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        if (nativeAlpha.Size == transparent.Size) graphics.DrawImageUnscaled(nativeAlpha, 0, 0);
                        else
                        {
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            graphics.DrawImage(nativeAlpha, new Rectangle(0, 0, renderWidth, height), 0, 0, nativeAlpha.Width, nativeAlpha.Height, GraphicsUnit.Pixel);
                        }
                    }
                }
                Rectangle ink = AlphaBounds(transparent);
                if (width <= 0)
                {
                    int occupied = Math.Max(1, Math.Min(renderWidth, ink.Right + 2));
                    if (occupied != renderWidth)
                    {
                        Bitmap cropped = transparent.Clone(new Rectangle(0, 0, occupied, height), PixelFormat.Format32bppArgb);
                        transparent.Dispose(); transparent = cropped; renderWidth = occupied;
                    }
                }
                entry = new LayoutEntry { Bitmap = transparent, Metrics = new RichTextMetrics(new SizeF(renderWidth, height), ink),
                    Used = ++clock, Bytes = (long)renderWidth * height * 4 + rtf.Length * 2L };
                transparent = null;
                layouts.Add(key, entry); cacheBytes += entry.Bytes;
                TrimLayouts(key);
                return entry;
            }
            finally { if (transparent != null) transparent.Dispose(); }
        }

        private static int MeasureHeight(RichTextBox box, IntPtr hdc, int width)
        {
            int count = box.TextLength;
            if (count == 0) return 1;
            // RichEdit can advance past the first paragraph even when the page
            // is too short to draw it. Its next-character result alone therefore
            // cannot establish the height. Read the actual formatted rectangle.
            int usedHeight;
            int next = FormatAndMeasure(box, hdc, width, MaximumDimension, false, out usedHeight);
            if (next < count || usedHeight < 1 || usedHeight > MaximumDimension - 3)
                throw new InvalidOperationException("This text is too tall. Widen the box, reduce its size, or split the note.");
            return usedHeight + 3;
        }

        private static Bitmap RenderMatte(RichTextBox box, int sourceWidth, int sourceHeight, int width, int height, Color matte)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                box.BackColor = matte;
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(matte);
                    IntPtr hdc = graphics.GetHdc();
                    try
                    {
                        int usedHeight;
                        if (FormatAndMeasure(box, hdc, sourceWidth, sourceHeight, true, out usedHeight) < box.TextLength || usedHeight > sourceHeight)
                            throw new InvalidOperationException("The complete text did not fit its layout. Increase the note width before applying it.");
                    }
                    finally { SendMessage(box.Handle, EmFormatRange, IntPtr.Zero, IntPtr.Zero); graphics.ReleaseHdc(hdc); }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private static int Format(RichTextBox box, IntPtr hdc, int width, int height, bool render)
        {
            int usedHeight;
            return FormatAndMeasure(box, hdc, width, height, render, out usedHeight);
        }

        private static int FormatAndMeasure(RichTextBox box, IntPtr hdc, int width, int height, bool render, out int usedHeight)
        {
            FormatRange range = new FormatRange { Hdc = hdc, Target = hdc,
                Area = new NativeRect(0, 0, checked(width * 15), checked(height * 15)),
                Page = new NativeRect(0, 0, checked(width * 15), checked(height * 15)),
                Characters = new CharacterRange { First = 0, Last = -1 } };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FormatRange)));
            try
            {
                Marshal.StructureToPtr(range, memory, false);
                int next = checked((int)SendMessage(box.Handle, EmFormatRange, render ? new IntPtr(1) : IntPtr.Zero, memory).ToInt64());
                FormatRange formatted = (FormatRange)Marshal.PtrToStructure(memory, typeof(FormatRange));
                usedHeight = (int)Math.Ceiling(formatted.Area.Bottom / 15.0);
                return next;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static Bitmap RecoverAlpha(Bitmap white, Bitmap black)
        {
            Bitmap result = new Bitmap(white.Width, white.Height, PixelFormat.Format32bppArgb);
            BitmapData light = null, dark = null, output = null;
            bool complete = false;
            try
            {
                Rectangle area = new Rectangle(Point.Empty, white.Size);
                light = white.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                dark = black.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                output = result.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int bytes = checked(white.Width * 4);
                byte[] a = new byte[bytes], b = new byte[bytes], row = new byte[bytes];
                for (int y = 0; y < white.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(light.Scan0, y * light.Stride), a, 0, bytes);
                    Marshal.Copy(IntPtr.Add(dark.Scan0, y * dark.Stride), b, 0, bytes);
                    for (int x = 0; x < bytes; x += 4)
                    {
                        int delta = Math.Max(0, Math.Max(a[x] - b[x], Math.Max(a[x + 1] - b[x + 1], a[x + 2] - b[x + 2])));
                        int alpha = 255 - Math.Min(255, delta);
                        row[x + 3] = (byte)alpha;
                        for (int channel = 0; channel < 3; channel++) row[x + channel] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (b[x + channel] * 255 + alpha / 2) / alpha);
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(output.Scan0, y * output.Stride), bytes);
                }
                complete = true;
                return result;
            }
            finally
            {
                if (light != null) white.UnlockBits(light);
                if (dark != null) black.UnlockBits(dark);
                if (output != null) result.UnlockBits(output);
                if (!complete) result.Dispose();
            }
        }

        private static Rectangle AlphaBounds(Bitmap bitmap)
        {
            Rectangle area = new Rectangle(Point.Empty, bitmap.Size);
            BitmapData data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
                byte[] row = new byte[checked(bitmap.Width * 4)];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < bitmap.Width; x++) if (row[x * 4 + 3] > 0)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = y; }
                }
                return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static RichTextBox Editor()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Rich text must be laid out on the editor's UI thread.");
            if (editor == null || editor.IsDisposed)
            {
                editor = new RichTextBox { BorderStyle = BorderStyle.None, DetectUrls = false, Multiline = true,
                    ScrollBars = RichTextBoxScrollBars.None, WordWrap = true, HideSelection = true, MaxLength = MaximumTextLength,
                    BackColor = Color.White, Size = new Size(512, 512) };
                IntPtr handle = editor.Handle;
                if (!exitHooked) { Application.ApplicationExit += delegate { ClearCache(); }; exitHooked = true; }
            }
            return editor;
        }
        private static RichTextBox Load(string rtf)
        {
            ValidateRtf(rtf);
            RichTextBox box = Editor();
            box.Rtf = rtf;
            if (box.TextLength > MaximumTextLength) throw new ArgumentException("Use at most 16,000 characters in one text note.", "rtf");
            box.ClearUndo(); RememberMetadata(rtf, box); return box;
        }
        private static TextMetadata Metadata(string rtf)
        {
            ValidateRtf(rtf);
            lock (gate)
            {
                TextMetadata value;
                if (metadata.TryGetValue(rtf, out value)) { value.Used = ++clock; return value; }
                Load(rtf); return metadata[rtf];
            }
        }
        private static void RememberMetadata(string rtf, RichTextBox box)
        {
            lock (gate)
            {
                if (metadata.ContainsKey(rtf)) { metadata[rtf].Used = ++clock; return; }
                box.Select(0, box.TextLength == 0 ? 0 : 1);
                using (Font font = box.SelectionFont ?? new Font("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Point))
                {
                    metadata.Add(rtf, new TextMetadata { Text = box.Text, Family = font.FontFamily.Name,
                        Size = font.SizeInPoints * 96f / 72f, Style = font.Style, Color = box.SelectionColor, Used = ++clock });
                }
                metadataBytes += rtf.Length * 2L + box.TextLength * 2L;
                while (metadata.Count > 128 || metadataBytes > 8L * 1024 * 1024)
                {
                    string oldest = null; long used = Int64.MaxValue;
                    foreach (KeyValuePair<string, TextMetadata> pair in metadata) if (pair.Key != rtf && pair.Value.Used < used) { oldest = pair.Key; used = pair.Value.Used; }
                    if (oldest == null) break;
                    metadataBytes -= oldest.Length * 2L + metadata[oldest].Text.Length * 2L;
                    metadata.Remove(oldest);
                }
            }
        }
        private static void TrimLayouts(LayoutKey keep)
        {
            while (layouts.Count > 64 || cacheBytes > MaximumCacheBytes)
            {
                LayoutKey oldest = new LayoutKey(); bool found = false; long used = Int64.MaxValue;
                foreach (KeyValuePair<LayoutKey, LayoutEntry> pair in layouts) if (!pair.Key.Equals(keep) && pair.Value.Used < used) { oldest = pair.Key; used = pair.Value.Used; found = true; }
                if (!found) break;
                LayoutEntry value = layouts[oldest]; cacheBytes -= value.Bytes; layouts.Remove(oldest); value.Dispose();
            }
        }
        internal static void ClearCache()
        {
            lock (gate)
            {
                foreach (LayoutEntry entry in layouts.Values) entry.Dispose();
                layouts.Clear(); metadata.Clear(); cacheBytes = 0; metadataBytes = 0;
                if (editor != null) { editor.Dispose(); editor = null; }
                if (editorFont != null) { editorFont.Dispose(); editorFont = null; }
            }
        }
        internal static void ValidateRtf(string rtf)
        {
            if (String.IsNullOrWhiteSpace(rtf) || !rtf.StartsWith("{\\rtf", StringComparison.Ordinal)) throw new ArgumentException("The text formatting is not valid RTF.", "rtf");
            if (rtf.Length > MaximumRtfLength) throw new ArgumentException("This formatted text is too large for one note.", "rtf");
            for (int i = 0; i < rtf.Length; i++)
            {
                if (rtf[i] != '\\' || ++i >= rtf.Length) continue;
                if (rtf[i] == '\\' || rtf[i] == '{' || rtf[i] == '}') continue;
                int start = i;
                while (i < rtf.Length && Char.IsLetter(rtf[i])) i++;
                string word = rtf.Substring(start, i - start).ToLowerInvariant();
                if (word == "object" || word == "objdata" || word == "pict" || word == "field" || word == "filetbl" || word == "bin")
                    throw new ArgumentException("This note supports formatted text only. Paste pictures, objects, and linked fields as plain text instead.", "rtf");
                i--;
            }
        }
        private static void ValidateFontSize(float value)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 1 || value > 1024) throw new ArgumentOutOfRangeException("value");
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect
        {
            internal int Left, Top, Right, Bottom;
            internal NativeRect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        }
        [StructLayout(LayoutKind.Sequential)] private struct CharacterRange { internal int First, Last; }
        [StructLayout(LayoutKind.Sequential)] private struct FormatRange
        { internal IntPtr Hdc, Target; internal NativeRect Area, Page; internal CharacterRange Characters; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    }
}
