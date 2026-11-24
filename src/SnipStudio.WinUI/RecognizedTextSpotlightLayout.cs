using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

internal static class RecognizedTextSpotlightLayout
{
    private const int HorizontalPadding = 3;
    private const int VerticalPadding = 4;

    internal static List<OcrBounds> CreateAreas(IReadOnlyList<OcrLineSnapshot> lines, int width, int height)
    {
        var areas = new List<OcrBounds>();
        if (width <= 0 || height <= 0) return areas;

        var ink = new List<OcrBounds>();
        foreach (OcrLineSnapshot line in lines)
        {
            foreach (OcrWordSnapshot word in line.Words)
            {
                OcrBounds box = word.Bounds;
                double right = box.X + box.Width;
                double bottom = box.Y + box.Height;
                if (string.IsNullOrWhiteSpace(word.Text) || box.Width <= 0 || box.Height <= 0 ||
                    !double.IsFinite(box.X) || !double.IsFinite(box.Y) ||
                    !double.IsFinite(right) || !double.IsFinite(bottom) ||
                    right <= 0 || bottom <= 0 || box.X >= width || box.Y >= height) continue;

                double left = Math.Max(0, Math.Floor(box.X));
                double top = Math.Max(0, Math.Floor(box.Y));
                right = Math.Min(width, Math.Ceiling(right));
                bottom = Math.Min(height, Math.Ceiling(bottom));
                ink.Add(new OcrBounds(left, top, right - left, bottom - top));
            }
        }

        // Vertically aligned words share a reveal band even when OCR delivered
        // the filename and timestamp as separate lines across a wide gutter.
        var rows = new List<RevealRow>();
        foreach (OcrBounds box in ink.OrderBy(CenterY).ThenBy(box => box.X))
        {
            if (rows.Count == 0 || !SharesVisualRow(rows[^1].Anchor, box)) rows.Add(new RevealRow(box));
            else rows[^1].Add(box);
        }

        // Short labels and capital letters in one font can have very different
        // ink heights. Use the tallest comparable row for consistent padding.
        RevealRow[] byHeight = rows.OrderBy(row => row.InkHeight).ToArray();
        for (int start = 0; start < byHeight.Length;)
        {
            double reference = byHeight[start].InkHeight;
            double comparable = reference + Math.Max(6, Math.Ceiling(reference / 3));
            int end = start + 1;
            while (end < byHeight.Length && byHeight[end].InkHeight <= comparable) end++;
            double paddedHeight = byHeight[end - 1].InkHeight + 2 * VerticalPadding;
            for (int index = start; index < end; index++) byHeight[index].RevealHeight = paddedHeight;
            start = end;
        }

        for (int index = 0; index < rows.Count; index++)
        {
            RevealRow row = rows[index];
            // Spare a pixel of the original gutter where tightly spaced rows
            // can otherwise grow together. Source ink always remains visible.
            double topLimit = index == 0 ? 0 :
                Math.Min(row.Top, Math.Floor((rows[index - 1].Bottom + row.Top) / 2) + 1);
            double bottomLimit = index + 1 == rows.Count ? height :
                Math.Max(row.Bottom, Math.Floor((row.Bottom + rows[index + 1].Top) / 2));
            double revealHeight = Math.Min(row.RevealHeight, bottomLimit - topLimit);
            double top = Math.Clamp(Math.Floor((row.Top + row.Bottom - revealHeight) / 2),
                Math.Max(topLimit, row.Bottom - revealHeight), Math.Min(row.Top, bottomLimit - revealHeight));
            foreach (OcrBounds box in row.Words)
            {
                double left = Math.Max(0, box.X - HorizontalPadding);
                double right = Math.Min(width, box.X + box.Width + HorizontalPadding);
                areas.Add(new OcrBounds(left, top, right - left, revealHeight));
            }
        }
        return areas;
    }

    private static bool SharesVisualRow(OcrBounds first, OcrBounds second)
    {
        double shorter = Math.Min(first.Height, second.Height);
        double overlap = Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y);
        return overlap >= shorter / 2 && Math.Abs(CenterY(first) - CenterY(second)) <= Math.Max(3, shorter / 2);
    }

    private static double CenterY(OcrBounds box) => box.Y + box.Height / 2;

    private sealed class RevealRow
    {
        internal OcrBounds Anchor { get; }
        internal List<OcrBounds> Words { get; }
        internal double Top { get; private set; }
        internal double Bottom { get; private set; }
        internal double InkHeight => Bottom - Top;
        internal double RevealHeight { get; set; }

        internal RevealRow(OcrBounds anchor)
        {
            Anchor = anchor;
            Words = [anchor];
            Top = anchor.Y;
            Bottom = anchor.Y + anchor.Height;
        }

        internal void Add(OcrBounds box)
        {
            Words.Add(box);
            Top = Math.Min(Top, box.Y);
            Bottom = Math.Max(Bottom, box.Y + box.Height);
        }
    }
}
