using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private readonly Dictionary<EditorTool, ToolCursor> toolPointers = new Dictionary<EditorTool, ToolCursor>();
        private ContextMenuStrip annotationPopup;
        private ContextMenuStrip recognizedTextPopup;
        private ToolStripMenuItem moveAnnotationItem, editAnnotationItem;
        private ToolStripMenuItem copyRecognizedSelectionItem;
        private int contextAnnotationIndex = -1;
        private long contextDocumentRevision;

        public event EventHandler ToolChanged;
        public event EventHandler CopySelectedRecognizedTextRequested;

        private void InitializeToolPointers()
        {
            HandleCreated += delegate { UpdateCursor(); };
            DpiChangedAfterParent += delegate
            {
                if (!AnnotationHandleDragging && !moving && !panning && !textSelectionDragging) UpdateCursor();
            };
            MouseEnter += delegate
            {
                if (!AnnotationHandleDragging && !moving && !panning && !textSelectionDragging) UpdateCursor();
            };
        }

        private void UpdateToolPointer()
        {
            if (disposingCanvas) return;
            if (AnnotationHandleDragging) return;
            if (textSelectionDragging) { Cursor = Cursors.IBeam; return; }
            if (moving || panning || removalMoving) { Cursor = Cursors.SizeAll; return; }
            if (textActionsMode) { Cursor = Cursors.Default; return; }
            if (CropModeActive)
            {
                if (CropDragging) SetCropPointer(cropHandle);
                else if (IsHandleCreated) SetCropPointer(CropFrame.HitTest(CropSelection, ToImage(PointToClient(MousePosition), false), CropHitRadius));
                else Cursor = Cursors.Default;
                return;
            }
            if (tool == EditorTool.Select)
            {
                if (IsHandleCreated) UpdateSelectPointer(PointToClient(MousePosition));
                else Cursor = Cursors.Default;
                return;
            }

            // A tool choice changes the toolbar selection, not the ordinary
            // pointer. Only contextual grips and text editing use other cursors.
            Cursor = Cursors.Default;
        }

        private void UpdateSelectPointer(Point location)
        {
            Cursor = IsOnSourceRemovalSelection(location) || (HasImage && IsOnImage(location) && document.HitTestAnnotation(ToImage(location, false), 5 / zoom) >= 0)
                ? Cursors.SizeAll : Cursors.Default;
        }

        internal bool CloseMenusForCapture()
        {
            bool closed = false;
            if (removalPopup != null && !removalPopup.IsDisposed && removalPopup.Visible)
            {
                removalPopup.Close(ToolStripDropDownCloseReason.CloseCalled);
                closed = true;
            }
            if (annotationPopup != null && !annotationPopup.IsDisposed && annotationPopup.Visible)
            {
                annotationPopup.Close(ToolStripDropDownCloseReason.CloseCalled);
                closed = true;
            }
            if (recognizedTextPopup != null && !recognizedTextPopup.IsDisposed && recognizedTextPopup.Visible)
            {
                recognizedTextPopup.Close(ToolStripDropDownCloseReason.CloseCalled);
                closed = true;
            }
            return closed;
        }

        private void RaiseToolChanged()
        {
            EventHandler handler = ToolChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void HandleAnnotationRightClick(MouseEventArgs e)
        {
            if (!textActionsMode && (tool == EditorTool.RemoveArea ||
                (tool == EditorTool.Select && IsOnSourceRemovalSelection(e.Location))))
            {
                if (ClientRectangle.Contains(e.Location)) ShowSourceRemovalMenu(e.Location);
                return;
            }
            CancelGesture();
            if (!ClientRectangle.Contains(e.Location)) return;
            // Preserve the chosen words. A right-click does not select an
            // annotation underneath the OCR layer or replace that word range.
            if (textActionsMode)
            {
                EnsureRecognizedTextPopup();
                copyRecognizedSelectionItem.Enabled = HasSelectedRecognizedText;
                StudioTheme.StyleMenu(recognizedTextPopup);
                recognizedTextPopup.Show(this, e.Location);
                return;
            }
            int hit = IsOnImage(e.Location) ? document.HitTestAnnotation(ToImage(e.Location, false), 5 / zoom) : -1;
            contextAnnotationIndex = hit;
            contextDocumentRevision = document.Revision;
            if (hit >= 0) SelectContextAnnotation(hit);
            EnsureAnnotationPopup();
            moveAnnotationItem.Text = hit >= 0 ? "Move selected object" : "Switch to Move";
            moveAnnotationItem.Checked = tool == EditorTool.Select;
            editAnnotationItem.Visible = hit >= 0 && document.GetTextAnnotation(hit) != null;
            StudioTheme.StyleMenu(annotationPopup);
            annotationPopup.Show(this, e.Location);
        }

        private void SelectContextAnnotation(int index)
        {
            // ToolChanged lets the toolbar update before an object is selected.
            // In particular, restoring the previous ink after Highlight cannot
            // accidentally recolor the text annotation under this right-click.
            Tool = EditorTool.Select;
            selectedIndex = index;
            RaiseTextStyleChanged();
            NotifyShapeSelectionChanged();
            UpdateCursor();
            Invalidate();
        }

        private void EnsureAnnotationPopup()
        {
            if (annotationPopup != null && !annotationPopup.IsDisposed) return;
            annotationPopup = new ContextMenuStrip();
            moveAnnotationItem = new ToolStripMenuItem("Switch to Move");
            moveAnnotationItem.Click += delegate
            {
                if (!HasImage || document.Revision != contextDocumentRevision) return;
                SelectContextAnnotation(contextAnnotationIndex);
                Focus();
            };
            editAnnotationItem = new ToolStripMenuItem("Advanced text…");
            editAnnotationItem.Click += delegate
            {
                if (!HasImage || document.Revision != contextDocumentRevision || document.GetTextAnnotation(contextAnnotationIndex) == null) return;
                int editIndex = contextAnnotationIndex;
                long editRevision = contextDocumentRevision;
                SelectContextAnnotation(editIndex);
                // Open after the menu's close sequence, keeping it alive until
                // WinForms has completed its own context-menu processing.
                BeginInvoke((Action)delegate
                {
                    if (!IsDisposed && HasImage && document.Revision == editRevision && document.GetTextAnnotation(editIndex) != null)
                        ShowAdvancedTextEditor();
                });
            };
            annotationPopup.Items.Add(moveAnnotationItem);
            annotationPopup.Items.Add(editAnnotationItem);
        }

        private void EnsureRecognizedTextPopup()
        {
            if (recognizedTextPopup != null && !recognizedTextPopup.IsDisposed) return;
            recognizedTextPopup = new ContextMenuStrip();
            copyRecognizedSelectionItem = new ToolStripMenuItem("Copy selection");
            copyRecognizedSelectionItem.ShortcutKeyDisplayString = "Ctrl+C";
            copyRecognizedSelectionItem.Click += delegate
            {
                if (!textActionsMode || !HasSelectedRecognizedText) return;
                EventHandler handler = CopySelectedRecognizedTextRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            recognizedTextPopup.Items.Add(copyRecognizedSelectionItem);
        }

        private void DisposeToolPointers()
        {
            Cursor = Cursors.Default;
            foreach (ToolCursor pointer in toolPointers.Values) pointer.Dispose();
            toolPointers.Clear();
            if (annotationPopup != null) annotationPopup.Dispose();
            if (recognizedTextPopup != null) recognizedTextPopup.Dispose();
        }
    }
}
