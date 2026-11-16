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
        private bool toolOptionsOpen;
        private Rectangle toolOptionsDockBounds;
        private int queuedToolPopup;

        // Control.Visible is inherited from the form, so it cannot describe an
        // open options row while a layout-only check keeps the form unshown.
        private bool IsToolOptionsOpen { get { return toolOptionsOpen && toolOptionsPopup != null && !toolOptionsPopup.IsDisposed; } }

        private bool HasInlineTextWorkflow { get { return canvas.HasImage && !canvas.TextActionsMode && canvas.InlineTextEditing; } }

        private void InitializeToolOptionsIntegration()
        {
            toolOptionsParking = new Panel { Visible = false, Size = Size.Empty };
            chromeRoot.Controls.Add(toolOptionsParking);
            paletteDockHost = new Panel { Visible = false, AutoScroll = true, Margin = Padding.Empty,
                Padding = Padding.Empty, BackColor = StudioTheme.Surface, AccessibleName = "Expanded tool palette" };
            chromeRoot.Controls.Add(paletteDockHost);
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
                if (IsToolOptionsOpen) toolOptionsPopup.AutoClose = false;
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
            if (alreadySelected && IsToolOptionsOpen && toolOptionsKind == CurrentToolOptionsKind)
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
            PrepareToolOptions(CurrentToolOptionsKind, anchorBounds);
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
            toolOptionsPopup = new ToolOptionsPopup(title, Ui(1000) / 1000f) { CompactMode = true, ShowCompactCaption = false };
            toolOptionsPopup.CardClosed += delegate
            {
                queuedToolPopup++;
                toolOptionsOpen = false;
                ClearInlineAnnotationPalette();
                if (!openingToolOptions && !arrangingChrome && !closing && !IsDisposed) LayoutChrome();
            };
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
                // Actual-size samples remain in the size menu. The resting
                // options row has no preview card competing with the snip.
                if (kind == AnnotationOptionsKind.Highlight)
                {
                    toolOptionsPopup.AttachControl(snapToText);
                    toolOptionsPopup.AttachControl(recognitionStatus);
                }
            }
            openingToolOptions = true;
            try
            {
                chromeRoot.Controls.Add(toolOptionsPopup);
                toolOptionsOpen = true;
                toolOptionsPopup.Visible = true;
                UpdateAnnotationContextControls();
                LayoutChrome();
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
            if (arrangingToolOptions || !IsToolOptionsOpen) return;
            if (!CanChangeAnnotationAppearance || CurrentToolOptionsKind != toolOptionsKind) { CloseToolOptions(); return; }
            // LayoutChrome owns both the row and the viewport. Event handlers
            // raised during chrome layout leave the final measurement to it.
            if (!arrangingChrome) LayoutChrome();
        }

        private int LayoutDockedToolOptions(int availableWidth, int top)
        {
            toolOptionsDockBounds = Rectangle.Empty;
            if (!IsToolOptionsOpen) return 0;
            if (!CanChangeAnnotationAppearance || CurrentToolOptionsKind != toolOptionsKind) { CloseToolOptions(); return 0; }
            arrangingToolOptions = true;
            try
            {
                int bodyWidth = Math.Max(1, availableWidth - toolOptionsPopup.HeaderWidth - toolOptionsPopup.ReservedCloseWidth);
                int rowHeight = Math.Max(Ui(32), Math.Max(Math.Max(width.PreferredSize.Height, textSize.PreferredSize.Height),
                    Math.Max(width.Height, textSize.Height)));
                int x = 0, gap = Ui(6);
                bool shapes = toolOptionsKind == AnnotationOptionsKind.Shapes;
                bool eraser = toolOptionsKind == AnnotationOptionsKind.Eraser;
                width.BackColor = textSize.BackColor = ToolOptionsAppearance.Field;
                width.ForeColor = textSize.ForeColor = ToolOptionsAppearance.Ink;
                optionsColorLabel.Visible = optionsSizeLabel.Visible = optionsHint.Visible = false;
                shapeSummary.Visible = false;
                string context = shapes ? IsEmojiContext ? "Emoji" : canvas.Tool == EditorTool.Select && canvas.SelectedShapeTool.HasValue
                    ? canvas.SelectedShapeTool.Value.ToString() : canvas.Tool.ToString() : "";
                toolOptionsPopup.SetContext(shapes ? "SHAPE OPTIONS" : toolOptionsKind.ToString().ToUpperInvariant() + " OPTIONS", context);
                Point optionsScroll = new Point(Math.Max(0, -toolOptionsPopup.Content.AutoScrollPosition.X), 0);
                toolOptionsPopup.Content.AutoScrollPosition = Point.Empty;
                if (shapes)
                {
                    foreach (EditorTool kind in new EditorTool[] { EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow })
                        PlaceCompactOptionsControl(shapeChoiceButtons[kind], Ui(28), ref x, rowHeight, Ui(3));
                    PlaceCompactOptionsControl(emojiButton, Ui(28), ref x, rowHeight, gap);
                    if (!IsEmojiContext)
                    {
                        PlaceCompactOptionsControl(shapeFillButton, shapeFillButton.PreferredContentWidth(Ui(1000) / 1000f), ref x, rowHeight, gap);
                        PlaceCompactOptionsControl(shapeOutlineButton, shapeOutlineButton.PreferredContentWidth(Ui(1000) / 1000f), ref x, rowHeight, gap);
                    }
                    tips.SetToolTip(emojiButton, shapeSummary.Text);
                }
                else if (eraser)
                {
                    eraseAllAnnotationsGuidance.Visible = false;
                    eraseAllAnnotationsButton.Enabled = CanChangeAnnotationAppearance && canvas.AnnotationCount > 0;
                    PlaceCompactOptionsControl(eraseAllAnnotationsButton, ToolbarWidth(eraseAllAnnotationsButton, 198), ref x, rowHeight, gap);
                    tips.SetToolTip(eraseAllAnnotationsButton, eraseAllAnnotationsGuidance.Text);
                    optionsHint.Text = "Undo: Ctrl+Z";
                    optionsHint.Visible = true;
                    PlaceCompactOptionsControl(optionsHint, ToolbarWidth(optionsHint, 88), ref x, rowHeight, gap);
                }
                else
                {
                    PlaceCompactOptionsControl(colorButton, Ui(32), ref x, rowHeight, gap);
                    Control size = toolOptionsKind == AnnotationOptionsKind.Text ? (Control)textSize : width;
                    PlaceCompactOptionsControl(size, ToolbarWidth(size, toolOptionsKind == AnnotationOptionsKind.Text ? 104 : 106), ref x, rowHeight, gap);
                    if (toolOptionsKind == AnnotationOptionsKind.Text)
                        PlaceCompactOptionsControl(advancedTextButton, ToolbarWidth(advancedTextButton, 130), ref x, rowHeight, gap);
                    if (toolOptionsKind == AnnotationOptionsKind.Highlight)
                    {
                        PlaceCompactOptionsControl(snapToText, Math.Max(Ui(112), snapToText.GetPreferredSize(Size.Empty).Width), ref x, rowHeight, gap);
                        recognitionStatus.Visible = false;
                        tips.SetToolTip(snapToText, "Snap to recognized text height. " + recognitionStatus.Text);
                    }
                }
                int neededWidth = Math.Max(1, x - (x > 0 ? gap : 0));
                // On unusually narrow windows the same single row scrolls
                // horizontally; controls never wrap over the image.
                int scrollHeight = neededWidth > bodyWidth ? Math.Max(SystemInformation.HorizontalScrollBarHeight, Ui(17)) : 0;
                toolOptionsPopup.ScrollContentSize = new Size(neededWidth, rowHeight);
                toolOptionsPopup.SetContentSize(new Size(Math.Max(bodyWidth, neededWidth), rowHeight + scrollHeight));
                toolOptionsPopup.FitToWorkingArea(new Rectangle(0, 0, availableWidth, rowHeight + scrollHeight + Ui(8)));
                toolOptionsPopup.Content.AutoScrollPosition = optionsScroll;
                toolOptionsDockBounds = new Rectangle(0, top, availableWidth, toolOptionsPopup.Height);
                toolOptionsPopup.Location = toolOptionsDockBounds.Location;
                toolOptionsPopup.Visible = true;
                toolOptionsPopup.BringToFront();
                return toolOptionsPopup.Height;
            }
            finally { arrangingToolOptions = false; }
        }

        private void PlaceCompactOptionsControl(Control control, int span, ref int x, int rowHeight, int gap)
        {
            int height = control is ComboBox ? Math.Max(Ui(28), control.PreferredSize.Height) : rowHeight;
            // Native owner-drawn combos enforce their own height. Position
            // after SetBounds so a requested 28px field that becomes 32px does
            // not protrude below this 32px row and create a vertical scrollbar.
            control.Margin = Padding.Empty;
            control.SetBounds(x, 0, Math.Max(1, span), height);
            control.Top = Math.Max(0, (rowHeight - control.Height) / 2);
            control.Visible = true;
            x += span + gap;
        }

        // The row occupies a dedicated band in chromeRoot. Its bounds and the
        // workspace bounds share coordinates and can never intersect.
        private Rectangle AttachedToolOptionsArea() { return toolOptionsDockBounds; }

        private Point AttachedToolOptionsLocation(ToolOptionsPopup popup) { return toolOptionsDockBounds.Location; }

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
            string labelText = value.ToString("0", CultureInfo.CurrentCulture) + " px";
            if (DrawClosedSizeSelection(e, labelText, width.Font, width.Enabled)) return;
            int padding = (int)Math.Round(8 * scale), labelWidth = Math.Max((int)Math.Round(62 * scale),
                TextRenderer.MeasureText(labelText, width.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
            Rectangle label = new Rectangle(e.Bounds.Left + padding, e.Bounds.Top, labelWidth, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, labelText, width.Font, label,
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
            if (DrawClosedSizeSelection(e, choice.ToString(), textSize.Font, textSize.Enabled)) return;
            float scale = e.Graphics.DpiX / 96f;
            int padding = (int)Math.Round(8 * scale), labelWidth = Math.Max((int)Math.Round(62 * scale),
                TextRenderer.MeasureText(choice.ToString(), textSize.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
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

        private static bool DrawClosedSizeSelection(DrawItemEventArgs e, string label, Font font, bool enabled)
        {
            if ((e.State & DrawItemState.ComboBoxEdit) == 0) return false;
            int padding = Math.Max(1, (int)Math.Round(8 * e.Graphics.DpiX / 96f));
            TextRenderer.DrawText(e.Graphics, label, font, new Rectangle(e.Bounds.Left + padding, e.Bounds.Top,
                Math.Max(1, e.Bounds.Width - padding * 2), e.Bounds.Height), enabled ? ToolOptionsAppearance.Ink : StudioTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            return true;
        }

        private bool CloseToolOptions()
        {
            queuedToolPopup++;
            bool wasOpen = IsToolOptionsOpen;
            toolOptionsOpen = false;
            ClearInlineAnnotationPalette();
            if (toolOptionsPopup != null && !toolOptionsPopup.IsDisposed)
                toolOptionsPopup.Close(ToolStripDropDownCloseReason.CloseCalled);
            else if (wasOpen && !arrangingChrome && !closing && !IsDisposed) LayoutChrome();
            return wasOpen;
        }

        private void DisposeToolOptions()
        {
            queuedToolPopup++;
            toolOptionsOpen = false;
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
