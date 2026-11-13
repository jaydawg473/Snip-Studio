using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        private InlineDraftBox inlineTextEditor;
        private int inlineTextIndex = -1;
        private PointF inlineTextLocation;
        private float inlineTextWidth, inlineTextMinimumHeight;
        private object inlineTextImage;
        private long inlineTextRevision;
        private string inlineInitialRtf, inlineInitialText, inlineTextStatus = "";
        private float inlineInitialWidth, inlineInitialMinimumHeight;
        private bool inlineLayoutChanging, inlineClosing, inlineLastDirty;
        private int inlineContentHeight;

        public event EventHandler InlineTextStateChanged;
        public bool InlineTextEditing { get { return inlineTextEditor != null && !inlineTextEditor.IsDisposed; } }
        public string InlineTextStatus { get { return inlineTextStatus; } }
        public bool CanShowAdvancedTextEditor { get { return InlineTextEditing || HasSelectedTextAnnotation; } }
        public bool HasPendingInlineTextChanges
        {
            get
            {
                if (!InlineTextEditing) return false;
                if (inlineTextIndex < 0) return !String.IsNullOrWhiteSpace(inlineTextEditor.Text);
                return inlineTextWidth != inlineInitialWidth || inlineTextMinimumHeight != inlineInitialMinimumHeight ||
                    !String.Equals(inlineTextEditor.Text, inlineInitialText, StringComparison.Ordinal) ||
                    !String.Equals(inlineTextEditor.Rtf, inlineInitialRtf, StringComparison.Ordinal);
            }
        }
        internal int InlineTextPreviewIndex
        {
            get { return InlineTextEditing && InlineTextSnapshotCurrent ? inlineTextIndex : -1; }
        }
        private bool InlineTextSnapshotCurrent
        {
            get { return HasImage && Object.ReferenceEquals(inlineTextImage, ImageIdentity) && inlineTextRevision == DocumentRevision; }
        }

        internal bool BeginInlineTextNote(PointF location)
        {
            if (!HasImage || disposingCanvas || !CommitInlineText()) return false;
            if (Single.IsNaN(location.X) || Single.IsInfinity(location.X) || Single.IsNaN(location.Y) || Single.IsInfinity(location.Y)) return false;
            float minimum = Math.Min(12, ImageSize.Width);
            location = new PointF(Math.Max(0, Math.Min(ImageSize.Width - minimum, location.X)),
                Math.Max(0, Math.Min(ImageSize.Height - 1, location.Y)));
            float width = Math.Min(ImageSize.Width - location.X, Math.Max(minimum, defaultTextBoxWidth));
            return BeginInlineText(-1, location, width, 0,
                RichTextLayout.CreatePlainRtf("", "Segoe UI", textFontSize, FontStyle.Regular, inkColor), false);
        }

        internal bool BeginInlineTextNote(RectangleF frame)
        {
            if (!HasImage || disposingCanvas || !FiniteTextBoxPoint(frame.Location) ||
                !FiniteTextBoxPoint(new PointF(frame.Width, frame.Height)) || frame.Width <= 0 || frame.Height <= 0 || !CommitInlineText()) return false;
            frame = RectangleF.Intersect(new RectangleF(PointF.Empty, ImageSize), frame);
            if (frame.Width < 1 || frame.Height < 1) return false;
            float width = Math.Max(1, Math.Min(32768, (float)Math.Round(frame.Width)));
            float minimumHeight = Math.Max(1, Math.Min(32768, (float)Math.Round(frame.Height)));
            return BeginInlineText(-1, frame.Location, width, minimumHeight,
                RichTextLayout.CreatePlainRtf("", "Segoe UI", textFontSize, FontStyle.Regular, inkColor), false);
        }

        internal bool BeginInlineTextEdit(int index)
        {
            if (!HasImage || disposingCanvas) return false;
            if (InlineTextEditing && inlineTextIndex == index && InlineTextSnapshotCurrent)
            { inlineTextEditor.Focus(); return true; }
            if (!CommitInlineText()) return false;
            TextAnnotationInfo info = document.GetTextAnnotation(index);
            if (info == null) return false;
            float available = Math.Max(1, ImageSize.Width - info.Location.X);
            float width = info.LayoutWidth > 0 ? info.LayoutWidth : Math.Min(available, Math.Max(24, info.Bounds.Width));
            return BeginInlineText(index, info.Location, width, info.MinimumHeight, info.Rtf, true);
        }

        private bool BeginInlineText(int index, PointF location, float width, float minimumHeight, string rtf, bool existing)
        {
            CancelGesture();
            RichTextLayout.ValidateRtf(rtf);
            InlineDraftBox draft = new InlineDraftBox(this);
            try
            {
                draft.Multiline = true;
                draft.WordWrap = true;
                draft.AcceptsTab = false;
                draft.DetectUrls = false;
                draft.AllowDrop = false;
                draft.HideSelection = false;
                draft.AutoWordSelection = false;
                draft.ScrollBars = RichTextBoxScrollBars.Vertical;
                draft.BorderStyle = BorderStyle.FixedSingle;
                draft.MaxLength = 16000;
                draft.Font = Font; // Make the base explicit before loading rich runs; parent font changes must not flatten them.
                draft.Tag = "PreserveColor";
                draft.AccessibleName = existing ? "Edit text directly on the snip" : "Type text directly on the snip";
                draft.AccessibleDescription = "Enter starts a new line. Ctrl+Enter applies. Escape cancels. Advanced text opens all formatting options.";
                Color color = RichTextLayout.GetFirstColor(rtf);
                draft.ForeColor = color;
                draft.Rtf = rtf;
                draft.BackColor = color.GetBrightness() > .6f ? Color.FromArgb(28, 34, 44) : Color.White;
                inlineTextEditor = draft;
                inlineTextIndex = index;
                inlineTextLocation = location;
                inlineTextWidth = width;
                inlineInitialWidth = width;
                inlineTextMinimumHeight = minimumHeight;
                inlineInitialMinimumHeight = minimumHeight;
                inlineTextImage = ImageIdentity;
                inlineTextRevision = DocumentRevision;
                inlineInitialRtf = draft.Rtf; // Compare RichEdit's canonical form, not incoming serialization.
                inlineInitialText = draft.Text;
                inlineLastDirty = false;
                inlineTextStatus = "Type in the text box. Enter adds a line; Ctrl+Enter applies; Escape cancels.";
                inlineContentHeight = 0;
                selectedIndex = index;
                Controls.Add(draft);
                draft.BringToFront();
                draft.ContentsResized += delegate(object sender, ContentsResizedEventArgs e)
                {
                    inlineContentHeight = e.NewRectangle.Height;
                    if (!inlineLayoutChanging) UpdateInlineTextLayout();
                };
                draft.TextChanged += delegate { InlineTextChanged(); };
                draft.SelectionChanged += delegate { if (!inlineClosing && InlineTextEditing) RaiseTextStyleChanged(); };
                UpdateInlineTextLayout();
                draft.Select(existing ? draft.TextLength : 0, 0);
                draft.Focus();
                RaiseInlineTextStateChanged();
                RaiseTextStyleChanged();
                Invalidate();
                return true;
            }
            catch
            {
                if (Object.ReferenceEquals(inlineTextEditor, draft)) inlineTextEditor = null;
                draft.Dispose();
                throw;
            }
        }

        private void InlineTextChanged()
        {
            if (inlineClosing || !InlineTextEditing) return;
            UpdateInlineTextLayout();
            bool dirty = HasPendingInlineTextChanges;
            if (dirty != inlineLastDirty)
            {
                inlineLastDirty = dirty;
                RaiseInlineTextStateChanged();
            }
        }

        internal void UpdateInlineTextLayout()
        {
            if (!InlineTextEditing || inlineLayoutChanging || disposingCanvas) return;
            inlineLayoutChanging = true;
            try
            {
                PointF origin = ImageOrigin;
                // RichEdit scales points with monitor DPI; canvas coordinates
                // are source pixels. Cancel that DPI factor before applying zoom.
                float factor = Math.Max(1f / 64f, Math.Min(64f, zoom * 96f / Math.Max(96, DeviceDpi)));
                if (Math.Abs(inlineTextEditor.ZoomFactor - factor) > .0001f) inlineTextEditor.ZoomFactor = factor;
                int left = (int)Math.Floor(origin.X + inlineTextLocation.X * zoom);
                int top = (int)Math.Floor(origin.Y + inlineTextLocation.Y * zoom);
                int width = Math.Max(8, (int)Math.Ceiling(inlineTextWidth * zoom) + 4);
                float fontPixels = Math.Max(1, InlineTextFontSize);
                int minimumHeight = Math.Max(24, (int)Math.Ceiling(fontPixels * zoom * 1.55f) + 8);
                int frameHeight = Math.Max(minimumHeight, (int)Math.Ceiling(inlineTextMinimumHeight * zoom) + 4);
                int wantedHeight = Math.Max(frameHeight, inlineContentHeight + 8);
                int remaining = Math.Max(minimumHeight, ClientSize.Height - Math.Max(0, top) - 8);
                int height = Math.Min(wantedHeight, remaining);
                inlineTextEditor.SetBounds(left, top, width, height);
                // Width changes can synchronously reflow RichEdit. Use the new
                // measured height once without recursively laying out the canvas.
                int reflowHeight = Math.Min(Math.Max(frameHeight, inlineContentHeight + 8), remaining);
                if (inlineTextEditor.Height != reflowHeight) inlineTextEditor.Height = reflowHeight;
            }
            finally { inlineLayoutChanging = false; }
        }

        internal bool HandleInlineTextMouseDown(MouseEventArgs e)
        {
            // Child-editor clicks never reach the canvas. A canvas click commits
            // the draft before the ordinary tool/move/selection gesture continues.
            return InlineTextEditing && !CommitInlineText();
        }

        public bool CommitInlineText()
        {
            if (!InlineTextEditing) return true;
            if (!InlineTextSnapshotCurrent)
            {
                inlineTextStatus = "The snip changed while this text was being edited. Copy your draft or cancel it before continuing.";
                RaiseInlineTextStateChanged();
                return false;
            }
            InlineDraftBox draft = inlineTextEditor;
            bool changed = HasPendingInlineTextChanges;
            int index = inlineTextIndex;
            float size;
            Color color;
            try
            {
                string plain = draft.Text, rtf = draft.Rtf;
                RichTextLayout.ValidateRtf(rtf);
                size = RichTextLayout.GetFirstFontSize(rtf);
                color = RichTextLayout.GetFirstColor(rtf);
                // Detach active state before the document transaction. It cannot
                // re-enter a draft commit through export/auto-copy callbacks.
                inlineTextEditor = null;
                if (index >= 0)
                {
                    changed = changed && document.UpdateRichTextAnnotation(index, plain, rtf, color, size, inlineTextWidth, inlineTextMinimumHeight);
                    selectedIndex = String.IsNullOrWhiteSpace(plain) ? -1 : index;
                }
                else if (!String.IsNullOrWhiteSpace(plain))
                {
                    document.AddRichText(plain, rtf, inlineTextLocation, color, size, inlineTextWidth, inlineTextMinimumHeight);
                    selectedIndex = document.AnnotationCount - 1;
                    changed = true;
                }
                else { selectedIndex = -1; changed = false; }
            }
            catch (Exception)
            {
                inlineTextEditor = draft;
                inlineTextStatus = "This text edit could not be applied. Keep the draft or use Advanced text to adjust its formatting and size.";
                RaiseInlineTextStateChanged();
                return false;
            }
            textFontSize = size;
            defaultTextBoxWidth = inlineTextWidth;
            CloseDetachedInlineText(draft);
            inlineTextStatus = changed ? "Text applied. Use Move to reposition it; Undo restores the previous state." : "Text unchanged.";
            RaiseInlineTextStateChanged();
            RaiseTextStyleChanged();
            if (changed) RaiseChanged(); else Invalidate();
            return true;
        }

        public void CancelInlineText()
        {
            if (!InlineTextEditing) return;
            InlineDraftBox draft = inlineTextEditor;
            inlineTextEditor = null;
            CloseDetachedInlineText(draft);
            inlineTextStatus = "Text editing canceled. The snip is unchanged.";
            RaiseInlineTextStateChanged(); RaiseTextStyleChanged(); Invalidate();
        }

        private void CloseDetachedInlineText(InlineDraftBox draft)
        {
            inlineClosing = true;
            try { Controls.Remove(draft); draft.Dispose(); }
            finally { inlineClosing = false; }
        }

        private void DisposeInlineText()
        {
            if (inlineTextEditor == null) return;
            InlineDraftBox draft = inlineTextEditor;
            inlineTextEditor = null;
            CloseDetachedInlineText(draft);
        }

        private void RaiseInlineTextStateChanged()
        {
            EventHandler handler = InlineTextStateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private float InlineTextFontSize
        {
            get
            {
                using (Font selected = inlineTextEditor.SelectionFont)
                    return selected == null ? RichTextLayout.GetFirstFontSize(inlineTextEditor.Rtf) : selected.SizeInPoints * 96f / 72f;
            }
        }

        private void ApplyInlineTextFontSize(float value)
        {
            RichEditFormatting.SetFontSizePixels(inlineTextEditor, value);
            InlineTextChanged(); RaiseTextStyleChanged();
        }

        internal void ApplyInlineTextColor(Color value)
        {
            if (!InlineTextEditing) return;
            RichEditFormatting.SetTextColor(inlineTextEditor, Color.FromArgb(255, value));
            InlineTextChanged(); RaiseTextStyleChanged();
        }

        public void ShowAdvancedTextEditor()
        {
            if (!InlineTextEditing)
            {
                if (HasSelectedTextAnnotation) EditTextAnnotationAdvanced(selectedIndex);
                return;
            }
            if (!InlineTextSnapshotCurrent) { CommitInlineText(); return; }
            InlineDraftBox draft = inlineTextEditor;
            int available = Math.Max(12, ImageSize.Width - (int)Math.Floor(inlineTextLocation.X));
            string rtf = draft.Rtf;
            using (TextEditorDialog dialog = new TextEditorDialog("Advanced text", draft.Text,
                RichTextLayout.GetFirstFontSize(rtf), inlineTextWidth, available,
                RichTextLayout.GetFirstColor(rtf), draft.BackColor, false, rtf))
            {
                dialog.SelectTextRange(draft.SelectionStart, draft.SelectionLength);
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) { draft.Focus(); return; }
                RichTextLayout.ValidateRtf(dialog.Rtf);
                draft.Rtf = dialog.Rtf;
                inlineTextWidth = dialog.BoxWidth;
                textFontSize = dialog.TextSize;
                InlineTextChanged();
                if (CommitInlineText()) Focus();
            }
        }

        private sealed class InlineDraftBox : RichTextBox
        {
            private readonly EditorCanvas owner;
            private readonly ContextMenuStrip editingMenu;
            internal InlineDraftBox(EditorCanvas owner)
            {
                this.owner = owner;
                editingMenu = new ContextMenuStrip();
                ToolStripMenuItem cut = new ToolStripMenuItem("Cut", null, delegate { Cut(); });
                ToolStripMenuItem copy = new ToolStripMenuItem("Copy", null, delegate { Copy(); });
                ToolStripMenuItem paste = new ToolStripMenuItem("Paste", null, delegate { PasteSupportedText(); });
                ToolStripMenuItem selectAll = new ToolStripMenuItem("Select all", null, delegate { SelectAll(); });
                ToolStripMenuItem advanced = new ToolStripMenuItem("Advanced text…");
                advanced.Click += delegate
                {
                    // Defer until the menu has finished closing; applying the
                    // dialog can dispose this draft and its menu.
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate
                        { if (!IsDisposed && owner.InlineTextEditing) owner.ShowAdvancedTextEditor(); });
                };
                editingMenu.Items.AddRange(new ToolStripItem[] { cut, copy, paste, selectAll, new ToolStripSeparator(), advanced });
                editingMenu.Opening += delegate
                {
                    cut.Enabled = copy.Enabled = SelectionLength > 0;
                    selectAll.Enabled = TextLength > 0;
                    foreach (ToolStripItem item in editingMenu.Items) item.ForeColor = StudioTheme.Text;
                };
                StudioTheme.StyleMenu(editingMenu);
                ContextMenuStrip = editingMenu;
            }
            protected override bool ProcessCmdKey(ref Message message, Keys keyData)
            {
                if (keyData == (Keys.Control | Keys.Enter))
                { if (owner.CommitInlineText()) owner.Focus(); return true; }
                if (keyData == Keys.Escape)
                { owner.CancelInlineText(); owner.Focus(); return true; }
                return base.ProcessCmdKey(ref message, keyData);
            }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (!owner.RequireCtrlForWheelZoom || (ModifierKeys & Keys.Control) != 0)
                {
                    Point point = owner.PointToClient(PointToScreen(e.Location));
                    owner.OnMouseWheel(new HandledMouseEventArgs(e.Button, e.Clicks, point.X, point.Y, e.Delta));
                    HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                    if (handled != null) handled.Handled = true;
                    return;
                }
                base.OnMouseWheel(e);
            }
            private void PasteSupportedText()
            {
                try
                {
                    bool hasPlain = Clipboard.ContainsText(TextDataFormat.UnicodeText);
                    bool hasRich = Clipboard.ContainsText(TextDataFormat.Rtf);
                    if (!hasPlain && !hasRich) return;
                    string plain = hasPlain ? Clipboard.GetText(TextDataFormat.UnicodeText) : "";
                    if (hasRich)
                    {
                        string rich = Clipboard.GetText(TextDataFormat.Rtf);
                        try
                        {
                            RichTextLayout.ValidateRtf(rich);
                            if (TextLength - SelectionLength + RichTextLayout.GetPlainText(rich).Length <= 16000)
                            { SelectedRtf = rich; return; }
                        }
                        catch (ArgumentException) { }
                    }
                    if (hasPlain && TextLength - SelectionLength + plain.Length <= 16000) SelectedText = plain;
                }
                catch (ExternalException)
                {
                    owner.inlineTextStatus = "The clipboard is busy. Try Paste again.";
                    owner.RaiseInlineTextStateChanged();
                }
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0302) { PasteSupportedText(); return; }
                if ((message.Msg == 0x0300 || message.Msg == 0x0301) && SelectionLength > 0) MainForm.RegisterClipboardIntent();
                base.WndProc(ref message);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { ContextMenuStrip = null; editingMenu.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
