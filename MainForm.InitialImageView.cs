using System;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class MainForm
    {
        private object observedInitialImage, initialFitImage;
        private long initialFitRevision;
        private bool initialFitPending, initialFitQueued, initialFitHandleHooked, initialFitUserIntentHooked;
        private bool userSizedEditorWindow;

        private void TrackInitialImageView()
        {
            if (!initialFitUserIntentHooked)
            {
                ResizeBegin += InitialImageFitUserResizeBegin;
                canvas.MouseDown += InitialImageFitUserCanvasMouseDown;
                initialFitUserIntentHooked = true;
            }
            object image = canvas.HasImage ? canvas.ImageIdentity : null;
            if (Object.ReferenceEquals(observedInitialImage, image)) return;
            observedInitialImage = image;
            initialFitPending = false;
            initialFitImage = null;
            // New/replaced images have a fresh history. Undo, crop and source
            // removal must not silently create a new presentation session.
            if (image == null || canvas.CanUndo || canvas.CanRedo) return;
            userSizedEditorWindow = false;
            initialFitImage = image;
            initialFitRevision = canvas.DocumentRevision;
            initialFitPending = true;
            QueueInitialImageFit();
        }

        private void InitialImageFitUserResizeBegin(object sender, EventArgs e)
        {
            // Moving/resizing is an explicit user choice. A late save or copy
            // completion must not grow this window back to its initial size.
            initialFitPending = false;
            initialFitImage = null;
            if (canvas.HasImage)
            {
                // The initial shell prefers complete command rows and native
                // image pixels. A manual resize may use the existing toolbar
                // scrollbars and a smaller fitted viewport instead. Lower the
                // tracking limits before Windows clamps the first shrink.
                userSizedEditorWindow = true;
                LayoutChrome();
            }
        }

        private void InitialImageFitUserCanvasMouseDown(object sender, MouseEventArgs e)
        {
            // Starting an edit/selection is user view intent too. In particular,
            // do not let delayed capture completion refit during font matching,
            // before the inline keyboard editor has been created.
            initialFitPending = false;
            initialFitImage = null;
        }

        private void CancelInitialImageFitForToolIntent()
        {
            if (!canvas.HasImage) return;
            initialFitPending = false;
            initialFitImage = null;
        }

        private void QueueInitialImageFit()
        {
            if (layoutTestMode || !initialFitPending || initialFitQueued || closing || IsDisposed || Disposing) return;
            if (!IsHandleCreated)
            {
                if (!initialFitHandleHooked)
                {
                    initialFitHandleHooked = true;
                    HandleCreated += InitialImageFitHandleCreated;
                }
                return;
            }
            initialFitQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    initialFitQueued = false;
                    ApplyInitialImageFit(false);
                });
            }
            catch (InvalidOperationException) { initialFitQueued = false; }
        }

        private void InitialImageFitHandleCreated(object sender, EventArgs e)
        {
            HandleCreated -= InitialImageFitHandleCreated;
            initialFitHandleHooked = false;
            QueueInitialImageFit();
        }

        private bool ApplyInitialImageFit(bool finish)
        {
            if (!initialFitPending) return false;
            if (closing || IsDisposed || Disposing || !canvas.HasImage ||
                !Object.ReferenceEquals(initialFitImage, canvas.ImageIdentity) ||
                initialFitRevision != canvas.DocumentRevision || !canvas.FitRequested ||
                canvas.InlineTextEditing || canvas.TextActionsMode || (IsToolOptionsOpen && !toolOptionsOpenedAutomatically) ||
                IsCropContext || capturePending || recordingSession ||
                (canvas.IsHandleCreated && canvas.Capture) || ModalOpenCount() != 0)
            {
                initialFitPending = false;
                initialFitImage = null;
                return false;
            }
            // First-show DPI and the final status row can change chrome after
            // the eager SetImage sizing. Re-measure before choosing image scale:
            // grow for native pixels where the working monitor permits it.
            // This also covers a replacement image with unchanged dimensions.
            LayoutChrome();
            if (!Object.ReferenceEquals(initialFitImage, canvas.ImageIdentity) ||
                initialFitRevision != canvas.DocumentRevision || !canvas.FitRequested) return false;
            ResizeForSnip();
            canvas.FitToWindow();
            if (finish || !captureProcessingPending)
            {
                initialFitPending = false;
                initialFitImage = null;
            }
            return true;
        }

        private void CompleteInitialImageView(object image, long revision)
        {
            if (!initialFitPending || !Object.ReferenceEquals(initialFitImage, image) ||
                initialFitRevision != revision) return;
            ApplyInitialImageFit(true);
        }
    }
}
