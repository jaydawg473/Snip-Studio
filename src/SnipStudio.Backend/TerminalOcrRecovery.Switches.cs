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
    internal static async Task ReviewSwitchesAsync(TextRecognitionResult primary, OfflineOcrEngine.Response response,
        Bitmap source, CancellationToken token)
    {
        if (response.Kind != "dark" || response.Panel is null || response.Rows is not { Length: > 0 and <= 64 }) return;
        RectangleF panel = response.Panel.ToRectangle();
        if (!Valid(panel, new RectangleF(PointF.Empty, source.Size)) || MeasurePitch(response.Rows, panel) <= 0) return;

        int reviewed = 0;
        foreach (var line in primary.Lines)
        {
            token.ThrowIfCancellationRequested();
            if (!Valid(line.Bounds, panel) || !line.ExcludedGraphicBounds.IsEmpty) continue;
            foreach (var word in line.Words)
            {
                string original = word.Text;
                if (AmbiguousSwitchIndex(original) < 0 || !Valid(word.Bounds, line.Bounds) ||
                    primary.ProtectedGraphics.Any(word.Bounds.IntersectsWith)) continue;
                int offset = line.Text.IndexOf(original, StringComparison.Ordinal);
                if (offset < 0 || (offset > 0 && line.Text[offset - 1] != ' ') ||
                    (offset + original.Length < line.Text.Length && line.Text[offset + original.Length] != ' ') ||
                    line.Text.IndexOf(original, offset + original.Length, StringComparison.Ordinal) >= 0) continue;
                if (!response.Rows.Any(row => row is { Agreement: 3, Confidence: >= .98 and <= 1, Bounds: not null } &&
                    row.Words is not null && Valid(row.Bounds.ToRectangle(), panel) &&
                    row.Words.Any(candidate => candidate is { Bounds: not null } && candidate.Text == original &&
                        candidate.Bounds.ToRectangle() == word.Bounds))) continue;

                Rectangle region = Rectangle.FromLTRB((int)Math.Floor(word.Bounds.Left), (int)Math.Floor(word.Bounds.Top),
                    (int)Math.Ceiling(word.Bounds.Right), (int)Math.Ceiling(word.Bounds.Bottom));
                if (region.Width is < 16 or > 140 || region.Height is < 6 or > 24 || !panel.Contains(region)) continue;
                if (++reviewed > 2) return;

                string first = await ReadSwitchAsync(source, region, invert: false, token).ConfigureAwait(false);
                string second = await ReadSwitchAsync(source, region, invert: true, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!TrySelectSwitchCorrection(original, first, second, out string replacement)) continue;
                line.Text = line.Text[..offset] + replacement + line.Text[(offset + original.Length)..];
                word.Text = replacement;
                primary.Text = string.Join(Environment.NewLine, primary.Lines.Select(item => item.Text));
                primary.RecoveryRawText += (primary.RecoveryRawText.Length == 0 ? "" : Environment.NewLine) +
                    first + Environment.NewLine + second;
                primary.RecoveryPasses += 2;
                primary.Message = "Text ready. Focused local reads recovered a terminal switch. Review identifiers against the image.";
            }
        }
    }

    internal static bool TrySelectSwitchCorrection(string original, string first, string second, out string replacement)
    {
        replacement = original;
        int ambiguous = AmbiguousSwitchIndex(original);
        if (ambiguous < 0 || first.Length != original.Length || first != second || first[ambiguous] != 'l' ||
            !first.AsSpan(0, ambiguous).SequenceEqual(original.AsSpan(0, ambiguous)) ||
            !first.AsSpan(ambiguous + 1).SequenceEqual(original.AsSpan(ambiguous + 1))) return false;
        replacement = first;
        return true;
    }

    private static int AmbiguousSwitchIndex(string original)
    {
        if (original.Length is < 5 or > 24 || original[0] != '/') return -1;
        int ambiguous = original.IndexOf('1');
        if (ambiguous < 2 || ambiguous >= original.Length - 1) return -1;
        for (int index = 1; index < original.Length; index++)
            if (index != ambiguous && !char.IsAsciiLetter(original[index])) return -1;
        return ambiguous;
    }

    private static async Task<string> ReadSwitchAsync(Bitmap source, Rectangle bounds, bool invert, CancellationToken token)
    {
        const int padding = 8;
        const int scale = 3;
        Color background = invert ? Color.White : Color.Black;
        using var crop = new Bitmap(bounds.Width + 2 * padding, bounds.Height + 2 * padding, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(crop)) graphics.Clear(background);
        for (int y = 0; y < bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < bounds.Width; x++)
            {
                Color pixel = source.GetPixel(bounds.X + x, bounds.Y + y);
                if (pixel.A != 255) return "";
                if (invert) pixel = Color.FromArgb(255 - pixel.R, 255 - pixel.G, 255 - pixel.B);
                crop.SetPixel(x + padding, y + padding, pixel);
            }
        }
        using var scaled = new Bitmap(crop.Width * scale, crop.Height * scale, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(background);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(crop, 0, 0, scaled.Width, scaled.Height);
        }
        TextRecognitionResult read = await TextRecognition.RecognizeAsync(scaled, token).ConfigureAwait(false);
        return read.Available ? read.RawText ?? "" : "";
    }
}
