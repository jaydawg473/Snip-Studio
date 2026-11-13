using System;
using System.Drawing;

namespace SnipStudio
{
    // Immutable styling can be shared by annotation history and drawing previews.
    // Color.Empty means None; transparent colors retain their original alpha.
    public sealed class ShapeStyle
    {
        public Color FillColor { get; private set; }
        public Color OutlineColor { get; private set; }
        public float OutlineWidth { get; private set; }
        public int FillOpacity { get; private set; }
        public int OutlineOpacity { get; private set; }
        public bool FillEnabled { get { return !FillColor.IsEmpty; } }
        public bool OutlineEnabled { get { return !OutlineColor.IsEmpty; } }
        public Color EffectiveFillColor { get { return ApplyOpacity(FillColor, FillOpacity); } }
        public Color EffectiveOutlineColor { get { return ApplyOpacity(OutlineColor, OutlineOpacity); } }

        public ShapeStyle(Color fillColor, Color outlineColor, float outlineWidth)
            : this(fillColor, outlineColor, outlineWidth, 100, 100) { }

        public ShapeStyle(Color fillColor, Color outlineColor, float outlineWidth, int fillOpacity, int outlineOpacity)
        {
            if (Single.IsNaN(outlineWidth) || Single.IsInfinity(outlineWidth) || outlineWidth <= 0 || outlineWidth > 1024)
                throw new ArgumentOutOfRangeException("outlineWidth");
            if (fillOpacity < 0 || fillOpacity > 100) throw new ArgumentOutOfRangeException("fillOpacity");
            if (outlineOpacity < 0 || outlineOpacity > 100) throw new ArgumentOutOfRangeException("outlineOpacity");
            FillColor = fillColor; OutlineColor = outlineColor; OutlineWidth = outlineWidth;
            FillOpacity = fillOpacity; OutlineOpacity = outlineOpacity;
        }

        internal bool VisibleFill { get { return FillEnabled && EffectiveFillColor.A > 0; } }
        internal bool VisibleOutline { get { return OutlineEnabled && EffectiveOutlineColor.A > 0; } }

        internal bool IsVisibleFor(EditorTool tool)
        {
            return VisibleOutline || ((tool == EditorTool.Rectangle || tool == EditorTool.Ellipse) && VisibleFill);
        }

        public bool SameAs(ShapeStyle other)
        {
            return other != null && FillEnabled == other.FillEnabled && OutlineEnabled == other.OutlineEnabled &&
                FillColor.ToArgb() == other.FillColor.ToArgb() && OutlineColor.ToArgb() == other.OutlineColor.ToArgb() &&
                OutlineWidth == other.OutlineWidth && FillOpacity == other.FillOpacity && OutlineOpacity == other.OutlineOpacity;
        }

        private static Color ApplyOpacity(Color color, int opacity)
        {
            if (color.IsEmpty || opacity == 100) return color;
            return Color.FromArgb((int)Math.Round(color.A * opacity / 100.0), color.R, color.G, color.B);
        }
    }
}
