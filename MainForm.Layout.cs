using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private Panel chromeRoot, captureToolbar, annotationToolbar, contextToolbar, workspace, footerStrip;
        private FlowLayoutPanel recordingOptions;
        private StudioButton screenshotModeButton, videoModeButton;
        private StudioButton delayOptionsButton, audioOptionsButton;
        private Label recordingModeCaption;
        private readonly List<Control> captureActions = new List<Control>();
        private readonly List<Control> primaryCaptureControls = new List<Control>();
        private readonly List<Control> annotationControls = new List<Control>();
        private readonly Dictionary<StudioButton, bool> annotationHorizontalIconOnly = new Dictionary<StudioButton, bool>();
        private StudioButton zoomOutButton, fitButton, zoomInButton;
        private StudioButton textActionsButton, copyAllTextButton, copySelectedTextButton, closeTextActionsButton;
        private StudioButton editRecognizedLineButton, deleteRecognizedTextButton;
        private StudioButton translateTextButton;
        private readonly StudioComboBox textSize = new StudioComboBox();
        private bool updatingTextSize;
        private bool recordingModeSelected, arrangingChrome;
        private bool lastTextCommandsReady;
        private Size lastViewLayoutClientSize;
        private object lastViewLayoutImageIdentity;
        private bool audioOptionsOpen;
        private ContextMenuStrip delayPopup;
        internal float LayoutScaleOverride = 0f;
        internal Rectangle LayoutWorkingAreaOverride = Rectangle.Empty;
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

        private sealed class TextSizeItem
        {
            internal readonly float Value;
            internal TextSizeItem(float value) { Value = value; }
            public override string ToString() { return Value.ToString("0.##", CultureInfo.InvariantCulture) + " px"; }
        }

        private int Ui(int value)
        {
            float scale = LayoutScaleOverride > 0 ? LayoutScaleOverride : 1;
            if (LayoutScaleOverride <= 0 && IsHandleCreated)
            {
                try { uint dpi = GetDpiForWindow(Handle); if (dpi > 0) scale = dpi / 96f; }
                catch (EntryPointNotFoundException) { }
            }
            return (int)Math.Round(value * scale);
        }

        private int CompactChromeHeight
        {
            get { return captureToolbar == null ? Ui(82) : captureToolbar.Height +
                updateNoticeHeight +
                (canvas.HasImage && !AnnotationToolbarIsVertical ? annotationToolbar.Height : 0) +
                (canvas.HasImage && (canvas.TextActionsMode || canvas.Tool == EditorTool.Highlighter || IsCropContext) ? contextToolbar.Height : 0) + footerStrip.Height; }
        }

        private bool AnnotationToolbarIsVertical
        {
            get { return String.Equals(preferences.AnnotationToolbarPosition, "Left", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(preferences.AnnotationToolbarPosition, "Right", StringComparison.OrdinalIgnoreCase); }
        }

        private void BuildLayout()
        {
            SuspendLayout();
            chromeRoot = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = StudioTheme.Background };
            Controls.Add(chromeRoot);
            captureToolbar = new Panel { BackColor = StudioTheme.Surface };
            annotationToolbar = new Panel { BackColor = StudioTheme.Surface };
            contextToolbar = new Panel { BackColor = StudioTheme.Surface };
            workspace = new Panel { BackColor = StudioTheme.Background };
            footerStrip = new Panel { BackColor = StudioTheme.Surface };
            chromeRoot.Controls.Add(captureToolbar);
            chromeRoot.Controls.Add(annotationToolbar);
            chromeRoot.Controls.Add(contextToolbar);
            chromeRoot.Controls.Add(workspace);
            chromeRoot.Controls.Add(footerStrip);
            BuildUpdateIndicator();
            editorToolShelf = annotationToolbar;
            editorContextBar = contextToolbar;

            StudioButton capture = ActionButton("New", "new", 96, delegate { if (recordingModeSelected) StartRecording(); else StartCapture(); });
            capture.Primary = true;
            capture.Font = new Font(Font, FontStyle.Bold);
            newButton = capture;
            captureToolbar.Controls.Add(capture);
            primaryCaptureControls.Add(capture);
            screenshotModeButton = IconButton("Screenshot mode", "camera", delegate { SetCaptureKind(false); });
            videoModeButton = IconButton("Record mode", "video", delegate { SetCaptureKind(true); });
            screenshotModeButton.Selected = true;
            captureToolbar.Controls.Add(screenshotModeButton);
            captureToolbar.Controls.Add(videoModeButton);
            primaryCaptureControls.Add(screenshotModeButton);
            primaryCaptureControls.Add(videoModeButton);
            ConfigureCombo(mode, 140, new object[] { "Rectangle", "Freeform", "Window", "Full screen", "Scrolling", "All monitors" });
            ConfigureCombo(delay, 108, new object[] { "No delay", "3 seconds", "5 seconds", "10 seconds" });
            captureToolbar.Controls.Add(mode);
            captureToolbar.Controls.Add(delay);
            delayOptionsButton = IconButton("Capture delay", "clock", delegate { ShowDelayOptions(); });
            delayOptionsButton.IconOnly = false;
            captureToolbar.Controls.Add(delayOptionsButton);
            delay.SelectedIndexChanged += delegate
            {
                UpdateDelayIndicator();
                if (!layoutTestMode && delay.SelectedIndex >= 0) sessionCaptureDelay = new int[] { 0, 3, 5, 10 }[delay.SelectedIndex];
            };
            audioOptionsButton = IconButton("Audio options", "microphone", delegate { ShowAudioOptions(); });
            captureToolbar.Controls.Add(audioOptionsButton);
            recordingModeCaption = new Label { Text = "Select an area to record", ForeColor = StudioTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            captureToolbar.Controls.Add(recordingModeCaption);
            tips.SetToolTip(mode, "Full screen lets you click one monitor. All monitors captures the entire connected desktop. Rectangle and Freeform let you select an area. For Scrolling, select only the scrolling content; leave out sidebars and fixed message boxes. Pause streaming content when possible.");
            tips.SetToolTip(delay, "Capture delay");
            tips.SetToolTip(capture, "New capture (Ctrl+N)");

            AddCaptureAction("Open image", "open", delegate { ChooseImage(); }, false);
            AddCaptureAction("Paste image", "paste", delegate { PasteImage(); }, false);
            AddCaptureAction("Save image", "save", delegate { SaveImage(false); }, true);
            AddCaptureAction("Copy image", "copy", delegate { CopyImage(); }, true);
            AddCaptureAction("Share image", "share", delegate { ShareCurrentImage(); }, true);
            Control more = BuildSettingsButton();
            captureToolbar.Controls.Add(more);
            captureActions.Add(more);

            AddCompactTool("Move", EditorTool.Select, "move", "Select and move annotations. Middle-drag or use scrollbars to pan the image.");
            AddCompactTool("Pen", EditorTool.Pen, "pen", "Pen");
            AddCompactTool("Highlight", EditorTool.Highlighter, "highlighter", "Highlight text; hold Shift for freehand");
            AddCompactTool("Arrow", EditorTool.Arrow, "arrow", "Draw an arrow");
            AddCompactTool("Box", EditorTool.Rectangle, "box", "Draw a box");
            AddCompactTool("Add text", EditorTool.Text, "text", "Add text, then use Move to reposition it");
            AddCompactTool("Eraser", EditorTool.Eraser, "eraser", "Erase annotations");
            AddCompactTool("Crop", EditorTool.Crop, "crop", "Drag an edge or corner to crop. Enter applies; Escape cancels.");
            AddCompactTool("Remove area", EditorTool.RemoveArea, "remove-area", "Remove captured content. Click a flat-color object or drag an area, review the selection, then press Delete. Undo restores it.");
            StudioButton color = IconButton("Ink color", "", delegate { ChooseColor(); });
            color.Swatch = true;
            colorButton = color;
            annotationToolbar.Controls.Add(color);
            annotationControls.Add(color);
            documentControls.Add(color);
            ConfigureCombo(width, 94, new object[] { "2 px", "4 px", "8 px", "12 px", "20 px" });
            width.SelectedIndexChanged += delegate { canvas.StrokeWidth = new int[] { 2, 4, 8, 12, 20 }[Math.Max(0, width.SelectedIndex)]; };
            annotationToolbar.Controls.Add(width);
            annotationControls.Add(width);
            documentControls.Add(width);
            tips.SetToolTip(width, "Stroke width");
            ConfigureCombo(textSize, 106, new object[0]);
            foreach (int size in new int[] { 8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 96, 128, 160 })
                textSize.Items.Add(new TextSizeItem(size));
            textSize.AccessibleName = "Text size";
            textSize.SelectedIndexChanged += delegate
            {
                if (updatingTextSize || capturePending || !canvas.HasImage) return;
                TextSizeItem choice = textSize.SelectedItem as TextSizeItem;
                if (choice != null) canvas.TextFontSize = choice.Value;
            };
            annotationToolbar.Controls.Add(textSize);
            annotationControls.Add(textSize);
            documentControls.Add(textSize);
            tips.SetToolTip(textSize, "Text size in image pixels. Applies to the selected text annotation or new text.");
            undoButton = AddEditorAction("Undo", "undo", delegate { canvas.Undo(); });
            redoButton = AddEditorAction("Redo", "redo", delegate { canvas.Redo(); });
            textActionsButton = (StudioButton)AddEditorAction("Text actions", "text-scan", delegate { CopyText(); });
            externalEditorButton = ActionButton("Edit in app", "pen", 132, delegate { EditInApplication(); });
            annotationToolbar.Controls.Add(externalEditorButton);
            annotationControls.Add(externalEditorButton);
            documentControls.Add(externalEditorButton);
            foreach (Control control in annotationControls)
            {
                StudioButton button = control as StudioButton;
                if (button != null) annotationHorizontalIconOnly.Add(button, button.IconOnly);
            }

            recordingOptions = BuildRecordingBar();
            recordingOptions.AutoSize = false;
            recordingOptions.Dock = DockStyle.None;
            recordingOptions.Padding = new Padding(12, 8, 12, 4);
            recordingOptions.Margin = Padding.Empty;
            recordingOptions.BackColor = StudioTheme.Surface;
            chromeRoot.Controls.Add(recordingOptions);

            snapToText.Text = "Snap to text";
            snapToText.AutoSize = true;
            snapToText.Checked = true;
            snapToText.ForeColor = StudioTheme.Text;
            snapToText.FlatStyle = FlatStyle.Flat;
            snapToText.CheckedChanged += delegate { canvas.SnapHighlightToText = snapToText.Checked; UpdateRecognitionStatus(); };
            canvas.SnapHighlightToText = true;
            contextToolbar.Controls.Add(snapToText);
            documentControls.Add(snapToText);
            recognitionStatus.AutoSize = false;
            recognitionStatus.AutoEllipsis = true;
            recognitionStatus.ForeColor = StudioTheme.Muted;
            contextToolbar.Controls.Add(recognitionStatus);
            copyAllTextButton = ActionButton("Copy all text", "copy", 126, delegate { CopyRecognizedText(true); });
            copySelectedTextButton = ActionButton("Copy selection", "copy", 140, delegate { CopyRecognizedText(false); });
            translateTextButton = ActionButton("Translate", "text", 116, delegate { ShowTranslation(); });
            editRecognizedLineButton = ActionButton("Edit line", "pen", 112, delegate { canvas.EditRecognizedLine(); UpdateTextActionControls(); });
            deleteRecognizedTextButton = ActionButton("Delete selection", "eraser", 160, delegate { canvas.DeleteRecognizedText(); UpdateTextActionControls(); });
            closeTextActionsButton = ActionButton("Done", "", 70, delegate { canvas.TextActionsMode = false; UpdateTextActionControls(); LayoutChrome(); });
            contextToolbar.Controls.Add(copyAllTextButton);
            contextToolbar.Controls.Add(copySelectedTextButton);
            contextToolbar.Controls.Add(translateTextButton);
            contextToolbar.Controls.Add(editRecognizedLineButton);
            contextToolbar.Controls.Add(deleteRecognizedTextButton);
            contextToolbar.Controls.Add(closeTextActionsButton);
            BuildCropActions();
            tips.SetToolTip(editRecognizedLineButton, "Edit one recognized line. Select words from a single line first.");
            tips.SetToolTip(translateTextButton, "Translate selected words, or all recognized text, using the included offline translator. Choose the original and target languages inside Snip Studio.");
            tips.SetToolTip(deleteRecognizedTextButton, "Remove the selected recognized text. Use Undo to restore it.");
            canvas.TextSelectionChanged += delegate { UpdateTextActionControls(); };
            canvas.SourceRemovalSelectionChanged += delegate
            {
                if (canvas.Tool == EditorTool.RemoveArea || canvas.Tool == EditorTool.Select)
                {
                    SetStatus(IsSourceRemovalContext ? canvas.SourceRemovalStatus : "Move: select an annotation and drag it. Middle-drag pans the image.");
                    UpdateColorButton();
                    UpdateContextHelp();
                }
            };

            canvas.Dock = DockStyle.Fill;
            canvas.AccessibleName = "Screenshot preview and annotation canvas";
            workspace.Padding = new Padding(10);
            workspace.Controls.Add(canvas);
            status.AutoSize = false;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = StudioTheme.Muted;
            footerStrip.Controls.Add(status);
            dimensions.AutoSize = false;
            dimensions.TextAlign = ContentAlignment.MiddleRight;
            dimensions.ForeColor = StudioTheme.Muted;
            footerStrip.Controls.Add(dimensions);
            zoomOutButton = IconButton("Zoom out", "zoom-out", delegate { canvas.ZoomOut(); });
            fitButton = ActionButton("Fit", "", 38, delegate { canvas.FitToWindow(); });
            zoomInButton = IconButton("Zoom in", "zoom-in", delegate { canvas.ZoomIn(); });
            footerStrip.Controls.Add(zoomOutButton);
            footerStrip.Controls.Add(fitButton);
            footerStrip.Controls.Add(zoomInButton);
            zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
            zoomLabel.ForeColor = StudioTheme.Muted;
            footerStrip.Controls.Add(zoomLabel);
            canvas.Resize += delegate { UpdateZoomLabel(); };
            canvas.ViewChanged += delegate { UpdateZoomLabel(); };
            canvas.TextRecognitionStatusChanged += delegate { UpdateRecognitionStatus(); UpdateTextActionControls(); };
            canvas.TextStyleChanged += delegate { UpdateTextStyleControls(); };
            status.TextChanged += delegate { LayoutChrome(); };
            dimensions.TextChanged += delegate { LayoutChrome(); };
            recognitionStatus.TextChanged += delegate { LayoutChrome(); };
            externalEditorButton.TextChanged += delegate { LayoutChrome(); };
            chromeRoot.Layout += delegate { LayoutChrome(); };
            UpdateTextStyleControls();
            ResumeLayout(true);
            LayoutChrome();
        }

        private StudioButton IconButton(string label, string glyph, EventHandler action)
        {
            StudioButton button = ActionButton(label, glyph, 34, action);
            button.IconOnly = true;
            button.Height = 34;
            tips.SetToolTip(button, label);
            return button;
        }

        private void AddCaptureAction(string label, string glyph, EventHandler action, bool requiresImage)
        {
            StudioButton button = IconButton(label, glyph, action);
            captureToolbar.Controls.Add(button);
            captureActions.Add(button);
            if (requiresImage) documentControls.Add(button);
        }

        private void AddCompactTool(string label, EditorTool tool, string glyph, string description)
        {
            StudioButton button = IconButton(label, glyph, delegate { SelectTool(tool); });
            toolButtons[tool] = button;
            annotationToolbar.Controls.Add(button);
            annotationControls.Add(button);
            documentControls.Add(button);
            tips.SetToolTip(button, description);
        }

        private Button AddEditorAction(string label, string glyph, EventHandler action)
        {
            StudioButton button = IconButton(label, glyph, action);
            annotationToolbar.Controls.Add(button);
            annotationControls.Add(button);
            documentControls.Add(button);
            return button;
        }

        private void SetCaptureKind(bool record)
        {
            if (capturePending) return;
            recordingModeSelected = record;
            if (record && !layoutTestMode && !loadingAudioSources) LoadAudioSources();
            screenshotModeButton.Selected = !record;
            videoModeButton.Selected = record;
            newButton.Text = record ? "Record" : "New";
            newButton.AccessibleName = record ? "Start recording" : "New screenshot";
            ((StudioButton)newButton).Glyph = record ? "record" : "new";
            canvas.EmptyMessage = record ? "Choose Record to select an area and start recording." : "Choose New, open an image, or paste from the clipboard.";
            canvas.Invalidate();
            LayoutChrome();
            if (!canvas.HasImage && !layoutTestMode)
            {
                Size = new Size(Width, Ui(260));
            }
            SetStatus(record ? "Choose your audio sources, then Record to select an area." : "Ready to capture. Choose a mode, then New.");
        }

        private void LayoutChrome()
        {
            if (chromeRoot == null || arrangingChrome) return;
            arrangingChrome = true;
            try
            {
                // A tool/context change may raise MinimumSize and grow the form
                // during this layout. Compare the entry size so that growth
                // preserves this document's current view, while a real window
                // resize or a newly captured image can still fit normally.
                Size entryLayoutClientSize = ClientSize;
                object entryLayoutImageIdentity = canvas.ImageIdentity;
                bool preserveView = canvas.HasImage && lastViewLayoutClientSize == entryLayoutClientSize &&
                    Object.ReferenceEquals(lastViewLayoutImageIdentity, entryLayoutImageIdentity);
                int w = chromeRoot.ClientSize.Width, h = chromeRoot.ClientSize.Height;
                if (w <= 0 || h <= 0) return;
                float scale = Ui(1000) / 1000f;
                ((StudioComboBox)mode).UpdateMetrics(scale);
                ((StudioComboBox)delay).UpdateMetrics(scale);
                ((StudioComboBox)width).UpdateMetrics(scale);
                textSize.UpdateMetrics(scale);
                int x = Ui(12), rowY = Ui(10), rowHeight = Ui(34), rowGap = Ui(6);
                PlaceToolbarControl(newButton, ToolbarWidth(newButton, canvas.HasImage || recordingModeSelected ? 104 : 80), ref x, ref rowY, rowHeight, w, Ui(8));
                PlaceToolbarControl(screenshotModeButton, Ui(34), ref x, ref rowY, rowHeight, w, 0);
                PlaceToolbarControl(videoModeButton, Ui(34), ref x, ref rowY, rowHeight, w, Ui(10));
                bool compact = !canvas.HasImage;
                mode.Visible = !recordingModeSelected;
                delay.Visible = false;
                delayOptionsButton.Visible = !recordingModeSelected;
                audioOptionsButton.Visible = recordingModeSelected;
                if (!recordingModeSelected)
                {
                    PlaceToolbarControl(mode, ToolbarWidth(mode, 140), ref x, ref rowY, rowHeight, w, Ui(8));
                    PlaceToolbarControl(delayOptionsButton, ToolbarWidth(delayOptionsButton, 80), ref x, ref rowY, rowHeight, w, Ui(8));
                }
                else PlaceToolbarControl(audioOptionsButton, Ui(34), ref x, ref rowY, rowHeight, w, Ui(8));
                int actionsWidth = (compact ? 1 : captureActions.Count) * Ui(38) - Ui(4);
                int captionWidth = ToolbarWidth(recordingModeCaption, 150);
                recordingModeCaption.AutoEllipsis = false;
                recordingModeCaption.Visible = recordingModeSelected && x + captionWidth + actionsWidth + Ui(20) <= w;
                if (recordingModeCaption.Visible)
                    PlaceToolbarControl(recordingModeCaption, captionWidth, ref x, ref rowY, rowHeight, w, Ui(8));
                if (x + actionsWidth > w - Ui(12)) { rowY += rowHeight + rowGap; x = Ui(12); }
                x = Math.Max(x, w - Ui(12) - actionsWidth);
                for (int i = 0; i < captureActions.Count; i++)
                {
                    captureActions[i].Visible = !compact || i == captureActions.Count - 1;
                    if (captureActions[i].Visible) PlaceToolbarControl(captureActions[i], Ui(34), ref x, ref rowY, rowHeight, w, Ui(4));
                }
                int y = rowY + rowHeight + Ui(10);
                captureToolbar.SetBounds(0, 0, w, y);
                y += LayoutUpdateIndicator(y, w);
                annotationToolbar.Visible = canvas.HasImage;
                bool verticalAnnotation = AnnotationToolbarIsVertical;
                bool bottomAnnotation = String.Equals(preferences.AnnotationToolbarPosition, "Bottom", StringComparison.OrdinalIgnoreCase);
                foreach (KeyValuePair<StudioButton, bool> pair in annotationHorizontalIconOnly)
                {
                    bool iconOnly = verticalAnnotation || pair.Value;
                    if (pair.Key.IconOnly != iconOnly) { pair.Key.IconOnly = iconOnly; pair.Key.Invalidate(); }
                }
                externalEditorButton.AccessibleName = externalEditorButton.Text;
                bool showTextSize = !canvas.TextActionsMode && (canvas.Tool == EditorTool.Text || canvas.HasSelectedTextAnnotation);
                width.Visible = !showTextSize;
                textSize.Visible = showTextSize;
                int annotationHeight = 0;
                if (!verticalAnnotation)
                {
                    annotationToolbar.AutoScrollPosition = Point.Empty;
                    annotationToolbar.AutoScrollMinSize = Size.Empty;
                    annotationToolbar.AutoScroll = false;
                }
                if (canvas.HasImage && !verticalAnnotation)
                {
                    x = Ui(12); rowY = Ui(6);
                    foreach (Control control in annotationControls)
                    {
                        if ((control == width && showTextSize) || (control == textSize && !showTextSize)) continue;
                        int span = ToolbarWidth(control, control == width ? 94 : control == textSize ? 106 : control == externalEditorButton ? 132 : 34);
                        span = Math.Min(span, Math.Max(Ui(34), w - Ui(24)));
                        PlaceToolbarControl(control, span, ref x, ref rowY, rowHeight, w, Ui(control == colorButton || control == width || control == textSize ? 8 : 2));
                    }
                    annotationHeight = rowY + rowHeight + Ui(6);
                }
                if (!verticalAnnotation && !bottomAnnotation)
                {
                    annotationToolbar.SetBounds(0, y, w, annotationHeight);
                    y += annotationHeight;
                }
                if (!audioOptionsOpen)
                {
                    recordingOptions.Visible = false;
                    recordingOptions.SetBounds(0, y, w, 0);
                }
                bool textMode = canvas.HasImage && canvas.TextActionsMode;
                bool cropMode = IsCropContext;
                bool context = cropMode || textMode || (canvas.HasImage && canvas.Tool == EditorTool.Highlighter);
                contextToolbar.Visible = context;
                snapToText.Visible = context && !textMode && !cropMode;
                recognitionStatus.Visible = context && !cropMode;
                copyAllTextButton.Visible = copySelectedTextButton.Visible = textMode && canvas.TextActionCommandsReady;
                translateTextButton.Visible = textMode && canvas.TextActionCommandsReady;
                translateTextButton.Enabled = textMode && canvas.TextActionCommandsReady && !capturePending && !String.IsNullOrWhiteSpace(canvas.AllRecognizedText);
                closeTextActionsButton.Visible = textMode;
                editRecognizedLineButton.Visible = deleteRecognizedTextButton.Visible = textMode && canvas.TextActionCommandsReady;
                editRecognizedLineButton.Enabled = textMode && !capturePending && canvas.CanEditRecognizedLine;
                deleteRecognizedTextButton.Enabled = textMode && !capturePending && canvas.CanDeleteRecognizedText;
                int contextHeight = 0;
                UpdateCropActionControls();
                if (cropMode) contextHeight = LayoutCropActions(w);
                else if (context)
                {
                    x = Ui(12); rowY = Ui(3);
                    int contextRowHeight = textMode ? rowHeight : Ui(24);
                    if (textMode)
                    {
                        if (canvas.TextActionCommandsReady)
                        {
                            PlaceToolbarControl(copyAllTextButton, ToolbarWidth(copyAllTextButton, 126), ref x, ref rowY, contextRowHeight, w, Ui(6));
                            PlaceToolbarControl(copySelectedTextButton, ToolbarWidth(copySelectedTextButton, 140), ref x, ref rowY, contextRowHeight, w, Ui(6));
                            PlaceToolbarControl(translateTextButton, ToolbarWidth(translateTextButton, 116), ref x, ref rowY, contextRowHeight, w, Ui(6));
                            PlaceToolbarControl(editRecognizedLineButton, ToolbarWidth(editRecognizedLineButton, 112), ref x, ref rowY, contextRowHeight, w, Ui(6));
                            PlaceToolbarControl(deleteRecognizedTextButton, ToolbarWidth(deleteRecognizedTextButton, 160), ref x, ref rowY, contextRowHeight, w, Ui(6));
                        }
                        PlaceToolbarControl(closeTextActionsButton, ToolbarWidth(closeTextActionsButton, 70), ref x, ref rowY, contextRowHeight, w, Ui(10));
                    }
                    else PlaceToolbarControl(snapToText, Math.Max(Ui(120), snapToText.GetPreferredSize(Size.Empty).Width), ref x, ref rowY, contextRowHeight, w, Ui(12));
                    recognitionStatus.AutoEllipsis = false;
                    int messageWidth = ToolbarWidth(recognitionStatus, 0);
                    if (x + messageWidth > w - Ui(12)) { x = Ui(12); rowY += contextRowHeight + Ui(3); }
                    int messageHeight = WrappedTextHeight(recognitionStatus, Math.Max(Ui(80), w - x - Ui(12)), Ui(24));
                    recognitionStatus.SetBounds(x, rowY, Math.Max(1, w - x - Ui(12)), Math.Max(contextRowHeight, messageHeight));
                    contextHeight = rowY + Math.Max(contextRowHeight, messageHeight) + Ui(4);
                }
                contextToolbar.SetBounds(0, y, w, contextHeight);
                y += contextHeight;
                int footer = Ui(28), zoomRowY = 0;
                // Reserve the full percentage slot so animated decimals neither
                // clip nor resize the workspace while the wheel is moving.
                int zoomLabelWidth = Ui(76);
                int dimensionWidth = ToolbarWidth(dimensions, 115);
                int footerActionsWidth = Ui(8 + 28 + 38 + 34) + zoomLabelWidth + dimensionWidth;
                int statusWidth = canvas.HasImage ? Math.Max(1, w - footerActionsWidth - Ui(24)) : Math.Max(1, w - Ui(24));
                status.AutoEllipsis = false;
                if (canvas.HasImage && ToolbarWidth(status, 0) > statusWidth)
                {
                    statusWidth = Math.Max(1, w - Ui(24));
                    zoomRowY = WrappedTextHeight(status, statusWidth, Ui(24));
                    footer += zoomRowY;
                }
                else if (!canvas.HasImage) footer = WrappedTextHeight(status, statusWidth, footer);
                int bottomAnnotationHeight = bottomAnnotation ? annotationHeight : 0;
                if (!canvas.HasImage && updateNoticeHeight > 0 && (!layoutTestMode || HasLayoutWorkingAreaOverride) &&
                    !IsFullScreenEditor && WindowState == FormWindowState.Normal)
                {
                    // Keep the compact prompt and its actions reachable when a
                    // high-DPI update notice needs a second row.
                    Rectangle work = EditorWorkingArea();
                    int requiredHeight = Math.Min(work.Height, y + footer + Ui(72) + Math.Max(0, Height - ClientSize.Height));
                    if (Height < requiredHeight) Height = requiredHeight;
                    int top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                    if (Top != top) Top = top;
                    h = ClientSize.Height;
                }
                if (canvas.HasImage && (!layoutTestMode || HasLayoutWorkingAreaOverride) && !IsFullScreenEditor && WindowState == FormWindowState.Normal)
                {
                    // A side toolbar must fit all its controls when the monitor
                    // has room, even when resize-to-snip is turned off.
                    Rectangle work = EditorWorkingArea();
                    int nonClientHeight = Math.Max(0, Height - ClientSize.Height);
                    int requiredViewport = canvas.HasImage && verticalAnnotation
                        ? Math.Max(Ui(96), MeasureVerticalAnnotationToolbar(showTextSize, false).Height) : Ui(96);
                    int minimumHeight = Math.Min(work.Height, y + footer + bottomAnnotationHeight + requiredViewport + nonClientHeight);
                    if (MinimumSize.Height != minimumHeight) MinimumSize = new Size(MinimumSize.Width, minimumHeight);
                    if (verticalAnnotation || cropMode)
                    {
                        if (Height > work.Height) Height = work.Height;
                        int top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                        if (Top != top) Top = top;
                    }
                    h = ClientSize.Height;
                }
                int viewportHeight = Math.Max(1, h - y - footer - bottomAnnotationHeight);
                footerStrip.SetBounds(0, Math.Max(y + bottomAnnotationHeight, h - footer), w, footer);
                if (bottomAnnotation)
                    annotationToolbar.SetBounds(0, y + viewportHeight, w, bottomAnnotationHeight);
                int annotationWidth = 0;
                bool leftAnnotation = String.Equals(preferences.AnnotationToolbarPosition, "Left", StringComparison.OrdinalIgnoreCase);
                if (canvas.HasImage && verticalAnnotation)
                    annotationWidth = LayoutVerticalAnnotationToolbar(leftAnnotation, y, viewportHeight, w, showTextSize);
                using (IDisposable preservedView = preserveView ? canvas.PreserveViewForLayout() : null)
                    workspace.SetBounds(leftAnnotation ? annotationWidth : 0, y, Math.Max(1, w - annotationWidth), viewportHeight);
                lastViewLayoutClientSize = ClientSize;
                lastViewLayoutImageIdentity = canvas.ImageIdentity;
                int zoomRight = w - Ui(8);
                zoomInButton.SetBounds(zoomRight - Ui(28), zoomRowY, Ui(28), Ui(28)); zoomRight -= Ui(28);
                zoomLabel.SetBounds(zoomRight - zoomLabelWidth, zoomRowY, zoomLabelWidth, Ui(28)); zoomRight -= zoomLabelWidth;
                fitButton.SetBounds(zoomRight - Ui(38), zoomRowY, Ui(38), Ui(28)); zoomRight -= Ui(38);
                zoomOutButton.SetBounds(zoomRight - Ui(28), zoomRowY, Ui(28), Ui(28)); zoomRight -= Ui(34);
                dimensions.SetBounds(zoomRight - dimensionWidth, zoomRowY, dimensionWidth, Ui(28));
                status.SetBounds(Ui(12), 0, statusWidth, zoomRowY > 0 ? zoomRowY : footer);
                bool hasImage = canvas.HasImage;
                zoomOutButton.Visible = fitButton.Visible = zoomInButton.Visible = zoomLabel.Visible = dimensions.Visible = hasImage;
                if (!hasImage) status.Width = Math.Max(10, w - Ui(24));
            }
            finally { arrangingChrome = false; }
        }

        private int LayoutVerticalAnnotationToolbar(bool left, int top, int height, int availableWidth, bool showTextSize)
        {
            int padding = Ui(8);
            int oldScroll = Math.Max(0, -annotationToolbar.AutoScrollPosition.Y);
            annotationToolbar.SuspendLayout();
            try
            {
                // Position from an unscrolled origin, then restore the user's
                // scroll offset. This also prevents accumulated offsets when
                // selection/style events lay out the same side bar again.
                annotationToolbar.AutoScrollPosition = Point.Empty;
                annotationToolbar.AutoScrollMinSize = Size.Empty;
                annotationToolbar.AutoScroll = true;
                Size content = MeasureVerticalAnnotationToolbar(showTextSize, true);
                int contentWidth = content.Width, contentHeight = content.Height;
                int scrollbar = contentHeight > height ? Math.Max(SystemInformation.VerticalScrollBarWidth, Ui(17)) : 0;
                int sidebarWidth = Math.Max(Ui(110), contentWidth + padding * 2) + scrollbar;
                sidebarWidth = Math.Min(Math.Max(1, availableWidth), sidebarWidth);
                annotationToolbar.SetBounds(left ? 0 : availableWidth - sidebarWidth, top, sidebarWidth, height);
                annotationToolbar.AutoScrollMinSize = new Size(0, contentHeight);
                annotationToolbar.AutoScrollPosition = new Point(0, oldScroll);
                return sidebarWidth;
            }
            finally { annotationToolbar.ResumeLayout(false); }
        }

        private Size MeasureVerticalAnnotationToolbar(bool showTextSize, bool arrange)
        {
            int padding = Ui(8), iconSize = Ui(38), gap = Ui(4);
            Control style = showTextSize ? (Control)textSize : width;
            int contentWidth = Math.Max(Ui(94), ToolbarWidth(style, 94));
            int rowY = padding, column = 0;
            foreach (Control control in annotationControls)
            {
                if ((control == width && showTextSize) || (control == textSize && !showTextSize)) continue;
                if (control == style)
                {
                    if (column != 0) { rowY += iconSize + gap; column = 0; }
                    int styleHeight = Math.Max(Ui(30), control.PreferredSize.Height);
                    if (arrange) control.SetBounds(padding, rowY, contentWidth, styleHeight);
                    rowY += styleHeight + gap;
                }
                else
                {
                    int gridLeft = padding + Math.Max(0, (contentWidth - iconSize * 2 - gap) / 2);
                    if (arrange) control.SetBounds(gridLeft + column * (iconSize + gap), rowY, iconSize, iconSize);
                    column++;
                    if (column == 2) { rowY += iconSize + gap; column = 0; }
                }
            }
            if (column != 0) rowY += iconSize + gap;
            return new Size(contentWidth, rowY + padding - gap);
        }

        private bool HasLayoutWorkingAreaOverride
        {
            get { return layoutTestMode && LayoutWorkingAreaOverride.Width > 0 && LayoutWorkingAreaOverride.Height > 0; }
        }

        private Rectangle EditorWorkingArea()
        {
            return HasLayoutWorkingAreaOverride ? LayoutWorkingAreaOverride : Screen.FromControl(this).WorkingArea;
        }

        private int ToolbarWidth(Control control, int minimum)
        {
            float scale = Ui(1000) / 1000f;
            StudioButton button = control as StudioButton;
            StudioComboBox combo = control as StudioComboBox;
            int measured = button != null ? button.PreferredContentWidth(scale) : combo != null ? combo.PreferredContentWidth(scale)
                : TextRenderer.MeasureText(control.Text ?? "", control.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + Ui(4);
            return Math.Max(Ui(minimum), measured);
        }

        private int WrappedTextHeight(Control control, int availableWidth, int minimum)
        {
            return Math.Max(minimum, TextRenderer.MeasureText(control.Text ?? "", control.Font, new Size(Math.Max(1, availableWidth), Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Ui(4));
        }

        private void PlaceToolbarControl(Control control, int widthPixels, ref int x, ref int rowY, int rowHeight, int availableWidth, int gap)
        {
            if (x > Ui(12) && x + widthPixels > availableWidth - Ui(12)) { x = Ui(12); rowY += rowHeight + Ui(6); }
            int height = control is ComboBox ? Math.Max(Ui(30), control.PreferredSize.Height) : rowHeight;
            control.SetBounds(x, rowY + Math.Max(0, (rowHeight - height) / 2), widthPixels, height);
            x += widthPixels + gap;
        }

        private void UpdateTextActionControls()
        {
            if (textActionsButton == null) return;
            bool changedMode = textActionsButton.Selected != canvas.TextActionsMode;
            bool changedReadiness = lastTextCommandsReady != canvas.TextActionCommandsReady;
            lastTextCommandsReady = canvas.TextActionCommandsReady;
            textActionsButton.Selected = canvas.TextActionsMode;
            copyAllTextButton.Enabled = !String.IsNullOrWhiteSpace(canvas.AllRecognizedText);
            copySelectedTextButton.Enabled = canvas.HasSelectedRecognizedText;
            translateTextButton.Enabled = canvas.TextActionsMode && canvas.TextActionCommandsReady && !capturePending && !String.IsNullOrWhiteSpace(canvas.AllRecognizedText);
            editRecognizedLineButton.Enabled = canvas.TextActionsMode && !capturePending && canvas.CanEditRecognizedLine;
            deleteRecognizedTextButton.Enabled = canvas.TextActionsMode && !capturePending && canvas.CanDeleteRecognizedText;
            UpdateRecognitionStatus();
            if (changedMode || changedReadiness) LayoutChrome();
        }

        private void UpdateTextStyleControls()
        {
            if (updatingTextSize || textSize.Items.Count == 0) return;
            updatingTextSize = true;
            try
            {
                float size = canvas.TextFontSize;
                if (Single.IsNaN(size) || Single.IsInfinity(size) || size <= 0) size = 20;
                int index = -1, insertAt = textSize.Items.Count;
                for (int i = 0; i < textSize.Items.Count; i++)
                {
                    TextSizeItem item = (TextSizeItem)textSize.Items[i];
                    if (Math.Abs(item.Value - size) < 0.001f) { index = i; break; }
                    if (item.Value > size && insertAt == textSize.Items.Count) insertAt = i;
                }
                if (index < 0)
                {
                    textSize.Items.Insert(insertAt, new TextSizeItem(size));
                    index = insertAt;
                }
                textSize.SelectedIndex = index;
                textSize.Enabled = canvas.HasImage && !capturePending;
            }
            finally { updatingTextSize = false; }
            LayoutChrome();
        }

        private void CopyRecognizedText(bool all)
        {
            string value = all ? canvas.AllRecognizedText : canvas.SelectedRecognizedText;
            if (String.IsNullOrWhiteSpace(value)) { SetStatus("Select recognized words on the image first."); return; }
            try { RegisterClipboardIntent(); Clipboard.SetDataObject(value, true, 4, 100); SetStatus(all ? "All recognized text copied." : "Selected text copied."); }
            catch (Exception) { SetStatus("The clipboard is busy. Please try Copy again."); }
        }

        private void ShowDelayOptions()
        {
            if (delayPopup == null || delayPopup.IsDisposed)
            {
                delayPopup = new ContextMenuStrip();
                TooltipService.RegisterMenu(delayPopup);
                string[] labels = { "No delay", "3 seconds", "5 seconds", "10 seconds" };
                for (int i = 0; i < labels.Length; i++)
                {
                    int index = i;
                    ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                    item.Click += delegate { delay.SelectedIndex = index; tips.SetToolTip(delayOptionsButton, "Capture delay: " + labels[index]); };
                    delayPopup.Items.Add(item);
                }
            }
            // Keep the menu alive through WinForms' complete close sequence.
            // It is disposed with the main window, rather than from its Closed event.
            for (int i = 0; i < delayPopup.Items.Count; i++)
                ((ToolStripMenuItem)delayPopup.Items[i]).Checked = delay.SelectedIndex == i;
            StudioTheme.StyleMenu(delayPopup);
            delayPopup.Show(delayOptionsButton, new Point(0, delayOptionsButton.Height));
        }

        private void UpdateDelayIndicator()
        {
            if (delayOptionsButton == null) return;
            int index = Math.Max(0, Math.Min(3, delay.SelectedIndex));
            int seconds = new int[] { 0, 3, 5, 10 }[index];
            delayOptionsButton.Text = seconds == 0 ? "Off" : seconds + "s";
            delayOptionsButton.Selected = seconds > 0;
            delayOptionsButton.AccessibleName = seconds == 0 ? "Capture delay off" : "Capture delay " + seconds + " seconds";
            tips.SetToolTip(delayOptionsButton, seconds == 0 ? "No capture delay" : "Capture delay: " + seconds + " seconds");
        }

        private void ShowAudioOptions()
        {
            if (capturePending) return;
            if (!layoutTestMode && !loadingAudioSources) LoadAudioSources();
            using (Form dialog = new Form())
            {
                dialog.Text = "Recording audio — Snip Studio";
                dialog.Font = Font;
                dialog.BackColor = StudioTheme.Surface;
                dialog.ForeColor = StudioTheme.Text;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(Ui(510), Ui(146));
                TrySetAppIcon(dialog);
                StudioButton done = ActionButton("Done", "", 90, delegate { dialog.Close(); });
                done.Primary = true;
                done.Dock = DockStyle.Bottom;
                done.Height = Ui(36);
                done.DialogResult = DialogResult.Cancel;
                dialog.CancelButton = done;
                audioOptionsOpen = true;
                chromeRoot.Controls.Remove(recordingOptions);
                recordingOptions.Dock = DockStyle.Fill;
                recordingOptions.Visible = true;
                dialog.Controls.Add(recordingOptions);
                dialog.Controls.Add(done);
                dialog.Shown += delegate { StudioTheme.ApplyCaption(dialog); };
                try { dialog.ShowDialog(this); }
                finally
                {
                    dialog.Controls.Remove(recordingOptions);
                    recordingOptions.Dock = DockStyle.None;
                    chromeRoot.Controls.Add(recordingOptions);
                    audioOptionsOpen = false;
                    LayoutChrome();
                }
            }
        }
    }
}
