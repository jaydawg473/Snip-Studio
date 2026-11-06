using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private CancellationTokenSource scrollingCaptureCancellation;
        private bool scrollingCaptureActive, closeAfterScrollingCapture, scrollingCloseForceExit;

        private async void StartScrollingCapture()
        {
            if (!CanStartBackgroundCapture()) return;
            if (IsCaptureSavePending) { SetStatus("Finishing the current capture. Please wait briefly."); return; }
            if (recordingModeSelected) SetCaptureKind(false);
            if (!preferences.MultipleWindows && !ConfirmReplace("starting a scrolling capture")) return;
            capturePending = true;
            scrollingCaptureActive = true;
            newButton.Enabled = false;
            bool usedCapture = false, failed = false;
            bool closeEmptyOnCancel = closeEmptyShortcutCapture && !canvas.HasImage;
            closeEmptyShortcutCapture = false;
            FormWindowState previousState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            CaptureVisibilityScope hiddenWindows = null;
            TaskbarVisibilityScope hiddenTaskbars = null;
            ScrollingCaptureControlForm controller = null;
            ScrollingCaptureResult result = null;
            CancellationTokenSource cancellation = new CancellationTokenSource();
            scrollingCaptureCancellation = cancellation;
            Exception restoreFailure = null;
            try
            {
                hiddenWindows = CaptureVisibilityScope.HideApplicationWindows(this);
                int seconds = new int[] { 0, 3, 5, 10 }[Math.Max(0, delay.SelectedIndex)];
                if (seconds > 0 && !CaptureCountdown.WaitForCapture(seconds)) return;
                await hiddenWindows.WaitUntilHiddenAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                // Select the scrolling content, excluding unrelated windows.
                Rectangle? selected = CaptureService.SelectRecordingRegion();
                if (!selected.HasValue) return;
                cancellation.Token.ThrowIfCancellationRequested();
                Rectangle region = selected.Value;
                controller = new ScrollingCaptureControlForm(region);
                controller.CancelRequested += delegate { cancellation.Cancel(); };
                controller.Show();
                ScrollingCaptureControlForm activeController = controller;
                if (preferences.HideTaskbarsDuringCapture)
                    hiddenTaskbars = TaskbarVisibilityScope.Hide(preferences.KeepPrimaryTaskbarVisible);
                TaskbarVisibilityScope activeTaskbars = hiddenTaskbars;
                ScrollingCaptureService service = new ScrollingCaptureService(delegate(Rectangle bounds)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (activeController.IsDisposed) throw new OperationCanceledException(cancellation.Token);
                    // An overlapping controller is removed for the pixel read,
                    // including on Windows versions without capture exclusion.
                    bool hideControl = activeController.Visible && !activeController.IsOutsideCapture;
                    if (hideControl) activeController.Hide();
                    try
                    {
                        if (hideControl)
                        {
                            try { DwmFlush(); }
                            catch (DllNotFoundException) { }
                            catch (EntryPointNotFoundException) { }
                        }
                        if (activeTaskbars != null) activeTaskbars.VerifyHidden();
                        return CaptureService.CaptureRegion(bounds);
                    }
                    finally
                    {
                        if (hideControl && !activeController.IsDisposed) activeController.Show();
                    }
                }, controller.Handle);
                result = await service.CaptureAsync(region,
                    delegate(ScrollingCaptureProgress progress)
                    {
                        if (!activeController.IsDisposed)
                            activeController.UpdateProgress(progress.FrameCount, progress.Height, progress.Status);
                    },
                    delegate { return activeController.FinishIsRequested; }, cancellation.Token);
                if (result.EndReason == ScrollingCaptureEndReason.EscapePressed)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                controller.SetFinishing("Preparing your scrolling capture preview...");
                controller.AllowClose = true;
                controller.Close();
                controller.Dispose();
                controller = null;
                if (hiddenTaskbars != null) { hiddenTaskbars.Dispose(); hiddenTaskbars = null; }
                if (hiddenWindows != null) { hiddenWindows.Dispose(); hiddenWindows = null; }
                cancellation.Token.ThrowIfCancellationRequested();
                if (IsDisposed || Disposing || closeAfterScrollingCapture) return;
                WindowState = previousState;
                Show();
                Activate();
                using (ScrollingCapturePreviewForm preview = new ScrollingCapturePreviewForm(result.Image, result.FrameCount, result.Detail, result.StoppedEarly))
                {
                    if (preview.ShowDialog(this) != DialogResult.OK) return;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                ApplyScrollingCapture(result.Image);
                usedCapture = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                failed = true;
                if (!IsDisposed && !Disposing)
                {
                    Show(); Activate();
                    ShowFailure("Scrolling capture could not finish. Your previous snip is kept", error);
                }
            }
            finally
            {
                if (controller != null)
                {
                    try { controller.AllowClose = true; controller.Close(); controller.Dispose(); }
                    catch (Exception error) { restoreFailure = error; }
                }
                if (hiddenTaskbars != null)
                    try { hiddenTaskbars.Dispose(); } catch (Exception error) { restoreFailure = error; }
                if (hiddenWindows != null)
                    try { hiddenWindows.Dispose(); } catch (Exception error) { restoreFailure = error; }
                if (result != null) result.Dispose();
                if (Object.ReferenceEquals(scrollingCaptureCancellation, cancellation)) scrollingCaptureCancellation = null;
                cancellation.Dispose();
                scrollingCaptureActive = false;
                capturePending = false;
                if (!IsDisposed && !Disposing)
                {
                    newButton.Enabled = true;
                    bool requestedClose = closeAfterScrollingCapture;
                    closeAfterScrollingCapture = false;
                    if (requestedClose)
                    {
                        forceExit = scrollingCloseForceExit;
                        Close();
                    }
                    else if (closeEmptyOnCancel && !usedCapture && !failed && restoreFailure == null) Close();
                    else
                    {
                        WindowState = previousState;
                        Show(); Activate();
                        UpdateDocumentState();
                        if (!usedCapture && !failed) SetStatus("Scrolling capture canceled. Your previous snip is unchanged.");
                        if (restoreFailure != null) ShowFailure("Some capture windows could not be restored", restoreFailure);
                    }
                }
            }
        }

        private void ApplyScrollingCapture(Bitmap image)
        {
            MainForm destination = this;
            if (preferences.MultipleWindows && canvas.HasImage)
            {
                destination = new MainForm(null);
                destination.mode.SelectedIndex = mode.SelectedIndex;
                destination.delay.SelectedIndex = delay.SelectedIndex;
            }
            try
            {
                destination.currentPath = null;
                destination.PrepareCaptureImage(image);
                destination.canvas.SetImage(image);
                if (!Object.ReferenceEquals(destination, this)) Program.OpenWindow(destination);
                else { Show(); Activate(); }
                destination.BeginCapturePolicies(image);
                destination.SetStatus(destination.capturePolicyMessage ?? "Scrolling capture ready. Review, crop, annotate, then Copy or Save.");
            }
            catch
            {
                if (!Object.ReferenceEquals(destination, this)) destination.Dispose();
                throw;
            }
        }

        private bool TryDelayCloseForScrollingCapture(FormClosingEventArgs e)
        {
            if (!scrollingCaptureActive) return false;
            e.Cancel = true;
            closeAfterScrollingCapture = true;
            scrollingCloseForceExit = forceExit;
            if (scrollingCaptureCancellation != null) scrollingCaptureCancellation.Cancel();
            return true;
        }

        private void CancelScrollingCaptureOnDispose()
        {
            if (scrollingCaptureCancellation != null) scrollingCaptureCancellation.Cancel();
        }
    }
}
