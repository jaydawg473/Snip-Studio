#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

/// <summary>One original line or the exact picked words on that line.</summary>
public sealed class OcrReviewLine
{
    public int LineIndex { get; }
    public string Text { get; }
    internal RectangleF SourceInk { get; }
    internal bool IsWholeLine { get; }

    internal OcrReviewLine(int lineIndex, string text, RectangleF sourceInk, bool isWholeLine)
    {
        LineIndex = lineIndex;
        Text = text;
        SourceInk = sourceInk;
        IsWholeLine = isWholeLine;
    }
}

/// <summary>A frozen, ordered edit or translation scope tied to one recognized image revision.</summary>
public sealed class OcrTextReview
{
    private readonly Dictionary<int, OcrReviewLine> lineLookup;
    public OcrSnapshot Source { get; }
    internal OcrSelectionSnapshot? Selection { get; }
    public IReadOnlyList<OcrReviewLine> Lines { get; }

    internal OcrTextReview(OcrSnapshot source, OcrSelectionSnapshot? selection, List<OcrReviewLine> lines)
    {
        Source = source;
        Selection = selection;
        Lines = Array.AsReadOnly(lines.ToArray());
        lineLookup = lines.ToDictionary(line => line.LineIndex);
    }

    internal OcrReviewLine FindLine(int index) => lineLookup[index];
}

public sealed partial class EditorSession
{
    /// <summary>Freezes the text that the user can review, send to translation, or apply on the image.</summary>
    public OcrTextReview PrepareRecognizedTextReview(OcrSnapshot source, OcrSelectionSnapshot? selection = null)
    {
        VerifyAccess();
        RequireCurrentOcr(source);
        if (!source.Available)
            throw new InvalidOperationException("Recognize the image before reviewing text.");
        if (selection != null && !ReferenceEquals(selection.Source, source))
            throw new InvalidOperationException("This text selection belongs to an earlier recognition. Select the words again.");

        IReadOnlyList<int> indices = selection?.LineIndices ?? source.Lines.Select(line => line.Index).ToArray();
        if (indices.Count > 4096)
            throw new InvalidOperationException("Select up to 4,096 lines to review them together.");
        var lines = new List<OcrReviewLine>(indices.Count);
        foreach (int index in indices)
        {
            var line = source.NativeResult.Lines[index];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            lines.Add(selection == null
                ? new OcrReviewLine(index, line.Text, line.Bounds, isWholeLine: true)
                : CaptureOcrReviewLine(source, selection, index));
        }
        if (lines.Count == 0)
            throw new InvalidOperationException("Select recognized text to review.");
        if (lines.Sum(line => (long)line.Text.Length) > 512 * 1024 ||
            lines.Any(line => line.Text.Length > MaximumTextCharacters || line.Text.Contains('\r') || line.Text.Contains('\n')))
            throw new InvalidOperationException("This text is too large to review in place. Select a smaller area or copy the extraction.");
        return new OcrTextReview(source, selection, lines);
    }

    private static OcrReviewLine CaptureOcrReviewLine(OcrSnapshot source, OcrSelectionSnapshot selection, int index)
    {
        var line = source.NativeResult.Lines[index];
        RectangleF ink = RectangleF.Empty;
        int first = -1, end = -1, cursor = 0;
        bool allPositionedWordsSelected = true;
        bool selectionEnded = false;
        for (int wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
        {
            var word = line.Words[wordIndex];
            if (string.IsNullOrWhiteSpace(word.Text) || !ValidOcrBounds(word.Bounds)) continue;
            bool chosen = selection.IncludesWord(index, wordIndex);
            allPositionedWordsSelected &= chosen;
            if (first >= 0 && !chosen)
            {
                selectionEnded = true;
                continue;
            }
            if (selectionEnded)
                throw new InvalidOperationException($"The selection on line {index + 1} has separated words. Select one continuous group to edit it, or use Copy selection.");
            int start = line.Text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException($"The selected words on line {index + 1} could not be mapped to the recognized text. Select the complete extraction to review all lines, or copy your selection.");
            cursor = start + word.Text.Length;
            if (!chosen) continue;
            if (first < 0) first = start;
            end = cursor;
            ink = ink.IsEmpty ? word.Bounds : RectangleF.Union(ink, word.Bounds);
        }
        if (first < 0)
            throw new InvalidOperationException($"Select the words on line {index + 1} again before reviewing them.");
        return new OcrReviewLine(index, line.Text[first..end], ink,
            isWholeLine: allPositionedWordsSelected && first == 0 && end == line.Text.Length);
    }

    /// <summary>Commits reviewed corrections atomically. An unchanged line keeps its source pixels.</summary>
    public Task<OcrConversionResult> ApplyRecognizedTextReviewAsync(OcrTextReview review,
        IReadOnlyList<string> editedLines, CancellationToken cancellationToken = default) =>
        ApplyRecognizedReviewAsync(review, editedLines, isTranslation: false, cancellationToken);

    /// <summary>Writes the reviewed output back to the same scope that was submitted to the translator.</summary>
    public Task<OcrConversionResult> ApplyReviewedTranslationAsync(OcrTextReview review,
        IReadOnlyList<string> translatedLines, CancellationToken cancellationToken = default) =>
        ApplyRecognizedReviewAsync(review, translatedLines, isTranslation: true, cancellationToken);

    /// <summary>Converts the exact reviewed source scope to editable text, preserving unselected words.</summary>
    public Task<OcrConversionResult> ConvertRecognizedTextReviewAsync(OcrTextReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        return ConvertRecognizedTextCoreAsync(review.Source, review.Lines.Select(line => line.LineIndex).ToArray(),
            null, cancellationToken, review);
    }

    private Task<OcrConversionResult> ApplyRecognizedReviewAsync(OcrTextReview review,
        IReadOnlyList<string> editedLines, bool isTranslation, CancellationToken cancellationToken)
    {
        VerifyCanMutate();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(editedLines);
        RequireCurrentOcr(review.Source);
        if (editedLines.Count != review.Lines.Count)
            return Task.FromResult(OcrFailure("Keep one edited line for each original line so the text can stay in place."));
        var reviewed = new Dictionary<int, string>();
        var lineIndices = new int[editedLines.Count];
        for (int i = 0; i < editedLines.Count; i++)
        {
            int index = review.Lines[i].LineIndex;
            string edited = editedLines[i];
            if (string.IsNullOrWhiteSpace(edited) || edited.Length > MaximumTextCharacters ||
                (!isTranslation && (edited.Contains('\r') || edited.Contains('\n'))))
                return Task.FromResult(ReviewedLineFailure(index, "Enter text on this line or restore its original wording. To remove source pixels, use Delete selection."));
            lineIndices[i] = index;
            reviewed.Add(index, edited);
        }
        return ConvertRecognizedTextCoreAsync(review.Source, lineIndices, reviewed, cancellationToken, review, isTranslation);
    }

    private Rectangle BuildSelectedOcrMask(OcrSnapshot source, OcrSelectionSnapshot selection, int sourceIndex, RectangleF ink)
    {
        Rectangle mask = EncloseSourcePixels(ink);
        mask.Inflate(2, 2);
        mask.Intersect(new Rectangle(Point.Empty, document.ImageSize));
        var clip = source.NativeResult.Lines[sourceIndex].RecognitionClipBounds;
        if (!clip.IsEmpty) mask.Intersect(Rectangle.Ceiling(clip));
        foreach (RectangleF graphic in source.NativeResult.ProtectedGraphics)
            mask = ProtectOcrPadding(mask, ink, graphic);
        for (int index = 0; index < document.AnnotationCount; index++)
            if (document.GetTextAnnotation(index) is { } note)
                mask = ProtectOcrPadding(mask, ink, note.InkBounds);
        for (int lineIndex = 0; lineIndex < source.NativeResult.Lines.Count; lineIndex++)
        {
            var line = source.NativeResult.Lines[lineIndex];
            mask = ProtectOcrPadding(mask, ink, line.ExcludedGraphicBounds);
            if (lineIndex != sourceIndex && ValidOcrBounds(line.Bounds))
                mask = ProtectOcrPadding(mask, ink, line.Bounds);
            for (int wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                if (!ValidOcrBounds(word.Bounds) ||
                    (lineIndex == sourceIndex && selection.IncludesWord(lineIndex, wordIndex))) continue;
                // Use native word identity, including for fractional bounds.
                mask = ProtectOcrPadding(mask, ink, word.Bounds);
            }
        }
        return mask;
    }
}
