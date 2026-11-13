using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private enum AnnotationOptionsKind { None, Pen, Highlight, Shapes, Text }
        private Panel toolOptionsParking;
        private ToolOptionsPopup toolOptionsPopup;
        private AnnotationOptionsKind toolOptionsKind;
        private Label optionsColorLabel, optionsSizeLabel;
        private bool arrangingToolOptions, openingToolOptions;
        private int queuedToolPopup;
        private Rectangle toolOptionsAnchor;

        private bool HasInlineTextWorkflow { get { return canvas.HasImage && !canvas.TextActionsMode && canvas.InlineTextEditing; } }

        private void InitializeToolOptionsIntegration()
        {
            toolOptionsParking = new Panel { Visible = false, Size = Size.Empty };
            chromeRoot.Controls.Add(toolOptionsParking);
            List<Control> parked = new List<Control> { colorButton, width, textSize, advancedTextButton,
                shapeFillButton, shapeOutlineButton, shapeSummary, emojiButton, snapToText };
            foreach (StudioButton button in shapeChoiceButtons.Values) parked.Add(button);
            foreach (Control control in parked)
            {
                annotationControls.Remove(control);
                toolOptionsParking.Controls.Add(control);
                control.Visible = false;
            }
            ConfigureOptionsCombo(width); ConfigureOptionsCombo(textSize);
        }

        private void ConfigureOptionsCombo(ComboBox combo)
        {
            combo.DropDown += delegate { if (toolOptionsPopup != null && toolOptionsPopup.Visible) toolOptionsPopup.AutoClose = false; };
            combo.DropDownClosed += delegate { if (toolOptionsPopup != null && !toolOptionsPopup.IsDisposed) toolOptionsPopup.AutoClose = true; };
        }

        private static bool ToolHasOptions(EditorTool tool)
        {
            return tool == EditorTool.Pen || tool == EditorTool.Highlighter || tool == EditorTool.Text || IsShapeToolKind(tool) || tool == EditorTool.Emoji;
        }

        private void ConfigureToolOptionsButton(StudioButton button, EditorTool tool)
        {
            if (!ToolHasOptions(tool)) return;
            button.DropDownIndicator = true;
            button.DropDownClick += delegate { ActivateAnnotationTool(tool, button, true); };
        }

        private void ActivateAnnotationTool(EditorTool tool, StudioButton button, bool optionsRequested)
        {
            bool alreadySelected = !canvas.TextActionsMode && (canvas.Tool == tool || (tool == EditorTool.Text && IsInlineTextContext) ||
                ((IsShapeToolKind(tool) || tool == EditorTool.Emoji) && IsShapeContext));
            if (!alreadySelected)
            {
                SelectTool(tool);
                if (canvas.Tool != tool) return;
            }
            if (ToolHasOptions(tool) && (optionsRequested || alreadySelected)) ShowToolOptions(button);
        }

        private AnnotationOptionsKind CurrentToolOptionsKind
        {
            get
            {
                if (!canvas.HasImage || canvas.TextActionsMode) return AnnotationOptionsKind.None;
                if (IsShapeContext) return AnnotationOptionsKind.Shapes;
                if (IsInlineTextContext) return AnnotationOptionsKind.Text;
                if (canvas.Tool == EditorTool.Pen) return AnnotationOptionsKind.Pen;
                if (canvas.Tool == EditorTool.Highlighter) return AnnotationOptionsKind.Highlight;
                return AnnotationOptionsKind.None;
            }
        }

        private void ShowToolOptions(Control anchor)
        {
            if (!CanChangeAnnotationAppearance || CurrentToolOptionsKind == AnnotationOptionsKind.None || toolOptionsParking == null) return;
            QueueToolOptionsAction(anchor, delegate(Rectangle bounds) { OpenToolOptions(bounds); });
        }

        private void OpenToolOptions(Rectangle anchorBounds)
        {
            if (!CanChangeAnnotationAppearance || CurrentToolOptionsKind == AnnotationOptionsKind.None || toolOptionsParking == null) return;
            ToolOptionsPopup popup = PrepareToolOptions(CurrentToolOptionsKind, anchorBounds);
            Point location = MenuPopupPlacement.Calculate(anchorBounds, popup.Size, EditorWorkingArea());
            popup.Show(this, PointToClient(location), ToolStripDropDownDirection.BelowRight);
        }

        // This layout-only seam also lets the signed component checks construct
        // the real options card without showing a window or dispatching input.
        private ToolOptionsPopup PrepareToolOptions(AnnotationOptionsKind kind, Rectangle anchorBounds)
        {
            CloseShapePopups();
            if (toolOptionsPopup != null) toolOptionsPopup.Dispose();
            toolOptionsKind = kind;
            toolOptionsAnchor = anchorBounds;
            string title = kind == AnnotationOptionsKind.Shapes ? "Shape options" : kind == AnnotationOptionsKind.Text ? "Text options" : kind == AnnotationOptionsKind.Highlight ? "Highlight options" : "Pen options";
            toolOptionsPopup = new ToolOptionsPopup(title, Ui(1000) / 1000f);
            optionsColorLabel = NewToolOptionsLabel(kind == AnnotationOptionsKind.Text ? "Text color" : "Color");
            optionsSizeLabel = NewToolOptionsLabel(kind == AnnotationOptionsKind.Text ? "Font size" : "Stroke width");
            toolOptionsPopup.Content.Controls.Add(optionsColorLabel);
            toolOptionsPopup.Content.Controls.Add(optionsSizeLabel);
            if (kind == AnnotationOptionsKind.Shapes)
            {
                foreach (EditorTool shape in new EditorTool[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow }) toolOptionsPopup.AttachControl(shapeChoiceButtons[shape]);
                toolOptionsPopup.AttachControl(emojiButton); toolOptionsPopup.AttachControl(shapeFillButton);
                toolOptionsPopup.AttachControl(shapeOutlineButton); toolOptionsPopup.AttachControl(shapeSummary);
            }
            else
            {
                toolOptionsPopup.AttachControl(colorButton);
                toolOptionsPopup.AttachControl(kind == AnnotationOptionsKind.Text ? (Control)textSize : width);
                if (kind == AnnotationOptionsKind.Text) toolOptionsPopup.AttachControl(advancedTextButton);
                if (kind == AnnotationOptionsKind.Highlight)
                {
                    toolOptionsPopup.AttachControl(snapToText);
                    toolOptionsPopup.AttachControl(recognitionStatus);
                }
            }
            openingToolOptions = true;
            try
            {
                UpdateAnnotationContextControls(); RefreshToolOptionsContents();
                toolOptionsPopup.FitToWorkingArea(EditorWorkingArea());
            }
            finally { openingToolOptions = false; }
            return toolOptionsPopup;
        }

        private Label NewToolOptionsLabel(string text)
        {
            return new Label { Text = text, ForeColor = StudioTheme.Text, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        }

        private bool IsHostedInToolOptions(Control control)
        {
            return toolOptionsPopup != null && !toolOptionsPopup.IsDisposed && control.Parent == toolOptionsPopup.Content;
        }

        private void RefreshToolOptionsContents()
        {
            if (arrangingToolOptions || toolOptionsPopup == null || toolOptionsPopup.IsDisposed || toolOptionsPopup.Content == null || (!openingToolOptions && !toolOptionsPopup.Visible)) return;
            if (CurrentToolOptionsKind != toolOptionsKind) { CloseToolOptions(); return; }
            arrangingToolOptions = true;
            try
            {
                int bodyWidth = Ui(344), margin = Ui(8), rowHeight = Ui(34), y = Ui(6);
                bool shapes = toolOptionsKind == AnnotationOptionsKind.Shapes;
                optionsColorLabel.Visible = optionsSizeLabel.Visible = !shapes;
                if (shapes)
                {
                    int x = margin;
                    foreach (EditorTool kind in new EditorTool[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow })
                    {
                        shapeChoiceButtons[kind].SetBounds(x, y, Ui(42), rowHeight); x += Ui(46);
                    }
                    emojiButton.SetBounds(x, y, Ui(42), rowHeight); y += Ui(42);
                    if (!IsEmojiContext)
                    {
                        shapeFillButton.SetBounds(margin, y, bodyWidth - margin * 2, rowHeight); y += Ui(40);
                        shapeOutlineButton.SetBounds(margin, y, bodyWidth - margin * 2, rowHeight); y += Ui(42);
                    }
                    int summaryHeight = WrappedTextHeight(shapeSummary, bodyWidth - margin * 2, Ui(38));
                    shapeSummary.SetBounds(margin, y, bodyWidth - margin * 2, summaryHeight); y += summaryHeight + Ui(6);
                }
                else
                {
                    optionsColorLabel.SetBounds(margin, y, Ui(130), rowHeight);
                    colorButton.SetBounds(bodyWidth - Ui(58), y, Ui(48), rowHeight); colorButton.Visible = true; y += Ui(42);
                    optionsSizeLabel.SetBounds(margin, y, Ui(130), rowHeight);
                    Control size = toolOptionsKind == AnnotationOptionsKind.Text ? (Control)textSize : width;
                    size.SetBounds(bodyWidth - Ui(142), y + Ui(2), Ui(132), Math.Max(Ui(30), size.PreferredSize.Height)); size.Visible = true; y += Ui(44);
                    if (toolOptionsKind == AnnotationOptionsKind.Text)
                    {
                        advancedTextButton.SetBounds(margin, y, bodyWidth - margin * 2, rowHeight);
                        advancedTextButton.Visible = true; y += Ui(42);
                    }
                    if (toolOptionsKind == AnnotationOptionsKind.Highlight)
                    {
                        snapToText.SetBounds(margin, y, bodyWidth - margin * 2, Ui(28)); snapToText.Visible = true; y += Ui(34);
                        recognitionStatus.AutoEllipsis = false;
                        int messageHeight = WrappedTextHeight(recognitionStatus, bodyWidth - margin * 2, Ui(38));
                        recognitionStatus.SetBounds(margin, y, bodyWidth - margin * 2, messageHeight);
                        recognitionStatus.Visible = true; y += messageHeight + Ui(6);
                    }
                }
                toolOptionsPopup.SetContentSize(new Size(bodyWidth, y));
                toolOptionsPopup.FitToWorkingArea(EditorWorkingArea());
                if (toolOptionsPopup.Visible)
                {
                    Point location = MenuPopupPlacement.Calculate(toolOptionsAnchor, toolOptionsPopup.Size, EditorWorkingArea());
                    if (toolOptionsPopup.Location != location) toolOptionsPopup.Location = location;
                }
            }
            finally { arrangingToolOptions = false; }
        }

        private bool CloseToolOptions()
        {
            if (toolOptionsPopup == null || toolOptionsPopup.IsDisposed || !toolOptionsPopup.Visible) return false;
            toolOptionsPopup.Close(ToolStripDropDownCloseReason.CloseCalled); return true;
        }

        private void DisposeToolOptions()
        {
            queuedToolPopup++;
            if (toolOptionsPopup != null) { toolOptionsPopup.Dispose(); toolOptionsPopup = null; }
        }

        private void QueueToolOptionsAction(Control anchor, Action<Rectangle> open)
        {
            if (IsDisposed || closing || !IsHandleCreated) return;
            Rectangle screenAnchor = anchor.RectangleToScreen(anchor.ClientRectangle);
            int request = ++queuedToolPopup;
            CloseShapePopups();
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || closing || request != queuedToolPopup || !CanChangeAnnotationAppearance) return;
                open(screenAnchor);
            });
        }

        private Color[] RecentToolColors()
        {
            List<Color> colors = new List<Color>();
            if (preferences.RecentInkColors != null) foreach (int argb in preferences.RecentInkColors) colors.Add(Color.FromArgb(argb));
            return colors.ToArray();
        }

        private void RememberToolColor(Color color)
        {
            if (color.IsEmpty) return;
            color = Color.FromArgb(255, color.R, color.G, color.B);
            List<Color> colors = new List<Color>(RecentToolColors());
            colors.RemoveAll(delegate(Color old) { return old.R == color.R && old.G == color.G && old.B == color.B; });
            colors.Insert(0, color); if (colors.Count > 8) colors.RemoveRange(8, colors.Count - 8);
            preferences.RecentInkColors = colors.ConvertAll<int>(delegate(Color current) { return Color.FromArgb(255, current.R, current.G, current.B).ToArgb(); }).ToArray();
        }
    }
}
