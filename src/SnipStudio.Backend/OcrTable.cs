#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;

namespace SnipStudio.WinUI.Backend;

public sealed class OcrTableSnapshot
{
    private readonly string[][] cells;
    public int RowCount => cells.Length;
    public int ColumnCount => cells[0].Length;

    internal OcrTableSnapshot(string[][] cells) => this.cells = cells;

    // A plain-text paste target has no text-cell type. Omit that representation
    // when it could interpret a cell as a formula or split it into extra cells.
    public string? TabSeparatedText => cells.All(row => row.All(CanPasteAsPlainText))
        ? string.Join("\r\n", cells.Select(row => string.Join("\t", row))) : null;

    public string Html
    {
        get
        {
            var html = new StringBuilder("<table xmlns:x=\"urn:schemas-microsoft-com:office:excel\">");
            foreach (string[] row in cells)
            {
                html.Append("<tr>");
                foreach (string cell in row)
                {
                    html.Append("<td x:str=\"\" style='mso-number-format:\"\\@\";white-space:pre-wrap'>")
                        .Append(WebUtility.HtmlEncode(cell)).Append("</td>");
                }
                html.Append("</tr>");
            }
            return html.Append("</table>").ToString();
        }
    }

    private static bool CanPasteAsPlainText(string cell)
    {
        if (cell.Any(character => char.IsControl(character) || character == '"')) return false;
        foreach (char character in cell)
        {
            if (char.IsWhiteSpace(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                continue;
            return character is not ('=' or '+' or '-' or '@' or '\'' or '＝' or '＋' or '－' or '＠');
        }
        return true;
    }
}

public sealed partial class EditorSession
{
    private const float StandardTableGutter = 6;

    private sealed class OcrTableCell(string text, RectangleF bounds)
    {
        internal string Text { get; } = text;
        internal RectangleF Bounds { get; } = bounds;
        internal float Middle => Bounds.Top + Bounds.Height / 2;
        internal int Row { get; set; }
        internal int Column { get; set; }
    }

    /// <summary>Copies geometry-backed rows without synthesizing cell values.</summary>
    public OcrTableSnapshot? PrepareRecognizedTable(OcrSnapshot source, OcrSelectionSnapshot? selection = null)
    {
        VerifyAccess();
        RequireCurrentOcr(source);
        if (selection != null && !ReferenceEquals(selection.Source, source))
            throw new InvalidOperationException("This selection belongs to an earlier recognition. Select the text again.");
        if (!source.Available) return null;

        IReadOnlyList<int> indices = selection?.LineIndices ?? source.Lines.Select(line => line.Index).ToArray();
        if (indices.Count > 4096) return null;

        var cells = new List<OcrTableCell>();
        long characters = 0;
        foreach (int index in indices)
        {
            var line = source.NativeResult.Lines[index];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            characters += line.Text.Length;
            if (characters > 512 * 1024 || !TryAddTableCells(line, index, selection, cells) || cells.Count > 16384)
                return null;
        }
        if (cells.Count < (selection is null ? 4 : 2)) return null;

        OcrTableCell? rowAnchor = null;
        int rowCount = 0;
        foreach (var cell in cells.OrderBy(item => item.Middle).ThenBy(item => item.Bounds.Left))
        {
            if (rowAnchor is null || !ShareTableRow(rowAnchor, cell))
            {
                rowAnchor = cell;
                rowCount++;
            }
            else if (cell.Bounds.Height > rowAnchor.Bounds.Height) rowAnchor = cell;
            cell.Row = rowCount - 1;
        }
        if (rowCount < 2) return null;

        int columnCount = AssignTableColumns(cells, rowCount);
        if (columnCount is < 1 or > 32 || rowCount * columnCount > 16384) return null;
        if (columnCount == 1 && selection is null) return null;

        var result = new string[rowCount][];
        var columnSupport = new int[columnCount];
        var rowSupport = new int[rowCount];
        for (int i = 0; i < rowCount; i++) result[i] = new string[columnCount];
        OcrTableCell? previousCell = null;
        foreach (OcrTableCell cell in cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ThenBy(cell => cell.Bounds.Left))
        {
            if (previousCell != null && previousCell.Row == cell.Row && previousCell.Column == cell.Column)
            {
                if (!global::SnipStudio.RecognizedTextRecovery.CanJoinAdjacentRuns(previousCell.Text, previousCell.Bounds, cell.Bounds))
                    return null;
                result[cell.Row][cell.Column] += cell.Text;
            }
            else
            {
                result[cell.Row][cell.Column] = cell.Text;
                columnSupport[cell.Column]++;
                rowSupport[cell.Row]++;
            }
            previousCell = cell;
        }
        // A shared gutter needs support on multiple rows. A stray word or one
        // indented row is insufficient evidence for a column.
        if (columnSupport.Any(count => count < 2) || (columnCount > 1 && rowSupport.Count(count => count > 1) < 2))
            return null;
        foreach (string[] row in result)
            for (int col = 0; col < columnCount; col++)
                row[col] ??= "";
        return new OcrTableSnapshot(result);
    }

    private static int AssignTableColumns(List<OcrTableCell> cells, int rowCount)
    {
        var ordered = cells.OrderBy(cell => cell.Bounds.Left).ToArray();
        var previousOnRow = new OcrTableCell?[rowCount];
        int columnCount = 0;
        float edge = float.NegativeInfinity;
        for (int index = 0; index < ordered.Length; index++)
        {
            OcrTableCell cell = ordered[index];
            float gap = cell.Bounds.Left - edge;
            if (gap > StandardTableGutter ||
                (gap >= 1 && HasSupportedNarrowGutter(ordered, index, columnCount - 1, previousOnRow)))
            {
                columnCount++;
                edge = cell.Bounds.Right;
            }
            else edge = Math.Max(edge, cell.Bounds.Right);
            cell.Column = columnCount - 1;
            previousOnRow[cell.Row] = cell;
        }
        return columnCount;
    }

    private static bool HasSupportedNarrowGutter(OcrTableCell[] ordered, int start, int leftColumn,
        OcrTableCell?[] previousOnRow)
    {
        // One long filename can nearly fill a fixed column. Use the normal
        // clearances from at least two other rows to confirm its narrow gap.
        float rightStart = ordered[start].Bounds.Left;
        var seenRows = new HashSet<int>();
        int supportedRows = 0;
        for (int index = start; index < ordered.Length; index++)
        {
            OcrTableCell right = ordered[index];
            if (right.Bounds.Left - rightStart > StandardTableGutter) break;
            if (!seenRows.Add(right.Row) || previousOnRow[right.Row] is not { } left || left.Column != leftColumn) continue;
            if (global::SnipStudio.RecognizedTextRecovery.CanJoinAdjacentRuns(left.Text, left.Bounds, right.Bounds))
                return false;
            if (right.Bounds.Left - left.Bounds.Right > StandardTableGutter) supportedRows++;
        }
        return supportedRows >= 2;
    }

    private static bool ShareTableRow(OcrTableCell anchor, OcrTableCell other)
    {
        float shorter = Math.Min(anchor.Bounds.Height, other.Bounds.Height);
        float overlap = Math.Min(anchor.Bounds.Bottom, other.Bounds.Bottom) - Math.Max(anchor.Bounds.Top, other.Bounds.Top);
        return overlap >= shorter * .5f && Math.Abs(anchor.Middle - other.Middle) <= Math.Max(anchor.Bounds.Height, other.Bounds.Height) * .5f;
    }

    private static bool TryAddTableCells(global::SnipStudio.TextLineRegion line, int lineIndex,
        OcrSelectionSnapshot? selection, List<OcrTableCell> cells)
    {
        int cursor = 0, cellStart = -1, cellEnd = 0;
        RectangleF cellBounds = RectangleF.Empty, previous = RectangleF.Empty;
        for (int index = 0; index < line.Words.Count; index++)
        {
            var word = line.Words[index];
            if (string.IsNullOrWhiteSpace(word.Text) || !ValidOcrBounds(word.Bounds)) return false;
            int start = line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (start < 0) return false;
            int priorEnd = cursor;
            cursor = start + word.Text.Length;
            if (selection != null && !selection.IncludesWord(lineIndex, index))
            {
                FinishCell();
                continue;
            }
            if (cellStart < 0)
            {
                cellStart = index == 0 ? 0 : start;
            }
            else if (word.Bounds.Left - previous.Right >= Math.Max(12, Math.Min(previous.Height, word.Bounds.Height) * 1.8f)
                && line.Text.AsSpan(priorEnd, start - priorEnd).Trim().IsEmpty)
            {
                FinishCell();
                cellStart = start;
            }
            cellEnd = index == line.Words.Count - 1 ? line.Text.Length : cursor;
            cellBounds = cellBounds.IsEmpty ? word.Bounds : RectangleF.Union(cellBounds, word.Bounds);
            previous = word.Bounds;
        }
        FinishCell();
        return cursor != 0;

        void FinishCell()
        {
            if (cellStart < 0) return;
            cells.Add(new OcrTableCell(line.Text[cellStart..cellEnd], cellBounds));
            cellStart = -1;
            cellBounds = RectangleF.Empty;
        }
    }
}
