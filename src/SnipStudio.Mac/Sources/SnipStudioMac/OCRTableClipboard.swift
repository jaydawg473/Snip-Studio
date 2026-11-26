import AppKit

struct OCRTable {
    let cells: [[String]]

    var rowCount: Int { cells.count }
    var columnCount: Int { cells.first?.count ?? 0 }

    var tabSeparatedText: String? {
        // A plain paste target has no text-cell type or way to preserve embedded separators.
        guard cells.allSatisfy({ $0.allSatisfy(Self.safeForPlainPaste) }) else { return nil }
        return cells.map { $0.joined(separator: "\t") }.joined(separator: "\n")
    }

    var html: String {
        var table = "<html><head><meta charset=\"utf-8\"></head><body>"
        table += "<table xmlns:x=\"urn:schemas-microsoft-com:office:excel\">"
        for row in cells {
            table += "<tr>"
            for value in row {
                table += "<td x:str=\"\" style='mso-number-format:\"\\@\";white-space:pre-wrap'>"
                table += Self.escapeHTML(value)
                table += "</td>"
            }
            table += "</tr>"
        }
        return table + "</table></body></html>"
    }

    private static let formulaPrefixes = Set("=+-@'＝＋－＠＇−".unicodeScalars)

    private static func safeForPlainPaste(_ value: String) -> Bool {
        let scalars = value.unicodeScalars
        guard !scalars.contains(where: { $0.properties.generalCategory == .control || $0 == "\"" }) else {
            return false
        }
        guard let first = scalars.first(where: {
            !$0.properties.isWhitespace && $0.properties.generalCategory != .format
        }) else { return true }
        return !formulaPrefixes.contains(first)
    }

    private static func escapeHTML(_ value: String) -> String {
        value
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }
}

enum OCRTableClipboard {
    // Vision reports image-normalized positions. This is a narrow gap at normal screenshot sizes.
    private static let standardGutter: CGFloat = 0.004
    private static let maximumColumns = 32

    static func makeTable(from lines: [RecognizedLine]) -> OCRTable? {
        guard (4...4096).contains(lines.count) else { return nil }
        var validLines: [RecognizedLine] = []
        var characterCount = 0
        let imageBounds = CGRect(x: 0, y: 0, width: 1, height: 1)
        for line in lines {
            guard !line.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
            characterCount += line.text.utf16.count
            let bounds = line.bounds
            guard characterCount <= 512 * 1024,
                  bounds.minX.isFinite, bounds.minY.isFinite, bounds.maxX.isFinite, bounds.maxY.isFinite else {
                return nil
            }
            let clipped = bounds.intersection(imageBounds)
            guard !clipped.isEmpty else { return nil }
            validLines.append(RecognizedLine(text: line.text, bounds: clipped))
        }

        let rows = TextRecognition.groupRows(validLines)
        guard rows.filter({ $0.count >= 2 }).count >= 2 else { return nil }
        let cellsByLeft = rows.enumerated().flatMap { row, lines in
            lines.map { Cell(text: $0.text, bounds: $0.bounds, row: row) }
        }.sorted { $0.bounds.minX < $1.bounds.minX }
        let columns = findColumns(cellsByLeft, rowCount: rows.count)
        guard columns.count >= 2, rows.count * columns.count <= 16_384 else { return nil }

        var result = Array(repeating: Array(repeating: "", count: columns.count), count: rows.count)
        for (column, cells) in columns.enumerated() {
            guard cells.count >= 2 else { return nil }
            for cell in cells {
                // Multiple observations can be fragments of a single string. Never invent a separator.
                guard result[cell.row][column].isEmpty else { return nil }
                result[cell.row][column] = cell.text
            }
        }
        return OCRTable(cells: result)
    }

    @MainActor
    @discardableResult
    static func write(_ lines: [RecognizedLine], to clipboard: NSPasteboard = .general) throws -> OCRTable {
        guard let table = makeTable(from: lines) else { throw Failure.noAlignedTable }
        let item = NSPasteboardItem()
        guard item.setData(Data(table.html.utf8), forType: .html) else { throw Failure.writeFailed }
        if let text = table.tabSeparatedText, !item.setString(text, forType: .string) {
            throw Failure.writeFailed
        }
        clipboard.clearContents()
        guard clipboard.writeObjects([item]) else { throw Failure.writeFailed }
        return table
    }

    private static func findColumns(_ cells: [Cell], rowCount: Int) -> [[Cell]] {
        var columns: [[Cell]] = []
        var previousOnRow = Array<Cell?>(repeating: nil, count: rowCount)
        var rightEdge = -CGFloat.infinity
        for (index, cell) in cells.enumerated() {
            let gap = cell.bounds.minX - rightEdge
            if gap > standardGutter || (gap > 0 && hasRepeatedGutter(cells[index...], previousOnRow: previousOnRow)) {
                guard columns.count < maximumColumns else { return [] }
                columns.append([])
                previousOnRow = Array(repeating: nil, count: rowCount)
                rightEdge = cell.bounds.maxX
            } else {
                rightEdge = max(rightEdge, cell.bounds.maxX)
            }
            columns[columns.count - 1].append(cell)
            previousOnRow[cell.row] = cell
        }
        return columns
    }

    private static func hasRepeatedGutter(_ candidates: ArraySlice<Cell>, previousOnRow: [Cell?]) -> Bool {
        guard let rightStart = candidates.first?.bounds.minX else { return false }
        var supportingRows: Set<Int> = []
        for right in candidates {
            if right.bounds.minX - rightStart > standardGutter { break }
            if let left = previousOnRow[right.row], right.bounds.minX - left.bounds.maxX > standardGutter {
                supportingRows.insert(right.row)
                if supportingRows.count == 2 { return true }
            }
        }
        return false
    }

    private struct Cell {
        let text: String
        let bounds: CGRect
        let row: Int
    }

    private enum Failure: LocalizedError {
        case noAlignedTable, writeFailed

        var errorDescription: String? {
            switch self {
            case .noAlignedTable: "Text Actions couldn’t find columns aligned across two or more rows. Try Copy all text or capture a tighter view of the table."
            case .writeFailed: "The clipboard could not accept the table. Try copying again."
            }
        }
    }
}
