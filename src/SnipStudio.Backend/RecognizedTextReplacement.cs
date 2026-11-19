using System.Drawing;

namespace SnipStudio
{
    // The legacy document's detached value type is retained without importing
    // EditorCanvas or its WinForms gesture/lifecycle implementation. OCR and
    // in-place replacement are not exposed by the first WinUI migration slice.
    internal sealed class RecognizedTextReplacement
    {
        internal readonly Rectangle Bounds;
        internal readonly RectangleF LayoutBounds;
        internal readonly RectangleF MeasuredInkBounds;
        internal readonly PointF TextLocation;
        internal readonly string PlainText, Rtf;
        internal readonly Color Foreground, Background;
        internal readonly float FontSize, LayoutWidth;

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth)
            : this(bounds, textLocation, plainText, rtf, foreground, background, fontSize, layoutWidth, bounds) { }

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth, RectangleF layoutBounds)
            : this(bounds, textLocation, plainText, rtf, foreground, background, fontSize, layoutWidth, layoutBounds, RectangleF.Empty) { }

        internal RecognizedTextReplacement(Rectangle bounds, PointF textLocation, string plainText, string rtf,
            Color foreground, Color background, float fontSize, float layoutWidth, RectangleF layoutBounds, RectangleF measuredInkBounds)
        {
            Bounds = bounds; LayoutBounds = layoutBounds; MeasuredInkBounds = measuredInkBounds;
            TextLocation = textLocation; PlainText = plainText; Rtf = rtf;
            Foreground = foreground; Background = background; FontSize = fontSize; LayoutWidth = layoutWidth;
        }
    }
}
