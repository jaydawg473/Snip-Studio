using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private StudioButton applyCropButton, cancelCropButton;
        private Label cropSummary;
        private bool lastCropContextVisible;

        private bool IsCropContext
        {
            get { return canvas.HasImage && !canvas.TextActionsMode && canvas.Tool == EditorTool.Crop && canvas.CropModeActive; }
        }

        private bool CanChangeCrop
        {
            get { return !IsDisposed && !closing && !capturePending && !recordingSession && !scrollingCaptureActive; }
        }

        private void BuildCropActions()
        {
            applyCropButton = ActionButton("Apply crop", "crop", 128, delegate { ApplyPendingCrop(); });
            applyCropButton.Primary = true;
            applyCropButton.AccessibleName = "Apply crop";
            cancelCropButton = ActionButton("Cancel", "", 82, delegate { CancelPendingCrop(); });
            cancelCropButton.AccessibleName = "Cancel crop";
            cropSummary = new Label
            {
                AutoSize = false,
                AutoEllipsis = false,
                ForeColor = StudioTheme.Text,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleName = "Crop size and instructions"
            };
            contextToolbar.Controls.Add(applyCropButton);
            contextToolbar.Controls.Add(cancelCropButton);
            contextToolbar.Controls.Add(cropSummary);
            documentControls.Add(applyCropButton);
            documentControls.Add(cancelCropButton);
            tips.SetToolTip(applyCropButton, "Keep the selected crop area (Enter). Undo restores the original snip.");
            tips.SetToolTip(cancelCropButton, "Leave Crop without changing the snip (Escape).");
            tips.SetToolTip(cropSummary, "Drag an edge or corner to adjust the area to keep. Apply crop or Enter commits it; Cancel or Escape leaves the snip unchanged.");
            canvas.CropSelectionChanged += delegate
            {
                if (IsDisposed || closing) return;
                bool previousContext = lastCropContextVisible;
                UpdateCropActionControls();
                // The dimensions change while dragging. Update their text in
                // place rather than laying out the entire editor on every move.
                if (previousContext != lastCropContextVisible) LayoutChrome();
            };
            UpdateCropActionControls();
        }

        private void UpdateCropActionControls()
        {
            if (applyCropButton == null || applyCropButton.IsDisposed) return;
            canvas.CropCommandsEnabled = CanChangeCrop;
            bool active = IsCropContext;
            lastCropContextVisible = active;
            applyCropButton.Visible = cancelCropButton.Visible = cropSummary.Visible = active;
            applyCropButton.Enabled = active && CanChangeCrop && canvas.CanApplyCrop;
            cancelCropButton.Enabled = active && CanChangeCrop;
            if (active)
            {
                Rectangle crop = canvas.CropSelection;
                cropSummary.Text = crop.Width.ToString("N0", CultureInfo.CurrentCulture) + " × " +
                    crop.Height.ToString("N0", CultureInfo.CurrentCulture) + " px  •  Drag an edge or corner. Enter applies; Esc cancels.";
            }
            else cropSummary.Text = "";
        }

        private int LayoutCropActions(int availableWidth)
        {
            UpdateCropActionControls();
            if (!IsCropContext) return 0;
            int x = Ui(12), rowY = Ui(3), rowHeight = Ui(34);
            int maximumWidth = Math.Max(1, availableWidth - Ui(24));
            PlaceToolbarControl(applyCropButton, Math.Min(ToolbarWidth(applyCropButton, 128), maximumWidth),
                ref x, ref rowY, rowHeight, availableWidth, Ui(6));
            PlaceToolbarControl(cancelCropButton, Math.Min(ToolbarWidth(cancelCropButton, 82), maximumWidth),
                ref x, ref rowY, rowHeight, availableWidth, Ui(10));
            if (x + ToolbarWidth(cropSummary, 0) > availableWidth - Ui(12))
            {
                x = Ui(12);
                rowY += rowHeight + Ui(3);
            }
            int messageWidth = Math.Max(1, availableWidth - x - Ui(12));
            int messageHeight = WrappedTextHeight(cropSummary, messageWidth, Ui(24));
            cropSummary.SetBounds(x, rowY, messageWidth, Math.Max(rowHeight, messageHeight));
            return rowY + Math.Max(rowHeight, messageHeight) + Ui(4);
        }

        private void ApplyPendingCrop()
        {
            if (!IsCropContext || !CanChangeCrop) return;
            try
            {
                bool changed = canvas.ApplyCrop();
                // A captured drag can temporarily refuse application. Keep its
                // preview and instructions instead of reporting a completed crop.
                if (canvas.CropModeActive) { UpdateCropActionControls(); return; }
                SetStatus(changed ? "Crop applied. Undo restores the previous snip." : "Crop unchanged.");
                UpdateCropActionControls();
                canvas.Focus();
            }
            catch (Exception error) { ShowFailure("The crop could not be applied", error); }
        }

        private void CancelPendingCrop()
        {
            if (!IsCropContext || !CanChangeCrop) return;
            canvas.CancelCrop();
            SetStatus("Crop canceled. Your snip is unchanged.");
            UpdateCropActionControls();
            canvas.Focus();
        }

        private bool HandleCropCommand(Keys keyData)
        {
            if (!IsCropContext || (keyData != Keys.Enter && keyData != Keys.Escape)) return false;
            if (CanChangeCrop)
            {
                if (keyData == Keys.Enter) ApplyPendingCrop();
                else CancelPendingCrop();
            }
            return true;
        }
    }
}
