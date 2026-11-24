#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NativeLine = SnipStudio.TextLineRegion;
using NativeRecovery = SnipStudio.RecognizedTextRecovery;
using NativeResult = SnipStudio.TextRecognitionResult;

namespace SnipStudio.WinUI.Backend;

internal static partial class OfflineOcrEngine
{
    private const int EdgeReadBorder = 4;
    internal sealed record ClippedEdgeRead(int LineIndex, Rectangle Source)
    {
        internal Size PaddedSize => new(Source.Width + 2 * EdgeReadBorder, Source.Height + 2 * EdgeReadBorder);
    }

    private static async Task TryRecoverClippedEdgeTextAsync(NativeResult primary, Size size, byte[] gray, string root,
        string host, CancellationToken token)
    {
        ClippedEdgeRead? plan = FindClippedEdgeText(primary, size, gray, token);
        if (plan is null) return;

        Size paddedSize = plan.PaddedSize;
        byte[] inverted = new byte[paddedSize.Width * paddedSize.Height];
        Array.Fill(inverted, (byte)255);
        for (int y = 0; y < plan.Source.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            int original = (plan.Source.Y + y) * size.Width + plan.Source.X;
            int target = (y + EdgeReadBorder) * paddedSize.Width + EdgeReadBorder;
            for (int x = 0; x < plan.Source.Width; x++)
                inverted[target + x] = (byte)(255 - gray[original + x]);
        }

        var frame = new Rectangle(Point.Empty, paddedSize);
        var explicitRow = new NativeRecovery.SparseColumnRecovery(frame, [], [], EdgeReadBorder, 3);
        Response? response = await ReadLocalAsync(paddedSize, inverted, root, host, explicitRow,
            [ToBox(frame)], token).ConfigureAwait(false);
        if (response is not null) MergeClippedEdgeText(primary, plan, response, gray, size, token);
    }

    internal static ClippedEdgeRead? FindClippedEdgeText(NativeResult primary, Size size, byte[] gray, CancellationToken token)
    {
        if (!primary.Available || primary.Lines.Count is < 4 or > 64 ||
            !ValidEdgeRaster(size, gray) || primary.TextAngle is double angle && (!double.IsFinite(angle) || Math.Abs(angle) > 2))
            return null;

        var frame = new Rectangle(Point.Empty, size);
        ClippedEdgeRead? candidate = null;
        for (int i = 0; i < primary.Lines.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            NativeLine row = primary.Lines[i];
            RectangleF bounds = row.Bounds;
            // A rasterized UI line can lose its ascenders at the top of the
            // snip. Restrict rereads to one short, already recognized phrase
            // well away from the side edges and over a dark background.
            if (!Valid(bounds, frame) || bounds.Top > .5f || bounds.Height is < 4 or > 14 ||
                bounds.Width is < 36 or > 220 || bounds.Left < Math.Max(8, size.Width * .08f) ||
                bounds.Right > size.Width - 14 || row.Text.Length > 55 || row.Words.Count is < 3 or > 6 ||
                !row.ExcludedGraphicBounds.IsEmpty || row.Words.Any(word => !Valid(word.Bounds, bounds) ||
                    word.Bounds.Top > 1 || !TryEdgeWord(word.Text, out _)))
                continue;

            var crop = Rectangle.FromLTRB((int)Math.Floor(bounds.Left) - 8, 0,
                (int)Math.Ceiling(bounds.Right) + 14, (int)Math.Ceiling(bounds.Bottom) + 2);
            if (!frame.Contains(crop) || (!row.RecognitionClipBounds.IsEmpty && !row.RecognitionClipBounds.Contains(crop)) ||
                primary.ProtectedGraphics.Any(((RectangleF)crop).IntersectsWith) ||
                primary.Lines.Where((_, index) => index != i).Any(other => other.Bounds.IntersectsWith(crop)) ||
                !HasDarkEdgeInk(gray, size.Width, crop))
                continue;

            if (candidate is not null) return null;
            candidate = new ClippedEdgeRead(i, crop);
        }
        return candidate;
    }

    internal static bool MergeClippedEdgeText(NativeResult primary, ClippedEdgeRead plan, Response response,
        byte[] gray, Size size, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paddedFrame = new Rectangle(Point.Empty, plan.PaddedSize);
        if (!ValidEdgeRaster(size, gray) || !new Rectangle(Point.Empty, size).Contains(plan.Source) ||
            plan.LineIndex < 0 || plan.LineIndex >= primary.Lines.Count || response.Kind != "sparse-column" ||
            response.Panel?.ToRectangle() != (RectangleF)paddedFrame || response.Rows is not { Length: 1 })
            return false;
        Row reread = response.Rows[0];
        if (!IsCandidate(reread) || reread.Agreement != 3 || reread.Confidence < .995 ||
            !Valid(reread.Bounds.ToRectangle(), new Rectangle(EdgeReadBorder, EdgeReadBorder, plan.Source.Width, plan.Source.Height)) ||
            CreateLine(reread, paddedFrame) is not NativeLine recovered)
            return false;

        NativeLine prior = primary.Lines[plan.LineIndex];
        if (prior.Text == recovered.Text || prior.Words.Count != recovered.Words.Count)
            return false;

        int unchanged = 0;
        int changes = 0;
        int letters = 0;
        float offsetX = plan.Source.X - EdgeReadBorder;
        float offsetY = plan.Source.Y - EdgeReadBorder;
        recovered.Bounds = OffsetEdgeBox(recovered.Bounds, offsetX, offsetY);
        for (int index = 0; index < recovered.Words.Count; index++)
        {
            var oldWord = prior.Words[index];
            var newWord = recovered.Words[index];
            if (!TryEdgeWord(oldWord.Text, out string oldCore) || !TryEdgeWord(newWord.Text, out string newCore) ||
                oldCore.Length != newCore.Length) return false;
            // Sentence-ending punctuation can also be clipped. Leave internal
            // punctuation and unconfirmed insertions or deletions intact.
            if (!oldWord.Text.AsSpan(oldCore.Length).SequenceEqual(newWord.Text.AsSpan(newCore.Length)) &&
                (index != recovered.Words.Count - 1 || oldWord.Text.Length == oldCore.Length ||
                    newWord.Text.Length == newCore.Length)) return false;
            newWord.Bounds = OffsetEdgeBox(newWord.Bounds, offsetX, offsetY);
            RectangleF overlap = RectangleF.Intersect(oldWord.Bounds, newWord.Bounds);
            if (overlap.Width < Math.Min(oldWord.Bounds.Width, newWord.Bounds.Width) * .72f ||
                overlap.Height < Math.Min(oldWord.Bounds.Height, newWord.Bounds.Height) * .72f ||
                !HasLightEdgeWord(gray, size.Width, newWord.Bounds)) return false;

            int changesInWord = 0;
            for (int j = 0; j < oldCore.Length; j++)
            {
                if (oldCore[j] == newCore[j]) continue;
                if (char.IsUpper(oldCore[j]) != char.IsUpper(newCore[j]) || ++changesInWord > 1) return false;
            }
            if (changesInWord == 0) unchanged++;
            changes += changesInWord;
            letters += oldCore.Length;
        }

        // The other engine must independently agree on at least two complete
        // positional words and most letters. Never insert isolated characters,
        // replace disjoint text, or infer words absent from both reads.
        if (unchanged < 2 || changes is < 1 or > 2 || changes * 5 > letters ||
            primary.ProtectedGraphics.Any(recovered.Bounds.IntersectsWith))
            return false;

        recovered.RecognitionClipBounds = plan.Source;
        primary.Lines[plan.LineIndex] = recovered;
        RecordRecovery(primary, response);
        return true;
    }

    private static bool TryEdgeWord(string text, out string letters)
    {
        letters = string.Empty;
        if (string.IsNullOrEmpty(text) || text.Length > 18) return false;
        int end = text[^1] is '.' or '!' or '?' or ',' ? text.Length - 1 : text.Length;
        if (end < 2) return false;
        for (int i = 0; i < end; i++)
            if (text[i] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')) return false;
        letters = text[..end];
        return true;
    }

    private static bool ValidEdgeRaster(Size size, byte[] gray) =>
        size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height == gray.Length;

    private static bool HasDarkEdgeInk(byte[] gray, int stride, Rectangle crop)
    {
        int bright = 0;
        int dark = 0;
        int perimeter = 0;
        int darkPerimeter = 0;
        for (int y = crop.Top; y < crop.Bottom; y++)
        {
            for (int x = crop.Left; x < crop.Right; x++)
            {
                byte level = gray[y * stride + x];
                if (level >= 112) bright++;
                if (level <= 64) dark++;
                // The top is clipped and may start inside its glyphs.
                if (x != crop.Left && x != crop.Right - 1 && y != crop.Bottom - 1) continue;
                perimeter++;
                if (level <= 64) darkPerimeter++;
            }
        }
        int area = crop.Width * crop.Height;
        return dark * 5 >= area * 3 && bright * 100 >= area * 3 && darkPerimeter * 10 >= perimeter * 9;
    }

    private static bool HasLightEdgeWord(byte[] gray, int stride, RectangleF bounds)
    {
        var pixels = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
            (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
        int lit = 0;
        for (int y = pixels.Top; y < pixels.Bottom; y++)
            for (int x = pixels.Left; x < pixels.Right; x++)
                if (gray[y * stride + x] >= 112) lit++;
        return lit >= Math.Max(4, pixels.Width * pixels.Height / 20);
    }

    private static RectangleF OffsetEdgeBox(RectangleF bounds, float x, float y)
    {
        bounds.Offset(x, y);
        return bounds;
    }
}
