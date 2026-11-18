using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private Panel inlineAnnotationPalette;
        private Panel paletteDockHost;
        private Func<Size> inlineAnnotationPaletteSize;
        private bool inlineAnnotationPaletteOwned;
        private Func<bool> inlineAnnotationPaletteIsCurrent;
        private bool changingInlineAnnotationPalette;

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
            changingInlineAnnotationPalette = true;
            try
            {
                ClearInlineAnnotationPalette();
                if (panel == null || panel.IsDisposed || !IsToolOptionsOpen || paletteDockHost == null) return;
                inlineAnnotationPalette = panel;
                inlineAnnotationPaletteSize = preferredSize;
                inlineAnnotationPaletteOwned = owned;
                inlineAnnotationPaletteIsCurrent = current;
                // The reserved side pane owns scrolling. Picker controls keep
                // their full preferred width and their original Apply/Cancel.
                panel.AutoScrollMinSize = Size.Empty;
                panel.AutoScroll = false;
                paletteDockHost.AutoScrollPosition = Point.Empty;
                paletteDockHost.Controls.Add(panel);
                panel.Visible = true;
            }
            finally
            {
                changingInlineAnnotationPalette = false;
                if (!arrangingChrome && !openingToolOptions && !closing && !IsDisposed) LayoutChrome();
            }
        }

        private int InlineAnnotationPaletteDockWidth(int availableWidth)
        {
            if (!IsToolOptionsOpen || inlineAnnotationPalette == null || inlineAnnotationPalette.IsDisposed) return 0;
            Size preferred = inlineAnnotationPaletteSize == null ? inlineAnnotationPalette.Size : inlineAnnotationPaletteSize();
            int desired = Math.Max(1, preferred.Width) + Ui(16) + Math.Max(SystemInformation.VerticalScrollBarWidth, Ui(17));
            // Preserve a useful image viewport. At smaller widths the palette
            // itself scrolls horizontally rather than clipping picker fields.
            return Math.Min(desired, Math.Max(0, availableWidth - Math.Min(availableWidth, Ui(120))));
        }

        private void LayoutInlineAnnotationPaletteDock(Rectangle bounds)
        {
            if (paletteDockHost == null) return;
            Panel panel = inlineAnnotationPalette;
            if (!IsToolOptionsOpen || panel == null || panel.IsDisposed || bounds.Width <= 0)
            {
                paletteDockHost.Visible = false;
                paletteDockHost.SetBounds(bounds.X, bounds.Y, 0, Math.Max(0, bounds.Height));
                return;
            }
            Size preferred = inlineAnnotationPaletteSize == null ? panel.Size : inlineAnnotationPaletteSize();
            Point scroll = new Point(Math.Max(0, -paletteDockHost.AutoScrollPosition.X), Math.Max(0, -paletteDockHost.AutoScrollPosition.Y));
            paletteDockHost.SuspendLayout();
            try
            {
                paletteDockHost.AutoScrollPosition = Point.Empty;
                paletteDockHost.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                panel.SetBounds(Ui(8), Ui(8), Math.Max(1, preferred.Width), Math.Max(1, preferred.Height));
                paletteDockHost.AutoScrollMinSize = new Size(panel.Right + Ui(8), panel.Bottom + Ui(8));
                paletteDockHost.AutoScrollPosition = scroll;
                panel.Visible = paletteDockHost.Visible = true;
            }
            finally { paletteDockHost.ResumeLayout(false); }
        }

        private void ClearInlineAnnotationPalette()
        {
            Panel panel = inlineAnnotationPalette;
            bool owned = inlineAnnotationPaletteOwned;
            inlineAnnotationPalette = null;
            inlineAnnotationPaletteSize = null;
            inlineAnnotationPaletteOwned = false;
            inlineAnnotationPaletteIsCurrent = null;
            if (paletteDockHost != null && !paletteDockHost.IsDisposed)
            {
                paletteDockHost.Visible = false;
                paletteDockHost.AutoScrollPosition = Point.Empty;
                paletteDockHost.AutoScrollMinSize = Size.Empty;
            }
            if (panel != null && !panel.IsDisposed)
            {
                panel.Visible = false;
                if (panel.Parent != null) panel.Parent.Controls.Remove(panel);
                if (owned) panel.Dispose();
            }
            if (panel != null && !changingInlineAnnotationPalette && !arrangingChrome && !openingToolOptions && !closing && !IsDisposed)
                LayoutChrome();
        }

        private void OpenInlineInkPalette()
        {
            if (!CanChangeAnnotationAppearance || !IsToolOptionsOpen) return;
            CloseNestedAnnotationPopups();
            Func<bool> current = InlinePaletteContextGuard();
            Panel panel = new Panel { BackColor = StudioTheme.Surface, ForeColor = StudioTheme.Text,
                Width = Ui(320), Margin = Padding.Empty, Padding = Padding.Empty };
            ColorPickerPanel picker = new ColorPickerPanel(CurrentDisplayedInkColor, RecentToolColors(), false, Ui(1000) / 1000f)
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
