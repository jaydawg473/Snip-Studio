#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnipStudio;

namespace SnipStudio.WinUI.Backend;

internal static partial class TerminalOcrRecovery
{
    internal static async Task ReviewClippedSideLabelAsync(TextRecognitionResult primary, OfflineOcrEngine.Response response,
        Bitmap source, CancellationToken token)
    {
        var candidate = FindClippedSideLabel(primary, response, source, token);
        if (candidate is not { } label) return;
        string first = await ReadSideLabelViewAsync(source, label.Focus, invert: false, token).ConfigureAwait(false);
        string second = await ReadSideLabelViewAsync(source, label.Focus, invert: true, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        AbstainFromSideLabel(primary, label.Line, first, second);
    }

    internal static (TextLineRegion Line, Rectangle Focus)? FindClippedSideLabel(TextRecognitionResult primary,
        OfflineOcrEngine.Response response, Bitmap source, CancellationToken token)
    {
        if (response.Kind != "dark" || response.Panel is null || response.Rows is not { Length: >= 2 and <= 64 })
            return null;
        RectangleF panel = response.Panel.ToRectangle();
        var frame = new Rectangle(Point.Empty, source.Size);
        if (!Valid(panel, frame) || panel.Left < 12 || panel.Left > Math.Min(36, source.Width * .09f) ||
            panel.Right < source.Width - 1 || panel.Height < source.Height * .85f || MeasurePitch(response.Rows, panel) <= 0)
            return null;

        (TextLineRegion Line, Rectangle Focus)? result = null;
        foreach (var line in primary.Lines)
        {
            token.ThrowIfCancellationRequested();
            if (!Valid(line.Bounds, frame) || line.Bounds.Left > .5f || line.Bounds.Right + 10 > panel.Left ||
                line.Bounds.Height is < 8 or > 14 || line.Bounds.Width is < 8 or > 24 || line.Words.Count != 1 ||
                line.Words[0].Text != line.Text || line.Words[0].Bounds != line.Bounds || !line.ExcludedGraphicBounds.IsEmpty ||
                line.Text.Length is < 4 or > 12 || line.Text[^1] is not ('t' or 'f') ||
                !line.Text[..^2].All(char.IsAsciiLetterOrDigit)) continue;

            Rectangle word = Rectangle.FromLTRB(0, (int)Math.Floor(line.Bounds.Top),
                (int)Math.Ceiling(line.Bounds.Right), (int)Math.Ceiling(line.Bounds.Bottom));
            Rectangle focus = Rectangle.FromLTRB(0, word.Top - 1, word.Right + 7, word.Bottom + 2);
            if (!frame.Contains(focus) || primary.ProtectedGraphics.Any(((RectangleF)focus).IntersectsWith) ||
                primary.Lines.Any(other => !ReferenceEquals(line, other) && other.Bounds.IntersectsWith(focus)) ||
                !HasBareClippedStem(source, word)) continue;
            if (result is not null) return null;
            result = (line, focus);
        }
        return result;
    }

    private static bool HasBareClippedStem(Bitmap source, Rectangle word)
    {
        int edgeInk = 0, longestStem = 0, run = 0;
        for (int y = word.Top; y < word.Bottom; y++)
        {
            if (NeutralInk(source.GetPixel(0, y), 162)) edgeInk++;
            if (NeutralInk(source.GetPixel(word.Right - 1, y), 162))
                longestStem = Math.Max(longestStem, ++run);
            else run = 0;

            // Any nearby antialiased ink could still be a crossbar. In that
            // case the side label is kept, even if the focused reads disagree.
            if (NeutralInk(source.GetPixel(word.Right - 2, y), 126) ||
                NeutralInk(source.GetPixel(word.Right, y), 126)) return false;
        }
        return edgeInk >= 2 && longestStem >= Math.Max(5, word.Height / 2);
    }

    private static bool NeutralInk(Color color, int minimum) => color.A == 255 &&
        (3 * color.R + 6 * color.G + color.B) / 10 >= minimum &&
        Math.Max(Math.Max(color.R, color.G), color.B) - Math.Min(Math.Min(color.R, color.G), color.B) <= 55;

    internal static bool AbstainFromSideLabel(TextRecognitionResult primary, TextLineRegion line, string first, string second)
    {
        string original = line.Text;
        if (original.Length is < 4 or > 12 || original[^1] is not ('t' or 'f') ||
            first != second || first.Length != original.Length || first[^1] != 'l' || !first.All(char.IsAsciiLetterOrDigit) ||
            !first.AsSpan(0, first.Length - 2).SequenceEqual(original.AsSpan(0, original.Length - 2)) ||
            (first[^2] != original[^2] && original[^2] is not (';' or ':' or '.' or ',')) ||
            primary.ProtectedGraphics.Count >= 512 || !primary.Lines.Remove(line)) return false;

        // This is a contradictory read, not a trusted replacement. Keep the
        // original native text and mark the actual ink as unsafe to overwrite.
        primary.ProtectedGraphics.Add(line.Bounds);
        primary.Text = string.Join(Environment.NewLine, primary.Lines.Select(item => item.Text));
        primary.RecoveryRawText += (primary.RecoveryRawText.Length == 0 ? "" : Environment.NewLine) +
            first + Environment.NewLine + second;
        primary.RecoveryPasses += 2;
        primary.HasUncertainClippedText = true;
        primary.Message = "Text ready. A clipped label could not be read reliably and remains in the original image.";
        return true;
    }

    private static async Task<string> ReadSideLabelViewAsync(Bitmap source, Rectangle bounds, bool invert, CancellationToken token)
    {
        const int padding = 8;
        const int scale = 6;
        Color background = invert ? Color.White : Color.FromArgb(96, 96, 96);
        using var padded = new Bitmap(bounds.Width + 2 * padding, bounds.Height + 2 * padding, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(padded)) graphics.Clear(background);
        for (int y = 0; y < bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < bounds.Width; x++)
            {
                Color pixel = source.GetPixel(bounds.X + x, bounds.Y + y);
                if (pixel.A != 255) return "";
                if (invert) pixel = Color.FromArgb(255 - pixel.R, 255 - pixel.G, 255 - pixel.B);
                padded.SetPixel(x + padding, y + padding, pixel);
            }
        }
        using var scaled = new Bitmap(padded.Width * scale, padded.Height * scale, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(background);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(padded, 0, 0, scaled.Width, scaled.Height);
        }
        TextRecognitionResult read = await TextRecognition.RecognizeAsync(scaled, token).ConfigureAwait(false);
        return read.Available && read.Lines.Count == 1 ? read.RawText ?? "" : "";
    }
}
