using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SnipStudio.WinUI.Backend;
using Windows.System;
using Windows.UI;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    private readonly record struct SourceCaret(int Index, double X);

    private OcrBounds sourceTextBounds;
    private SourceCaret[] sourceCarets = [];
    private bool showOriginalSourceText;
    private int sourceSelectionAnchor;
    private uint? sourceSelectionPointer;
    private readonly Rectangle sourceSelectionMark = new();
    private readonly SolidColorBrush sourceSelectionBrush = new(Color.FromArgb(70, 0, 102, 204));
    private readonly SolidColorBrush sourceCaretBrush = new();

    private void ConfigureSourceTextSelection(OcrLineSnapshot? line, string text)
    {
        ClearSourceTextSelection();
        if (line is null || line.Bounds is not { Width: > 0, Height: > 0 } bounds || text.Length == 0) return;

        sourceTextBounds = bounds;
        sourceCaretBrush.Color = ToColor(initialTextDraft!.Argb);
        // Use text-element boundaries so a source click never splits a surrogate
        // pair or combining sequence. Anchor the projected stops to each OCR word.
        int[] stops = [.. StringInfo.ParseCombiningCharacters(text), text.Length];
        var anchors = new SortedDictionary<int, double>();
        bool rightToLeft = line.Words.Count > 1 && line.Words[^1].Bounds.X < line.Words[0].Bounds.X;
        anchors[0] = rightToLeft ? bounds.X + bounds.Width : bounds.X;
        anchors[text.Length] = rightToLeft ? bounds.X : bounds.X + bounds.Width;
        int cursor = 0;
        foreach (OcrWordSnapshot word in line.Words)
        {
            if (word.Text.Length == 0 || word.Bounds.Width <= 0 || word.Bounds.Height <= 0) continue;
            int start = text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (start < 0) continue;
            cursor = start + word.Text.Length;
            anchors[start] = rightToLeft ? word.Bounds.X + word.Bounds.Width : word.Bounds.X;
            anchors[cursor] = rightToLeft ? word.Bounds.X : word.Bounds.X + word.Bounds.Width;
        }
        var edges = anchors.ToArray();
        sourceCarets = new SourceCaret[stops.Length];
        for (int i = 0, edge = 0; i < stops.Length; i++)
        {
            int stop = stops[i];
            while (edge + 1 < edges.Length - 1 && stop > edges[edge + 1].Key) edge++;
            var first = edges[edge];
            var last = edges[edge + 1];
            double fraction = (double)(stop - first.Key) / (last.Key - first.Key);
            sourceCarets[i] = new SourceCaret(stop, first.Value + (last.Value - first.Value) * fraction);
        }
    }

    private void ClearSourceTextSelection()
    {
        sourceCarets = [];
        sourceSelectionPointer = null;
        ShowOriginalSourceText(false);
    }

    private void ShowOriginalSourceText(bool value)
    {
        showOriginalSourceText = value;
        // The native editor retains focus, IME, undo and keyboard selection.
        // Its reconstructed glyphs and border stay hidden until the draft changes.
        TextEditorFrame.Opacity = value ? 0 : 1;
        TextEditorFrame.IsHitTestVisible = !value || sourceCarets.Length == 0;
        UpdateSourceTextVisual();
    }

    private void OnInlineSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (!loadingText) UpdateSourceTextVisual();
    }

    private void UpdateSourceTextVisual()
    {
        if (!view.HasImage || recognizedDraftMask is not OcrBounds mask)
        {
            RecognizedDraftMask.Visibility = sourceSelectionMark.Visibility = Visibility.Collapsed;
            return;
        }

        RecognizedDraftMask.Visibility = showOriginalSourceText ? Visibility.Collapsed : Visibility.Visible;
        if (!showOriginalSourceText) PositionRectangle(RecognizedDraftMask, mask.X, mask.Y, mask.Width, mask.Height);
        sourceSelectionMark.Visibility = Visibility.Collapsed;
        if (!showOriginalSourceText || sourceCarets.Length == 0) return;

        var selection = InlineEditor.Document.Selection;
        bool caret = selection.StartPosition == selection.EndPosition;
        if (caret && InlineEditor.FocusState == FocusState.Unfocused) return;
        double first = SourceCaretX(selection.StartPosition);
        double last = SourceCaretX(selection.EndPosition);
        double pixel = 1.0 / (view.Scale * (XamlRoot?.RasterizationScale ?? 1));
        if (sourceSelectionMark.Parent is null) SourceTextSelection.Children.Add(sourceSelectionMark);
        sourceSelectionMark.Fill = caret ? sourceCaretBrush : sourceSelectionBrush;
        PositionRectangle(sourceSelectionMark, Math.Min(first, last), sourceTextBounds.Y,
            Math.Max(pixel, Math.Abs(last - first)), sourceTextBounds.Height);
        sourceSelectionMark.Visibility = Visibility.Visible;
    }

    private double SourceCaretX(int index)
    {
        int start = 0, end = sourceCarets.Length - 1;
        while (start < end)
        {
            int midpoint = (start + end + 1) / 2;
            if (sourceCarets[midpoint].Index <= index) start = midpoint;
            else end = midpoint - 1;
        }
        return sourceCarets[start].X;
    }

    private int SourceCaretAt(double x)
    {
        SourceCaret closest = sourceCarets[0];
        foreach (SourceCaret candidate in sourceCarets)
            if (Math.Abs(candidate.X - x) <= Math.Abs(closest.X - x)) closest = candidate;
        return closest.Index;
    }

    internal bool PlaceSourceTextCaret(ImagePoint point)
    {
        if (!showOriginalSourceText || sourceCarets.Length == 0) return false;
        int caret = SourceCaretAt(point.X);
        InlineEditor.Document.Selection.SetRange(caret, caret);
        return true;
    }

    private bool TryBeginSourceSelection(CanvasPoint position, PointerRoutedEventArgs args)
    {
        if (!showOriginalSourceText || sourceCarets.Length == 0) return false;
        CanvasPoint image = view.ImageAt(position);
        OcrBounds bounds = sourceTextBounds;
        if (image.X < bounds.X - 2 || image.X > bounds.X + bounds.Width + 2 ||
            image.Y < bounds.Y || image.Y > bounds.Y + bounds.Height || !Host.CapturePointer(args.Pointer)) return false;

        var selection = InlineEditor.Document.Selection;
        int positionInText = SourceCaretAt(image.X);
        sourceSelectionPointer = args.Pointer.PointerId;
        sourceSelectionAnchor = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0
            ? selection.StartPosition : positionInText;
        InlineEditor.Focus(FocusState.Programmatic);
        selection.SetRange(sourceSelectionAnchor, positionInText);
        args.Handled = true;
        return true;
    }

    private bool TryContinueSourceSelection(PointerRoutedEventArgs args)
    {
        if (sourceSelectionPointer != args.Pointer.PointerId) return false;
        if (showOriginalSourceText && sourceCarets.Length > 0)
        {
            var point = args.GetCurrentPoint(Host).Position;
            int end = SourceCaretAt(view.ImageAt(new CanvasPoint(point.X, point.Y)).X);
            InlineEditor.Document.Selection.SetRange(sourceSelectionAnchor, end);
        }
        args.Handled = true;
        return true;
    }
}
