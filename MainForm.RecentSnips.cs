using System;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private RecentSnipsForm recentSnipsForm;
        private Timer recentSnipsRestoreTimer;
        private bool restoreRecentSnips;

        private void ShowRecentSnips()
        {
            if (!CanChangeAnnotationAppearance || IsCaptureSavePending) return;
            if (recentSnipsForm != null && !recentSnipsForm.IsDisposed)
            {
                restoreRecentSnips = false;
                if (!recentSnipsForm.Visible) recentSnipsForm.Show(this);
                recentSnipsForm.BringToFront();
                recentSnipsForm.Activate();
                return;
            }
            RecentSnipsForm gallery = new RecentSnipsForm(OpenRecentSnip);
            recentSnipsForm = gallery;
            gallery.FormClosed += delegate
            {
                if (Object.ReferenceEquals(recentSnipsForm, gallery)) { recentSnipsForm = null; restoreRecentSnips = false; }
            };
            try { gallery.Show(this); }
            catch (Exception error)
            {
                if (Object.ReferenceEquals(recentSnipsForm, gallery)) recentSnipsForm = null;
                gallery.Dispose();
                ShowFailure("Recent snips could not be opened", error);
            }
        }

        private void OpenRecentSnip(string path)
        {
            if (!CanChangeAnnotationAppearance || IsCaptureSavePending || String.IsNullOrWhiteSpace(path)) return;
            RecentSnipsForm gallery = recentSnipsForm;
            bool showGallery = gallery != null && !gallery.IsDisposed && gallery.Visible;
            try
            {
                // Keep the owned gallery from covering the owner's save prompt.
                if (showGallery) gallery.Hide();
                Activate();
                if (!ConfirmReplace("opening another image")) return;
                // Only an explicit Open reaches the editor's established path.
                OpenImage(path);
            }
            finally
            {
                if (showGallery && !closing && !IsDisposed && Visible && WindowState != FormWindowState.Minimized && !gallery.IsDisposed)
                {
                    gallery.Show(this);
                    gallery.Activate();
                }
            }
        }

        private void InitializeRecentSnipsIntegration()
        {
            VisibleChanged += UpdateRecentSnipsOwnerVisibility;
            Resize += UpdateRecentSnipsOwnerVisibility;
        }

        private void UpdateRecentSnipsOwnerVisibility(object sender, EventArgs e)
        {
            if (recentSnipsForm == null || recentSnipsForm.IsDisposed)
            {
                if (recentSnipsRestoreTimer != null) recentSnipsRestoreTimer.Stop();
                return;
            }
            if (!Visible || WindowState == FormWindowState.Minimized)
            {
                if (recentSnipsForm.Visible) { restoreRecentSnips = true; recentSnipsForm.Hide(); }
                if (recentSnipsRestoreTimer != null) recentSnipsRestoreTimer.Stop();
                return;
            }
            if (!restoreRecentSnips || closing || IsDisposed)
            {
                if (recentSnipsRestoreTimer != null) recentSnipsRestoreTimer.Stop();
                return;
            }
            if (!CanChangeAnnotationAppearance || IsCaptureSavePending)
            {
                if (recentSnipsRestoreTimer == null)
                {
                    recentSnipsRestoreTimer = new Timer { Interval = 160 };
                    recentSnipsRestoreTimer.Tick += UpdateRecentSnipsOwnerVisibility;
                }
                recentSnipsRestoreTimer.Start();
                return;
            }
            if (recentSnipsRestoreTimer != null) recentSnipsRestoreTimer.Stop();
            restoreRecentSnips = false;
            recentSnipsForm.RestoreWithoutActivation = true;
            try { recentSnipsForm.Show(this); }
            finally { recentSnipsForm.RestoreWithoutActivation = false; }
        }

        private void DisposeRecentSnipsIntegration()
        {
            VisibleChanged -= UpdateRecentSnipsOwnerVisibility;
            Resize -= UpdateRecentSnipsOwnerVisibility;
            if (recentSnipsRestoreTimer != null) { recentSnipsRestoreTimer.Stop(); recentSnipsRestoreTimer.Dispose(); recentSnipsRestoreTimer = null; }
            restoreRecentSnips = false;
            if (recentSnipsForm != null) { recentSnipsForm.Dispose(); recentSnipsForm = null; }
        }
    }
}
