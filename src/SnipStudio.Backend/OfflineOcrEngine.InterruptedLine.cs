#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using NativeLine = SnipStudio.TextLineRegion;
using NativeResult = SnipStudio.TextRecognitionResult;
using NativeWord = SnipStudio.TextWordRegion;

namespace SnipStudio.WinUI.Backend;

internal static partial class OfflineOcrEngine
{
    internal sealed record InterruptedLineQuery(
        Rectangle Source, NativeLine Left, NativeLine Right, Rectangle LeftProofSource, Rectangle RightProofSource);

    internal static InterruptedLineQuery? FindInterruptedLine(
        NativeResult primary, Size size, byte[] gray, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanReadInterruptedLine(primary, size, gray)) return null;

        var frame = new Rectangle(Point.Empty, size);
        InterruptedLineQuery? found = null;
        foreach (NativeLine left in primary.Lines)
        {
            if (left.Words.Count < 2 || left.Text.Length < 12) continue;
            foreach (NativeLine right in primary.Lines)
            {
                token.ThrowIfCancellationRequested();
                if (!TryInterruptedLineClip(primary, left, right, frame, out var clip) ||
                    !HasInterruptedGapInk(clip.Source, left.Bounds, right.Bounds, size, gray, token))
                    continue;
                if (found is not null) return null;
                found = clip;
            }
        }
        return found;
    }

    internal static bool MergeInterruptedLine(
        NativeResult primary, InterruptedLineQuery query, Response response, Size size, byte[] gray,
        CancellationToken token, Response? leftProof = null, Response? rightProof = null)
    {
        token.ThrowIfCancellationRequested();
        if (!CanReadInterruptedLine(primary, size, gray) ||
            !TryInterruptedLineClip(primary, query.Left, query.Right, new Rectangle(Point.Empty, size), out var current) ||
            current != query ||
            !HasInterruptedGapInk(query.Source, query.Left.Bounds, query.Right.Bounds, size, gray, token))
            return false;

        var leftIndex = primary.Lines.IndexOf(query.Left);
        var rightIndex = primary.Lines.IndexOf(query.Right);
        if (leftIndex < 0 || rightIndex < 0 ||
            ReadInterruptedResponse(response, "gap-row", query.Source, 4) is not NativeLine recovered)
            return false;

        int firstNew = query.Left.Words.Count;
        int endNew = recovered.Words.Count - query.Right.Words.Count;
        if (endNew <= firstNew ||
            !PreserveInterruptedAnchor(query.Left, recovered, 0, leftProof, query.LeftProofSource, terminal: false) ||
            !PreserveInterruptedAnchor(query.Right, recovered, endNew, rightProof, query.RightProofSource, terminal: true))
            return false;

        RectangleF verifiedBounds = RectangleF.Union(query.Left.Bounds, query.Right.Bounds);
        for (int index = firstNew; index < endNew; index++)
        {
            token.ThrowIfCancellationRequested();
            NativeWord word = recovered.Words[index];
            if (word.Bounds.Left < query.Left.Bounds.Right || word.Bounds.Right > query.Right.Bounds.Left ||
                !word.Text.Any(char.IsLetterOrDigit) || !HasInterruptedWordInk(word.Bounds, size, gray))
                return false;
            verifiedBounds = RectangleF.Union(verifiedBounds, word.Bounds);
        }
        if (!OrderedInterruptedWords(recovered) || !((RectangleF)query.Source).Contains(verifiedBounds))
            return false;

        token.ThrowIfCancellationRequested();
        recovered.Bounds = verifiedBounds;
        recovered.RecognitionClipBounds = query.Source;
        int destination = Math.Min(leftIndex, rightIndex);
        primary.Lines.RemoveAt(Math.Max(leftIndex, rightIndex));
        primary.Lines[destination] = recovered;
        RecordRecovery(primary, response);
        return true;
    }

    private static bool CanReadInterruptedLine(NativeResult primary, Size size, byte[] gray) =>
        primary.Available && primary.Lines.Count is >= 2 and <= 72 &&
        ValidEdgeRaster(size, gray) &&
        (primary.TextAngle is not double angle || double.IsFinite(angle) && Math.Abs(angle) <= 1.5);

    private static bool TryInterruptedLineClip(
        NativeResult primary, NativeLine left, NativeLine right, Rectangle frame, out InterruptedLineQuery query)
    {
        query = null!;
        RectangleF from = left.Bounds, to = right.Bounds;
        if (!Valid(from, frame) || !Valid(to, frame) ||
            to.Left - from.Right < 120 || Math.Abs(from.Top - to.Top) > 2 ||
            Math.Abs(from.Bottom - to.Bottom) > 4 || left.Words.Count < 2 || left.Text.Length < 12 ||
            right.Text.Length < 4 || !left.ExcludedGraphicBounds.IsEmpty || !right.ExcludedGraphicBounds.IsEmpty)
            return false;

        var source = Rectangle.FromLTRB((int)Math.Floor(from.Left) - 2, (int)Math.Floor(Math.Min(from.Top, to.Top)) - 2,
            (int)Math.Ceiling(to.Right) + 4, (int)Math.Ceiling(Math.Max(from.Bottom, to.Bottom)) + 2);
        if (source.Width is < 310 or > 806 || source.Height is < 12 or > 22 || !frame.Contains(source) ||
            !ValidInterruptedAnchor(left, source) || !ValidInterruptedAnchor(right, source))
            return false;

        // The endpoint crops retain the adjoining blank pixels, without
        // including meaningful ink from the newly proposed middle words.
        var leftSource = Rectangle.FromLTRB(source.Left, source.Top, (int)Math.Ceiling(from.Right) + 1, source.Bottom);
        var rightSource = Rectangle.FromLTRB((int)Math.Floor(to.Left) - 3, source.Top, source.Right + 1, source.Bottom);
        if (!frame.Contains(rightSource) || !ValidInterruptedAnchor(right, rightSource) ||
            IntersectsInterruptedObstacle(primary, left, right,
                RectangleF.Union(source, rightSource)))
            return false;
        query = new InterruptedLineQuery(source, left, right, leftSource, rightSource);
        return true;
    }

    private static bool ValidInterruptedAnchor(NativeLine line, RectangleF source)
    {
        if (line.Words.Count is < 1 or > 32 || line.Text.Length > 256 || line.Text.Any(char.IsControl) ||
            (!line.RecognitionClipBounds.IsEmpty && !line.RecognitionClipBounds.Contains(source)) ||
            !line.Words.All(word => Valid(word.Bounds, line.Bounds)) || !OrderedInterruptedWords(line))
            return false;
        return line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SequenceEqual(line.Words.Select(word => word.Text), StringComparer.Ordinal);
    }

    private static bool IntersectsInterruptedObstacle(
        NativeResult primary, NativeLine left, NativeLine right, RectangleF source) =>
        primary.ProtectedGraphics.Any(source.IntersectsWith) ||
        primary.Lines.Any(other => !ReferenceEquals(other, left) && !ReferenceEquals(other, right) &&
            (other.Bounds.IntersectsWith(source) || other.ExcludedGraphicBounds.IntersectsWith(source)));

    private static bool HasInterruptedGapInk(
        Rectangle clip, RectangleF left, RectangleF right, Size size, byte[] gray, CancellationToken token)
    {
        int start = (int)Math.Ceiling(left.Right);
        int end = (int)Math.Floor(right.Left);
        int columns = end - start;
        int occupied = 0, dark = 0, light = 0, blank = 0;
        for (int x = start; x < end; x++)
        {
            if ((x & 31) == 0) token.ThrowIfCancellationRequested();
            int ink = 0;
            for (int y = clip.Top; y < clip.Bottom; y++)
            {
                byte value = gray[y * size.Width + x];
                if (value < 220) ink++;
                if (value > 238) light++;
            }
            dark += ink;
            if (ink == 0)
            {
                if (++blank > 12) return false;
            }
            else
            {
                occupied++;
                blank = 0;
            }
        }

        int area = columns * clip.Height;
        return occupied * 100 >= columns * 68 && dark * 100 >= area * 4 &&
            dark * 2 <= area && light * 2 >= area;
    }

    private static NativeLine? ReadInterruptedResponse(Response response, string kind, Rectangle source, int minimumAgreement)
    {
        if (response.Kind != kind || response.Panel?.ToRectangle() != InterruptedResponseFrame(source) ||
            response.Rows is not { Length: 1 })
            return null;
        return ReadInterruptedRow(response.Rows[0], source, minimumAgreement, 6);
    }

    private static RectangleF InterruptedResponseFrame(Rectangle source) =>
        new(0, 0, source.Width, source.Height);

    private static NativeLine? ReadInterruptedRow(Row row, Rectangle source, int minimumAgreement, int maximumAgreement)
    {
        if (!IsCandidate(row) || row.Agreement < minimumAgreement || row.Agreement > maximumAgreement ||
            CreateLine(row, InterruptedResponseFrame(source)) is not NativeLine line || !OrderedInterruptedWords(line))
            return null;

        line.Bounds = OffsetEdgeBox(line.Bounds, source.X, source.Y);
        foreach (NativeWord word in line.Words)
            word.Bounds = OffsetEdgeBox(word.Bounds, source.X, source.Y);
        return line;
    }

    private static bool PreserveInterruptedAnchor(
        NativeLine original, NativeLine recovered, int offset, Response? suppliedProof, Rectangle proofSource, bool terminal)
    {
        int corrections = 0;
        for (int index = 0; index < original.Words.Count; index++)
        {
            NativeWord prior = original.Words[index], current = recovered.Words[offset + index];
            if (!CorrespondingInterruptedWord(prior.Bounds, current.Bounds) ||
                (prior.Text != current.Text && !InterruptedAnchorCorrection(prior.Text, current.Text, ref corrections)))
                return false;
        }
        if (corrections > 0 && !VerifyInterruptedCorrections(original, recovered, offset, suppliedProof, proofSource, terminal))
            return false;

        for (int index = 0; index < original.Words.Count; index++)
        {
            NativeWord prior = original.Words[index], current = recovered.Words[offset + index];
            recovered.Words[offset + index] = prior.Text == current.Text ? prior : new NativeWord
            {
                Text = current.Text,
                Bounds = prior.Bounds,
                Footprint = prior.Footprint
            };
        }
        return true;
    }

    private static bool VerifyInterruptedCorrections(
        NativeLine original, NativeLine recovered, int offset, Response? response, Rectangle source, bool terminal)
    {
        if (response is null || response.Kind != "gap-anchor" || response.Panel?.ToRectangle() != InterruptedResponseFrame(source) ||
            response.Rows is not { Length: >= 1 and <= 4 })
            return false;
        int votes = 0, support = 0, disagreement = 0;
        foreach (Row row in response.Rows)
        {
            if (row is null || row.Agreement is < 2 or > 10 || (votes += row.Agreement) > 10)
                return false;
            if (ReadInterruptedRow(row, source, 2, 10) is not NativeLine proof ||
                !ComparableInterruptedProof(original, recovered, offset, proof, terminal, out bool agrees))
                continue;
            if (agrees) support += row.Agreement;
            else disagreement += row.Agreement;
        }
        // A dispute requires a majority of the ten physical source views,
        // rather than a majority of only the views that returned text.
        return support >= 6 || (support >= 2 && disagreement == 0);
    }

    private static bool ComparableInterruptedProof(
        NativeLine original, NativeLine recovered, int offset, NativeLine proof, bool terminal, out bool agrees)
    {
        agrees = true;
        if (proof.Words.Count != original.Words.Count) return false;
        for (int index = 0; index < proof.Words.Count; index++)
        {
            NativeWord prior = original.Words[index], independent = proof.Words[index];
            if (!CorrespondingInterruptedWord(prior.Bounds, independent.Bounds)) return false;
            ReadOnlySpan<char> before = prior.Text, after = recovered.Words[offset + index].Text, observed = independent.Text;
            // Differing votes on the count of visible terminal periods say
            // nothing about the preceding dollar sign. The proposed line must
            // keep the original literal periods before this exception applies.
            if (terminal && index == proof.Words.Count - 1 &&
                before.EndsWith("...", StringComparison.Ordinal) && after.EndsWith("...", StringComparison.Ordinal) &&
                observed.EndsWith(".", StringComparison.Ordinal))
            {
                before = before.TrimEnd('.');
                after = after.TrimEnd('.');
                observed = observed.TrimEnd('.');
            }
            if (before.Length != after.Length || after.Length != observed.Length) return false;
            for (int character = 0; character < before.Length; character++)
            {
                if (observed[character] == after[character]) continue;
                if (before[character] == after[character]) return false;
                agrees = false;
            }
        }
        return true;
    }

    private static bool InterruptedAnchorCorrection(string oldText, string newText, ref int corrections)
    {
        if (oldText.Length < 5 || oldText.Length != newText.Length) return false;
        for (int index = 0; index < oldText.Length; index++)
        {
            if (oldText[index] == newText[index]) continue;
            bool dollar = index == 0 && oldText[index] == 'S' && newText[index] == '$' &&
                char.IsAsciiLetter(newText[1]);
            bool apostrophe = index == oldText.Length - 1 && oldText[index] == '\u2022' &&
                newText[index] == '\'' && newText.AsSpan(0, index).Contains('\'') && char.IsAsciiLetter(newText[index - 1]);
            if ((!dollar && !apostrophe) || ++corrections > 2) return false;
        }
        return true;
    }

    private static bool CorrespondingInterruptedWord(RectangleF original, RectangleF observed)
    {
        RectangleF overlap = RectangleF.Intersect(original, observed);
        return overlap.Width >= Math.Min(original.Width, observed.Width) * .72f &&
            overlap.Height >= Math.Min(original.Height, observed.Height) * .72f &&
            Math.Abs(original.Left - observed.Left) <= 3 && Math.Abs(original.Right - observed.Right) <= 3;
    }

    private static bool OrderedInterruptedWords(NativeLine line)
    {
        float end = line.Bounds.Left;
        foreach (NativeWord word in line.Words)
        {
            if (word.Bounds.Left < end) return false;
            end = word.Bounds.Right;
        }
        return true;
    }

    private static bool HasInterruptedWordInk(RectangleF word, Size size, byte[] gray)
    {
        var pixels = Rectangle.FromLTRB((int)Math.Floor(word.Left), (int)Math.Floor(word.Top),
            (int)Math.Ceiling(word.Right), (int)Math.Ceiling(word.Bottom));
        int dark = 0;
        for (int y = pixels.Top; y < pixels.Bottom; y++)
            for (int x = pixels.Left; x < pixels.Right; x++)
                if (gray[y * size.Width + x] < 220) dark++;
        return dark >= 4 && dark * 100 >= pixels.Width * pixels.Height * 4;
    }
}
