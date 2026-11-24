using System.Drawing;
using SnipStudio;

internal static partial class Program
{
    private static void CheckOcrFilenameSpacing()
    {
        const string stem = "FILE-000028";
        const string suffix = ".PdF";
        const string splitText = stem + " " + suffix;
        const string joinedText = stem + suffix;
        const string raw = "The original OCR provider returned split filename text.";
        const string recoveryRaw = "The independent provider read used the same separator.";

        using var image = new Bitmap(360, 150);
        var primary = new TextRecognitionResult { Available = true, RawText = raw, RecoveryRawText = recoveryRaw };
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            AddFilename(graphics, 6);
            AddFilename(graphics, 25, dark: true);
            AddFilename(graphics, 44, realSpace: true);
            AddFilename(graphics, 63, displacedDot: true);
            AddFilename(graphics, 82, originalSeparator: "  ");
            AddFilename(graphics, 101);
            AddFilename(graphics, 120, extraInk: true);
        }
        primary.ProtectedGraphics.Add(new RectangleF(125, 101, 6, 13));
        primary.Text = String.Join(Environment.NewLine, primary.Lines.Select(line => line.Text));
        RectangleF firstBounds = primary.Lines[0].Bounds;

        int changed = RecognizedTextRecovery.RecoverSpacedFilenameExtensions(primary, image, CancellationToken.None);
        Check(changed == 2 && primary.Lines.Count == 7 &&
            primary.Lines.Take(2).All(line => line.Text == joinedText && line.Words.Count == 1) &&
            primary.Lines[0].Words[0].Bounds == firstBounds,
            "Repeated rows with a real baseline period in the next glyph cell merge once per source row in both contrast polarities");
        Check(primary.Lines[2].Text == splitText && primary.Lines[2].Words.Count == 2,
            "A physical blank character cell before the period preserves an actual filename space");
        Check(primary.Lines[3].Text == splitText && primary.Lines[4].Text == stem + "  " + suffix &&
            primary.Lines[5].Text == splitText && primary.Lines[6].Text == splitText &&
            primary.Lines.Skip(3).All(line => line.Words.Count == 2),
            "A displaced dot, ambiguous interstitial ink, protected pixels, and an unverified multiple-space separator are preserved");
        Check(primary.Text == String.Join(Environment.NewLine, primary.Lines.Select(line => line.Text)) &&
            primary.RawText == raw && primary.RecoveryRawText == recoveryRaw,
            "Copy all preserves order, duplicates, case, digits, and both raw provider outputs");

        var selection = new TextOverlaySelection(primary.Lines);
        int hit = selection.HitTest(new PointF(146, 13), 0);
        selection.Select(hit, hit);
        Check(hit == 0 && selection.SelectedLineIndex == 0 && selection.SelectedText == joinedText &&
            selection.TryGetSelectedCharacterRange(joinedText, out int start, out int length) &&
            start == 0 && length == joinedText.Length,
            "A click inside the suffix selects the full recovered filename and its exact editable character range");
        Check(RecognizedTextRecovery.RecoverSpacedFilenameExtensions(primary, image, CancellationToken.None) == 0,
            "Repeating the filename recovery leaves both proven and preserved separators stable");
        Check(OcrFilenameSpacingHonorsCancellation(primary, image),
            "Filename recovery observes cancellation before inspecting or changing the source");

        void AddFilename(Graphics graphics, int top, bool dark = false, bool realSpace = false, bool displacedDot = false,
            string originalSeparator = " ", bool extraInk = false)
        {
            const int origin = 10, advance = 10;
            Brush ink = dark ? Brushes.White : Brushes.Black;
            if (dark) graphics.FillRectangle(Brushes.Black, 0, top - 1, image.Width, 16);

            string glyphs = joinedText;
            for (int index = 0; index < glyphs.Length; index++)
            {
                int left = origin + advance * (index + (realSpace && index >= stem.Length ? 1 : 0));
                if (glyphs[index] == '.')
                    graphics.FillRectangle(ink, left + 6, top + (displacedDot ? 3 : 9), 3, 2);
                else if (glyphs[index] == '-')
                    graphics.FillRectangle(ink, left + 4, top + 6, 6, 1);
                else
                    graphics.FillRectangle(ink, left + 4, top + 1, 6, 10);
            }
            if (extraInk) graphics.FillRectangle(ink, 122, top + 6, 1, 2);
            var leftBounds = RectangleF.FromLTRB(origin + 4, top, origin + advance * stem.Length, top + 13);
            int extensionOrigin = origin + advance * (stem.Length + (realSpace ? 1 : 0));
            var extensionBounds = RectangleF.FromLTRB(extensionOrigin + 6, top,
                extensionOrigin + advance * suffix.Length, top + 13);
            var row = new TextLineRegion
            {
                Text = stem + originalSeparator + suffix,
                Bounds = RectangleF.Union(leftBounds, extensionBounds)
            };
            row.Words.Add(new TextWordRegion { Text = stem, Bounds = leftBounds });
            row.Words.Add(new TextWordRegion { Text = suffix, Bounds = extensionBounds });
            primary.Lines.Add(row);
        }
    }

    private static bool OcrFilenameSpacingHonorsCancellation(TextRecognitionResult primary, Bitmap image)
    {
        try
        {
            RecognizedTextRecovery.RecoverSpacedFilenameExtensions(primary, image, new CancellationToken(true));
            return false;
        }
        catch (OperationCanceledException) { return true; }
    }
}
