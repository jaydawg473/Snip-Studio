#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using NativeLine = global::SnipStudio.TextLineRegion;
using NativeOcrResult = global::SnipStudio.TextRecognitionResult;

namespace SnipStudio.WinUI.Backend;

/// <summary>Coordinates in the original image's physical pixels.</summary>
public readonly record struct OcrBounds(double X, double Y, double Width, double Height);

public sealed class OcrWordSnapshot
{
    public string Text { get; }
    public OcrBounds Bounds { get; }
    public OcrWordSnapshot(string text, OcrBounds bounds)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Bounds = bounds;
    }
}

public sealed class OcrLineSnapshot
{
    public int Index { get; }
    public string Text { get; }
    public OcrBounds Bounds { get; }
    public IReadOnlyList<OcrWordSnapshot> Words { get; }

    public OcrLineSnapshot(int index, string text, OcrBounds bounds, IReadOnlyList<OcrWordSnapshot> words)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        Index = index;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Bounds = bounds;
        ArgumentNullException.ThrowIfNull(words);
        var copy = new OcrWordSnapshot[words.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = words[i] ?? throw new ArgumentException("Every OCR word must be present.", nameof(words));
        Words = Array.AsReadOnly(copy);
    }
}

/// <summary>
/// A detached, immutable OCR result tied to one document revision. Text and raw
/// output are never spelling-corrected. Font matching does not verify OCR text.
/// </summary>
public sealed class OcrSnapshot
{
    private global::SnipStudio.TextOverlaySelection? selectionGeometry;
    internal EditorSession Owner { get; }
    internal object ImageIdentity { get; }
    internal global::SnipStudio.TextRecognitionResult NativeResult { get; }
    internal global::SnipStudio.TextOverlaySelection SelectionGeometry =>
        selectionGeometry ??= new(NativeResult.Lines);
    public long Revision { get; }
    public bool Available { get; }
    public string Text { get; }
    public string RawText { get; }
    public string RecoveryRawText { get; }
    public string Language { get; }
    public string Message { get; }
    public double? TextAngle { get; }
    public int RecoveryPasses { get; }
    public bool HasUncertainClippedText { get; }
    public bool WasResized { get; }
    public IReadOnlyList<OcrLineSnapshot> Lines { get; }

    internal OcrSnapshot(EditorSession owner, object imageIdentity, long revision,
        global::SnipStudio.TextRecognitionResult result)
    {
        Owner = owner;
        ImageIdentity = imageIdentity;
        Revision = revision;
        Available = result.Available;
        string providerText = result.Text ?? "";
        RawText = result.RawText ?? providerText;
        RecoveryRawText = result.RecoveryRawText ?? "";
        Language = result.Language ?? "";
        Message = result.Message ?? "";
        TextAngle = result.TextAngle;
        RecoveryPasses = result.RecoveryPasses;
        HasUncertainClippedText = result.HasUncertainClippedText;
        WasResized = result.WasResized;
        var native = new global::SnipStudio.TextRecognitionResult
        {
            Available = Available, RawText = RawText, RecoveryRawText = RecoveryRawText,
            Language = Language, Message = Message, TextAngle = TextAngle,
            RecoveryPasses = RecoveryPasses, WasResized = WasResized, HasUncertainClippedText = HasUncertainClippedText
        };
        native.ProtectedGraphics.AddRange(result.ProtectedGraphics);
        var lines = new List<OcrLineSnapshot>(result.Lines.Count);
        foreach (var source in VerticalReadingOrder(result))
        {
            // Keep each line's words and text verbatim, including duplicates.
            // Both the editor and selection use the same detached line order.
            var line = new global::SnipStudio.TextLineRegion
            {
                Text = source?.Text ?? "", Bounds = source?.Bounds ?? RectangleF.Empty,
                ExcludedGraphicBounds = source?.ExcludedGraphicBounds ?? RectangleF.Empty,
                RecognitionClipBounds = source?.RecognitionClipBounds ?? RectangleF.Empty
            };
            var words = new List<OcrWordSnapshot>();
            if (source != null)
                foreach (var word in source.Words)
                {
                    if (word == null) continue;
                    line.Words.Add(new global::SnipStudio.TextWordRegion
                    {
                        Text = word.Text ?? "", Bounds = word.Bounds, Footprint = word.Footprint
                    });
                    words.Add(new OcrWordSnapshot(word.Text ?? "", FromRectangle(word.Bounds)));
                }
            lines.Add(new OcrLineSnapshot(lines.Count, line.Text, FromRectangle(line.Bounds), words));
            native.Lines.Add(line);
        }
        Lines = new ReadOnlyCollection<OcrLineSnapshot>(lines);
        // Windows can return an aggregate with spaces where its individual
        // OCR lines ended. Copy the same ordered rows the editor displays.
        Text = lines.Count == 0 ? providerText.ReplaceLineEndings("\r\n")
            : string.Join("\r\n", lines.Select(line => line.Text));
        native.Text = Text;
        NativeResult = native;
    }

    private static IEnumerable<NativeLine> VerticalReadingOrder(NativeOcrResult result)
    {
        var lines = result.Lines;
        if (lines.Count is < 2 or > 4096 ||
            (result.TextAngle is double angle && (!double.IsFinite(angle) || Math.Abs(angle) > 2)))
            return lines;

        bool inverted = false;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index] is not { Bounds: var bounds } ||
                !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Right) ||
                !float.IsFinite(bounds.Top) || !float.IsFinite(bounds.Bottom) ||
                bounds.Left < 0 || bounds.Top < 0 || bounds.Width <= 0 || bounds.Height <= 0)
                return lines;
            if (index > 0)
            {
                RectangleF previous = lines[index - 1].Bounds;
                inverted |= MidpointY(bounds) < MidpointY(previous) && !SameTextRow(bounds, previous);
            }
        }
        if (!inverted || !HasSingleVerticalFlow(lines)) return lines;

        int[] order = Enumerable.Range(0, lines.Count).OrderBy(index => MidpointY(lines[index].Bounds)).ToArray();
        for (int start = 0; start < order.Length;)
        {
            RectangleF anchor = lines[order[start]].Bounds;
            int end = start + 1;
            while (end < order.Length && SameTextRow(anchor, lines[order[end]].Bounds)) end++;
            // Split OCR runs in one row keep their provider order.
            Array.Sort(order, start, end - start);
            start = end;
        }
        return order.Select(index => lines[index]);
    }

    private static bool HasSingleVerticalFlow(List<NativeLine> lines)
    {
        // Chat timestamps or indented lines may sit above or below the main
        // flow. Recurring side runs inside it can be a second column even when
        // their baselines never align, so defer to the provider in that case.
        float[] heights = lines.Select(line => line.Bounds.Height).Order().ToArray();
        float tolerance = Math.Max(24, heights[heights.Length / 2] * 2.5f);
        RectangleF[] byLeft = lines.Select(line => line.Bounds).OrderBy(bounds => bounds.Left).ToArray();
        int laneFirst = 0, laneLast = 0;
        for (int first = 0, last = 0; last < byLeft.Length; last++)
        {
            while (byLeft[last].Left - byLeft[first].Left > tolerance) first++;
            if (last - first > laneLast - laneFirst) (laneFirst, laneLast) = (first, last);
        }
        if ((laneLast - laneFirst + 1) * 5 < byLeft.Length * 3) return false;

        float top = float.PositiveInfinity, bottom = float.NegativeInfinity;
        for (int index = laneFirst; index <= laneLast; index++)
        {
            top = Math.Min(top, byLeft[index].Top);
            bottom = Math.Max(bottom, byLeft[index].Bottom);
        }
        float previousSideLeft = float.NegativeInfinity;
        for (int index = 0; index < byLeft.Length; index++)
        {
            RectangleF bounds = byLeft[index];
            if (index >= laneFirst && index <= laneLast || bounds.Top >= bottom || bounds.Bottom <= top) continue;
            // A single indent and a distant continuation do not form a column.
            if (bounds.Left - previousSideLeft <= tolerance) return false;
            previousSideLeft = bounds.Left;
        }
        return true;
    }

    private static float MidpointY(RectangleF area) => area.Top + area.Height / 2;

    private static bool SameTextRow(RectangleF first, RectangleF second) =>
        Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top) >= Math.Min(first.Height, second.Height) / 2;

    internal static OcrBounds FromRectangle(RectangleF value) => new(value.X, value.Y, value.Width, value.Height);
}

public sealed class OcrSelectionSnapshot
{
    private readonly HashSet<(int LineIndex, int WordIndex)> selectedWords;
    internal OcrSnapshot Source { get; }
    public string Text { get; }
    /// <summary>One source region per selected line for edit and conversion.</summary>
    public IReadOnlyList<OcrBounds> Bands { get; }
    /// <summary>Individual selected word regions for the viewport highlight.</summary>
    public IReadOnlyList<OcrBounds> WordBands { get; }
    public IReadOnlyList<int> LineIndices { get; }
    /// <summary>The exact native character range, when the selection maps to a single line.</summary>
    public int SelectionStart { get; }
    public int SelectionLength { get; }

    internal OcrSelectionSnapshot(OcrSnapshot source, string text, IEnumerable<OcrBounds> bands,
        IEnumerable<OcrBounds> wordBands, IEnumerable<int> lines,
        IEnumerable<(int LineIndex, int WordIndex)>? words = null, int selectionStart = 0, int selectionLength = 0)
    {
        Source = source;
        Text = text;
        Bands = new List<OcrBounds>(bands).AsReadOnly();
        WordBands = new List<OcrBounds>(wordBands).AsReadOnly();
        LineIndices = new List<int>(lines).AsReadOnly();
        selectedWords = words == null ? new() : new(words);
        SelectionStart = selectionStart;
        SelectionLength = selectionLength;
    }

    internal bool IncludesWord(int lineIndex, int wordIndex) => selectedWords.Contains((lineIndex, wordIndex));
}

/// <summary>A source-style draft for one recognized line. Preparation leaves the image and Undo history intact.</summary>
public sealed class OcrLineEditSnapshot
{
    internal OcrSnapshot Source { get; }
    internal RectangleF SourceInk { get; }
    internal Rectangle SourceMask { get; }
    internal RectangleF ProtectedLayout { get; }
    internal float MinimumHeight { get; }

    public int ExistingAnnotationIndex { get; }
    public string Text { get; }
    public string Rtf { get; }
    public string FontFamily { get; }
    public TextFontStyle FontStyle { get; }
    public ImagePoint Location { get; }
    public uint Argb { get; }
    public uint BackgroundArgb { get; }
    public float FontSize { get; }
    public float LayoutWidth { get; }
    public OcrBounds Mask => OcrSnapshot.FromRectangle(SourceMask);

    internal OcrLineEditSnapshot(OcrSnapshot source, int existingAnnotationIndex, string text, string rtf,
        string fontFamily, TextFontStyle fontStyle, PointF location, Color foreground, float fontSize,
        float layoutWidth, float minimumHeight, RectangleF sourceInk = default, Rectangle sourceMask = default,
        RectangleF protectedLayout = default, Color background = default)
    {
        Source = source;
        ExistingAnnotationIndex = existingAnnotationIndex;
        Text = text;
        Rtf = rtf;
        FontFamily = fontFamily;
        FontStyle = fontStyle;
        Location = new ImagePoint(location.X, location.Y);
        Argb = unchecked((uint)foreground.ToArgb());
        BackgroundArgb = unchecked((uint)background.ToArgb());
        FontSize = fontSize;
        LayoutWidth = layoutWidth;
        MinimumHeight = minimumHeight;
        SourceInk = sourceInk;
        SourceMask = sourceMask;
        ProtectedLayout = protectedLayout;
    }
}

public sealed record OcrConversionResult(bool Applied, int LastAnnotationIndex, int ConvertedLineCount, string Message);
