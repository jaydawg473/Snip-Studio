using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipStudio
{
    internal static class SourceTextColor
    {
        // Prefer the dominant observed glyph color over antialias fringes.
        // No minimum RGB contrast: pale or dark-on-dark text still has a color.
        internal static Color Estimate(Bitmap source, Color background, Color fallback)
        {
            if (source == null) throw new ArgumentNullException("source");
            Dictionary<int, int> colors = new Dictionary<int, int>();
            Dictionary<int, int> joinedChannels = new Dictionary<int, int>();
            int fringeEdges = 0;
            int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)source.Width * source.Height / 500000)));
            BitmapData data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[checked(source.Width * 4)];
                for (int y = 0; y < source.Height; y += step)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, checked(y * data.Stride)), row, 0, row.Length);
                    for (int x = 0; x < source.Width; x += step)
                    {
                        int at = x * 4;
                        if (row[at + 3] < 128) continue;
                        int rgb = (row[at + 2] << 16) | (row[at + 1] << 8) | row[at];
                        int count;
                        colors.TryGetValue(rgb, out count); colors[rgb] = count + 1;
                        // ClearType can put each channel's strongest coverage
                        // in a different horizontal pixel. Vote with the local
                        // channel envelope, without changing any source pixels.
                        int r = row[at + 2], g = row[at + 1], b = row[at];
                        for (int neighbor = Math.Max(0, x - 1); neighbor <= Math.Min(source.Width - 1, x + 1); neighbor++)
                        {
                            int other = neighbor * 4;
                            if (row[other + 3] < 128) continue;
                            int nr = row[other + 2], ng = row[other + 1], nb = row[other];
                            int dr = nr - row[at + 2], db = nb - row[at];
                            if (dr * db < 0 && Math.Abs(dr) > 24 && Math.Abs(db) > 24) fringeEdges++;
                            if (Math.Abs(nr - background.R) > Math.Abs(r - background.R)) r = nr;
                            if (Math.Abs(ng - background.G) > Math.Abs(g - background.G)) g = ng;
                            if (Math.Abs(nb - background.B) > Math.Abs(b - background.B)) b = nb;
                        }
                        int joined = (r << 16) | (g << 8) | b;
                        joinedChannels.TryGetValue(joined, out count); joinedChannels[joined] = count + 1;
                    }
                }
            }
            finally { source.UnlockBits(data); }
            // Never invent a foreground from neighboring different colors:
            // red beside blue must not become an unobserved black. A recovered
            // color needs repeated support in the unmodified source itself.
            if (fringeEdges >= 3)
            {
                int dominantObserved = 0;
                int backgroundRgb = (background.R << 16) | (background.G << 8) | background.B;
                foreach (KeyValuePair<int, int> observed in colors)
                    if (observed.Key != backgroundRgb) dominantObserved = Math.Max(dominantObserved, observed.Value);
                int minimumSupport = Math.Max(3, (int)Math.Ceiling(dominantObserved * .25));
                Dictionary<int, int> supported = new Dictionary<int, int>();
                foreach (KeyValuePair<int, int> joined in joinedChannels)
                {
                    int observed;
                    if (joined.Key != backgroundRgb && colors.TryGetValue(joined.Key, out observed) && observed >= minimumSupport) supported[joined.Key] = joined.Value;
                }
                if (supported.Count > 0) colors = supported;
            }
            Dictionary<int, ColorGroup> groups = new Dictionary<int, ColorGroup>();
            int inkSamples = 0;
            foreach (KeyValuePair<int, int> color in colors)
            {
                int r = (color.Key >> 16) & 255, g = (color.Key >> 8) & 255, b = color.Key & 255;
                int distance = Square(r - background.R) + Square(g - background.G) + Square(b - background.B);
                if (distance == 0) continue;
                int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                ColorGroup group;
                if (!groups.TryGetValue(key, out group)) { group = new ColorGroup(); groups.Add(key, group); }
                group.Count += color.Value;
                group.Weight += (long)color.Value * distance;
                inkSamples += color.Value;
                if (color.Value > group.ModeCount || (color.Value == group.ModeCount && distance > group.ModeDistance))
                { group.Mode = color.Key; group.ModeCount = color.Value; group.ModeDistance = distance; }
            }
            ColorGroup best = null;
            foreach (ColorGroup group in groups.Values)
            {
                // A stray pixel must not dominate an otherwise coherent line.
                if (inkSamples >= 8 && group.Count < 3) continue;
                if (best == null || group.Weight > best.Weight ||
                    (group.Weight == best.Weight && group.ModeCount > best.ModeCount)) best = group;
            }
            if (best == null) return Color.FromArgb(255, fallback);
            return Color.FromArgb(255, (best.Mode >> 16) & 255, (best.Mode >> 8) & 255, best.Mode & 255);
        }

        private static int Square(int value) { return value * value; }
        private sealed class ColorGroup
        {
            internal int Count, Mode, ModeCount, ModeDistance;
            internal long Weight;
        }
    }
}
