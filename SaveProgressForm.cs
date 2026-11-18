using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnipStudio
{
    // Keeps the modal message loop responsive while the frozen image is encoded.
    // Closing is deferred until the operation has relinquished the bitmap.
    internal sealed class SaveProgressForm : Form
    {
        private readonly Action operation;
        private readonly Font ownedFont;
        private Task operationTask;
        private Exception failure;
        private bool completed, started;

        internal SaveProgressForm(Action operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            this.operation = operation;
            ownedFont = new Font("Segoe UI", 9.5f);
            Font = ownedFont;
            Text = "Saving your snip";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(360, 114);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            Label message = new Label { Text = "Saving the image and finishing the file…", AutoSize = false,
                Bounds = new Rectangle(20, 18, 320, 36), ForeColor = StudioTheme.Text };
            ProgressBar progress = new ProgressBar { Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 24, Bounds = new Rectangle(20, 66, 320, 18), TabStop = false };
            Controls.Add(message);
            Controls.Add(progress);
            MainForm.TrySetAppIcon(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); MainForm.ApplyDarkCaption(this); }

        internal bool Started { get { return started; } }
        internal Task OperationTask { get { return operationTask; } }
        internal Exception Failure { get { return failure; } }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (started) return;
            started = true;
            try
            {
                operationTask = Task.Run(operation);
                await operationTask;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                completed = true;
                if (!IsDisposed && !Disposing)
                {
                    DialogResult = failure == null ? DialogResult.OK : DialogResult.Abort;
                    Close();
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!completed) e.Cancel = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ownedFont.Dispose();
            base.Dispose(disposing);
        }
    }
}
