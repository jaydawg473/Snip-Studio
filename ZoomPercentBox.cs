using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SnipStudio
{
    // Editable view scale, independent of image/export resolution. Native list
    // selection and Enter are the only commit boundaries; view notifications
    // never overwrite text that is currently being edited.
    internal sealed class ZoomPercentBox : ComboBox
    {
        private bool updatingText, editing;
        private float actualPercent = 100f;
        internal float RequestedPercent { get; private set; }
        internal string ValidationMessage { get; private set; }
        internal event EventHandler ZoomRequested;
        internal event EventHandler ValidationFailed;

        internal ZoomPercentBox()
        {
            DropDownStyle = ComboBoxStyle.DropDown;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = StudioTheme.Input;
            ForeColor = StudioTheme.Text;
            Margin = Padding.Empty;
            AccessibleName = "Zoom percentage";
            AccessibleDescription = "Type a percentage from 1 to 800 and press Enter, or select a preset. Escape cancels the edit.";
            foreach (int percent in new int[] { 1, 10, 25, 50, 75, 100, 125, 150, 200, 400, 800 })
                Items.Add(percent.ToString(CultureInfo.InvariantCulture) + "%");
            MaxDropDownItems = 11;
            IntegralHeight = true;
            UpdateMetrics(1f);
            SetCaption();
        }

        internal static bool TryParsePercent(string text, out float percent)
        {
            percent = 0;
            if (String.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            if (value.EndsWith("%", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1).Trim();
            const NumberStyles style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
            if (!Single.TryParse(value, style, CultureInfo.CurrentCulture, out percent) &&
                !Single.TryParse(value, style, CultureInfo.InvariantCulture, out percent)) return false;
            return !Single.IsNaN(percent) && !Single.IsInfinity(percent) && percent >= 1f && percent <= 800f;
        }

        internal void UpdateMetrics(float scale)
        {
            ItemHeight = Math.Max((int)Math.Ceiling(22 * scale), Font.Height + (int)Math.Ceiling(4 * scale));
            DropDownWidth = Math.Max(Width, (int)Math.Ceiling(110 * scale));
        }

        internal void UpdateViewPercent(float percent)
        {
            if (Single.IsNaN(percent) || Single.IsInfinity(percent) || percent <= 0) return;
            actualPercent = percent;
            if (!editing && !ContainsFocus && !DroppedDown) SetCaption();
        }

        internal bool CommitInput()
        {
            float percent;
            if (!TryParsePercent(Text, out percent))
            {
                editing = true;
                ValidationMessage = "Enter a zoom percentage from 1% to 800%, then press Enter. Escape keeps the current zoom.";
                AccessibleDescription = ValidationMessage;
                EventHandler invalid = ValidationFailed;
                if (invalid != null) invalid(this, EventArgs.Empty);
                return false;
            }
            RequestedPercent = percent;
            actualPercent = percent;
            editing = false;
            ValidationMessage = "";
            AccessibleDescription = "Type a percentage from 1 to 800 and press Enter, or select a preset. Escape cancels the edit.";
            SetCaption();
            EventHandler handler = ZoomRequested;
            if (handler != null) handler(this, EventArgs.Empty);
            return true;
        }

        internal void RevertInput()
        {
            editing = false;
            ValidationMessage = "";
            AccessibleDescription = "Type a percentage from 1 to 800 and press Enter, or select a preset. Escape cancels the edit.";
            SetCaption();
        }

        internal static bool IsEditingShortcut(Keys keys)
        {
            return keys == (Keys.Control | Keys.A) || keys == (Keys.Control | Keys.C) ||
                keys == (Keys.Control | Keys.V) || keys == (Keys.Control | Keys.X) ||
                keys == (Keys.Control | Keys.Z) || keys == (Keys.Control | Keys.Y) ||
                keys == (Keys.Control | Keys.Shift | Keys.Z);
        }

        private void SetCaption()
        {
            updatingText = true;
            try
            {
                string caption = actualPercent.ToString("0.##", CultureInfo.CurrentCulture) + "%";
                if (!String.Equals(Text, caption, StringComparison.Ordinal)) Text = caption;
                ClearCaptionSelection();
            }
            finally { updatingText = false; }
        }

        private void ClearCaptionSelection()
        {
            // Native editable combos may select their caption when its text is
            // assigned or committed. Leave an idle value as plain text without
            // creating a handle or moving keyboard focus.
            if (!IsHandleCreated || IsDisposed) return;
            SelectionStart = Text.Length;
            SelectionLength = 0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!editing) ClearCaptionSelection();
        }

        protected override void OnEnter(EventArgs e)
        {
            editing = true;
            base.OnEnter(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            if (!updatingText) editing = true;
            base.OnTextChanged(e);
        }

        protected override void OnSelectionChangeCommitted(EventArgs e)
        {
            // The selected preset is authoritative. In particular, the editable
            // child may still hold pending text when native selection commits.
            if (SelectedIndex >= 0) Text = GetItemText(SelectedItem);
            CommitInput();
            base.OnSelectionChangeCommitted(e);
        }

        protected override void OnLeave(EventArgs e)
        {
            RevertInput();
            base.OnLeave(e);
            ClearCaptionSelection();
        }

        protected override void OnDropDownClosed(EventArgs e)
        {
            base.OnDropDownClosed(e);
            if (!editing) ClearCaptionSelection();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Enter) { CommitInput(); return true; }
            if (keyData == Keys.Escape) { DroppedDown = false; RevertInput(); return true; }
            if (keyData == (Keys.Control | Keys.A))
            {
                editing = true;
                if (IsHandleCreated) SelectAll();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void WndProc(ref Message message)
        {
            // Hovering the footer must not change zoom by an accidental wheel
            // notch. Image-wheel zoom retains its existing canvas behavior.
            if (message.Msg == 0x020A) return;
            base.WndProc(ref message);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush fill = new SolidBrush(selected ? StudioTheme.Selection : StudioTheme.Input)) e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index >= 0 && e.Index < Items.Count)
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds,
                    selected ? StudioTheme.SelectionText : StudioTheme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
    }
}
