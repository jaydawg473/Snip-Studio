using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private bool textBoxDrawing;
        private PointF textBoxStart, textBoxEnd;
        private object textBoxImage;
        private long textBoxRevision;

        internal bool TextBoxDrawingActive { get { return textBoxDrawing; } }
        private bool TextBoxSnapshotCurrent
        {
            get { return HasImage && Object.ReferenceEquals(textBoxImage, ImageIdentity) && textBoxRevision == DocumentRevision; }
        }

        internal bool BeginTextBoxDrawing(PointF point)
        {
            if (!HasImage || disposingCanvas || textActionsMode || tool != EditorTool.Text || !FiniteTextBoxPoint(point) || !CommitInlineText()) return false;
            CancelGesture();
            textBoxStart = textBoxEnd = ClampTextBoxPoint(point);
            textBoxImage = ImageIdentity;
            textBoxRevision = DocumentRevision;
            textBoxDrawing = true;
            selectedIndex = -1;
            Capture = true;
            RaiseTextStyleChanged();
            Invalidate();
            return true;
        }

        internal bool UpdateTextBoxDrawing(PointF point)
        {
            if (!textBoxDrawing) return false;
            if (!TextBoxSnapshotCurrent || !FiniteTextBoxPoint(point))
            { CancelTextBoxDrawing(); return true; }
            textBoxEnd = ClampTextBoxPoint(point);
            Invalidate();
            return true;
        }

        internal bool CompleteTextBoxDrawing(PointF point)
        {
            if (!textBoxDrawing) return false;
            UpdateTextBoxDrawing(point);
            if (!textBoxDrawing) return true;
            PointF anchor = textBoxStart;
            RectangleF frame = TextBoxDrawingBounds;
            bool current = TextBoxSnapshotCurrent;
            // End capture before creating the child editor. Its startup cancels
            // other gestures, and capture-loss must not discard the new draft.
            textBoxDrawing = false;
            textBoxImage = null;
            if (Capture) Capture = false;
            if (current)
            {
                if (frame.Width >= 1 && frame.Height >= 1 && frame.Width * zoom >= 3 && frame.Height * zoom >= 3)
                    BeginInlineTextNote(frame);
                else BeginInlineTextNote(anchor);
            }
            Invalidate();
            return true;
        }

        internal void CancelTextBoxDrawing()
        {
            if (!textBoxDrawing) return;
            textBoxDrawing = false;
            textBoxImage = null;
            if (Capture) Capture = false;
            Invalidate();
        }

        internal void DrawTextBoxPreview(Graphics graphics)
        {
            if (!textBoxDrawing || !TextBoxSnapshotCurrent) return;
            RectangleF frame = TextBoxDrawingBounds;
            using (Brush fill = new SolidBrush(Color.FromArgb(22, 0, 103, 192)))
            using (Pen outline = new Pen(Color.FromArgb(0, 103, 192), 1.3f / zoom))
            {
                outline.DashStyle = DashStyle.Dash;
                graphics.FillRectangle(fill, frame);
                graphics.DrawRectangle(outline, frame.X, frame.Y, frame.Width, frame.Height);
            }
        }

        private RectangleF TextBoxDrawingBounds
        {
            get { return RectangleF.FromLTRB(Math.Min(textBoxStart.X, textBoxEnd.X), Math.Min(textBoxStart.Y, textBoxEnd.Y),
                Math.Max(textBoxStart.X, textBoxEnd.X), Math.Max(textBoxStart.Y, textBoxEnd.Y)); }
        }

        private PointF ClampTextBoxPoint(PointF point)
        {
            return new PointF(Math.Max(0, Math.Min(ImageSize.Width, point.X)), Math.Max(0, Math.Min(ImageSize.Height, point.Y)));
        }

        private static bool FiniteTextBoxPoint(PointF point)
        {
            return !Single.IsNaN(point.X) && !Single.IsInfinity(point.X) && !Single.IsNaN(point.Y) && !Single.IsInfinity(point.Y);
        }
    }
}
