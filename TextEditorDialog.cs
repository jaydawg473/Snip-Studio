using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    // A local rich-text draft. Only Apply returns it to the document/history.
    internal sealed class TextEditorDialog : Form
    {
        private static string[] installedFontNames;
        private readonly RichTextDraftBox entry;
        private readonly ComboBox family;
        private readonly NumericUpDown pointSize, boxWidth, beforeSpace, afterSpace;
        private readonly ComboBox lineSpacing;
        private readonly Button inkButton, highlightButton, backgroundButton;
        private readonly StudioButton bold, italic, underline, strike, superscript, subscript;
        private readonly StudioButton alignLeft, alignCenter, alignRight, alignJustify, bullets, numbering;
        private readonly Panel body, page;
        private readonly TableLayoutPanel layout;
        private readonly FlowLayoutPanel characterTools, paragraphTools, documentTools, editingTools;
        private readonly Label description, guidance, previewScale;
        private readonly ToolTip tips = TooltipService.RegisterProvider(new ToolTip());
        private readonly int availableWidth;
        private readonly string sourceFamily;
        private readonly float sourceSizePixels;
        private readonly Color sourceInk;
        private Color backgroundColor;
        private Color lastHighlight = Color.FromArgb(255, 232, 120);
        private bool updating, reflowing;
        private int initialSelectionStart = -1, initialSelectionLength;

        public string EnteredText { get { return entry.Text; } }
        public string Rtf { get { return entry.Rtf; } }
        public float TextSize { get { return entry.TextLength > 0 ? RichTextLayout.GetFirstFontSize(entry.Rtf) : (float)pointSize.Value * 96f / 72f; } }
        public float BoxWidth { get { return (float)boxWidth.Value; } }
        public Color TextColor { get { return entry.TextLength > 0 ? RichTextLayout.GetFirstColor(entry.Rtf) : sourceInk; } }
        public Color BackgroundColor { get { return backgroundColor; } }

        internal TextEditorDialog(string title, string initialText, float initialFontSize,
            float initialBoxWidth, int maximumWidth, Color foreground, Color background, bool showBackground)
            : this(title, initialText, initialFontSize, initialBoxWidth, maximumWidth, foreground, background, showBackground, null) { }

        internal TextEditorDialog(string title, string initialText, float initialFontSize,
            float initialBoxWidth, int maximumWidth, Color foreground, Color background, bool showBackground, string initialRtf)
        {
            availableWidth = Math.Max(12, maximumWidth);
            backgroundColor = Color.FromArgb(255, background);
            sourceInk = Color.FromArgb(255, foreground);
            Text = title ?? "Edit text";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1020, 720);
            MinimumSize = new Size(620, 500);
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            KeyPreview = true;
            MainForm.TrySetAppIcon(this);

            entry = new RichTextDraftBox();
            entry.Tag = "PreserveColor";
            entry.BorderStyle = BorderStyle.None;
            entry.Multiline = true;
            entry.WordWrap = true;
            entry.AcceptsTab = true;
            entry.DetectUrls = false;
            entry.AllowDrop = false;
            entry.HideSelection = false;
            entry.AutoWordSelection = false;
            entry.ScrollBars = RichTextBoxScrollBars.Vertical;
            entry.MaxLength = 16000;
            entry.BackColor = backgroundColor;
            entry.ForeColor = sourceInk;
            entry.AccessibleName = showBackground ? "Recognized text document" : "Text note document";
            float initialPixels = Math.Max(1, Math.Min(1024, initialFontSize));
            entry.Font = new Font("Segoe UI", initialPixels * 72f / 96f, FontStyle.Regular, GraphicsUnit.Point);
            if (!String.IsNullOrWhiteSpace(initialRtf))
            {
                RichTextDraftBox.ValidateTextRtf(initialRtf);
                entry.Rtf = initialRtf;
            }
            else entry.Rtf = RichTextLayout.CreatePlainRtf(initialText ?? "", "Segoe UI", initialPixels, FontStyle.Regular, sourceInk);
            if (entry.TextLength > 16000) throw new ArgumentException("Text notes are limited to 16,000 characters.", "initialText");
            sourceFamily = RichTextLayout.GetFirstFontFamily(entry.Rtf);
            sourceSizePixels = Math.Max(1, Math.Min(1024, RichTextLayout.GetFirstFontSize(entry.Rtf)));

            body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = StudioTheme.Surface };
            Controls.Add(body);
            layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 7, Padding = new Padding(20, 16, 20, 16), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Absolute : SizeType.AutoSize, i == 5 ? 300 : 0));
            body.Controls.Add(layout);

            TableLayoutPanel heading = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0, 0, 0, 10) };
            Label titleLabel = Body(Text, StudioTheme.Text);
            titleLabel.Font = new Font("Segoe UI", 17f, FontStyle.Bold);
            heading.Controls.Add(titleLabel);
            description = Body(showBackground ? "Edit the recognized line using the document's estimated style. Select words to format just those words."
                : "Write and format your note. Select words for individual formatting, or select all to change the whole note.", StudioTheme.Muted);
            heading.Controls.Add(description);
            layout.Controls.Add(heading, 0, 0);

            characterTools = ToolRow();
            family = new ExplicitComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 212, DropDownWidth = 360, AccessibleName = "Font family" };
            family.Items.Add(sourceFamily);
            family.SelectedIndex = 0;
            Field(characterTools, "Font", family, 212);
            pointSize = Number("Font size in points", 1, 768, 80);
            pointSize.DecimalPlaces = 1;
            pointSize.Value = Clamp(sourceSizePixels * 72f / 96f, pointSize.Minimum, pointSize.Maximum);
            Field(characterTools, "Size (pt)", pointSize, 74);
            FlowLayoutPanel styles = ButtonGroup();
            bold = Small(styles, "B", "Bold (Ctrl+B)", delegate { Toggle(FontStyle.Bold); });
            bold.Font = new Font(Font, FontStyle.Bold);
            italic = Small(styles, "I", "Italic (Ctrl+I)", delegate { Toggle(FontStyle.Italic); });
            italic.Font = new Font(Font, FontStyle.Italic);
            underline = Small(styles, "U", "Underline (Ctrl+U)", delegate { Toggle(FontStyle.Underline); });
            underline.Font = new Font(Font, FontStyle.Underline);
            strike = Small(styles, "S", "Strikethrough", delegate { Toggle(FontStyle.Strikeout); });
            strike.Font = new Font(Font, FontStyle.Strikeout);
            superscript = Small(styles, "x²", "Superscript", delegate { Apply(delegate { RichEditFormatting.SetScript(entry, RichEditFormatting.GetScript(entry) == 1 ? 0 : 1); }); });
            subscript = Small(styles, "x₂", "Subscript", delegate { Apply(delegate { RichEditFormatting.SetScript(entry, RichEditFormatting.GetScript(entry) == -1 ? 0 : -1); }); });
            Field(characterTools, "Style", styles, 232);
            inkButton = ColorButton("Ink", "Choose text color for selected words or the insertion point", delegate { PickInk(); });
            Field(characterTools, "Text color", inkButton, 100);
            highlightButton = ColorButton("Highlight", "Choose a highlight color for selected words", delegate { PickHighlight(); });
            Field(characterTools, "Highlight", highlightButton, 108);
            FlowLayoutPanel clearGroup = ButtonGroup();
            Small(clearGroup, "Clear", "Clear selection formatting and restore the source's base style", delegate
            { Apply(delegate { RichEditFormatting.ClearFormatting(entry, sourceFamily, sourceSizePixels, sourceInk); }); }, 66);
            Small(clearGroup, "No fill", "Remove highlighting from selected words", delegate
            { Apply(delegate { RichEditFormatting.SetHighlightColor(entry, null); }); }, 68);
            Field(characterTools, "Reset", clearGroup, 142);
            layout.Controls.Add(characterTools, 0, 1);

            paragraphTools = ToolRow();
            FlowLayoutPanel align = ButtonGroup();
            alignLeft = Small(align, "Left", "Align selected paragraphs left (Ctrl+L)", delegate { Align(0); }, 48);
            alignCenter = Small(align, "Center", "Center selected paragraphs (Ctrl+E)", delegate { Align(1); }, 60);
            alignRight = Small(align, "Right", "Align selected paragraphs right (Ctrl+R)", delegate { Align(2); }, 52);
            alignJustify = Small(align, "Justify", "Justify selected paragraphs (Ctrl+J)", delegate { Align(3); }, 58);
            Field(paragraphTools, "Alignment", align, 230);
            FlowLayoutPanel lists = ButtonGroup();
            bullets = Small(lists, "Bullets", "Toggle bullet list for selected paragraphs", delegate { Apply(delegate { RichEditFormatting.SetBullets(entry, RichEditFormatting.GetListKind(entry) != 1); }); }, 68);
            numbering = Small(lists, "1. List", "Toggle numbered list for selected paragraphs", delegate { Apply(delegate { RichEditFormatting.SetNumbering(entry, RichEditFormatting.GetListKind(entry) != 2); }); }, 70);
            Field(paragraphTools, "Lists", lists, 146);
            FlowLayoutPanel indentation = ButtonGroup();
            Small(indentation, "−", "Decrease paragraph indent", delegate { Apply(delegate { RichEditFormatting.ChangeIndent(entry, -24); }); }, 36);
            Small(indentation, "+", "Increase paragraph indent", delegate { Apply(delegate { RichEditFormatting.ChangeIndent(entry, 24); }); }, 36);
            Field(paragraphTools, "Indent", indentation, 80);
            lineSpacing = new ExplicitComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, AccessibleName = "Paragraph line spacing" };
            lineSpacing.Items.AddRange(new object[] { "1.0", "1.15", "1.5", "2.0" });
            lineSpacing.SelectedIndex = 0;
            Field(paragraphTools, "Line spacing", lineSpacing, 86);
            beforeSpace = Number("Space before selected paragraphs in points", 0, 120, 72);
            afterSpace = Number("Space after selected paragraphs in points", 0, 120, 72);
            beforeSpace.DecimalPlaces = afterSpace.DecimalPlaces = 1;
            Field(paragraphTools, "Before (pt)", beforeSpace, 82);
            Field(paragraphTools, "After (pt)", afterSpace, 82);
            layout.Controls.Add(paragraphTools, 0, 2);

            documentTools = ToolRow();
            boxWidth = Number("Text box width in image pixels", 12, availableWidth, 126);
            boxWidth.Increment = 10;
            boxWidth.Value = Clamp(initialBoxWidth, 12, availableWidth);
            Field(documentTools, "Box width (px)", boxWidth, 138);
            backgroundButton = ColorButton("Fill", "Choose the solid fill used to cover the original screenshot line", delegate { PickBackground(); });
            if (showBackground) Field(documentTools, "Replacement fill", backgroundButton, 120);
            previewScale = Body("", StudioTheme.Muted);
            previewScale.Margin = new Padding(4, 23, 8, 0);
            documentTools.Controls.Add(previewScale);
            layout.Controls.Add(documentTools, 0, 3);

            editingTools = ToolRow();
            Small(editingTools, "Undo", "Undo the last change in this text draft (Ctrl+Z)", delegate { if (entry.CanUndo) entry.Undo(); entry.Focus(); }, 70);
            Small(editingTools, "Redo", "Redo the last text draft change (Ctrl+Y)", delegate { if (entry.CanRedo) entry.Redo(); entry.Focus(); }, 70);
            Small(editingTools, "Cut", "Cut selected words", delegate { entry.Cut(); entry.Focus(); }, 58);
            Small(editingTools, "Copy", "Copy selected words with their formatting", delegate { entry.Copy(); entry.Focus(); }, 62);
            Small(editingTools, "Paste", "Paste text and supported character formatting; embedded objects are omitted", delegate { entry.PasteTextOnlyContent(); entry.Focus(); }, 64);
            Small(editingTools, "Select all", "Select all words in this text note (Ctrl+A)", delegate { entry.SelectAll(); entry.Focus(); }, 90);
            layout.Controls.Add(editingTools, 0, 4);

            page = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = StudioTheme.Background, Padding = new Padding(12), Margin = new Padding(0, 2, 0, 10) };
            entry.SetBounds(12, 12, 500, 276);
            page.Controls.Add(entry);
            layout.Controls.Add(page, 0, 5);
            guidance = Body(showBackground
                ? "Screenshot pixels do not contain their original font metadata. The estimated font and solid replacement fill are adjustable. Undo in Snip Studio restores the original image."
                : "The note wraps to its image width. Side handles can reshape it after Apply. Keep the note inside the screenshot edges.", StudioTheme.Muted);
            layout.Controls.Add(guidance, 0, 6);

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(20, 12, 20, 16) };
            StudioButton apply = ActionButton("Apply", true);
            apply.Click += delegate { ApplyDraft(); };
            StudioButton cancel = ActionButton("Cancel", false);
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(apply); actions.Controls.Add(cancel);
            Controls.Add(actions);
            AcceptButton = apply; CancelButton = cancel;

            family.SelectedIndexChanged += delegate
            { if (!updating && family.SelectedItem != null) Apply(delegate { RichEditFormatting.SetFontFamily(entry, family.SelectedItem.ToString()); }); };
            pointSize.ValueChanged += delegate
            {
                if (updating) return;
                float pixels = (float)pointSize.Value * 96f / 72f;
                if (pixels * 1.5f > availableWidth)
                {
                    MessageBox.Show(this, "That font size would extend beyond the screenshot's available width. Choose a smaller size or move the text box to a wider area.",
                        "Text size", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshFormatting();
                    return;
                }
                if (boxWidth.Value < (decimal)(pixels * 1.5f)) boxWidth.Value = Math.Min(boxWidth.Maximum, (decimal)Math.Ceiling(pixels * 1.5f));
                Apply(delegate { RichEditFormatting.SetFontSizePixels(entry, pixels); });
            };
            lineSpacing.SelectedIndexChanged += delegate
            {
                float multiple;
                if (!updating && Single.TryParse(lineSpacing.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out multiple))
                    Apply(delegate { RichEditFormatting.SetLineSpacing(entry, multiple); });
            };
            beforeSpace.ValueChanged += delegate { if (!updating) Apply(delegate { RichEditFormatting.SetSpaceBefore(entry, (float)beforeSpace.Value); }); };
            afterSpace.ValueChanged += delegate { if (!updating) Apply(delegate { RichEditFormatting.SetSpaceAfter(entry, (float)afterSpace.Value); }); };
            boxWidth.ValueChanged += delegate { if (!updating) ReflowPage(); };
            entry.SelectionChanged += delegate { RefreshFormatting(); };
            entry.TextChanged += delegate { RefreshFormatting(); };
            page.SizeChanged += delegate { ReflowPage(); };
            body.SizeChanged += delegate { Reflow(); };
            StudioTheme.ApplyTo(this);
            UpdateColorButton(inkButton, sourceInk, "Ink");
            UpdateColorButton(highlightButton, lastHighlight, "Highlight");
            UpdateColorButton(backgroundButton, backgroundColor, "Fill");
            Shown += delegate
            {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(620, Math.Max(320, work.Width - 24)), Math.Min(500, Math.Max(300, work.Height - 24)));
                Size = new Size(Math.Min(Width, work.Width - 24), Math.Min(Height, work.Height - 24));
                LoadFonts();
                Reflow();
                entry.Focus();
                if (initialSelectionStart >= 0) entry.Select(initialSelectionStart, initialSelectionLength);
                else entry.Select(entry.TextLength, 0);
                entry.ScrollToCaret(); RefreshFormatting();
            };
            Reflow();
        }

        private static FlowLayoutPanel ToolRow()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, Margin = new Padding(0, 0, 0, 6), Padding = Padding.Empty };
        }
        private static FlowLayoutPanel ButtonGroup()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        }
        private static Label Body(string text, Color color)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = color, UseMnemonic = false, Margin = new Padding(0, 0, 0, 5) };
        }
        private void Field(FlowLayoutPanel row, string caption, Control control, int width)
        {
            TableLayoutPanel field = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 8, 6) };
            field.Controls.Add(Body(caption, StudioTheme.Muted), 0, 0);
            control.Width = width; control.Height = 32; control.Margin = Padding.Empty;
            field.Controls.Add(control, 0, 1); row.Controls.Add(field);
        }
        private StudioButton Small(FlowLayoutPanel row, string text, string hint, EventHandler click, int width = 34)
        {
            StudioButton button = new StudioButton { Text = text, AccessibleName = hint, Width = width, Height = 32,
                Margin = new Padding(0, 0, 3, 0), Cursor = Cursors.Hand };
            button.Click += click;
            tips.SetToolTip(button, hint);
            row.Controls.Add(button); return button;
        }
        private static StudioButton ActionButton(string text, bool primary)
        {
            return new StudioButton { Text = text, AccessibleName = text, Primary = primary, Size = new Size(108, 38), Margin = new Padding(8, 0, 0, 0) };
        }
        private static NumericUpDown Number(string name, decimal minimum, decimal maximum, int width)
        {
            return new ExplicitNumericUpDown { AccessibleName = name, Minimum = minimum, Maximum = Math.Max(minimum, maximum),
                Width = width, Height = 32, BackColor = StudioTheme.Input, ForeColor = StudioTheme.Text, BorderStyle = BorderStyle.FixedSingle };
        }
        private Button ColorButton(string text, string hint, EventHandler click)
        {
            Button button = new Button { Text = text, AccessibleName = hint, Tag = "PreserveColor", FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand, Height = 32 };
            button.Click += click; tips.SetToolTip(button, hint); return button;
        }
        private void Apply(Action action)
        {
            if (updating) return;
            action(); entry.Focus(); RefreshFormatting();
        }
        private void Toggle(FontStyle style)
        {
            Apply(delegate { RichEditFormatting.SetStyle(entry, style, RichEditFormatting.GetStyleState(entry, style) != true); });
        }
        private void Align(int value) { Apply(delegate { RichEditFormatting.ParagraphAlign(entry, value); }); }
        private void PickInk()
        {
            Color current = entry.SelectionColor.IsEmpty ? sourceInk : entry.SelectionColor;
            using (ColorPaletteDialog palette = new ColorPaletteDialog(current, new Color[0]))
                if (palette.ShowDialog(this) == DialogResult.OK)
                { Apply(delegate { RichEditFormatting.SetTextColor(entry, palette.SelectedColor); }); UpdateColorButton(inkButton, palette.SelectedColor, "Ink"); }
        }
        private void PickHighlight()
        {
            using (ColorPaletteDialog palette = new ColorPaletteDialog(lastHighlight, new Color[0]))
                if (palette.ShowDialog(this) == DialogResult.OK)
                { lastHighlight = palette.SelectedColor; Apply(delegate { RichEditFormatting.SetHighlightColor(entry, lastHighlight); }); UpdateColorButton(highlightButton, lastHighlight, "Highlight"); }
        }
        private void PickBackground()
        {
            using (ColorPaletteDialog palette = new ColorPaletteDialog(backgroundColor, new Color[0]))
                if (palette.ShowDialog(this) == DialogResult.OK)
                { backgroundColor = palette.SelectedColor; entry.BackColor = backgroundColor; UpdateColorButton(backgroundButton, backgroundColor, "Fill"); }
        }
        private static void UpdateColorButton(Button button, Color color, string text)
        {
            Color opaque = Color.FromArgb(255, color);
            double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
            Color contrast = luminance > .179 ? Color.Black : Color.White;
            string hex = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
            if (button.BackColor != opaque) button.BackColor = opaque;
            if (button.ForeColor != contrast) button.ForeColor = contrast;
            if (button.Text != hex) button.Text = hex;
            button.FlatAppearance.BorderColor = StudioTheme.Border;
        }
        private static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        private void RefreshFormatting()
        {
            if (updating || entry.IsDisposed) return;
            updating = true;
            try
            {
                Font selected = entry.SelectionFont;
                if (selected != null)
                {
                    int index = family.Items.IndexOf(selected.FontFamily.Name);
                    if (index < 0) index = family.Items.Add(selected.FontFamily.Name);
                    family.SelectedIndex = index;
                    pointSize.Value = Clamp(selected.SizeInPoints, pointSize.Minimum, pointSize.Maximum);
                }
                else { family.SelectedIndex = -1; pointSize.Text = ""; }
                bold.Selected = RichEditFormatting.GetStyleState(entry, FontStyle.Bold) == true;
                italic.Selected = RichEditFormatting.GetStyleState(entry, FontStyle.Italic) == true;
                underline.Selected = RichEditFormatting.GetStyleState(entry, FontStyle.Underline) == true;
                strike.Selected = RichEditFormatting.GetStyleState(entry, FontStyle.Strikeout) == true;
                superscript.Selected = RichEditFormatting.GetScript(entry) == 1;
                subscript.Selected = RichEditFormatting.GetScript(entry) == -1;
                int? alignment = RichEditFormatting.GetAlignment(entry);
                alignLeft.Selected = alignment == 0; alignCenter.Selected = alignment == 1;
                alignRight.Selected = alignment == 2; alignJustify.Selected = alignment == 3;
                int? list = RichEditFormatting.GetListKind(entry);
                bullets.Selected = list == 1; numbering.Selected = list == 2;
                float? before = RichEditFormatting.GetSpaceBefore(entry), after = RichEditFormatting.GetSpaceAfter(entry);
                if (before.HasValue) beforeSpace.Value = Clamp(before.Value, beforeSpace.Minimum, beforeSpace.Maximum); else beforeSpace.Text = "";
                if (after.HasValue) afterSpace.Value = Clamp(after.Value, afterSpace.Minimum, afterSpace.Maximum); else afterSpace.Text = "";
                float? multiple = RichEditFormatting.GetLineSpacing(entry);
                int spacingIndex = -1;
                if (multiple.HasValue)
                    for (int i = 0; i < 4; i++) if (Math.Abs(Single.Parse(lineSpacing.Items[i].ToString(), CultureInfo.InvariantCulture) - multiple.Value) < .01f) spacingIndex = i;
                if (spacingIndex < 0)
                {
                    if (lineSpacing.Items.Count == 4) lineSpacing.Items.Add("Custom / mixed");
                    spacingIndex = 4;
                }
                lineSpacing.SelectedIndex = spacingIndex;
                Color selectedInk = entry.SelectionColor;
                if (!selectedInk.IsEmpty) UpdateColorButton(inkButton, selectedInk, "Ink");
            }
            finally { updating = false; }
        }
        private void LoadFonts()
        {
            if (installedFontNames == null)
            {
                List<string> names = new List<string>();
                foreach (FontFamily font in FontFamily.Families)
                {
                    using (font) if (!names.Contains(font.Name)) names.Add(font.Name);
                }
                names.Sort(StringComparer.CurrentCultureIgnoreCase);
                installedFontNames = names.ToArray();
            }
            updating = true;
            try
            {
                string current = family.Text;
                family.BeginUpdate(); family.Items.Clear(); family.Items.AddRange(installedFontNames);
                if (!family.Items.Contains(sourceFamily)) family.Items.Add(sourceFamily);
                int at = family.Items.IndexOf(current); family.SelectedIndex = at >= 0 ? at : family.Items.IndexOf(sourceFamily);
                family.EndUpdate();
            }
            finally { updating = false; }
        }
        private void Reflow()
        {
            if (reflowing || layout == null || body == null) return;
            reflowing = true;
            try
            {
                int width = Math.Max(300, body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                layout.MaximumSize = new Size(width, 0); layout.Width = width;
                Size constraint = new Size(Math.Max(260, width - layout.Padding.Horizontal), 0);
                description.MaximumSize = guidance.MaximumSize = constraint;
                characterTools.MaximumSize = paragraphTools.MaximumSize = documentTools.MaximumSize = editingTools.MaximumSize = constraint;
                ReflowPage();
            }
            finally { reflowing = false; }
        }
        private void ReflowPage()
        {
            if (page == null || entry == null || page.ClientSize.Width <= 24) return;
            float dpiScale = 1;
            if (IsHandleCreated) using (Graphics graphics = CreateGraphics()) dpiScale = graphics.DpiX / 96f;
            float logicalWidth = Math.Max(12, (float)boxWidth.Value);
            float factor = Math.Max(.05f, Math.Min(1f, (page.ClientSize.Width - 28) / Math.Max(1f, logicalWidth * dpiScale)));
            int previewWidth = Math.Max(12, Math.Min(page.ClientSize.Width - 24, (int)Math.Round(logicalWidth * dpiScale * factor)));
            entry.RightMargin = Math.Max(1, (int)Math.Round(logicalWidth * dpiScale));
            if (Math.Abs(entry.ZoomFactor - factor) > .001f) entry.ZoomFactor = factor;
            entry.SetBounds(12, 12, previewWidth, Math.Max(100, page.ClientSize.Height - 24));
            previewScale.Text = "Preview " + Math.Round(factor * 100) + "%  •  Width controls wrapping; font size stays unchanged.";
        }
        private void ApplyDraft()
        {
            if (entry.TextLength > 16000)
            { MessageBox.Show(this, "Keep this text note within 16,000 characters.", "Text note", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { RichTextDraftBox.ValidateTextRtf(entry.Rtf); }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, "Unsupported text content", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            DialogResult = DialogResult.OK;
        }
        public void SelectTextRange(int start, int length)
        {
            initialSelectionStart = Math.Max(0, Math.Min(entry.TextLength, start));
            initialSelectionLength = Math.Max(0, Math.Min(entry.TextLength - initialSelectionStart, length));
            entry.Select(initialSelectionStart, initialSelectionLength);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter)) { ApplyDraft(); return true; }
            if (entry.ContainsFocus)
            {
                if (keyData == (Keys.Control | Keys.B)) { Toggle(FontStyle.Bold); return true; }
                if (keyData == (Keys.Control | Keys.I)) { Toggle(FontStyle.Italic); return true; }
                if (keyData == (Keys.Control | Keys.U)) { Toggle(FontStyle.Underline); return true; }
                if (keyData == (Keys.Control | Keys.L)) { Align(0); return true; }
                if (keyData == (Keys.Control | Keys.E)) { Align(1); return true; }
                if (keyData == (Keys.Control | Keys.R)) { Align(2); return true; }
                if (keyData == (Keys.Control | Keys.J)) { Align(3); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
        private static decimal Clamp(float value, decimal minimum, decimal maximum)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value)) return minimum;
            return Math.Max(minimum, Math.Min(maximum, (decimal)value));
        }
        private sealed class ExplicitNumericUpDown : NumericUpDown
        {
            protected override void OnMouseWheel(MouseEventArgs e)
            { HandledMouseEventArgs handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true; }
        }
        private sealed class ExplicitComboBox : ComboBox
        {
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (DroppedDown) base.OnMouseWheel(e);
                else { HandledMouseEventArgs handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true; }
            }
        }
        private sealed class RichTextDraftBox : RichTextBox
        {
            private const int WmPaste = 0x0302;
            internal static void ValidateTextRtf(string rtf)
            {
                RichTextLayout.ValidateRtf(rtf);
            }
            internal void PasteTextOnlyContent()
            {
                try { PasteSupportedText(); }
                catch (System.Runtime.InteropServices.ExternalException)
                { MessageBox.Show(FindForm(), "The clipboard is busy. Try Paste again.", "Paste text", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }
            private void PasteSupportedText()
            {
                bool hasPlain = Clipboard.ContainsText(TextDataFormat.UnicodeText), hasRich = Clipboard.ContainsText(TextDataFormat.Rtf);
                if (!hasPlain && !hasRich) return;
                string plain = hasPlain ? Clipboard.GetText(TextDataFormat.UnicodeText) : "";
                if (TextLength - SelectionLength + plain.Length > 16000)
                { MessageBox.Show(FindForm(), "This paste would exceed the 16,000-character text limit.", "Paste text", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (hasRich)
                {
                    string rich = Clipboard.GetText(TextDataFormat.Rtf);
                    try
                    {
                        ValidateTextRtf(rich);
                        if (TextLength - SelectionLength + RichTextLayout.GetPlainText(rich).Length <= 16000) { SelectedRtf = rich; return; }
                    }
                    catch (ArgumentException) { }
                }
                if (hasPlain) SelectedText = plain;
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == WmPaste) { PasteTextOnlyContent(); return; }
                if ((message.Msg == 0x0300 || message.Msg == 0x0301) && SelectionLength > 0)
                    MainForm.RegisterClipboardIntent();
                base.WndProc(ref message);
            }
        }
    }
}
