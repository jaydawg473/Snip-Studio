using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SnipStudio.WinUI.Backend;
using Windows.Globalization.NumberFormatting;
using ShapeOutlinePattern = SnipStudio.ShapeOutlinePattern;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private int selectedAnnotationIndex = -1;
    private bool changingTextFormat;
    private readonly FontSizeDisplayFormatter textSizeFormatter = new();

    private void ConfigureTextSizeFormatter()
    {
        textSizeFormatter.SetExactValue(TextSizeBox.Value);
        TextSizeBox.NumberFormatter = textSizeFormatter;
    }

    /// <summary>
    /// NumberBox reparses its formatted text on focus loss. Retain the exact
    /// size for that unchanged display round-trip; only different input changes
    /// the value. This formatter belongs only to the text-size control.
    /// </summary>
    private sealed class FontSizeDisplayFormatter : INumberFormatter2, INumberParser
    {
        private readonly DecimalFormatter formatter = new()
        {
            IntegerDigits = 1,
            FractionDigits = 0,
            IsGrouped = false,
            NumberRounder = new IncrementNumberRounder { Increment = 0.01, RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp }
        };
        private double exactValue = 18, formattedExactValue;
        private string? formattedText;

        internal double ExactValue => exactValue;
        internal void SetExactValue(double value) => exactValue = value;

        public string FormatDouble(double value)
        {
            // NumberBox may round its argument before calling the formatter;
            // remember the separately supplied exact Value, not that argument.
            formattedText = formatter.FormatDouble(value);
            formattedExactValue = exactValue;
            return formattedText;
        }

        public double? ParseDouble(string text) => formattedText is not null &&
            formattedExactValue.Equals(exactValue) && string.Equals(text.Trim(), formattedText.Trim(), StringComparison.Ordinal)
            ? exactValue : formatter.ParseDouble(text);

        public string FormatInt(long value) => formatter.FormatInt(value);
        public string FormatUInt(ulong value) => formatter.FormatUInt(value);
        public long? ParseInt(string text) => formatter.ParseInt(text);
        public ulong? ParseUInt(string text) => formatter.ParseUInt(text);
    }

    private async void OnSelectClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Select);
    private async void OnTextClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Text);
    private async void OnEraserClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Eraser);
    private async void OnEmojiClicked(object sender, RoutedEventArgs args)
    {
        if (!session.HasImage) return;
        await RunActionAsync("Choose an emoji sticker…", async () =>
        {
            EmojiStickerSnapshot? chosen = await ChooseEmojiAsync();
            if (chosen is null)
            {
                if (!closed) SetStatus(ToolInstruction());
                return false;
            }
            ActionToken.ThrowIfCancellationRequested();
            if (closed) return false;
            if (Viewport.IsTextEditing)
            {
                CommitTextDraft();
                await RefreshPreviewAsync(resetView: false);
            }
            var center = PreviewIsCurrent ? Viewport.VisibleImageCenter
                : new ImagePoint(session.ImageWidth / 2d, session.ImageHeight / 2d);
            if (!session.PlaceEmoji(chosen.Key, center)) return false;
            selectedAnnotationIndex = session.AnnotationCount - 1;
            SetTool(PreviewTool.Select, whileBusy: true);
            await RefreshPreviewAsync(resetView: false);
            UpdateSelection();
            SetStatus($"{chosen.DisplayName} added. Drag it to move or use the round handle to rotate.");
            return true;
        }, commitDraft: false);
    }
    private async void OnRemoveAreaClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.RemoveArea);
    private async void OnOcrSelectClicked(object sender, RoutedEventArgs args)
    {
        if (selectedTool == PreviewTool.OcrSelect) CloseTextActions();
        else await RecognizeAsync(copyOnly: false);
    }
    private async void OnDeleteObjectClicked(object sender, RoutedEventArgs args) => await DeleteSelectedAsync();
    private async void OnApplyTextClicked(object sender, RoutedEventArgs args) => await ApplyTextDraftAsync();
    private void OnCancelTextClicked(object sender, RoutedEventArgs args) => CancelTextDraft();
    private async void OnObjectPropertiesClicked(object sender, RoutedEventArgs args) => await ShowObjectPropertiesAsync();

    private async void OnRotateLeftClicked(object sender, RoutedEventArgs args) => await RotateSelectedAsync(-90);
    private async void OnRotateRightClicked(object sender, RoutedEventArgs args) => await RotateSelectedAsync(90);

    private void OnViewportAnnotationSelectionChanged(object? sender, AnnotationSnapshot? selection)
    {
        if (busy || closed) return;
        selectedAnnotationIndex = selection?.Index ?? -1;
        UpdateCommands();
    }

    private void UpdateObjectCommands(AnnotationSnapshot? selection, bool move)
    {
        bool enabled = !busy && !applicationExitPending && PreviewIsCurrent;
        bool hasShape = selection?.Shape is not null;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled =
            enabled && (selection is { Shape: not null } or { Emoji: not null });
        MovePropertiesButton.IsEnabled = enabled && selection is not null && selection.Kind != AnnotationKind.Ink;
        SelectionWidthButton.IsEnabled = enabled && hasShape;
        SelectionWidthButton.Visibility = hasShape ? Visibility.Visible : Visibility.Collapsed;
        if (selection?.Shape is { } shape) SelectionWidthButton.Label = $"Width {shape.OutlineWidth:0.##} px";
        SelectionLinePatternContainer.Visibility = move ? Visibility.Visible : Visibility.Collapsed;
        SelectionLinePatternButton.IsEnabled = enabled && hasShape;
        ShowLinePattern(SelectionLinePatternButton, selection?.Shape?.OutlinePattern);
    }

    private Task RotateSelectedAsync(float clockwiseDegrees) => RunActionAsync("Rotating object…", async () =>
    {
        var selection = session.GetAnnotation(selectedAnnotationIndex);
        float? current = selection?.Shape?.RotationDegrees ?? selection?.Emoji?.RotationDegrees;
        if (selection is null || current is not float angle ||
            !session.RotateAnnotation(selection, angle + clockwiseDegrees)) return false;
        await RefreshPreviewAsync(resetView: false);
        UpdateSelection();
        SetStatus("Object rotated. Undo restores its previous angle.");
        return true;
    });

    private async Task ChooseToolAsync(PreviewTool next)
    {
        if (busy || closed) return;
        CancelPendingSnipResize();
        if (next == selectedTool && Viewport.IsTextEditing)
        {
            SetTool(selectedTool);
            return;
        }
        if (Viewport.IsTextEditing && !await ApplyTextDraftAsync())
        {
            SetTool(selectedTool);
            return;
        }
        SetTool(next);
        if (next == PreviewTool.Highlighter) await PrepareHighlighterRecognitionAsync();
    }

    private async void OnSnapHighlighterChanged(object sender, RoutedEventArgs args)
    {
        Viewport.SnapHighlighterToText = SnapHighlighterButton.IsChecked == true;
        if (selectedTool == PreviewTool.Highlighter) await PrepareHighlighterRecognitionAsync();
    }

    private Task PrepareHighlighterRecognitionAsync()
    {
        if (!Viewport.SnapHighlighterToText || !session.HasImage ||
            (ocrSnapshot is { } cached && session.IsOcrSnapshotCurrent(cached))) return Task.CompletedTask;

        return RunActionAsync("Finding text for the highlighter…", async () =>
        {
            ocrSnapshot = await session.RecognizeTextAsync(ActionToken);
            bool ready = ocrSnapshot.Available && ocrSnapshot.Lines.Count > 0;
            SetStatus(ready ? ToolInstruction() : "No text found. Hold Shift to highlight freehand.");
            return ready;
        }, commitDraft: false);
    }

    private IReadOnlyList<OcrBounds> PreviewSnappedHighlight(ImagePoint start, ImagePoint end, double tolerance) =>
        CurrentRecognition() is OcrSnapshot snapshot ? session.PreviewSnappedHighlight(snapshot, start, end, tolerance) : [];

    private void ResetDocumentSelection()
    {
        selectedAnnotationIndex = -1;
        ocrSnapshot = null;
        ocrTextNavigation = false;
        Viewport.SetSelection(null);
        ClearOcrSelection();
    }

    private void UpdateSelection()
    {
        AnnotationSnapshot? item = session.GetAnnotation(selectedAnnotationIndex);
        if (item is null) selectedAnnotationIndex = -1;
        Viewport.SetSelectedAnnotation(item);
    }

    private async Task CommitGestureAsync(DrawGestureEventArgs args)
    {
        ImagePoint first = args.Points[0], last = args.Points[^1];
        switch (args.Tool)
        {
            case PreviewTool.Select:
            case PreviewTool.Crop:
                return; // Move and Crop commit through their dedicated canvas commands.
            case PreviewTool.Text:
                if (Math.Abs(last.X - first.X) < 2 && Math.Abs(last.Y - first.Y) < 2)
                {
                    if (TryOpenTextNoteAt(first) || await TryNavigateRecognizedTextAsync(first)) return;
                }
                BeginTextDraft(first, last);
                return;
            case PreviewTool.OcrSelect:
                await CommitOcrGestureAsync(args);
                return;
            case PreviewTool.Eraser:
                session.ErasePath(args.Points, args.Width / 2);
                selectedAnnotationIndex = -1;
                break;
            case PreviewTool.RemoveArea:
                if (!await ReviewRemoveAreaAsync(first, last)) return;
                ResetDocumentSelection();
                break;
            case PreviewTool.Rectangle:
            case PreviewTool.Ellipse:
            case PreviewTool.Line:
            case PreviewTool.Arrow:
                if (!session.AddShape(args.Tool switch
                {
                    PreviewTool.Ellipse => AnnotationShape.Ellipse,
                    PreviewTool.Line => AnnotationShape.Line,
                    PreviewTool.Arrow => AnnotationShape.Arrow,
                    _ => AnnotationShape.Rectangle
                }, first, last, args.Argb | 0xFF000000u, args.Width, outlinePattern: drawingLinePattern,
                    outlineOpacity: penOpacity)) return;
                selectedAnnotationIndex = session.AnnotationCount - 1;
                break;
            case PreviewTool.Highlighter when args.SnapTolerance > 0:
                if (ocrSnapshot is null || !session.IsOcrSnapshotCurrent(ocrSnapshot)) ocrSnapshot = await session.RecognizeTextAsync(ActionToken);
                OcrSnapshot? highlighted = ocrSnapshot.Available ? session.ApplySnappedHighlight(ocrSnapshot, first, last,
                    args.SnapTolerance, args.Argb, (byte)(args.Argb >> 24)) : null;
                if (highlighted is null)
                {
                    SetStatus("Drag across recognized words, or hold Shift to highlight freehand.");
                    return;
                }
                ocrSnapshot = highlighted;
                break;
            default:
                session.AddStroke(args.Points, args.Argb, args.Width, args.Tool == PreviewTool.Highlighter,
                    (byte)(args.Argb >> 24));
                selectedAnnotationIndex = -1;
                break;
        }
        ocrSelection = null;
        Viewport.SetRecognizedBands(null);
        await RefreshPreviewAsync(resetView: false);
        UpdateSelection();
    }

    private async void OnSelectionTransformCommitted(object? sender, SelectionTransformEventArgs args)
    {
        bool canvasHadFocus = ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), Viewport);
        try
        {
            await RunActionAsync("Applying object geometry…", async () =>
            {
                if (!session.IsAnnotationCurrent(args.Selection))
                    throw new InvalidOperationException("This selection changed. Select the object again.");
                bool changed;
                if (args.MoveDelta is ImagePoint delta) changed = session.MoveAnnotation(args.Selection, delta);
                else if (args.Endpoints is { Count: 2 } endpoints)
                    changed = session.UpdateShapeEndpoints(args.Selection, endpoints[0], endpoints[1]);
                else if (args.RotationDegrees is float angle) changed = session.RotateAnnotation(args.Selection, angle);
                else changed = session.UpdateAnnotationProperties(args.Selection, args.Frame);
                if (!changed) return false;
                selectedAnnotationIndex = args.Selection.Index;
                ocrSelection = null;
                Viewport.SetRecognizedBands(null);
                await RefreshPreviewAsync(resetView: false);
                UpdateSelection();
                SetStatus(ToolInstruction());
                return true;
            });
        }
        finally
        {
            if (!closed)
            {
                Viewport.ClearObjectTransformPreview();
                if (canvasHadFocus && (selectedTool == PreviewTool.Select || selectedTool.IsShapeTool()) && !busy)
                    Viewport.Focus(FocusState.Programmatic);
            }
        }
    }

    private bool TryOpenTextNoteAt(ImagePoint point)
    {
        if (session.HitTestTextInk(point) is not { Text: { } text } annotation) return false;
        ClearOcrSelection();
        SetTool(PreviewTool.Text, whileBusy: true);
        BeginTextDraft(text.Location, text.Location, annotation);
        SetStatus("Editing the existing text. Choose Done or press Ctrl+Enter to keep it.");
        return true;
    }

    private void BeginTextDraft(ImagePoint first, ImagePoint last, AnnotationSnapshot? existing = null)
    {
        int index = existing?.Index ?? session.HitTest(first, 3);
        AnnotationSnapshot? annotation = existing ?? session.GetAnnotation(index);
        TextAnnotationSnapshot? text = annotation?.Text;
        TextEditDraft draft;
        if (text is not null)
        {
            selectedAnnotationIndex = index;
            draft = new(index, text.Text, text.Rtf, text.FontFamily, text.FontStyle, text.Location,
                text.Argb, text.FontSize, text.LayoutWidth, text.MinimumHeight);
        }
        else
        {
            selectedAnnotationIndex = -1;
            string family = SelectedFontFamily;
            float size = (float)(double.IsFinite(TextSizeBox.Value) ? TextSizeBox.Value : 18);
            var style = CurrentTextStyle;
            double dragWidth = Math.Abs(last.X - first.X), dragHeight = Math.Abs(last.Y - first.Y);
            bool drewFrame = dragWidth >= 2 && dragHeight >= 2;
            float width = drewFrame ? (float)Math.Round(dragWidth) : Math.Min(320, session.ImageWidth - (float)first.X);
            float minimumHeight = drewFrame ? (float)Math.Round(dragHeight) : 0;
            draft = new(-1, "", session.CreateTextRtf("", family, size, style, textColor), family, style,
                drewFrame ? new ImagePoint(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y)) : first,
                textColor, size, Math.Max(drewFrame ? 1 : 8, width), minimumHeight);
        }
        BeginTextDraft(draft);
    }

    private void BeginTextDraft(TextEditDraft draft, OcrLineEditSnapshot? recognizedLine = null, OcrLineSnapshot? sourceGeometry = null)
    {
        ocrLineDraft = recognizedLine;
        if (recognizedLine is not null) selectedAnnotationIndex = -1;
        changingTextFormat = true;
        try
        {
            bool found = false;
            foreach (ComboBoxItem item in FontFamilyBox.Items)
                if (string.Equals(item.Content?.ToString(), draft.FontFamily, StringComparison.OrdinalIgnoreCase))
                { FontFamilyBox.SelectedItem = item; found = true; break; }
            if (!found) { var item = new ComboBoxItem { Content = draft.FontFamily }; FontFamilyBox.Items.Add(item); FontFamilyBox.SelectedItem = item; }
            TextSizeBox.Value = draft.FontSize;
            BoldButton.IsChecked = draft.Style.HasFlag(TextFontStyle.Bold);
            ItalicButton.IsChecked = draft.Style.HasFlag(TextFontStyle.Italic);
            UnderlineButton.IsChecked = draft.Style.HasFlag(TextFontStyle.Underline);
            textColor = draft.Argb;
            Viewport.InkArgb = draft.Argb;
            changingColor = true;
            InkColorPicker.Color = Windows.UI.Color.FromArgb((byte)(draft.Argb >> 24), (byte)(draft.Argb >> 16), (byte)(draft.Argb >> 8), (byte)draft.Argb);
            changingColor = false;
        }
        finally { changingTextFormat = false; }
        Viewport.SetSelection(null);
        Viewport.BeginTextEdit(draft, recognizedLine, sourceGeometry);
        contextDismissed = false;
        UpdateToolContext();
        UpdateTextDraftCommands();
    }

    private Task EditSelectedTextAnnotationAsync()
    {
        if (Viewport.IsTextEditing || session.GetAnnotation(selectedAnnotationIndex) is not { Text: { } text } annotation)
            return Task.CompletedTask;

        return RunActionAsync("Opening text note…", () =>
        {
            SetTool(PreviewTool.Text, whileBusy: true);
            BeginTextDraft(text.Location, text.Location, annotation);
            return Task.FromResult(true);
        });
    }

    private string SelectedFontFamily => !string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? FontFamilyBox.Text.Trim()
        : (FontFamilyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Segoe UI";
    private TextFontStyle CurrentTextStyle => (BoldButton.IsChecked == true ? TextFontStyle.Bold : 0)
        | (ItalicButton.IsChecked == true ? TextFontStyle.Italic : 0)
        | (UnderlineButton.IsChecked == true ? TextFontStyle.Underline : 0);

    private void CommitTextDraft()
    {
        TextEditDraft? draft = Viewport.CaptureTextDraft();
        if (draft is null) return;
        if (!Viewport.TextDraftWithinLimit)
            throw new InvalidOperationException("Text is longer than 16,000 native characters. Your draft is kept. Shorten it or split it into text objects before applying.");
        if (ocrLineDraft is OcrLineEditSnapshot recognizedLine)
            selectedAnnotationIndex = session.ApplyRecognizedLineEdit(recognizedLine, draft.Text, draft.Rtf, draft.Argb,
                draft.FontSize, draft.LayoutWidth, draft.ReplacementBackground);
        else if (draft.AnnotationIndex >= 0)
        {
            session.UpdateText(draft.AnnotationIndex, draft.Text, draft.Argb, draft.FontSize, draft.LayoutWidth, draft.Rtf, draft.MinimumHeight);
            if (draft.Text.Length == 0) selectedAnnotationIndex = -1;
        }
        else if (draft.Text.Length > 0)
            selectedAnnotationIndex = session.AddText(draft.Text, draft.Location, draft.Argb, draft.FontSize, draft.LayoutWidth, draft.Rtf, draft.MinimumHeight);
        ocrLineDraft = null;
        Viewport.EndTextEdit();
        ocrSelection = null;
        Viewport.SetRecognizedBands(null);
        UpdateToolContext();
        UpdateSelection();
    }

    private Task<bool> ApplyTextDraftAsync() => RunActionAsync("Keeping text…", () =>
    {
        SetStatus("Text kept. Click text to edit it again.");
        return Task.FromResult(true);
    });

    private void CancelTextDraft()
    {
        if (busy || !Viewport.IsTextEditing) return;
        ocrLineDraft = null;
        Viewport.EndTextEdit();
        UpdateSelection();
        UpdateTextDraftCommands();
        SetStatus("Text draft canceled. Your previous annotation is kept.");
    }

    private void UpdateTextDraftCommands()
    {
        if (closed || ApplyTextButton is null || Viewport is null) return;
        ApplyTextButton.IsEnabled = !busy && Viewport.IsTextEditing && Viewport.TextDraftWithinLimit;
        CancelTextButton.IsEnabled = !busy && Viewport.IsTextEditing;
        UndoButton.IsEnabled = !busy && (Viewport.IsTextEditing ? Viewport.TextCanUndo : session.CanUndo);
        RedoButton.IsEnabled = !busy && (Viewport.IsTextEditing ? Viewport.TextCanRedo : session.CanRedo);
        if (Viewport.IsTextEditing && !Viewport.TextDraftWithinLimit) SetStatus("Text exceeds 16,000 native characters. Shorten the draft before applying; no text has been removed.");
    }

    private void OnTextFormatChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!changingTextFormat && !busy && !closed && Viewport is not null && FontFamilyBox is not null)
            Viewport.FormatText((FontFamilyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SelectedFontFamily, null, null, null);
    }
    private void OnFontFamilySubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        if (changingTextFormat || busy || closed) return;
        string family = args.Text.Trim();
        if (family.Length is < 1 or > 128 || family.Any(char.IsControl))
        { SetStatus("Enter a font family name containing 1 to 128 characters."); return; }
        ComboBoxItem? selected = sender.Items.Cast<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Content?.ToString(), family, StringComparison.OrdinalIgnoreCase));
        if (selected is null) { selected = new ComboBoxItem { Content = family }; sender.Items.Add(selected); }
        if (ReferenceEquals(sender.SelectedItem, selected)) Viewport.FormatText(family, null, null, null);
        else sender.SelectedItem = selected;
    }
    private void OnTextFormatClicked(object sender, RoutedEventArgs args)
    {
        if (changingTextFormat || busy || closed || Viewport is null || sender is not AppBarToggleButton button) return;
        Viewport.FormatTextStyle(button == BoldButton ? TextFontStyle.Bold : button == ItalicButton ? TextFontStyle.Italic : TextFontStyle.Underline, button.IsChecked == true);
    }
    private void OnTextSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsFinite(args.NewValue))
        {
            RestoreSizeValue(sender, textSizeFormatter.ExactValue);
            return;
        }
        textSizeFormatter.SetExactValue(args.NewValue);
        if (!changingTextFormat && !busy && !closed && Viewport is not null)
            Viewport.FormatText(null, (float)Math.Clamp(args.NewValue, 1, 1024), null, null);
    }

    private async Task DeleteSelectedAsync()
    {
        if (!busy && selectedTool == PreviewTool.OcrSelect && ocrSnapshot is not null && ocrSelection is not null && ocrSelection.Text.Length > 0)
        {
            await RunActionAsync("Deleting selected source text…", async () =>
            {
                if (!session.IsOcrSnapshotCurrent(ocrSnapshot)) throw new InvalidOperationException("Recognize this image again before deleting selected words.");
                session.DeleteRecognizedSelection(ocrSnapshot, ocrSelection);
                ResetDocumentSelection();
                await RefreshPreviewAsync(resetView: false);
                SetStatus("Selected source text removed. Other source pixels are kept; Undo restores the deletion."); return true;
            });
            return;
        }
        if (selectedAnnotationIndex < 0 || busy) return;
        await RunActionAsync("Deleting annotation…", async () =>
        {
            session.RemoveAnnotation(selectedAnnotationIndex);
            selectedAnnotationIndex = -1;
            await RefreshPreviewAsync(resetView: false);
            UpdateSelection();
            SetStatus("Annotation deleted. Undo restores it.");
            return true;
        });
    }

    private async void OnEraseAllClicked(object sender, RoutedEventArgs args) => await RunActionAsync("Review annotations…", async () =>
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Erase all annotations?", Content = "The original screenshot pixels are kept. Undo can restore the annotations.", PrimaryButtonText = "Erase annotations", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        session.EraseAllAnnotations(); ResetDocumentSelection();
        await RefreshPreviewAsync(resetView: false);
        SetStatus("Annotations erased. Undo restores them."); return true;
    });

    private static NumberBox PixelNumber(string label, double value, double max) => new() { Header = label, Value = value, Minimum = Math.Min(0, value), Maximum = Math.Max(value, Math.Max(1, max)), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

    private async Task<bool> ReviewRemoveAreaAsync(ImagePoint first, ImagePoint last)
    {
        var x = PixelNumber("Left (px)", Math.Min(first.X, last.X), session.ImageWidth - 1);
        var y = PixelNumber("Top (px)", Math.Min(first.Y, last.Y), session.ImageHeight - 1);
        var width = PixelNumber("Width (px)", Math.Abs(last.X - first.X), session.ImageWidth);
        var height = PixelNumber("Height (px)", Math.Abs(last.Y - first.Y), session.ImageHeight);
        var color = new ColorPicker { Color = Windows.UI.Color.FromArgb(255, 255, 255, 255), IsAlphaEnabled = true, IsAlphaSliderVisible = true };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Choose the source pixels to replace. Annotations remain editable.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(x); panel.Children.Add(y); panel.Children.Add(width); panel.Children.Add(height); panel.Children.Add(color);
        Viewport.SetSelection(new ImageRectangle(x.Value, y.Value, width.Value, height.Value));
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Remove image area", Content = new ScrollViewer { Content = panel, MaxHeight = 440 }, PrimaryButtonText = "Replace area", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        bool apply = await dialog.ShowAsync() == ContentDialogResult.Primary;
        Viewport.SetSelection(null);
        if (!apply) return false;
        foreach (double number in new[] { x.Value, y.Value, width.Value, height.Value })
            if (!double.IsFinite(number)) throw new ArgumentException("Enter finite pixel coordinates for the area.");
        var start = new ImagePoint(x.Value, y.Value); var end = new ImagePoint(Math.Min(session.ImageWidth, x.Value + width.Value), Math.Min(session.ImageHeight, y.Value + height.Value));
        var c = color.Color;
        session.RemoveArea(start, end, ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
        return true;
    }

    private async Task<EmojiStickerSnapshot?> ChooseEmojiAsync()
    {
        const int columns = 5;
        var picker = new Grid { ColumnSpacing = 4, RowSpacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        for (int index = 0; index < columns; index++)
            picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "Add an emoji",
            Content = new ScrollViewer { Content = picker, MaxHeight = 250 }, CloseButtonText = "Cancel"
        };
        EmojiStickerSnapshot? chosen = null;
        foreach (EmojiStickerSnapshot sticker in session.EmojiCatalog)
        {
            int index = picker.Children.Count;
            if (index % columns == 0) picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tile = new Button
            {
                Content = sticker.Glyph, FontSize = 24, Width = 46, Height = 42, MinWidth = 0,
                Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, sticker.DisplayName);
            if (preferences.ShowTooltips) ToolTipService.SetToolTip(tile, sticker.DisplayName);
            tile.Click += (_, _) => { chosen = sticker; dialog.Hide(); };
            Grid.SetRow(tile, index / columns);
            Grid.SetColumn(tile, index % columns);
            picker.Children.Add(tile);
        }
        await dialog.ShowAsync();
        return chosen;
    }

    private async Task ShowObjectPropertiesAsync()
    {
        await RunActionAsync("Selected object properties", async () =>
        {
            AnnotationSnapshot? item = session.GetAnnotation(selectedAnnotationIndex);
            if (item is null) return false;
            if (item.Text is not null) { SetTool(PreviewTool.Text, whileBusy: true); BeginTextDraft(item.Text.Location, item.Text.Location, item); return true; }
            if (item.Kind == AnnotationKind.Ink) { SetStatus("Ink can be moved, erased, or deleted. Choose a shape for editable geometry and style."); return false; }
            ImageRectangle bounds = item.Emoji?.Frame ?? item.Shape?.GeometryBounds ?? item.Bounds;
            var x = PixelNumber("Left (px)", bounds.X, session.ImageWidth);
            var y = PixelNumber("Top (px)", bounds.Y, session.ImageHeight);
            var width = PixelNumber("Width (px)", bounds.Width, session.ImageWidth);
            var height = PixelNumber("Height (px)", bounds.Height, session.ImageHeight);
            bool hasEndpoints = item.Shape?.Endpoints.Count == 2;
            var tailX = PixelNumber("Tail X (px)", hasEndpoints ? item.Shape!.Endpoints[0].X : 0, session.ImageWidth);
            var tailY = PixelNumber("Tail Y (px)", hasEndpoints ? item.Shape!.Endpoints[0].Y : 0, session.ImageHeight);
            var headX = PixelNumber("Head X (px)", hasEndpoints ? item.Shape!.Endpoints[1].X : 0, session.ImageWidth);
            var headY = PixelNumber("Head Y (px)", hasEndpoints ? item.Shape!.Endpoints[1].Y : 0, session.ImageHeight);
            bool supportsFrameRotation = !hasEndpoints && (item.Emoji is not null || item.Shape is not null);
            var rotation = new NumberBox { Header = "Rotation (degrees)", Minimum = -360, Maximum = 360,
                SmallChange = 1, LargeChange = 15, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = item.Emoji?.RotationDegrees ?? item.Shape?.RotationDegrees ?? 0 };
            var outlineWidth = new NumberBox { Header = "Outline width (px)", Minimum = 1, Maximum = 100, Value = item.Shape?.OutlineWidth ?? 3 };
            var outlineOpacity = new NumberBox { Header = "Outline opacity (%)", Minimum = 0, Maximum = 100, Value = item.Shape?.OutlineOpacity ?? 100, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var linePattern = new ComboBox { Header = "Line style", ItemsSource = Enum.GetValues<ShapeOutlinePattern>(),
                SelectedItem = item.Shape?.OutlinePattern ?? ShapeOutlinePattern.Solid, HorizontalAlignment = HorizontalAlignment.Stretch };
            var fillOpacity = new NumberBox { Header = "Fill opacity (%)", Minimum = 0, Maximum = 100, Value = item.Shape?.FillOpacity ?? 100, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var fill = new CheckBox { Content = "Fill shape", IsChecked = item.Shape?.FillArgb.HasValue == true };
            var outline = new CheckBox { Content = "Show outline", IsChecked = item.Shape?.OutlineArgb.HasValue == true };
            uint colorArgb = item.Shape?.OutlineArgb ?? penColor;
            var outlineColor = new ColorPicker { Color = Windows.UI.Color.FromArgb((byte)(colorArgb >> 24), (byte)(colorArgb >> 16), (byte)(colorArgb >> 8), (byte)colorArgb), IsAlphaEnabled = true };
            uint fillArgb = item.Shape?.FillArgb ?? 0x400078D4u;
            var fillColor = new ColorPicker { Color = Windows.UI.Color.FromArgb((byte)(fillArgb >> 24), (byte)(fillArgb >> 16), (byte)(fillArgb >> 8), (byte)fillArgb), IsAlphaEnabled = true };
            var panel = new StackPanel { Spacing = 8 };
            if (hasEndpoints) { panel.Children.Add(tailX); panel.Children.Add(tailY); panel.Children.Add(headX); panel.Children.Add(headY); }
            else { panel.Children.Add(x); panel.Children.Add(y); panel.Children.Add(width); panel.Children.Add(height); }
            if (supportsFrameRotation) panel.Children.Add(rotation);
            if (item.Shape is not null) { panel.Children.Add(outline); panel.Children.Add(linePattern); panel.Children.Add(outlineWidth); panel.Children.Add(outlineOpacity); panel.Children.Add(new TextBlock { Text = "Outline color" }); panel.Children.Add(outlineColor); if (item.Shape.Shape is AnnotationShape.Rectangle or AnnotationShape.Ellipse) { panel.Children.Add(fill); panel.Children.Add(fillOpacity); panel.Children.Add(new TextBlock { Text = "Fill color" }); panel.Children.Add(fillColor); } }
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false };
            panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Object properties", Content = new ScrollViewer { Content = panel, MaxHeight = 460 }, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (supportsFrameRotation && (!double.IsFinite(rotation.Value) || rotation.Value is < -360 or > 360))
                {
                    args.Cancel = true;
                    error.Message = "Enter a rotation from −360 to 360 degrees.";
                    error.IsOpen = true;
                    return;
                }
                if (item.Shape is null) return;
                foreach (double opacity in new[] { outlineOpacity.Value, fillOpacity.Value })
                    if (!double.IsFinite(opacity) || opacity < 0 || opacity > 100 || opacity != Math.Truncate(opacity))
                    { args.Cancel = true; error.Message = "Enter a whole-number opacity from 0 to 100 percent."; error.IsOpen = true; return; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
            if (!session.IsAnnotationCurrent(item)) throw new InvalidOperationException("This selection changed. Select the object again.");
            var updatedBounds = new ImageRectangle(x.Value, y.Value, width.Value, height.Value);
            ShapeStyleEdit? styleEdit = null;
            if (item.Shape is not null)
            {
                static uint Argb(Windows.UI.Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                uint? newFill = fill.IsChecked == true ? Argb(fillColor.Color) : null;
                uint? newOutline = outline.IsChecked == true ? Argb(outlineColor.Color) : null;
                var pattern = linePattern.SelectedItem is ShapeOutlinePattern editedPattern ? editedPattern : item.Shape.OutlinePattern;
                if (newFill != item.Shape.FillArgb || newOutline != item.Shape.OutlineArgb || outlineWidth.Value != item.Shape.OutlineWidth || fillOpacity.Value != item.Shape.FillOpacity || outlineOpacity.Value != item.Shape.OutlineOpacity || pattern != item.Shape.OutlinePattern)
                    styleEdit = new ShapeStyleEdit(newFill, newOutline, (float)outlineWidth.Value, (int)fillOpacity.Value, (int)outlineOpacity.Value, pattern);
            }
            if (hasEndpoints) session.UpdateShapeEndpoints(item, new ImagePoint(tailX.Value, tailY.Value), new ImagePoint(headX.Value, headY.Value), styleEdit);
            else session.UpdateAnnotationProperties(item, updatedBounds, styleEdit, supportsFrameRotation ? (float)rotation.Value : null);
            await RefreshPreviewAsync(resetView: false); UpdateSelection(); SetStatus("Object properties kept."); return true;
        });
    }
}
