using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // Insertion geometry is measured once at the same source scale as the RTF
    // bitmap. Screen zoom is a projection only, never a second font layout.
    internal sealed class RichTextCaretMap
    {
        internal sealed class Row
        {
            internal int First, Last, End;
            internal float Top, Bottom, EndX;
        }

        internal readonly string Text;
        internal readonly int[] Boundaries;
        internal readonly PointF[] Points;
        internal readonly Row[] Rows;
        internal long Bytes { get { return 256L + Text.Length * 2L + Boundaries.Length * 12L + Rows.Length * 64L; } }

        internal RichTextCaretMap(string text, int[] boundaries, PointF[] points, Row[] rows)
        { Text = text; Boundaries = boundaries; Points = points; Rows = rows; }

        internal int Normalize(int index)
        {
            index = Math.Max(0, Math.Min(Text.Length, index));
            int found = Array.BinarySearch(Boundaries, index);
            return found >= 0 ? index : Boundaries[Math.Max(0, ~found - 1)];
        }

        internal PointF PointAt(int index)
        {
            int found = Array.BinarySearch(Boundaries, Normalize(index));
            return Points[Math.Max(0, found)];
        }

        internal int HitTest(PointF point)
        {
            Row chosen = Rows[0];
            float distance = Single.MaxValue;
            foreach (Row row in Rows)
            {
                float vertical = Math.Max(row.Top - point.Y, Math.Max(0, point.Y - row.Bottom));
                if (vertical < distance || (vertical == distance && point.Y >= row.Top))
                { chosen = row; distance = vertical; }
            }
            int index = Boundaries[chosen.First];
            float closest = Single.MaxValue;
            for (int item = chosen.First; item <= chosen.Last; item++)
            {
                float horizontal = Math.Abs(point.X - Points[item].X);
                if (horizontal < closest || (horizontal == closest && Boundaries[item] > index))
                { closest = horizontal; index = Boundaries[item]; }
            }
            // At an automatic wrap, the next line's first logical insertion
            // also has an affinity at the end of this rendered row.
            if (Math.Abs(point.X - chosen.EndX) < closest) index = chosen.End;
            return Normalize(index);
        }
    }

    internal static partial class RichTextLayout
    {
        private const int EmSetTargetDevice = 0x0400 + 72;
        private const int EmGetRect = 0x00B2;
        private static readonly ConditionalWeakTable<LayoutEntry, RichTextCaretMap> caretMaps = new ConditionalWeakTable<LayoutEntry, RichTextCaretMap>();

        internal static RichTextCaretMap CaretMap(string rtf, float width)
        {
            lock (gate)
            {
                LayoutEntry entry = Layout(rtf, width);
                RichTextCaretMap map;
                if (caretMaps.TryGetValue(entry, out map)) return map;
                map = BuildCaretMap(rtf, width, entry);
                caretMaps.Add(entry, map);
                entry.Bytes += map.Bytes; cacheBytes += map.Bytes;
                TrimLayouts(new LayoutKey(rtf, width <= 0 ? 0 : Math.Max(1, (int)Math.Round(width))));
                return map;
            }
        }

        private static RichTextCaretMap BuildCaretMap(string rtf, float width, LayoutEntry entry)
        {
            RichTextBox box = Load(rtf);
            string text = box.Text;
            List<int> stops = new List<int>();
            int regionalRun = 0, previousRegionalEnd = -1;
            foreach (int boundary in StringInfo.ParseCombiningCharacters(text))
            {
                // Framework StringInfo predates extended emoji ZWJ clusters.
                if (boundary > 0 && (text[boundary - 1] == '\u200D' || text[boundary] == '\u200D')) continue;
                if (boundary > 0 && text[boundary] == '\n' && text[boundary - 1] == '\r') continue;
                int scalar = ScalarAt(text, boundary);
                if (boundary > 0 && scalar >= 0x1F3FB && scalar <= 0x1F3FF) continue;
                if (scalar >= 0x1F1E6 && scalar <= 0x1F1FF)
                {
                    regionalRun = previousRegionalEnd == boundary ? regionalRun + 1 : 1;
                    previousRegionalEnd = boundary + 2;
                    if ((regionalRun & 1) == 0) continue;
                }
                else { regionalRun = 0; previousRegionalEnd = -1; }
                stops.Add(boundary);
            }
            if (stops.Count == 0 || stops[0] != 0) stops.Insert(0, 0);
            if (stops[stops.Count - 1] != text.Length) stops.Add(text.Length);
            int[] boundaries = stops.ToArray();
            PointF[] points = new PointF[boundaries.Length];
            int[] lines = new int[boundaries.Length];
            int lineWidth = width <= 0 ? MaximumDimension : Math.Max(1, (int)Math.Round(width));
            float dpi = Math.Max(96, box.DeviceDpi);
            float projection = 96f / dpi;
            Size previousSize = box.Size;
            float previousZoom = box.ZoomFactor;
            using (Bitmap scratch = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(scratch))
            {
                IntPtr hdc = graphics.GetHdc();
                IntPtr rectangle = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
                try
                {
                    box.ZoomFactor = 1;
                    box.Size = new Size(Math.Max(8, Math.Min(MaximumDimension, (int)Math.Ceiling(lineWidth / projection) + 8)),
                        Math.Max(8, Math.Min(MaximumDimension, (int)Math.Ceiling(entry.Metrics.Size.Height / projection) + 64)));
                    // EM_SETTARGETDEVICE gives the control the same device and
                    // twip line width used by EM_FORMATRANGE for this bitmap.
                    // Microsoft documents this as the WYSIWYG print-preview path.
                    if (SendMessage(box.Handle, EmSetTargetDevice, hdc, new IntPtr(checked(lineWidth * 15))) == IntPtr.Zero)
                        throw new InvalidOperationException("Text insertion geometry could not use the render device.");
                    Marshal.StructureToPtr(new NativeRect(), rectangle, false);
                    SendMessage(box.Handle, EmGetRect, IntPtr.Zero, rectangle);
                    NativeRect format = (NativeRect)Marshal.PtrToStructure(rectangle, typeof(NativeRect));
                    for (int index = 0; index < boundaries.Length; index++)
                    {
                        Point point = box.GetPositionFromCharIndex(boundaries[index]);
                        points[index] = new PointF((point.X - format.Left) * projection, (point.Y - format.Top) * projection);
                        lines[index] = box.GetLineFromCharIndex(boundaries[index]);
                    }
                }
                finally
                {
                    SendMessage(box.Handle, EmSetTargetDevice, IntPtr.Zero, new IntPtr(1));
                    SendMessage(box.Handle, EmFormatRange, IntPtr.Zero, IntPtr.Zero);
                    box.ZoomFactor = previousZoom; box.Size = previousSize;
                    Marshal.FreeHGlobal(rectangle); graphics.ReleaseHdc(hdc);
                }
            }
            List<RichTextCaretMap.Row> rows = new List<RichTextCaretMap.Row>();
            for (int first = 0; first < boundaries.Length; )
            {
                int last = first;
                float top = points[first].Y;
                while (last + 1 < boundaries.Length && lines[last + 1] == lines[first]) { last++; top = Math.Min(top, points[last].Y); }
                int end = last + 1 < boundaries.Length ? boundaries[last + 1] : text.Length;
                if (end > 0 && text[end - 1] == '\n') end--;
                float bottom = last + 1 < boundaries.Length ? points[last + 1].Y : Math.Max(entry.Metrics.Size.Height, top + GetFirstFontSize(rtf) * 1.3f);
                float endX = points[last].X;
                bool rightToLeft = points[last].X < points[first].X;
                if (end > boundaries[last]) endX = RenderedRowEdge(entry.Bitmap, top, bottom, endX, rightToLeft);
                rows.Add(new RichTextCaretMap.Row { First = first, Last = last, End = end, Top = top, Bottom = Math.Max(top + 1, bottom), EndX = endX });
                first = last + 1;
            }
            return new RichTextCaretMap(text, boundaries, points, rows.ToArray());
        }

        private static int ScalarAt(string text, int index)
        {
            return Char.IsHighSurrogate(text[index]) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]) ?
                Char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
        }

        private static float RenderedRowEdge(Bitmap image, float top, float bottom, float fallback, bool rightToLeft)
        {
            int first = Math.Max(0, (int)Math.Floor(top));
            int last = Math.Min(image.Height, (int)Math.Ceiling(bottom));
            Rectangle area = new Rectangle(0, first, image.Width, Math.Max(0, last - first));
            if (area.Height == 0) return fallback;
            BitmapData data = image.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[checked(image.Width * 4)];
                int edge = rightToLeft ? image.Width : -1;
                for (int y = 0; y < area.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    if (rightToLeft)
                    { for (int x = 0; x < edge; x++) if (row[x * 4 + 3] > 32) { edge = x; break; } }
                    else
                    { for (int x = image.Width - 1; x > edge; x--) if (row[x * 4 + 3] > 32) { edge = x; break; } }
                }
                return rightToLeft ? Math.Min(fallback, edge) : Math.Max(fallback, edge + 1);
            }
            finally { image.UnlockBits(data); }
        }
    }
}
