using System;
using System.Drawing;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private ShapeStyle shapeStyle;
        public event EventHandler ShapeStyleChanged;

        public bool HasSelectedShape { get { return HasImage && !textActionsMode && document.GetShapeAnnotationStyle(selectedIndex) != null; } }
        public ShapeStyle SelectedShapeStyle { get { return HasSelectedShape ? document.GetShapeAnnotationStyle(selectedIndex) : null; } }
        public EditorTool? SelectedShapeTool { get { return HasSelectedShape ? document.GetShapeAnnotationTool(selectedIndex) : null; } }

        public ShapeStyle CurrentShapeStyle
        {
            get { return SelectedShapeStyle ?? shapeStyle ?? new ShapeStyle(Color.Empty, inkColor, strokeWidth); }
            set
            {
                if (value == null) throw new ArgumentNullException("value");
                shapeStyle = value;
                if (HasSelectedShape && document.UpdateShapeAnnotationStyle(selectedIndex, value)) RaiseChanged();
                else Invalidate();
                NotifyShapeSelectionChanged();
            }
        }

        public static bool IsShapeTool(EditorTool value)
        {
            return value == EditorTool.Rectangle || value == EditorTool.Ellipse || value == EditorTool.Line || value == EditorTool.Arrow;
        }

        internal EditorDocument.Annotation CreateShapePreview(PointF first, PointF second)
        {
            return EditorDocument.CreateShapeAnnotation(tool, first, second, CurrentShapeStyle);
        }

        internal bool CommitShape(PointF first, PointF second)
        {
            if (!IsShapeTool(tool)) return false;
            bool changed = document.AddShape(tool, first, second, CurrentShapeStyle);
            if (changed) selectedIndex = document.AnnotationCount - 1;
            NotifyShapeSelectionChanged();
            return changed;
        }

        internal void NotifyShapeSelectionChanged()
        {
            EventHandler changed = ShapeStyleChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }
    }
}
