using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnipStudio
{
    internal static class StudioTheme
    {
        private static bool isDark = true;
        private static string mode = "Dark";
        private static bool systemEventsConnected;
        private static readonly List<WeakReference> roots = new List<WeakReference>();
        internal static event EventHandler ThemeChanged;

        internal static bool IsDark { get { return isDark; } }
        internal static string Mode { get { return mode; } }
        internal static Color Background { get { return isDark ? Color.FromArgb(20, 26, 36) : Color.FromArgb(241, 244, 248); } }
        internal static Color Surface { get { return isDark ? Color.FromArgb(28, 36, 49) : Color.White; } }
        internal static Color Raised { get { return isDark ? Color.FromArgb(36, 46, 62) : Color.FromArgb(230, 236, 244); } }
        internal static Color Border { get { return isDark ? Color.FromArgb(55, 68, 87) : Color.FromArgb(199, 209, 223); } }
        internal static Color Text { get { return isDark ? Color.FromArgb(237, 242, 249) : Color.FromArgb(27, 40, 59); } }
        internal static Color Muted { get { return isDark ? Color.FromArgb(155, 171, 191) : Color.FromArgb(87, 104, 127); } }
        internal static Color Accent { get { return isDark ? Color.FromArgb(77, 163, 255) : Color.FromArgb(0, 103, 192); } }
        internal static Color AccentHover { get { return isDark ? Color.FromArgb(101, 176, 255) : Color.FromArgb(0, 91, 175); } }
        internal static Color AccentPressed { get { return isDark ? Color.FromArgb(54, 137, 226) : Color.FromArgb(0, 80, 157); } }
        internal static Color PrimaryText { get { return isDark ? Color.FromArgb(9, 27, 47) : Color.White; } }
        internal static Color Selection { get { return isDark ? Color.FromArgb(33, 62, 93) : Color.FromArgb(221, 237, 253); } }
        internal static Color SelectionText { get { return isDark ? Color.FromArgb(158, 208, 255) : Color.FromArgb(0, 77, 150); } }
        internal static Color Pressed { get { return isDark ? Color.FromArgb(46, 62, 84) : Color.FromArgb(211, 224, 241); } }
        internal static Color Disabled { get { return isDark ? Color.FromArgb(89, 104, 124) : Color.FromArgb(134, 147, 164); } }
        internal static Color Input { get { return isDark ? Color.FromArgb(20, 28, 40) : Color.FromArgb(247, 250, 253); } }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal static void SetTheme(string requestedMode)
        {
            string choice = String.IsNullOrWhiteSpace(requestedMode) ? "Dark" : requestedMode.Trim();
            mode = String.Equals(choice, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
                : String.Equals(choice, "System", StringComparison.OrdinalIgnoreCase) ? "System" : "Dark";
            bool next = mode == "System" ? ReadSystemDark() : mode == "Dark";
            bool changed = next != isDark;
            isDark = next;
            ConnectSystemEvents();
            if (!changed) return;
            ApplyOpenWindows();
            if (changed)
            {
                EventHandler handler = ThemeChanged;
                if (handler != null) handler(null, EventArgs.Empty);
            }
        }

        private static bool ReadSystemDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value != null && Convert.ToInt32(value) == 0;
                }
            }
            catch (Exception) { return false; }
        }

        private static void ConnectSystemEvents()
        {
            if (systemEventsConnected) return;
            try
            {
                SystemEvents.UserPreferenceChanged += delegate
                {
                    if (mode != "System" || ReadSystemDark() == isDark) return;
                    Control dispatcher = null;
                    lock (roots)
                    {
                        for (int i = roots.Count - 1; i >= 0; i--)
                        {
                            Control candidate = roots[i].Target as Control;
                            if (candidate == null || candidate.IsDisposed) { roots.RemoveAt(i); continue; }
                            if (candidate.IsHandleCreated) { dispatcher = candidate; break; }
                        }
                    }
                    if (dispatcher == null) return;
                    try { dispatcher.BeginInvoke((MethodInvoker)delegate { if (mode == "System") SetTheme("System"); }); }
                    catch (InvalidOperationException) { }
                };
                systemEventsConnected = true;
            }
            catch (Exception) { }
        }

        private static void ApplyOpenWindows()
        {
            List<Form> forms = new List<Form>();
            foreach (Form form in Application.OpenForms) forms.Add(form);
            foreach (Form form in forms)
            {
                if (!form.IsDisposed) ApplyTo(form);
            }
        }

        internal static void ApplyTo(Control root)
        {
            if (root == null || root.IsDisposed) return;
            if (root.InvokeRequired)
            {
                try { root.BeginInvoke((MethodInvoker)delegate { ApplyTo(root); }); }
                catch (InvalidOperationException) { }
                return;
            }
            if (root is Form)
            {
                bool known = false;
                lock (roots)
                {
                    for (int i = roots.Count - 1; i >= 0; i--)
                    {
                        Control existing = roots[i].Target as Control;
                        if (existing == null || existing.IsDisposed) roots.RemoveAt(i);
                        else if (Object.ReferenceEquals(existing, root)) known = true;
                    }
                    if (!known) roots.Add(new WeakReference(root));
                }
                if (!known) root.HandleCreated += delegate { ApplyCaption((Form)root); };
            }
            root.SuspendLayout();
            try { ApplyControl(root); }
            finally { root.ResumeLayout(true); }
            Form window = root as Form;
            if (window != null) ApplyCaption(window);
            root.Invalidate(true);
        }

        private static void ApplyControl(Control control)
        {
            bool preserve = String.Equals(control.Tag as string, "PreserveColor", StringComparison.Ordinal);
            StudioButton button = control as StudioButton;
            StudioComboBox combo = control as StudioComboBox;
            if (!preserve)
            {
                if (button != null) { control.BackColor = Surface; control.ForeColor = Text; }
                else if (combo != null) { control.BackColor = Raised; control.ForeColor = Text; }
                else if (control is TextBoxBase || control is NumericUpDown || control is ListBox)
                {
                    control.BackColor = Input;
                    control.ForeColor = Text;
                }
                else
                {
                    Color back = control.BackColor;
                    Color fore = control.ForeColor;
                    control.BackColor = MapBackground(back, control);
                    control.ForeColor = MapForeground(fore);
                }
            }
            LinkLabel link = control as LinkLabel;
            if (link != null)
            {
                link.LinkColor = Accent;
                link.ActiveLinkColor = AccentHover;
                link.VisitedLinkColor = Accent;
            }
            if (control.ContextMenuStrip != null) StyleMenu(control.ContextMenuStrip);
            ToolStrip strip = control as ToolStrip;
            if (strip != null) StyleMenu(strip);
            foreach (Control child in control.Controls) ApplyControl(child);
        }

        private static bool Same(Color value, int r, int g, int b)
        {
            return value.A == 255 && value.R == r && value.G == g && value.B == b;
        }

        private static Color MapBackground(Color value, Control control)
        {
            if (value.A < 255) return value;
            if (Same(value, 20, 26, 36) || Same(value, 241, 244, 248) || Same(value, 239, 242, 246)) return Background;
            if (Same(value, 28, 36, 49) || Same(value, 255, 255, 255)) return Surface;
            if (Same(value, 36, 46, 62) || Same(value, 230, 236, 244)) return Raised;
            if (Same(value, 55, 68, 87) || Same(value, 199, 209, 223)) return Border;
            if (Same(value, 20, 28, 40) || Same(value, 247, 250, 253)) return Input;
            if (Same(value, 33, 62, 93) || Same(value, 221, 237, 253) || Same(value, 43, 65, 92)) return Selection;
            if (Same(value, 46, 62, 84) || Same(value, 211, 224, 241)) return Pressed;
            if (value.ToArgb() == SystemColors.Control.ToArgb())
                return control.Parent == null ? Surface : control.Parent.BackColor;
            return value;
        }

        private static Color MapForeground(Color value)
        {
            if (value.A < 255) return value;
            if (Same(value, 155, 171, 191) || Same(value, 87, 104, 127) || value.ToArgb() == SystemColors.GrayText.ToArgb()) return Muted;
            if (Same(value, 77, 163, 255) || Same(value, 0, 103, 192)) return Accent;
            if (Same(value, 237, 242, 249) || Same(value, 27, 40, 59) || Same(value, 0, 0, 0) || Same(value, 255, 255, 255)
                || value.ToArgb() == SystemColors.ControlText.ToArgb()) return Text;
            return value;
        }

        internal static void ApplyCaption(Form form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
            try
            {
                int dark = IsDark ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
                int caption = ColorTranslator.ToWin32(Background);
                int text = ColorTranslator.ToWin32(Text);
                DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        internal static void StyleMenu(ToolStrip menu)
        {
            if (menu == null || menu.IsDisposed) return;
            menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable());
            menu.BackColor = Surface;
            menu.ForeColor = Text;
            foreach (ToolStripItem item in menu.Items)
            {
                item.BackColor = Surface;
                item.ForeColor = Text;
                string glyph = item.Tag as string;
                if (glyph != null && glyph.StartsWith("glyph:", StringComparison.Ordinal))
                {
                    Image previous = item.Image;
                    item.Image = IconArtwork.CreateToolIcon(glyph.Substring(6), 20, Text);
                    if (previous != null) previous.Dispose();
                }
                ToolStripDropDownItem parent = item as ToolStripDropDownItem;
                if (parent != null && parent.HasDropDownItems) StyleMenu(parent.DropDown);
            }
        }

        private sealed class ThemeColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Accent; } }
            public override Color MenuItemSelected { get { return Selection; } }
            public override Color MenuItemSelectedGradientBegin { get { return Selection; } }
            public override Color MenuItemSelectedGradientEnd { get { return Selection; } }
            public override Color MenuItemPressedGradientBegin { get { return Pressed; } }
            public override Color MenuItemPressedGradientEnd { get { return Pressed; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Surface; } }
        }

        internal static GraphicsPath Round(RectangleF box, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
            if (diameter < 1) { path.AddRectangle(box); return path; }
            path.AddArc(box.X, box.Y, diameter, diameter, 180, 90);
            path.AddArc(box.Right - diameter, box.Y, diameter, diameter, 270, 90);
            path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(box.X, box.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class StudioButton : Button
    {
        private bool hovered, pressed, selected;
        private string glyph = "";
        private Bitmap glyphImage;
        private int cachedSize;
        private Color cachedColor;
        internal bool Primary;
        internal bool ToolLayout;
        internal bool IconOnly;
        internal bool Swatch;
        internal Color SwatchColor = Color.Red;

        internal string Glyph { get { return glyph; } set { glyph = value; ResetIcon(); Invalidate(); } }
        internal bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        internal int PreferredContentWidth(float scale)
        {
            if (IconOnly) return (int)Math.Ceiling(34 * scale);
            int text = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            return text + (int)Math.Ceiling((String.IsNullOrEmpty(glyph) ? 16 : 48) * scale);
        }

        internal StudioButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { ResetIcon(); Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96f;
            Color surface = Parent == null ? StudioTheme.Surface : Parent.BackColor;
            g.Clear(surface);
            Color fill = Primary ? (hovered ? StudioTheme.AccentHover : StudioTheme.Accent) : selected ? StudioTheme.Selection : hovered ? StudioTheme.Raised : surface;
            if (pressed) fill = Primary ? StudioTheme.AccentPressed : StudioTheme.Pressed;
            if (!Enabled) fill = surface;
            RectangleF bounds = new RectangleF(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = StudioTheme.Round(bounds, 7 * scale))
            {
                using (Brush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (selected || (hovered && !Primary))
                    using (Pen outline = new Pen(selected ? StudioTheme.Accent : StudioTheme.Border, scale)) g.DrawPath(outline, path);
            }
            Color ink = !Enabled ? StudioTheme.Disabled : Primary ? StudioTheme.PrimaryText : selected ? StudioTheme.SelectionText : StudioTheme.Text;
            bool iconOnly = IconOnly || Text == "+" || Text == "−";
            int iconSize = (int)((ToolLayout ? 22 : 18) * scale);
            int left = (int)(12 * scale);
            int top = (Height - iconSize) / 2;
            if (ToolLayout) { left = (Width - iconSize) / 2; top = (int)(10 * scale); }
            if (iconOnly) left = (Width - iconSize) / 2;
            if (!String.IsNullOrEmpty(glyph))
            {
                if (glyphImage == null || cachedSize != iconSize || cachedColor != ink)
                {
                    ResetIcon();
                    glyphImage = IconArtwork.CreateToolIcon(glyph, iconSize, ink);
                    cachedSize = iconSize;
                    cachedColor = ink;
                }
                g.DrawImageUnscaled(glyphImage, left, top);
            }
            if (Swatch)
            {
                Rectangle swatch = new Rectangle((Width - (int)(24 * scale)) / 2, ToolLayout ? (int)(10 * scale) : (Height - (int)(21 * scale)) / 2, (int)(24 * scale), (int)(21 * scale));
                using (Brush brush = new SolidBrush(Enabled ? SwatchColor : StudioTheme.Border))
                using (GraphicsPath path = StudioTheme.Round(swatch, 5 * scale)) g.FillPath(brush, path);
                using (Pen border = new Pen(StudioTheme.IsDark ? Color.FromArgb(100, 220, 231, 245) : Color.FromArgb(130, 50, 70, 95), scale))
                using (GraphicsPath path = StudioTheme.Round(swatch, 5 * scale)) g.DrawPath(border, path);
            }
            if (!iconOnly)
            {
                Rectangle textBox = ToolLayout ? new Rectangle(2, (int)(37 * scale), Width - 4, Height - (int)(38 * scale))
                    : new Rectangle(String.IsNullOrEmpty(glyph) ? 4 : left + iconSize + (int)(8 * scale), 0,
                        Width - (String.IsNullOrEmpty(glyph) ? 8 : left + iconSize + (int)(14 * scale)), Height);
                TextRenderer.DrawText(g, Text, Font, textBox, ink,
                    TextFormatFlags.VerticalCenter | (ToolLayout || String.IsNullOrEmpty(glyph) ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues)
            {
                Rectangle focus = ClientRectangle;
                focus.Inflate(-4, -4);
                ControlPaint.DrawFocusRectangle(g, focus, StudioTheme.Accent, fill);
            }
        }

        private void ResetIcon() { if (glyphImage != null) { glyphImage.Dispose(); glyphImage = null; } }
        protected override void Dispose(bool disposing) { if (disposing) ResetIcon(); base.Dispose(disposing); }
    }

    internal sealed class StudioComboBox : ComboBox
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        internal StudioComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = StudioTheme.Raised;
            ForeColor = StudioTheme.Text;
            ItemHeight = 26;
            DropDownStyle = ComboBoxStyle.DropDownList;
        }

        internal int PreferredContentWidth(float scale)
        {
            int widest = 0;
            foreach (object item in Items)
            {
                string label = GetItemText(item);
                int content = TextRenderer.MeasureText(label, Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                if (ModeGlyph(label) != null) content += (int)Math.Ceiling(24 * scale);
                widest = Math.Max(widest, content);
            }
            // DrawItem bounds already exclude the native arrow. Reserve its
            // space here, in addition to our owner-drawn content padding.
            return widest + (int)Math.Ceiling(16 * scale) + Math.Max(SystemInformation.VerticalScrollBarWidth, (int)Math.Ceiling(22 * scale));
        }

        internal void UpdateMetrics(float scale)
        {
            int height = Math.Max((int)Math.Ceiling(26 * scale), Font.Height + (int)Math.Ceiling(6 * scale));
            if (ItemHeight != height) ItemHeight = height;
        }

        protected override void OnDropDown(EventArgs e)
        {
            using (Graphics graphics = CreateGraphics())
                DropDownWidth = Math.Max(Width, Math.Min(Screen.FromControl(this).WorkingArea.Width - 16, PreferredContentWidth(graphics.DpiX / 96f)));
            base.OnDropDown(e);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush fill = new SolidBrush(selected ? StudioTheme.Selection : StudioTheme.Raised)) e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index >= 0)
            {
                Rectangle bounds = e.Bounds;
                float scale = e.Graphics.DpiX / 96f;
                int padding = (int)Math.Round(8 * scale);
                bounds.X += padding;
                bounds.Width -= padding + (int)Math.Round(4 * scale);
                string label = GetItemText(Items[e.Index]);
                string glyph = ModeGlyph(label);
                if (glyph != null)
                {
                    int size = Math.Max(12, (int)Math.Round(16 * scale));
                    using (Bitmap image = IconArtwork.CreateToolIcon(glyph, size, Enabled ? StudioTheme.Text : StudioTheme.Muted))
                        e.Graphics.DrawImageUnscaled(image, bounds.X, e.Bounds.Y + (e.Bounds.Height - size) / 2);
                    bounds.X += size + padding;
                    bounds.Width -= size + padding;
                }
                TextRenderer.DrawText(e.Graphics, label, Font, bounds,
                    Enabled ? StudioTheme.Text : StudioTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x020A && !DroppedDown)
            {
                Control parent = Parent;
                while (parent != null)
                {
                    ScrollableControl scroll = parent as ScrollableControl;
                    if (scroll != null && scroll.AutoScroll && scroll.IsHandleCreated)
                    {
                        SendMessage(scroll.Handle, message.Msg, message.WParam, message.LParam);
                        break;
                    }
                    parent = parent.Parent;
                }
                return;
            }
            base.WndProc(ref message);
        }

        private static string ModeGlyph(string label)
        {
            if (String.Equals(label, "Rectangle", StringComparison.OrdinalIgnoreCase)) return "rectangle";
            if (String.Equals(label, "Freeform", StringComparison.OrdinalIgnoreCase)) return "freeform";
            if (String.Equals(label, "Window", StringComparison.OrdinalIgnoreCase)) return "window";
            if (String.Equals(label, "Scrolling", StringComparison.OrdinalIgnoreCase)) return "scrolling";
            if (String.Equals(label, "Full screen", StringComparison.OrdinalIgnoreCase) || String.Equals(label, "Fullscreen", StringComparison.OrdinalIgnoreCase) || String.Equals(label, "All monitors", StringComparison.OrdinalIgnoreCase)) return "fullscreen";
            return null;
        }
    }
}
