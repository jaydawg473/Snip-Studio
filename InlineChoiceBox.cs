using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // Settings choices occupy normal layout space. Opening a list never creates
    // a popup window or commits the keyboard's highlighted choice.
    internal sealed class InlineChoiceBox : UserControl
    {
        private readonly ChoiceButton header;
        private readonly Panel choices;
        private readonly List<ChoiceButton> buttons = new List<ChoiceButton>();
        private readonly ItemCollection items;
        private int selectedIndex = -1;
        private bool expanded, arranging, rowsDirty = true;
        private int updateDepth;
        private int measuredWidth = -1;
        private int preferredHeight;

        internal InlineChoiceBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            TabStop = false;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
            Size = new Size(240, 38);
            MinimumSize = new Size(80, 0);
            BackColor = StudioTheme.Surface;
            items = new ItemCollection(this);
            header = new ChoiceButton(this, -1);
            header.TabIndex = 0;
            header.Click += delegate { ToggleExpanded(); };
            choices = new Panel { Margin = Padding.Empty, Padding = Padding.Empty, Visible = false, TabStop = false };
            Controls.Add(choices);
            Controls.Add(header);
            AccessibleRole = AccessibleRole.Grouping;
            AccessibleDescription = "Expand the choices below this setting. Select an option to apply it; Escape closes the list without changing the value.";
            RefreshHeader();
            UpdateLayoutSize();
        }

        public ItemCollection Items { get { return items; } }
        public event EventHandler SelectedIndexChanged;
        public event EventHandler ExpansionChanged;

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value < -1 || value >= items.Count) throw new ArgumentOutOfRangeException("value");
                if (selectedIndex == value) return;
                selectedIndex = value;
                RefreshHeader();
                RefreshSelectedRows();
                InvalidateMeasurements();
                EventHandler changed = SelectedIndexChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; }
            set { SelectedIndex = value == null ? -1 : items.IndexOf(value); }
        }

        public bool Expanded
        {
            get { return expanded; }
            set
            {
                bool next = value && Enabled && items.Count > 0;
                if (expanded == next) return;
                expanded = next;
                if (expanded) EnsureRows();
                choices.Visible = expanded;
                header.Invalidate();
                InvalidateMeasurements();
                UpdateLayoutSize();
                if (Parent != null) Parent.PerformLayout();
                EventHandler changed = ExpansionChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }

        // Kept for Settings helpers shared with the former ComboBox. Choices
        // always use this control's available width, not a floating list width.
        public int DropDownWidth { get; set; }
        public ComboBoxStyle DropDownStyle { get; set; }
        public string GetItemText(object item) { return item == null ? "" : Convert.ToString(item); }
        public void BeginUpdate() { updateDepth++; }
        public void EndUpdate()
        {
            if (updateDepth > 0) updateDepth--;
            if (updateDepth == 0) { RefreshHeader(); InvalidateMeasurements(); UpdateLayoutSize(); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 ? proposedSize.Width : Width > 0 ? Width : 240;
            width = Math.Max(MinimumSize.Width, width);
            return new Size(width, MeasureHeight(width));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            UpdateLayoutSize();
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width != measuredWidth) UpdateLayoutSize();
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            InvalidateMeasurements();
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            InvalidateMeasurements();
            UpdateLayoutSize();
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled && expanded) Expanded = false;
            if (header != null) header.Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { ForwardWheel(e); }

        private void ToggleExpanded()
        {
            Expanded = !expanded;
            if (expanded) FocusRow(selectedIndex >= 0 ? selectedIndex : 0);
        }

        private void Commit(int index)
        {
            if (!Enabled || index < 0 || index >= items.Count) return;
            SelectedIndex = index;
            Expanded = false;
            header.Focus();
        }

        private bool HandleChoiceKey(int index, Keys keyData)
        {
            if ((keyData & (Keys.Control | Keys.Alt)) != 0) return false;
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Escape && expanded) { Expanded = false; header.Focus(); return true; }
            if (key == Keys.Enter || key == Keys.Space)
            {
                if (index < 0) ToggleExpanded(); else Commit(index);
                return true;
            }
            if (key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End)
            {
                if (items.Count == 0) return true;
                bool wasOpen = expanded;
                Expanded = true;
                int next = index >= 0 ? index : selectedIndex >= 0 ? selectedIndex : 0;
                if (key == Keys.Home) next = 0;
                else if (key == Keys.End) next = items.Count - 1;
                else if (wasOpen && index >= 0) next += key == Keys.Down ? 1 : -1;
                FocusRow(Math.Max(0, Math.Min(items.Count - 1, next)));
                return true;
            }
            return false;
        }

        private void FocusRow(int index)
        {
            EnsureRows();
            if (index >= 0 && index < buttons.Count) buttons[index].Focus();
        }

        private void EnsureRows()
        {
            if (!rowsDirty) return;
            choices.SuspendLayout();
            try
            {
                foreach (ChoiceButton button in buttons) button.Dispose();
                buttons.Clear();
                choices.Controls.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    int index = i;
                    ChoiceButton button = new ChoiceButton(this, index);
                    button.Text = GetItemText(items[i]);
                    button.TabIndex = i;
                    button.AccessibleName = button.Text;
                    button.AccessibleDescription = "Select this option. Escape closes the list without changing the current setting.";
                    button.Click += delegate { Commit(index); };
                    buttons.Add(button);
                    choices.Controls.Add(button);
                }
                rowsDirty = false;
                RefreshSelectedRows();
            }
            finally { choices.ResumeLayout(false); }
        }

        private void RefreshHeader()
        {
            if (header == null) return;
            header.Text = selectedIndex >= 0 && selectedIndex < items.Count ? GetItemText(items[selectedIndex]) : items.Count == 0 ? "No options available" : "Choose an option";
            header.AccessibleName = String.IsNullOrWhiteSpace(AccessibleName) ? "Setting choices" : AccessibleName;
            header.AccessibleDescription = "Expand the options below this setting; other settings move down. Escape closes without changing the value.";
            header.Invalidate();
        }
        private void RefreshSelectedRows()
        {
            foreach (ChoiceButton button in buttons) { button.Selected = button.Index == selectedIndex; button.Invalidate(); }
        }
        private void InvalidateMeasurements()
        {
            measuredWidth = -1;
            if (updateDepth == 0 && header != null) UpdateLayoutSize();
        }
        private int MeasureHeight(int width)
        {
            if (header == null) return Ui(38);
            if (measuredWidth == width && preferredHeight > 0) return preferredHeight;
            int height = header.MeasureRowHeight(width);
            if (expanded)
            {
                height += Ui(5);
                foreach (object item in items) height += ChoiceButton.MeasureLabelHeight(GetItemText(item), Font, Math.Max(1, width - Ui(46)), Ui(38), Ui(16));
                height += Ui(2);
            }
            measuredWidth = width;
            preferredHeight = height;
            return height;
        }
        private void UpdateLayoutSize()
        {
            if (arranging || header == null || updateDepth > 0) return;
            arranging = true;
            try
            {
                int width = Math.Max(1, ClientSize.Width);
                int headerHeight = header.MeasureRowHeight(width);
                header.SetBounds(0, 0, width, headerHeight);
                int listHeight = 0;
                if (expanded)
                {
                    EnsureRows();
                    foreach (ChoiceButton button in buttons)
                    {
                        int rowHeight = button.MeasureRowHeight(width);
                        button.SetBounds(0, listHeight, width, rowHeight);
                        listHeight += rowHeight;
                    }
                }
                choices.SetBounds(0, headerHeight + Ui(5), width, listHeight);
                choices.Visible = expanded;
                int desired = headerHeight + (expanded ? Ui(5) + listHeight + Ui(2) : 0);
                measuredWidth = width; preferredHeight = desired;
                if (Height != desired) Height = desired;
            }
            finally { arranging = false; }
        }
        private int Ui(int logical) { return (int)Math.Round(logical * (DeviceDpi > 0 ? DeviceDpi / 96f : 1f)); }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        private void ForwardWheel(MouseEventArgs e)
        {
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
            Control current = Parent;
            while (current != null)
            {
                ScrollableControl scroll = current as ScrollableControl;
                if (scroll != null && scroll.AutoScroll && scroll.IsHandleCreated)
                {
                    int keys = (ModifierKeys & Keys.Shift) != 0 ? 4 : 0;
                    SendMessage(scroll.Handle, 0x020A, new IntPtr((e.Delta << 16) | keys), IntPtr.Zero);
                    return;
                }
                current = current.Parent;
            }
        }

        private void ItemsMutated(int at, int removed, int added, bool replaced)
        {
            int oldSelection = selectedIndex;
            if (removed > 0 && selectedIndex >= at && selectedIndex < at + removed) selectedIndex = replaced && added > 0 ? at : -1;
            else if (selectedIndex >= at + removed) selectedIndex += added - removed;
            rowsDirty = true;
            if (items.Count == 0 && expanded) Expanded = false;
            RefreshHeader();
            InvalidateMeasurements();
            if (oldSelection != selectedIndex || (replaced && oldSelection == at))
            { EventHandler changed = SelectedIndexChanged; if (changed != null) changed(this, EventArgs.Empty); }
        }

        public sealed class ItemCollection : IList
        {
            private readonly InlineChoiceBox owner;
            private readonly List<object> values = new List<object>();
            internal ItemCollection(InlineChoiceBox owner) { this.owner = owner; }
            public int Add(object value) { int index = values.Count; values.Add(value); owner.ItemsMutated(index, 0, 1, false); return index; }
            public void AddRange(object[] valuesToAdd) { if (valuesToAdd == null) throw new ArgumentNullException("valuesToAdd"); owner.BeginUpdate(); try { foreach (object value in valuesToAdd) Add(value); } finally { owner.EndUpdate(); } }
            public void Clear() { int old = values.Count; values.Clear(); owner.ItemsMutated(0, old, 0, false); }
            public bool Contains(object value) { return values.Contains(value); }
            public int IndexOf(object value) { return values.IndexOf(value); }
            public void Insert(int index, object value) { values.Insert(index, value); owner.ItemsMutated(index, 0, 1, false); }
            public void Remove(object value) { int index = values.IndexOf(value); if (index >= 0) RemoveAt(index); }
            public void RemoveAt(int index) { values.RemoveAt(index); owner.ItemsMutated(index, 1, 0, false); }
            public object this[int index] { get { return values[index]; } set { values[index] = value; owner.ItemsMutated(index, 1, 1, true); } }
            public int Count { get { return values.Count; } }
            public bool IsReadOnly { get { return false; } }
            public bool IsFixedSize { get { return false; } }
            public bool IsSynchronized { get { return false; } }
            public object SyncRoot { get { return ((ICollection)values).SyncRoot; } }
            public void CopyTo(Array array, int index) { ((ICollection)values).CopyTo(array, index); }
            public IEnumerator GetEnumerator() { return values.GetEnumerator(); }
        }

        private sealed class ChoiceButton : Button
        {
            private readonly InlineChoiceBox owner;
            internal readonly int Index;
            internal bool Selected;
            private bool hovered;
            private int wrapWidth = -1;
            private string wrapped;
            internal ChoiceButton(InlineChoiceBox owner, int index)
            {
                this.owner = owner; Index = index;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                Margin = Padding.Empty;
                Cursor = Cursors.Hand;
                TabStop = true;
                AccessibleRole = AccessibleRole.PushButton;
            }
            internal int MeasureRowHeight(int width)
            {
                int available = Math.Max(1, width - owner.Ui(Index < 0 ? 48 : 46));
                return MeasureLabelHeight(Text, Font, available, owner.Ui(38), owner.Ui(16));
            }
            internal static int MeasureLabelHeight(string label, Font font, int available, int minimum, int padding)
            {
                string text = WrapLabel(label ?? "", font, available);
                return Math.Max(minimum, TextRenderer.MeasureText(text.Length == 0 ? " " : text, font,
                    new Size(Math.Max(1, available), Int32.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + padding);
            }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
            }
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { return owner.HandleChoiceKey(Index, keyData) || base.ProcessCmdKey(ref msg, keyData); }
            protected override bool ProcessDialogKey(Keys keyData) { return owner.HandleChoiceKey(Index, keyData) || base.ProcessDialogKey(keyData); }
            protected override void OnMouseWheel(MouseEventArgs e) { owner.ForwardWheel(e); }
            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnTextChanged(EventArgs e) { wrapWidth = -1; base.OnTextChanged(e); }
            protected override void OnFontChanged(EventArgs e) { wrapWidth = -1; base.OnFontChanged(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(StudioTheme.Surface);
                Color fill = !Enabled ? StudioTheme.Surface : Selected ? StudioTheme.Selection : hovered || Focused ? StudioTheme.Raised : StudioTheme.Surface;
                RectangleF shape = new RectangleF(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (GraphicsPath path = StudioTheme.Round(shape, owner.Ui(6)))
                using (Brush brush = new SolidBrush(fill))
                using (Pen border = new Pen(Focused ? StudioTheme.Accent : Index < 0 ? StudioTheme.Border : fill))
                { graphics.FillPath(brush, path); graphics.DrawPath(border, path); }
                Color ink = !Enabled ? StudioTheme.Disabled : Selected ? StudioTheme.SelectionText : StudioTheme.Text;
                int left = owner.Ui(Index < 0 ? 12 : 34), right = owner.Ui(Index < 0 ? 36 : 12);
                int available = Math.Max(1, Width - left - right);
                if (wrapWidth != available || wrapped == null) { wrapped = WrapLabel(Text ?? "", Font, available); wrapWidth = available; }
                TextRenderer.DrawText(graphics, wrapped, Font, new Rectangle(left, owner.Ui(8), available, Math.Max(1, Height - owner.Ui(16))), ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                using (Pen marker = new Pen(Enabled ? StudioTheme.Accent : StudioTheme.Disabled, Math.Max(1, owner.Ui(2))))
                {
                    if (Index < 0)
                    {
                        float x = Width - owner.Ui(22), y = Height / 2f, size = owner.Ui(4);
                        graphics.DrawLines(marker, owner.expanded
                            ? new PointF[] { new PointF(x - size, y + size / 2), new PointF(x, y - size / 2), new PointF(x + size, y + size / 2) }
                            : new PointF[] { new PointF(x - size, y - size / 2), new PointF(x, y + size / 2), new PointF(x + size, y - size / 2) });
                    }
                    else if (Selected)
                    {
                        float x = owner.Ui(16), y = Height / 2f;
                        graphics.DrawLines(marker, new PointF[] { new PointF(x - owner.Ui(4), y), new PointF(x - owner.Ui(1), y + owner.Ui(3)), new PointF(x + owner.Ui(5), y - owner.Ui(4)) });
                    }
                }
                if (Focused && ShowFocusCues) { Rectangle focus = ClientRectangle; focus.Inflate(-owner.Ui(4), -owner.Ui(4)); ControlPaint.DrawFocusRectangle(graphics, focus, StudioTheme.Accent, fill); }
            }

            private static string WrapLabel(string label, Font font, int width)
            {
                width = Math.Max(1, width);
                List<string> result = new List<string>();
                foreach (string sourceLine in label.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string remaining = sourceLine;
                    if (remaining.Length == 0) { result.Add(""); continue; }
                    while (remaining.Length > 0)
                    {
                        if (TextRenderer.MeasureText(remaining, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= width) { result.Add(remaining); break; }
                        int low = 1, high = remaining.Length;
                        while (low < high)
                        {
                            int middle = (low + high + 1) / 2;
                            if (TextRenderer.MeasureText(remaining.Substring(0, middle), font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= width) low = middle;
                            else high = middle - 1;
                        }
                        int count = Math.Max(1, low);
                        int space = remaining.LastIndexOf(' ', Math.Min(remaining.Length - 1, count - 1), Math.Min(count, remaining.Length));
                        if (space > 0) count = space;
                        result.Add(remaining.Substring(0, count).TrimEnd());
                        remaining = remaining.Substring(count).TrimStart();
                    }
                }
                return String.Join(Environment.NewLine, result.ToArray());
            }
        }
    }
}
