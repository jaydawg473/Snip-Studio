using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using SnipStudio;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckInterruptedLineOcrRecovery()
    {
        Size size = new(755, 238);
        byte[] pixels = new byte[size.Width * size.Height];
        Array.Fill(pixels, (byte)255);
        PaintInterruptedGap(100);
        PaintInterruptedGap(150);

        var primary = new TextRecognitionResult
        {
            Available = true,
            Language = "en-US",
            RawText = "Retained first-pass result",
            RecoveryRawText = "Retained alternate diagnostic"
        };
        TextLineRegion above = InterruptedLine("Earlier row", new RectangleF(25, 50, 50, 10));
        TextLineRegion left = InterruptedLine("Run SBuild='Fast•", new RectangleF(31.5f, 100, 212.5f, 14),
            ("Run", new RectangleF(31.5f, 100, 22.5f, 11)),
            ("SBuild='Fast•", new RectangleF(59, 100, 185, 14)));
        TextLineRegion right = InterruptedLine("Senv:DO...", new RectangleF(661.5f, 100, 61, 12),
            ("Senv:DO...", new RectangleF(661.5f, 100, 61, 12)));
        TextLineRegion below = InterruptedLine("Later row", new RectangleF(25, 198, 50, 10));
        primary.Lines.AddRange([above, left, right, below]);

        Rectangle source = new(29, 98, 698, 18);
        Rectangle leftSource = new(29, 98, 216, 18);
        Rectangle rightSource = new(658, 98, 70, 18);
        var query = OfflineOcrEngine.FindInterruptedLine(primary, size, pixels, CancellationToken.None);
        Check(query is not null && ReferenceEquals(query.Left, left) && ReferenceEquals(query.Right, right) &&
            query.Source == source && query.LeftProofSource == leftSource && query.RightProofSource == rightSource,
            "One aligned source-ink gap produces an exact full row crop and separate proof crops");

        var blank = new byte[pixels.Length];
        Array.Fill(blank, (byte)255);
        Check(OfflineOcrEngine.FindInterruptedLine(primary, size, blank, CancellationToken.None) is null,
            "An aligned but blank column gutter does not schedule another OCR pass");
        Check(OfflineOcrEngine.FindInterruptedLine(primary, size, new byte[pixels.Length], CancellationToken.None) is null,
            "A dark panel or photograph cannot masquerade as ink inside a light gap");
        primary.TextAngle = 2;
        Check(OfflineOcrEngine.FindInterruptedLine(primary, size, pixels, CancellationToken.None) is null,
            "A tilted OCR row is unsuitable for one horizontal recovery crop");
        primary.TextAngle = null;

        primary.ProtectedGraphics.Add(new RectangleF(300, 102, 8, 8));
        Check(OfflineOcrEngine.FindInterruptedLine(primary, size, pixels, CancellationToken.None) is null,
            "Protected graphic regions veto a recovery that would enclose them");
        primary.ProtectedGraphics.Clear();
        var third = InterruptedLine("Third row", new RectangleF(307, 103, 46, 9));
        primary.Lines.Insert(2, third);
        Check(OfflineOcrEngine.FindInterruptedLine(primary, size, pixels, CancellationToken.None) is null,
            "An already-recognized third row blocks the interrupted row merge");
        primary.Lines.Remove(third);

        var anotherLeft = InterruptedLine("Run SBuild='Fast•", new RectangleF(31.5f, 150, 212.5f, 14),
            ("Run", new RectangleF(31.5f, 150, 22.5f, 11)),
            ("SBuild='Fast•", new RectangleF(59, 150, 185, 14)));
        var anotherRight = InterruptedLine("Senv:DO...", new RectangleF(661.5f, 150, 61, 12),
            ("Senv:DO...", new RectangleF(661.5f, 150, 61, 12)));
        primary.Lines.AddRange([anotherLeft, anotherRight]);
        Check(OfflineOcrEngine.FindInterruptedLine(primary, size, pixels, CancellationToken.None) is null,
            "Two otherwise eligible source gaps prevent any speculative read");
        primary.Lines.Remove(anotherLeft);
        primary.Lines.Remove(anotherRight);

        var complete = InterruptedResponse("gap-row", source, 4,
            ("Run", left.Words[0].Bounds), ("$Build='Fast'", left.Words[1].Bounds),
            ("ID-0007", new RectangleF(250, 100, 406, 12)), ("$env:DO...", right.Words[0].Bounds));
        var verifiedLeft = InterruptedResponse("gap-anchor", leftSource, 6,
            ("Run", left.Words[0].Bounds), ("$Build='Fast'", left.Words[1].Bounds));
        var disputedLeft = InterruptedResponse("gap-anchor", leftSource, 3,
            ("Run", left.Words[0].Bounds), ("SBuild='Fast•", left.Words[1].Bounds));
        verifiedLeft = verifiedLeft with { Rows = [.. verifiedLeft.Rows, .. disputedLeft.Rows] };
        var verifiedRight = InterruptedResponse("gap-anchor", rightSource, 4, ("$env:DO..", right.Words[0].Bounds));
        var secondRightView = InterruptedResponse("gap-anchor", rightSource, 2, ("$env:DO...", right.Words[0].Bounds));
        verifiedRight = verifiedRight with { Rows = [.. verifiedRight.Rows, .. secondRightView.Rows] };

        bool Merge(OfflineOcrEngine.Response reply, OfflineOcrEngine.Response? first = null,
            OfflineOcrEngine.Response? last = null) =>
            OfflineOcrEngine.MergeInterruptedLine(primary, query!, reply, size, pixels, CancellationToken.None, first, last);

        Check(!Merge(complete) && !Merge(complete, verifiedLeft),
            "A candidate with revised boundary glyphs requires independent evidence for both endpoints");
        Check(!Merge(complete with { Rows = [complete.Rows[0] with { Agreement = 3 }] }, verifiedLeft, verifiedRight),
            "Three agreeing whole-row views cannot authorize an interrupted row merge");
        Check(!Merge(complete with { Panel = complete.Panel! with { Width = source.Width + 1 } }, verifiedLeft, verifiedRight),
            "A candidate must report the exact bounded local source frame");

        var tiedRight = InterruptedResponse("gap-anchor", rightSource, 2, ("Senv:DO...", right.Words[0].Bounds));
        var losingRight = InterruptedResponse("gap-anchor", rightSource, 3, ("Senv:DO...", right.Words[0].Bounds));
        Check(!Merge(complete, verifiedLeft, verifiedRight with { Rows = [.. secondRightView.Rows, .. tiedRight.Rows] }) &&
            !Merge(complete, verifiedLeft, verifiedRight with { Rows = [.. secondRightView.Rows, .. losingRight.Rows] }),
            "A tied or losing dollar hypothesis keeps native text intact");
        Check(!Merge(complete, verifiedLeft with { Rows = [.. verifiedLeft.Rows, .. disputedLeft.Rows] }, verifiedRight),
            "Endpoint vote counts cannot exceed the ten source views");
        var displaced = InterruptedResponse("gap-anchor", leftSource, 2,
            ("Run", left.Words[0].Bounds), ("$Build='Fast'", RectangleF.FromLTRB(67, 100, 243, 114)));
        Check(!Merge(complete, displaced, verifiedRight), "A valid literal without matching source geometry cannot prove an anchor");

        var changedCase = InterruptedResponse("gap-row", source, 4,
            ("Run", left.Words[0].Bounds), ("$Build='fast'", left.Words[1].Bounds),
            ("ID-0007", new RectangleF(250, 100, 406, 12)), ("$env:DO...", right.Words[0].Bounds));
        var proofChangedCase = InterruptedResponse("gap-anchor", leftSource, 5,
            ("Run", left.Words[0].Bounds), ("$Build='fast'", left.Words[1].Bounds));
        var changedPeriods = InterruptedResponse("gap-row", source, 4,
            ("Run", left.Words[0].Bounds), ("$Build='Fast'", left.Words[1].Bounds),
            ("ID-0007", new RectangleF(250, 100, 406, 12)), ("$env:DO..", right.Words[0].Bounds));
        Check(!Merge(changedCase, proofChangedCase, verifiedRight) &&
            !Merge(changedPeriods, verifiedLeft, verifiedRight),
            "Consensus cannot rewrite source anchor casing or punctuation that the native line already retains");

        var unsupported = InterruptedResponse("gap-row", source, 4,
            ("Run", left.Words[0].Bounds), ("$Build='Fast'", left.Words[1].Bounds),
            ("ID-0007", new RectangleF(250, 98, 406, 2)), ("$env:DO...", right.Words[0].Bounds));
        Check(!Merge(unsupported, verifiedLeft, verifiedRight), "Recovered words need original-source ink inside their own boxes");
        Check(primary.Lines.SequenceEqual(new[] { above, left, right, below }) && primary.RecoveryPasses == 0,
            "Rejected recovery leaves the same row objects and diagnostics intact");

        Check(Merge(complete, verifiedLeft, verifiedRight) && primary.Lines.Count == 3 &&
            primary.Lines[0] == above && primary.Lines[2] == below &&
            primary.Lines[1].Text == "Run $Build='Fast' ID-0007 $env:DO..." &&
            primary.Lines[1].RecognitionClipBounds == source &&
            primary.Lines[1].Words[0] == left.Words[0] &&
            primary.Lines[1].Words[1].Bounds == left.Words[1].Bounds &&
            primary.Lines[1].Words[3].Bounds == right.Words[0].Bounds,
            "A corroborated row restores the omitted identifier without moving either original endpoint or neighboring rows");
        Check(primary.RawText == "Retained first-pass result" &&
            primary.RecoveryRawText == "Retained alternate diagnostic" + Environment.NewLine + complete.Rows[0].Text &&
            primary.RecoveryPasses == 1 && primary.Text.Contains("ID-0007", StringComparison.Ordinal) &&
            !Merge(complete, verifiedLeft, verifiedRight),
            "The accepted candidate updates copy text without altering original diagnostics or allowing a stale replay");
        completedGroups.Add("Interrupted-line OCR, isolated source ink, independently verified endpoint glyphs and exact original word geometry");

        void PaintInterruptedGap(int y)
        {
            for (int x = 250; x < 656; x++)
                if (x % 8 < 6)
                    for (int dy = 2; dy < 8; dy++)
                        pixels[(y + dy) * size.Width + x] = 140;
        }
    }

    private static TextLineRegion InterruptedLine(
        string text, RectangleF bounds, params (string Text, RectangleF Bounds)[] words)
    {
        var line = new TextLineRegion { Text = text, Bounds = bounds };
        line.Words.AddRange(words.Select(word => new TextWordRegion { Text = word.Text, Bounds = word.Bounds }));
        return line;
    }

    private static OfflineOcrEngine.Response InterruptedResponse(string kind, Rectangle source, int agreement,
        params (string Text, RectangleF Bounds)[] words)
    {
        OfflineOcrEngine.Box Local(RectangleF bounds) => new(bounds.X - source.X, bounds.Y - source.Y, bounds.Width, bounds.Height);
        var offered = words.Select(word => new OfflineOcrEngine.Word(word.Text, Local(word.Bounds))).ToArray();
        RectangleF span = words.Select(word => word.Bounds).Aggregate(RectangleF.Union);
        var row = new OfflineOcrEngine.Row(string.Join(" ", words.Select(word => word.Text)), Local(span), offered, .999, agreement);
        return new OfflineOcrEngine.Response(kind, new OfflineOcrEngine.Box(0, 0, source.Width, source.Height), [row]);
    }
}
