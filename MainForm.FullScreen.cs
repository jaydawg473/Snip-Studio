using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private bool fullScreenEditor;
        private bool changingFullScreenEditor;
        private Rectangle windowedEditorBounds;
        private Rectangle windowedEditorMaximizedBounds;
        private FormWindowState windowedEditorState;
        private FormBorderStyle windowedEditorBorder;
        private Size windowedEditorMinimumSize;
        private Size windowedEditorMaximumSize;

        private bool IsFullScreenEditor { get { return fullScreenEditor; } }

        private string FullScreenToolTip
        {
            get { return fullScreenEditor ? "Exit full screen editor (F11 or Escape)" : "Full screen editor on this monitor (F11)"; }
        }

        private void ToggleFullScreenEditor()
        {
            if (changingFullScreenEditor || capturePending || ModalOpenCount() > 0) return;
            if (fullScreenEditor) { ExitFullScreenEditor(); return; }

            EnterFullScreenEditor(Screen.FromControl(this).Bounds);
        }

        // Keep geometry application separate from monitor discovery so the
        // exact fullscreen layout can also be checked without native windows.
        private void EnterFullScreenEditor(Rectangle monitorBounds)
        {
            if (monitorBounds.Width < 1 || monitorBounds.Height < 1)
                throw new ArgumentOutOfRangeException("monitorBounds");
            windowedEditorState = WindowState;
            windowedEditorBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            windowedEditorMaximizedBounds = MaximizedBounds;
            windowedEditorBorder = FormBorderStyle;
            windowedEditorMinimumSize = MinimumSize;
            windowedEditorMaximumSize = MaximumSize;

            changingFullScreenEditor = true;
            bool wasArranging = arrangingChrome;
            arrangingChrome = true;
            SuspendLayout();
            try
            {
                // A normal, borderless window fills the current monitor without
                // changing its z-order relationship with other applications.
                fullScreenEditor = true;
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                MaximizedBounds = monitorBounds;
                Bounds = monitorBounds;
            }
            finally
            {
                // Complete Dock=Fill on chromeRoot before measuring its child
                // toolbars and image viewport. Deferring this leaves old bounds.
                ResumeLayout(true);
                arrangingChrome = wasArranging;
                changingFullScreenEditor = false;
            }
            if (!wasArranging)
            {
                LayoutChrome();
                canvas.FitToWindow();
            }
        }

        private void ExitFullScreenEditor()
        {
            if (!fullScreenEditor || changingFullScreenEditor) return;
            changingFullScreenEditor = true;
            bool wasArranging = arrangingChrome;
            arrangingChrome = true;
            SuspendLayout();
            try
            {
                WindowState = FormWindowState.Normal;
                MinimumSize = Size.Empty;
                MaximumSize = Size.Empty;
                FormBorderStyle = windowedEditorBorder;
                MaximizedBounds = windowedEditorMaximizedBounds;
                Bounds = windowedEditorBounds;
                MinimumSize = windowedEditorMinimumSize;
                MaximumSize = windowedEditorMaximumSize;
                WindowState = windowedEditorState;
                fullScreenEditor = false;
            }
            finally
            {
                ResumeLayout(true);
                arrangingChrome = wasArranging;
                changingFullScreenEditor = false;
            }
            if (!wasArranging) LayoutChrome();
        }

        private bool TryExitFullScreenOnEscape()
        {
            // The canvas consumes Escape for drawing, annotation selection and
            // text actions before the key bubbles up to MainForm.
            if (!fullScreenEditor || changingFullScreenEditor || capturePending || canvas.Capture || ModalOpenCount() > 0) return false;
            ExitFullScreenEditor();
            return true;
        }
    }
}
