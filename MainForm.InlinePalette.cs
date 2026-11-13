using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private Panel inlineAnnotationPalette;
        private Func<Size> inlineAnnotationPaletteSize;
        private bool inlineAnnotationPaletteOwned;
        private Func<bool> inlineAnnotationPaletteIsCurrent;

        private Func<bool> InlinePaletteContextGuard()
        {
            object image = canvas.ImageIdentity;
            long revision = canvas.DocumentRevision;
            int index = canvas.SelectedAnnotationIndex;
            EditorTool tool = canvas.Tool;
            return delegate { return canvas.HasImage && Object.ReferenceEquals(image, canvas.ImageIdentity) &&
                revision == canvas.DocumentRevision && index == canvas.SelectedAnnotationIndex && tool == canvas.Tool; };
        }

        private void DismissStaleInlineAnnotationPalette()
        {
            if (inlineAnnotationPaletteIsCurrent != null && !inlineAnnotationPaletteIsCurrent()) ClearInlineAnnotationPalette();
        }

        private int InlineAnnotationPaletteMinimumWidth
        {
            get { return inlineAnnotationPalette == null || inlineAnnotationPaletteSize == null ? 0 :
                inlineAnnotationPaletteSize().Width + Ui(16); }
        }

        private void SetInlineAnnotationPalette(Panel panel, Func<Size> preferredSize, bool owned = false, Func<bool> current = null)
        {
            ClearInlineAnnotationPalette();
            if (panel == null || panel.IsDisposed || toolOptionsPopup == null || toolOptionsPopup.IsDisposed) return;
            inlineAnnotationPalette = panel;
            inlineAnnotationPaletteSize = preferredSize;
            inlineAnnotationPaletteOwned = owned;
            inlineAnnotationPaletteIsCurrent = current;
            // One scroll owner keeps the expanded palette reachable as part of
            // the same attached card. The original picker still owns its panel.
            panel.AutoScrollMinSize = Size.Empty;
            panel.AutoScroll = false;
            toolOptionsPopup.Content.Controls.Add(panel);
            panel.Visible = true;
            RefreshToolOptionsContents();
        }

        private int LayoutInlineAnnotationPalette(int bodyWidth, int y)
        {
            Panel panel = inlineAnnotationPalette;
            if (panel == null || panel.IsDisposed) return y;
            Size preferred = inlineAnnotationPaletteSize == null ? panel.Size : inlineAnnotationPaletteSize();
            int width = Math.Max(1, preferred.Width);
            panel.SetBounds(Ui(8), y + Ui(10), width, Math.Max(1, preferred.Height));
            panel.Visible = true;
            return panel.Bottom + Ui(8);
        }

        private void ClearInlineAnnotationPalette()
        {
            Panel panel = inlineAnnotationPalette;
            bool owned = inlineAnnotationPaletteOwned;
            inlineAnnotationPalette = null;
            inlineAnnotationPaletteSize = null;
            inlineAnnotationPaletteOwned = false;
            inlineAnnotationPaletteIsCurrent = null;
            if (panel == null || panel.IsDisposed) return;
            panel.Visible = false;
            if (panel.Parent != null) panel.Parent.Controls.Remove(panel);
            if (owned) panel.Dispose();
        }

        private void OpenInlineInkPalette()
        {
            if (!CanChangeAnnotationAppearance || toolOptionsPopup == null || toolOptionsPopup.IsDisposed) return;
            CloseNestedAnnotationPopups();
            Func<bool> current = InlinePaletteContextGuard();
            Panel panel = new Panel { BackColor = StudioTheme.Surface, ForeColor = StudioTheme.Text,
                Width = Ui(320), Margin = Padding.Empty, Padding = Padding.Empty };
            ColorPickerPanel picker = new ColorPickerPanel(canvas.InkColor, RecentToolColors(), false, Ui(1000) / 1000f)
            { Location = new Point(Ui(12), Ui(12)), Width = Ui(296) };
            panel.Controls.Add(picker);
            StudioButton cancel = new StudioButton { Text = "Cancel", AccessibleName = "Cancel color changes" };
            StudioButton apply = new StudioButton { Text = "Use color", Primary = true, AccessibleName = "Apply color" };
            panel.Controls.Add(cancel); panel.Controls.Add(apply);
            Action arrange = delegate
            {
                if (panel.IsDisposed) return;
                picker.Height = picker.ContentHeight;
                int y = picker.Bottom + Ui(12);
                cancel.SetBounds(Ui(12), y, Ui(104), Ui(34));
                apply.SetBounds(panel.Width - Ui(124), y, Ui(112), Ui(34));
                panel.Height = y + Ui(46);
                apply.Enabled = picker.IsValidColor;
                RefreshToolOptionsContents();
            };
            picker.ContentHeightChanged += delegate { arrange(); };
            picker.ColorChanged += delegate { if (!apply.IsDisposed) apply.Enabled = picker.IsValidColor; };
            cancel.Click += delegate { ClearInlineAnnotationPalette(); RefreshToolOptionsContents(); };
            apply.Click += delegate
            {
                if (!CanChangeAnnotationAppearance || !picker.IsValidColor || !current()) return;
                Color chosen = picker.SelectedColor;
                ClearInlineAnnotationPalette();
                canvas.InkColor = chosen;
                if (!highlighterColor) preferences.InkArgb = chosen.ToArgb();
                RememberToolColor(chosen); UpdateColorButton(); RefreshToolOptionsContents();
            };
            arrange();
            SetInlineAnnotationPalette(panel, delegate { return panel.Size; }, true, current);
        }
    }
}
