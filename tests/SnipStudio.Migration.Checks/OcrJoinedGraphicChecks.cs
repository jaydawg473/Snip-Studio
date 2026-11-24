using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using SnipStudio.WinUI.Backend;
using NativeLine = SnipStudio.TextLineRegion;
using NativeResult = SnipStudio.TextRecognitionResult;
using NativeWord = SnipStudio.TextWordRegion;

internal static partial class Program
{
    private static void CheckJoinedGraphicPrefixes()
    {
        using var image = new Bitmap(210, 76, PixelFormat.Format32bppArgb);
        const string filename = "@00042_Aa@.pdf";
        const string joined = "@" + filename;
        const string nativeText = joined + "  001";
        string[] paper = [".########...", "#........#..", "#.........#.", "#..........#",
            "#....#.....#", "#...#.#....#", "#...###....#", "#...###....#", "#...#.####.#",
            "#.######.#.#", "#.#.#..###.#", "#..##......#", "#..........#", "#..........#", ".##########."];
        using (Graphics graphics = Graphics.FromImage(image))
        using (var font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            for (int y = 0; y < paper.Length; y++)
                for (int x = 0; x < paper[y].Length; x++)
                    if (paper[y][x] == '#') graphics.FillRectangle(Brushes.Red, 12 + x, 10 + y, 1, 1);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.DrawString(filename, font, Brushes.Black, 29, 10);
            graphics.DrawString("@0032", font, Brushes.Red, 12, 46);
        }
        RectangleF nameInk = RectangleF.Empty;
        for (int y = 8; y < 28; y++)
            for (int x = 26; x < 130; x++)
                if (image.GetPixel(x, y).ToArgb() == Color.Black.ToArgb())
                {
                    var pixel = new RectangleF(x, y, 1, 1);
                    nameInk = nameInk.IsEmpty ? pixel : RectangleF.Union(nameInk, pixel);
                }
        var icon = new RectangleF(12, 10, 12, 15);
        var row = Line(nativeText, (joined, RectangleF.Union(icon, nameInk)), ("001", new RectangleF(158, 12, 19, 12)));
        var genuine = Line("@0032", ("@0032", new RectangleF(12, 48, 37, 13)));
        string raw = nativeText + Environment.NewLine + genuine.Text;
        var result = new NativeResult { Available = true, Text = raw, RawText = raw };
        result.Lines.Add(row);
        result.Lines.Add(genuine);

        Type regions = typeof(EditorSession).Assembly.GetType("SnipStudio.RecognizedTextRegions", throwOnError: true)!;
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        var find = regions.GetMethod("FindJoinedGraphicPrefixes", methods)!;
        var apply = regions.GetMethod("ApplyJoinedGraphicPrefix", methods)!;
        var targets = (IList)find.Invoke(null, [result, image])!;
        Check(targets.Count == 1 && row.ExcludedGraphicBounds == icon && genuine.ExcludedGraphicBounds.IsEmpty,
            "Detect and protect a folded icon inside an OCR word while keeping a genuine @ in a red identifier");
        object target = targets[0]!;
        Check(row.Text == nativeText && row.Words[0].Text == joined,
            "Raster evidence alone cannot choose how many characters to remove");
        var conflict = Reread("@00042_AA@.pdf", nameInk);
        Check(!(bool)apply.Invoke(null, [result, target, conflict])! && row.Text == nativeText,
            "An independently read but conflicting filename does not override the original");
        var incompleteBounds = new RectangleF(nameInk.Left + 12, nameInk.Top, nameInk.Width - 12, nameInk.Height);
        Check(!(bool)apply.Invoke(null, [result, target, Reread("00042_Aa@.pdf", incompleteBounds)])! &&
                row.Text == nativeText, "A matching suffix cannot drop genuine leading zeros when the crop misses the filename's left edge");
        Check((bool)apply.Invoke(null, [result, target, Reread(filename, nameInk)])!,
            "A complete crop that confirms the exact filename can remove the misread icon prefix");
        Check(result.RawText == raw && row.Text == filename + "  001" && row.Words[0].Bounds == nameInk &&
                !row.Bounds.IntersectsWith(icon) && result.Text.EndsWith("@0032", StringComparison.Ordinal) &&
                image.GetPixel(12, 13).ToArgb() == Color.Red.ToArgb(),
            "Preserve identifier case, punctuation, spacing, later rows, and source icon pixels");
        Check(!(bool)apply.Invoke(null, [result, target, Reread(filename, nameInk)])!,
            "A recovered prefix cannot be applied again to an updated word");
        CheckUiIconRaster(regions);

        static NativeLine Line(string value, params (string Text, RectangleF Bounds)[] words)
        {
            var line = new NativeLine { Text = value };
            foreach ((string text, RectangleF bounds) in words)
            {
                line.Words.Add(new NativeWord { Text = text, Bounds = bounds });
                line.Bounds = line.Bounds.IsEmpty ? bounds : RectangleF.Union(line.Bounds, bounds);
            }
            return line;
        }

        static NativeResult Reread(string value, RectangleF bounds)
        {
            var read = new NativeResult { Available = true, Text = value };
            read.Lines.Add(Line(value, (value, bounds)));
            return read;
        }
    }

    private static void CheckUiIconRaster(Type regions)
    {
        using var image = new Bitmap(235, 115, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        using (var gray = new Pen(Color.Gray))
        using (var font = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.DrawRectangle(gray, 105, 8, 9, 9);
            graphics.FillRectangle(Brushes.White, 102, 11, 10, 10);
            graphics.DrawRectangle(gray, 102, 11, 9, 9);
            DrawBadge(13, Brushes.OrangeRed);
            DrawBadge(32, Brushes.DeepSkyBlue);
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.DrawString("4:16  PM", font, Brushes.Gray, 47, 7);
            graphics.DrawString("Saved", font, Brushes.Gray, 54, 37);
            graphics.DrawString("O  0  B  8  +  C++", font, Brushes.Black, 10, 64);
            graphics.DrawString("+", font, Brushes.OrangeRed, 12, 92);
            graphics.DrawString("O", font, Brushes.DeepSkyBlue, 30, 92);
            graphics.DrawString("valid", font, Brushes.Black, 54, 92);

            void DrawBadge(int x, Brush brush) =>
                graphics.FillPolygon(brush, [
                    new Point(x, 39), new Point(x + 3, 39), new Point(x + 6, 43),
                    new Point(x + 10, 38), new Point(x + 13, 39), new Point(x + 10, 46),
                    new Point(x + 13, 51), new Point(x + 9, 52), new Point(x + 6, 48),
                    new Point(x + 2, 52), new Point(x, 49), new Point(x + 3, 45)
                ]);
        }

        var result = new NativeResult { Available = true };
        result.Lines.Add(Row("4:16  PM O", ("4:16", 49, 10, 22, 11), ("PM", 76, 10, 19, 11), ("O", 102, 8, 13, 13)));
        result.Lines.Add(Row("O", ("O", 102, 8, 13, 13)));
        result.Lines.Add(Row("+ Saved", ("+", 13, 38, 13, 14), ("Saved", 55, 38, 35, 13)));
        result.Lines.Add(Row("O", ("O", 32, 38, 13, 14)));
        const string real = "O  0  B  8  +  C++";
        result.Lines.Add(Row(real, ("O", 12, 66, 11, 14), ("0", 28, 66, 10, 14),
            ("B", 42, 66, 10, 14), ("8", 57, 66, 10, 14), ("+", 71, 66, 10, 14), ("C++", 89, 66, 25, 14)));
        result.Lines.Add(Row("+ O valid", ("+", 14, 94, 11, 14), ("O", 32, 94, 11, 14), ("valid", 55, 94, 35, 14)));
        string original = string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
        result.Text = result.RawText = original;
        var refine = regions.GetMethod("Refine", BindingFlags.Static | BindingFlags.NonPublic)!;
        refine.Invoke(null, [result, image]);
        Check(result.Lines.Select(line => line.Text).SequenceEqual(["4:16  PM", "Saved", real, "+ O valid"]),
            "Remove raster-proven copy and badge icons while keeping literal O, 0, B, 8, +, C++ and colored glyphs");
        Check(result.RawText == original && result.Text == string.Join(Environment.NewLine,
                new[] { "4:16  PM", "Saved", real, "+ O valid" }),
            "Keep the raw OCR audit text, exact literal spacing and row order when excluding graphics");
        Check(result.ProtectedGraphics.Count == 3 && result.ProtectedGraphics.All(ink =>
                !result.Lines.Any(line => line.Bounds.IntersectsWith(ink))),
            "Keep the excluded UI artwork outside all editable line bounds");

        static NativeLine Row(string value, params (string Text, int X, int Y, int Width, int Height)[] words)
        {
            var line = new NativeLine { Text = value };
            foreach (var word in words)
            {
                var bounds = new RectangleF(word.X, word.Y, word.Width, word.Height);
                line.Words.Add(new NativeWord { Text = word.Text, Bounds = bounds });
                line.Bounds = line.Bounds.IsEmpty ? bounds : RectangleF.Union(line.Bounds, bounds);
            }
            return line;
        }
    }
}
