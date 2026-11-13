using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private StudioButton eraseAllAnnotationsButton;
        private Label eraseAllAnnotationsGuidance;

        // These controls belong to this popup rather than the parked formatting
        // controls, so closing/disposal cannot leave an old event binding alive.
        private void BuildEraserOptions(ToolOptionsPopup popup)
        {
            eraseAllAnnotationsButton = ActionButton("Erase all annotations", "eraser", 300, delegate { EraseAllAnnotationsFromOptions(); });
            eraseAllAnnotationsButton.Name = "eraseAllAnnotationsButton";
            eraseAllAnnotationsButton.AccessibleName = "Erase all annotations";
            eraseAllAnnotationsButton.AccessibleDescription = "Remove every annotation in one step. Undo or Ctrl+Z restores all annotations.";
            eraseAllAnnotationsGuidance = new Label
            {
                Name = "eraseAllAnnotationsGuidance", AutoSize = false, AutoEllipsis = false,
                ForeColor = StudioTheme.Muted, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Erase all annotations instructions",
                Text = "Remove every annotation in one step. Image pixels stay unchanged. Undo (Ctrl+Z) restores all annotations together."
            };
            popup.Content.Controls.Add(eraseAllAnnotationsButton);
            popup.Content.Controls.Add(eraseAllAnnotationsGuidance);
        }

        private int LayoutEraserOptions(int bodyWidth, int y)
        {
            if (eraseAllAnnotationsButton == null || eraseAllAnnotationsButton.IsDisposed ||
                eraseAllAnnotationsGuidance == null || eraseAllAnnotationsGuidance.IsDisposed) return y;
            int margin = Ui(8), availableWidth = Math.Max(1, bodyWidth - margin * 2);
            eraseAllAnnotationsButton.Enabled = CanChangeAnnotationAppearance && canvas.HasImage && canvas.AnnotationCount > 0;
            eraseAllAnnotationsButton.SetBounds(margin, y, availableWidth, Ui(36));
            eraseAllAnnotationsButton.Visible = true;
            y += Ui(44);
            int height = WrappedTextHeight(eraseAllAnnotationsGuidance, availableWidth, Ui(52));
            eraseAllAnnotationsGuidance.SetBounds(margin, y, availableWidth, height);
            eraseAllAnnotationsGuidance.Visible = true;
            return y + height + Ui(6);
        }

        private void EraseAllAnnotationsFromOptions()
        {
            if (!CanChangeAnnotationAppearance || !canvas.HasImage) return;
            try
            {
                if (!canvas.CommitInlineText())
                {
                    SetStatus(canvas.InlineTextStatus);
                    return;
                }
                int count = canvas.AnnotationCount;
                if (canvas.EraseAllAnnotations())
                    SetStatus("Erased " + count + (count == 1 ? " annotation" : " annotations") + ". Undo (Ctrl+Z) restores them together.");
                else SetStatus("No annotations to erase.");
                RefreshToolOptionsContents();
                canvas.Focus();
            }
            catch (Exception error) { ShowFailure("The annotations could not be erased", error); }
        }
    }
}
