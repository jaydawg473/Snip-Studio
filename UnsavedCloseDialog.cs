using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    // All editor windows use one UI thread. A close burst must reuse the one
    // pending decision instead of stacking prompts or silently discarding work.
    internal static class EditorClosePrompt
    {
        private static UnsavedCloseDialog pending;
        private static readonly EditorCloseQueue deferred = new EditorCloseQueue();
        private static Form batchOwner;
        private static int batchGeneration;

        internal static bool IsCloseRequest(int message, IntPtr parameter)
        {
            return message == 0x0010 ||
                (message == 0x0112 && (parameter.ToInt64() & 0xFFF0) == 0xF060);
        }

        internal static Rectangle FitToWorkingArea(Rectangle workingArea, Size requested)
        {
            if (workingArea.Width <= 0 || workingArea.Height <= 0)
                throw new ArgumentOutOfRangeException("workingArea");
            int marginX = Math.Min(16, workingArea.Width / 8);
            int marginY = Math.Min(16, workingArea.Height / 8);
            int width = Math.Min(Math.Max(1, requested.Width), Math.Max(1, workingArea.Width - marginX * 2));
            int height = Math.Min(Math.Max(1, requested.Height), Math.Max(1, workingArea.Height - marginY * 2));
            return new Rectangle(workingArea.Left + (workingArea.Width - width) / 2,
                workingArea.Top + (workingArea.Height - height) / 2, width, height);
        }

        internal static bool ActivatePendingForCloseMessage(Form editor, int message, IntPtr parameter)
        {
            return IsCloseRequest(message, parameter) && DeferCloseIfPending(editor);
        }

        internal static bool DeferCloseIfPending(Form editor)
        {
            UnsavedCloseDialog current = pending;
            if (current != null && !current.IsDisposed && !current.Disposing)
            {
                bool queued = deferred.Enqueue(editor, current.EditorOwner);
                // A capture/recording failure may have canceled an earlier
                // batch while this decision was still visible. A fresh close
                // of another editor starts a new tracked batch, not a lost queue.
                if (queued && batchOwner == null) TrackOwner(current.EditorOwner);
                current.Present();
                return true;
            }
            // A previous choice may be in Save As or awaiting a recording/capture
            // save. Do not start another decision until that editor really closes.
            if (batchOwner != null && !Object.ReferenceEquals(batchOwner, editor))
            {
                deferred.Enqueue(editor, batchOwner);
                return true;
            }
            return false;
        }

        internal static bool TryActivatePending()
        {
            UnsavedCloseDialog current = pending;
            if (current == null || current.IsDisposed || current.Disposing) return false;
            current.Present();
            return true;
        }

        internal static DialogResult AskToSave(Form owner)
        {
            if (TryActivatePending()) return DialogResult.Cancel;
            if (batchOwner == null) TrackOwner(owner);
            using (UnsavedCloseDialog dialog = new UnsavedCloseDialog(owner))
            {
                pending = dialog;
                try
                {
                    dialog.PrepareForShow();
                    return dialog.ShowDialog(owner);
                }
                catch { CancelQueuedCloses(); throw; }
                finally
                {
                    if (Object.ReferenceEquals(pending, dialog)) pending = null;
                }
            }
        }

        private static void TrackOwner(Form owner)
        {
            batchOwner = owner;
            owner.FormClosed += OnOwnerClosed;
        }

        private static void OnOwnerClosed(object sender, FormClosedEventArgs e)
        {
            Form owner = sender as Form;
            if (owner != null) owner.FormClosed -= OnOwnerClosed;
            if (!Object.ReferenceEquals(batchOwner, owner)) return;
            batchOwner = null;
            ResumeNext();
        }

        internal static void CompleteHiddenClose(Form owner)
        {
            // The user's opt-in tray setting retains its editor by design.
            // That completed hide must not strand other queued close requests.
            if (Object.ReferenceEquals(batchOwner, owner)) OnOwnerClosed(owner, null);
        }

        private static void ResumeNext()
        {
            Form next = deferred.TakeNext();
            if (next == null) return;
            TrackOwner(next);
            int generation = batchGeneration;
            try
            {
                next.BeginInvoke((MethodInvoker)delegate
                {
                    if (generation != batchGeneration || !Object.ReferenceEquals(batchOwner, next)) return;
                    if (next.IsDisposed) { OnOwnerClosed(next, null); return; }
                    try { next.Close(); }
                    catch { CancelQueuedCloses(); throw; }
                });
            }
            catch (InvalidOperationException) { CancelQueuedCloses(); }
        }

        internal static void CancelQueuedCloses()
        {
            batchGeneration++;
            deferred.Clear();
            if (batchOwner != null) batchOwner.FormClosed -= OnOwnerClosed;
            batchOwner = null;
        }
    }

    // The queue holds at most one reference per existing editor, never requests
    // or new windows. Repeated Shell messages cannot grow it indefinitely.
    internal sealed class EditorCloseQueue
    {
        private readonly List<Form> editors = new List<Form>();
        internal int Count { get { return editors.Count; } }

        internal bool Enqueue(Form editor, Form current)
        {
            if (editor == null || editor.IsDisposed || editor.Disposing ||
                Object.ReferenceEquals(editor, current) || editors.Contains(editor)) return false;
            editors.Add(editor);
            return true;
        }

        internal Form TakeNext()
        {
            while (editors.Count != 0)
            {
                Form next = editors[0];
                editors.RemoveAt(0);
                if (!next.IsDisposed && !next.Disposing) return next;
            }
            return null;
        }

        internal void Clear() { editors.Clear(); }
    }

    internal sealed class UnsavedCloseDialog : Form
    {
        private readonly Form editor;
        private readonly Font textFont;
        private readonly Font headingFont;
        private bool presenting;

        internal Form EditorOwner { get { return editor; } }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        internal UnsavedCloseDialog(Form owner)
        {
            editor = owner;
            Text = "Unsaved snip";
            textFont = new Font("Segoe UI", 9.5f);
            headingFont = new Font("Segoe UI", 15f, FontStyle.Bold);
            Font = textFont;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            BackColor = StudioTheme.Surface;
            ForeColor = StudioTheme.Text;
            ClientSize = new Size(492, 200);
            MainForm.TrySetAppIcon(this);

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Padding = new Padding(24, 20, 24, 20), BackColor = StudioTheme.Surface
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label heading = new Label
            {
                Text = "Save changes to your snip?", Font = headingFont,
                AutoSize = true, Dock = DockStyle.Top, ForeColor = StudioTheme.Text,
                Margin = new Padding(0, 0, 0, 10)
            };
            Label explanation = new Label
            {
                Text = "Save keeps your edits. Don't save closes this snip without them. Cancel keeps the window open.",
                AutoSize = true, Dock = DockStyle.Top, ForeColor = StudioTheme.Muted,
                MaximumSize = new Size(444, 0), Margin = new Padding(0, 0, 0, 16)
            };
            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty
            };
            StudioButton save = new StudioButton
            {
                Text = "Save", Primary = true, DialogResult = DialogResult.Yes,
                Size = new Size(100, 38), Margin = new Padding(0, 0, 8, 0),
                AccessibleName = "Save snip and close", TabIndex = 0
            };
            StudioButton discard = new StudioButton
            {
                Text = "Don't save", DialogResult = DialogResult.No,
                Size = new Size(112, 38), Margin = new Padding(0, 0, 8, 0),
                AccessibleName = "Close snip without saving edits", TabIndex = 1
            };
            StudioButton cancel = new StudioButton
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                Size = new Size(100, 38), Margin = Padding.Empty,
                AccessibleName = "Keep snip open", TabIndex = 2
            };
            actions.Controls.Add(cancel);
            actions.Controls.Add(discard);
            actions.Controls.Add(save);
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(explanation, 0, 1);
            layout.Controls.Add(actions, 0, 2);
            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
            Shown += delegate { Present(); };
            Activated += delegate
            {
                // If Windows initially declined foreground activation, a click
                // or keyboard activation ends the temporary visibility fallback.
                if (!presenting && TopMost) TopMost = false;
            };
        }

        internal void PrepareForShow() { PositionOnScreen(); }

        private void PositionOnScreen()
        {
            // Mouse-initiated closes follow the pointer's monitor. Alt+F4 keeps
            // the decision with the editor rather than an unrelated pointer.
            bool keyboardClose = (Control.ModifierKeys & Keys.Alt) != 0;
            Point anchor = keyboardClose && editor != null && !editor.IsDisposed
                ? new Point(editor.Left + editor.Width / 2, editor.Top + editor.Height / 2)
                : Cursor.Position;
            Bounds = EditorClosePrompt.FitToWorkingArea(Screen.FromPoint(anchor).WorkingArea, Size);
        }

        internal void Present()
        {
            if (IsDisposed || Disposing || presenting || !Visible || !IsHandleCreated) return;
            PositionOnScreen();
            presenting = true;
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                // This affects only the pending decision, never editor windows.
                // Keep it visible if Windows declines a foreground request.
                TopMost = true;
                BringToFront();
                Activate();
                if (SetForegroundWindow(Handle)) TopMost = false;
            }
            finally { presenting = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            StudioTheme.ApplyCaption(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (TopMost) TopMost = false;
                headingFont.Dispose();
                textFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
