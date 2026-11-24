using System.Drawing;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckRecognizedTable()
    {
        using var session = new EditorSession();
        session.OpenImageBytes(CreateImage(510, 160));
        var baseline = session.GetCaptureBaseline();
        var headerName = TableLine("File", ("File", 30, 18, 27));
        var headerDate = TableLine("Date", ("Date", 188, 19, 31));
        var headerCount = TableLine("Count", ("Count", 364, 18, 34));
        var firstName = TableLine("00007_Aa.pdf", ("00007_Aa.pdf", 30, 48, 89));
        var nextName = TableLine("00007_Aa.pdf", ("00007_Aa.pdf", 30, 80, 89));
        var mixed = TableLine("11/19/2026   00008", ("11/19/2026", 188, 50, 79), ("00008", 358, 50, 40));
        var nextCount = TableLine("00012", ("00012", 361, 80, 37));
        OcrSnapshot source = Snapshot(headerDate, mixed, firstName, nextName, headerName, headerCount, nextCount);

        OcrTableSnapshot? table = session.PrepareRecognizedTable(source);
        Check(table is { RowCount: 3, ColumnCount: 3 } && table.TabSeparatedText ==
            "File\tDate\tCount\r\n00007_Aa.pdf\t11/19/2026\t00008\r\n00007_Aa.pdf\t\t00012",
            "Table uses physical rows and columns, preserves duplicate filenames and leading zeros, and leaves an empty cell");
        Check(table?.Html.Contains("00008</td>", StringComparison.Ordinal) == true &&
            table.Html.Contains("mso-number-format:\"\\@\"", StringComparison.Ordinal) &&
            table.Html.Contains("x:str=\"\"", StringComparison.Ordinal),
            "The formatted table carries original strings and text cell hints");
        OcrSelectionSnapshot selection = session.SelectRecognizedText(source, new(17, 10), new(295, 102));
        OcrTableSnapshot? sliced = session.PrepareRecognizedTable(source, selection);
        Check(sliced is { RowCount: 3, ColumnCount: 2 } && sliced.TabSeparatedText ==
            "File\tDate\r\n00007_Aa.pdf\t11/19/2026\r\n00007_Aa.pdf\t",
            "Copy as table respects the area selection and omits cells outside it");

        var formula = TableLine(" \u200B=2+3<&", ("=2+3<&", 30, 48, 75));
        var formulaSource = Snapshot(headerName, headerCount, formula, nextCountAtSecondRow());
        OcrTableSnapshot? richTable = session.PrepareRecognizedTable(formulaSource);
        Check(richTable is { ColumnCount: 2 } && richTable.TabSeparatedText is null &&
            richTable.Html.Contains("=2+3&lt;&amp;", StringComparison.Ordinal) &&
            !richTable.Html.Contains("x:fmla", StringComparison.Ordinal),
            "Formula-like literal values keep encoded text cells and omit a potentially executable plain-text paste");
        var tab = TableLine("A\tB", ("A", 30, 48, 6), ("B", 43, 48, 6));
        Check(session.PrepareRecognizedTable(Snapshot(headerName, headerCount, tab, nextCountAtSecondRow())) is { TabSeparatedText: null },
            "A literal tab inside one cell cannot create a bogus TSV column");
        var quoted = TableLine("\"=2+3\"", ("\"=2+3\"", 30, 48, 70));
        Check(session.PrepareRecognizedTable(Snapshot(headerName, headerCount, quoted, nextCountAtSecondRow())) is { TabSeparatedText: null },
            "Delimited-text paste cannot strip literal quotes and then interpret a cell as a formula");
        Check(session.PrepareRecognizedTable(Snapshot(firstName, nextName)) is null &&
            session.PrepareRecognizedTable(Snapshot(firstName, nextName, nextCountAtSecondRow())) is null,
            "An unstructured single column or one stray cell does not become a guessed table");

        const string timestamp = "11/23/2026 4:38 PM";
        var prefix = ShortRun(".camera-", 30, 30, 44, 6);
        var suffix = ShortRun("viewer-files.nsi", 75, 27, 78, 9);
        var shortName = ShortRun("cs", 30, 61, 10, 4);
        var repeatedName = ShortRun("cs", 30, 89, 10, 4);
        var dates = new[] { ShortRun(timestamp, 255, 28, 105, 10), ShortRun(timestamp, 255, 55, 105, 10),
            ShortRun(timestamp, 255, 83, 105, 10) };
        var explorer = Snapshot(dates[1], suffix, shortName, dates[2], prefix, repeatedName, dates[0]);
        string expectedRows = $".camera-viewer-files.nsi\t{timestamp}\r\ncs\t{timestamp}\r\ncs\t{timestamp}";
        Check(session.PrepareRecognizedTable(explorer) is { RowCount: 3, ColumnCount: 2 } explorerTable &&
            explorerTable.TabSeparatedText == expectedRows,
            "Adjacent filename fragments and short folder labels stay in their rows with repeated dates");
        OcrSelectionSnapshot explorerArea = session.SelectRecognizedText(explorer, new(15, 21), new(375, 102));
        Check(session.PrepareRecognizedTable(explorer, explorerArea)?.TabSeparatedText == expectedRows,
            "Copy as table keeps the same Explorer cells after an area selection");
        OcrSelectionSnapshot selectedNames = session.SelectRecognizedText(explorer, new(15, 21), new(164, 102));
        Check(session.PrepareRecognizedTable(explorer, selectedNames) is { RowCount: 3, ColumnCount: 1 } oneColumn &&
            oneColumn.TabSeparatedText == ".camera-viewer-files.nsi\r\ncs\r\ncs",
            "An explicitly selected name column copies one cell per row, including duplicates");
        var explorerWithHeaders = Snapshot(ShortRun("Name", 20, 8, 28, 10), ShortRun("Date modified", 255, 8, 79, 10),
            dates[1], suffix, shortName, dates[2], prefix, repeatedName, dates[0]);
        OcrSelectionSnapshot namesWithHeader = session.SelectRecognizedText(explorerWithHeaders, new(15, 0), new(164, 102));
        Check(session.PrepareRecognizedTable(explorerWithHeaders, namesWithHeader) is { RowCount: 4, ColumnCount: 1 } selectedColumn &&
            selectedColumn.TabSeparatedText == "Name\r\n.camera-viewer-files.nsi\r\ncs\r\ncs",
            "A selected Name column includes the header when folder icons indent its body cells");
        var ambiguous = Snapshot(ShortRun("first", 30, 28, 44, 10), suffix, dates[0], shortName, dates[1]);
        Check(session.PrepareRecognizedTable(ambiguous) is null,
            "Table assembly rejects same-column fragments without a proven text boundary");

        const string longFilename = "01234_ConsistentLongFilename_Original.pdf";
        var denseHeader = ShortRun("Name", 30, 10, 25, 10);
        var denseDateHeader = ShortRun("Date modified", 260, 10, 77, 10);
        var denseName = ShortRun(longFilename, 30, 35, 227, 10);
        var shortDenseName = ShortRun("00008_Bb.pdf", 30, 60, 69, 10);
        var denseDate = ShortRun("11/29/2026", 260, 35, 60, 10);
        var repeatedDenseDate = ShortRun("11/29/2026", 260, 60, 60, 10);
        var denseTable = Snapshot(denseDate, denseHeader, shortDenseName, denseName, denseDateHeader, repeatedDenseDate);
        const string denseTsv = "Name\tDate modified\r\n" + longFilename +
            "\t11/29/2026\r\n00008_Bb.pdf\t11/29/2026";
        Check(session.PrepareRecognizedTable(denseTable) is { RowCount: 3, ColumnCount: 2 } denseResult &&
            denseResult.TabSeparatedText == denseTsv,
            "A filename three pixels from the date keeps its table columns when the other rows establish the gutter");
        OcrSelectionSnapshot selectedDense = session.SelectRecognizedText(denseTable, new(15, 0), new(350, 75));
        Check(session.PrepareRecognizedTable(denseTable, selectedDense)?.TabSeparatedText == denseTsv,
            "A selected dense table preserves the exact names, repeated dates, and leading zeros");
        Check(session.PrepareRecognizedTable(Snapshot(denseName, shortDenseName, denseDate, repeatedDenseDate)) is null,
            "One wide row cannot support a narrow candidate gutter by itself");
        var continuedPrefix = ShortRun("01234_Continued-", 30, 35, 229, 10);
        var continuedSuffix = ShortRun("Filename.pdf", 260, 35, 70, 10);
        Check(session.PrepareRecognizedTable(Snapshot(denseHeader, denseDateHeader, continuedPrefix, continuedSuffix,
            shortDenseName, repeatedDenseDate)) is null,
            "A recognized filename continuation makes the apparent narrow gutter ambiguous");

        Check(session.Revision == baseline.Revision && session.AnnotationCount == 0,
            "Preparing table clipboard data leaves the snip and Undo history intact");

        global::SnipStudio.TextLineRegion nextCountAtSecondRow() => TableLine("00012", ("00012", 361, 48, 37));

        OcrSnapshot Snapshot(params global::SnipStudio.TextLineRegion[] lines)
        {
            var result = new global::SnipStudio.TextRecognitionResult { Available = true };
            result.Lines.AddRange(lines);
            result.Text = string.Join(Environment.NewLine, lines.Select(line => line.Text));
            return new OcrSnapshot(session, baseline.ImageIdentity, baseline.Revision, result);
        }

        static global::SnipStudio.TextLineRegion TableLine(string text, params (string Text, float X, float Y, float Width)[] words)
        {
            var line = new global::SnipStudio.TextLineRegion { Text = text };
            foreach (var word in words)
            {
                var bounds = new RectangleF(word.X, word.Y, word.Width, 11);
                line.Words.Add(new global::SnipStudio.TextWordRegion { Text = word.Text, Bounds = bounds });
                line.Bounds = line.Bounds.IsEmpty ? bounds : RectangleF.Union(line.Bounds, bounds);
            }
            return line;
        }

        static global::SnipStudio.TextLineRegion ShortRun(string text, int x, int y, int width, int height)
        {
            var bounds = new RectangleF(x, y, width, height);
            var line = new global::SnipStudio.TextLineRegion { Text = text, Bounds = bounds };
            line.Words.Add(new global::SnipStudio.TextWordRegion { Text = text, Bounds = bounds });
            return line;
        }
    }
}
