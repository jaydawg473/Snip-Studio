using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using SnipStudio;

internal static partial class Program
{
    private static void CheckCorroboratedInteriorOcrRecovery()
    {
        Size size = new(700, 340);
        string[] labels = ["AA-0101", "AB-0102", "AC-0103", "AD-0104", "AD-0104", "AF-0106", "AG-0107", "AH-0108"];
        const int split = 4;
        TextRecognitionResult primary = InteriorFixture(labels, split);
        var plan = RecognizedTextRecovery.FindInteriorRowRecovery(primary, size, CancellationToken.None);
        Check(plan is not null && plan.FirstRunIndex == split && plan.RunCount == 2 && plan.NarrowCrop.Right < plan.WideCrop.Right &&
            plan.WideCrop.Right < size.Width && plan.NarrowCrop.Height == size.Height,
            "Exactly one same-row fragment pair in a regular block gets two distinct source-pixel right-margin crops");

        TextRecognitionResult narrow = InteriorFixture(labels);
        string[] disagreement = (string[])labels.Clone();
        disagreement[split] = "AD-8104";
        TextRecognitionResult wider = InteriorFixture(disagreement);
        var before = primary.Lines.ToArray();
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, narrow, wider, plan!, _ => true, CancellationToken.None) == 0 &&
            primary.Lines.SequenceEqual(before), "Disputed rereads leave every original OCR fragment intact");

        disagreement[split] = labels[split];
        disagreement[0] = "AA-8101";
        wider = InteriorFixture(disagreement);
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, narrow, wider, plan!, _ => true, CancellationToken.None) == 0 &&
            primary.Lines.SequenceEqual(before), "An unrelated change elsewhere in either crop invalidates the recovery");
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, wider, wider, plan!, _ => true, CancellationToken.None) == 0 &&
            primary.Lines.SequenceEqual(before), "Even agreeing crops cannot change an already recognized neighboring row");

        wider = InteriorFixture(labels);
        wider.ProtectedGraphics.Add(new RectangleF(60, 132, 14, 14));
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, narrow, wider, plan!, _ => true, CancellationToken.None) == 0,
            "Protected graphics invalidate automatic interior text recovery");
        wider.ProtectedGraphics.Clear();
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, narrow, wider, plan!, _ => false, CancellationToken.None) == 0 &&
            primary.Lines.SequenceEqual(before), "A matching model guess with no supporting original raster ink is rejected");

        RectangleF? slot = null;
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, narrow, wider, plan!, bounds => { slot = bounds; return true; },
            CancellationToken.None) == 1 && slot == narrow.Lines[split].Words[1].Bounds &&
            primary.Lines.Select(line => line.Text).SequenceEqual(narrow.Lines.Select(line => line.Text), StringComparer.Ordinal),
            "Two exact rereads recover the visible interior word and preserve legitimate repeated rows in place");
        Check(primary.RawText == "initial provider text" && primary.RecoveryRawText == "raw alternate reads" &&
            primary.Lines[split].RecognitionClipBounds == plan!.NarrowCrop &&
            primary.Lines.Where((_, i) => i != split).SequenceEqual(before.Where((_, i) => i != split && i != split + 1)),
            "Accepted recovery leaves both diagnostic strings and all other original line objects unchanged");

        TextRecognitionResult single = InteriorFixture(labels, split, keepIncompleteTogether: true);
        var singlePlan = RecognizedTextRecovery.FindInteriorRowRecovery(single, size, CancellationToken.None);
        Check(singlePlan is { RunCount: 1, WordSlot: 1 } && singlePlan.FirstRunIndex == split &&
            RecognizedTextRecovery.MergeInteriorRowRecovery(single, narrow, wider, singlePlan, _ => true, CancellationToken.None) == 1 &&
            single.Lines.Select(line => line.Text).SequenceEqual(narrow.Lines.Select(line => line.Text), StringComparer.Ordinal),
            "A single row with proven missing ink between its unchanged prefix and suffix also recovers exactly one word");
        Check(RecognizedTextRecovery.FindInteriorRowRecovery(InteriorFixture(labels, split, keepIncompleteTogether: true,
                anotherIncomplete: 6), size, CancellationToken.None) is null,
            "Competing interior gaps invalidate the one-row recovery plan");
        CheckInteriorRetry(labels, split, singlePlan!);

        Check(RecognizedTextRecovery.FindInteriorRowRecovery(InteriorFixture(labels, columns: true), size, CancellationToken.None) is null,
            "Multiple separated columns are not merged into apparent missing-word rows");
        Check(RecognizedTextRecovery.FindInteriorRowRecovery(narrow, size, CancellationToken.None) is null,
            "Complete rows do not schedule any interior rereads");

        using Bitmap pixels = new(64, 40, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(pixels))
        {
            graphics.Clear(Color.White);
            for (int x = 12; x <= 30; x += 6) graphics.FillRectangle(Brushes.Black, x, 12, 3, 9);
        }
        Check(TextRecognition.TryReadOpaqueRightMargin(pixels, 46, CancellationToken.None, out int background) &&
            TextRecognition.HasInteriorWordInk(pixels, new RectangleF(10, 10, 28, 14), background, CancellationToken.None),
            "Original neutral ink runs in the added word box are distinct from the opaque blank crop margin");
        Check(!TextRecognition.HasInteriorWordInk(pixels, new RectangleF(40, 10, 20, 14), background, CancellationToken.None),
            "Blank pixels do not supply missing-word evidence");
        pixels.SetPixel(63, 39, Color.FromArgb(255, 253, 254, 255));
        Check(!TextRecognition.TryReadOpaqueRightMargin(pixels, 46, CancellationToken.None, out _),
            "One nonbackground pixel in the omitted area prevents both rereads even without a recognized box");

        var prepared = new Size(200, 100);
        var crop = new Rectangle(100, 50, 100, 75);
        RectangleF original = TextRecognition.SourceWordBounds(10, 20, 30, 10, prepared, crop, 0);
        Check(original == new RectangleF(105, 65, 15, 7.5f) &&
            original == TextRecognition.SourceWordBounds(10, 20, 30, 10, prepared, crop, null),
            "Zero or absent OCR angle preserves the exact geometry of a nonuniformly resized crop");
        RectangleF clockwise = TextRecognition.SourceWordBounds(10, 20, 30, 10, prepared, crop, 2);
        Check(Math.Abs(clockwise.Left - 105.37641f) < .001f && Math.Abs(clockwise.Top - 62.65799f) < .001f &&
            Math.Abs(clockwise.Right - 120.54177f) < .001f && Math.Abs(clockwise.Bottom - 70.93866f) < .001f,
            "WinRT word corners rotate clockwise around the prepared crop center before nonuniform scaling and source translation");
        RectangleF angled = TextRecognition.SourceWordBounds(10, 20, 30, 10, prepared, crop, -4.5);
        Check(Math.Abs(angled.Left - 103.96183f) < .001f && Math.Abs(angled.Top - 68.60002f) < .001f &&
            Math.Abs(angled.Right - 119.30789f) < .001f && Math.Abs(angled.Bottom - 77.84223f) < .001f,
            "Angled words use original source locations for copy and selection even beyond the safe in-place editing angle");
        Check(TextRecognition.SourceWordBounds(10, 20, 30, 10, prepared, crop, double.NaN) == original,
            "An invalid provider angle keeps finite unrotated bounds");
        RectangleF clipped = TextRecognition.SourceWordBounds(0, 0, 18, 18, prepared, crop, 2);
        Check(crop.Contains(Rectangle.Ceiling(clipped)) && clipped.Top == crop.Top && clipped.Width > 0,
            "Rotated bounds cannot select source pixels beyond the actual OCR crop");
        completedGroups.Add("Pixel-backed corroborated interior OCR recovery, original diagnostics, and source-aligned WinRT geometry");
    }

    private static void CheckInteriorRetry(string[] labels, int split, RecognizedTextRecovery.InteriorRowRecovery plan)
    {
        TextRecognitionResult primary = InteriorFixture(labels, split, keepIncompleteTogether: true);
        TextRecognitionResult omitted = InteriorFixture(labels, split, keepIncompleteTogether: true, anotherIncomplete: 6);
        TextRecognitionResult complete = InteriorFixture(labels);
        var before = primary.Lines.ToArray();
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, omitted, complete, plan, _ => true, CancellationToken.None) == 0 &&
            RecognizedTextRecovery.TryChooseInteriorRetry(primary, omitted, complete, plan, _ => true, CancellationToken.None, out bool keepWide) &&
            keepWide && primary.Lines.SequenceEqual(before),
            "A target and one control word omitted at their original positions permit a third view without mutating the primary result");
        Check(RecognizedTextRecovery.TryChooseInteriorRetry(primary, complete, primary, plan, _ => true, CancellationToken.None,
            out keepWide) && !keepWide, "A single exact omission permits retry when the other initial view is valid");

        Rectangle middle = plan.NarrowCrop;
        middle.Width += (plan.WideCrop.Width - middle.Width) / 2;
        var midpoint = new RecognizedTextRecovery.InteriorRowRecovery(split, plan.RunCount, plan.WordSlot, middle, plan.WideCrop);
        string[] contradictory = (string[])labels.Clone();
        contradictory[split] = "AD-8104";
        TextRecognitionResult contradiction = InteriorFixture(contradictory);
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, complete, contradiction, plan, _ => true, CancellationToken.None,
            out _) && RecognizedTextRecovery.MergeInteriorRowRecovery(primary, contradiction, complete, midpoint, _ => true,
            CancellationToken.None) == 0 && primary.Lines.SequenceEqual(before),
            "A different affirmative value does not permit retry or qualify as corroboration at the midpoint");
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, omitted, primary, plan, _ => true, CancellationToken.None,
            out _), "Two incomplete views do not authorize a third read");

        TextRecognitionResult changed = InteriorFixture(labels, split, keepIncompleteTogether: true, anotherIncomplete: 6);
        changed.Lines[6].Text += ".";
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, changed, complete, plan, _ => true, CancellationToken.None,
            out _), "Preserved words with changed punctuation are a disagreement rather than an omission");
        changed = InteriorFixture(labels, split, keepIncompleteTogether: true, anotherIncomplete: 6);
        changed.Lines.RemoveAt(0);
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, changed, complete, plan, _ => true, CancellationToken.None,
            out _), "Losing a whole row cannot authorize retry");
        changed = InteriorFixture(labels, split, keepIncompleteTogether: true, anotherIncomplete: 6);
        changed.Lines[7] = InteriorFixture(labels, 7, keepIncompleteTogether: true).Lines[7];
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, changed, complete, plan, _ => true, CancellationToken.None,
            out _), "More than two total missing words are outside the retry evidence bound");
        changed = InteriorFixture(labels, 6, keepIncompleteTogether: true);
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, changed, complete, plan, _ => true, CancellationToken.None,
            out _), "A control-only omission cannot schedule a third view for a target read by both candidates");

        TextRecognitionResult ambiguousPrimary = InteriorFixture(labels, split, keepIncompleteTogether: true);
        TextRecognitionResult ambiguousComplete = InteriorFixture(labels);
        TextRecognitionResult ambiguousPartial = InteriorFixture(labels, split, keepIncompleteTogether: true);
        TextWordRegion[] duplicateWords = ambiguousComplete.Lines[6].Words.ToArray();
        duplicateWords[1] = duplicateWords[0];
        ambiguousPrimary.Lines[6] = ambiguousComplete.Lines[6] = InteriorFixtureLine(duplicateWords);
        ambiguousPartial.Lines[6] = InteriorFixtureLine([duplicateWords[0], duplicateWords[2], duplicateWords[3]]);
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(ambiguousPrimary, ambiguousPartial, ambiguousComplete, plan, _ => true,
            CancellationToken.None, out _), "Overlapping identical anchors leave the omitted position ambiguous");

        omitted.ProtectedGraphics.Add(new RectangleF(60, 132, 14, 14));
        Check(!RecognizedTextRecovery.TryChooseInteriorRetry(primary, omitted, complete, plan, _ => true, CancellationToken.None,
            out _), "An incomplete read with protected graphics cannot authorize a third view");
        Check(RecognizedTextRecovery.MergeInteriorRowRecovery(primary, InteriorFixture(labels), complete, midpoint, _ => true,
            CancellationToken.None) == 1 && primary.Lines.Select(line => line.Text).SequenceEqual(complete.Lines.Select(line => line.Text),
            StringComparer.Ordinal) && primary.Lines[split].RecognitionClipBounds == middle &&
            primary.Lines.Where((_, index) => index != split).SequenceEqual(before.Where((_, index) => index != split)),
            "Two validated agreeing widths including the midpoint restore only the originally incomplete row");
    }

    private static TextRecognitionResult InteriorFixture(string[] labels, int incomplete = -1, bool columns = false,
        bool keepIncompleteTogether = false, int anotherIncomplete = -1)
    {
        var result = new TextRecognitionResult { Available = true, RawText = "initial provider text", RecoveryRawText = "raw alternate reads" };
        for (int index = 0; index < labels.Length; index++)
        {
            int y = 20 + index * 28;
            TextWordRegion[] words =
            [
                new() { Text = "Batch", Bounds = new RectangleF(20, y, 30, 16) },
                new() { Text = labels[index], Bounds = new RectangleF(58, y, 53, 16) },
                new() { Text = "was", Bounds = new RectangleF(120, y, 28, 16) },
                new() { Text = "ready", Bounds = new RectangleF(154, y, 42, 16) }
            ];
            if ((index == incomplete || index == anotherIncomplete) && keepIncompleteTogether)
                result.Lines.Add(InteriorFixtureLine([words[0], words[2], words[3]]));
            else if (index == incomplete || columns)
            {
                result.Lines.Add(InteriorFixtureLine(words[..1]));
                result.Lines.Add(InteriorFixtureLine(words[2..]));
            }
            else result.Lines.Add(InteriorFixtureLine(words));
        }
        result.Text = String.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
        return result;
    }

    private static TextLineRegion InteriorFixtureLine(TextWordRegion[] words)
    {
        var line = new TextLineRegion
        {
            Text = String.Join(" ", words.Select(word => word.Text)),
            Bounds = words.Select(word => word.Bounds).Aggregate(RectangleF.Union)
        };
        line.Words.AddRange(words);
        return line;
    }
}
