using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnipStudio.WinUI.Backend;
using Windows.Foundation;

namespace SnipStudio.WinUI;

public sealed partial class EditorViewport
{
    private IReadOnlyList<OcrLineSnapshot>? highlightedTextLines;

    internal void SetRecognizedTextHighlights(IReadOnlyList<OcrLineSnapshot>? lines)
    {
        if (!view.HasImage) lines = null;
        if (ReferenceEquals(highlightedTextLines, lines)) return;
        highlightedTextLines = lines;
        RecognizedTextSpotlight.Data = lines is { Count: > 0 }
            ? CreateRecognizedTextSpotlight(lines, view.ImageWidth, view.ImageHeight)
            : null;
        PositionRecognizedTextHighlights();
    }

    private void PositionRecognizedTextHighlights()
    {
        bool visible = tool == PreviewTool.OcrSelect && Picture.Source is not null && RecognizedTextSpotlight.Data is not null;
        RecognizedTextSpotlight.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        CanvasPoint origin = view.DisplayOrigin;
        Canvas.SetLeft(RecognizedTextSpotlight, origin.X);
        Canvas.SetTop(RecognizedTextSpotlight, origin.Y);
        RecognizedTextSpotlightScale.ScaleX = RecognizedTextSpotlightScale.ScaleY = view.Scale;
    }

    private static GeometryGroup? CreateRecognizedTextSpotlight(IReadOnlyList<OcrLineSnapshot> lines, int width, int height)
    {
        List<OcrBounds> wordAreas = RecognizedTextSpotlightLayout.CreateAreas(lines, width, height);
        if (wordAreas.Count == 0) return null;

        // The image rectangle is the scrim. Consecutive strips of the word-area
        // union cut it out without dimming any overlapping OCR bounds twice.
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, width, height) });
        wordAreas.Sort((first, second) => first.X.CompareTo(second.X));
        var edges = new List<(double Y, int Word, bool Enter)>(wordAreas.Count * 2);
        for (int index = 0; index < wordAreas.Count; index++)
        {
            OcrBounds area = wordAreas[index];
            edges.Add((area.Y, index, true));
            edges.Add((area.Y + area.Height, index, false));
        }

        edges.Sort((first, second) => first.Y.CompareTo(second.Y));
        var active = new SortedSet<int>();
        double previousY = edges[0].Y;
        foreach (var edge in edges)
        {
            if (edge.Y > previousY)
            {
                double left = -1, right = -1;
                foreach (int index in active)
                {
                    OcrBounds word = wordAreas[index];
                    if (word.X > right)
                    {
                        if (right > left) AddStrip(left, right, previousY, edge.Y);
                        left = word.X;
                    }
                    right = Math.Max(right, word.X + word.Width);
                }
                if (right > left) AddStrip(left, right, previousY, edge.Y);
                previousY = edge.Y;
            }

            if (edge.Enter) active.Add(edge.Word);
            else active.Remove(edge.Word);
        }

        return geometry;

        void AddStrip(double left, double right, double top, double bottom) =>
            geometry.Children.Add(new RectangleGeometry { Rect = new Rect(left, top, right - left, bottom - top) });
    }
}
