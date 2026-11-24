using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SnipStudio.WinUI.Backend;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace SnipStudio.WinUI;

internal sealed class AdvancedTextDialog
{
    internal sealed record Result(TextEditDraft Draft, int Start, int End);

    private const int MaxCharacters = 16_000;
    private readonly TextEditDraft original;
    private readonly uint? originalFill;
    private readonly float maximumWidth;
    private readonly RichEditBox editor;
    private readonly ContentDialog dialog;
    private readonly InfoBar feedback = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly ComboBox font;
    private readonly NumberBox size, width, before, after;
    private readonly ComboBox spacing;
    private readonly ColorPicker ink, highlight;
    private readonly List<(ToggleButton Button, Func<ITextCharacterFormat, bool> IsOn)> styleButtons = [];
    private bool syncing, widthChanged;
    private string initialText = "", initialRtf = "";
    private uint? fill;
    private Result? result;

    private AdvancedTextDialog(XamlRoot root, TextEditDraft draft, int start, int end,
        float availableWidth, uint? replacementFill)
    {
        original = draft;
        originalFill = fill = replacementFill;
        maximumWidth = Math.Max(1, availableWidth);
        bool narrow = root.Size.Width < 540;
        var names = EditorSession.GetInstalledTextFonts().ToList();
        if (!names.Contains(draft.FontFamily, StringComparer.CurrentCultureIgnoreCase))
            names.Insert(0, draft.FontFamily);
        font = new ComboBox { Header = "Font", ItemsSource = names, HorizontalAlignment = HorizontalAlignment.Stretch };
        size = Number("Size (pt)", 0.75, 768, draft.FontSize * 0.75);
        width = Number("Box width (px)", 1, maximumWidth,
            Math.Clamp(draft.LayoutWidth > 0 ? draft.LayoutWidth : Math.Min(320, maximumWidth), 1, maximumWidth));
        before = Number("Before (pt)", 0, 120, 0);
        after = Number("After (pt)", 0, 120, 0);
        spacing = new ComboBox { Header = "Line spacing", ItemsSource = new[] { "1.0", "1.15", "1.5", "2.0" },
            HorizontalAlignment = HorizontalAlignment.Stretch };

        editor = new RichEditBox
        {
            AcceptsReturn = true,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            Height = 196,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(ToColor(replacementFill ?? 0xFFFFFFFF)),
            Foreground = new SolidColorBrush(ToColor(draft.Argb)),
            FontFamily = new FontFamily(draft.FontFamily),
            FontSize = draft.FontSize
        };
        editor.Resources["TextControlBackgroundFocused"] = editor.Background;
        editor.Resources["TextControlBackgroundPointerOver"] = editor.Background;
        AutomationProperties.SetName(editor, "Text draft");
        SetEditorWidth(root.Size.Width);

        var styles = Row();
        styles.Children.Add(Style("B", "Bold", f => f.Bold == FormatEffect.On, (f, on) => f.Bold = Effect(on)));
        styles.Children.Add(Style("I", "Italic", f => f.Italic == FormatEffect.On, (f, on) => f.Italic = Effect(on)));
        styles.Children.Add(Style("U", "Underline", f => f.Underline == UnderlineType.Single,
            (f, on) => f.Underline = on ? UnderlineType.Single : UnderlineType.None));
        styles.Children.Add(Style("S", "Strikethrough", f => f.Strikethrough == FormatEffect.On, (f, on) => f.Strikethrough = Effect(on)));
        styles.Children.Add(Style("x²", "Superscript", f => f.Superscript == FormatEffect.On, (f, on) => f.Superscript = Effect(on)));
        styles.Children.Add(Style("x₂", "Subscript", f => f.Subscript == FormatEffect.On, (f, on) => f.Subscript = Effect(on)));

        var colors = Row();
        colors.Children.Add(ColorButton("Text color", ToColor(draft.Argb), c => ApplyCharacter(p => p.ForegroundColor = c), out ink));
        colors.Children.Add(ColorButton("Highlight", ToColor(0xFFFFE877), c => ApplyCharacter(p => p.BackgroundColor = c), out highlight));
        var reset = narrow ? Row() : colors;
        reset.Children.Add(Command("Clear highlight", () => ApplyCharacter(p => p.BackgroundColor = TextConstants.AutoColor)));
        reset.Children.Add(Command("Reset style", ResetCharacterStyle));

        var alignment = Row();
        AddAlignment(alignment, "Left", ParagraphAlignment.Left);
        AddAlignment(alignment, "Center", ParagraphAlignment.Center);
        AddAlignment(alignment, "Right", ParagraphAlignment.Right);
        AddAlignment(alignment, "Justify", ParagraphAlignment.Justify);

        var lists = Row();
        lists.Children.Add(Command("Bullets", () => SetList(MarkerType.Bullet)));
        lists.Children.Add(Command("Numbering", () => SetList(MarkerType.Arabic)));
        var indent = narrow ? Row() : lists;
        indent.Children.Add(Command("Decrease indent", () => ChangeIndent(-18)));
        indent.Children.Add(Command("Increase indent", () => ChangeIndent(18)));
        var paragraph = new Expander
        {
            Header = "Paragraph",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = narrow ? Stack(alignment, lists, indent, Columns(spacing, before, after))
                : Stack(alignment, lists, Columns(spacing, before, after))
        };
        var box = Stack(width);
        if (replacementFill.HasValue)
            box.Children.Add(ColorButton("Replacement fill", ToColor(replacementFill.Value), color =>
            {
                fill = Argb(color) | 0xFF000000;
                editor.Background = new SolidColorBrush(color);
                editor.Resources["TextControlBackgroundFocused"] = editor.Background;
                editor.Resources["TextControlBackgroundPointerOver"] = editor.Background;
                editor.Focus(FocusState.Programmatic);
            }, out _));
        var frame = new Expander
        {
            Header = "Text box",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = box
        };

        var edits = Row();
        edits.Children.Add(Command("Undo", () => ChangeHistory(undo: true)));
        edits.Children.Add(Command("Redo", () => ChangeHistory(undo: false)));
        edits.Children.Add(Command("Cut", () => CopySelection(cut: true)));
        var clipboard = narrow ? Row() : edits;
        clipboard.Children.Add(Command("Copy", () => CopySelection(cut: false)));
        clipboard.Children.Add(Command("Paste", () => _ = PasteAsync()));
        clipboard.Children.Add(Command("Select all", () =>
        {
            editor.Document.Selection.SetRange(0, ReadText().Length);
            editor.Focus(FocusState.Programmatic);
        }));

        var content = narrow ? Stack(Columns(font, size), styles, colors, reset, editor, edits, clipboard, paragraph, frame, feedback)
            : Stack(Columns(font, size), styles, colors, editor, edits, paragraph, frame, feedback);
        dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = replacementFill.HasValue ? "Advanced text · source line" : "Advanced text",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            Content = new ScrollViewer { Content = content, MaxHeight = Math.Clamp(root.Size.Height - 170, 128, 590) },
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Resources["ContentDialogMaxWidth"] = Math.Clamp(root.Size.Width - 24, 320, 676);
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !TryReadResult();
        dialog.Opened += (_, _) => InitializeEditor(start, end);

        font.SelectionChanged += (_, _) =>
        {
            if (!syncing && font.SelectedItem is string family) ApplyCharacter(p => p.Name = family);
        };
        size.ValueChanged += (_, args) =>
        {
            if (!syncing && double.IsFinite(args.NewValue)) ApplyCharacter(p => p.Size = (float)args.NewValue);
        };
        width.ValueChanged += (_, _) =>
        {
            widthChanged = true;
            SetEditorWidth(root.Size.Width);
        };
        before.ValueChanged += (_, args) =>
        {
            if (!syncing && double.IsFinite(args.NewValue)) ApplyParagraph(p => p.SpaceBefore = (float)args.NewValue);
        };
        after.ValueChanged += (_, args) =>
        {
            if (!syncing && double.IsFinite(args.NewValue)) ApplyParagraph(p => p.SpaceAfter = (float)args.NewValue);
        };
        spacing.SelectionChanged += (_, _) =>
        {
            if (!syncing && spacing.SelectedIndex >= 0)
            {
                float multiple = spacing.SelectedIndex switch { 1 => 1.15f, 2 => 1.5f, 3 => 2f, _ => 1f };
                ApplyParagraph(p => p.SetLineSpacing(LineSpacingRule.Multiple, multiple));
            }
        };
        editor.SelectionChanged += (_, _) => SyncTools();
        editor.CopyingToClipboard += (_, args) => { args.Handled = true; CopySelection(cut: false); };
        editor.CuttingToClipboard += (_, args) => { args.Handled = true; CopySelection(cut: true); };
        editor.Paste += (_, args) => { args.Handled = true; _ = PasteAsync(); };
    }

    internal static async Task<Result?> ShowAsync(XamlRoot root, TextEditDraft draft, int start, int end,
        float maxWidth, uint? replacementFill)
    {
        var view = new AdvancedTextDialog(root, draft, start, end, maxWidth, replacementFill);
        return await view.dialog.ShowAsync() == ContentDialogResult.Primary ? view.result : null;
    }

    private void InitializeEditor(int start, int end)
    {
        syncing = true;
        try
        {
            editor.Document.SetText(TextSetOptions.FormatRtf | TextSetOptions.ApplyRtfDocumentDefaults, original.Rtf);
            initialText = ReadText();
            editor.Document.GetText(TextGetOptions.FormatRtf, out initialRtf);
            editor.Document.ClearUndoRedoHistory();
            editor.Focus(FocusState.Programmatic);
            editor.Document.Selection.SetRange(Math.Clamp(start, 0, initialText.Length), Math.Clamp(end, 0, initialText.Length));
        }
        finally { syncing = false; }
        SyncTools();
    }

    private bool TryReadResult()
    {
        string plain = ReadText();
        editor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
        if (plain.Length > MaxCharacters)
            return ShowFeedback("Keep this text note within 16,000 characters.");
        if (widthChanged && (!double.IsFinite(width.Value) || width.Value < 1 || width.Value > maximumWidth))
            return ShowFeedback($"Enter a box width from 1 to {maximumWidth:0.##} px.");
        if (originalFill.HasValue && string.IsNullOrWhiteSpace(plain))
            return ShowFeedback("Enter text to replace this source line.");
        try { EditorSession.ValidateFormattedText(rtf); }
        catch (ArgumentException error) { return ShowFeedback(error.Message); }

        var edited = original;
        if (plain != initialText || rtf != initialRtf)
        {
            ITextCharacterFormat first = editor.Document.GetRange(0, Math.Min(1, plain.Length)).CharacterFormat;
            edited = edited with
            {
                Text = plain,
                Rtf = rtf,
                FontFamily = string.IsNullOrWhiteSpace(first.Name) ? original.FontFamily : first.Name,
                FontSize = float.IsFinite(first.Size) && first.Size > 0 ? Math.Clamp(first.Size / 0.75f, 1, 1024) : original.FontSize,
                Argb = first.ForegroundColor.A == 255 ? Argb(first.ForegroundColor) : original.Argb,
                Style = (first.Bold == FormatEffect.On ? TextFontStyle.Bold : 0)
                    | (first.Italic == FormatEffect.On ? TextFontStyle.Italic : 0)
                    | (first.Underline != UnderlineType.None && first.Underline != UnderlineType.Undefined ? TextFontStyle.Underline : 0)
                    | (first.Strikethrough == FormatEffect.On ? TextFontStyle.Strikeout : 0)
            };
        }
        edited = edited with
        {
            LayoutWidth = widthChanged ? (float)width.Value : original.LayoutWidth,
            ReplacementBackground = fill != originalFill ? fill : original.ReplacementBackground
        };
        var selection = editor.Document.Selection;
        result = new Result(edited, selection.StartPosition, selection.EndPosition);
        return true;
    }

    private void SyncTools()
    {
        if (syncing) return;
        syncing = true;
        try
        {
            var selection = editor.Document.Selection;
            ITextCharacterFormat ch = selection.CharacterFormat;
            ITextParagraphFormat para = selection.ParagraphFormat;
            font.SelectedItem = ((IEnumerable<string>)font.ItemsSource)
                .FirstOrDefault(name => name.Equals(ch.Name, StringComparison.CurrentCultureIgnoreCase));
            size.Value = float.IsFinite(ch.Size) && ch.Size is >= 0.75f and <= 768 ? ch.Size : double.NaN;
            foreach (var style in styleButtons) style.Button.IsChecked = style.IsOn(ch);
            if (ch.ForegroundColor.A == 255) ink.Color = ch.ForegroundColor;
            if (ch.BackgroundColor.A == 255) highlight.Color = ch.BackgroundColor;
            before.Value = para.SpaceBefore is >= 0 and <= 120 ? para.SpaceBefore : double.NaN;
            after.Value = para.SpaceAfter is >= 0 and <= 120 ? para.SpaceAfter : double.NaN;
            var lineSpacing = para.LineSpacing;
            spacing.SelectedIndex = para.LineSpacingRule switch
            {
                LineSpacingRule.Single => 0,
                LineSpacingRule.OneAndHalf => 2,
                LineSpacingRule.Double => 3,
                LineSpacingRule.Multiple when Math.Abs(lineSpacing - 1) < .01f => 0,
                LineSpacingRule.Multiple when Math.Abs(lineSpacing - 1.15f) < .01f => 1,
                LineSpacingRule.Multiple when Math.Abs(lineSpacing - 1.5f) < .01f => 2,
                LineSpacingRule.Multiple when Math.Abs(lineSpacing - 2) < .01f => 3,
                _ => -1
            };
        }
        finally { syncing = false; }
    }

    private void ApplyCharacter(Action<ITextCharacterFormat> apply)
    {
        var selection = editor.Document.Selection;
        var format = selection.CharacterFormat;
        apply(format);
        selection.CharacterFormat = format;
        editor.Focus(FocusState.Programmatic);
        SyncTools();
    }

    private void ApplyParagraph(Action<ITextParagraphFormat> apply)
    {
        var selection = editor.Document.Selection;
        var format = selection.ParagraphFormat;
        apply(format);
        selection.ParagraphFormat = format;
        editor.Focus(FocusState.Programmatic);
        SyncTools();
    }

    private void ResetCharacterStyle() => ApplyCharacter(p =>
    {
        p.Name = original.FontFamily;
        p.Size = original.FontSize * .75f;
        p.ForegroundColor = ToColor(original.Argb);
        p.BackgroundColor = TextConstants.AutoColor;
        p.Bold = Effect(original.Style.HasFlag(TextFontStyle.Bold));
        p.Italic = Effect(original.Style.HasFlag(TextFontStyle.Italic));
        p.Underline = original.Style.HasFlag(TextFontStyle.Underline) ? UnderlineType.Single : UnderlineType.None;
        p.Strikethrough = Effect(original.Style.HasFlag(TextFontStyle.Strikeout));
        p.Superscript = p.Subscript = FormatEffect.Off;
    });

    private void AddAlignment(StackPanel panel, string label, ParagraphAlignment alignment) =>
        panel.Children.Add(Command(label, () => ApplyParagraph(p => p.Alignment = alignment)));

    private void SetList(MarkerType marker) => ApplyParagraph(p =>
    {
        p.ListType = p.ListType == marker ? MarkerType.None : marker;
        if (p.ListType == MarkerType.Arabic) p.ListStyle = MarkerStyle.Period;
    });

    private void ChangeIndent(int delta) => ApplyParagraph(p =>
    {
        float startingIndent = RegularIndent(p.LeftIndent);
        p.SetIndents(RegularIndent(p.FirstLineIndent), Math.Clamp(startingIndent + delta, 0, 720), RegularIndent(p.RightIndent));
    });

    private static float RegularIndent(float value) => value is >= -720 and <= 720 ? value : 0;

    private void ChangeHistory(bool undo)
    {
        if (undo && editor.Document.CanUndo()) editor.Document.Undo();
        if (!undo && editor.Document.CanRedo()) editor.Document.Redo();
        editor.Focus(FocusState.Programmatic);
        SyncTools();
    }

    private void CopySelection(bool cut)
    {
        var selection = editor.Document.Selection;
        if (selection.StartPosition == selection.EndPosition) return;
        try
        {
            selection.GetText(TextGetOptions.UseCrlf, out string plain);
            selection.GetText(TextGetOptions.FormatRtf, out string rtf);
            if (DesktopTextClipboard.Copy(plain, rtf: rtf) && cut) selection.SetText(TextSetOptions.None, "");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            ShowFeedback("Could not copy selected text. " + error.Message);
        }
        editor.Focus(FocusState.Programmatic);
    }

    private async Task PasteAsync()
    {
        var selection = editor.Document.Selection;
        int start = selection.StartPosition;
        int end = selection.EndPosition;
        string originalText = ReadText();
        try
        {
            var clipboard = Clipboard.GetContent();
            string? rtf = null, plain = null;
            if (clipboard.Contains(StandardDataFormats.Rtf))
            {
                string formatted = await clipboard.GetRtfAsync();
                try { plain = EditorSession.ReadFormattedText(formatted); rtf = formatted; }
                catch (ArgumentException) { }
            }
            if (plain is null && clipboard.Contains(StandardDataFormats.Text)) plain = await clipboard.GetTextAsync();
            if (plain is null) { ShowFeedback("The clipboard contains no supported text."); return; }
            if (originalText.Length - Math.Abs(end - start) + NormalizedText(plain).Length > MaxCharacters)
            { ShowFeedback("This paste would exceed 16,000 characters."); return; }
            if (!editor.IsLoaded || selection.StartPosition != start || selection.EndPosition != end || ReadText() != originalText) return;
            selection.SetText(rtf is null ? TextSetOptions.None : TextSetOptions.FormatRtf, rtf ?? plain);
            editor.Focus(FocusState.Programmatic);
            feedback.IsOpen = false;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { ShowFeedback("Could not paste text. " + error.Message); }
    }

    private string ReadText()
    {
        editor.Document.GetText(TextGetOptions.None, out string text);
        return NormalizedText(text.EndsWith('\r') ? text[..^1] : text);
    }

    private static string NormalizedText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private bool ShowFeedback(string message)
    {
        feedback.Message = message;
        feedback.IsOpen = true;
        feedback.StartBringIntoView();
        return false;
    }

    private void SetEditorWidth(double hostWidth)
    {
        double accessibleWidth = Math.Clamp(hostWidth - 100, 180, 586);
        double previewWidth = double.IsFinite(width.Value) ? width.Value : 320;
        editor.Width = Math.Clamp(previewWidth, Math.Min(140, accessibleWidth), accessibleWidth);
    }

    private ToggleButton Style(string label, string meaning, Func<ITextCharacterFormat, bool> read,
        Action<ITextCharacterFormat, bool> apply)
    {
        var button = new ToggleButton { Content = label, MinWidth = 36 };
        AutomationProperties.SetName(button, meaning);
        ToolTipService.SetToolTip(button, meaning);
        button.Click += (_, _) => ApplyCharacter(p => apply(p, button.IsChecked == true));
        styleButtons.Add((button, read));
        return button;
    }

    private static Button ColorButton(string label, Color initial, Action<Color> onApply, out ColorPicker picker)
    {
        var color = new ColorPicker { Color = initial, IsAlphaEnabled = false };
        var apply = new Button { Content = "Use color" };
        var flyout = new Flyout { Content = Stack(color, apply) };
        apply.Click += (_, _) => { flyout.Hide(); onApply(color.Color); };
        picker = color;
        return new Button { Content = label, Flyout = flyout };
    }

    private static Button Command(string label, Action invoke)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => invoke();
        return button;
    }

    private static NumberBox Number(string label, double minimum, double maximum, double value) =>
        new()
        {
            Header = label,
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

    private static StackPanel Stack(params UIElement[] controls)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 6 };

    private static Grid Columns(params FrameworkElement[] controls)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (int index = 0; index < controls.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(controls.Length == 2 && index == 0 ? 2 : 1, GridUnitType.Star) });
            Grid.SetColumn(controls[index], index);
            grid.Children.Add(controls[index]);
        }
        return grid;
    }

    private static FormatEffect Effect(bool on) => on ? FormatEffect.On : FormatEffect.Off;
    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    private static uint Argb(Color color) => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
