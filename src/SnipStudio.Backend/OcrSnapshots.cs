#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;

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
    public bool WasResized { get; }
    public IReadOnlyList<OcrLineSnapshot> Lines { get; }

    internal OcrSnapshot(EditorSession owner, object imageIdentity, long revision,
        global::SnipStudio.TextRecognitionResult result)
    {
        Owner = owner;
        ImageIdentity = imageIdentity;
        Revision = revision;
        Available = result.Available;
        Text = result.Text ?? "";
        RawText = result.RawText ?? Text;
        RecoveryRawText = result.RecoveryRawText ?? "";
        Language = result.Language ?? "";
        Message = result.Message ?? "";
        TextAngle = result.TextAngle;
        RecoveryPasses = result.RecoveryPasses;
        WasResized = result.WasResized;
        var native = new global::SnipStudio.TextRecognitionResult
        {
            Available = Available, Text = Text, RawText = RawText, RecoveryRawText = RecoveryRawText,
            Language = Language, Message = Message, TextAngle = TextAngle,
            RecoveryPasses = RecoveryPasses, WasResized = WasResized
        };
        var lines = new List<OcrLineSnapshot>(result.Lines.Count);
        foreach (var source in result.Lines)
        {
            // Keep native line/word order and duplicate rows. A detached copy
            // prevents later mutation of native collections changing selection.
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
                    line.Words.Add(new global::SnipStudio.TextWordRegion { Text = word.Text ?? "", Bounds = word.Bounds });
                    words.Add(new OcrWordSnapshot(word.Text ?? "", FromRectangle(word.Bounds)));
                }
            lines.Add(new OcrLineSnapshot(lines.Count, line.Text, FromRectangle(line.Bounds), words));
            native.Lines.Add(line);
        }
        NativeResult = native;
        Lines = new ReadOnlyCollection<OcrLineSnapshot>(lines);
    }

    internal static OcrBounds FromRectangle(RectangleF value) => new(value.X, value.Y, value.Width, value.Height);
}

public sealed class OcrSelectionSnapshot
{
    private readonly HashSet<(int LineIndex, int WordIndex)> selectedWords;
    internal OcrSnapshot Source { get; }
    public string Text { get; }
    public IReadOnlyList<OcrBounds> Bands { get; }
    public IReadOnlyList<int> LineIndices { get; }
    /// <summary>The exact native character range, when the selection maps to a single line.</summary>
    public int SelectionStart { get; }
    public int SelectionLength { get; }

    internal OcrSelectionSnapshot(OcrSnapshot source, string text, IEnumerable<OcrBounds> bands, IEnumerable<int> lines,
        IEnumerable<(int LineIndex, int WordIndex)>? words = null, int selectionStart = 0, int selectionLength = 0)
    {
        Source = source;
        Text = text;
        Bands = new List<OcrBounds>(bands).AsReadOnly();
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
