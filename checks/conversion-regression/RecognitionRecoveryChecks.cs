using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using SnipStudio;

// Geometric merge-policy tests use explicitly supplied OCR observations. They
// do not claim that Windows recognizes these identifiers or languages exactly.
internal static class RecognitionRecoveryChecks
{
    private static int checks;
    private static readonly Rectangle Source = new Rectangle(0, 0, 1000, 600);
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    private static TextLineRegion Line(string text, float x, float y, float width, float height)
    {
        TextLineRegion line = new TextLineRegion { Text = text, Bounds = new RectangleF(x, y, width, height) };
        line.Words.Add(new TextWordRegion { Text = text, Bounds = line.Bounds });
        return line;
    }
    private static TextRecognitionResult Result(params TextLineRegion[] lines)
    {
        TextRecognitionResult result = new TextRecognitionResult { Available = true, Language = "en-US", RawText = "native observations" };
        result.Lines.AddRange(lines);
        List<string> text = new List<string>();
        foreach (TextLineRegion line in lines) text.Add(line.Text);
        result.Text = String.Join(Environment.NewLine, text.ToArray());
        return result;
    }
    private static int Merge(TextRecognitionResult primary, TextRecognitionResult candidate, CancellationToken token)
    {
        Type recovery = typeof(TextRecognitionResult).Assembly.GetType("SnipStudio.RecognizedTextRecovery", true);
        MethodInfo merge = recovery.GetMethod("MergeMissing", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (merge == null) throw new InvalidOperationException("The recovery merge contract is unavailable.");
        try { return (int)merge.Invoke(null, new object[] { primary, candidate, Source, token }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static string Fingerprint(TextRecognitionResult result)
    {
        StringBuilder value = new StringBuilder();
        value.Append(result.RawText).Append('|').Append(result.Text).Append('|').Append(result.Language).Append('|').Append(result.TextAngle);
        foreach (TextLineRegion line in result.Lines)
        {
            value.Append("\nL:").Append(line.Text).Append(':').Append(line.Bounds).Append(':').Append(line.ExcludedGraphicBounds).Append(':').Append(line.RecognitionClipBounds);
            foreach (TextWordRegion word in line.Words) value.Append("\nW:").Append(word.Text).Append(':').Append(word.Bounds);
        }
        return value.ToString();
    }
    private static TextLineRegion Find(TextRecognitionResult result, string text)
    { foreach (TextLineRegion line in result.Lines) if (line.Text == text) return line; return null; }
    private static void SuffixAndColumns()
    {
        TextLineRegion date = Line("11/18/2026 2:43 PM", 420, 30, 110, 12);
        TextLineRegion type = Line("PDF Document", 650, 30, 100, 12);
        TextRecognitionResult primary = Result(Line(".pdf", 264, 30, 26, 12), date, type);
        primary.RawText = "unchanged primary native raw";
        TextLineRegion recovered = Line("N0001_@literal.pdf", 50, 30, 240, 12);
        recovered.ExcludedGraphicBounds = new RectangleF(25, 28, 14, 16);
        TextRecognitionResult candidate = Result(recovered);
        string originalCandidate = Fingerprint(candidate);
        Check(Merge(primary, candidate, CancellationToken.None) == 1, "contained suffix gains one complete native run");
        Check(primary.Lines.Count == 3, "suffix replacement does not create a duplicate fourth line");
        Check(Find(primary, recovered.Text) != null && Find(primary, ".pdf") == null, "contained suffix is replaced exactly once");
        Check(Find(primary, date.Text) == date && Find(primary, type.Text) == type, "existing date and type cells retain identity and values");
        Check(primary.RawText == "unchanged primary native raw", "primary native raw provenance is retained");
        Check(Fingerprint(candidate) == originalCandidate, "accepted candidate observations are not mutated");
        TextLineRegion accepted = Find(primary, recovered.Text);
        Check(accepted.Bounds == recovered.Bounds && accepted.ExcludedGraphicBounds == recovered.ExcludedGraphicBounds, "accepted source coordinates and icon exclusion are retained");
        Check(!Object.ReferenceEquals(accepted, recovered) && !Object.ReferenceEquals(accepted.Words[0], recovered.Words[0]), "accepted candidate line and words are detached clones");
        string merged = Fingerprint(primary);
        Check(Merge(primary, candidate, CancellationToken.None) == 0 && Fingerprint(primary) == merged, "remerging the same native run is idempotent");
        recovered.Words[0].Text = "mutated caller word";
        Check(accepted.Words[0].Text == "N0001_@literal.pdf", "later candidate mutation cannot alter accepted words");
    }
    private static void Reject(TextRecognitionResult primary, TextRecognitionResult candidate, string label)
    {
        string before = Fingerprint(primary), candidateBefore = Fingerprint(candidate);
        Check(Merge(primary, candidate, CancellationToken.None) == 0, label + ": no replacement accepted");
        Check(Fingerprint(primary) == before, label + ": primary values and geometry unchanged");
        Check(Fingerprint(candidate) == candidateBefore, label + ": candidate values and geometry unchanged");
    }
    private static void ConflictsAndBridges()
    {
        Reject(Result(Line("Article-000123.pdf", 50, 30, 240, 12)), Result(Line("Article-000128.pdf", 50, 30, 240, 12)), "conflicting complete identifier");
        Reject(Result(Line("ABC0000123456789", 150, 30, 100, 12)), Result(Line("Other native run ABC0000123456789", 50, 30, 200, 12)),
            "medium-width complete identifier is not treated as a short suffix");
        Reject(Result(Line("Article-000123.pdf", 50, 30, 240, 12), Line("11/18/2026", 420, 30, 110, 12)),
            Result(Line("Article-000123.pdf 11/18/2026", 50, 30, 480, 12)), "candidate bridging filename and date columns");
        Reject(Result(Line("Article-000123.pdf", 50, 30, 240, 12), Line("Article-000124.pdf", 50, 60, 240, 12)),
            Result(Line("Article-000123.pdf Article-000124.pdf", 50, 30, 240, 42)), "candidate bridging adjacent rows");
        Reject(Result(Line(".pdf", 264, 30, 26, 12)), Result(Line("Article-000123.pdf", -1, 30, 291, 12)), "out-of-source candidate");
        Reject(Result(Line(".pdf", 264, 30, 26, 12)), Result(Line("Article-000123.pdf", Single.NaN, 30, 291, 12)), "nonfinite candidate");
        TextLineRegion invalidWord = Line("Article-000123.pdf", 50, 30, 240, 12);
        invalidWord.Words[0].Bounds = new RectangleF(1500, 30, 240, 12);
        Reject(Result(Line(".pdf", 264, 30, 26, 12)), Result(invalidWord), "out-of-source word");
        invalidWord = Line("Article-000123.pdf", 50, 30, 240, 12);
        invalidWord.Words[0].Bounds = new RectangleF(350, 30, 20, 12);
        Reject(Result(Line(".pdf", 264, 30, 26, 12)), Result(invalidWord), "word outside its claimed line");
    }
    private static void ExistingGraphicProtection()
    {
        TextLineRegion suffix = Line(".pdf", 264, 30, 26, 12);
        suffix.ExcludedGraphicBounds = new RectangleF(25, 28, 14, 16);
        TextRecognitionResult primary = Result(suffix);
        TextLineRegion fromIconFreePane = Line("N0001_report.pdf", 50, 30, 240, 12);
        Check(Merge(primary, Result(fromIconFreePane), CancellationToken.None) == 1, "icon-free pane can recover a suffix-only row");
        TextLineRegion accepted = Find(primary, fromIconFreePane.Text);
        Check(accepted != null && accepted.ExcludedGraphicBounds.Contains(suffix.ExcludedGraphicBounds),
            "replacement retains primary icon protection when candidate has none");
    }
    private static void RecognitionClipProtection()
    {
        TextLineRegion suffix = Line(".pdf", 264, 30, 26, 12);
        suffix.RecognitionClipBounds = new RectangleF(200, 0, 800, 600);
        TextLineRegion complete = Line("N0001_report.pdf", 50, 30, 240, 12);
        complete.RecognitionClipBounds = new RectangleF(50, 0, 950, 600);
        Reject(Result(suffix), Result(complete), "preserved clip must contain an accepted replacement");
        suffix = Line(".pdf", 264, 30, 26, 12);
        suffix.RecognitionClipBounds = new RectangleF(200, 0, 100, 600);
        complete = Line("N0001_report.pdf", 50, 30, 240, 12);
        complete.RecognitionClipBounds = new RectangleF(0, 0, 180, 600);
        Reject(Result(suffix), Result(complete), "disjoint clip intersection must not become unrestricted empty clip");
        complete = Line("N0001_report.pdf", 51, 90, 239, 12);
        complete.RecognitionClipBounds = new RectangleF(50, 0, 950, 600);
        TextRecognitionResult primary = Result(Line("keep", 50, 30, 100, 12));
        Check(Merge(primary, Result(complete), CancellationToken.None) == 1, "disjoint native pane run is accepted inside its source clip");
        Check(Find(primary, complete.Text).RecognitionClipBounds == complete.RecognitionClipBounds, "accepted pane boundary is cloned exactly");
    }
    private static void ColumnReadingOrder()
    {
        TextLineRegion first = Line("A0001.pdf", 50, 30, 220, 12);
        TextLineRegion third = Line("A0003.pdf", 50, 90, 220, 12);
        TextRecognitionResult primary = Result(first, third,
            Line("11/16/2026", 420, 30, 110, 12), Line("11/17/2026", 420, 60, 110, 12), Line("11/18/2026", 420, 90, 110, 12),
            Line("PDF first", 650, 30, 100, 12), Line("PDF second", 650, 60, 100, 12), Line("PDF third", 650, 90, 100, 12));
        TextLineRegion[] existing = primary.Lines.ToArray();
        Check(Merge(primary, Result(Line("A0002.pdf", 50, 60, 220, 12)), CancellationToken.None) == 1, "missing filename row is recovered in a multi-column table");
        int last = -1;
        foreach (TextLineRegion line in existing)
        {
            int index = primary.Lines.IndexOf(line);
            Check(index > last, "existing native column-major relative order remains intact: " + line.Text);
            last = index;
        }
        TextOverlaySelection selection = new TextOverlaySelection(primary.Lines);
        int start = selection.HitTest(new PointF(100, 35), 0), end = selection.HitTest(new PointF(100, 95), 0);
        Check(start >= 0 && end >= 0, "filename drag endpoints remain selectable");
        selection.Select(start, end);
        Check(selection.SelectedText == "A0001.pdf" + Environment.NewLine + "A0002.pdf" + Environment.NewLine + "A0003.pdf",
            "dragging down filenames after recovery selects no date or type cells");
    }
    private static void SparseSuffixReadingOrder()
    {
        TextRecognitionResult primary = Result(Line("(1).pdf", 246, 40, 34, 12), Line("(1).pdf", 251, 96, 34, 12), Line(".pdf", 246, 152, 34, 12));
        TextRecognitionResult candidate = Result(
            Line("File-0001_report.pdf", 6, 12, 254, 12), Line("File-0002_report.pdf", 6, 40, 274, 12),
            Line("File-0003_report.pdf", 6, 68, 254, 12), Line("File-0004_report.pdf", 6, 96, 279, 12),
            Line("File-0005_report.pdf", 6, 124, 254, 12), Line("File-0006_report.pdf", 6, 152, 274, 12));
        Check(Merge(primary, candidate, CancellationToken.None) == 6, "varied-width native recovery accepts six filename bodies");
        Check(primary.Lines.Count == 6, "varied-width native recovery retains six distinct rows");
        for (int index = 0; index < candidate.Lines.Count; index++)
            Check(primary.Lines[index].Text == candidate.Lines[index].Text && primary.Lines[index].Bounds.Top == candidate.Lines[index].Bounds.Top,
                "sparse suffix observations recover in top-to-bottom source order: row " + (index + 1));
        TextOverlaySelection selection = new TextOverlaySelection(primary.Lines);
        selection.Select(selection.HitTest(new PointF(10, 17), 0), selection.HitTest(new PointF(10, 45), 0));
        Check(selection.SelectedText == candidate.Lines[0].Text + Environment.NewLine + candidate.Lines[1].Text,
            "first-to-second filename drag includes exactly the first two recovered rows");
    }
    private static void LegitimateDuplicatesAndReadingOrder()
    {
        TextRecognitionResult duplicate = Result(Line("same.pdf", 50, 30, 140, 12), Line("same.pdf", 50, 60, 140, 12));
        Check(Merge(duplicate, Result(Line("same.pdf", 50, 90, 140, 12)), CancellationToken.None) == 1, "same text on a separate row is a legitimate new observation");
        Check(duplicate.Lines.Count == 3, "duplicate source values remain three distinct rows");
        TextLineRegion rtl = new TextLineRegion { Text = "\u05D0\u05D1\u05D2 \u05D3\u05D4\u05D5", Bounds = new RectangleF(80, 120, 130, 12) };
        rtl.Words.Add(new TextWordRegion { Text = "\u05D0\u05D1\u05D2", Bounds = new RectangleF(180, 120, 30, 12) });
        rtl.Words.Add(new TextWordRegion { Text = "\u05D3\u05D4\u05D5", Bounds = new RectangleF(80, 120, 30, 12) });
        TextRecognitionResult primary = Result(Line("keep", 50, 30, 100, 12));
        Check(Merge(primary, Result(rtl), CancellationToken.None) == 1, "disjoint RTL observation can be retained");
        TextLineRegion accepted = Find(primary, rtl.Text);
        Check(accepted != null && accepted.Words[0].Text == rtl.Words[0].Text && accepted.Words[0].Bounds.X == 180 && accepted.Words[1].Bounds.X == 80,
            "RTL provider word order is retained without sorting words by X");
        foreach (string text in new string[] { "@real-address", "N00001-CaseSensitive", "\u53E3 0000123" })
        {
            primary = Result(Line("keep", 50, 30, 100, 12));
            Check(Merge(primary, Result(Line(text, 50, 90, 240, 12)), CancellationToken.None) == 1, "disjoint real prefix is retained: " + text);
            Check(Find(primary, text) != null && Find(primary, text).Words[0].Text == text, "prefix, leading zero and case remain exact: " + text);
        }
    }
    private static void Canceled()
    {
        TextRecognitionResult primary = Result(Line(".pdf", 264, 30, 26, 12));
        TextRecognitionResult candidate = Result(Line("Article-000123.pdf", 50, 30, 240, 12));
        string before = Fingerprint(primary), candidateBefore = Fingerprint(candidate);
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool canceled = false;
            try { Merge(primary, candidate, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "canceled merge throws cancellation rather than reporting success");
            Check(Fingerprint(primary) == before && Fingerprint(candidate) == candidateBefore, "canceled merge leaves both observations unchanged");
        }
    }
    private static int Main()
    {
        try
        {
            SuffixAndColumns();
            ConflictsAndBridges();
            ExistingGraphicProtection();
            RecognitionClipProtection();
            ColumnReadingOrder();
            SparseSuffixReadingOrder();
            LegitimateDuplicatesAndReadingOrder();
            Canceled();
            Console.WriteLine("PASS: " + checks + " recognition recovery checks; deterministic geometric OCR observations, not native language/identifier accuracy.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
