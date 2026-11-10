using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private Panel updateBanner;
        private Label updateBannerLabel;
        private StudioButton updateDownloadButton, updateDismissButton;
        private ToolStripMenuItem checkUpdatesItem;
        private UpdateCheckResult updateOffer;
        private string dismissedUpdateVersion;
        private int updateNoticeHeight;
        private bool checkingForUpdates, updateIntegrationReady, updateCheckScheduled;

        private void BuildUpdateIndicator()
        {
            updateBanner = new Panel { BackColor = StudioTheme.Surface, Visible = false };
            updateBannerLabel = new Label
            {
                AutoEllipsis = false,
                ForeColor = StudioTheme.Text,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            updateDownloadButton = ActionButton("Download", "save", 112, delegate { OpenUpdateDownload(); });
            updateDownloadButton.Primary = true;
            updateDismissButton = ActionButton("×", "", 34, delegate
            {
                dismissedUpdateVersion = UpdateVersionText(updateOffer);
                LayoutChrome();
            });
            updateDismissButton.AccessibleName = "Dismiss update notice";
            tips.SetToolTip(updateDownloadButton, "Open the official installer download in your browser. You choose when to run Setup.");
            tips.SetToolTip(updateDismissButton, "Hide this update notice in this window. More > Check for updates shows it again.");
            updateBanner.Controls.Add(updateBannerLabel);
            updateBanner.Controls.Add(updateDownloadButton);
            updateBanner.Controls.Add(updateDismissButton);
            chromeRoot.Controls.Add(updateBanner);
        }

        private void InitializeUpdateIntegration()
        {
            if (layoutTestMode || updateIntegrationReady) return;
            updateIntegrationReady = true;
            UpdateNotifications.Changed += OnUpdateNotificationChanged;
            Shown += OnUpdateWindowShown;
            ApplyUpdateNotice(UpdateNotifications.AvailableUpdate);
        }

        private void OnUpdateWindowShown(object sender, EventArgs e)
        {
            if (updateCheckScheduled || layoutTestMode || IsDisposed || Disposing) return;
            updateCheckScheduled = true;
            ApplyUpdateNotice(UpdateNotifications.AvailableUpdate);
            // Queue after the entire Shown event so initial image loading and
            // shortcut-triggered capture do not wait for disk or network work.
            try { BeginInvoke((MethodInvoker)delegate { CheckForUpdates(true); }); }
            catch (InvalidOperationException) { }
        }

        private void OnUpdateNotificationChanged()
        {
            if (IsDisposed || Disposing || closing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && !Disposing && !closing)
                        ApplyUpdateNotice(UpdateNotifications.AvailableUpdate);
                });
            }
            catch (InvalidOperationException) { }
        }

        internal async void CheckForUpdates(bool automatic)
        {
            if (layoutTestMode || checkingForUpdates || IsDisposed || Disposing || closing) return;
            checkingForUpdates = true;
            if (checkUpdatesItem != null) checkUpdatesItem.Enabled = false;
            if (!automatic) SetStatus("Checking for updates…");
            try
            {
                UpdateCheckResult result = await UpdateNotifications.CheckAsync(automatic);
                if (IsDisposed || Disposing || closing) return;
                if (result == null) { ApplyUpdateNotice(UpdateNotifications.AvailableUpdate); return; }
                if (!automatic)
                {
                    dismissedUpdateVersion = null;
                    SetStatus(result.UserMessage);
                }
                ApplyUpdateNotice(UpdateNotifications.AvailableUpdate);
            }
            catch (Exception)
            {
                if (!automatic && !IsDisposed && !Disposing && !closing)
                    SetStatus("Updates could not be checked. Please try again later.");
            }
            finally
            {
                checkingForUpdates = false;
                if (checkUpdatesItem != null && !checkUpdatesItem.IsDisposed) checkUpdatesItem.Enabled = true;
            }
        }

        internal void ApplyUpdateNotice(UpdateCheckResult available)
        {
            if (IsDisposed || Disposing || updateBanner == null) return;
            updateOffer = available != null && available.Status == UpdateCheckStatus.UpdateAvailable ? available : null;
            updateBannerLabel.Text = updateOffer == null ? "" : "Snip Studio " + UpdateVersionText(updateOffer) + " is available.";
            LayoutChrome();
        }

        private static string UpdateVersionText(UpdateCheckResult update)
        {
            return update == null || update.NewVersion == null ? "" : update.NewVersion.ToString(3);
        }

        private int LayoutUpdateIndicator(int top, int availableWidth)
        {
            if (updateBanner == null) return 0;
            bool show = updateOffer != null && UpdateVersionText(updateOffer) != dismissedUpdateVersion &&
                !capturePending && !recordingSession && !scrollingCaptureActive;
            updateBanner.Visible = show;
            if (!show)
            {
                updateNoticeHeight = 0;
                updateBanner.SetBounds(0, top, availableWidth, 0);
                return 0;
            }
            int padding = Ui(12), gap = Ui(8), rowHeight = Ui(34);
            int downloadWidth = ToolbarWidth(updateDownloadButton, 112), dismissWidth = Ui(34);
            int textWidth = Math.Max(1, availableWidth - padding * 2 - downloadWidth - dismissWidth - gap * 2);
            bool oneRow = textWidth >= ToolbarWidth(updateBannerLabel, 100);
            if (oneRow)
            {
                updateBannerLabel.SetBounds(padding, Ui(6), textWidth, rowHeight);
                updateDownloadButton.SetBounds(padding + textWidth + gap, Ui(6), downloadWidth, rowHeight);
                updateDismissButton.SetBounds(availableWidth - padding - dismissWidth, Ui(6), dismissWidth, rowHeight);
                updateNoticeHeight = rowHeight + Ui(12);
            }
            else
            {
                int labelWidth = Math.Max(1, availableWidth - padding * 2 - dismissWidth - gap);
                int labelHeight = WrappedTextHeight(updateBannerLabel, labelWidth, rowHeight);
                updateBannerLabel.SetBounds(padding, Ui(6), labelWidth, labelHeight);
                updateDismissButton.SetBounds(availableWidth - padding - dismissWidth, Ui(6), dismissWidth, rowHeight);
                updateDownloadButton.SetBounds(padding, labelHeight + Ui(10), Math.Min(downloadWidth, Math.Max(1, availableWidth - padding * 2)), rowHeight);
                updateNoticeHeight = labelHeight + rowHeight + Ui(16);
            }
            updateBanner.SetBounds(0, top, availableWidth, updateNoticeHeight);
            return updateNoticeHeight;
        }

        private void OpenUpdateDownload()
        {
            if (updateOffer == null || capturePending || recordingSession || scrollingCaptureActive) return;
            string version = UpdateVersionText(updateOffer);
            string expected = "https://github.com/jaydawg473/Snip-Studio/releases/download/v" + version + "/SnipStudio-" + version + "-Setup.exe";
            if (!String.Equals(updateOffer.DownloadUrl, expected, StringComparison.Ordinal))
            {
                SetStatus("The update download could not be verified. Check for updates again.");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(expected) { UseShellExecute = true });
                SetStatus("Opened the official installer download. Run Setup when you are ready to update.");
            }
            catch (Exception error) { ShowFailure("The update download could not open", error); }
        }

        private void DisposeUpdateIntegration()
        {
            if (!updateIntegrationReady) return;
            updateIntegrationReady = false;
            UpdateNotifications.Changed -= OnUpdateNotificationChanged;
            Shown -= OnUpdateWindowShown;
        }
    }
}
