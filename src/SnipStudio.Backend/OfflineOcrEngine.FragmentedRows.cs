#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NativeLine = SnipStudio.TextLineRegion;
using NativeResult = SnipStudio.TextRecognitionResult;

namespace SnipStudio.WinUI.Backend;

internal static partial class OfflineOcrEngine
{
    internal sealed record FragmentedRow(Rectangle Ink, Rectangle Crop, List<NativeLine> Fragments);

    private static Task<bool> TryRecoverFragmentedRowsAsync(NativeResult primary, Size size, byte[] gray,
        string root, string host, CancellationToken token) => RecoverFragmentedRowsAsync(primary, size, gray,
            (crop, pixels, cancel) => ReadLocalAsync(crop.Size, pixels, root, host, null, null, cancel, ReadMode.LineAnchor), token);

    internal static async Task<bool> RecoverFragmentedRowsAsync(NativeResult primary, Size size, byte[] gray,
        Func<Rectangle, byte[], CancellationToken, Task<Response?>> readRow, CancellationToken token)
    {
        List<FragmentedRow>? rows = FindFragmentedRows(primary, size, gray, token);
        if (rows is null) return false;

        var planned = new List<NativeLine>(rows.Count);
        var recovered = new List<Row>();
        foreach (FragmentedRow row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Fragments.Count == 1)
            {
                planned.Add(row.Fragments[0]);
                continue;
            }

            Rectangle crop = row.Crop;
            var pixels = new byte[crop.Width * crop.Height];
            for (int y = 0; y < crop.Height; y++)
                Buffer.BlockCopy(gray, (crop.Top + y) * size.Width + crop.Left, pixels, y * crop.Width, crop.Width);

            Response? response = await readRow(crop, pixels, token).ConfigureAwait(false);
            if (ReadFragmentedRow(row, response, size, gray, token) is not NativeLine line) return false;
            planned.Add(line);
            recovered.Add(response!.Rows[0]);
        }

        token.ThrowIfCancellationRequested();
        primary.Lines.Clear();
        primary.Lines.AddRange(planned);
        RecordRecovery(primary, new Response("fragmented-rows", null, recovered.ToArray()));
        primary.RecoveryPasses += recovered.Count - 1;
        return true;
    }

    internal static List<FragmentedRow>? FindFragmentedRows(NativeResult primary, Size size, byte[] gray,
        CancellationToken token)
    {
        if (size.Width is < 80 or > 900 || size.Height is < 60 or > 400 ||
            size.Width * size.Height != gray.Length || primary.Lines.Count is < 6 or > 36)
            return null;

        var bands = new List<Rectangle>();
        int top = -1;
        for (int y = 0; y <= size.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            int ink = 0;
            if (y < size.Height)
                for (int x = 0; x < size.Width; x++)
                    if (gray[y * size.Width + x] < 150) ink++;

            // A straight border at the edge is common in screen captures.
            if (ink * 4 >= size.Width * 3 && (y <= 2 || y >= size.Height - 3)) ink = 0;
            if (ink >= 3)
            {
                if (top < 0) top = y;
                continue;
            }
            if (top < 0) continue;
            // A thin descender can have a one- or two-pixel gap from the main
            // text band. Keep it inside that band's padded reread window.
            if (y - top <= 2 && bands.Count > 0 && top - bands[^1].Bottom <= 2 && y <= bands[^1].Bottom + 4)
            {
                top = -1;
                continue;
            }
            if (y - top is < 7 or > 14 || bands.Count == 16 ||
                ReadFragmentedBand(gray, size.Width, top, y) is not Rectangle band)
                return null;
            bands.Add(band);
            top = -1;
        }

        if (bands.Count < 4) return null;
        int pitch = (bands[^1].Top - bands[0].Top + (bands.Count - 1) / 2) / (bands.Count - 1);
        int commonLeft = bands.Min(band => band.Left);
        var rows = new List<FragmentedRow>(bands.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            Rectangle band = bands[i];
            if (band.Left - commonLeft > 4 || Math.Abs(band.Top - (bands[0].Top + i * pitch)) > 3 ||
                (i > 0 && band.Top - bands[i - 1].Bottom < 7))
                return null;

            int left = Math.Max(0, commonLeft - 3);
            int right = Math.Min(size.Width, band.Right + 7);
            Rectangle crop = Rectangle.FromLTRB(left, Math.Max(0, band.Top - 4), right,
                Math.Min(size.Height, band.Bottom + 4));
            rows.Add(new FragmentedRow(band, crop, new List<NativeLine>()));
        }

        foreach (NativeLine line in primary.Lines)
        {
            FragmentedRow? owner = null;
            foreach (FragmentedRow row in rows)
            {
                if (Math.Min(line.Bounds.Bottom, row.Ink.Bottom) - Math.Max(line.Bounds.Top, row.Ink.Top) < row.Ink.Height * .5f)
                    continue;
                if (owner is not null || line.Bounds.Left < row.Ink.Left - 4 || line.Bounds.Right > row.Ink.Right + 5)
                    return null;
                owner = row;
            }
            if (owner is null) return null;
            owner.Fragments.Add(line);
        }

        int splitRows = 0;
        foreach (FragmentedRow row in rows)
        {
            if (row.Fragments.Count == 0) return null;
            row.Fragments.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
            if (Math.Abs(row.Fragments[0].Bounds.Left - row.Ink.Left) > 4 ||
                Math.Abs(row.Fragments[^1].Bounds.Right - row.Ink.Right) > 6)
                return null;
            if (row.Fragments.Count == 1) continue;
            if (++splitRows > 6 || row.Crop.Width is < 24 or > 309 || row.Crop.Height is < 12 or > 22 ||
                primary.ProtectedGraphics.Any(((RectangleF)row.Crop).IntersectsWith) ||
                row.Fragments.Any(line => !line.ExcludedGraphicBounds.IsEmpty))
                return null;
        }
        return splitRows >= 2 ? rows : null;
    }

    private static Rectangle? ReadFragmentedBand(byte[] gray, int width, int top, int bottom)
    {
        int left = -1;
        int right = -1;
        for (int x = 0; x < width; x++)
        {
            int y = top;
            while (y < bottom && gray[y * width + x] >= 150) y++;
            if (y == bottom) continue;
            // A larger internal gutter can be a table cell separator.
            if (right >= 0 && x - right > Math.Max(24, 2 * (bottom - top))) return null;
            if (left < 0) left = x;
            right = x;
        }
        if (left < 0) return null;
        // Include the antialiased outside edge when framing the reread.
        for (int x = right + 1; x < Math.Min(width, right + 3); x++)
            for (int y = top; y < bottom; y++)
                if (gray[y * width + x] < 155) { right = x; break; }
        return Rectangle.FromLTRB(left, top, right + 1, bottom);
    }

    private static NativeLine? ReadFragmentedRow(FragmentedRow row, Response? response, Size size, byte[] gray,
        CancellationToken token)
    {
        if (response is not { Kind: "gap-anchor", Rows.Length: >= 1 and <= 4 } ||
            response.Panel?.ToRectangle() != (RectangleF)new Rectangle(Point.Empty, row.Crop.Size) ||
            response.Rows.Any(candidate => candidate is null || candidate.Agreement is < 2 or > 10) ||
            response.Rows.Sum(candidate => candidate.Agreement) > 10)
            return null;

        Row winner = response.Rows[0];
        if (winner.Confidence < .97 || response.Rows.Skip(1).Any(other => other.Agreement >= winner.Agreement - 1) ||
            ReadInterruptedRow(winner, row.Crop, 4, 10) is not NativeLine line)
            return null;

        RectangleF actual = line.Words.Select(word => word.Bounds).Aggregate(RectangleF.Union);
        RectangleF tolerance = row.Ink;
        tolerance.Inflate(3, 3);
        if (Math.Abs(actual.Left - row.Ink.Left) > 4 || Math.Abs(actual.Right - row.Ink.Right) > 5 ||
            !tolerance.Contains(actual) || line.Words.Any(word => !HasInterruptedWordInk(word.Bounds, size, gray)))
            return null;

        // Compare letters and digits to earlier fragments as corroboration only.
        // Copy the winning row verbatim, including every recognized separator and space.
        string proposed = new(line.Text.Where(char.IsLetterOrDigit).ToArray());
        string native = new(row.Fragments.SelectMany(fragment => fragment.Text.Where(char.IsLetterOrDigit)).ToArray());
        if (native.Length is < 4 or > 128 || proposed.Length is < 4 or > 128 ||
            !native.Where(char.IsDigit).SequenceEqual(proposed.Where(char.IsDigit)))
            return null;
        int shared = CountSharedInOrder(native, proposed);
        int added = proposed.Length - native.Length;
        if (added > 2)
        {
            if (shared != native.Length || winner.Agreement < 5 || winner.Confidence < .98 ||
                !HasMissingFragmentInk(row, added, size, gray, token)) return null;
        }
        else if (shared < Math.Max(native.Length, proposed.Length) - 2) return null;

        line.Bounds = actual;
        line.RecognitionClipBounds = row.Crop;
        return line;
    }

    private static bool HasMissingFragmentInk(FragmentedRow row, int added, Size size, byte[] gray,
        CancellationToken token)
    {
        for (int i = 1; i < row.Fragments.Count; i++)
        {
            RectangleF left = row.Fragments[i - 1].Bounds;
            RectangleF right = row.Fragments[i].Bounds;
            if (right.Left - left.Right >= Math.Max(18, 3 * added) &&
                HasInterruptedGapInk(row.Crop, left, right, size, gray, token)) return true;
        }
        return false;
    }

    private static int CountSharedInOrder(string first, string second)
    {
        var counts = new int[second.Length + 1];
        foreach (char letter in first)
        {
            int previous = 0;
            for (int i = 1; i < counts.Length; i++)
            {
                int diagonal = previous;
                previous = counts[i];
                counts[i] = letter == second[i - 1] ? diagonal + 1 : Math.Max(counts[i], counts[i - 1]);
            }
        }
        return counts[^1];
    }
}
