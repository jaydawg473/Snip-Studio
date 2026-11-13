using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private Rectangle cropFrame, cropDragStart;
        private CropFrameHandle cropHandle;
        private PointF cropGrab;
        private object cropImage;
        private long cropRevision;
        private bool cropDraftReady, cropGestureMoved;
        private bool cropCommandsEnabled = true;

        public event EventHandler CropSelectionChanged;
        public bool CropModeActive { get { return HasImage && tool == EditorTool.Crop && !textActionsMode; } }
        public Rectangle CropSelection { get { EnsureCropFrame(); return CropModeActive ? cropFrame : Rectangle.Empty; } }
        public bool CropCommandsEnabled { get { return cropCommandsEnabled; } set { cropCommandsEnabled = value; } }
        public bool CanApplyCrop { get { Rectangle frame = CropSelection; return cropCommandsEnabled && !CropDragging && frame.Width >= 2 && frame.Height >= 2 && frame != new Rectangle(Point.Empty, ImageSize); } }
        private bool CropDragging { get { return cropHandle != CropFrameHandle.None; } }

        private void EnsureCropFrame()
        {
            if (!CropModeActive) return;
            if (cropDraftReady && Object.ReferenceEquals(cropImage, ImageIdentity) && cropRevision == DocumentRevision) return;
            cropFrame = new Rectangle(Point.Empty, ImageSize);
            cropImage = ImageIdentity; cropRevision = DocumentRevision; cropDraftReady = true;
        }

        private void ResetCropFrame()
        {
            CancelCropDrag(); cropDraftReady = false; cropImage = null; cropFrame = Rectangle.Empty;
            EnsureCropFrame(); RaiseCropSelectionChanged();
        }

        private void RaiseCropSelectionChanged()
        {
            Invalidate();
            EventHandler changed = CropSelectionChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        internal bool SetCropSelection(Rectangle frame)
        {
            if (!CropModeActive) return false;
            EnsureCropFrame();
            if (frame.Width < 2 || frame.Height < 2 || !new Rectangle(Point.Empty, ImageSize).Contains(frame) || frame == cropFrame) return false;
            cropFrame = frame; RaiseCropSelectionChanged(); return true;
        }

        public bool ApplyCrop()
        {
            if (!CropModeActive || !cropCommandsEnabled || CropDragging) return false;
            EnsureCropFrame();
            Rectangle frame = cropFrame;
            bool changed = CanApplyCrop;
            if (changed)
            {
                ChangeViewIntent(); StopViewAnimations();
                document.Crop(frame); selectedIndex = -1;
                // Applying changes image dimensions; choosing the tool itself
                // never changes zoom or image coordinates.
                fit = true; RecalculateFit(); AutoScrollPosition = Point.Empty;
            }
            Tool = EditorTool.Select;
            if (changed) RaiseChanged();
            RaiseCropSelectionChanged();
            return changed;
        }

        public void CancelCrop()
        {
            if (tool != EditorTool.Crop || !cropCommandsEnabled) return;
            CancelCropDrag();
            Tool = EditorTool.Select;
            RaiseCropSelectionChanged();
        }

        private bool BeginCropDrag(MouseEventArgs e)
        {
            if (!CropModeActive || e.Button != MouseButtons.Left) return false;
            if (!cropCommandsEnabled) return true;
            EnsureCropFrame();
            PointF point = ToImage(e.Location, false);
            CropFrameHandle hit = CropFrame.HitTest(cropFrame, point, CropHitRadius);
            if ((hit == CropFrameHandle.Move && cropFrame == new Rectangle(Point.Empty, ImageSize)) ||
                (hit == CropFrameHandle.None && IsOnImage(e.Location))) hit = CropFrameHandle.Create;
            if (hit == CropFrameHandle.None) return true;
            cropDragStart = cropFrame; cropGrab = point; cropHandle = hit; cropGestureMoved = false;
            Capture = true; SetCropPointer(hit); Invalidate();
            return true;
        }

        private bool UpdateCropDrag(MouseEventArgs e)
        {
            if (!CropModeActive) return false;
            if (!cropCommandsEnabled) { if (CropDragging) CancelGesture(); return true; }
            EnsureCropFrame();
            PointF point = ToImage(e.Location, false);
            if (CropDragging)
            {
                float dx = point.X - cropGrab.X, dy = point.Y - cropGrab.Y;
                if (Math.Abs(dx * zoom) >= 2 || Math.Abs(dy * zoom) >= 2) cropGestureMoved = true;
                Rectangle next = cropHandle == CropFrameHandle.Create ? CropFrame.Create(cropGrab, point, ImageSize) :
                    CropFrame.Resize(cropDragStart, cropHandle, new PointF(dx, dy), ImageSize);
                if (!next.IsEmpty && cropGestureMoved) SetCropSelection(next);
                SetCropPointer(cropHandle);
            }
            else SetCropPointer(CropFrame.HitTest(cropFrame, point, CropHitRadius));
            return true;
        }

        private bool EndCropDrag(MouseEventArgs e)
        {
            if (!CropDragging || e.Button != MouseButtons.Left) return false;
            UpdateCropDrag(e);
            cropHandle = CropFrameHandle.None;
            if (!cropGestureMoved) cropFrame = cropDragStart;
            Capture = false;
            UpdateCropDrag(e); RaiseCropSelectionChanged();
            return true;
        }

        private void CancelCropDrag()
        {
            if (!CropDragging) return;
            cropFrame = cropDragStart; cropHandle = CropFrameHandle.None; cropGestureMoved = false;
            RaiseCropSelectionChanged();
        }

        private float CropHitRadius { get { return 8f * Math.Max(96, DeviceDpi) / 96f / zoom; } }

        private void SetCropPointer(CropFrameHandle handle)
        {
            if (handle == CropFrameHandle.Move) Cursor = cropFrame == new Rectangle(Point.Empty, ImageSize) ? Cursors.Default : Cursors.SizeAll;
            else if (handle == CropFrameHandle.Left || handle == CropFrameHandle.Right) Cursor = Cursors.SizeWE;
            else if (handle == CropFrameHandle.Top || handle == CropFrameHandle.Bottom) Cursor = Cursors.SizeNS;
            else if (handle == (CropFrameHandle.Left | CropFrameHandle.Top) || handle == (CropFrameHandle.Right | CropFrameHandle.Bottom)) Cursor = Cursors.SizeNWSE;
            else if (handle == (CropFrameHandle.Right | CropFrameHandle.Top) || handle == (CropFrameHandle.Left | CropFrameHandle.Bottom)) Cursor = Cursors.SizeNESW;
            else Cursor = Cursors.Default;
        }

        private bool HandleCropKey(Keys key)
        {
            if (!CropModeActive) return false;
            if (!cropCommandsEnabled && (key == Keys.Enter || key == Keys.Escape)) return true;
            if (key == Keys.Enter) { ApplyCrop(); return true; }
            if (key == Keys.Escape)
            {
                if (CropDragging) { CancelCropDrag(); Capture = false; UpdateCursor(); }
                else CancelCrop();
                return true;
            }
            return false;
        }

        private void DrawCropFrame(Graphics graphics)
        {
            if (!CropModeActive) return;
            EnsureCropFrame();
            using (Brush shade = new SolidBrush(Color.FromArgb(112, 15, 23, 42)))
            using (Region outside = new Region(new Rectangle(Point.Empty, ImageSize)))
            {
                outside.Exclude(cropFrame); graphics.FillRegion(shade, outside);
            }
            GraphicsState state = graphics.Save();
            try
            {
                // Edge handles extend past the bitmap and remain fully visible.
                graphics.ResetClip();
                float scale = Math.Max(96, DeviceDpi) / 96f, pixel = scale / zoom;
                using (Pen outline = new Pen(Color.White, 1.5f * pixel))
                using (Pen shadow = new Pen(Color.FromArgb(0, 95, 175), 3.5f * pixel))
                {
                    graphics.DrawRectangle(shadow, cropFrame); graphics.DrawRectangle(outline, cropFrame);
                }
                if (CropDragging)
                    using (Pen grid = new Pen(Color.FromArgb(140, Color.White), pixel))
                        for (int i = 1; i <= 2; i++)
                        {
                            float x = cropFrame.Left + cropFrame.Width * i / 3f, y = cropFrame.Top + cropFrame.Height * i / 3f;
                            graphics.DrawLine(grid, x, cropFrame.Top, x, cropFrame.Bottom);
                            graphics.DrawLine(grid, cropFrame.Left, y, cropFrame.Right, y);
                        }
                PointF[] handles = new PointF[] {
                    new PointF(cropFrame.Left,cropFrame.Top),new PointF(cropFrame.Left+cropFrame.Width/2f,cropFrame.Top),new PointF(cropFrame.Right,cropFrame.Top),
                    new PointF(cropFrame.Right,cropFrame.Top+cropFrame.Height/2f),new PointF(cropFrame.Right,cropFrame.Bottom),new PointF(cropFrame.Left+cropFrame.Width/2f,cropFrame.Bottom),
                    new PointF(cropFrame.Left,cropFrame.Bottom),new PointF(cropFrame.Left,cropFrame.Top+cropFrame.Height/2f) };
                using (Brush fill = new SolidBrush(Color.White))
                using (Pen edge = new Pen(Color.FromArgb(0, 95, 175), pixel))
                    foreach (PointF center in handles)
                    {
                        RectangleF box = new RectangleF(center.X - 4 * pixel, center.Y - 4 * pixel, 8 * pixel, 8 * pixel);
                        graphics.FillRectangle(fill, box); graphics.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
                    }
            }
            finally { graphics.Restore(state); }
        }
    }
}
