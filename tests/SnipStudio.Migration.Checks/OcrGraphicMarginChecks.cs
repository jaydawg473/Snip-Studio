using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using SnipStudio;

internal static partial class Program
{
    private static void CheckGraphicAdjacentOcrRecovery()
    {
        using Bitmap source = new(960, 340, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.OrangeRed, 419, 115, 35, 46);
        }

        Rectangle narrow = new(0, 0, 384, source.Height);
        Rectangle wide = new(0, 0, 448, source.Height);
        var original = new RecognizedTextRecovery.InteriorRowRecovery(7, 1, 1, narrow, wide);
        CancellationToken token = CancellationToken.None;
        Check(!TextRecognition.TryReadOpaqueRightMargin(source, narrow.Right, token, out _) &&
            !TextRecognition.TryReadOpaqueRightMargin(source, wide.Right, token, out _),
            "Original OCR recovery widths cannot discard a colored graphic");

        var expanded = TextRecognition.ExpandInteriorRowCrops(source, original, 4096, token, out int background);
        Check(expanded is not null && expanded.NarrowCrop.Width == 480 && expanded.WideCrop.Width == 544 &&
            expanded.NarrowCrop.Contains(new Rectangle(419, 115, 35, 46)) &&
            expanded.WideCrop.Contains(new Rectangle(419, 115, 35, 46)) &&
            TextRecognition.TryReadOpaqueRightMargin(source, expanded.NarrowCrop.Right, token, out int inspectedBackground) &&
            inspectedBackground == background && background == Color.White.ToArgb(),
            "Both expanded OCR views retain the complete graphic and discard only independently verified opaque white pixels");
        Check(expanded is not null && expanded.FirstRunIndex == original.FirstRunIndex &&
            expanded.RunCount == original.RunCount && expanded.WordSlot == original.WordSlot &&
            original.NarrowCrop == narrow && original.WideCrop == wide,
            "Outward expansion retains the original row position, missing word and recovery plan");
        Check(TextRecognition.ExpandInteriorRowCrops(source, original, 512, token, out _) is null,
            "No wider OCR read is proposed when the recognizer dimension cannot accommodate it");

        source.SetPixel(498, 280, Color.Black);
        expanded = TextRecognition.ExpandInteriorRowCrops(source, original, 4096, token, out _);
        Check(expanded is not null && expanded.NarrowCrop.Width == 512 && expanded.WideCrop.Width == 576 &&
            expanded.NarrowCrop.Contains(498, 280),
            "Every row of the proposed OCR margin contributes to its rightmost source-pixel boundary");
        source.SetPixel(512, 280, Color.Black);
        Check(TextRecognition.ExpandInteriorRowCrops(source, original, 4096, token, out _) is null,
            "A distant mark cannot trigger unbounded crop expansion or lose source pixels");
        source.SetPixel(512, 280, Color.White);
        source.SetPixel(source.Width - 1, 0, Color.Transparent);
        Check(TextRecognition.ExpandInteriorRowCrops(source, original, 4096, token, out _) is null,
            "A transparent edge cannot establish an opaque background for OCR recovery");

        bool canceled = false;
        try { TextRecognition.ExpandInteriorRowCrops(source, original, 4096, new CancellationToken(true), out _); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Canceled interior recovery does not scan the source margin");
    }
}
