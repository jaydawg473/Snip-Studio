using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipStudio.WinUI.Backend;
using Windows.Globalization.NumberFormatting;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private int selectedAnnotationIndex = -1;
    private bool changingTextFormat;
    private EmojiStickerSnapshot? activeEmoji;
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
        private double exactValue, formattedExactValue;
        private string? formattedText;

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
    private async void OnCropClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Crop);
    private async void OnLineClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Line);
    private async void OnArrowClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Arrow);
    private async void OnEllipseClicked(object sender, RoutedEventArgs args) => await ChooseToolAsync(PreviewTool.Ellipse);
    private async void OnEmojiClicked(object sender, RoutedEventArgs args)
    {
        EmojiButton.IsChecked = selectedTool == PreviewTool.Emoji;
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
            activeEmoji = chosen;
            selectedAnnotationIndex = -1;
            UpdateSelection();
            SetTool(PreviewTool.Emoji, whileBusy: true);
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
    }

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
            {
                selectedAnnotationIndex = session.HitTest(first, Math.Max(3, args.Width));
                if (selectedAnnotationIndex >= 0 && (Math.Abs(last.X - first.X) >= 0.5 || Math.Abs(last.Y - first.Y) >= 0.5))
                    session.MoveAnnotation(selectedAnnotationIndex, new ImagePoint(last.X - first.X, last.Y - first.Y));
                UpdateSelection();
                break;
            }
            case PreviewTool.Text:
                if (Math.Abs(last.X - first.X) < 2 && Math.Abs(last.Y - first.Y) < 2 &&
                    await TryNavigateRecognizedTextAsync(first)) return;
                BeginTextDraft(first, last);
                return;
            case PreviewTool.OcrSelect:
                await CommitOcrGestureAsync(args);
                return;
            case PreviewTool.Eraser:
                session.ErasePath(args.Points, Math.Max(3, args.Width));
                selectedAnnotationIndex = -1;
                break;
            case PreviewTool.Crop:
                if (!await ReviewAreaAsync(first, last, remove: false)) return;
                ResetDocumentSelection();
                break;
            case PreviewTool.RemoveArea:
                if (!await ReviewAreaAsync(first, last, remove: true)) return;
                ResetDocumentSelection();
                break;
            case PreviewTool.Emoji:
                if (activeEmoji is null) return;
                bool placed = Math.Abs(last.X - first.X) >= 0.5 && Math.Abs(last.Y - first.Y) >= 0.5
                    ? session.AddEmoji(activeEmoji.Key, first, last)
                    : session.PlaceEmoji(activeEmoji.Key, first);
                if (!placed) return;
                selectedAnnotationIndex = session.AnnotationCount - 1;
                break;
            case PreviewTool.Rectangle:
            case PreviewTool.Ellipse:
            case PreviewTool.Line:
            case PreviewTool.Arrow:
                session.AddShape(args.Tool switch
                {
                    PreviewTool.Ellipse => AnnotationShape.Ellipse,
                    PreviewTool.Line => AnnotationShape.Line,
                    PreviewTool.Arrow => AnnotationShape.Arrow,
                    _ => AnnotationShape.Rectangle
                }, first, last, args.Argb, args.Width);
                selectedAnnotationIndex = session.AnnotationCount - 1;
                break;
            case PreviewTool.Highlighter when SnapHighlighterButton.IsChecked == true && !args.Freehand:
                if (ocrSnapshot is null || !session.IsOcrSnapshotCurrent(ocrSnapshot)) ocrSnapshot = await session.RecognizeTextAsync(ActionToken);
                if (!ocrSnapshot.Available) throw new InvalidOperationException(ocrSnapshot.Message + " Hold Shift while dragging for freehand highlighting.");
                if (!session.HighlightRecognizedText(ocrSnapshot, first, last, args.Argb))
                    throw new InvalidOperationException("No recognized words were touched. Hold Shift while dragging for freehand highlighting.");
                break;
            default:
                session.AddStroke(args.Points, args.Argb, args.Width, args.Tool == PreviewTool.Highlighter);
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
        await RunActionAsync("Applying object geometry…", async () =>
        {
            if (!session.IsAnnotationCurrent(args.Selection)) throw new InvalidOperationException("This selection changed. Select the object again.");
            if (args.Endpoints is { Count: 2 }) session.UpdateShapeEndpoints(args.Selection, args.Endpoints[0], args.Endpoints[1]);
            else session.UpdateAnnotationProperties(args.Selection, args.Frame, rotationDegrees: args.RotationDegrees);
            selectedAnnotationIndex = args.Selection.Index;
            ocrSelection = null; Viewport.SetRecognizedBands(null);
            await RefreshPreviewAsync(resetView: false); UpdateSelection();
            SetStatus("Object geometry kept. Undo restores the previous geometry."); return true;
        });
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
            float width = Math.Abs(last.X - first.X) >= 16 ? (float)Math.Abs(last.X - first.X) : Math.Min(320, session.ImageWidth - (float)first.X);
            draft = new(-1, "", session.CreateTextRtf("", family, size, style, textColor), family, style,
                new ImagePoint(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y)), textColor, size, Math.Max(8, width), 0);
        }
        BeginTextDraft(draft);
    }

    private void BeginTextDraft(TextEditDraft draft, OcrLineEditSnapshot? recognizedLine = null)
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
        Viewport.BeginTextEdit(draft, recognizedLine);
        contextDismissed = false;
        UpdateToolContext();
        UpdateTextDraftCommands();
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
            selectedAnnotationIndex = session.ApplyRecognizedLineEdit(recognizedLine, draft.Text, draft.Rtf, draft.Argb, draft.FontSize);
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

    private async Task<bool> ApplyTextDraftAsync() => await RunActionAsync("Applying text…", async () =>
    {
        await RefreshPreviewAsync(resetView: false);
        UpdateSelection();
        SetStatus("Text kept. Click text to edit it again.");
        return true;
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
        textSizeFormatter.SetExactValue(args.NewValue);
        if (!changingTextFormat && !busy && !closed && Viewport is not null && double.IsFinite(args.NewValue)) Viewport.FormatText(null, (float)Math.Clamp(args.NewValue, 1, 1024), null, null);
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

    private async Task<bool> ReviewAreaAsync(ImagePoint first, ImagePoint last, bool remove)
    {
        var x = PixelNumber("Left (px)", Math.Min(first.X, last.X), session.ImageWidth - 1);
        var y = PixelNumber("Top (px)", Math.Min(first.Y, last.Y), session.ImageHeight - 1);
        var width = PixelNumber("Width (px)", Math.Abs(last.X - first.X), session.ImageWidth);
        var height = PixelNumber("Height (px)", Math.Abs(last.Y - first.Y), session.ImageHeight);
        var color = new ColorPicker { Color = Windows.UI.Color.FromArgb(255, 255, 255, 255), IsAlphaEnabled = true, IsAlphaSliderVisible = true };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(new TextBlock { Text = remove ? "Choose the source pixels to replace. Annotations remain editable." : "Keep this rectangle. Undo can restore the full image.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(x); panel.Children.Add(y); panel.Children.Add(width); panel.Children.Add(height); if (remove) panel.Children.Add(color);
        Viewport.SetSelection(new ImageRectangle(x.Value, y.Value, width.Value, height.Value));
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = remove ? "Remove image area" : "Apply crop", Content = new ScrollViewer { Content = panel, MaxHeight = 440 }, PrimaryButtonText = remove ? "Replace area" : "Crop", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        bool apply = await dialog.ShowAsync() == ContentDialogResult.Primary;
        Viewport.SetSelection(null);
        if (!apply) return false;
        foreach (double number in new[] { x.Value, y.Value, width.Value, height.Value })
            if (!double.IsFinite(number)) throw new ArgumentException("Enter finite pixel coordinates for the area.");
        var start = new ImagePoint(x.Value, y.Value); var end = new ImagePoint(Math.Min(session.ImageWidth, x.Value + width.Value), Math.Min(session.ImageHeight, y.Value + height.Value));
        if (remove) { var c = color.Color; session.RemoveArea(start, end, ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B); }
        else session.Crop(start, end);
        return true;
    }

    private async Task<EmojiStickerSnapshot?> ChooseEmojiAsync()
    {
        var picker = new GridView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            MaxHeight = 260,
            MaxWidth = 360
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Emoji stickers");
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Choose an emoji", Content = picker, CloseButtonText = "Cancel" };
        foreach (EmojiStickerSnapshot sticker in session.EmojiCatalog)
        {
            var tile = new GridViewItem { Content = new TextBlock { Text = sticker.Glyph, FontSize = 26 }, Tag = sticker };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, sticker.DisplayName);
            ToolTipService.SetToolTip(tile, sticker.DisplayName);
            picker.Items.Add(tile);
        }
        EmojiStickerSnapshot? chosen = null;
        picker.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not GridViewItem { Tag: EmojiStickerSnapshot sticker }) return;
            chosen = sticker;
            dialog.Hide();
        };
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
            var rotation = new NumberBox { Header = "Emoji rotation (degrees)", Minimum = -360, Maximum = 360, Value = item.Emoji?.RotationDegrees ?? 0 };
            var outlineWidth = new NumberBox { Header = "Outline width (px)", Minimum = 1, Maximum = 100, Value = item.Shape?.OutlineWidth ?? 3 };
            var outlineOpacity = new NumberBox { Header = "Outline opacity (%)", Minimum = 0, Maximum = 100, Value = item.Shape?.OutlineOpacity ?? 100, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
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
            if (item.Emoji is not null) panel.Children.Add(rotation);
            if (item.Shape is not null) { panel.Children.Add(outline); panel.Children.Add(outlineWidth); panel.Children.Add(outlineOpacity); panel.Children.Add(new TextBlock { Text = "Outline color" }); panel.Children.Add(outlineColor); if (item.Shape.Shape is AnnotationShape.Rectangle or AnnotationShape.Ellipse) { panel.Children.Add(fill); panel.Children.Add(fillOpacity); panel.Children.Add(new TextBlock { Text = "Fill color" }); panel.Children.Add(fillColor); } }
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false };
            panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Object properties", Content = new ScrollViewer { Content = panel, MaxHeight = 460 }, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) =>
            {
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
                if (newFill != item.Shape.FillArgb || newOutline != item.Shape.OutlineArgb || outlineWidth.Value != item.Shape.OutlineWidth || fillOpacity.Value != item.Shape.FillOpacity || outlineOpacity.Value != item.Shape.OutlineOpacity)
                    styleEdit = new ShapeStyleEdit(newFill, newOutline, (float)outlineWidth.Value, (int)fillOpacity.Value, (int)outlineOpacity.Value);
            }
            if (hasEndpoints) session.UpdateShapeEndpoints(item, new ImagePoint(tailX.Value, tailY.Value), new ImagePoint(headX.Value, headY.Value), styleEdit);
            else session.UpdateAnnotationProperties(item, updatedBounds, styleEdit, item.Emoji is not null ? (float)rotation.Value : null);
            await RefreshPreviewAsync(resetView: false); UpdateSelection(); SetStatus("Object properties kept."); return true;
        });
    }
}
