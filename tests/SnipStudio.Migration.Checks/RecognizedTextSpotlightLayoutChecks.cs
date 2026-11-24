using SnipStudio.WinUI;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckRecognizedTextSpotlightLayout()
    {
        OcrLineSnapshot filename = Row(0, ".camera-viewer-shortcuts", new(39, 5, 138, 10));
        OcrLineSnapshot firstDate = Row(1, "11/23/2026 4:38 PM", new(278, 4, 103, 12));
        OcrLineSnapshot shortLabel = Row(2, "cs", new(39, 43, 13, 6));
        OcrLineSnapshot secondDate = Row(3, "11/23/2026 4:38 PM", new(278, 40, 103, 11));
        OcrLineSnapshot dateMissing = Row(4, "libvlc", new(39, 70, 16, 6));
        OcrLineSnapshot heading = Row(5, "A larger heading", new(39, 109, 170, 24));
        List<OcrBounds> explorer = RecognizedTextSpotlightLayout.CreateAreas(
            [filename, firstDate, shortLabel, secondDate, dateMissing, heading], 404, 160);
        Check(explorer.Count == 6 && explorer.Take(5).All(area => area.Height == 20) && explorer[5].Height == 32,
            "Explorer text uses a larger common reveal height while a larger type size retains its own band");
        Check(explorer[0].Y == explorer[1].Y && explorer[2].Y == explorer[3].Y,
            "Independently recognized file and date cells share exactly one reveal-band baseline");
        Check(explorer[0].X == 36 && explorer[0].X + explorer[0].Width == 180 && explorer[1].X == 275,
            "The original horizontal padding leaves icon pixels and wide column gutters under the scrim");
        Check(filename.Words[0].Bounds == new OcrBounds(39, 5, 138, 10) &&
              shortLabel.Words[0].Bounds == new OcrBounds(39, 43, 13, 6) &&
              secondDate.Words[0].Text == firstDate.Words[0].Text,
            "Reveal padding leaves the word geometry and duplicate recognition intact");

        List<OcrBounds> tight = RecognizedTextSpotlightLayout.CreateAreas(
            [Row(0, "Top", new(-1, -2, 5, 7)), Row(1, "Middle", new(3, 9, 8, 5)),
             Row(2, "Bottom", new(27, 19, 6, 8)), Row(3, "Outside", new(40, 0, 5, 5)),
             Row(4, "", new(7, 4, 2, 2)), Row(5, "Invalid", new(0, double.NaN, 10, 10))], 30, 25);
        Check(tight.Count == 3 && tight[0].X == 0 && tight[0].Y == 0 &&
              tight[2].X + tight[2].Width == 30 && tight[2].Y + tight[2].Height == 25,
            "The visual padding clips to the image and ignores empty, invalid, and off-image words");
        Check(tight[0].Y + tight[0].Height < tight[1].Y && tight[1].Y + tight[1].Height < tight[2].Y &&
              tight[1].Y <= 9 && tight[1].Y + tight[1].Height >= 14,
            "Crowded rows keep separate reveal bands while their original word pixels stay visible");
        Check(RecognizedTextSpotlightLayout.CreateAreas([filename], 0, 160).Count == 0,
            "A missing image has no recognized-text reveal bands");

        static OcrLineSnapshot Row(int index, string text, OcrBounds source) =>
            new(index, text, source, [new OcrWordSnapshot(text, source)]);
    }
}
