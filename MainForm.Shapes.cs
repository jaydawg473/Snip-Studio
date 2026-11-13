using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private StudioButton shapesButton, emojiButton, advancedTextButton, finishInlineTextButton, cancelInlineTextButton;
        private readonly Dictionary<EditorTool, StudioButton> shapeChoiceButtons = new Dictionary<EditorTool, StudioButton>();
        private ShapeStyleButton shapeFillButton, shapeOutlineButton;
        private Label shapeSummary, inlineTextGuidance;
        private EditorTool lastShapeTool = EditorTool.Rectangle;
        private bool lastShapeContextVisible, lastInlineContextVisible, lastEmojiContextVisible, lastInlineWorkflowVisible;
        private ShapeStylePopup shapeStylePopup;
        private EmojiPickerPopup emojiPickerPopup;

        private static bool IsShapeToolKind(EditorTool kind)
        {
            return kind == EditorTool.Rectangle || kind == EditorTool.Ellipse || kind == EditorTool.Line || kind == EditorTool.Arrow;
        }

        private bool IsShapeContext
        {
            get { return canvas.HasImage && !canvas.TextActionsMode &&
                (IsShapeToolKind(canvas.Tool) || IsEmojiContext || (canvas.Tool == EditorTool.Select && canvas.HasSelectedShape)); }
        }

        private bool IsEmojiContext
        {
            get { return canvas.HasImage && !canvas.TextActionsMode &&
                (canvas.Tool == EditorTool.Emoji || (canvas.Tool == EditorTool.Select && canvas.HasSelectedEmoji)); }
        }

        private bool IsInlineTextContext
        {
            get { return canvas.HasImage && !canvas.TextActionsMode && !IsShapeContext &&
                (canvas.Tool == EditorTool.Text || canvas.HasSelectedTextAnnotation || canvas.InlineTextEditing); }
        }

        private bool CanChangeAnnotationAppearance
        {
            get { return !IsDisposed && !closing && !capturePending && !recordingSession && !scrollingCaptureActive; }
        }

        private void BuildShapesButton()
        {
            shapesButton = IconButton("Shapes", "shapes", delegate { ActivateAnnotationTool(lastShapeTool, shapesButton, false); });
            shapesButton.DropDownIndicator = true;
            shapesButton.DropDownClick += delegate { ActivateAnnotationTool(lastShapeTool, shapesButton, true); };
            shapesButton.AccessibleName = "Shapes";
            annotationToolbar.Controls.Add(shapesButton);
            annotationControls.Add(shapesButton);
            documentControls.Add(shapesButton);
            tips.SetToolTip(shapesButton, "Shapes: rectangle, ellipse, line, arrow and colored emoji stickers. Click again or use the arrow for Shape options (Alt+Down).");
        }

        private void BuildShapeActions()
        {
            AddShapeChoice("Rectangle", EditorTool.Rectangle, "box");
            AddShapeChoice("Ellipse", EditorTool.Ellipse, "ellipse");
            AddShapeChoice("Line", EditorTool.Line, "line");
            AddShapeChoice("Arrow", EditorTool.Arrow, "arrow");
            emojiButton = IconButton("Emoji", "emoji", delegate { ShowEmojiPicker(); });
            emojiButton.AccessibleName = "Choose an emoji";
            contextToolbar.Controls.Add(emojiButton);
            tips.SetToolTip(emojiButton, "Choose a colored emoji sticker, then click the snip to place it. Drag a corner to resize; drag the top handle to rotate.");
            shapeFillButton = new ShapeStyleButton { Text = "Fill", AccessibleName = "Shape fill color and opacity" };
            shapeOutlineButton = new ShapeStyleButton { Text = "Outline", AccessibleName = "Shape outline color, opacity and size", OutlineSample = true };
            shapeFillButton.Click += delegate { ShowShapeStylePicker(true); };
            shapeOutlineButton.Click += delegate { ShowShapeStylePicker(false); };
            contextToolbar.Controls.Add(shapeFillButton);
            contextToolbar.Controls.Add(shapeOutlineButton);
            shapeSummary = new Label { AutoSize = false, AutoEllipsis = false, ForeColor = StudioTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Shape instructions" };
            contextToolbar.Controls.Add(shapeSummary);

            advancedTextButton = ActionButton("Advanced text…", "text", 154, delegate
            {
                if (!CanChangeAnnotationAppearance || !canvas.CanShowAdvancedTextEditor) return;
                QueueToolOptionsAction(advancedTextButton, delegate(Rectangle anchor)
                {
                    if (canvas.CanShowAdvancedTextEditor) canvas.ShowAdvancedTextEditor();
                    UpdateAnnotationContextControls();
                });
            });
            finishInlineTextButton = ActionButton("Done", "", 70, delegate
            {
                if (!CanChangeAnnotationAppearance || !canvas.InlineTextEditing) return;
                if (canvas.CommitInlineText()) { SetStatus("Text entry finished. Use Move to reposition the note."); canvas.Focus(); }
                UpdateAnnotationContextControls();
            });
            cancelInlineTextButton = ActionButton("Cancel", "", 82, delegate
            {
                if (!CanChangeAnnotationAppearance) return;
                canvas.CancelInlineText();
                SetStatus("Text entry canceled. The snip is unchanged.");
                UpdateAnnotationContextControls(); canvas.Focus();
            });
            contextToolbar.Controls.Add(advancedTextButton);
            contextToolbar.Controls.Add(finishInlineTextButton);
            contextToolbar.Controls.Add(cancelInlineTextButton);
            inlineTextGuidance = new Label { AutoSize = false, AutoEllipsis = false, ForeColor = StudioTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Text entry instructions" };
            contextToolbar.Controls.Add(inlineTextGuidance);
            tips.SetToolTip(advancedTextButton, "Open the advanced text editor for fonts, alignment, formatting and exact text-box size. Your current text is retained.");
            tips.SetToolTip(finishInlineTextButton, "Finish typing directly on the snip (Ctrl+Enter). Enter inserts a new line.");
            tips.SetToolTip(cancelInlineTextButton, "Cancel the current inline text edit (Escape), retaining the previous note.");

            canvas.ShapeStyleChanged += delegate { UpdateAnnotationContextAfterSelection(); };
            canvas.InlineTextStateChanged += delegate { UpdateAnnotationContextAfterSelection(); UpdateDocumentTitle(); };
            canvas.TextStyleChanged += delegate { UpdateAnnotationContextAfterSelection(); };
            UpdateAnnotationContextControls();
            RefreshToolOptionsContents();
        }

        private void AddShapeChoice(string label, EditorTool kind, string glyph)
        {
            StudioButton button = IconButton(label, glyph, delegate { SelectTool(kind); });
            button.AccessibleName = label + " shape";
            shapeChoiceButtons.Add(kind, button);
            contextToolbar.Controls.Add(button);
            tips.SetToolTip(button, label + ": drag on the snip to draw, then use Move to reposition it.");
        }

        private void UpdateAnnotationContextAfterSelection()
        {
            if (IsDisposed || closing || shapeFillButton == null) return;
            bool beforeShape = lastShapeContextVisible, beforeText = lastInlineContextVisible, beforeEmoji = lastEmojiContextVisible, beforeWorkflow = lastInlineWorkflowVisible;
            UpdateAnnotationContextControls();
            RefreshToolOptionsContents();
            if (beforeShape != lastShapeContextVisible || beforeText != lastInlineContextVisible || beforeEmoji != lastEmojiContextVisible || beforeWorkflow != lastInlineWorkflowVisible) LayoutChrome();
        }

        private void UpdateAnnotationContextControls()
        {
            if (shapeFillButton == null || shapeFillButton.IsDisposed) return;
            bool shape = IsShapeContext, text = IsInlineTextContext, emoji = IsEmojiContext;
            lastShapeContextVisible = shape; lastInlineContextVisible = text; lastEmojiContextVisible = emoji;
            lastInlineWorkflowVisible = HasInlineTextWorkflow;
            bool ready = CanChangeAnnotationAppearance;
            EditorTool active = emoji ? EditorTool.Emoji : canvas.Tool == EditorTool.Select && canvas.SelectedShapeTool.HasValue
                ? canvas.SelectedShapeTool.Value : canvas.Tool;
            if (IsShapeToolKind(canvas.Tool) || canvas.Tool == EditorTool.Emoji) lastShapeTool = canvas.Tool;
            if (shapesButton != null) shapesButton.Selected = shape;
            foreach (KeyValuePair<EditorTool, StudioButton> pair in shapeChoiceButtons)
            {
                pair.Value.Visible = shape; pair.Value.Enabled = ready && shape;
                pair.Value.Selected = active == pair.Key;
            }
            emojiButton.Visible = shape; emojiButton.Enabled = shape && ready;
            emojiButton.Selected = emoji;
            shapeFillButton.Visible = shapeOutlineButton.Visible = shape && !emoji;
            shapeSummary.Visible = shape;
            if (emoji)
            {
                shapeFillButton.Enabled = shapeOutlineButton.Enabled = false;
                shapeSummary.Text = canvas.Tool == EditorTool.Emoji
                    ? canvas.ActiveEmojiName + ": click to place. Drag corners to resize; the top handle rotates. Emoji stays active."
                    : "Drag the sticker to move it. Drag a corner to resize; drag the top handle to rotate.";
            }
            else if (shape)
            {
                ShapeStyle style = canvas.CurrentShapeStyle;
                bool closed = active == EditorTool.Rectangle || active == EditorTool.Ellipse;
                shapeFillButton.Enabled = ready && closed;
                shapeOutlineButton.Enabled = ready;
                shapeFillButton.Text = style.FillEnabled ? "Fill · " + style.FillOpacity + "%" : "Fill · None";
                shapeOutlineButton.Text = style.OutlineEnabled ? "Outline · " + style.OutlineWidth.ToString("0.#", CultureInfo.CurrentCulture) + " px" : "Outline · None";
                shapeFillButton.SampleColor = style.EffectiveFillColor; shapeFillButton.NoColor = !style.FillEnabled;
                shapeOutlineButton.SampleColor = style.EffectiveOutlineColor; shapeOutlineButton.NoColor = !style.OutlineEnabled;
                shapeFillButton.Invalidate(); shapeOutlineButton.Invalidate();
                tips.SetToolTip(shapeFillButton, closed ? "Fill color and opacity. None leaves the inside transparent." : "Lines and arrows use Outline; fill is available for rectangles and ellipses.");
                tips.SetToolTip(shapeOutlineButton, "Outline color, opacity and size. Current opacity: " + style.OutlineOpacity + "%.");
                shapeSummary.Text = !closed && !style.OutlineEnabled ? "Choose an Outline color to draw a line or arrow." :
                    !style.OutlineEnabled && !style.FillEnabled ? "Choose a visible Fill or Outline before drawing." :
                    canvas.HasSelectedShape ? "Appearance changes apply to the selected shape. Undo restores its previous style." :
                    "Drag to draw. Use Move to select or reposition a shape.";
            }
            advancedTextButton.Visible = text;
            inlineTextGuidance.Visible = HasInlineTextWorkflow;
            advancedTextButton.Enabled = text && ready && canvas.CanShowAdvancedTextEditor;
            finishInlineTextButton.Visible = cancelInlineTextButton.Visible = text && canvas.InlineTextEditing;
            finishInlineTextButton.Enabled = cancelInlineTextButton.Enabled = text && ready && canvas.InlineTextEditing;
            if (text) inlineTextGuidance.Text = canvas.InlineTextEditing ? "Type inside the box. Enter adds a line; Ctrl+Enter finishes; Esc cancels." :
                "Drag a text box, then type. Click for a quick default box. Advanced text opens the full editor.";
        }

        private int LayoutShapeActions(int availableWidth)
        {
            UpdateAnnotationContextControls();
            int x = Ui(12), rowY = Ui(3), rowHeight = Ui(34), max = Math.Max(1, availableWidth - Ui(24));
            foreach (EditorTool kind in new EditorTool[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow })
                PlaceToolbarControl(shapeChoiceButtons[kind], Math.Min(Ui(34), max), ref x, ref rowY, rowHeight, availableWidth, Ui(3));
            PlaceToolbarControl(emojiButton, Math.Min(Ui(34), max), ref x, ref rowY, rowHeight, availableWidth, Ui(12));
            if (!IsEmojiContext)
            {
                PlaceToolbarControl(shapeFillButton, Math.Min(shapeFillButton.PreferredContentWidth(Ui(1000) / 1000f), max), ref x, ref rowY, rowHeight, availableWidth, Ui(6));
                PlaceToolbarControl(shapeOutlineButton, Math.Min(shapeOutlineButton.PreferredContentWidth(Ui(1000) / 1000f), max), ref x, ref rowY, rowHeight, availableWidth, Ui(8));
            }
            return LayoutAnnotationSummary(shapeSummary, availableWidth, x, rowY, rowHeight);
        }

        private int LayoutInlineTextActions(int availableWidth)
        {
            UpdateAnnotationContextControls();
            int x = Ui(12), rowY = Ui(3), rowHeight = Ui(34), max = Math.Max(1, availableWidth - Ui(24));
            if (canvas.InlineTextEditing)
            {
                PlaceToolbarControl(finishInlineTextButton, Math.Min(ToolbarWidth(finishInlineTextButton, 70), max), ref x, ref rowY, rowHeight, availableWidth, Ui(6));
                PlaceToolbarControl(cancelInlineTextButton, Math.Min(ToolbarWidth(cancelInlineTextButton, 82), max), ref x, ref rowY, rowHeight, availableWidth, Ui(10));
            }
            return LayoutAnnotationSummary(inlineTextGuidance, availableWidth, x, rowY, rowHeight);
        }

        private int LayoutAnnotationSummary(Label label, int availableWidth, int x, int rowY, int rowHeight)
        {
            if (x + ToolbarWidth(label, 0) > availableWidth - Ui(12)) { x = Ui(12); rowY += rowHeight + Ui(4); }
            int width = Math.Max(1, availableWidth - x - Ui(12));
            int height = WrappedTextHeight(label, width, Ui(24));
            label.SetBounds(x, rowY, width, Math.Max(rowHeight, height));
            return rowY + Math.Max(rowHeight, height) + Ui(5);
        }

        private void ShowShapeStylePicker(bool fill)
        {
            if (!IsShapeContext || IsEmojiContext || !CanChangeAnnotationAppearance) return;
            QueueToolOptionsAction(fill ? (Control)shapeFillButton : shapeOutlineButton,
                delegate(Rectangle anchor) { OpenShapeStylePicker(fill, anchor); });
        }

        private void OpenShapeStylePicker(bool fill, Rectangle anchor)
        {
            if (!IsShapeContext || IsEmojiContext || !CanChangeAnnotationAppearance) return;
            EditorTool kind = canvas.Tool == EditorTool.Select && canvas.SelectedShapeTool.HasValue ? canvas.SelectedShapeTool.Value : canvas.Tool;
            bool closed = kind == EditorTool.Rectangle || kind == EditorTool.Ellipse;
            if (fill && !closed) return;
            CloseShapePopups();
            if (shapeStylePopup != null) shapeStylePopup.Dispose();
            shapeStylePopup = new ShapeStylePopup(canvas.CurrentShapeStyle, fill, closed, delegate(ShapeStyle style)
            {
                if (!CanChangeAnnotationAppearance || !IsShapeContext || IsEmojiContext) return;
                try
                {
                    canvas.CurrentShapeStyle = style;
                    RememberToolColor(fill ? style.FillColor : style.OutlineColor);
                    SetStatus(canvas.HasSelectedShape ? "Shape appearance updated. Undo restores the previous appearance." : "Shape style ready. Drag on the snip to draw.");
                    UpdateAnnotationContextControls(); LayoutChrome();
                }
                catch (Exception error) { ShowFailure("The shape appearance could not be updated", error); }
            }, Ui(1000) / 1000f, this, RecentToolColors());
            ShowAnnotationPopup(shapeStylePopup, anchor);
        }

        private void ShowEmojiPicker()
        {
            if (!CanChangeAnnotationAppearance || !canvas.HasImage) return;
            QueueToolOptionsAction(emojiButton, delegate(Rectangle anchor) { OpenEmojiPicker(anchor); });
        }

        private void OpenEmojiPicker(Rectangle anchor)
        {
            if (!CanChangeAnnotationAppearance || !canvas.HasImage) return;
            CloseShapePopups();
            if (emojiPickerPopup != null) emojiPickerPopup.Dispose();
            emojiPickerPopup = new EmojiPickerPopup(delegate(string value)
            {
                if (!CanChangeAnnotationAppearance || !canvas.HasImage) return;
                canvas.BeginEmojiPlacement(value);
                if (canvas.Tool != EditorTool.Emoji) { UpdateAnnotationContextControls(); return; }
                SetStatus(canvas.ActiveEmojiName + ": click to place. Drag a corner to resize; use the top handle to rotate. Emoji stays active.");
                UpdateAnnotationContextControls(); LayoutChrome(); canvas.Focus();
            }, Ui(1000) / 1000f);
            ShowAnnotationPopup(emojiPickerPopup, anchor);
        }

        private void ShowAnnotationPopup(ToolStripDropDown popup, Control anchor)
        {
            ShowAnnotationPopup(popup, anchor.RectangleToScreen(anchor.ClientRectangle));
        }

        private void ShowAnnotationPopup(ToolStripDropDown popup, Rectangle bounds)
        {
            Rectangle work = EditorWorkingArea();
            ShapeStylePopup style = popup as ShapeStylePopup;
            EmojiPickerPopup emoji = popup as EmojiPickerPopup;
            if (style != null) style.FitToWorkingArea(work);
            if (emoji != null) emoji.FitToWorkingArea(work);
            Point location = MenuPopupPlacement.Calculate(bounds, popup.Size, work);
            popup.Show(this, PointToClient(location), ToolStripDropDownDirection.BelowRight);
        }

        internal bool CloseShapePopups()
        {
            bool closed = CloseToolOptions();
            if (shapeStylePopup != null && !shapeStylePopup.IsDisposed && shapeStylePopup.Visible) { shapeStylePopup.Close(ToolStripDropDownCloseReason.CloseCalled); closed = true; }
            if (emojiPickerPopup != null && !emojiPickerPopup.IsDisposed && emojiPickerPopup.Visible) { emojiPickerPopup.Close(ToolStripDropDownCloseReason.CloseCalled); closed = true; }
            return closed;
        }

        private void DisposeShapeActions()
        {
            DisposeToolOptions();
            if (shapeStylePopup != null) { shapeStylePopup.Dispose(); shapeStylePopup = null; }
            if (emojiPickerPopup != null) { emojiPickerPopup.Dispose(); emojiPickerPopup = null; }
        }

        private static bool IsInlineTextInputCommand(Keys keys)
        {
            Keys code = keys & Keys.KeyCode;
            Keys modifiers = keys & Keys.Modifiers;
            if ((modifiers & Keys.Alt) != 0) return false;
            if (code == Keys.Enter || code == Keys.Escape || code == Keys.Back || code == Keys.Delete ||
                code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down ||
                code == Keys.Home || code == Keys.End || code == Keys.PageUp || code == Keys.PageDown) return true;
            return (modifiers & Keys.Control) != 0 && (code == Keys.A || code == Keys.C || code == Keys.X || code == Keys.V || code == Keys.Z || code == Keys.Y);
        }

        private sealed class ShapeStyleButton : Button
        {
            internal Color SampleColor = Color.Red;
            internal bool NoColor, OutlineSample;
            private bool hovered;
            internal ShapeStyleButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
                BackColor = StudioTheme.Surface; ForeColor = StudioTheme.Text;
                MouseEnter += delegate { hovered = true; Invalidate(); };
                MouseLeave += delegate { hovered = false; Invalidate(); };
            }
            internal int PreferredContentWidth(float scale)
            {
                return TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (int)Math.Ceiling(62 * scale);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = Math.Max(.5f, Height / 34f);
                Color back = hovered && Enabled ? StudioTheme.Raised : StudioTheme.Surface;
                using (GraphicsPath round = StudioTheme.Round(new RectangleF(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), 6 * scale))
                using (Brush fill = new SolidBrush(back)) g.FillPath(fill, round);
                float side = 18 * scale, left = 10 * scale, top = (Height - side) / 2;
                RectangleF sample = new RectangleF(left, top, side, side);
                using (Brush white = new SolidBrush(Color.White)) g.FillEllipse(white, sample);
                Color ink = !Enabled ? StudioTheme.Disabled : NoColor ? StudioTheme.Text : SampleColor;
                using (Pen outline = new Pen(NoColor ? StudioTheme.Muted : ink, (OutlineSample && !NoColor ? 3 : 1.3f) * scale))
                {
                    if (!NoColor && !OutlineSample) using (Brush fill = new SolidBrush(ink)) g.FillEllipse(fill, sample);
                    g.DrawEllipse(outline, sample);
                    if (NoColor) g.DrawLine(outline, sample.Left + 2 * scale, sample.Bottom - 2 * scale, sample.Right - 2 * scale, sample.Top + 2 * scale);
                }
                Rectangle text = new Rectangle((int)(left + side + 7 * scale), 0, Math.Max(1, Width - (int)(left + side + 26 * scale)), Height);
                TextRenderer.DrawText(g, Text, Font, text, Enabled ? StudioTheme.Text : StudioTheme.Disabled, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                using (Pen arrow = new Pen(Enabled ? StudioTheme.Text : StudioTheme.Disabled, 1.2f * scale))
                {
                    float x = Width - 12 * scale, y = Height / 2f;
                    g.DrawLines(arrow, new PointF[] { new PointF(x - 3 * scale, y - 1.5f * scale), new PointF(x, y + 1.5f * scale), new PointF(x + 3 * scale, y - 1.5f * scale) });
                }
                if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(3, 3, Math.Max(1, Width - 6), Math.Max(1, Height - 6)));
            }
        }
    }
}
