using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    // Dialog state is local until the caller accepts it. Document changes and
    // undo history belong to EditorCanvas/EditorDocument, never to this form.
    internal sealed class TextEditorDialog : Form
    {
        private readonly TextBox entry;
        private readonly NumericUpDown fontSize;
        private readonly NumericUpDown boxWidth;
        private readonly Button textColorButton;
        private readonly Button backgroundColorButton;
        private readonly Label description;
        private readonly Label guidance;
        private readonly Panel body;
        private readonly TableLayoutPanel layout;
        private readonly FlowLayoutPanel options;
        private readonly int availableWidth;
        private Color textColor;
        private Color backgroundColor;
        private bool updatingMetrics;
        private bool reflowing;
        private int initialSelectionStart = -1;
        private int initialSelectionLength;

        public string EnteredText { get { return entry.Text; } }
        public float TextSize { get { return (float)fontSize.Value; } }
        public float BoxWidth { get { return (float)boxWidth.Value; } }
        public Color TextColor { get { return textColor; } }
        public Color BackgroundColor { get { return backgroundColor; } }

        internal TextEditorDialog(string title, string initialText, float initialFontSize,
            float initialBoxWidth, int maximumWidth, Color foreground, Color background, bool showBackground)
        {
            availableWidth = Math.Max(1, maximumWidth);
            textColor = foreground;
            backgroundColor = background;
            Text = title ?? "Edit text";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f);
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            ClientSize = new Size(650, 580);
            MinimumSize = new Size(560, 440);
            KeyPreview = true;
            MainForm.TrySetAppIcon(this);

            body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, BackColor = StudioTheme.Surface };
            Controls.Add(body);
            layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Top;
            layout.AutoSize = true;
            layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.Padding = new Padding(24, 20, 24, 20);
            layout.Margin = Padding.Empty;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(layout);

            TableLayoutPanel heading = new TableLayoutPanel();
            heading.AutoSize = true;
            heading.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            heading.Dock = DockStyle.Fill;
            heading.ColumnCount = 1;
            heading.Margin = new Padding(0, 0, 0, 14);
            Label titleLabel = Body(Text, StudioTheme.Text);
            titleLabel.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
            titleLabel.Margin = new Padding(0, 0, 0, 5);
            heading.Controls.Add(titleLabel);
            description = Body(showBackground
                ? "Change the recognized words, or clear the text to remove this line."
                : "Write your note. Use a narrower box to wrap it onto more lines.", StudioTheme.Muted);
            heading.Controls.Add(description);
            layout.Controls.Add(heading, 0, 0);

            Label textLabel = Body("Text", StudioTheme.Text);
            textLabel.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(textLabel, 0, 1);
            entry = new TextBox();
            entry.Multiline = true;
            entry.WordWrap = true;
            entry.AcceptsReturn = true;
            entry.AcceptsTab = false;
            entry.ScrollBars = ScrollBars.Vertical;
            entry.BorderStyle = BorderStyle.FixedSingle;
            entry.Dock = DockStyle.Fill;
            entry.Margin = new Padding(0, 0, 0, 16);
            entry.MaxLength = 16000;
            entry.Text = initialText ?? "";
            entry.Font = new Font("Segoe UI", 11f);
            entry.BackColor = StudioTheme.Input;
            entry.ForeColor = StudioTheme.Text;
            entry.AccessibleName = showBackground ? "Recognized line text" : "Annotation text";
            layout.Controls.Add(entry, 0, 2);

            options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.AutoSize = true;
            options.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            options.WrapContents = true;
            options.Margin = new Padding(0, 0, 0, 12);
            decimal maximumFontSize = Math.Max(8, Math.Min(160, Math.Floor(availableWidth / 1.5m * 10) / 10));
            fontSize = Number("Text size in image pixels", 8, maximumFontSize);
            fontSize.DecimalPlaces = 1;
            fontSize.Increment = 1;
            fontSize.Value = Clamp(initialFontSize, fontSize.Minimum, fontSize.Maximum, 20);
            boxWidth = Number("Text box width in image pixels", 1, availableWidth);
            boxWidth.Increment = 10;
            boxWidth.Value = Clamp(initialBoxWidth, 1, availableWidth, Math.Min(360, availableWidth));
            AddOption("Size (px)", fontSize, 108);
            AddOption("Box width (px)", boxWidth, 142);
            textColorButton = ColorButton("Choose text color", delegate { PickColor(false); });
            AddOption("Text color", textColorButton, 132);
            backgroundColorButton = ColorButton("Choose replacement background color", delegate { PickColor(true); });
            if (showBackground) AddOption("Background fill", backgroundColorButton, 132);
            layout.Controls.Add(options, 0, 3);

            guidance = Body(showBackground
                ? "The original line is covered with the chosen background fill. Font size is estimated; you can adjust it. Undo restores the original."
                : "After applying, drag a side handle to make the box wider or narrower. Text wraps and the box grows in height without shrinking the font.", StudioTheme.Muted);
            guidance.Margin = new Padding(0, 0, 0, 14);
            layout.Controls.Add(guidance, 0, 4);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Bottom;
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Margin = Padding.Empty;
            actions.Padding = new Padding(24, 12, 24, 20);
            StudioButton apply = ActionButton("Apply", true);
            apply.DialogResult = DialogResult.OK;
            StudioButton cancel = ActionButton("Cancel", false);
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(apply);
            actions.Controls.Add(cancel);
            Controls.Add(actions);
            AcceptButton = apply;
            CancelButton = cancel;

            fontSize.ValueChanged += delegate { ConstrainBoxWidth(); };
            ConstrainBoxWidth();
            UpdateColorButton(textColorButton, textColor);
            UpdateColorButton(backgroundColorButton, backgroundColor);
            StudioTheme.ApplyTo(this);
            layout.SizeChanged += delegate { ReflowHelp(); };
            body.SizeChanged += delegate { ReflowHelp(); };
            Shown += delegate
            {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                int maximumFormWidth = Math.Max(320, work.Width - 32);
                int maximumFormHeight = Math.Max(280, work.Height - 32);
                MinimumSize = new Size(Math.Min(MinimumSize.Width, maximumFormWidth), Math.Min(MinimumSize.Height, maximumFormHeight));
                Size = new Size(Math.Min(Width, maximumFormWidth), Math.Min(Height, maximumFormHeight));
                ReflowHelp();
                entry.Focus();
                if (initialSelectionStart >= 0) entry.Select(initialSelectionStart, initialSelectionLength);
                else entry.Select(entry.TextLength, 0);
                entry.ScrollToCaret();
            };
            ReflowHelp();
        }

        private static Label Body(string value, Color color)
        {
            return new Label { Text = value, AutoSize = true, ForeColor = color, Margin = Padding.Empty, UseMnemonic = false };
        }

        private static NumericUpDown Number(string accessibleName, decimal minimum, decimal maximum)
        {
            return new ExplicitNumericUpDown
            {
                AccessibleName = accessibleName,
                Minimum = minimum,
                Maximum = maximum,
                AutoSize = false,
                Width = 110,
                Height = 30,
                ThousandsSeparator = false,
                BackColor = StudioTheme.Input,
                ForeColor = StudioTheme.Text,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private void AddOption(string caption, Control control, int logicalWidth)
        {
            TableLayoutPanel field = new TableLayoutPanel();
            field.ColumnCount = 1;
            field.RowCount = 2;
            field.AutoSize = true;
            field.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            field.Margin = new Padding(0, 0, 12, 8);
            Label label = Body(caption, StudioTheme.Text);
            label.Margin = new Padding(0, 0, 0, 6);
            field.Controls.Add(label, 0, 0);
            control.Width = logicalWidth;
            control.Height = 32;
            control.Margin = Padding.Empty;
            field.Controls.Add(control, 0, 1);
            options.Controls.Add(field);
        }

        private static Button ColorButton(string accessibleName, EventHandler click)
        {
            Button button = new Button();
            button.Tag = "PreserveColor";
            button.AccessibleName = accessibleName;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.Click += click;
            return button;
        }

        private static StudioButton ActionButton(string text, bool primary)
        {
            return new StudioButton
            {
                Text = text,
                AccessibleName = text,
                Primary = primary,
                Size = new Size(104, 38),
                Margin = new Padding(8, 0, 0, 0)
            };
        }

        private void PickColor(bool background)
        {
            using (ColorPaletteDialog picker = new ColorPaletteDialog(background ? backgroundColor : textColor, new Color[0]))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (background) { backgroundColor = picker.SelectedColor; UpdateColorButton(backgroundColorButton, backgroundColor); }
                else { textColor = picker.SelectedColor; UpdateColorButton(textColorButton, textColor); }
            }
        }

        private static void UpdateColorButton(Button button, Color value)
        {
            Color opaque = Color.FromArgb(value.R, value.G, value.B);
            button.BackColor = opaque;
            double light = (0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B));
            button.ForeColor = light > 0.179 ? Color.Black : Color.White;
            button.Text = "#" + value.R.ToString("X2") + value.G.ToString("X2") + value.B.ToString("X2");
            button.FlatAppearance.BorderColor = StudioTheme.Border;
        }

        private static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private void ConstrainBoxWidth()
        {
            if (updatingMetrics) return;
            updatingMetrics = true;
            try
            {
                decimal minimum = Math.Min(availableWidth, Math.Max(24, Math.Ceiling(fontSize.Value * 1.5m)));
                if (boxWidth.Value < minimum) boxWidth.Value = minimum;
                boxWidth.Minimum = minimum;
            }
            finally { updatingMetrics = false; }
        }

        private void ReflowHelp()
        {
            if (reflowing) return;
            reflowing = true;
            try
            {
                // Reserve scrollbar space even before it is visible. This keeps
                // wrapping stable as the body becomes scrollable on a small screen.
                int bodyWidth = Math.Max(160, body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                Size bodyConstraint = new Size(bodyWidth, 0);
                if (layout.MaximumSize != bodyConstraint) layout.MaximumSize = bodyConstraint;
                if (layout.Width != bodyWidth) layout.Width = bodyWidth;
                int contentWidth = Math.Max(120, bodyWidth - layout.Padding.Horizontal);
                Size constraint = new Size(contentWidth, 0);
                if (description.MaximumSize != constraint) description.MaximumSize = constraint;
                if (guidance.MaximumSize != constraint) guidance.MaximumSize = constraint;
                // Bound the auto-sized flow row before measuring its height, so
                // color fields wrap instead of enlarging the table horizontally.
                if (options.MaximumSize != constraint) options.MaximumSize = constraint;
            }
            finally { reflowing = false; }
        }

        private static decimal Clamp(float value, decimal minimum, decimal maximum, decimal fallback)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value)) return Math.Max(minimum, Math.Min(maximum, fallback));
            if (value <= (float)minimum) return minimum;
            if (value >= (float)maximum) return maximum;
            return Math.Max(minimum, Math.Min(maximum, (decimal)value));
        }

        public void SelectTextRange(int start, int length)
        {
            initialSelectionStart = Math.Max(0, Math.Min(entry.TextLength, start));
            initialSelectionLength = Math.Max(0, Math.Min(entry.TextLength - initialSelectionStart, length));
            entry.Select(initialSelectionStart, initialSelectionLength);
            if (entry.IsHandleCreated) entry.ScrollToCaret();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter)) { DialogResult = DialogResult.OK; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private sealed class ExplicitNumericUpDown : NumericUpDown
        {
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }
        }
    }
}
