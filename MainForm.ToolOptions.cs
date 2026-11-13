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
        private enum AnnotationOptionsKind { None, Pen, Highlight, Shapes, Text, Eraser }
        private Panel toolOptionsParking;
        private ToolOptionsPopup toolOptionsPopup;
        private AnnotationOptionsKind toolOptionsKind;
        private Label optionsColorLabel, optionsSizeLabel;
        private Label optionsHint;
        private ToolSizePreview optionsSizePreview;
        private bool arrangingToolOptions, openingToolOptions;
        private int queuedToolPopup;

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
            ((StudioComboBox)width).ItemPreviewRenderer = DrawStrokeSizeOption;
            textSize.ItemPreviewRenderer = DrawTextSizeOption;
            width.SelectedIndexChanged += delegate { UpdateToolSizePreview(); };
            textSize.SelectedIndexChanged += delegate { UpdateToolSizePreview(); };
            canvas.TextStyleChanged += delegate { UpdateToolSizePreview(); };
            colorButton.Invalidated += delegate { UpdateToolSizePreview(); };
            chromeRoot.SizeChanged += delegate { RefreshToolOptionsContents(); };
            annotationToolbar.LocationChanged += delegate { RefreshToolOptionsContents(); };
            annotationToolbar.SizeChanged += delegate { RefreshToolOptionsContents(); };
            contextToolbar.SizeChanged += delegate { RefreshToolOptionsContents(); };
            contextToolbar.LocationChanged += delegate { RefreshToolOptionsContents(); };
            contextToolbar.VisibleChanged += delegate { RefreshToolOptionsContents(); };
            footerStrip.LocationChanged += delegate { RefreshToolOptionsContents(); };
            footerStrip.SizeChanged += delegate { RefreshToolOptionsContents(); };
        }

        private void ConfigureOptionsCombo(ComboBox combo)
        {
            combo.DropDown += delegate
            {
                if (toolOptionsPopup != null && toolOptionsPopup.Visible) toolOptionsPopup.AutoClose = false;
                int sampleWidth = Ui(combo == textSize ? 204 : 164);
                combo.DropDownWidth = Math.Max(combo.DropDownWidth, Math.Min(sampleWidth, Math.Max(1, EditorWorkingArea().Width - Ui(16))));
            };
            combo.DropDownClosed += delegate
            {
                if (toolOptionsPopup != null && !toolOptionsPopup.IsDisposed) toolOptionsPopup.AutoClose = true;
                UpdateToolSizePreview();
            };
        }

        private static bool ToolHasOptions(EditorTool tool)
        {
            return tool == EditorTool.Pen || tool == EditorTool.Highlighter || tool == EditorTool.Text || IsShapeToolKind(tool) || tool == EditorTool.Emoji || tool == EditorTool.Eraser;
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
            if (alreadySelected && toolOptionsPopup != null && !toolOptionsPopup.IsDisposed && toolOptionsPopup.Visible && toolOptionsKind == CurrentToolOptionsKind)
            {
                queuedToolPopup++; CloseToolOptions(); return;
            }
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
                if (canvas.Tool == EditorTool.Eraser) return AnnotationOptionsKind.Eraser;
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
            popup.Show(this, AttachedToolOptionsLocation(popup), ToolStripDropDownDirection.BelowRight);
        }

        // This layout-only seam also lets the signed component checks construct
        // the real options card without showing a window or dispatching input.
        private ToolOptionsPopup PrepareToolOptions(AnnotationOptionsKind kind, Rectangle anchorBounds)
        {
            CloseShapePopups();
            if (toolOptionsPopup != null) toolOptionsPopup.Dispose();
            optionsSizePreview = null;
            toolOptionsKind = kind;
            string title = kind == AnnotationOptionsKind.Shapes ? "Shape options" : kind == AnnotationOptionsKind.Text ? "Text options" : kind == AnnotationOptionsKind.Highlight ? "Highlight options" : kind == AnnotationOptionsKind.Eraser ? "Eraser options" : "Pen options";
            toolOptionsPopup = new ToolOptionsPopup(title, Ui(1000) / 1000f);
            toolOptionsPopup.CardClosed += delegate { ClearInlineAnnotationPalette(); };
            optionsColorLabel = NewToolOptionsLabel(kind == AnnotationOptionsKind.Text ? "Text color" : "Color");
            optionsSizeLabel = NewToolOptionsLabel(kind == AnnotationOptionsKind.Text ? "Font size" : "Stroke width");
            toolOptionsPopup.Content.Controls.Add(optionsColorLabel);
            toolOptionsPopup.Content.Controls.Add(optionsSizeLabel);
            optionsHint = NewToolOptionsLabel("");
            optionsHint.ForeColor = ToolOptionsAppearance.Muted;
            optionsHint.AutoEllipsis = false;
            toolOptionsPopup.Content.Controls.Add(optionsHint);
            if (kind == AnnotationOptionsKind.Shapes)
            {
                foreach (EditorTool shape in new EditorTool[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow }) toolOptionsPopup.AttachControl(shapeChoiceButtons[shape]);
                toolOptionsPopup.AttachControl(emojiButton); toolOptionsPopup.AttachControl(shapeFillButton);
                toolOptionsPopup.AttachControl(shapeOutlineButton); toolOptionsPopup.AttachControl(shapeSummary);
            }
            else if (kind == AnnotationOptionsKind.Eraser) BuildEraserOptions(toolOptionsPopup);
            else
            {
                toolOptionsPopup.AttachControl(colorButton);
                toolOptionsPopup.AttachControl(kind == AnnotationOptionsKind.Text ? (Control)textSize : width);
                if (kind == AnnotationOptionsKind.Text) toolOptionsPopup.AttachControl(advancedTextButton);
                optionsSizePreview = new ToolSizePreview { Font = toolOptionsPopup.Content.Font };
                toolOptionsPopup.Content.Controls.Add(optionsSizePreview);
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
                toolOptionsPopup.FitToWorkingArea(AttachedToolOptionsArea());
            }
            finally { openingToolOptions = false; }
            return toolOptionsPopup;
        }

        private Label NewToolOptionsLabel(string text)
        {
            return new Label { Text = text, ForeColor = ToolOptionsAppearance.Muted, BackColor = Color.Transparent,
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
                Rectangle attachedArea = AttachedToolOptionsArea();
                // Leave room for the card inset and its vertical scrollbar, so
                // a shorter editor window does not introduce horizontal scroll.
                int bodyWidth = Math.Max(1, Math.Min(Ui(590), attachedArea.Width - Ui(40) - Math.Max(SystemInformation.VerticalScrollBarWidth, Ui(17))));
                int gap = Ui(10), rowHeight = Ui(36), labelHeight = Ui(22), y = Ui(4);
                bool shapes = toolOptionsKind == AnnotationOptionsKind.Shapes;
                bool eraser = toolOptionsKind == AnnotationOptionsKind.Eraser;
                width.BackColor = textSize.BackColor = ToolOptionsAppearance.Field;
                width.ForeColor = textSize.ForeColor = ToolOptionsAppearance.Ink;
                optionsColorLabel.Visible = optionsSizeLabel.Visible = !shapes && !eraser;
                optionsHint.Visible = !shapes && !eraser;
                string context = shapes ? IsEmojiContext ? "Emoji" : canvas.Tool == EditorTool.Select && canvas.SelectedShapeTool.HasValue
                    ? canvas.SelectedShapeTool.Value.ToString() : canvas.Tool.ToString() : toolOptionsKind == AnnotationOptionsKind.Text ? "Formatting" : "";
                toolOptionsPopup.SetContext(shapes ? "SHAPE OPTIONS" : toolOptionsKind == AnnotationOptionsKind.Highlight ? "HIGHLIGHT OPTIONS" : toolOptionsKind.ToString().ToUpperInvariant() + " OPTIONS", context);
                if (shapes)
                {
                    int x = 0;
                    foreach (EditorTool kind in new EditorTool[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow })
                    {
                        if (x > 0 && x + rowHeight > bodyWidth) { x = 0; y += rowHeight + Ui(3); }
                        shapeChoiceButtons[kind].SetBounds(x, y, rowHeight, rowHeight); x += Ui(39);
                    }
                    if (x > 0 && x + rowHeight > bodyWidth) { x = 0; y += rowHeight + Ui(3); }
                    emojiButton.SetBounds(x, y, rowHeight, rowHeight); x += rowHeight + gap;
                    if (!IsEmojiContext)
                    {
                        int fillWidth = Math.Max(Ui(132), shapeFillButton.PreferredContentWidth(Ui(1000) / 1000f));
                        int outlineWidth = Math.Max(Ui(162), shapeOutlineButton.PreferredContentWidth(Ui(1000) / 1000f));
                        if (x + fillWidth + outlineWidth + gap > bodyWidth) { y += rowHeight + gap; x = 0; }
                        shapeFillButton.SetBounds(x, y, Math.Min(fillWidth, bodyWidth), rowHeight); x += fillWidth + gap;
                        if (x + outlineWidth > bodyWidth) { y += rowHeight + gap; x = 0; }
                        shapeOutlineButton.SetBounds(x, y, Math.Min(outlineWidth, bodyWidth), rowHeight);
                    }
                    y += rowHeight + Ui(10);
                    int summaryHeight = WrappedTextHeight(shapeSummary, bodyWidth, Ui(28));
                    shapeSummary.ForeColor = ToolOptionsAppearance.Muted;
                    shapeSummary.SetBounds(0, y, bodyWidth, summaryHeight); y += summaryHeight;
                }
                else if (eraser) y = LayoutEraserOptions(bodyWidth, y);
                else
                {
                    int colorWidth = Math.Min(Ui(76), bodyWidth), sizeWidth = Ui(164), sizeX = colorWidth + gap;
                    optionsColorLabel.SetBounds(0, y, colorWidth, labelHeight);
                    int sizeLabelY = y;
                    y += labelHeight;
                    colorButton.SetBounds(0, y, colorWidth, rowHeight); colorButton.Visible = true;
                    int fieldY = y, rowBottom = y + rowHeight;
                    if (bodyWidth - sizeX < Ui(112)) { sizeX = 0; sizeLabelY = rowBottom + gap; fieldY = sizeLabelY + labelHeight; }
                    Control size = toolOptionsKind == AnnotationOptionsKind.Text ? (Control)textSize : width;
                    sizeWidth = Math.Max(1, Math.Min(sizeWidth, bodyWidth - sizeX));
                    optionsSizeLabel.SetBounds(sizeX, sizeLabelY, sizeWidth, labelHeight);
                    size.SetBounds(sizeX, fieldY + Ui(2), sizeWidth, Math.Max(Ui(30), size.PreferredSize.Height)); size.Visible = true;
                    rowBottom = Math.Max(rowBottom, fieldY + rowHeight);
                    if (toolOptionsKind == AnnotationOptionsKind.Text)
                    {
                        int advancedWidth = Ui(164), advancedX = sizeX + sizeWidth + gap, advancedY = fieldY;
                        if (advancedX + advancedWidth > bodyWidth) { advancedY = rowBottom + gap; advancedX = 0; }
                        advancedTextButton.SetBounds(advancedX, advancedY, Math.Min(advancedWidth, bodyWidth), rowHeight);
                        advancedTextButton.Visible = true;
                        rowBottom = Math.Max(rowBottom, advancedY + rowHeight);
                    }
                    y = rowBottom + Ui(12);
                    optionsSizePreview.SetBounds(0, y, bodyWidth, Ui(toolOptionsKind == AnnotationOptionsKind.Text ? 118 : 104));
                    optionsSizePreview.Visible = true;
                    UpdateToolSizePreview();
                    y += optionsSizePreview.Height + Ui(8);
                    optionsHint.Text = toolOptionsKind == AnnotationOptionsKind.Text
                        ? "Size is in image pixels. Advanced text offers fonts, bold, italic and alignment."
                        : toolOptionsKind == AnnotationOptionsKind.Highlight ? "The sample shows the freehand stroke. Snap to text follows the recognized text height."
                        : "Preview uses image pixels; the image zoom stays unchanged.";
                    int hintHeight = WrappedTextHeight(optionsHint, bodyWidth, Ui(24));
                    optionsHint.SetBounds(0, y, bodyWidth, hintHeight); y += hintHeight;
                    if (toolOptionsKind == AnnotationOptionsKind.Highlight)
                    {
                        snapToText.SetBounds(0, y, bodyWidth, Ui(28)); snapToText.Visible = true; y += Ui(34);
                        recognitionStatus.AutoEllipsis = false;
                        int messageHeight = WrappedTextHeight(recognitionStatus, bodyWidth, Ui(38));
                        recognitionStatus.SetBounds(0, y, bodyWidth, messageHeight);
                        recognitionStatus.Visible = true; y += messageHeight + Ui(6);
                    }
                }
                y = LayoutInlineAnnotationPalette(bodyWidth, y);
                toolOptionsPopup.SetContentSize(new Size(Math.Max(bodyWidth, InlineAnnotationPaletteMinimumWidth), y));
                toolOptionsPopup.FitToWorkingArea(attachedArea);
                if (toolOptionsPopup.Visible)
                {
                    Point location = AttachedToolOptionsLocation(toolOptionsPopup);
                    if (toolOptionsPopup.Location != location) toolOptionsPopup.Location = location;
                    toolOptionsPopup.BringToFront();
                }
            }
            finally { arrangingToolOptions = false; }
        }

        // The card overlays the image; opening it never resizes the workspace,
        // changes the editor zoom, or owns a separate native popup window.
        private Rectangle AttachedToolOptionsArea()
        {
            int margin = Ui(10), gap = Ui(4);
            int left = margin, top = margin, right = Math.Max(margin + 1, ClientSize.Width - margin), bottom = Math.Max(margin + 1, ClientSize.Height - margin);
            Rectangle shelf = BoundsInMainForm(annotationToolbar);
            Rectangle capture = BoundsInMainForm(captureToolbar);
            Rectangle footer = BoundsInMainForm(footerStrip);
            if (footerStrip.Visible && footer.Top > top) bottom = Math.Max(top + 1, Math.Min(bottom, footer.Top - gap));
            if (AnnotationToolbarIsVertical)
            {
                top = Math.Min(bottom - 1, Math.Max(top, capture.Bottom + gap));
                if (String.Equals(preferences.AnnotationToolbarPosition, "Left", StringComparison.OrdinalIgnoreCase))
                    left = Math.Max(left, Math.Min(right - 1, shelf.Right + gap));
                else right = Math.Max(left + 1, Math.Min(right, shelf.Left - gap));
            }
            else if (String.Equals(preferences.AnnotationToolbarPosition, "Bottom", StringComparison.OrdinalIgnoreCase))
            {
                bottom = Math.Max(top + 1, Math.Min(bottom, shelf.Top - gap));
                top = Math.Min(bottom - 1, Math.Max(top, capture.Bottom + gap));
            }
            else top = Math.Min(bottom - 1, Math.Max(top, shelf.Bottom + gap));
            if (contextToolbar.Visible) top = Math.Min(bottom - 1, Math.Max(top, BoundsInMainForm(contextToolbar).Bottom + gap));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private Point AttachedToolOptionsLocation(ToolOptionsPopup popup)
        {
            Rectangle area = AttachedToolOptionsArea();
            int x = area.Left, y = area.Top;
            if (String.Equals(preferences.AnnotationToolbarPosition, "Right", StringComparison.OrdinalIgnoreCase)) x = area.Right - popup.Width;
            if (String.Equals(preferences.AnnotationToolbarPosition, "Bottom", StringComparison.OrdinalIgnoreCase)) y = area.Bottom - popup.Height;
            return new Point(Math.Max(0, Math.Min(x, Math.Max(0, ClientSize.Width - popup.Width))),
                Math.Max(0, Math.Min(y, Math.Max(0, ClientSize.Height - popup.Height))));
        }

        private Rectangle BoundsInMainForm(Control control)
        {
            if (control == null) return Rectangle.Empty;
            Point point = control.Location;
            for (Control parent = control.Parent; parent != null && parent != this; parent = parent.Parent) point.Offset(parent.Location);
            return new Rectangle(point, control.Size);
        }

        private void UpdateToolSizePreview() { PreviewToolSize(null, null); }

        private void PreviewToolSize(float? textOverride, float? strokeOverride)
        {
            if (optionsSizePreview == null || optionsSizePreview.IsDisposed || toolOptionsPopup == null || toolOptionsPopup.IsDisposed) return;
            if (toolOptionsKind == AnnotationOptionsKind.Text)
            {
                string family; FontStyle style; Color color;
                canvas.ReadTextPreviewStyle(out family, out style, out color);
                optionsSizePreview.UpdateText(textOverride.HasValue ? textOverride.Value : canvas.TextFontSize, family, style, color);
            }
            else if (toolOptionsKind == AnnotationOptionsKind.Pen || toolOptionsKind == AnnotationOptionsKind.Highlight)
                optionsSizePreview.UpdateStroke(strokeOverride.HasValue ? strokeOverride.Value : canvas.StrokeWidth, canvas.InkColor, toolOptionsKind == AnnotationOptionsKind.Highlight);
        }

        private void DrawStrokeSizeOption(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= width.Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawSizeOptionBackground(e, selected);
            float scale = e.Graphics.DpiX / 96f;
            float value = new int[] { 2, 4, 8, 12, 20 }[e.Index];
            int padding = (int)Math.Round(8 * scale), labelWidth = (int)Math.Round(62 * scale);
            Rectangle label = new Rectangle(e.Bounds.Left + padding, e.Bounds.Top, labelWidth, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, value.ToString("0", CultureInfo.CurrentCulture) + " px", width.Font, label,
                width.Enabled ? ToolOptionsAppearance.Ink : StudioTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            float sampleLeft = label.Right + padding, sampleRight = e.Bounds.Right - padding - value / 2;
            if (sampleRight > sampleLeft)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(width.Enabled ? canvas.InkColor : StudioTheme.Muted, value))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    e.Graphics.DrawLine(pen, sampleLeft + value / 2, e.Bounds.Top + e.Bounds.Height / 2f, sampleRight, e.Bounds.Top + e.Bounds.Height / 2f);
                }
            }
            if (width.DroppedDown && selected && (e.State & DrawItemState.ComboBoxEdit) == 0) PreviewToolSize(null, value);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        private void DrawTextSizeOption(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= textSize.Items.Count) return;
            TextSizeItem choice = textSize.Items[e.Index] as TextSizeItem;
            if (choice == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            DrawSizeOptionBackground(e, selected);
            float scale = e.Graphics.DpiX / 96f;
            int padding = (int)Math.Round(8 * scale), labelWidth = (int)Math.Round(62 * scale);
            Rectangle label = new Rectangle(e.Bounds.Left + padding, e.Bounds.Top, labelWidth, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, choice.ToString(), textSize.Font, label,
                textSize.Enabled ? ToolOptionsAppearance.Ink : StudioTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            string family; FontStyle style; Color color; canvas.ReadTextPreviewStyle(out family, out style, out color);
            int percentWidth = (int)Math.Round(38 * scale);
            Rectangle sample = new Rectangle(label.Right + padding, e.Bounds.Top + 2, Math.Max(1, e.Bounds.Right - label.Right - padding * 2 - percentWidth), Math.Max(1, e.Bounds.Height - 4));
            using (Font actual = ToolSizePreview.SampleFont(family, choice.Value, style))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                SizeF natural = e.Graphics.MeasureString("Aa", actual, Int32.MaxValue, format);
                float fit = ToolSizePreview.CalculateFitScale(natural, sample.Size);
                GraphicsState state = e.Graphics.Save(); e.Graphics.SetClip(sample);
                e.Graphics.TranslateTransform(sample.X, sample.Y + (sample.Height - natural.Height * fit) / 2); e.Graphics.ScaleTransform(fit, fit);
                using (Brush brush = new SolidBrush(textSize.Enabled ? color : StudioTheme.Muted)) e.Graphics.DrawString("Aa", actual, brush, 0, 0, format);
                e.Graphics.Restore(state);
                TextRenderer.DrawText(e.Graphics, (fit * 100).ToString("0", CultureInfo.CurrentCulture) + "%", textSize.Font,
                    new Rectangle(sample.Right, e.Bounds.Top, percentWidth, e.Bounds.Height), ToolOptionsAppearance.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            if (textSize.DroppedDown && selected && (e.State & DrawItemState.ComboBoxEdit) == 0) PreviewToolSize(choice.Value, null);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        private static void DrawSizeOptionBackground(DrawItemEventArgs e, bool selected)
        {
            using (Brush fill = new SolidBrush(selected ? StudioTheme.Selection : ToolOptionsAppearance.Field)) e.Graphics.FillRectangle(fill, e.Bounds);
        }

        private bool CloseToolOptions()
        {
            if (toolOptionsPopup == null || toolOptionsPopup.IsDisposed) return false;
            ClearInlineAnnotationPalette();
            if (!toolOptionsPopup.Visible) return false;
            toolOptionsPopup.Close(ToolStripDropDownCloseReason.CloseCalled); return true;
        }

        private void DisposeToolOptions()
        {
            queuedToolPopup++;
            ClearInlineAnnotationPalette();
            if (toolOptionsPopup != null) { toolOptionsPopup.Dispose(); toolOptionsPopup = null; }
        }

        private void QueueToolOptionsAction(Control anchor, Action<Rectangle> open)
        {
            if (IsDisposed || closing || !IsHandleCreated) return;
            Rectangle screenAnchor = anchor.RectangleToScreen(anchor.ClientRectangle);
            int request = ++queuedToolPopup;
            CloseNestedAnnotationPopups();
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
