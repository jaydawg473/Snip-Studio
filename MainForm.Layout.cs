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
        private readonly StudioComboBox recordingRegionMode = new StudioComboBox();
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
        private int lastViewLayoutPaletteWidth;
        private bool audioOptionsOpen;
        private ContextMenuStrip delayPopup;
        private Rectangle lastToolbarWorkingArea;
        private int reservedSubmenuHeight;
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
                (canvas.HasImage ? reservedSubmenuHeight : 0) + footerStrip.Height; }
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
            ConfigureCombo(recordingRegionMode, 140, new object[] { "Draw area", "Window" });
            recordingRegionMode.SelectedIndex = 0;
            recordingRegionMode.AccessibleName = "Recording area selection";
            ConfigureCombo(delay, 108, new object[] { "No delay", "3 seconds", "5 seconds", "10 seconds" });
            captureToolbar.Controls.Add(mode);
            captureToolbar.Controls.Add(recordingRegionMode);
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
            recordingModeCaption = new Label { Text = "Select the recording area", ForeColor = StudioTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            captureToolbar.Controls.Add(recordingModeCaption);
            tips.SetToolTip(mode, "Full screen lets you click one monitor. All monitors captures the entire connected desktop. Rectangle and Freeform let you select an area. For Scrolling, select only the scrolling content; leave out sidebars and fixed message boxes. Pause streaming content when possible.");
            tips.SetToolTip(recordingRegionMode, "Draw area: drag a custom rectangle. Window: click an application's visible window. Recording keeps the selected screen area if the window moves or resizes.");
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
            BuildShapesButton();
            AddCompactTool("Add text", EditorTool.Text, "text", "After recognizing text, click a found line to edit it. Switching tools keeps valid recognition available. Drag a blank area to add a text box; Advanced text opens full formatting.");
            AddCompactTool("Eraser", EditorTool.Eraser, "eraser", "Erase annotations");
            AddCompactTool("Crop", EditorTool.Crop, "crop", "Drag an edge or corner to crop. Enter applies; Escape cancels.");
            AddCompactTool("Remove area", EditorTool.RemoveArea, "remove-area", "Remove captured content. Click a flat-color object or drag an area, review the selection, then press Delete. Undo restores it.");
            StudioButton color = IconButton("Ink color", "", delegate { QueueToolOptionsAction(colorButton, delegate(Rectangle anchor) { ChooseColor(anchor); }); });
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
            BuildShapeActions();
            InitializeToolOptionsIntegration();
            tips.SetToolTip(editRecognizedLineButton, "Edit the selected line directly on the snip. You can also click recognized text; drag to select words for copying. Source font is estimated.");
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
            workspace.Padding = new Padding(4);
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
            zoomLabel.ZoomRequested += delegate
            {
                if (!canvas.HasImage || capturePending)
                {
                    zoomLabel.UpdateViewPercent(canvas.Zoom * 100);
                    zoomLabel.RevertInput();
                    return;
                }
                if (Math.Abs(zoomLabel.RequestedPercent - 100f) < .0001f) canvas.ActualSize();
                else canvas.ZoomAt(zoomLabel.RequestedPercent / 100f, new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2));
                UpdateZoomLabel();
            };
            zoomLabel.ValidationFailed += delegate { SetStatus(zoomLabel.ValidationMessage); };
            tips.SetToolTip(zoomLabel, "Type a zoom percentage and press Enter, or select 100% or another preset. Escape cancels the edit.");
            footerStrip.Controls.Add(zoomLabel);
            documentControls.Add(zoomLabel);
            canvas.Resize += delegate { UpdateZoomLabel(); };
            canvas.ViewChanged += delegate { UpdateZoomLabel(); };
            canvas.TextRecognitionStatusChanged += delegate { UpdateRecognitionStatus(); UpdateTextActionControls(); };
            canvas.TextStyleChanged += delegate { UpdateTextStyleControls(); };
            status.TextChanged += delegate { LayoutChrome(); };
            dimensions.TextChanged += delegate { LayoutChrome(); };
            recognitionStatus.TextChanged += delegate { LayoutChrome(); RefreshToolOptionsContents(); };
            externalEditorButton.TextChanged += delegate { LayoutChrome(); };
            chromeRoot.Layout += delegate { LayoutChrome(); };
            LocationChanged += delegate
            {
                if (!arrangingChrome && IsHandleCreated && EditorWorkingArea() != lastToolbarWorkingArea) LayoutChrome();
            };
            DpiChanged += delegate { LayoutChrome(); };
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
            StudioButton button = null;
            button = IconButton(label, glyph, delegate { ActivateAnnotationTool(tool, button, false); });
            ConfigureToolOptionsButton(button, tool);
            toolButtons[tool] = button;
            annotationToolbar.Controls.Add(button);
            annotationControls.Add(button);
            documentControls.Add(button);
            tips.SetToolTip(button, description + (ToolHasOptions(tool) ? " Select the tool, then click again or use its arrow for options (Alt+Down)." : ""));
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
            canvas.EmptyMessage = record ? "Choose Draw area or Window, then Record." : "Choose New, open an image, or paste from the clipboard.";
            canvas.Invalidate();
            LayoutChrome();
            if (!canvas.HasImage && !layoutTestMode)
            {
                Size = new Size(Width, Ui(260));
            }
            SetStatus(record ? "Choose Draw area or Window and your audio sources, then Record." : "Ready to capture. Choose a mode, then New.");
        }

        private CaptureMode SelectedRecordingCaptureMode
        {
            get { return CaptureService.RecordingModeFromIndex(recordingRegionMode.SelectedIndex); }
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
                recordingRegionMode.UpdateMetrics(scale);
                ((StudioComboBox)delay).UpdateMetrics(scale);
                ((StudioComboBox)width).UpdateMetrics(scale);
                textSize.UpdateMetrics(scale);
                bool verticalAnnotation = AnnotationToolbarIsVertical;
                foreach (KeyValuePair<StudioButton, bool> pair in annotationHorizontalIconOnly)
                {
                    bool iconOnly = verticalAnnotation || pair.Value;
                    if (pair.Key.IconOnly != iconOnly) { pair.Key.IconOnly = iconOnly; pair.Key.Invalidate(); }
                }
                bool showTextSize = !canvas.TextActionsMode && !IsEmojiContext && (canvas.Tool == EditorTool.Text || canvas.HasSelectedTextAnnotation);
                int requiredWidth = Math.Max(MeasureSingleRowToolbarWidth(false), MeasureSingleRowToolbarWidth(true));
                EnsureSingleRowWindowWidth(requiredWidth);
                // Dock=Fill may be deferred by an enclosing layout operation.
                chromeRoot.Size = ClientSize;
                w = chromeRoot.ClientSize.Width; h = chromeRoot.ClientSize.Height;
                int captureScroll = ResetHorizontalToolbar(captureToolbar);
                int annotationScroll = verticalAnnotation ? 0 : ResetHorizontalToolbar(annotationToolbar);
                int contextScroll = ResetHorizontalToolbar(contextToolbar);
                int captureWidth = Math.Max(w, MeasureCaptureToolbarWidth());
                int x = Ui(12), rowY = Ui(10), rowHeight = Ui(34);
                PlaceToolbarControl(newButton, ToolbarWidth(newButton, canvas.HasImage || recordingModeSelected ? 104 : 80), ref x, ref rowY, rowHeight, w, Ui(8));
                PlaceToolbarControl(screenshotModeButton, Ui(34), ref x, ref rowY, rowHeight, w, 0);
                PlaceToolbarControl(videoModeButton, Ui(34), ref x, ref rowY, rowHeight, w, Ui(10));
                bool compact = !canvas.HasImage;
                mode.Visible = !recordingModeSelected;
                recordingRegionMode.Visible = recordingModeSelected;
                delay.Visible = false;
                delayOptionsButton.Visible = !recordingModeSelected;
                audioOptionsButton.Visible = recordingModeSelected;
                if (!recordingModeSelected)
                {
                    PlaceToolbarControl(mode, ToolbarWidth(mode, 140), ref x, ref rowY, rowHeight, w, Ui(8));
                    PlaceToolbarControl(delayOptionsButton, ToolbarWidth(delayOptionsButton, 80), ref x, ref rowY, rowHeight, w, Ui(8));
                }
                else
                {
                    PlaceToolbarControl(recordingRegionMode, ToolbarWidth(recordingRegionMode, 140), ref x, ref rowY, rowHeight, w, Ui(8));
                    PlaceToolbarControl(audioOptionsButton, Ui(34), ref x, ref rowY, rowHeight, w, Ui(8));
                }
                int actionsWidth = (compact ? 1 : captureActions.Count) * (Ui(34) + Ui(4)) - Ui(4);
                int captionWidth = ToolbarWidth(recordingModeCaption, 150);
                recordingModeCaption.AutoEllipsis = false;
                bool showRecordingCaption = recordingModeSelected && x + captionWidth + actionsWidth + Ui(20) <= w;
                recordingModeCaption.Visible = showRecordingCaption;
                if (showRecordingCaption)
                    PlaceToolbarControl(recordingModeCaption, captionWidth, ref x, ref rowY, rowHeight, w, Ui(8));
                x = Math.Max(x, captureWidth - Ui(12) - actionsWidth);
                for (int i = 0; i < captureActions.Count; i++)
                {
                    bool showAction = !compact || i == captureActions.Count - 1;
                    captureActions[i].Visible = showAction;
                    if (showAction) PlaceToolbarControl(captureActions[i], Ui(34), ref x, ref rowY, rowHeight, w, Ui(4));
                }
                int y = rowY + rowHeight + Ui(10);
                y = LayoutHorizontalToolbar(captureToolbar, 0, w, y, captureWidth, captureScroll);
                y += LayoutUpdateIndicator(y, w);
                annotationToolbar.Visible = canvas.HasImage;
                bool bottomAnnotation = String.Equals(preferences.AnnotationToolbarPosition, "Bottom", StringComparison.OrdinalIgnoreCase);
                externalEditorButton.AccessibleName = externalEditorButton.Text;
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
                        if (IsShapeContext && (control == width || control == colorButton)) continue;
                        int span = ToolbarWidth(control, control == width ? 94 : control == textSize ? 106 : control == externalEditorButton ? 132 : 34);
                        PlaceToolbarControl(control, span, ref x, ref rowY, rowHeight, w, Ui(control == colorButton || control == width || control == textSize ? 8 : 2));
                    }
                    annotationHeight = LayoutHorizontalToolbar(annotationToolbar, y, w, rowY + rowHeight + Ui(6),
                        Math.Max(w, x - Ui(2) + Ui(12)), annotationScroll);
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
                int submenuTop = y;
                reservedSubmenuHeight = canvas.HasImage ? MeasureReservedSubmenuHeight(w) : 0;
                bool textMode = canvas.HasImage && canvas.TextActionsMode;
                bool cropMode = IsCropContext;
                bool shapeMode = IsShapeContext;
                bool composeMode = HasInlineTextWorkflow;
                bool context = cropMode || composeMode || textMode;
                contextToolbar.Visible = context;
                if (!IsHostedInToolOptions(snapToText)) snapToText.Visible = false;
                recognitionStatus.Visible = textMode || (IsHostedInToolOptions(recognitionStatus) && canvas.Tool == EditorTool.Highlighter);
                copyAllTextButton.Visible = copySelectedTextButton.Visible = textMode && canvas.TextActionCommandsReady;
                translateTextButton.Visible = textMode && canvas.TextActionCommandsReady;
                translateTextButton.Enabled = textMode && canvas.TextActionCommandsReady && !capturePending && !String.IsNullOrWhiteSpace(canvas.AllRecognizedText);
                closeTextActionsButton.Visible = textMode;
                editRecognizedLineButton.Visible = deleteRecognizedTextButton.Visible = textMode && canvas.TextActionCommandsReady;
                editRecognizedLineButton.Enabled = textMode && !capturePending && canvas.CanEditRecognizedLine;
                deleteRecognizedTextButton.Enabled = textMode && !capturePending && canvas.CanDeleteRecognizedText;
                int contextHeight = 0;
                int contextWidth = Math.Max(w, MeasureContextCommandsWidth());
                UpdateCropActionControls();
                UpdateAnnotationContextControls();
                if (cropMode) contextHeight = LayoutCropActions(contextWidth);
                else if (composeMode) contextHeight = LayoutInlineTextActions(contextWidth);
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
                    if (x + messageWidth > contextWidth - Ui(12)) { x = Ui(12); rowY += contextRowHeight + Ui(3); }
                    int messageHeight = WrappedTextHeight(recognitionStatus, Math.Max(Ui(80), contextWidth - x - Ui(12)), Ui(24));
                    recognitionStatus.SetBounds(x, rowY, Math.Max(1, contextWidth - x - Ui(12)), Math.Max(contextRowHeight, messageHeight));
                    contextHeight = rowY + Math.Max(contextRowHeight, messageHeight) + Ui(4);
                }
                contextHeight = LayoutHorizontalToolbar(contextToolbar, y, w, contextHeight, contextWidth, contextScroll);
                y += contextHeight;
                // Options are a compact band in the chrome, outside the image.
                // Keep the same location for Top/Bottom/Left/Right toolbars.
                LayoutDockedToolOptions(w, y);
                y = submenuTop + reservedSubmenuHeight;
                zoomLabel.UpdateMetrics(scale);
                int zoomRowHeight = Math.Max(Ui(28), zoomLabel.PreferredSize.Height);
                int footer = zoomRowHeight, zoomRowY = 0;
                // Reserve the full percentage slot so animated decimals neither
                // clip nor resize the workspace while the wheel is moving.
                int zoomLabelWidth = Ui(104);
                int dimensionWidth = ToolbarWidth(dimensions, 115);
                int footerActionsWidth = Ui(8 + 28 + 38 + 34) + zoomLabelWidth + dimensionWidth;
                int statusWidth = canvas.HasImage ? Math.Max(1, w - footerActionsWidth - Ui(24)) : Math.Max(1, w - Ui(24));
                status.AutoEllipsis = true;
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
                    int requiredViewport = MinimumImageWorkspaceHeight();
                    if (verticalAnnotation)
                        requiredViewport = Math.Max(requiredViewport, Math.Max(MeasureVerticalAnnotationToolbar(false, false).Height,
                            MeasureVerticalAnnotationToolbar(true, false).Height));
                    // The fixed submenu band is part of the initial snip shell.
                    int minimumHeight = Math.Min(work.Height, y + footer + bottomAnnotationHeight + requiredViewport + nonClientHeight);
                    int previousHeight = Height;
                    if (MinimumSize.Height != minimumHeight) MinimumSize = new Size(MinimumSize.Width, minimumHeight);
                    if (Height > previousHeight || verticalAnnotation || cropMode || shapeMode || composeMode)
                    {
                        if (Height > work.Height) Height = work.Height;
                        int top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
                        if (Top != top) Top = top;
                    }
                    h = ClientSize.Height;
                }
                h = ClientSize.Height;
                int viewportHeight = Math.Max(1, h - y - footer - bottomAnnotationHeight);
                footerStrip.SetBounds(0, Math.Max(y + bottomAnnotationHeight, h - footer), w, footer);
                if (bottomAnnotation)
                    annotationToolbar.SetBounds(0, y + viewportHeight, w, bottomAnnotationHeight);
                int annotationWidth = 0;
                bool leftAnnotation = String.Equals(preferences.AnnotationToolbarPosition, "Left", StringComparison.OrdinalIgnoreCase);
                if (canvas.HasImage && verticalAnnotation)
                    annotationWidth = LayoutVerticalAnnotationToolbar(leftAnnotation, y, viewportHeight, w, showTextSize);
                int viewportLeft = leftAnnotation ? annotationWidth : 0;
                int availableViewportWidth = Math.Max(1, w - annotationWidth);
                int paletteWidth = InlineAnnotationPaletteDockWidth(availableViewportWidth);
                // A palette changes the actual image viewport. A fitted image
                // must fit that viewport, not become a manual view at its old
                // scale with a new horizontal scrolling extent left behind.
                bool refitPaletteView = paletteWidth != lastViewLayoutPaletteWidth && canvas.FitRequested;
                using (IDisposable preservedView = preserveView ? canvas.PreserveViewForLayout() : null)
                {
                    workspace.SetBounds(viewportLeft, y, Math.Max(1, availableViewportWidth - paletteWidth), viewportHeight);
                    LayoutInlineAnnotationPaletteDock(new Rectangle(viewportLeft + availableViewportWidth - paletteWidth, y, paletteWidth, viewportHeight));
                }
                lastViewLayoutPaletteWidth = paletteWidth;
                if (refitPaletteView) canvas.RefitForPaletteLayout();
                lastViewLayoutClientSize = ClientSize;
                lastViewLayoutImageIdentity = canvas.ImageIdentity;
                int zoomRight = w - Ui(8);
                zoomInButton.SetBounds(zoomRight - Ui(28), zoomRowY, Ui(28), Ui(28)); zoomRight -= Ui(28);
                zoomLabel.SetBounds(zoomRight - zoomLabelWidth, zoomRowY, zoomLabelWidth, zoomRowHeight); zoomRight -= zoomLabelWidth;
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
                Size current = MeasureVerticalAnnotationToolbar(showTextSize, true);
                Size text = MeasureVerticalAnnotationToolbar(true, false), drawing = MeasureVerticalAnnotationToolbar(false, false);
                Size content = new Size(Math.Max(current.Width, Math.Max(text.Width, drawing.Width)),
                    Math.Max(current.Height, Math.Max(text.Height, drawing.Height)));
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
            // Formatting lives in options cards, so a side toolbar keeps the
            // same width when the selected tool or its font-size choice changes.
            int contentWidth = Ui(94);
            int rowY = padding, column = 0;
            foreach (Control control in annotationControls)
            {
                if ((control == width && showTextSize) || (control == textSize && !showTextSize)) continue;
                if (IsShapeContext && (control == width || control == colorButton)) continue;
                int gridLeft = padding + Math.Max(0, (contentWidth - iconSize * 2 - gap) / 2);
                if (arrange) control.SetBounds(gridLeft + column * (iconSize + gap), rowY, iconSize, iconSize);
                column++;
                if (column == 2) { rowY += iconSize + gap; column = 0; }
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

        private int MeasureCaptureToolbarWidth()
        {
            int primary = ToolbarWidth(newButton, canvas.HasImage || recordingModeSelected ? 104 : 80) + Ui(8) + Ui(34) * 2 + Ui(10);
            primary += recordingModeSelected ? ToolbarWidth(recordingRegionMode, 140) + Ui(8) + Ui(34) + Ui(8) : ToolbarWidth(mode, 140) + Ui(8) + ToolbarWidth(delayOptionsButton, 80) + Ui(8);
            int actions = (canvas.HasImage ? captureActions.Count : 1) * (Ui(34) + Ui(4)) - Ui(4);
            return Ui(12) * 2 + primary + actions;
        }

        private int AnnotationControlWidth(Control control)
        {
            return ToolbarWidth(control, control == width ? 94 : control == textSize ? 106 : control == externalEditorButton ? 132 : 34);
        }

        private int MeasureContextCommandsWidth()
        {
            int content = 0;
            if (IsCropContext) content = ToolbarWidth(applyCropButton, 128) + Ui(6) + ToolbarWidth(cancelCropButton, 82) + Ui(10);
            else if (HasInlineTextWorkflow) content = ToolbarWidth(finishInlineTextButton, 70) + Ui(6) + ToolbarWidth(cancelInlineTextButton, 82) + Ui(10);
            else if (canvas.HasImage && canvas.TextActionsMode)
            {
                if (canvas.TextActionCommandsReady)
                    content = ToolbarWidth(copyAllTextButton, 126) + ToolbarWidth(copySelectedTextButton, 140) +
                        ToolbarWidth(translateTextButton, 116) + ToolbarWidth(editRecognizedLineButton, 112) +
                        ToolbarWidth(deleteRecognizedTextButton, 160) + Ui(6) * 5;
                content += ToolbarWidth(closeTextActionsButton, 70) + Ui(10);
            }
            return content == 0 ? 0 : Ui(12) * 2 + content;
        }

        private int MeasureMaximumTextCommandsWidth()
        {
            return Ui(24) + ToolbarWidth(copyAllTextButton, 126) + ToolbarWidth(copySelectedTextButton, 140) +
                ToolbarWidth(translateTextButton, 116) + ToolbarWidth(editRecognizedLineButton, 112) +
                ToolbarWidth(deleteRecognizedTextButton, 160) + Ui(6) * 5 + ToolbarWidth(closeTextActionsButton, 70) + Ui(10);
        }

        private int MeasureReservedSubmenuHeight(int availableWidth)
        {
            int inlineActions = ToolbarWidth(finishInlineTextButton, 70) + Ui(6) + ToolbarWidth(cancelInlineTextButton, 82) + Ui(10);
            int inlineWidth = Math.Max(availableWidth, Ui(24) + inlineActions);
            int inline = Math.Max(
                MeasureSummaryBand(inlineTextGuidance, "Edit text in place. Ctrl+Enter finishes; Esc cancels.", inlineActions, inlineWidth, 4, 5),
                MeasureSummaryBand(inlineTextGuidance, "Type inside the box. Enter adds a line; Ctrl+Enter finishes; Esc cancels.", inlineActions, inlineWidth, 4, 5));
            if (inlineWidth > availableWidth) inline += SystemInformation.HorizontalScrollBarHeight;
            int cropActions = ToolbarWidth(applyCropButton, 128) + Ui(6) + ToolbarWidth(cancelCropButton, 82) + Ui(10);
            int cropWidth = Math.Max(availableWidth, Ui(24) + cropActions);
            string cropMessage = canvas.ImageSize.Width.ToString("N0", CultureInfo.CurrentCulture) + " × " +
                canvas.ImageSize.Height.ToString("N0", CultureInfo.CurrentCulture) + " px  •  Drag an edge or corner. Enter applies; Esc cancels.";
            int crop = MeasureSummaryBand(cropSummary, cropMessage, cropActions, cropWidth, 3, 4);
            if (cropWidth > availableWidth) crop += SystemInformation.HorizontalScrollBarHeight;
            int readyWidth = Math.Max(availableWidth, MeasureMaximumTextCommandsWidth());
            int readyActions = MeasureMaximumTextCommandsWidth() - Ui(24);
            int find = MeasureSummaryBand(recognitionStatus,
                "Select words to copy, edit a line, or delete. F2 edits; Delete removes the selection.", readyActions, readyWidth, 3, 4);
            if (readyWidth > availableWidth) find += SystemInformation.HorizontalScrollBarHeight;
            int scan = MeasureSummaryBand(recognitionStatus,
                "Matching the source font and style… You can continue using the preview.",
                ToolbarWidth(closeTextActionsButton, 70) + Ui(10), availableWidth, 3, 4);
            int options = MeasureReservedToolOptionsHeight(availableWidth);
            // Find Text and Crop do not open formatting options. Inline text can
            // show both bands, so reserve that actual simultaneous combination.
            return Math.Max(Math.Max(find, scan), Math.Max(crop, inline + options));
        }

        private int MeasureSummaryBand(Control label, string message, int actions, int width, int rowGap, int bottom)
        {
            int x = Ui(12) + actions, y = Ui(3), row = Ui(34);
            int textWidth = TextRenderer.MeasureText(message, label.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + Ui(4);
            if (x + textWidth > width - Ui(12)) { x = Ui(12); y += row + Ui(rowGap); }
            int textHeight = TextRenderer.MeasureText(message, label.Font,
                new Size(Math.Max(1, width - x - Ui(12)), Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Ui(4);
            return y + Math.Max(row, Math.Max(Ui(24), textHeight)) + Ui(bottom);
        }

        private int MeasureSingleRowToolbarWidth(bool showTextSize)
        {
            int required = Math.Max(MeasureCaptureToolbarWidth(), canvas.HasImage ? MeasureMaximumTextCommandsWidth() : 0);
            if (canvas.HasImage && !AnnotationToolbarIsVertical)
            {
                int content = Ui(12) * 2;
                foreach (Control control in annotationControls)
                {
                    if ((control == width && showTextSize) || (control == textSize && !showTextSize)) continue;
                    if (IsShapeContext && (control == width || control == colorButton)) continue;
                    content += AnnotationControlWidth(control) + Ui(control == colorButton || control == width || control == textSize ? 8 : 2);
                }
                required = Math.Max(required, content);
            }
            return required;
        }

        private void EnsureSingleRowWindowWidth(int requiredClientWidth)
        {
            if ((layoutTestMode && !HasLayoutWorkingAreaOverride) || IsFullScreenEditor || changingFullScreenEditor || WindowState != FormWindowState.Normal) return;
            Rectangle work = EditorWorkingArea();
            if (work.Width <= 0) return;
            int maximumWidth = MaximumSize.Width > 0 ? Math.Min(work.Width, MaximumSize.Width) : work.Width;
            int minimumWidth = Math.Min(maximumWidth, Math.Max(Ui(500), requiredClientWidth + Math.Max(0, Width - ClientSize.Width)));
            if (MinimumSize.Width != minimumWidth) MinimumSize = new Size(minimumWidth, MinimumSize.Height);
            // Grow for newly available commands; never shrink an existing snip
            // merely because a tool, option row, or capture mode changes.
            int targetWidth = Math.Min(maximumWidth, Math.Max(Width, minimumWidth));
            if (Width != targetWidth) Width = targetWidth;
            int left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            if (Left != left) Left = left;
            lastToolbarWorkingArea = work;
        }

        private static int ResetHorizontalToolbar(Panel toolbar)
        {
            int offset = Math.Max(0, -toolbar.AutoScrollPosition.X);
            toolbar.AutoScrollPosition = Point.Empty;
            toolbar.AutoScrollMinSize = Size.Empty;
            toolbar.AutoScroll = false;
            return offset;
        }

        private int LayoutHorizontalToolbar(Panel toolbar, int top, int viewportWidth, int contentHeight, int contentWidth, int scrollOffset)
        {
            bool overflow = contentHeight > 0 && contentWidth > viewportWidth;
            int height = contentHeight + (overflow ? SystemInformation.HorizontalScrollBarHeight : 0);
            toolbar.SetBounds(0, top, viewportWidth, height);
            toolbar.AutoScroll = overflow;
            toolbar.AutoScrollMinSize = overflow ? new Size(contentWidth, 0) : Size.Empty;
            if (overflow) toolbar.AutoScrollPosition = new Point(scrollOffset, 0);
            return height;
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
            int previousIndex = textSize.SelectedIndex;
            bool addedSize = false;
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
                    addedSize = true;
                }
                textSize.SelectedIndex = index;
                textSize.Enabled = canvas.HasImage && !capturePending;
            }
            finally { updatingTextSize = false; }
            // Caret movement raises TextStyleChanged too. Keep that frequent path
            // out of the complete chrome layout unless the size choice changed.
            if (addedSize || previousIndex != textSize.SelectedIndex) LayoutChrome();
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
